using System;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;
using UnityEngine;

// This file contains three co-located classes that together handle safe type stripping
// from a Cecil AssemblyDefinition before it is written to disk.
//
//   MethodBodyAnalyzer       – query layer: finds all Cecil members that reference a named type
//   AssemblyIntegrityValidator – validation layer: checks for null/broken members before write
//   SafeAssemblyStripper     – orchestration layer: multi-pass removal of unwanted types
//
// They are in a single file because MethodBodyAnalyzer and AssemblyIntegrityValidator are
// exclusively used by SafeAssemblyStripper; keeping them together makes the dependency
// relationship explicit and avoids three separate editor-script imports for one logical unit.

namespace MeatKit
{
    // -----------------------------------------------------------------------------------------
    // MethodBodyAnalyzer
    //
    // Pure query utilities.  Each method walks the Cecil module and returns all members
    // (methods / fields / properties / events / types) that carry a reference to a given
    // fully-qualified type name.  Results are returned as plain Lists; no LINQ iterators are
    // used so there are no implicit finally { enumerator.Dispose() } blocks that could act as
    // ThreadAbortException delivery checkpoints when called during a domain-reload window.
    // -----------------------------------------------------------------------------------------
    public static class MethodBodyAnalyzer
    {
        public static List<MethodDefinition> FindMethodsReferencingType(AssemblyDefinition assembly, string typeName)
        {
            var result = new List<MethodDefinition>();
            foreach (var type in assembly.MainModule.Types)
            {
                if (type == null) continue;
                foreach (var method in type.Methods)
                {
                    if (method == null) continue;

                    if (method.ReturnType != null && method.ReturnType.FullName == typeName)
                    {
                        result.Add(method);
                        continue;
                    }

                    bool addedForParam = false;
                    if (method.Parameters != null)
                    {
                        foreach (var param in method.Parameters)
                        {
                            if (param.ParameterType != null && param.ParameterType.FullName == typeName)
                            {
                                result.Add(method);
                                addedForParam = true;
                                break;
                            }
                        }
                    }
                    if (addedForParam) continue;

                    if (method.HasBody && method.Body != null && method.Body.Instructions != null)
                    {
                        foreach (var instr in method.Body.Instructions)
                        {
                            TypeReference typeRef = instr.Operand as TypeReference;
                            if (typeRef != null && typeRef.FullName == typeName) { result.Add(method); break; }

                            MethodReference mRef = instr.Operand as MethodReference;
                            if (mRef != null && mRef.DeclaringType != null && mRef.DeclaringType.FullName == typeName) { result.Add(method); break; }

                            FieldReference fRef = instr.Operand as FieldReference;
                            if (fRef != null && fRef.DeclaringType != null && fRef.DeclaringType.FullName == typeName) { result.Add(method); break; }
                        }
                    }
                }
            }
            return result;
        }

        public static List<TypeDefinition> FindAllTypesReferencingType(AssemblyDefinition assembly, string typeName)
        {
            var result = new List<TypeDefinition>();
            foreach (var type in assembly.MainModule.Types)
            {
                if (type == null) continue;
                if (ReferencesType(type, typeName)) result.Add(type);
            }
            return result;
        }

        private static bool ReferencesType(TypeDefinition type, string name)
        {
            if (type == null) return false;

            if (type.BaseType != null && type.BaseType.FullName == name) return true;

            if (type.Interfaces != null)
                foreach (var iface in type.Interfaces)
                    if (iface.InterfaceType != null && iface.InterfaceType.FullName == name) return true;

            if (type.Fields != null)
                foreach (var field in type.Fields)
                    if (field != null && field.FieldType != null && field.FieldType.FullName == name) return true;

            if (type.Properties != null)
                foreach (var prop in type.Properties)
                    if (prop != null && prop.PropertyType != null && prop.PropertyType.FullName == name) return true;

            if (type.Events != null)
                foreach (var evt in type.Events)
                    if (evt != null && evt.EventType != null && evt.EventType.FullName == name) return true;

            if (type.Methods != null)
            {
                foreach (var method in type.Methods)
                {
                    if (method == null) continue;
                    if (method.ReturnType != null && method.ReturnType.FullName == name) return true;
                    if (method.Parameters != null)
                        foreach (var param in method.Parameters)
                            if (param.ParameterType != null && param.ParameterType.FullName == name) return true;
                }
            }

            return false;
        }

        public static List<MethodDefinition> FindMethodsReferencingTypes(AssemblyDefinition assembly, string[] typeNames)
        {
            // Use a HashSet to accumulate without duplicates (avoids Distinct() LINQ call).
            var seen = new HashSet<MethodDefinition>();
            foreach (var typeName in typeNames)
                foreach (var method in FindMethodsReferencingType(assembly, typeName))
                    seen.Add(method);
            return new List<MethodDefinition>(seen);
        }

