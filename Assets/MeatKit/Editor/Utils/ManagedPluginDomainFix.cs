using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace MeatKit
{
    // Fixes two domain-reload assembly-resolution issues:
    //
    // 1. H3VRCode-CSharp.dll crash: Unity wipes Library/ScriptAssemblies/ during compile,
    //    removing H3VRCode DLLs that Mono needs co-located with Assembly-CSharp.dll.
    //    Fix: NativeHookManager.BeforeShutdownCallbacks copies them back after compile,
    //    before the new-domain assembly loading begins.
    //
    // 2. Sodalite / UnityEngine.UI TypeLoadException: plugin DLLs are scanned by
    //    ProcessInitializeOnLoadAttributes before any [InitializeOnLoad] code runs.
    //    Sodalite.GetTypes() fails to resolve UnityEngine.UI because Unity's extension
    //    DLLs aren't in any standard Mono probe path at that point.
    //    Fix: mono_set_assemblies_path (exported from mono.dll) adds all UnityExtensions
    //    subdirectories to Mono's global (process-wide) assembly search path.
    [InitializeOnLoad]
    static class ManagedPluginDomainFix
    {
        private const bool DebugLogging = false;

        private static readonly string _managedDir;
        private static readonly string _scriptAssembliesDir;
        private static readonly string _unityExtensionsDir;
        private static string _pendingManifestPath;

        static ManagedPluginDomainFix()
        {
            _managedDir = Path.Combine(Application.dataPath, "Managed");
            _scriptAssembliesDir = Path.GetFullPath(
                Path.Combine(Path.Combine(Path.Combine(Application.dataPath, ".."), "Library"), "ScriptAssemblies"));
            _unityExtensionsDir = Path.Combine(EditorApplication.applicationContentsPath, "UnityExtensions");
            _pendingManifestPath = Path.GetFullPath(
                Path.Combine(Path.Combine(Path.Combine(Path.Combine(Application.dataPath, ".."), "Library"), "PendingDllImports"), "manifest.txt"));

            ApplyPendingDllImports();

            SetMonoAssemblySearchPaths();
            PreloadUnityExtensionDlls();
            AppDomain.CurrentDomain.AssemblyResolve += ResolveUnityExtensionAssembly;

            CopyMissingToScriptAssemblies();

            NativeHookManager.BeforeShutdownCallbacks.Add(CopyAllToScriptAssemblies);

            // Permanent BeforeEATI callback: ensures H3VRCode is copied to ScriptAssemblies
            // before every EATI call so EATI always processes the correct editor-context DLL.
            NativeHookManager.BeforeEATICallbacks.Add(EnsureH3VRCodeInScriptAssemblies);

            // Note: PIOLA and RUA hooks cannot be installed safely from an [InitializeOnLoad]
            // static ctor. ShutdownManaged + BeforeEATI callbacks cover all required work.

            // ── MonoScript repair ────────────────────────────────────────────────────────────────
            // Called directly from the ctor (H3VRCode is already loaded by RefreshPlugins at this
            // point). Suppresses RequestScriptReload during repair so CheckConsistency/
            // FixRuntimeScriptReference does not trigger an infinite domain-reload loop.
            if (NativeHookManager.RequestScriptReloadHookInstalled)
            {
                // Hook is live: suppress RequestScriptReload during ctor repair so that
                // CheckConsistency → FixRuntimeScriptReference fixes components but does NOT
                // schedule a domain reload.  Suppression is cleared in the delayCall below.
                NativeHookManager.SuppressRequestScriptReload = true;
                RepairH3VRCodeMonoScripts("ctor");
                // Clear the suppression flag once EndReloadAssembly's step 10 has completed.
                // step 10 runs after SetupLoadedEditorAssemblies (step 4) returns, i.e. after all
                // [InitializeOnLoad] ctors finish.  The delayCall fires after EndReloadAssembly fully
                // returns — the correct point to re-enable RequestScriptReload.
                EditorApplication.delayCall += delegate
                {
                    NativeHookManager.SuppressRequestScriptReload = false;
                    VerifyH3VRCodeMonoScripts();
                };
            }
            else
            {
                // Hook failed to install (wrong RVA? unsupported version?). Fall back to the
                // safe delayCall path, which avoids the reload loop but leaves the inspector
                // with broken components until the next domain reload.
                Debug.LogWarning("[ManagedPluginDomainFix] RequestScriptReload hook not installed — falling back to delayCall repair (inspector may show broken components)");
                EditorApplication.delayCall += delegate { RepairH3VRCodeMonoScripts("delayCall"); };
            }
        }

        // ── Mono assembly search path setup ────────────────────────────────────────

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void d_mono_set_assemblies_path([MarshalAs(UnmanagedType.LPStr)] string path);

        private static void SetMonoAssemblySearchPaths()
        {
            try
            {
                IntPtr monoModule = GetModuleHandle("mono");
                if (monoModule == IntPtr.Zero) monoModule = GetModuleHandle("mono.dll");
                if (monoModule == IntPtr.Zero)
                {
                    Debug.LogWarning("[ManagedPluginDomainFix] mono module not found");
                    return;
                }

                IntPtr setPathPtr = GetProcAddress(monoModule, "mono_set_assemblies_path");
                if (setPathPtr == IntPtr.Zero)
                {
                    Debug.LogWarning("[ManagedPluginDomainFix] mono_set_assemblies_path not found");
                    return;
                }

                var setPath = (d_mono_set_assemblies_path)Marshal.GetDelegateForFunctionPointer(
                    setPathPtr, typeof(d_mono_set_assemblies_path));

                var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var dirList = new System.Collections.Generic.List<string>();

                System.Action<string> addDir = d =>
                {
                    if (!string.IsNullOrEmpty(d) && Directory.Exists(d) && seen.Add(d)) dirList.Add(d);
                };

                // Restore Unity's three initial Mono search paths.
                string appContents = EditorApplication.applicationContentsPath;
                addDir(Path.Combine(appContents, "Managed"));
                string monoLib = Path.Combine(Path.Combine(appContents, "Mono"), "lib");
                addDir(Path.Combine(Path.Combine(monoLib, "mono"), "2.0"));
                addDir(Path.Combine(appContents, "UnityScript"));

                // Add UnityExtensions directories so UnityEngine.UI and other extension
                // DLLs are resolvable during ProcessInitializeOnLoadAttributes (Sodalite fix).
                if (Directory.Exists(_unityExtensionsDir))
                    foreach (var dll in Directory.GetFiles(_unityExtensionsDir, "*.dll", SearchOption.AllDirectories))
                        addDir(Path.GetDirectoryName(dll));

                // Add Assets/Managed/ so that plugin DLLs are resolvable through Mono's probe path.
                addDir(_managedDir);

                setPath(string.Join(Path.PathSeparator.ToString(), dirList.ToArray()));
                Log("SetMonoAssemblySearchPaths: " + dirList.Count + " directories registered");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ManagedPluginDomainFix] SetMonoAssemblySearchPaths failed: " + ex);
            }
        }

        // ── Extension DLL preloading ───────────────────────────────────────────────

        private static bool IsManagedAssembly(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var br = new BinaryReader(fs))
                {
                    if (fs.Length < 0x40) return false;
                    if (br.ReadUInt16() != 0x5A4D) return false;
                    fs.Seek(0x3C, SeekOrigin.Begin);
                    uint peOffset = br.ReadUInt32();
                    if (peOffset + 4 >= fs.Length) return false;
                    fs.Seek(peOffset, SeekOrigin.Begin);
                    if (br.ReadUInt32() != 0x00004550) return false;
                    fs.Seek(peOffset + 20, SeekOrigin.Begin);
                    ushort optHeaderSize = br.ReadUInt16();
                    fs.Seek(2, SeekOrigin.Current);
                    if (optHeaderSize < 224) return false;
                    long optStart = fs.Position;
                    ushort magic = br.ReadUInt16();
                    long dirOffset = (magic == 0x20B) ? optStart + 112 : optStart + 96;
                    long cliDirOffset = dirOffset + 14 * 8;
                    if (cliDirOffset + 4 > fs.Length) return false;
                    fs.Seek(cliDirOffset, SeekOrigin.Begin);
                    uint cliRva = br.ReadUInt32();
                    return cliRva != 0;
                }
            }
            catch { return false; }
        }

        private static void PreloadUnityExtensionDlls()
        {
            if (!Directory.Exists(_unityExtensionsDir)) return;
            var allDlls = Directory.GetFiles(_unityExtensionsDir, "*.dll", SearchOption.AllDirectories);
            Array.Sort(allDlls, (a, b) => a.Length.CompareTo(b.Length));
            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int loaded = 0, skipped = 0;
            foreach (var dll in allDlls)
            {
                if (!seen.Add(Path.GetFileNameWithoutExtension(dll))) continue;
                if (!IsManagedAssembly(dll)) { skipped++; continue; }
                try { InternalEditorUtility.LoadAssemblyWrapper(Path.GetFileName(dll), dll); loaded++; }
                catch { skipped++; }
            }
            Log("PreloadUnityExtensionDlls: loaded=" + loaded + " skipped=" + skipped);
        }

        // ── Assembly resolution fallback ───────────────────────────────────────────

        private static Assembly ResolveUnityExtensionAssembly(object sender, ResolveEventArgs args)
        {
            var simpleName = new AssemblyName(args.Name).Name;
            Log("AssemblyResolve: " + simpleName);

            if (Directory.Exists(_unityExtensionsDir))
            {
                var matches = Directory.GetFiles(_unityExtensionsDir, simpleName + ".dll", SearchOption.AllDirectories);
                if (matches.Length > 0)
                {
                    Array.Sort(matches, (a, b) => a.Length.CompareTo(b.Length));
                    foreach (var dll in matches)
                    {
                        try
                        {
                            var asm = InternalEditorUtility.LoadAssemblyWrapper(Path.GetFileName(dll), dll);
                            Log("AssemblyResolve resolved " + simpleName + " from " + dll);
                            return asm;
                        }
                        catch { }
                    }
                }
            }

            if (Directory.Exists(_managedDir))
            {
                var candidate = Path.Combine(_managedDir, simpleName + ".dll");
                if (File.Exists(candidate))
                {
                    try
                    {
                        var asm = InternalEditorUtility.LoadAssemblyWrapper(Path.GetFileName(candidate), candidate);
                        Log("AssemblyResolve resolved " + simpleName + " from " + candidate);
                        return asm;
                    }
                    catch { }
                }
            }

            return null;
        }

        // ── DLL copy at domain shutdown ────────────────────────────────────────────

        // Called each reload via BeforeShutdownCallbacks (inside OnShutdownManaged).
        private static void CopyAllToScriptAssemblies()
        {
            if (!Directory.Exists(_managedDir)) return;
            EnsureDir();
            foreach (var name in new[] { MeatKit.AssemblyRename, MeatKit.AssemblyFirstpassRename })
            {
                var src = Path.Combine(_managedDir, name + ".dll");
                if (!File.Exists(src)) continue;
                var dest = Path.Combine(_scriptAssembliesDir, name + ".dll");
                // Skip the copy when destination already matches source byte-for-byte.
                // File.Copy always updates the destination timestamp even for identical bytes;
                // Unity detects the timestamp change as a DLL modification and queues a domain
                // reload -- creating a permanent reload loop on every domain shutdown.
                if (File.Exists(dest) && FileContentsEqual(src, dest))
                    continue;
                try
                {
                    File.Copy(src, dest, true);
                    // Preserve the source mtime so Unity's change detection doesn't see
                    // the copy as a "new" DLL and trigger a recompilation cascade.
                    File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(src));
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("[ManagedPluginDomainFix] Copy failed: " + name + ".dll: " + ex.Message);
                }
            }

            // Belt-and-suspenders: if a TypeTree backup was set (by Build.cs),
            // restore it now.  CopyAllToScriptAssemblies runs inside OnShutdownManaged
            // which fires AFTER Unity's own shutdown serialization, so this write is
            // guaranteed to be the last before the new domain loads.
            try
            {
                if (_pendingShutdownRestore != null && _pendingShutdownRestore.Count > 0)
                {
                    RestoreH3VRCodeTypeTreeCache(_pendingShutdownRestore);
                    _pendingShutdownRestore = null;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[ManagedPluginDomainFix] Shutdown TypeTree restore failed: " + ex.Message);
            }
        }

        private static bool FileContentsEqual(string a, string b)
        {
            try
            {
                var infoA = new FileInfo(a);
                var infoB = new FileInfo(b);
                if (infoA.Length != infoB.Length) return false;
                const int BufSize = 32768;
                var bufA = new byte[BufSize];
                var bufB = new byte[BufSize];
                using (var fa = File.OpenRead(a))
                using (var fb = File.OpenRead(b))
                {
                    int readA;
                    while ((readA = fa.Read(bufA, 0, BufSize)) > 0)
                    {
                        int readB = fb.Read(bufB, 0, readA);
                        if (readB != readA) return false;
                        for (int i = 0; i < readA; i++)
                            if (bufA[i] != bufB[i]) return false;
                    }
                }
                return true;
            }
            catch { return false; }
        }

        // Called on first [InitializeOnLoad]; skips files already present.
        private static void CopyMissingToScriptAssemblies()
        {
            if (!Directory.Exists(_managedDir)) return;
            EnsureDir();
            foreach (var name in new[] { MeatKit.AssemblyRename, MeatKit.AssemblyFirstpassRename })
            {
                var src = Path.Combine(_managedDir, name + ".dll");
                if (!File.Exists(src)) continue;
                var dest = Path.Combine(_scriptAssembliesDir, name + ".dll");
                if (!File.Exists(dest)) try { File.Copy(src, dest); } catch { }
            }
        }

        // ── TypeTree metadata backup / restore ─────────────────────────────────────
        // BuildAssetBundles corrupts Library/metadata for H3VRCode DLLs (standalone
        // compile changes the MVID, invalidating cached TypeTree hashes).  We backup
        // the metadata bytes before the build and restore them after, so the post-build
        // domain reload sees correct TypeTree data and doesn't produce null MonoScripts.

        /// <summary>
        /// Permanent BeforeEATI callback: ensures H3VRCode DLLs from Assets/Managed/ are
        /// present in Library/ScriptAssemblies/ before every EATI invocation (build or not).
        /// This is the primary defence against post-build recompilation writing a stale
        /// standalone MVID to Library/metadata and producing null MonoScripts after builds.
        /// Uses content equality to skip the copy when the files already match so Unity's
        /// file-change detector does not trigger spurious domain reloads.
        /// </summary>
        private static void EnsureH3VRCodeInScriptAssemblies()
        {
            if (!Directory.Exists(_managedDir)) return;
            EnsureDir();
            foreach (var name in new[] { MeatKit.AssemblyRename, MeatKit.AssemblyFirstpassRename })
            {
                var src  = Path.Combine(_managedDir, name + ".dll");
                var dest = Path.Combine(_scriptAssembliesDir, name + ".dll");
                if (!File.Exists(src)) continue;
                // Skip copy when content already matches to avoid spurious domain reloads.
                if (File.Exists(dest) && FileContentsEqual(src, dest)) continue;
                try
                {
                    File.Copy(src, dest, true);
                    File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(src));
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[ManagedPluginDomainFix] EnsureH3VRCodeInScriptAssemblies copy failed: " + ex.Message);
                }
            }
        }

        // ── Pending DLL import (sharing-violation recovery) ──────────────────────────
        //
        // When ImportAssemblies tries to overwrite H3VRCode-CSharp.dll while Unity's
        // child domain holds it memory-mapped, Mono.Cecil throws IOException.  The fix:
        //   1. Cecil writes to Library/PendingDllImports/{name}.dll.
        //   2. StageForPendingImport() temporarily disables the plugin in .meta.
        //   3. On the next [InitializeOnLoad], ApplyPendingDllImports() copies the staged
        //      file over the original (now unlocked) and re-enables the plugin.
        //   4. A deferred AssetDatabase.Refresh() reloads the updated DLL.

        /// <summary>Called by AssemblyImporter when a direct Write failed with IOException.</summary>
        internal static void StageForPendingImport(string pendingPath, string destPath)
        {
            string metaPath = destPath + ".meta";
            string existingMeta = File.Exists(metaPath) ? File.ReadAllText(metaPath) : "";
            string guid = ExtractGuidFromMeta(existingMeta);

            // Disable the plugin so domain reload doesn't re-lock the file.
            File.WriteAllText(metaPath, BuildPluginMeta(guid, anyEnabled: false));

            // Record the pending operation.
            string pendingDir = Path.GetDirectoryName(pendingPath);
            if (!Directory.Exists(pendingDir)) Directory.CreateDirectory(pendingDir);
            File.AppendAllText(_pendingManifestPath, pendingPath + "|" + destPath + "\n");
        }

        private static void ApplyPendingDllImports()
        {
            if (string.IsNullOrEmpty(_pendingManifestPath) || !File.Exists(_pendingManifestPath))
                return;

            string[] lines = File.ReadAllLines(_pendingManifestPath);
            // NOTE: do NOT delete manifest here. Only delete after successful copy.

            bool anyApplied = false;
            var remaining = new System.Collections.Generic.List<string>();
            var seenEntries = new System.Collections.Generic.HashSet<string>();
            foreach (var raw in lines)
            {
                string line = raw.Trim();
                if (string.IsNullOrEmpty(line)) continue;
                if (!seenEntries.Add(line)) continue; // skip duplicate manifest entries
                int sep = line.IndexOf('|');
                if (sep < 0) continue;
                string pendingPath = line.Substring(0, sep);
                string destPath    = line.Substring(sep + 1);
                string metaPath    = destPath + ".meta";

                if (!File.Exists(pendingPath))
                {
                    // Staged file is gone but meta may still be disabled — re-enable it.
                    if (File.Exists(metaPath))
                    {
                        string g = ExtractGuidFromMeta(File.ReadAllText(metaPath));
                        File.WriteAllText(metaPath, BuildPluginMeta(g, anyEnabled: true));
                    }
                    continue;
                }
                try
                {
                    File.Copy(pendingPath, destPath, true);
                    File.Delete(pendingPath);

                    // Re-enable the plugin now that the file is replaced.
                    if (File.Exists(metaPath))
                    {
                        string guid = ExtractGuidFromMeta(File.ReadAllText(metaPath));
                        File.WriteAllText(metaPath, BuildPluginMeta(guid, anyEnabled: true));
                    }
                    anyApplied = true;
                }
                catch (IOException)
                {
                    // File still locked by current domain.
                    // DO NOT re-enable the meta here: it must stay Any:enabled=0 so the next
                    // domain reload does NOT load H3VRCode again.  If we re-enable now, the next
                    // domain locks the DLL immediately and the pending copy can never succeed.
                    // The ACSC hook and mcs.rsp -r: entries ensure compilation still works even
                    // when Any:enabled=0.
                    remaining.Add(line);
                    Debug.LogWarning("[ManagedPluginDomainFix] DLL still locked, pending copy deferred to next domain: " +
                        Path.GetFileName(destPath));
                }
                catch (Exception ex)
                {
                    // Unknown error — re-enable meta so compile works, drop the entry.
                    if (File.Exists(metaPath))
                    {
                        string guid = ExtractGuidFromMeta(File.ReadAllText(metaPath));
                        File.WriteAllText(metaPath, BuildPluginMeta(guid, anyEnabled: true));
                    }
                    Debug.LogWarning("[ManagedPluginDomainFix] Failed to apply pending DLL " +
                        Path.GetFileName(destPath) + ": " + ex.Message);
                }
            }

            // Rewrite manifest with only the entries that couldn't be applied yet.
            if (remaining.Count > 0)
                File.WriteAllLines(_pendingManifestPath, remaining.ToArray());
            else
                File.Delete(_pendingManifestPath);

            if (anyApplied)
                EditorApplication.delayCall += delegate { AssetDatabase.Refresh(); };
        }

        private static string ExtractGuidFromMeta(string metaContent)
        {
            foreach (var raw in metaContent.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("guid: "))
                    return line.Substring(6).Trim();
            }
            return Guid.NewGuid().ToString("N");
        }

        private static string BuildPluginMeta(string guid, bool anyEnabled)
        {
            return "fileFormatVersion: 2\nguid: " + guid + "\nPluginImporter:\n" +
                   "  serializedVersion: 2\n  iconMap: {}\n  executionOrder: {}\n" +
                   "  isPreloaded: 0\n  isOverridable: 0\n  platformData:\n" +
                   "    data:\n      first:\n        Any:\n      second:\n" +
                   "        enabled: " + (anyEnabled ? "1" : "0") + "\n        settings: {}\n" +
                   "    data:\n      first:\n        Editor: Editor\n      second:\n" +
                   "        enabled: 0\n        settings:\n          DefaultValueInitialized: true\n" +
                   "    data:\n      first:\n        Windows Store Apps: WindowsStoreApps\n      second:\n" +
                   "        enabled: 0\n        settings:\n          CPU: AnyCPU\n" +
                   "  userData:\n  assetBundleName:\n  assetBundleVariant:";
        }

        /// <summary>
        /// Saves a byte-for-byte copy of the Library/metadata files for both H3VRCode DLLs
        /// (main index file, .info sidecar, and .resource sidecar if present).
        /// Returns a dictionary mapping file path → saved bytes.
        /// </summary>
        internal static Dictionary<string, byte[]> BackupH3VRCodeTypeTreeCache()
        {
            var backups = new Dictionary<string, byte[]>();
            try
            {
                string[] relPaths = new[]
                {
                    "Assets/Managed/" + MeatKit.AssemblyRename + ".dll",
                    "Assets/Managed/" + MeatKit.AssemblyFirstpassRename + ".dll"
                };
                string metaRoot = Path.GetFullPath(
                    Path.Combine(Path.Combine(Application.dataPath, ".."),
                    Path.Combine("Library", "metadata")));
                foreach (string relPath in relPaths)
                {
                    string guid = AssetDatabase.AssetPathToGUID(relPath);
                    if (string.IsNullOrEmpty(guid)) continue;
                    string baseFile = Path.Combine(Path.Combine(metaRoot, guid.Substring(0, 2)), guid);
                    // Backup the main metadata file
                    if (File.Exists(baseFile))
                    {
                        backups[baseFile] = File.ReadAllBytes(baseFile);
                        BuildLog.WriteLine("BackupTypeTree: saved " + baseFile + " (" + backups[baseFile].Length + " bytes)");
                    }
                    // Backup the .info sidecar (contains serialized TypeTree for all MonoScripts in the DLL)
                    string infoFile = baseFile + ".info";
                    if (File.Exists(infoFile))
                    {
                        backups[infoFile] = File.ReadAllBytes(infoFile);
                        BuildLog.WriteLine("BackupTypeTree: saved " + infoFile + " (" + backups[infoFile].Length + " bytes)");
                    }
                    // Backup the .resource sidecar if present (rare for DLL assets)
                    string resourceFile = baseFile + ".resource";
                    if (File.Exists(resourceFile))
                    {
                        backups[resourceFile] = File.ReadAllBytes(resourceFile);
                        BuildLog.WriteLine("BackupTypeTree: saved " + resourceFile + " (" + backups[resourceFile].Length + " bytes)");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ManagedPluginDomainFix] BackupH3VRCodeTypeTreeCache failed: " + ex.Message);
            }
            return backups;
        }

        /// <summary>
        /// Restores Library/metadata files from backup bytes.  Called after
        /// BuildAssetBundles to undo TypeTree corruption.
        /// </summary>
        internal static void RestoreH3VRCodeTypeTreeCache(Dictionary<string, byte[]> backups)
        {
            if (backups == null || backups.Count == 0) return;
            foreach (var kvp in backups)
            {
                try
                {
                    File.WriteAllBytes(kvp.Key, kvp.Value);
                    BuildLog.WriteLine("RestoreTypeTree: restored " + kvp.Key);
                }
                catch (Exception ex)
                {
                    BuildLog.WriteLine("WARNING: RestoreTypeTree failed for " + kvp.Key + ": " + ex.Message);
                }
            }
        }

        /// <summary>
        /// TypeTree backup held until CopyAllToScriptAssemblies runs during OnShutdownManaged.
        /// Writing during shutdown ensures it is the last write before the new domain loads.
        /// </summary>
        internal static Dictionary<string, byte[]> _pendingShutdownRestore;

        // ── Native MonoScript class repair ───────────────────────────────────────────────────────

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void d_MonoScriptRebuildFromAwake(IntPtr thisPtr);

        // RVA of MonoScript::RebuildFromAwake in Unity.exe 5.6.7f1 x64.
        private const long RebuildFromAwakeRva = 0xe369d0L;

        // caller: "ctor" (direct ctor call) or "delayCall" (backup, fires after PIOLA).
        private static void RepairH3VRCodeMonoScripts(string caller)
        {
            try
            {
                if (!EditorVersion.IsSupportedVersion) return;

                string h3vr   = MeatKit.AssemblyRename + ".dll";
                string h3vrFp = MeatKit.AssemblyFirstpassRename + ".dll";

                // First pass: count nulls.
                int total = 0, broken = 0;
                foreach (MonoScript s in MonoImporter.GetAllRuntimeMonoScripts())
                {
                    if ((UnityEngine.Object)s == null) continue;
                    string f = Path.GetFileName(AssetDatabase.GetAssetPath(s));
                    if (f != h3vr && f != h3vrFp) continue;
                    total++;
                    if (s.GetClass() == null) broken++;
                }

                if (broken == 0)
                {
                    Log("RepairH3VRCodeMonoScripts(" + caller + "): all " + total + " scripts healthy");
                    return;
                }

                Debug.LogWarning(string.Format(
                    "[ManagedPluginDomainFix] RepairH3VRCodeMonoScripts({2}): {0}/{1} H3VRCode scripts null — calling RebuildFromAwake",
                    broken, total, caller));

                IntPtr unityBase = GetModuleHandle("Unity.exe");
                if (unityBase == IntPtr.Zero) unityBase = GetModuleHandle("Unity");
                if (unityBase == IntPtr.Zero)
                {
                    Debug.LogWarning("[ManagedPluginDomainFix] RepairH3VRCodeMonoScripts: Unity.exe module not found");
                    return;
                }

                IntPtr fnPtr = new IntPtr(unityBase.ToInt64() + RebuildFromAwakeRva);
                var rebuildFromAwake = (d_MonoScriptRebuildFromAwake)Marshal.GetDelegateForFunctionPointer(
                    fnPtr, typeof(d_MonoScriptRebuildFromAwake));

                FieldInfo cachedPtrField = typeof(UnityEngine.Object).GetField(
                    "m_CachedPtr", BindingFlags.NonPublic | BindingFlags.Instance);
                if (cachedPtrField == null) return;

                int repaired = 0, skipped = 0;
                foreach (MonoScript s in MonoImporter.GetAllRuntimeMonoScripts())
                {
                    if ((UnityEngine.Object)s == null) continue;
                    string f = Path.GetFileName(AssetDatabase.GetAssetPath(s));
                    if (f != h3vr && f != h3vrFp) continue;
                    if (s.GetClass() != null) continue;

                    IntPtr nativePtr = (IntPtr)cachedPtrField.GetValue(s);
                    if (nativePtr == IntPtr.Zero) { skipped++; continue; }

                    rebuildFromAwake(nativePtr);
                    repaired++;
                }

                // Verification pass.
                int stillBroken = 0;
                foreach (MonoScript s in MonoImporter.GetAllRuntimeMonoScripts())
                {
                    if ((UnityEngine.Object)s == null) continue;
                    string f = Path.GetFileName(AssetDatabase.GetAssetPath(s));
                    if (f != h3vr && f != h3vrFp) continue;
                    if (s.GetClass() == null) stillBroken++;
                }

                Log(string.Format(
                    "RepairH3VRCodeMonoScripts({3}): repaired={0} skipped={1} stillBroken={2}",
                    repaired, skipped, stillBroken, caller));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ManagedPluginDomainFix] RepairH3VRCodeMonoScripts(" + caller + "): " + ex);
            }
        }

        // ── Post-repair health check ────────────────────────────────────────────────

        /// <summary>
        /// Logs the health of H3VRCode MonoScripts after the ctor repair + step 10.
        /// Called from the delayCall (fires after EndReloadAssembly completes).
        /// Does NOT attempt to repair — if any scripts are still broken here, something
        /// unexpected happened in step 10 that needs investigation.
        /// </summary>
        private static void VerifyH3VRCodeMonoScripts()
        {
            try
            {
                if (!EditorVersion.IsSupportedVersion) return;
                string h3vr   = MeatKit.AssemblyRename + ".dll";
                string h3vrFp = MeatKit.AssemblyFirstpassRename + ".dll";
                int total = 0, broken = 0;
                foreach (MonoScript s in MonoImporter.GetAllRuntimeMonoScripts())
                {
                    if ((UnityEngine.Object)s == null) continue;
                    string f = Path.GetFileName(AssetDatabase.GetAssetPath(s));
                    if (f != h3vr && f != h3vrFp) continue;
                    total++;
                    if (s.GetClass() == null) broken++;
                }
                if (broken > 0)
                    Debug.LogWarning(string.Format(
                        "[ManagedPluginDomainFix] VerifyH3VRCodeMonoScripts: {0}/{1} scripts STILL null after ctor repair",
                        broken, total));
                else
                    Log("VerifyH3VRCodeMonoScripts: all " + total + " scripts healthy");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ManagedPluginDomainFix] VerifyH3VRCodeMonoScripts: " + ex);
            }
        }
        private static void EnsureDir()
        {
            if (!Directory.Exists(_scriptAssembliesDir))
                Directory.CreateDirectory(_scriptAssembliesDir);
        }

        private static void Log(string msg)
        {
#pragma warning disable 0162
            if (DebugLogging) Debug.Log("[ManagedPluginDomainFix] " + msg);
#pragma warning restore 0162
        }
    }
}
