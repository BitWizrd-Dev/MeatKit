using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using HarmonyLib;
using Mono.Cecil;
using Mono.Cecil.Cil;
using UnityEngine;
using MethodBody = Mono.Cecil.Cil.MethodBody;

namespace MeatKit
{
    public static partial class MeatKit
    {
        private const string EditorAssemblyPath = "Library/ScriptAssemblies/";

        private static void ExportEditorAssembly(string folder, string tempFile = null,
            Dictionary<string, List<string>> requiredScripts = null)
        {
            // Make a copy of the file if we aren't already given one
            string editorAssembly = EditorAssemblyPath + AssemblyName + ".dll";
            if (!File.Exists(editorAssembly) && !File.Exists(tempFile))
            {
                throw new MeatKitBuildException("Editor assembly missing! Can't export scripts.");
            }

            if (string.IsNullOrEmpty(tempFile))
            {
                tempFile = Path.GetTempFileName();
                File.Copy(editorAssembly, tempFile, true);
            }

            // Delete the old file
            var settings = BuildWindow.SelectedProfile;
            var exportPath = folder + settings.PackageName + ".dll";
            if (File.Exists(exportPath)) File.Delete(exportPath);

            var unityManagedDir = Path.GetDirectoryName(typeof(UnityEngine.Object).Assembly.Location);
            var rParams = new ReaderParameters
            {
                AssemblyResolver =
                    new RedirectedAssemblyResolver(unityManagedDir, ManagedDirectory)
            };

            // Load via MemoryStream: releases the file lock immediately and avoids Cecil 0.9 Dispose() issues.
            byte[] asmBytes = File.ReadAllBytes(tempFile);
            var asm = AssemblyDefinition.ReadAssembly(new System.IO.MemoryStream(asmBytes), rParams);
            try
            {
                // Locate the plugin class for this profile and set it's name and namespace
                string mainNamespace = BuildWindow.SelectedProfile.MainNamespace;
                var plugin = FindPluginClass(asm.MainModule, mainNamespace);
                BuildLog.WriteLine("Using plugin class " + plugin.FullName);
                plugin.Namespace = mainNamespace;
                plugin.Name = settings.PackageName + "Plugin";

                // Watermark the plugin just in case it's useful to someone
                BuildLog.WriteLine("Watermarking plugin class");
                var str = asm.MainModule.TypeSystem.String;
                var descriptionAttributeConstructor = typeof(DescriptionAttribute).GetConstructor(new[] { typeof(string) });
                var descriptionAttributeRef = asm.MainModule.ImportReference(descriptionAttributeConstructor);
                var descriptionAttribute = new CustomAttribute(descriptionAttributeRef);
                descriptionAttribute.ConstructorArguments.Add(new CustomAttributeArgument(str, "Built with MeatKit"));
                plugin.CustomAttributes.Add(descriptionAttribute);

                // Get the BepInPlugin attribute and replace the values in it with our own
                BuildLog.WriteLine("Applying BepInPlugin attribute params");
                var guid = settings.Author + "." + settings.PackageName;
                // Explicit loop — LINQ MoveNext() implicit finally can cause a double-TAE Mono crash.
                CustomAttribute pluginAttribute = null;
                foreach (CustomAttribute ca in plugin.CustomAttributes)
                {
                    if (ca.AttributeType.Name == "BepInPlugin") { pluginAttribute = ca; break; }
                }
                if (pluginAttribute == null) throw new MeatKitBuildException("No [BepInPlugin] attribute found on plugin class.", null);
                pluginAttribute.ConstructorArguments[0] = new CustomAttributeArgument(str, guid);
                pluginAttribute.ConstructorArguments[1] = new CustomAttributeArgument(str, settings.PackageName);
                pluginAttribute.ConstructorArguments[2] = new CustomAttributeArgument(str, settings.Version);

                // Get the LoadAssets method and make a new body for it
                BuildLog.WriteLine("Generating LoadAssets()");
                // Explicit loop — same TAE/implicit-finally reason as above.
                MethodDefinition loadAssetsMethod = null;
                foreach (MethodDefinition md in plugin.Methods)
                {
                    if (md.Name == "LoadAssets") { loadAssetsMethod = md; break; }
                }
                if (loadAssetsMethod == null) throw new MeatKitBuildException("LoadAssets() method not found in plugin class.", null);
                loadAssetsMethod.Body = new MethodBody(loadAssetsMethod);
                var il = loadAssetsMethod.Body.GetILProcessor();

                // If we're automatically applying Harmony patches, do that now
                // This IL translates to Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), "PluginGuid");
                if (settings.ApplyHarmonyPatches)
                {
                    BuildLog.WriteLine("  Code to apply harmony patches");
                    var assemblyGetExecutingAssembly = typeof(Assembly).GetMethod("GetExecutingAssembly");
                    var harmonyCreateAndPatchALl = typeof(Harmony).GetMethod("CreateAndPatchAll", new[] { typeof(Assembly), typeof(string) });
                    il.Emit(OpCodes.Call, plugin.Module.ImportReference(assemblyGetExecutingAssembly));
                    il.Emit(OpCodes.Ldstr, guid);
                    il.Emit(OpCodes.Call, plugin.Module.ImportReference(harmonyCreateAndPatchALl));
                    il.Emit(OpCodes.Pop);
                }

                // Let any build items insert their own code in here
                foreach (var item in settings.BuildItems)
                {
                    BuildLog.WriteLine("  " + item);
                    item.GenerateLoadAssets(plugin, il);
                }

                // Insert a ret at the end so it's valid
                il.Emit(OpCodes.Ret);

                // Module name needs to be changed away from Assembly-CSharp.dll because it is a reserved name.
                string newAssemblyName = settings.PackageName + ".dll";
                BuildLog.WriteLine("Renaming assembly (" + newAssemblyName + ")");
                asm.Name = new AssemblyNameDefinition(settings.PackageName, asm.Name.Version);
                asm.MainModule.Name = newAssemblyName;

                // References to renamed unity code must be swapped out.
                BuildLog.WriteLine("Renaming assembly references");
                foreach (var ii in asm.MainModule.AssemblyReferences)
                {
                    FixAssemblyNameReference(ii);
                }

                // For some reason, constructor arguments uses their own instance of AssemblyNameReference
                // So we need to do the same thing as above again.
                BuildLog.WriteLine("Fixing custom attribute arguments");
                // Explicit loop — same TAE/implicit-finally reason as above.
                foreach (CustomAttribute customAttr in GetAllCustomAttributesList(asm))
                {
                    foreach (CustomAttributeArgument x in customAttr.ConstructorArguments)
                    {
                        TypeReference typeRef = x.Value as TypeReference;

                        if (typeRef != null)
                        {
                            AssemblyNameReference asmNameRef = typeRef.Scope as AssemblyNameReference;
                            if (asmNameRef != null)
                            {
                                FixAssemblyNameReference(asmNameRef);
                            }
                        }
                    }
                }

                if (BuildWindow.SelectedProfile.StripNamespaces)
                {
                    // Remove types not in an allowed namespace or the global namespace
                    BuildLog.WriteLine("Stripping namespaces");
                    string[] allowedNamespaces = BuildWindow.SelectedProfile.GetAllAllowedNamespaces();
                    List<TypeDefinition> typesToRemove = new List<TypeDefinition>();
                    foreach (var type in asm.MainModule.Types)
                    {
                        if (type.Namespace == "" || allowedNamespaces.Any(x => type.Namespace.Contains(x)))
                            continue;
                        BuildLog.WriteLine("  " + type.FullName);
                        typesToRemove.Add(type);
                    }

                    foreach (var type in typesToRemove) asm.MainModule.Types.Remove(type);
                }

                // Remove the same types we didn't want to import. This cannot be skipped.
                // Explicit loop instead of LINQ Select+Where to avoid TAE-in-implicit-finally crash.
                foreach (string strippedTypeName in StripAssemblyTypes)
                {
                    TypeDefinition strippedType = asm.MainModule.GetType(strippedTypeName);
                    if (strippedType != null) asm.MainModule.Types.Remove(strippedType);
                }

                // Check if we're now missing any scripts from the export
                BuildLog.WriteLine("Checking for missing types");
                List<string> missing = new List<string>();
                string originalAssemblyName = AssemblyName + ".dll";
                if (requiredScripts != null && requiredScripts.ContainsKey(originalAssemblyName))
                {
                    // Explicit loop instead of LINQ Where to avoid TAE-in-implicit-finally crash.
                    foreach (string reqTypeName in requiredScripts[originalAssemblyName])
                    {
                        if (!StripAssemblyTypes.Contains(reqTypeName) && asm.MainModule.GetType(reqTypeName) == null)
                            missing.Add(reqTypeName);
                    }
                }

                // If we're missing anything, fail the build.
                if (missing.Count > 0)
                {
                    string missingTypes = string.Join("\n", missing.ToArray());
                    throw new MeatKitBuildException(
                        "Exported objects reference scripts which do not exist in the exported assembly... Did you forget to allow a namespace?\n\nMissing types:\n" +
                        missingTypes, null);
                }

                // Check over all the types and code in the exported assembly to see if they reference other types that
                // would be problematic, such as stuff from UnityEditor or types that got removed because of namespace
                // stripping.
                BuildLog.WriteLine("Building reference map");
                Dictionary<MemberReference, List<MemberReference>> referenceMap = BuildReferenceMap(asm.MainModule);
                bool referenceMapHasIssues = false;
                StringBuilder sb = new StringBuilder();
                foreach (var kvp in referenceMap)
                {
                    MemberReference scriptMember = kvp.Key;
                    foreach (MemberReference referencedMember in kvp.Value)
                    {
                        IMetadataScope scope;

                        TypeReference typeRef = referencedMember as TypeReference;
                        if (typeRef != null)
                        {
                            scope = typeRef.Scope;
                        } else
                        {
                            scope = referencedMember.DeclaringType.Scope;
                        }

                        if (scope == null)
                        {
                            referenceMapHasIssues = true;
                            sb.AppendFormat("{0} references {1} but it was removed due to namespace stripping\n", scriptMember, referencedMember);
                        }
                        else if (scope.Name == "UnityEditor")
                        {
                            referenceMapHasIssues = true;
                            sb.AppendFormat("{0} references Editor-only type {1}\n", scriptMember, referencedMember);
                        }
                    }
                }

                // Don't continue if there were any issues
                if (referenceMapHasIssues)
                {
                    throw new MeatKitBuildException("Exported assembly contains issues:\n" + sb);
                }

                // Make sure the assembly doesn't reference the editor assembly. This should be safe to do now.
                // Explicit loop — same TAE/finally crash reason as above.
                AssemblyNameReference editorAsmRef = null;
                foreach (AssemblyNameReference anr in asm.MainModule.AssemblyReferences)
                {
                    if (anr.Name == "UnityEditor") { editorAsmRef = anr; break; }
                }
                if (editorAsmRef != null)
                {
                    asm.MainModule.AssemblyReferences.Remove(editorAsmRef);
                }

                // Flush any pending TAE here (safe point) so it doesn't fire inside Cecil's write path.
                System.Threading.Thread.Sleep(0);

                // All done, save the modified assembly.
                // All done, save the modified assembly.
                // Write via MemoryStream to keep Cecil's P/Invoke out of the FileStream path.
                try
                {
                    var writeMs = new System.IO.MemoryStream();
                    asm.Write(writeMs);
                    System.IO.File.WriteAllBytes(exportPath, writeMs.ToArray());
                }
                catch (ArgumentException e)
                {
                    throw new MeatKitBuildException(
                        "Unable to write exported scripts file. This is likely due to namespace stripping being enabled and a required namespace is not whitelisted.",
                        e);
                }
            }
            catch (System.Threading.ThreadAbortException)
            {
                // ResetAbort before the finally runs; a second TAE inside finally → Mono hard crash.
                System.Threading.Thread.ResetAbort();
                throw new MeatKitBuildException("Build was interrupted by a domain reload during assembly export. Re-trigger the build.");
            }
            finally
            {
                // Intentionally not calling asm.Dispose() — a TAE inside a finally block
                // is not caught by the outer catch(TAE), causing a Mono g_error hard crash.
            }

            // Delete temp file now that we're done.
            File.Delete(tempFile);
        }