        public static List<FieldDefinition> FindFieldsReferencingType(AssemblyDefinition assembly, string typeName)
        {
            var result = new List<FieldDefinition>();
            foreach (var type in assembly.MainModule.Types)
            {
                if (type == null) continue;
                foreach (var field in type.Fields)
                {
                    if (field == null) continue;
                    if (field.FieldType != null && field.FieldType.FullName == typeName) result.Add(field);
                }
            }
            return result;
        }

        public static List<FieldDefinition> FindFieldsReferencingTypes(AssemblyDefinition assembly, string[] typeNames)
        {
            var seen = new HashSet<FieldDefinition>();
            foreach (var typeName in typeNames)
                foreach (var field in FindFieldsReferencingType(assembly, typeName))
                    seen.Add(field);
            return new List<FieldDefinition>(seen);
        }

        public static List<TypeDefinition> FindTypesReferencingType(AssemblyDefinition assembly, string typeName)
        {
            var result = new List<TypeDefinition>();
            foreach (var type in assembly.MainModule.Types)
            {
                if (type == null) continue;
                if (type.BaseType != null && type.BaseType.FullName == typeName) { result.Add(type); continue; }
                if (type.Interfaces != null)
                    foreach (var iface in type.Interfaces)
                        if (iface.InterfaceType != null && iface.InterfaceType.FullName == typeName) { result.Add(type); break; }
            }
            return result;
        }

        public static List<PropertyDefinition> FindPropertiesReferencingType(AssemblyDefinition assembly, string typeName)
        {
            var result = new List<PropertyDefinition>();
            foreach (var type in assembly.MainModule.Types)
            {
                if (type == null) continue;
                foreach (var prop in type.Properties)
                {
                    if (prop == null) continue;
                    if (prop.PropertyType != null && prop.PropertyType.FullName == typeName) result.Add(prop);
                }
            }
            return result;
        }

        public static List<EventDefinition> FindEventsReferencingType(AssemblyDefinition assembly, string typeName)
        {
            var result = new List<EventDefinition>();
            foreach (var type in assembly.MainModule.Types)
            {
                if (type == null) continue;
                foreach (var evt in type.Events)
                {
                    if (evt == null) continue;
                    if (evt.EventType != null && evt.EventType.FullName == typeName) result.Add(evt);
                }
            }
            return result;
        }
    }

    // -----------------------------------------------------------------------------------------
    // AssemblyIntegrityValidator
    //
    // Walks a Cecil assembly before writing and reports null members, bad operands, and
    // dangling assembly references.  Called by SafeAssemblyStripper after each stripping pass.
    // -----------------------------------------------------------------------------------------
    public static class AssemblyIntegrityValidator
    {
        public static ValidationResult ValidateAssemblyIntegrity(AssemblyDefinition assembly)
        {
            var result = new ValidationResult();
            if (assembly == null) { result.Errors.Add("Assembly is null"); return result; }
            if (assembly.MainModule == null) { result.Errors.Add("Assembly MainModule is null"); return result; }

            int nullTypeCount = 0;
            foreach (var t in assembly.MainModule.Types) { if (t == null) nullTypeCount++; }
            if (nullTypeCount > 0) result.Errors.Add("Assembly contains " + nullTypeCount + " null types");

            foreach (var type in assembly.MainModule.Types)
            {
                if (type == null) continue;
                ValidateTypeIntegrity(type, result);
            }

            ValidateAssemblyReferences(assembly, result);
            return result;
        }

        private static void ValidateTypeIntegrity(TypeDefinition type, ValidationResult result)
        {
            foreach (var method in type.Methods)
            {
                if (method == null) { result.Errors.Add("Type " + type.Name + " contains null method"); continue; }
                if (method.HasBody && method.Body == null) result.Errors.Add("Method " + method.Name + " has null body");
                if (method.HasBody) ValidateMethodBody(method, result);
            }
            foreach (var field in type.Fields)
                if (field == null) result.Errors.Add("Type " + type.Name + " contains null field");
            foreach (var nested in type.NestedTypes)
            {
                if (nested == null) result.Errors.Add("Type " + type.Name + " contains null nested type");
                else ValidateTypeIntegrity(nested, result);
            }
        }

        private static void ValidateMethodBody(MethodDefinition method, ValidationResult result)
        {
            if (method.Body.Instructions == null)
            {
                result.Errors.Add("Method " + method.Name + " has null instructions collection");
                return;
            }
            foreach (var instr in method.Body.Instructions)
            {
                if (instr == null) { result.Errors.Add("Method " + method.Name + " contains null instruction"); continue; }
                if (instr.Operand == null && instr.OpCode.OperandType != OperandType.InlineNone)
                    result.Errors.Add("Method " + method.Name + " has null operand for " + instr.OpCode);
            }
        }

