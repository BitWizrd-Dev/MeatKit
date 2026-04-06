using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Ionic.Zip;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace MeatKit
{
    public partial class MeatKit
    {
        // Guards against re-entrant builds.
        internal static bool _buildRunning = false;

        public static void DoBuild()
        {
            if (_buildRunning)
            {
                Debug.LogWarning("[MeatKit] DoBuild called while a build is already running — ignoring.");
                return;
            }

            // Refuse to start a build while scripts are still being compiled
            if (EditorApplication.isCompiling)
            {
                EditorUtility.DisplayDialog("Cannot build",
                    "Scripts are currently being compiled. Please wait for compilation to finish and then try again.",
                    "Ok");
                return;
            }

            // Lock assembly reloads for the duration of the build to prevent a crash on subsequent builds
            _buildRunning = true;
            EditorApplication.LockReloadAssemblies();

            BuildLog.StartNew();
            try
            {
                DoBuildInternal();
            }
            catch (System.Threading.ThreadAbortException)
            {
                System.Threading.Thread.ResetAbort();
                NativeHookManager.InsideEATI = false;
                BuildLog.SetCompletionStatus(true, "Build interrupted by domain reload.", null);
                Debug.LogWarning("[MeatKit] Build was interrupted by a domain reload. Please try again.");
            }
            catch (MeatKitBuildException e)
            {
                NativeHookManager.InsideEATI = false;
                string message = e.Message;
                if (e.InnerException != null) message += "\n\n" + e.InnerException.Message;
                EditorUtility.DisplayDialog("Build failed", message, "Ok.");
                Debug.LogError("Build failed: " + message);
                BuildLog.SetCompletionStatus(true, "MeatKit Build Exception", e);
            }
            catch (Exception e)
            {
                NativeHookManager.InsideEATI = false;
                EditorUtility.DisplayDialog("Build failed with unknown error",
                    "Error message: " + e.Message + "\n\nCheck console for full exception text.", "Ok.");
                Debug.LogException(e);
                BuildLog.SetCompletionStatus(true, "Unexpected exception during build", e);
            }
            finally
            {
                _buildRunning = false;
                try { BuildLog.Finish(); }
                catch (System.Threading.ThreadAbortException) { System.Threading.Thread.ResetAbort(); }
                catch (Exception) { }
                // Defer the unlock to the next editor frame so the domain reload fires with a clean call stack
                EditorApplication.delayCall += UnlockReloadDelayed;
            }
        }

        // Deferred unlock so the domain reload fires on a clean call stack
        private static void UnlockReloadDelayed()
        {
            EditorApplication.UnlockReloadAssemblies();
        }

        private static void DoBuildInternal()
        {
            // Make sure the scripts are imported.
            if (ShowErrorIfH3VRNotImported()) return;

            // Get our profile and make sure it isn't null
            BuildProfile profile = BuildWindow.SelectedProfile;
            if (!profile) return;

            string bundleOutputPath = profile.ExportPath;

            // Start a stopwatch to time the build
            Stopwatch sw = Stopwatch.StartNew();

            // If there's anything invalid in the settings don't continue
            if (!profile.EnsureValidForEditor()) return;

            // Clean the output folder
            BuildLog.WriteLine("Cleaning build folder");
            CleanBuild(profile);

            // Back up both editor assemblies before BuildAssetBundles replaces them.
            string editorAssembly = EditorAssemblyPath + AssemblyName + ".dll";
            string tempAssemblyFile = Path.GetTempFileName();
            DateTime originalAsmMtime = File.GetLastWriteTimeUtc(editorAssembly);
            BuildLog.WriteLine("Backing up editor assembly");
            File.Copy(editorAssembly, tempAssemblyFile, true);

            string editorFirstpassAssembly = EditorAssemblyPath + AssemblyFirstpassName + ".dll";
            string tempFirstpassFile = null;
            DateTime originalFirstpassMtime = default(DateTime);
            if (File.Exists(editorFirstpassAssembly))
            {
                tempFirstpassFile = Path.GetTempFileName();
                originalFirstpassMtime = File.GetLastWriteTimeUtc(editorFirstpassAssembly);
                BuildLog.WriteLine("Backing up editor firstpass assembly");
                try { File.Copy(editorFirstpassAssembly, tempFirstpassFile, true); }
                catch (Exception ex) { BuildLog.WriteLine("WARNING: Failed to back up firstpass DLL: " + ex.Message); tempFirstpassFile = null; }
            }
            
            // Make sure we have the virtual reality supported checkbox enabled
            // If this is not set to true when we build our asset bundles, the shaders will not compile correctly
            BuildLog.WriteLine("Forcing VR support on");
            bool wasVirtualRealitySupported = PlayerSettings.virtualRealitySupported;
            PlayerSettings.virtualRealitySupported = true;

            // Create a map of assembly names to what we want to rename them to, then enable bundle processing
            var replaceMap = new Dictionary<string, string>
            {
                {AssemblyName + ".dll", profile.PackageName + ".dll"},
                {AssemblyFirstpassName + ".dll", profile.PackageName + "-firstpass.dll"},
                {AssemblyRename + ".dll", AssemblyName + ".dll"},
                {AssemblyFirstpassRename + ".dll", AssemblyFirstpassName + ".dll"}
            };
            BuildLog.WriteLine("Enabling bundle processing.");
            BuildLog.WriteLine("Replace map:");
            foreach (var key in replaceMap.Keys)
                BuildLog.WriteLine("  " + key + " -> " + replaceMap[key]);
            BuildLog.WriteLine("Ignored types (Assembly-CSharp.dll):");
            foreach (var type in StripAssemblyTypes)
                BuildLog.WriteLine("  " + type);
            AssetBundleIO.EnableProcessing(replaceMap, false, true);

            // Get the list of asset bundle configurations and build them
            BuildLog.WriteLine("Collecting bundles from build items");
            var bundles = profile.BuildItems.SelectMany(x => x.ConfigureBuild()).ToArray();

            BuildLog.WriteLine("Adding Author and PackageName to internal bundle names");
            var bundleNameMap = new Dictionary<string, string>();
            for (var i = 0; i < bundles.Length; i++)
            {
                var originalName = bundles[i].assetBundleName;
                // Needed to prevent runtime conflicts. 2 bundles with the same internal (build-time) name
                // cannot be loaded simultaneously by Unity. Apply lowercase, since names passed to BuildPipeline
                // are also lowercased.
                var buildTimeName = (profile.Author + "." + profile.PackageName + "." + originalName).ToLower();
                if (bundleNameMap.ContainsKey(buildTimeName))
                    throw new MeatKitBuildException("Two or more AssetBundles share the same name - this is not " +
                                                    "supported. Make sure all your AssetBundles have unique names.");
                bundleNameMap[buildTimeName] = originalName;
                bundles[i].assetBundleName = buildTimeName;
            }

            BuildLog.WriteLine(bundles.Length + " bundles to build. Building bundles.");

            if (!NativeHookManager.EATIHookInstalled)
                BuildLog.WriteLine("WARNING: EATI hook not installed — TypeTree guard inactive.");
            if (NativeHookManager.AHVTIHookInstalled)
                BuildLog.WriteLine("AHVTI hook active — H3VRCode TypeTree will be preserved natively.");
            else
                BuildLog.WriteLine("AHVTI hook not installed — falling back to TypeTree backup/restore.");

            Dictionary<string, byte[]> _eatICapturedBackup = null;
            Action _beforeEATI = delegate
            {
                NativeHookManager.InsideEATI = true;
                if (_eatICapturedBackup == null)
                    _eatICapturedBackup = ManagedPluginDomainFix.BackupH3VRCodeTypeTreeCache();
            };

            Action _afterEATI = delegate
            {
                ManagedPluginDomainFix.RestoreH3VRCodeTypeTreeCache(_eatICapturedBackup);
                ManagedPluginDomainFix._pendingShutdownRestore = _eatICapturedBackup;
                BuildLog.WriteLine("Post-EATI: TypeTree metadata restored");
            };

            NativeHookManager.BeforeEATICallbacks.Add(_beforeEATI);
            NativeHookManager.AfterEATICallbacks.Add(_afterEATI);

            var bundleManifest = BuildPipeline.BuildAssetBundles(bundleOutputPath, bundles,
                BuildAssetBundleOptions.ChunkBasedCompression, BuildTarget.StandaloneWindows64);

            NativeHookManager.BeforeEATICallbacks.Remove(_beforeEATI);
            NativeHookManager.AfterEATICallbacks.Remove(_afterEATI);
            BuildLog.WriteLine("PostBuild: InsideEATI kept True to guard post-build compilation EATI");
            if (_eatICapturedBackup != null && _eatICapturedBackup.Count > 0)
            {
                ManagedPluginDomainFix.RestoreH3VRCodeTypeTreeCache(_eatICapturedBackup);
                BuildLog.WriteLine("PostBuild: eager TypeTree restore (main + .info files)");
            }

            if (bundleManifest == null)
                throw new MeatKitBuildException("AssetBundle build failed to produce a manifest. Check the console for errors.");

            // Disable bundle processing now that we're done with it.
            AssetBundleIO.DisableProcessing();
            BuildLog.WriteLine("Bundles built");

            // Unconditionally restore both editor DLLs with original mtime.
            string _editorAsmPath = EditorAssemblyPath + AssemblyName + ".dll";
            if (File.Exists(tempAssemblyFile))
            {
                try
                {
                    File.Copy(tempAssemblyFile, _editorAsmPath, true);
                    File.SetLastWriteTimeUtc(_editorAsmPath, originalAsmMtime);
                    BuildLog.WriteLine("Restored Assembly-CSharp.dll");
                }
                catch (Exception ex)
                {
                    BuildLog.WriteLine("WARNING: Failed to restore Assembly-CSharp.dll: " + ex.Message);
                }
            }

            string _editorFirstpassPath = EditorAssemblyPath + AssemblyFirstpassName + ".dll";
            if (tempFirstpassFile != null && File.Exists(tempFirstpassFile))
            {
                try
                {
                    File.Copy(tempFirstpassFile, _editorFirstpassPath, true);
                    File.SetLastWriteTimeUtc(_editorFirstpassPath, originalFirstpassMtime);
                    BuildLog.WriteLine("Restored Assembly-CSharp-firstpass.dll");
                }
                catch (Exception ex)
                {
                    BuildLog.WriteLine("WARNING: Failed to restore Assembly-CSharp-firstpass.dll: " + ex.Message);
                }
            }

            // Cleanup the unused files created with building the bundles
            BuildLog.WriteLine("Cleaning unused files");
            foreach (var file in Directory.GetFiles(bundleOutputPath, "*.manifest"))
                File.Delete(file);
            File.Delete(Path.Combine(bundleOutputPath, profile.Version));

            // Rename built bundles back to their original names
            BuildLog.WriteLine("Verifying built bundles, restoring their original names in file system");
            foreach (var entry in bundleNameMap)
            {
                BuildLog.WriteLine("Renaming bundle: " + entry.Key + " -> " + entry.Value);
                var buildTimeNamePath = Path.Combine(bundleOutputPath, entry.Key);
                var originalNamePath = Path.Combine(bundleOutputPath, entry.Value);
                if (!File.Exists(buildTimeNamePath))
                    throw new MeatKitBuildException("One or more AssetBundles have failed to build! Check the " +
                                                    "console/build items for errors. Make sure your bundle names " +
                                                    "don't contain any illegal characters. " +
                                                    "Name of bundle that failed: " + entry.Value);
                File.Move(buildTimeNamePath, originalNamePath);
            }

            // Reset the virtual reality supported checkbox, so if the user had it disabled it will stay disabled
            PlayerSettings.virtualRealitySupported = wasVirtualRealitySupported;

            // And export the assembly to the folder
            BuildLog.WriteLine("Exporting editor assembly");
            var requiredScripts = AssetBundleIO.SerializedScriptNames;
            ExportEditorAssembly(bundleOutputPath, tempAssemblyFile, requiredScripts);
            File.Delete(tempAssemblyFile);

            // Now we can write the Thunderstore stuff to the folder
            BuildLog.WriteLine("Writing Thunderstore manifest");
            profile.WriteThunderstoreManifest(bundleOutputPath + "manifest.json");

            // Check if the icon is already 256x256
            Texture2D icon = profile.Icon;

            // Make sure our icon is marked as readable
            var importSettings = (TextureImporter) AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(profile.Icon));
            if (!importSettings.isReadable ||
                importSettings.textureCompression != TextureImporterCompression.Uncompressed)
            {
                BuildLog.WriteLine("Fixing icon import settings");
                importSettings.isReadable = true;
                importSettings.textureCompression = TextureImporterCompression.Uncompressed;
                importSettings.SaveAndReimport();
            }

            if (profile.Icon.width != 256 || profile.Icon.height != 256)
            {
                // Resize it for the build
                BuildLog.WriteLine("Icon was not 256x256, resizing");
                icon = icon.ScaleTexture(256, 256);
            }

            // Write the texture to file
            BuildLog.WriteLine("Saving icon");
            File.WriteAllBytes(bundleOutputPath + "icon.png", icon.EncodeToPNG());
            // Copy the readme
            BuildLog.WriteLine("Copying readme");
            var readmePath = bundleOutputPath + "README.md";
            File.Copy(AssetDatabase.GetAssetPath(profile.ReadMe), readmePath);

            if (profile.Changelog)
            {
                BuildLog.WriteLine("Copying changelog");
                File.Copy(AssetDatabase.GetAssetPath(profile.Changelog), bundleOutputPath + "CHANGELOG.md");
            }
            else
            {
                BuildLog.WriteLine("No changelog to copy");
            }

            string packageName = profile.Author + "-" + profile.PackageName;
            if (profile.BuildAction == BuildAction.CopyToProfile)
            {
                BuildLog.WriteLine("Copying built files to profile");
                string pluginFolder = Path.Combine(profile.OutputProfile, "BepInEx/plugins/" + packageName);
                if (Directory.Exists(pluginFolder)) Directory.Delete(pluginFolder, true);
                Directory.CreateDirectory(pluginFolder);
                Extensions.CopyFilesRecursively(bundleOutputPath, pluginFolder);
            }

            if (profile.BuildAction == BuildAction.CreateThunderstorePackage)
            {
                BuildLog.WriteLine("Zipping built files");
                var zipPath = Path.Combine(bundleOutputPath, packageName + "-" + profile.Version + ".zip");
                var zip = new ZipFile();
                try
                {
                    zip.AddDirectory(bundleOutputPath, "");
                    zip.Save(zipPath);
                }
                finally
                {
                    zip.Dispose();
                }

                if (File.Exists(zipPath))
                    EditorUtility.RevealInFinder(zipPath);
            }
            else
            {
                BuildLog.WriteLine("Opening folder with built files");
                if (File.Exists(readmePath))
                    EditorUtility.RevealInFinder(readmePath);
            }

            // End the stopwatch and save the time
            BuildLog.SetCompletionStatus(false, "", null);
            MeatKitCache.LastBuildDuration = sw.Elapsed;
            MeatKitCache.LastBuildTime = DateTime.Now;
        }

        public static void CleanBuild(BuildProfile profile)
        {
            string outputPath = profile.ExportPath;
            if (Directory.Exists(outputPath)) Directory.Delete(outputPath, true);
            Directory.CreateDirectory(outputPath);
        }



    }
}