        private static void FixAssemblyNameReference(AssemblyNameReference asmNameRef)
        {
            // Rename any references to the game's code
            if (asmNameRef.Name.Contains("H3VRCode-CSharp"))
            {
                var newReference = asmNameRef.Name.Replace("H3VRCode-CSharp", "Assembly-CSharp");
                BuildLog.WriteLine("  " + asmNameRef.Name + " -> " + newReference);
                asmNameRef.Name = newReference;
            }

            // And also if we're referencing a MonoMod DLL, we need to fix reference too
            if (asmNameRef.Name.EndsWith(".mm"))
            {
                // What the name currently is:
                //    Assembly-CSharp.PatchName.mm
                // What we want:
                //    Assembly-CSharp
                // So just lop off anything past the second to last dot
                int idx = asmNameRef.Name.LastIndexOf('.', asmNameRef.Name.Length - 4);
                var newReference = asmNameRef.Name.Substring(0, idx);
                BuildLog.WriteLine("  " + asmNameRef.Name + " -> " + newReference);
                asmNameRef.Name = newReference;
            }
        }

        private static TypeDefinition FindPluginClass(ModuleDefinition module, string mainNamespace)
        {
            // Get the default MeatKitPlugin class from the module
            var pluginClass = module.GetType("MeatKitPlugin");

            // Try and locate any alternative plugin classes.
            // [BepInPlugin] attribute check instead of IsSubtypeOf — that triggers Cecil assembly
            // resolution which crashes on the second consecutive build.
            bool found = false;
            foreach (var type in module.Types)
            {
                // We're looking for types in the main namespace of our mod with [BepInPlugin]
                if (type.Namespace == mainNamespace && type.HasCustomAttributes)
                {
                    foreach (var attr in type.CustomAttributes)
                    {
                        if (attr.AttributeType.Name == "BepInPlugin")
                        {
                            pluginClass = type;
                            found = true;
                            break;
                        }
                    }
                    if (found) break;
                }
            }

            return pluginClass;
        }