        private static void ValidateAssemblyReferences(AssemblyDefinition assembly, ValidationResult result)
        {
            if (assembly.MainModule.AssemblyReferences == null)
            {
                result.Errors.Add("Assembly references collection is null");
                return;
            }
            foreach (var reference in assembly.MainModule.AssemblyReferences)
                if (reference == null) result.Errors.Add("Assembly contains null assembly reference");
        }

        public static bool CanSafelyWriteAssembly(AssemblyDefinition assembly)
        {
            return ValidateAssemblyIntegrity(assembly).IsValid;
        }
    }

    public class ValidationResult
    {
        public List<string> Errors { get; private set; }
        public ValidationResult() { Errors = new List<string>(); }
        public bool IsValid { get { return Errors.Count == 0; } }
        public string GetErrorString() { return string.Join("; ", Errors.ToArray()); }
    }

    // -----------------------------------------------------------------------------------------
    // SafeAssemblyStripper
    //
    // Orchestrates multi-pass removal of unwanted types from a Cecil AssemblyDefinition.
    // For each target type it removes: dependent types, methods, fields, properties, and events
    // before removing the type itself.  Runs up to 3 passes to handle cascading dependencies.
    // Calls AssemblyIntegrityValidator after stripping to guard against corrupt state.
    // -----------------------------------------------------------------------------------------
    public static class SafeAssemblyStripper
    {
        // Toggle verbose debug logging.  Off by default to avoid noisy editor output.
        public static bool DebugEnabled = false;

        // Types that are never stripped even when inferred as dependents.
        public static readonly string[] PreserveTypes =
        {
            "FistVR.ManagerSingleton`1",
            "FistVR.GM", "FistVR.IM", "FistVR.OM",
            "FistVR.SM", "FistVR.AM", "FistVR.PM", "FistVR.FXM"
        };

        public static bool IsPreservedType(TypeDefinition type)
        {
            if (type == null) return false;
            foreach (var p in PreserveTypes) if (p == type.FullName) return true;
            return false;
        }

        public static StrippingResult StripTypesSafely(AssemblyDefinition assembly, string[] typesToStrip)
        {
            var result = new StrippingResult();
            if (assembly == null) { result.Errors.Add("Assembly is null"); return result; }
            if (typesToStrip == null || typesToStrip.Length == 0)
            {
                if (DebugEnabled) Debug.Log("[MeatKit] No types to strip");
                return result;
            }

            if (DebugEnabled) Debug.Log("[MeatKit] Starting safe stripping of " + typesToStrip.Length + " types");

            for (int pass = 1; pass <= 3; pass++)
            {
                if (DebugEnabled) Debug.Log("[MeatKit] Stripping pass " + pass + "...");
                int typesStrippedThisPass = 0;
                foreach (var typeName in typesToStrip)
                {
                    try
                    {
                        int before = result.StrippedTypes.Count;
                        StripSingleTypeSafely(assembly, typeName, result);
                        if (result.StrippedTypes.Count > before) typesStrippedThisPass++;
                    }
                    catch (Exception ex)
                    {
                        result.Errors.Add("Failed to strip type " + typeName + ": " + ex.Message);
                        Debug.LogError("[MeatKit] Error stripping type " + typeName + ": " + ex.Message);
                    }
                }
                if (DebugEnabled) Debug.Log("[MeatKit] Pass " + pass + " completed. Stripped " + typesStrippedThisPass + " types.");
                if (typesStrippedThisPass == 0) break;
            }

            var validation = AssemblyIntegrityValidator.ValidateAssemblyIntegrity(assembly);
            if (!validation.IsValid)
            {
                result.Errors.AddRange(validation.Errors);
                Debug.LogError("[MeatKit] Assembly validation failed after stripping: " + validation.GetErrorString());
            }

            if (DebugEnabled) Debug.Log("[MeatKit] Safe stripping completed. " + result.GetSummary());
            return result;
        }

