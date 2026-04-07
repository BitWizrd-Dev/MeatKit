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

            // Make a copy of the editor assembly because when we build an asset bundle, Unity will delete it
            string editorAssembly  = EditorAssemblyPath + AssemblyName + ".dll";
            string editorFirstpass = EditorAssemblyPath + AssemblyFirstpassName + ".dll";
            string tempAssemblyFile  = Path.GetTempFileName();
            string tempFirstpassFile = Path.GetTempFileName();
            DateTime editorAsmMtime = File.GetLastWriteTimeUtc(editorAssembly);
            DateTime editorFpMtime  = File.Exists(editorFirstpass) ? File.GetLastWriteTimeUtc(editorFirstpass) : DateTime.UtcNow;
            BuildLog.WriteLine("Copying editor assembly: " + editorAssembly + " -> " + tempAssemblyFile);
            File.Copy(editorAssembly, tempAssemblyFile, true);
            if (File.Exists(editorFirstpass))
            {
                BuildLog.WriteLine("Copying editor firstpass: " + editorFirstpass + " -> " + tempFirstpassFile);
                File.Copy(editorFirstpass, tempFirstpassFile, true);
            }

            // Make sure we have the virtual reality supported checkbox enabled
            // If this is not set to true when we build our asset bundles, the shaders will not compile correctly
            BuildLog.WriteLine("Forcing VR support on");
            bool wasVirtualRealitySupported = PlayerSettings.virtualRealitySupported;
            PlayerSettings.virtualRealitySupported = true;

            // Create a map of assembly names to what we want to rename them to, then enable bundle processing.
            // H3VRCode-CSharp entries are intentionally absent: the hook no longer renames them
            // in-place (which corrupted m_ScriptCache on the second build). Instead,
            // PostProcessBundles() does a safe binary replacement after BuildAssetBundles.
            var replaceMap = new Dictionary<string, string>
            {
                {AssemblyName + ".dll", profile.PackageName + ".dll"},
                {AssemblyFirstpassName + ".dll", profile.PackageName + "-firstpass.dll"}
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

            // Guard H3VRCode TypeTree during BuildAssetBundles. EATI (ExtractAssemblyTypeInfoAll) is a native
            // Unity function that extracts type metadata; we back up before it runs and restore after to prevent
            // it from overwriting our H3VRCode TypeTree modifications.
            Dictionary<string, byte[]> _eatICapturedBackup = null;
            Action _beforeEATI = delegate
            {
                // Ensure H3VRCode DLLs are in Library/ScriptAssemblies/ BEFORE EATI processes them.
                ManagedPluginDomainFix.EnsureH3VRCodeInScriptAssemblies();
                NativeHookManager.InsideEATI = true;
                NativeHookManager.InsideBundleEATI = true;
                ManagedPluginDomainFix.ReprimeMBCachesBeforeEATI();
                if (_eatICapturedBackup == null)
                {
                    _eatICapturedBackup = ManagedPluginDomainFix.BackupH3VRCodeTypeTreeCache();
                }
            };

            Action _afterEATI = delegate
            {
                ManagedPluginDomainFix.ReprimeSilentAfterEATI();
                ManagedPluginDomainFix._pendingShutdownRestore = _eatICapturedBackup;
            };

            // Prime TypeTrees before EATI hooks are registered so SaveAssets() inside
            // PrimeTypeTreesForBuild() cannot race with the backup/restore callbacks.
            ManagedPluginDomainFix.PrimeTypeTreesForBuild();

            NativeHookManager.BeforeEATICallbacks.Add(_beforeEATI);
            NativeHookManager.AfterEATICallbacks.Add(_afterEATI);

            var bundleManifest = BuildPipeline.BuildAssetBundles(bundleOutputPath, bundles,
                BuildAssetBundleOptions.ChunkBasedCompression, BuildTarget.StandaloneWindows64);

            NativeHookManager.BeforeEATICallbacks.Remove(_beforeEATI);
            NativeHookManager.AfterEATICallbacks.Remove(_afterEATI);
            NativeHookManager.InsideBundleEATI = false;

            if (bundleManifest == null)
                throw new MeatKitBuildException("AssetBundle build failed to produce a manifest. Check the console for errors.");

            // Disable bundle processing now that we're done with it.
            AssetBundleIO.DisableProcessing();
            BuildLog.WriteLine("Bundles built");

            // Restore H3VRCode DLLs to Library/ScriptAssemblies/ immediately.
            // BuildAssetBundles deletes them during its standalone compile, and the post-build
            // domain reload's compiler needs them to resolve FistVR types (H3VR_IMPORTED guard).
            // The delayCall in ManagedPluginDomainFix also does this, but too late — the compiler
            // runs before delayCall fires.
            ManagedPluginDomainFix.EnsureH3VRCodeInScriptAssemblies();
            BuildLog.WriteLine("Restored H3VRCode DLLs to ScriptAssemblies");

            // If the firstpass backup was empty (file was absent before the build), the standalone
            // compile inside BuildAssetBundles will have written a real Assembly-CSharp-firstpass.dll
            // to Library/ScriptAssemblies/.  Capture it now into tempFirstpassFile so the restore
            // below puts valid bytes back with the original mtime.  Using the original mtime is
            // critical: Unity 5.6 uses mtime-only change detection for DLLs, so restoring T0
            // prevents a spurious editor recompile.  Without this, the editor recompile produces
            // a 0-byte firstpass (no .cs sources in Assets/Plugins/) which causes Build 2's
            // AssemblyHelper.ExtractAssemblyTypeInfo to throw BadImageFormatException.
            if (new FileInfo(tempFirstpassFile).Length == 0 && File.Exists(editorFirstpass) && new FileInfo(editorFirstpass).Length > 0)
            {
                File.Copy(editorFirstpass, tempFirstpassFile, true);
                BuildLog.WriteLine("Updated firstpass backup with standalone-compiled version (" + new FileInfo(tempFirstpassFile).Length + " bytes)");
            }

            // ### FIX: Validate firstpass before restore to prevent 0-byte DLL corruption ###
            // Check if tempFirstpassFile is 0 bytes (invalid). If so, don't restore it.
            // A 0-byte firstpass causes domain reload BadImageFormatException and Build 2 crash.
            long firstpassBackupSize = new FileInfo(tempFirstpassFile).Length;
            bool firstpassBackupIsValid = firstpassBackupSize > 0;

            // Restore both editor DLLs unconditionally with original mtime. The BPEVST hook
            // suppresses the post-bundle standalone compile, but if it fails the compile would
            // replace these DLLs with H3VR_IMPORTED-absent versions; the post-build domain
            // reload would then load wrong DLLs causing null MonoScripts and Anvil fields vanishing.
            if (File.Exists(tempAssemblyFile))
            {
                try
                {
                    File.Copy(tempAssemblyFile, editorAssembly, true);
                    File.SetLastWriteTimeUtc(editorAssembly, editorAsmMtime);
                    BuildLog.WriteLine("Restored Assembly-CSharp.dll");
                }
                catch (Exception ex)
                {
                    BuildLog.WriteLine("WARNING: Failed to restore Assembly-CSharp.dll: " + ex.Message);
                }
            }
            if (firstpassBackupIsValid && File.Exists(tempFirstpassFile))
            {
                try
                {
                    File.Copy(tempFirstpassFile, editorFirstpass, true);
                    File.SetLastWriteTimeUtc(editorFirstpass, editorFpMtime);
                    BuildLog.WriteLine("Restored Assembly-CSharp-firstpass.dll");
                }
                catch (Exception ex)
                {
                    BuildLog.WriteLine("WARNING: Failed to restore Assembly-CSharp-firstpass.dll: " + ex.Message);
                }
            }
            else if (!firstpassBackupIsValid)
            {
                BuildLog.WriteLine("SKIPPED: Assembly-CSharp-firstpass.dll restore — backup is " + firstpassBackupSize + " bytes (invalid)");
                BuildLog.WriteLine("  This is expected if no firstpass DLL exists in the project.");
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

            // Binary-replace H3VRCode-CSharp → Assembly-CSharp in every output bundle file.
            // Both strings are 15 bytes, so the replacement is in-place and preserves every
            // length prefix in the binary format (including MMHOOK-H3VRCode-CSharp variants).
            PostProcessBundles(bundleOutputPath);

            // Reset the virtual reality supported checkbox, so if the user had it disabled it will stay disabled
            PlayerSettings.virtualRealitySupported = wasVirtualRealitySupported;

            // And export the assembly to the folder
            BuildLog.WriteLine("Exporting editor assembly");
            var requiredScripts = AssetBundleIO.SerializedScriptNames;
            ExportEditorAssembly(bundleOutputPath, tempAssemblyFile, requiredScripts);
            try { if (File.Exists(tempAssemblyFile))  File.Delete(tempAssemblyFile);  } catch { }
            try { if (File.Exists(tempFirstpassFile)) File.Delete(tempFirstpassFile); } catch { }

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
                using (var zip = new ZipFile())
                {
                    zip.AddDirectory(bundleOutputPath, "");
                    var zipPath = Path.Combine(bundleOutputPath, packageName + "-" + profile.Version + ".zip");
                    zip.Save(zipPath);
                    
                    if (MeatKitCache.OpenFolderAfterBuild && File.Exists(zipPath))
                        EditorUtility.RevealInFinder(zipPath);
                }
            }
            else
            {
                BuildLog.WriteLine("Opening folder with built files");
                if (MeatKitCache.OpenFolderAfterBuild && File.Exists(readmePath))
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

        /// <summary>
        /// Binary-replaces every occurrence of "H3VRCode-CSharp" with "Assembly-CSharp" in all
        /// bundle files under <paramref name="outputPath"/>.  Both strings are exactly 15 bytes,
        /// so no length prefixes or offsets in Unity's binary bundle format are disturbed.
        /// This covers H3VRCode-CSharp.dll, H3VRCode-CSharp-firstpass.dll, and any MonoMod
        /// MMHOOK-H3VRCode-CSharp variants — all in a single pass per file.
        /// </summary>
        private static void PostProcessBundles(string outputPath)
        {
            byte[] oldBytes = System.Text.Encoding.ASCII.GetBytes(AssemblyRename); // "H3VRCode-CSharp" (15)
            byte[] newBytes = System.Text.Encoding.ASCII.GetBytes(AssemblyName);   // "Assembly-CSharp"  (15)
            if (oldBytes.Length != newBytes.Length)
            {
                BuildLog.WriteLine("[PostProcessBundles] SKIP: assembly name lengths differ (" +
                                   oldBytes.Length + " vs " + newBytes.Length + ") — binary replacement unsafe");
                return;
            }
            int patched = 0;
            foreach (var file in Directory.GetFiles(outputPath))
            {
                try
                {
                    byte[] data = File.ReadAllBytes(file);
                    bool modified = false;
                    for (int i = 0; i <= data.Length - oldBytes.Length; i++)
                    {
                        bool match = true;
                        for (int j = 0; j < oldBytes.Length; j++)
                        {
                            if (data[i + j] != oldBytes[j]) { match = false; break; }
                        }
                        if (match)
                        {
                            for (int j = 0; j < newBytes.Length; j++)
                                data[i + j] = newBytes[j];
                            i += newBytes.Length - 1;
                            modified = true;
                            patched++;
                        }
                    }
                    if (modified)
                        File.WriteAllBytes(file, data);
                }
                catch (Exception ex)
                {
                    BuildLog.WriteLine("[PostProcessBundles] WARNING: failed to patch " + file + ": " + ex.Message);
                }
            }
            BuildLog.WriteLine("[PostProcessBundles] Patched " + patched + " occurrence(s) of \"" +
                               AssemblyRename + "\" → \"" + AssemblyName + "\" across bundle files");
        }


    }
}