        private static Dictionary<MemberReference, List<MemberReference>> BuildReferenceMap(ModuleDefinition module)
        {
            Dictionary<MemberReference, List<MemberReference>> map = new Dictionary<MemberReference, List<MemberReference>>();

            // Materialise via CollectAllTypes() — Cecil's GetTypes() is a recursive iterator
            // whose implicit Dispose() can cause a double-TAE Mono crash.
            List<TypeDefinition> allTypes = new List<TypeDefinition>();
            CollectAllTypes(module.Types, allTypes);
            foreach (TypeDefinition type in allTypes)
            {
                if (type.BaseType != null)
                {
                    AddReference(map, type, type.BaseType);
                }
                
                foreach (CustomAttribute typeAttribute in type.CustomAttributes)
                {
                    AddReference(map, type, typeAttribute.AttributeType);
                }

                foreach (FieldDefinition field in type.Fields)
                {
                    AddReference(map, field, field.FieldType);

                    foreach (CustomAttribute fieldAttribute in field.CustomAttributes)
                    {
                        AddReference(map, field, fieldAttribute.AttributeType);
                    }
                }

                foreach (PropertyDefinition property in type.Properties)
                {
                    AddReference(map, property, property.PropertyType);

                    foreach (CustomAttribute propertyAttribute in property.CustomAttributes)
                    {
                        AddReference(map, property, propertyAttribute.AttributeType);
                    }
                }

                foreach (MethodDefinition method in type.Methods)
                {
                    foreach (CustomAttribute methodAttribute in method.CustomAttributes)
                    {
                        AddReference(map, method, methodAttribute.AttributeType);
                    }

                    foreach (ParameterDefinition methodParameter in method.Parameters)
                    {
                        AddReference(map, method, methodParameter.ParameterType);
                    }

                    if (method.ReturnType != null)
                    {
                        AddReference(map, method, method.ReturnType);
                    }

                    if (method.HasBody)
                    {
                        foreach (Instruction instruction in method.Body.Instructions)
                        {
                            MemberReference memRef = instruction.Operand as MemberReference;
                            if (memRef != null)
                            {
                                AddReference(map, method, memRef);
                            }
                        }
                    }
                }
            }

            return map;
        }