        private static void StripSingleTypeSafely(AssemblyDefinition assembly, string typeName, StrippingResult result)
        {
            if (DebugEnabled) Debug.Log("[MeatKit] Stripping type: " + typeName);

            foreach (var type in MethodBodyAnalyzer.FindAllTypesReferencingType(assembly, typeName))
            {
                if (type == null) continue;
                if (IsPreservedType(type))
                {
                    if (DebugEnabled) Debug.Log("[MeatKit] Preserved dependent type: " + type.FullName);
                    continue;
                }
                try
                {
                    RemoveTypeCompletely(assembly, type);
                    result.StrippedTypes.Add(type.FullName);
                    if (DebugEnabled) Debug.Log("[MeatKit] Removed dependent type: " + type.FullName);
                }
                catch (Exception ex)
                {
                    if (DebugEnabled) Debug.LogWarning("[MeatKit] Failed to remove dependent type " + type.FullName + ": " + ex.Message);
                }
            }

            foreach (var method in MethodBodyAnalyzer.FindMethodsReferencingType(assembly, typeName))
            {
                if (method == null || method.DeclaringType == null) continue;
                try
                {
                    method.DeclaringType.Methods.Remove(method);
                    result.RemovedMethods.Add(method.DeclaringType.Name + "::" + method.Name);
                    if (DebugEnabled) Debug.Log("[MeatKit] Removed method: " + method.DeclaringType.Name + "::" + method.Name);
                }
                catch { }
            }

            foreach (var field in MethodBodyAnalyzer.FindFieldsReferencingType(assembly, typeName))
            {
                if (field == null || field.DeclaringType == null) continue;
                try
                {
                    field.DeclaringType.Fields.Remove(field);
                    result.RemovedFields.Add(field.DeclaringType.Name + "::" + field.Name);
                    if (DebugEnabled) Debug.Log("[MeatKit] Removed field: " + field.DeclaringType.Name + "::" + field.Name);
                }
                catch { }
            }

            foreach (var prop in MethodBodyAnalyzer.FindPropertiesReferencingType(assembly, typeName))
            {
                if (prop == null || prop.DeclaringType == null) continue;
                try
                {
                    prop.DeclaringType.Properties.Remove(prop);
                    result.RemovedFields.Add(prop.DeclaringType.Name + "::" + prop.Name + " (property)");
                    if (DebugEnabled) Debug.Log("[MeatKit] Removed property: " + prop.DeclaringType.Name + "::" + prop.Name);
                }
                catch { }
            }

            foreach (var evt in MethodBodyAnalyzer.FindEventsReferencingType(assembly, typeName))
            {
                if (evt == null || evt.DeclaringType == null) continue;
                try
                {
                    evt.DeclaringType.Events.Remove(evt);
                    result.RemovedFields.Add(evt.DeclaringType.Name + "::" + evt.Name + " (event)");
                    if (DebugEnabled) Debug.Log("[MeatKit] Removed event: " + evt.DeclaringType.Name + "::" + evt.Name);
                }
                catch { }
            }

            var targetType = assembly.MainModule.GetType(typeName);
            if (targetType != null)
            {
                if (!IsPreservedType(targetType))
                {
                    RemoveTypeCompletely(assembly, targetType);
                    result.StrippedTypes.Add(typeName);
                    if (DebugEnabled) Debug.Log("[MeatKit] Stripped type: " + typeName);
                }
                else
                {
                    if (DebugEnabled) Debug.Log("[MeatKit] Preserved type (strip target): " + typeName);
                }
            }
            else
            {
                TypeDefinition foundType = null;
                foreach (var type in assembly.MainModule.Types)
                    if (type != null && type.FullName == typeName) { foundType = type; break; }

                if (foundType != null)
                {
                    RemoveTypeCompletely(assembly, foundType);
                    result.StrippedTypes.Add(typeName);
                    if (DebugEnabled) Debug.Log("[MeatKit] Stripped type (found by search): " + typeName);
                }
                else if (DebugEnabled)
                {
                    Debug.LogWarning("[MeatKit] Type " + typeName + " was not found in assembly");
                }
            }
        }

        private static void RemoveTypeCompletely(AssemblyDefinition assembly, TypeDefinition type)
        {
            if (assembly == null || assembly.MainModule == null || assembly.MainModule.Types == null || type == null)
                return;
            if (IsPreservedType(type)) return;
            try
            {
                if (type.NestedTypes != null)
                {
                    var nestedCopy = new List<TypeDefinition>(type.NestedTypes);
                    foreach (var nested in nestedCopy) RemoveTypeCompletely(assembly, nested);
                }
                assembly.MainModule.Types.Remove(type);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[MeatKit] Failed to remove type " + type.Name + ": " + ex.Message);
            }
        }
    }

    public class StrippingResult
    {
        public List<string> StrippedTypes { get; private set; }
        public List<string> RemovedMethods { get; private set; }
        public List<string> RemovedFields { get; private set; }
        public List<string> Errors { get; private set; }

        public StrippingResult()
        {
            StrippedTypes = new List<string>();
            RemovedMethods = new List<string>();
            RemovedFields = new List<string>();
            Errors = new List<string>();
        }

        public bool HasErrors { get { return Errors.Count > 0; } }
        public bool IsSuccessful { get { return !HasErrors; } }
        public string GetSummary() { return "Stripped " + StrippedTypes.Count + " types, removed " + RemovedMethods.Count + " methods, removed " + RemovedFields.Count + " fields"; }
        public string GetErrorString() { return string.Join("; ", Errors.ToArray()); }
    }
}
