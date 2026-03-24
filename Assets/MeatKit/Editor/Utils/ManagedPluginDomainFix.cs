using System;
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
    //    Fix: NativeHookManager.BeforeShutdownCallbacks copies them back at step 2
    //    (after compile, before new-domain assembly loading at step 5).
    //
    // 2. Sodalite / UnityEngine.UI TypeLoadException: plugin DLLs are scanned by
    //    ProcessInitializeOnLoadAttributes before any [InitializeOnLoad] code runs.
    //    Sodalite.GetTypes() fails to resolve UnityEngine.UI because Unity's extension
    //    DLLs aren't in any standard Mono probe path at that point.
    //    Fix: mono_set_assemblies_path (exported from mono.dll) adds all UnityExtensions
    //    subdirectories to Mono's global (process-wide) assembly search path. mono_set_assemblies_path
    //    is process-wide and persists across domain reloads, so one call at startup is sufficient.
    [InitializeOnLoad]
    static class ManagedPluginDomainFix
    {
        // Set to true to enable verbose console logging from this class.
        private const bool DebugLogging = false;

        private static readonly string _managedDir;
        private static readonly string _scriptAssembliesDir;
        private static readonly string _unityExtensionsDir;

        static ManagedPluginDomainFix()
        {
            _managedDir = Path.Combine(Application.dataPath, "Managed");
            _scriptAssembliesDir = Path.GetFullPath(
                Path.Combine(Path.Combine(Path.Combine(Application.dataPath, ".."), "Library"), "ScriptAssemblies"));
            _unityExtensionsDir = Path.Combine(EditorApplication.applicationContentsPath, "UnityExtensions");

            // NOTE: no crash-recovery safety net is needed here. Build.cs uses
            // AssetDatabase.StartAssetEditing() / StopAssetEditing() to pause the file watcher,
            // which is an in-memory runtime state only — it is NOT persisted to the registry or
            // disk. If Unity crashes while StartAssetEditing() is active the state is simply
            // gone on the next startup and Unity opens normally.

            SetMonoAssemblySearchPaths();
            PreloadUnityExtensionDlls();
            AppDomain.CurrentDomain.AssemblyResolve += ResolveUnityExtensionAssembly;

            CopyMissingToScriptAssemblies();

            // Repair is deferred to the next editor frame so it runs AFTER Unity's initial
            // asset scan and ProcessInitializeOnLoadAttributes have settled. Calling
            // RepairH3VRCodeMonoScripts() synchronously in the static constructor fires before
            // Unity has finished its startup scan; the Init() call can then interfere with
            // Unity's pending reimport queue and trigger a spurious compilation cycle.
            EditorApplication.delayCall += RepairH3VRCodeMonoScripts;

            // SetMonoAssemblySearchPaths is intentionally NOT re-registered here.
            // mono_set_assemblies_path is a process-wide native Mono function; its effect
            // persists across domain reloads, so the one call above at startup is sufficient.
            NativeHookManager.BeforeShutdownCallbacks.Add(CopyAllToScriptAssemblies);
        }

        // P/Invoke into mono.dll to call mono_set_assemblies_path, which sets Mono's
        // global (process-wide) assembly search path. Persists across domain reloads.
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

                // Restore Unity's three initial Mono search paths. Our mono_set_assemblies_path
                // call REPLACES whatever Unity set, so we must re-add these explicitly or:
                //   path[0] Data/Managed   — Mono.Cecil 0.9.66 (needed by Unity.SerializationLogic)
                //                           would be resolved instead from lib/mono/unity, which
                //                           has an older build missing CustomAttributeArgument,
                //                           causing TypeLoadException and broken VS-sync menus.
                //   path[1] lib/mono/2.0   — System.Runtime.Serialization and other BCL assemblies
                //                           that child AppDomains (UNetWeaver, ExtractAssemblyTypeInfo)
                //                           need but cannot find without an explicit search path.
                //   path[2] UnityScript    — UnityScript language runtime assemblies.
                //
                // lib/mono/unity is intentionally excluded: it contains an older Cecil build
                // that conflicts when loaded alongside Data/Managed/Mono.Cecil.dll, producing
                // TypeLoadException. System.Runtime.Serialization is in lib/mono/2.0 already.
                string appContents = EditorApplication.applicationContentsPath;
                addDir(Path.Combine(appContents, "Managed"));                       // path[0]
                string monoLib = Path.Combine(Path.Combine(appContents, "Mono"), "lib");
                addDir(Path.Combine(Path.Combine(monoLib, "mono"), "2.0"));         // path[1]
                addDir(Path.Combine(appContents, "UnityScript"));                   // path[2]

                // Add UnityExtensions directories so UnityEngine.UI and other extension
                // DLLs are resolvable during ProcessInitializeOnLoadAttributes (Sodalite fix).
                if (Directory.Exists(_unityExtensionsDir))
                    foreach (var dll in Directory.GetFiles(_unityExtensionsDir, "*.dll", SearchOption.AllDirectories))
                        addDir(Path.GetDirectoryName(dll));

                // Add Assets/Managed/ so that plugin DLLs (H3VRCode-CSharp, OtherLoader,
                // Sodalite, etc.) are resolvable through Mono's probe path after every
                // domain reload — including post-build reloads where Unity's internal
                // assembly scanner may not have registered them yet.  Data/Managed/ is
                // listed first (above) so its Mono.Cecil.dll takes priority over the
                // project-local copy.
                addDir(_managedDir);

                setPath(string.Join(Path.PathSeparator.ToString(), dirList.ToArray()));
                Log("SetMonoAssemblySearchPaths: " + dirList.Count + " directories registered");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ManagedPluginDomainFix] SetMonoAssemblySearchPaths failed: " + ex);
            }
        }

        // Checks the PE CLI header to confirm a DLL is managed before attempting to load it.
        // Prevents CS0009 errors from native DLLs (e.g. OVRPlugin, openvr_api) being fed
        // to the compiler as assembly references.
        private static bool IsManagedAssembly(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var br = new BinaryReader(fs))
                {
                    if (fs.Length < 0x40) return false;
                    if (br.ReadUInt16() != 0x5A4D) return false; // MZ
                    fs.Seek(0x3C, SeekOrigin.Begin);
                    uint peOffset = br.ReadUInt32();
                    if (peOffset + 4 >= fs.Length) return false;
                    fs.Seek(peOffset, SeekOrigin.Begin);
                    if (br.ReadUInt32() != 0x00004550) return false; // PE\0\0
                    // COFF header: machine(2) sections(2) timestamp(4) sym(4) numsym(4) opthdr(2) chars(2)
                    fs.Seek(peOffset + 20, SeekOrigin.Begin);
                    ushort optHeaderSize = br.ReadUInt16();
                    fs.Seek(2, SeekOrigin.Current); // skip characteristics
                    if (optHeaderSize < 224) return false; // need data directories
                    long optStart = fs.Position;
                    ushort magic = br.ReadUInt16();
                    // CLI header is at data directory index 14 (0-based)
                    // For PE32:  directories start at opt+96  (16 entries * 8 bytes)
                    // For PE32+: directories start at opt+112
                    long dirOffset = (magic == 0x20B) ? optStart + 112 : optStart + 96;
                    long cliDirOffset = dirOffset + 14 * 8; // entry 14 = COM descriptor (CLI)
                    if (cliDirOffset + 4 > fs.Length) return false;
                    fs.Seek(cliDirOffset, SeekOrigin.Begin);
                    uint cliRva = br.ReadUInt32();
                    return cliRva != 0;
                }
            }
            catch { return false; }
        }

        // Belt-and-suspenders: eagerly loads managed extension DLLs into the domain.
        // Skips native DLLs via IsManagedAssembly. Prefers the shortest path (root variant)
        // for each assembly name to avoid loading standalone/editor-specific variants.
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

        // Last-resort fallback: fires when Mono cannot resolve a reference through any probe path.
        // Searches UnityExtensions/ and Assets/Managed/ by name.
        private static Assembly ResolveUnityExtensionAssembly(object sender, ResolveEventArgs args)
        {
            var simpleName = new AssemblyName(args.Name).Name;
            Log("AssemblyResolve: " + simpleName);

            // Try UnityExtensions first (preferring the shallowest path).
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

            // Try Assets/Managed/ as a fallback for plugin DLLs.
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

        // Called each reload via BeforeShutdownCallbacks; overwrites to ensure fresh copies.
        private static void CopyAllToScriptAssemblies()
        {
            if (!Directory.Exists(_managedDir)) return;
            EnsureDir();
            foreach (var name in new[] { MeatKit.AssemblyRename, MeatKit.AssemblyFirstpassRename })
            {
                var src = Path.Combine(_managedDir, name + ".dll");
                if (!File.Exists(src)) continue;
                try { File.Copy(src, Path.Combine(_scriptAssembliesDir, name + ".dll"), true); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("[ManagedPluginDomainFix] Copy failed: " + name + ".dll: " + ex.Message);
                }
            }
        }

        // Called on first [InitializeOnLoad]; skips files already present to avoid
        // overwriting DLLs that may be locked by the running domain.
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

        // Calls MonoScript.Init() (internal native method) ONLY on H3VRCode-CSharp MonoScripts
        // whose GetClass() returns null, i.e. whose native m_Script (MonoClass*) pointer is
        // truly broken after a domain reload triggered by the build.
        //
        // IMPORTANT: Do NOT call Init() on already-valid MonoScripts. Even though GrabScript
        // writes the same value, calling Init() via reflection marks the UnityEngine.Object as
        // modified.  Unity's asset-pipeline then queues a reimport for those .dll assets, which
        // triggers a script compilation cycle.  When that cycle fires during Build 2's
        // BuildAssetBundles standalone-compile step it cancels the build.  We therefore guard
        // every Init() call with a GetClass() != null check so we only touch actually-broken
        // MonoScripts.

        /// <summary>
        /// Called from Build.cs immediately after BuildAssetBundles to repair any H3VRCode
        /// MonoScript m_Script pointers that the Transfer detour may have nulled in this domain,
        /// without waiting for a domain reload.
        /// </summary>
        public static void RepairNow()
        {
            RepairH3VRCodeMonoScripts();
        }

        // Tracks whether PrimeH3VRCodeMonoScriptTypeTrees() has already primed in this domain.
        // Reset automatically on every domain reload (static field lifetime = domain lifetime).
        private static bool _typeTreePrimed = false;

        /// <summary>
        /// Call BEFORE BuildAssetBundles to refresh the per-GUID TypeTree cache for all
        /// H3VRCode-CSharp MonoScripts. The AssetBundleIO Transfer hook temporarily renames
        /// the native assemblyName to "Assembly-CSharp.dll" before calling
        /// OrigMonoScriptTransferWrite; if the per-GUID cache is stale OrigMonoScriptTransferWrite
        /// falls back to getClass("Assembly-CSharp.dll", "FistVR", "FVRObject") which finds the
        /// mod-scripts assembly (not the game assembly) and writes an empty TypeTree, causing
        /// items to not spawn. Init() refreshes the cache from the live class before the build.
        /// The _typeTreePrimed flag limits the post-build reimport compile chain to once per domain.
        /// </summary>
        public static void PrimeTypeTreesForBuild()
        {
            if (_typeTreePrimed) return;
            try
            {
                var initMethod = typeof(MonoScript).GetMethod("Init",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var getNsMethod = typeof(MonoScript).GetMethod("GetNamespace",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (initMethod == null || getNsMethod == null) return;
                string h3vr = MeatKit.AssemblyRename + ".dll";
                string h3vrFp = MeatKit.AssemblyFirstpassRename + ".dll";
                int primed = 0;
                // StartAssetEditing pauses the file-system watcher. After StopAssetEditing we
                // call AssetDatabase.SaveAssets() to write the dirty MonoScript sub-assets to
                // the Library cache and clear their dirty flags.  Without this, Unity serialises
                // the dirty flag on shutdown and on the next boot passes the DLL as a source
                // file to the C# compiler, crashing with:
                //   "Unable to find a suitable compiler for sources with extension 'dll'"
                // Note: EditorUtility.ClearDirty() does not exist in Unity 5.6 (added 2017.1).
                AssetDatabase.StartAssetEditing();
                try
                {
                    foreach (var script in MonoImporter.GetAllRuntimeMonoScripts())
                    {
                        if ((UnityEngine.Object)script == null) continue;
                        string assetPath = AssetDatabase.GetAssetPath(script);
                        if (string.IsNullOrEmpty(assetPath)) continue;
                        string file = System.IO.Path.GetFileName(assetPath);
                        if (file != h3vr && file != h3vrFp) continue;
                        string ns = (string)getNsMethod.Invoke(script, null);
                        initMethod.Invoke(script, new object[] { "", script.name, ns, file, false });
                        primed++;
                    }
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                }
                // Persist the primed TypeTree state and clear dirty flags before shutdown.
                AssetDatabase.SaveAssets();
                Log("PrimeTypeTreesForBuild: primed=" + primed);
                _typeTreePrimed = true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ManagedPluginDomainFix] PrimeTypeTreesForBuild failed: " + ex);
            }
        }

        private static void RepairH3VRCodeMonoScripts()
        {
            try
            {
                var initMethod = typeof(MonoScript).GetMethod("Init",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var getNsMethod = typeof(MonoScript).GetMethod("GetNamespace",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (initMethod == null || getNsMethod == null)
                {
                    Log("RepairH3VRCodeMonoScripts: MonoScript.Init/GetNamespace not found via reflection");
                    return;
                }
                int repaired = 0, skipped = 0;
                string h3vr = MeatKit.AssemblyRename + ".dll";
                string h3vrFp = MeatKit.AssemblyFirstpassRename + ".dll";
                foreach (var script in MonoImporter.GetAllRuntimeMonoScripts())
                {
                    if ((UnityEngine.Object)script == null) continue;
                    string assetPath = AssetDatabase.GetAssetPath(script);
                    if (string.IsNullOrEmpty(assetPath)) continue;
                    string file = System.IO.Path.GetFileName(assetPath);
                    if (file != h3vr && file != h3vrFp) continue;

                    // Only call Init() when the MonoScript is actually broken (GetClass returns
                    // null). Calling Init() on a valid script marks it as modified and queues a
                    // reimport that interferes with subsequent BuildAssetBundles calls.
                    if (script.GetClass() != null) { skipped++; continue; }

                    string ns = (string)getNsMethod.Invoke(script, null);
                    initMethod.Invoke(script, new object[] { "", script.name, ns, file, false });
                    repaired++;
                }
                Log("RepairH3VRCodeMonoScripts: repaired=" + repaired + " skipped(already-valid)=" + skipped);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ManagedPluginDomainFix] RepairH3VRCodeMonoScripts failed: " + ex);
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