        private static void AddReference(Dictionary<MemberReference, List<MemberReference>> dict, MemberReference key, MemberReference value)
        {
            List<MemberReference> list;
            if (!dict.TryGetValue(key, out list))
            {
                list = new List<MemberReference>();
                dict.Add(key, list);
            }
            
            list.Add(value);
        }

        // Returns a materialised List instead of yield-return to avoid TAE-in-implicit-finally crashes.
        private static List<CustomAttribute> GetAllCustomAttributesList(AssemblyDefinition asm)
        {
            var result = new List<CustomAttribute>();
            foreach (TypeDefinition type in asm.MainModule.Types)
            {
                foreach (CustomAttribute attrib in type.CustomAttributes)
                    result.Add(attrib);
                foreach (MethodDefinition method in type.Methods)
                    foreach (CustomAttribute attrib in method.CustomAttributes)
                        result.Add(attrib);
            }
            return result;
        }

        // Materialises all types (including nested) from a Cecil Collection into a plain List.
        private static void CollectAllTypes(IEnumerable<TypeDefinition> types, List<TypeDefinition> result)
        {
            foreach (TypeDefinition type in types)
            {
                result.Add(type);
                if (type.HasNestedTypes) CollectAllTypes(type.NestedTypes, result);
            }
        }
    }
}
