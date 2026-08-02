using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using MonoMod.Utils;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace MeatKit
{
    // Copies H3VRCode DLLs to ScriptAssemblies/ on domain shutdown, widens Mono's probe path
    // to include UnityExtensions (fixes Sodalite/UnityEngine.UI), and injects H3VRCode's
    // MonoImage into MonoManager for correct TypeTree/CSD generation.
    [InitializeOnLoad]
    static class ManagedPluginDomainFix
    {
        private const bool DebugLogging = false;

        private static void Log(string msg) { if (DebugLogging) UnityEngine.Debug.Log("[ManagedPluginDomainFix] " + msg); }

        private static readonly string _managedDir;
        private static readonly string _scriptAssembliesDir;
        private static readonly string _unityExtensionsDir;
        private static readonly string _pendingManifestPath;

        // Cached module handles — resolved lazily, valid for process lifetime.
        private static IntPtr _monoModule;
        private static IntPtr GetMonoModule()
        {
            if (_monoModule == IntPtr.Zero)
            {
                _monoModule = GetModuleHandle("mono");
                if (_monoModule == IntPtr.Zero) _monoModule = GetModuleHandle("mono.dll");
            }
            return _monoModule;
        }

        static ManagedPluginDomainFix()
        {
            _managedDir = Path.Combine(Application.dataPath, "Managed");
            string libraryDir = Path.GetFullPath(Path.Combine(Path.Combine(Application.dataPath, ".."), "Library"));
            _scriptAssembliesDir = Path.Combine(libraryDir, "ScriptAssemblies");
            _unityExtensionsDir = Path.Combine(EditorApplication.applicationContentsPath, "UnityExtensions");
            _pendingManifestPath = Path.Combine(Path.Combine(libraryDir, "PendingDllImports"), "manifest.txt");

            ApplyPendingDllImports();
            SetMonoAssemblySearchPaths();
            PreloadUnityExtensionDlls();
            AppDomain.CurrentDomain.AssemblyResolve += ResolveUnityExtensionAssembly;
            CopyH3VRCodeDlls(true);
            NativeHookManager.BeforeShutdownCallbacks.Add(delegate { CopyH3VRCodeDlls(false); });
            InstallDomainFixHooks();

            EditorApplication.delayCall += OnDomainLoad;
        }

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void d_mono_set_assemblies_path([MarshalAs(UnmanagedType.LPStr)] string path);

        // GetMonoManagerPtr() -> MonoManager* — RVA 0x14C2510.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr d_GetMonoManagerPtr();

        // MonoManager::RenewMonoScriptsFromAssemblies(MonoManager*, int* mbInstanceIDs, int mbCount)
        // — RVA 0x14C6910. EndReloadAssembly step 5 renewal; re-run at OnDomainLoad once H3VRCode is
        // loaded. mbList is a 32-byte buffer whose [0x18] qword is the MB count (0 = no MB rebuild).
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate void d_RenewMonoScriptsFromAssemblies(IntPtr monoManager, IntPtr mbList);

        // MonoScript::GetClass(MonoScript*, ScriptingClassPtr* resultOut). Returns the class ptr
        // from MonoScriptCache+8 (or NULL when ClassNotFound). Guarded to return 0 for garbage classes.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr d_GetClass(IntPtr monoScript, IntPtr resultOut);

        private static d_GetClass _origGetClass;

        /// <summary>True when the MonoScript::GetClass hook is installed.</summary>
        internal static bool GetClassHookInstalled { get { return _origGetClass != null; } }

        // MonoBehaviour::GetClass(MonoBehaviour*, ScriptingClassPtr* resultOut) — RVA 0x14BC7B0.
        // Returns *(MB+160 cache)+8. Guarded to return 0 when that class is a stale/freed old-domain
        // pointer, which would crash reload step 7 and the Inspector.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr d_MonoBehaviourGetClass(IntPtr thisMB, IntPtr resultOut);

        private static d_MonoBehaviourGetClass _origMonoBehaviourGetClass;

        // MonoBehaviour::CallMethodInactive(MonoBehaviour*, ScriptingMethodPtr*) -> bool — RVA 0x14BC9E0.
        // Reload step 8 fires it per MB. Guarded to return true (skip invoke) when MB+160's class is
        // a stale/freed old-domain pointer.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate byte d_CallMethodInactive(IntPtr thisMB, IntPtr methodPtr);

        private static d_CallMethodInactive _origCallMethodInactive;

        // SerializedProperty.objectReferenceValue icall — RVA 0x1386D30. Returns the field-value
        // wrapper; guarded to return NULL for stale wrappers (see OnObjectReferenceValue).
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr d_ObjectReferenceValue(IntPtr prop);
        private static d_ObjectReferenceValue _origObjectReferenceValue;

        // BuildSerializationCacheFor(ScriptingClassPtr, bool*) -> CachedSerializationData* — RVA 0xE4BD30.
        // Lazy-builds the serialization command queue for a class; guarded against stale/garbage classes.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr d_BuildSerializationCacheFor(IntPtr classPtr, IntPtr createFlags);

        private static d_BuildSerializationCacheFor _origBuildSerializationCacheFor;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void MonoScriptRenewDelegate(long pMonoScript, long classPtr);

        private static MonoScriptRenewDelegate _origMonoScriptRenew;

        // MonoManager::GetMonoClassWithAssemblyName(MonoManager*, className*, namespace*, assemblyName*)
        // -> MonoClass* — RVA 0x14C32E0. Returns 0 for H3VRCode during EndReloadAssembly step 5 (H3VRCode
        // isn't in MonoManager's image table yet). Fallback resolves the class from the H3VRCode image so
        // step 5's MonoScript_Renew gets a real cache (see OnGetMonoClassWithAssemblyName).
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate long d_GetMonoClassWithAssemblyName(IntPtr monoManager, IntPtr classNameStr, IntPtr namespaceStr, IntPtr assemblyNameStr);

        private static d_GetMonoClassWithAssemblyName _origGetMonoClassWithAssemblyName;

        // Native MonoScript / MonoScriptCache offsets (Unity 5.6.7f1 x64, IDA-verified).
        private const int MonoScriptCacheOffset = 216; // MonoScript::m_ScriptCache
        private const int CacheClassOffset      = 8;   // MonoScriptCache::m_pClass

        // ReprimeMBCachesBeforeEATI / ReprimeSilentAfterEATI (restored from MeatKit-main) are
        // deliberately no-ops: their reflection (FindObjectsOfTypeAll + GetValue) hard-faults on the
        // post-build corrupt MonoScript state. Cache repair is handled by RepairBrokenMonoScriptClasses.
        internal static void ReprimeMBCachesBeforeEATI()
        {
            return;
        }

        internal static void ReprimeSilentAfterEATI()
        {
            return;
        }

        // --- CheckTypeSerializable gate byte patch ---
        // RVA in MonoManager_CheckTypeSerializable where GetAssemblyIndexFromImage result is checked.
        // Patch: cmp eax,-1; setnz al -> mov al,1; nop; nop; nop; nop  (always returns true for non-corlib types).
        private const long RVA_GatePatchSite = 0xE3F6D5;
        private static readonly byte[] GateOrigBytes = new byte[] { 0x83, 0xF8, 0xFF, 0x0F, 0x95, 0xC0 };
        private static readonly byte[] GatePatchBytes = new byte[] { 0xB0, 0x01, 0x90, 0x90, 0x90, 0x90 };
        private static bool _gatePatchApplied = false;

        // --- SetupScriptingCache early-exit NOP patch ---
        // MonoBehaviour::SetupScriptingCache (RVA 0x14BE350) sets MB+160 from the MonoScript's
        // MonoScriptCache+8 class. An early-exit (jnz at RVA 0x14BE38F) skips the rebuild when
        // MB+160 is non-zero, keeping a STALE cache from the old domain. NOP-ing the jnz forces
        // MB+160 to always rebuild from the current MonoScript+216 cache.
        private const long RVA_SetupScriptingCacheEarlyExit = 0x14BE38F;
        private static readonly byte[] SSCacheOrigBytes = new byte[] { 0x0F, 0x85, 0x42, 0x02, 0x00, 0x00 };
        private static readonly byte[] SSCachePatchBytes = new byte[] { 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 };
        private static bool _ssCachePatchApplied = false;

        // True from EndReloadAssembly step 5 until RepairBrokenMonoScriptClasses completes at OnDomainLoad.
        private static volatile bool _insideReload = false;

        // True after the first OnDomainLoad completes (boot fully settled).
        private static volatile bool BootCompleted = false;

        // Re-runs the engine's step-5 MonoScript renewal (RenewMonoScriptsFromAssemblies) at OnDomainLoad,
        // after step 6 has loaded H3VRCode. Step 5 runs before H3VRCode is in MonoManager, leaving H3VRCode
        // MonoScript caches null/ClassNotFound (or corrupted to &mono!builtin_types[0]), which crashes the
        // Inspector. Re-running against the now-loaded H3VRCode restores every cache+8.
        internal static void RepairBrokenMonoScriptClasses()
        {
            try
            {
                var getMonoManagerFn = (d_GetMonoManagerPtr)NativeHookManager.GetDelegateForFunctionPointer<d_GetMonoManagerPtr>(0x14C2510);
                var renewFn = (d_RenewMonoScriptsFromAssemblies)NativeHookManager.GetDelegateForFunctionPointer<d_RenewMonoScriptsFromAssemblies>(0x14C6910);
                if (getMonoManagerFn == null || renewFn == null)
                {
                    Debug.LogError("[ManagedPluginDomainFix] RepairBrokenMonoScriptClasses: failed to resolve native functions");
                    return;
                }

                IntPtr monoManager = getMonoManagerFn();
                if (monoManager == IntPtr.Zero)
                {
                    Debug.LogError("[ManagedPluginDomainFix] RepairBrokenMonoScriptClasses: MonoManager is NULL");
                    return;
                }

                // Renew can fire RequestScriptReload (infinite reload loop); suppress during the pass.
                bool wasSuppressing = NativeHookManager.SuppressRequestScriptReload;
                NativeHookManager.SuppressRequestScriptReload = true;
                try
                {
                    // The full RenewMonoScriptsFromAssemblies re-run is disabled (Renew asserts on valid
                    // caches -> log flood); MonoScript caches are fixed by RepairSceneMonoScriptClasses.
                    // The reload window (steps 5-8) is over; allow the on-demand GetClass repair again.
                    _insideReload = false;
                    // Repairs scene-referenced m_Script MonoScripts whose cache+8 is ClassNotFound
                    // (fixes the Inspector NRE/ATE and the step-7/8 reload crash).
                    try { RepairSceneMonoScriptClasses(); }
                    catch (Exception ex3) { Debug.LogWarning("[ManagedPluginDomainFix] RepairSceneMonoScriptClasses: " + ex3.Message); }

                    // MB instance rebuilds are disabled: RebuildMonoInstance() destroys the managed
                    // instance, breaking the following build. The GetClass guards fix the Inspector crash.
                }
                finally
                {
                    NativeHookManager.SuppressRequestScriptReload = wasSuppressing;
                    if (!wasSuppressing)
                        NativeHookManager.DiscardPendingScriptReload(); // keep the repaired state; no reload loop
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[ManagedPluginDomainFix] RepairBrokenMonoScriptClasses failed: " + ex);
            }
        }


        // Heuristic: is this pointer obviously not a live MonoClass? Pure memory reads, no deref.
        private static bool IsGarbageClassPtr(long cls)
        {
            if (cls == 0) return true;
            if (cls >= 0x0000000100000000L && cls < 0x100000000L) return true; // low code/JIT range
            IntPtr monoBase = DynDll.OpenLibrary("mono.dll");
            if (monoBase != IntPtr.Zero &&
                cls >= monoBase.ToInt64() && cls < monoBase.ToInt64() + (16L * 1024 * 1024)) return true; // &builtin_types[0]
            return false;
        }

        // mono_class_get_image(MonoClass*) -> MonoImage*. cdecl, imported from mono.dll.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr d_MonoClassGetImage(IntPtr monoClass);

        // Cached mono_class_get_image delegate (IAT slot 0x143D338B8).
        private static d_MonoClassGetImage _monoClassGetImageCached;
        private static IntPtr MonoClassGetImage(IntPtr monoClass)
        {
            if (_monoClassGetImageCached == null)
            {
                try { _monoClassGetImageCached = (d_MonoClassGetImage)Marshal.GetDelegateForFunctionPointer(
                    Marshal.ReadIntPtr((IntPtr)0x143D338B8), typeof(d_MonoClassGetImage)); }
                catch { _monoClassGetImageCached = null; }
            }
            if (_monoClassGetImageCached == null) return IntPtr.Zero;
            return _monoClassGetImageCached(monoClass);
        }

        // Cached MonoManager* getter (RVA 0x14C2510). Resolved via Marshal directly because the guards
        // fire DURING serialization, when Application APIs are not allowed.
        private static d_GetMonoManagerPtr _getMonoManagerCached;
        private static IntPtr GetMonoManagerSafe()
        {
            if (_getMonoManagerCached == null)
            {
                try
                {
                    IntPtr basePtr = DynDll.OpenLibrary("Unity.exe");
                    if (basePtr != IntPtr.Zero)
                        _getMonoManagerCached = (d_GetMonoManagerPtr)Marshal.GetDelegateForFunctionPointer(
                            (IntPtr)(basePtr.ToInt64() + 0x14C2510), typeof(d_GetMonoManagerPtr));
                }
                catch { _getMonoManagerCached = null; }
            }
            if (_getMonoManagerCached == null) return IntPtr.Zero;
            try { return _getMonoManagerCached(); } catch { return IntPtr.Zero; }
        }

        // Guard BuildSerializationCacheFor: return the NULL-class cache for stale/garbage classes
        // so the serialization command queue is never built from freed class memory.
        private static IntPtr OnBuildSerializationCacheFor(IntPtr classPtr, IntPtr createFlags)
        {
            // A garbage class would build a garbage command queue (SafeBinaryRead over-read -> reload
            // crash). BuildSerializationCacheFor(NULL) is safe (base-fields-only cache), so use it.
            long guardCls = 0;
            try { guardCls = classPtr.ToInt64(); } catch { }
            if (IsGarbageClassPtr(guardCls))
                return _origBuildSerializationCacheFor(IntPtr.Zero, createFlags);
            // A freed old-domain class passes the range check but its MonoImage is gone from MonoManager's
            // image table; treat it as garbage too.
            try
            {
                IntPtr img = MonoClassGetImage(classPtr);
                if (img != IntPtr.Zero)
                {
                    IntPtr mgr = GetMonoManagerSafe();
                    if (mgr != IntPtr.Zero)
                    {
                        IntPtr imgData = Marshal.ReadIntPtr(mgr, 0x208);
                        IntPtr imgEnd = Marshal.ReadIntPtr(mgr, 0x210);
                        long imgN = (imgEnd != IntPtr.Zero && imgData != IntPtr.Zero)
                            ? (imgEnd.ToInt64() - imgData.ToInt64()) / 8 : 0;
                        long imgVal = img.ToInt64();
                        bool found = false;
                        if (imgN > 0 && imgN < 10000)
                        {
                            for (long i = 0; i < imgN; i++)
                            {
                                if (Marshal.ReadInt64(imgData, (int)(i * 8)) == imgVal) { found = true; break; }
                            }
                        }
                        if (!found) return _origBuildSerializationCacheFor(IntPtr.Zero, createFlags);
                    }
                }
            }
            catch { }
            return _origBuildSerializationCacheFor(classPtr, createFlags);
        }

        // Return NULL (safe 'None') for stale field-value wrappers (NULL vtable or garbage klass) so
        // the Inspector never type-checks a broken wrapper. Gated to Inspector time.
        private static IntPtr OnObjectReferenceValue(IntPtr prop)
        {
            IntPtr wrapper = _origObjectReferenceValue(prop);
            if (wrapper == IntPtr.Zero) return wrapper;
            if (_insideReload || NativeHookManager.BuildInProgress || NativeHookManager.InsideBundleEATI) return wrapper;
            try
            {
                IntPtr vtable = Marshal.ReadIntPtr(wrapper, 0);
                if (vtable == IntPtr.Zero)
                {
                    // Stale wrapper: NULL vtable -> stelemref would NRE. Return null.
                    return IntPtr.Zero;
                }
                IntPtr klass = Marshal.ReadIntPtr(vtable, 0);
                long k = klass.ToInt64();
                if (IsGarbageClassPtr(k))
                {
                    // Stale wrapper: garbage klass -> mono_class_init would AV. Return null.
                    return IntPtr.Zero;
                }
                // A freed old-domain class's MonoImage is gone from MonoManager's image table -> stale.
                try
                {
                    IntPtr image = MonoClassGetImage(klass);
                    if (image == IntPtr.Zero) return IntPtr.Zero;
                    IntPtr mgr = GetMonoManagerSafe();
                    if (mgr != IntPtr.Zero)
                    {
                        IntPtr imgData = Marshal.ReadIntPtr(mgr, 0x208);
                        IntPtr imgEnd = Marshal.ReadIntPtr(mgr, 0x210);
                        long imgN = (imgEnd != IntPtr.Zero && imgData != IntPtr.Zero)
                            ? (imgEnd.ToInt64() - imgData.ToInt64()) / 8 : 0;
                        long img = image.ToInt64();
                        bool found = false;
                        if (imgN > 0 && imgN < 10000)
                        {
                            for (long i = 0; i < imgN; i++)
                            {
                                if (Marshal.ReadInt64(imgData, (int)(i * 8)) == img) { found = true; break; }
                            }
                        }
                        if (!found) return IntPtr.Zero; // image not current -> stale class
                    }
                }
                catch { }
            }
            catch { }
            return wrapper;
        }

        private static IntPtr OnMonoBehaviourGetClass(IntPtr thisMB, IntPtr resultOut)
        {
            IntPtr ret = _origMonoBehaviourGetClass(thisMB, resultOut);
            // Return 0 (safe 'missing script') when the class is garbage: null, in mono.dll's image
            // (&builtin_types[0]), or in the low JIT/code region. Prevents the crash at reload step 7
            // and Inspector time.
            try
            {
                if (ret != IntPtr.Zero)
                {
                    long cls = ret.ToInt64();
                    bool bad = false;
                    IntPtr monoBase = DynDll.OpenLibrary("mono.dll");
                    if (cls >= 0x0000000100000000L && cls < 0x100000000L) bad = true;          // low code/JIT range
                    if (monoBase != IntPtr.Zero &&
                        cls >= monoBase.ToInt64() && cls < monoBase.ToInt64() + (16L * 1024 * 1024)) bad = true; // &builtin_types[0]
                    if (bad)
                    {
                        Marshal.WriteInt64(resultOut, 0);
                        return resultOut;
                    }
                }
            }
            catch { }
            return ret;
        }

        // Reload step-8 guard: skip the invoke when the MB's class (MB+160 -> cache+8) is garbage.
        private static byte OnCallMethodInactive(IntPtr thisMB, IntPtr methodPtr)
        {
            try
            {
                if (thisMB != IntPtr.Zero)
                {
                    IntPtr cache = Marshal.ReadIntPtr(thisMB, 0xA0); // MB+160
                    if (cache != IntPtr.Zero)
                    {
                        long cls = Marshal.ReadInt64((IntPtr)(cache.ToInt64() + 8)); // cache+8
                        bool bad = (cls == 0);
                        if (!bad)
                        {
                            IntPtr monoBase = DynDll.OpenLibrary("mono.dll");
                            if (cls >= 0x0000000100000000L && cls < 0x100000000L) bad = true;
                            if (monoBase != IntPtr.Zero &&
                                cls >= monoBase.ToInt64() && cls < monoBase.ToInt64() + (16L * 1024 * 1024)) bad = true;
                        }
                        if (bad)
                            return 1; // skip the invoke — reload must survive
                    }
                }
            }
            catch { }
            return _origCallMethodInactive(thisMB, methodPtr);
        }

        private static IntPtr OnMonoScriptGetClass(IntPtr monoScript, IntPtr resultOut)
        {
            IntPtr ret = _origGetClass(monoScript, resultOut);
            // Return 0 (safe 'missing script') when cache+8 is garbage (e.g. &mono!builtin_types[0]),
            // which would otherwise crash the Inspector's mono_class_init write.
            long guardCls = 0;
            try { guardCls = Marshal.ReadInt64(resultOut); } catch { }
            if (IsGarbageClassPtr(guardCls))
            {
                try { Marshal.WriteInt64(resultOut, 0); } catch { }
            }
            return ret;
        }

        // Repairs scene-referenced m_Script MonoScripts whose +216 cache+8 is ClassNotFound (they are
        // not in FindObjectsOfType<MonoScript>, so the OnDomainLoad renewal never fixes them). Iterates
        // live MonoBehaviours via FindInstanceIDsOfType<MonoBehaviour> (safe on corrupt MBs), dedups the
        // referenced MonoScripts, and re-Renews any whose cache+8 is stale, using GetMonoClassWithAssemblyName
        // as the authoritative class. Zeroes +216 first (avoid Renew's assert) and PropertiesHash (+200/+208).
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate long d_GetMonoClassWithAssemblyNameCdecl(IntPtr manager, IntPtr classNameStr, IntPtr namespaceStr, IntPtr asmNameStr);
        // Object::FindInstanceIDsOfType — 0x14091E8D0. Fills an array with all live MonoBehaviour IDs.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void d_FindInstanceIDsOfType(IntPtr rtti, IntPtr outArray, int sort);
        // GetObjectFromInstanceId(int) -> Object* — 0x140AB9BB0.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr d_GetObjectFromInstanceId(int instanceId);

        internal static void RepairSceneMonoScriptClasses()
        {
            try
            {
                var getMonoManager = (d_GetMonoManagerPtr)NativeHookManager.GetDelegateForFunctionPointer<d_GetMonoManagerPtr>(0x14C2510);
                if (getMonoManager == null) return;
                IntPtr mgr = getMonoManager();
                if (mgr == IntPtr.Zero) return;

                var gmcawn = (d_GetMonoClassWithAssemblyNameCdecl)NativeHookManager.GetDelegateForFunctionPointer<d_GetMonoClassWithAssemblyNameCdecl>(0x14C32E0);
                var findMBs = (d_FindInstanceIDsOfType)NativeHookManager.GetDelegateForFunctionPointer<d_FindInstanceIDsOfType>(0x91E8D0);
                var getObjById = (d_GetObjectFromInstanceId)NativeHookManager.GetDelegateForFunctionPointer<d_GetObjectFromInstanceId>(0xAB9BB0);
                if (gmcawn == null || findMBs == null || getObjById == null) return;

                // dynamic_array<int> header for FindInstanceIDsOfType output.
                int maxMBs = 60000;
                IntPtr mbDataBuf = Marshal.AllocHGlobal(maxMBs * 4);
                IntPtr mbArrayHdr = Marshal.AllocHGlobal(64);
                try
                {
                    for (int i = 0; i < 8; i++) Marshal.WriteInt64(mbArrayHdr, i * 8, 0);
                    Marshal.WriteInt64(mbArrayHdr, 0, mbDataBuf.ToInt64());   // data
                    Marshal.WriteInt64(mbArrayHdr, 24, 0);                    // size
                    Marshal.WriteInt64(mbArrayHdr, 32, maxMBs);               // capacity

                    // Object::FindInstanceIDsOfType(&TypeInfoContainer<MonoBehaviour>::rtti, &array, sort=false)
                    findMBs(new IntPtr(0x143B3BC30), mbArrayHdr, 0);

                    long mbCount = Marshal.ReadInt64(mbArrayHdr, 24);
                    if (mbCount <= 0 || mbCount > maxMBs)
                    {
                        Debug.Log("[ManagedPluginDomainFix] RepairSceneMonoScriptClasses: bad MB count=" + mbCount);
                        return;
                    }

                    var repairedMs = new HashSet<long>();
                    int repaired = 0, checkedCount = 0, alreadyValid = 0, unresolved = 0, badCache = 0;
                    for (long i = 0; i < mbCount; i++)
                    {
                        int mbId = Marshal.ReadInt32(mbDataBuf, (int)(i * 4));
                        if (mbId == 0) continue;
                        IntPtr mb = getObjById(mbId);
                        if (mb == IntPtr.Zero) continue;

                        // m_Script PPtr<MonoScript> at MB+0x68 (104): a 4-byte instance ID.
                        int msId = Marshal.ReadInt32(mb, 0x68);
                        if (msId == 0) continue;
                        IntPtr ms = getObjById(msId);
                        if (ms == IntPtr.Zero) continue;

                        long msKey = ms.ToInt64();
                        if (!repairedMs.Add(msKey)) continue;

                        IntPtr cache = Marshal.ReadIntPtr(ms, MonoScriptCacheOffset); // +216
                        long cls = 0;
                        if (cache != IntPtr.Zero) cls = Marshal.ReadInt64((IntPtr)(cache.ToInt64() + CacheClassOffset)); // +8
                        checkedCount++;
                        bool garbage = IsGarbageClassPtr(cls);

                        // A cache+8 class can pass the range check yet still be stale (old-domain image).
                        // GetMonoClassWithAssemblyName is the authoritative class; renew if they differ.
                        string className = UnityNativeHelper.ReadNativeString(ms, 0xE0);
                        if (string.IsNullOrEmpty(className)) { unresolved++; continue; }

                        long resolved = gmcawn(
                            mgr,
                            new IntPtr(msKey + 0xE0),    // className
                            new IntPtr(msKey + 0x110),   // namespace
                            new IntPtr(msKey + 0x140));  // assemblyName

                        bool stale = false;
                        if (resolved == 0)
                        {
                            // Assembly not resolvable in the current MonoManager image table. If the cache
                            // class is already garbage, treat as broken; otherwise leave it (cannot verify).
                            if (garbage) { badCache++; }
                            else { alreadyValid++; }
                            unresolved++;
                            continue;
                        }
                        if (garbage) { stale = true; }
                        else if (cls != resolved) { stale = true; }

                        if (!stale)
                        {
                            alreadyValid++;
                            continue;
                        }
                        badCache++;
                        Marshal.WriteInt64(ms, MonoScriptCacheOffset, 0); // clear +216 (avoid Renew assert)
                        Marshal.WriteInt64(ms, 200, 0);                    // zero PropertiesHash lo
                        Marshal.WriteInt64(ms, 208, 0);                    // zero PropertiesHash hi
                        _origMonoScriptRenew(msKey, resolved);
                        repaired++;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(mbDataBuf);
                    Marshal.FreeHGlobal(mbArrayHdr);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ManagedPluginDomainFix] RepairSceneMonoScriptClasses failed: " + ex.Message);
            }
        }

        // MonoScript::Renew hook. Marks the reload window (EndReloadAssembly step 5). Previously returned
        // early for H3VRCode (classPtr==0 before step 6 loads it), leaving +216 null and crashing step 8;
        // now it passes classPtr==0 through so Renew creates a proper ClassNotFound cache, and
        // RepairBrokenMonoScriptClasses fixes the real class at OnDomainLoad.
        private static void OnMonoScriptRenew(long pMonoScript, long classPtr)
        {
            // Mark the reload window; the on-demand repair in OnMonoScriptGetClass only runs outside it.
            _insideReload = true;
            _origMonoScriptRenew(pMonoScript, classPtr);
        }

        // GetMonoClassWithAssemblyName hook: the original returns 0 for H3VRCode during reload step 5
        // (H3VRCode not yet in MonoManager's image table). Re-resolve the class from the H3VRCode image so
        // step 5's MonoScript_Renew produces a real cache. Runs inside the reload but does not re-enter it.
        // Args: (MonoManager*, className*, namespace*, assemblyName*) as core::basic_string pointers.
        private static long OnGetMonoClassWithAssemblyName(IntPtr monoManager, IntPtr classNameStr, IntPtr namespaceStr, IntPtr assemblyNameStr)
        {
            long ret = _origGetMonoClassWithAssemblyName(monoManager, classNameStr, namespaceStr, assemblyNameStr);
            if (ret != 0) return ret;
            try
            {
                // Fast path: only attempt the fallback for the H3VRCode assemblies.
                string asmName = UnityNativeHelper.ReadNativeString(assemblyNameStr, 0);
                if (string.IsNullOrEmpty(asmName)) return 0;
                bool isH3VR = asmName.IndexOf("H3VRCode", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!isH3VR) return 0;

                string className = UnityNativeHelper.ReadNativeString(classNameStr, 0);
                string ns = UnityNativeHelper.ReadNativeString(namespaceStr, 0);
                if (string.IsNullOrEmpty(className) || string.IsNullOrEmpty(ns)) return 0;

                // Resolve the MonoClass* from the already-loaded H3VRCode assembly image.
                long monoClass = ResolveH3VRCodeClass(asmName, ns, className);
                if (monoClass != 0)
                    return monoClass;
                return 0;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ManagedPluginDomainFix] GetMonoClassWithAssemblyName fallback failed: " + ex.Message);
                return 0;
            }
        }

        // Resolves a MonoClass* via mono_assembly_loaded / get_image / class_from_name; 0 on failure.
        // Import table: 0x143D33890 / 0x143D33668 / 0x143D33518.
        private static long ResolveH3VRCodeClass(string assemblyName, string ns, string className)
        {
            try
            {
                var assemblyLoaded = (d_mono_assembly_loaded)NativeHookManager.GetDelegateForFunctionPointer<d_mono_assembly_loaded>(0x143D33890);
                var assemblyGetImage = (d_mono_assembly_get_image)NativeHookManager.GetDelegateForFunctionPointer<d_mono_assembly_get_image>(0x143D33668);
                var classFromName = (d_mono_class_from_name)NativeHookManager.GetDelegateForFunctionPointer<d_mono_class_from_name>(0x143D33518);
                if (assemblyLoaded == null || assemblyGetImage == null || classFromName == null) return 0;

                // Parse the name into a stack MonoAssemblyName, then load the assembly and image.
                var assemblyNameParse = (d_mono_assembly_name_parse)NativeHookManager.GetDelegateForFunctionPointer<d_mono_assembly_name_parse>(0x143D33888);
                if (assemblyNameParse == null) return 0;

                IntPtr asmNameBuf = Marshal.AllocHGlobal(0x48);
                IntPtr monoImage = IntPtr.Zero;
                try
                {
                    for (int i = 0; i < 0x48; i += 8) Marshal.WriteInt64(asmNameBuf, i, 0);
                    if (assemblyNameParse(assemblyName, asmNameBuf) == 0) return 0;
                    IntPtr asm = assemblyLoaded(asmNameBuf);
                    if (asm == IntPtr.Zero) return 0;
                    monoImage = assemblyGetImage(asm);
                }
                finally
                {
                    Marshal.FreeHGlobal(asmNameBuf);
                }
                if (monoImage == IntPtr.Zero) return 0;

                return classFromName(monoImage, ns, className);
            }
            catch
            {
                return 0;
            }
        }

        // mono.dll import shims. mono_assembly_name_parse(const char*, MonoAssemblyName*) — arg order
        // verified from call site 0x1414C3525.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int d_mono_assembly_name_parse(string nameIn, IntPtr nameOut);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr d_mono_assembly_loaded(IntPtr aname);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr d_mono_assembly_get_image(IntPtr assembly);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate long d_mono_class_from_name(IntPtr image, string ns, string name);

        // OnDomainUnload handler REMOVED: MonoImporter.GetAllRuntimeMonoScripts() crashed there during
        // AppDomain teardown ("MonoManager is NULL").

        private static void OnDomainLoad()
        {
            // Repairs Assets/Managed MonoScripts whose caches are null/ClassNotFound post-reload, then
            // retries once a few frames later (H3VRCode plugin loading can lag a tick).
            try
            {
                RepairBrokenMonoScriptClasses();
                BootCompleted = true;
                EditorApplication.delayCall += delegate
                {
                    try { RepairBrokenMonoScriptClasses(); }
                    catch (Exception ex2) { Debug.LogError("[ManagedPluginDomainFix] RepairBrokenMonoScriptClasses (retry) failed: " + ex2); }
                };
            }
            catch (Exception ex)
            {
                Debug.LogError("[ManagedPluginDomainFix] RepairBrokenMonoScriptClasses failed: " + ex);
            }
        }

        private static void SetMonoAssemblySearchPaths()
        {
            try
            {
                IntPtr monoModule = GetMonoModule();
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

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var dirList = new List<string>();

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

        private static bool IsManagedAssembly(string path)
        {
            try { AssemblyName.GetAssemblyName(path); return true; }
            catch { return false; }
        }

        private static void PreloadUnityExtensionDlls()
        {
            if (!Directory.Exists(_unityExtensionsDir)) return;
            var allDlls = Directory.GetFiles(_unityExtensionsDir, "*.dll", SearchOption.AllDirectories);
            Array.Sort(allDlls, (a, b) => a.Length.CompareTo(b.Length));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

        private static Assembly ResolveUnityExtensionAssembly(object sender, ResolveEventArgs args)
        {
            var simpleName = new AssemblyName(args.Name).Name;
            // Search UnityExtensions first (recursive), then Assets/Managed/ (flat).
            string[][] searchPaths = new string[][] {
                Directory.Exists(_unityExtensionsDir) ? Directory.GetFiles(_unityExtensionsDir, simpleName + ".dll", SearchOption.AllDirectories) : new string[0],
                File.Exists(Path.Combine(_managedDir, simpleName + ".dll")) ? new[] { Path.Combine(_managedDir, simpleName + ".dll") } : new string[0]
            };
            foreach (string[] dlls in searchPaths)
            {
                if (dlls.Length == 0) continue;
                Array.Sort(dlls, (a, b) => a.Length.CompareTo(b.Length));
                foreach (var dll in dlls)
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
            return null;
        }

        // Called each reload via BeforeShutdownCallbacks (inside OnShutdownManaged).
        // Copies H3VRCode DLLs to ScriptAssemblies/. Core helper for shutdown, ctor, and pre-EATI paths.
        private static void CopyH3VRCodeDlls(bool onlyIfMissing)
        {
            if (!Directory.Exists(_managedDir)) return;
            if (!Directory.Exists(_scriptAssembliesDir)) Directory.CreateDirectory(_scriptAssembliesDir);
            foreach (var name in new[] { MeatKit.AssemblyRename, MeatKit.AssemblyFirstpassRename })
            {
                var src  = Path.Combine(_managedDir, name + ".dll");
                var dest = Path.Combine(_scriptAssembliesDir, name + ".dll");
                try
                {
                    if (onlyIfMissing) { if (!File.Exists(dest) && File.Exists(src)) File.Copy(src, dest); }
                    else CopyDllIfChanged(src, dest);
                }
                catch (Exception ex) { Debug.LogWarning("[ManagedPluginDomainFix] DLL copy failed: " + name + ".dll: " + ex.Message); }
            }
        }

        private static bool FileContentsEqual(string a, string b)
        {
            try
            {
                byte[] ba = File.ReadAllBytes(a), bb = File.ReadAllBytes(b);
                if (ba.Length != bb.Length) return false;
                for (int i = 0; i < ba.Length; i++)
                    if (ba[i] != bb[i]) return false;
                return true;
            }
            catch { return false; }
        }

        private static void CopyDllIfChanged(string src, string dest)
        {
            if (!File.Exists(src)) return;
            if (File.Exists(dest) && FileContentsEqual(src, dest)) return;
            File.Copy(src, dest, true);
            File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(src));
        }

        internal static void EnsureH3VRCodeInScriptAssemblies() { CopyH3VRCodeDlls(false); }

        // Sharing-violation recovery: stages locked DLL, disables .meta, copies next reload.
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

            string[] lines;
            try { lines = File.ReadAllLines(_pendingManifestPath); }
            catch { return; }
            // NOTE: do NOT delete manifest here. Only delete after successful copy.

            bool anyApplied = false;
            var remaining = new List<string>();
            var seenEntries = new HashSet<string>();
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
                    ReEnablePluginMeta(metaPath);
                    continue;
                }
                try
                {
                    File.Copy(pendingPath, destPath, true);
                    File.Delete(pendingPath);

                    ReEnablePluginMeta(metaPath);
                    anyApplied = true;
                }
                catch (IOException)
                {
                    remaining.Add(line);
                    Debug.LogWarning("[ManagedPluginDomainFix] DLL still locked, pending copy deferred to next domain: " +
                        Path.GetFileName(destPath));
                }
                catch (Exception ex)
                {
                    ReEnablePluginMeta(metaPath);
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

        private static void ReEnablePluginMeta(string metaPath)
        {
            if (!File.Exists(metaPath)) return;
            File.WriteAllText(metaPath, BuildPluginMeta(ExtractGuidFromMeta(File.ReadAllText(metaPath)), anyEnabled: true));
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
            string e = anyEnabled ? "1" : "0";
            return "fileFormatVersion: 2\nguid: " + guid + "\nPluginImporter:\n  serializedVersion: 2\n  iconMap: {}\n  executionOrder: {}\n" +
                   "  isPreloaded: 0\n  isOverridable: 0\n  platformData:\n    data:\n      first:\n        Any:\n      second:\n        enabled: " + e + "\n        settings: {}\n" +
                   "    data:\n      first:\n        Editor: Editor\n      second:\n        enabled: " + e + "\n        settings:\n          DefaultValueInitialized: true\n" +
                   "    data:\n      first:\n        Windows Store Apps: WindowsStoreApps\n      second:\n        enabled: 0\n        settings:\n          CPU: AnyCPU\n" +
                   "  userData:\n  assetBundleName:\n  assetBundleVariant:";
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtect(IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

        [DllImport("kernel32.dll")]
        private static extern bool FlushInstructionCache(IntPtr hProcess, IntPtr lpBaseAddress, UIntPtr dwSize);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        private static void ApplyCheckTypeSerializablePatch()
        {
            if (_gatePatchApplied) return;
            try
            {
                IntPtr unityBase = DynDll.OpenLibrary("Unity.exe");
                IntPtr patchAddr = (IntPtr)(unityBase.ToInt64() + RVA_GatePatchSite);

                byte[] current = new byte[6];
                Marshal.Copy(patchAddr, current, 0, 6);
                bool match = true;
                for (int i = 0; i < 6; i++)
                {
                    if (current[i] != GateOrigBytes[i]) { match = false; break; }
                }

                if (!match)
                {
                    bool already = true;
                    for (int i = 0; i < 6; i++)
                        if (current[i] != GatePatchBytes[i]) { already = false; break; }
                    if (already) { _gatePatchApplied = true; return; }
                    Debug.LogWarning("[ManagedPluginDomainFix] CheckTypeSerializable: unexpected bytes at patch site — skipping");
                    return;
                }

                uint oldProtect;
                if (!VirtualProtect(patchAddr, (UIntPtr)6, 0x40, out oldProtect)) return;
                Marshal.Copy(GatePatchBytes, 0, patchAddr, 6);
                uint ignored;
                VirtualProtect(patchAddr, (UIntPtr)6, oldProtect, out ignored);
                FlushInstructionCache(GetCurrentProcess(), patchAddr, (UIntPtr)6);
                _gatePatchApplied = true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ManagedPluginDomainFix] ApplyCheckTypeSerializablePatch: " + ex.Message);
            }
        }

        // Applies the SetupScriptingCache early-exit NOP patch (see constants above). Refuses to
        // patch if the current bytes don't match the expected originals (wrong binary/version).
        private static void ApplySetupScriptingCachePatch()
        {
            if (_ssCachePatchApplied) return;
            try
            {
                IntPtr unityBase = DynDll.OpenLibrary("Unity.exe");
                IntPtr patchAddr = (IntPtr)(unityBase.ToInt64() + RVA_SetupScriptingCacheEarlyExit);

                byte[] current = new byte[6];
                Marshal.Copy(patchAddr, current, 0, 6);
                bool match = true;
                for (int i = 0; i < 6; i++)
                    if (current[i] != SSCacheOrigBytes[i]) { match = false; break; }

                if (!match)
                {
                    bool already = true;
                    for (int i = 0; i < 6; i++)
                        if (current[i] != SSCachePatchBytes[i]) { already = false; break; }
                    if (already) { _ssCachePatchApplied = true; return; }
                    Debug.LogWarning("[ManagedPluginDomainFix] SetupScriptingCache: unexpected bytes at patch site - skipping");
                    return;
                }

                uint oldProtect;
                if (!VirtualProtect(patchAddr, (UIntPtr)6, 0x40, out oldProtect)) return;
                Marshal.Copy(SSCachePatchBytes, 0, patchAddr, 6);
                uint ignored;
                VirtualProtect(patchAddr, (UIntPtr)6, oldProtect, out ignored);
                FlushInstructionCache(GetCurrentProcess(), patchAddr, (UIntPtr)6);
                _ssCachePatchApplied = true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ManagedPluginDomainFix] ApplySetupScriptingCachePatch: " + ex.Message);
            }
        }

        // Installs the domain-fix detours and byte patches. Runs from the static ctor.
        private static void InstallDomainFixHooks()
        {
            if (!EditorVersion.IsSupportedVersion) return;

            NativeHookFunctionOffsets offsets = EditorVersion.Current.FunctionOffsets;

            // Install MonoScript::GetClass hook (guarded by OnMonoScriptGetClass).
            long getClassOffset = offsets.GetClass;
            if (getClassOffset != 0)
            {
                try
                {
                    _origGetClass = NativeHookManager.ApplyEditorDetour<d_GetClass>(
                        getClassOffset,
                        new d_GetClass(OnMonoScriptGetClass));
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[ManagedPluginDomainFix] Failed to install GetClass detour: " + ex.Message);
                }

                // Guard MonoBehaviour::GetClass: return 0 for garbage per-instance classes to prevent
                // the reload step-7 and Inspector crashes.
                try
                {
                    _origMonoBehaviourGetClass = NativeHookManager.ApplyEditorDetour<d_MonoBehaviourGetClass>(
                        0x14BC7B0,
                        new d_MonoBehaviourGetClass(OnMonoBehaviourGetClass));
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[ManagedPluginDomainFix] Failed to install MonoBehaviour::GetClass detour: " + ex.Message);
                }

                // Guard CallMethodInactive (reload step 8): skip invokes on MBs whose class is garbage.
                try
                {
                    _origCallMethodInactive = NativeHookManager.ApplyEditorDetour<d_CallMethodInactive>(
                        0x14BC9E0,
                        new d_CallMethodInactive(OnCallMethodInactive));
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[ManagedPluginDomainFix] Failed to install CallMethodInactive detour: " + ex.Message);
                }
            }

            // Install MonoScript_Renew hook. Marks the reload window (see OnMonoScriptRenew).
            long msrOffset = offsets.MonoScriptRenew;
            if (msrOffset != 0)
            {
                try
                {
                    _origMonoScriptRenew = NativeHookManager.ApplyEditorDetour<MonoScriptRenewDelegate>(
                        msrOffset,
                        new MonoScriptRenewDelegate(OnMonoScriptRenew));
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[ManagedPluginDomainFix] Failed to install MonoScript_Renew detour: " + ex.Message);
                }
            }

            // Detour MonoManager::GetMonoClassWithAssemblyName so reload step 5 resolves H3VRCode
            // classes instead of returning 0 (see OnGetMonoClassWithAssemblyName).
            try
            {
                _origGetMonoClassWithAssemblyName = NativeHookManager.ApplyEditorDetour<d_GetMonoClassWithAssemblyName>(
                    0x14C32E0,
                    new d_GetMonoClassWithAssemblyName(OnGetMonoClassWithAssemblyName));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ManagedPluginDomainFix] Failed to install GetMonoClassWithAssemblyName detour: " + ex.Message);
            }

            // Guard BuildSerializationCacheFor against stale/garbage classes during step-7 restore.
            try
            {
                _origBuildSerializationCacheFor = NativeHookManager.ApplyEditorDetour<d_BuildSerializationCacheFor>(
                    0xE4BD30,
                    new d_BuildSerializationCacheFor(OnBuildSerializationCacheFor));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ManagedPluginDomainFix] Failed to install BuildSerializationCacheFor detour: " + ex.Message);
            }

            // Guard SerializedProperty.objectReferenceValue: return NULL for stale wrappers whose
            // vtable/klass is garbage, so the Inspector never type-checks a broken wrapper.
            try
            {
                _origObjectReferenceValue = NativeHookManager.ApplyEditorDetour<d_ObjectReferenceValue>(
                    0x1386D30,
                    new d_ObjectReferenceValue(OnObjectReferenceValue));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ManagedPluginDomainFix] Failed to install objectReferenceValue guard detour: " + ex.Message);
            }

            // Apply the CheckTypeSerializable gate patch immediately (process-lifetime code section
            // patch, no domain dependencies). Wrapped in try/catch so failures don't break the ctor.
            try { ApplyCheckTypeSerializablePatch(); }
            catch (Exception ex) { Debug.LogWarning("[ManagedPluginDomainFix] Gate patch failed: " + ex.Message); }

            // NOP the SetupScriptingCache early-exit so MB+160 always rebuilds from the current
            // MonoScript cache (prevents the stale-domain MB+160 crash during the post-build reload).
            try { ApplySetupScriptingCachePatch(); }
            catch (Exception ex) { Debug.LogWarning("[ManagedPluginDomainFix] SetupScriptingCache patch failed: " + ex.Message); }
        }

    }
}
