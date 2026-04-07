using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
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
        private const string H3VRCodeReimportFlagKey = "ManagedPluginDomainFix_H3VRReimportDone";
        private const string PostBuildReimportKey = "ManagedPluginDomainFix_PostBuildReimportPending";
        private const string PostBuildFlagKey = "ManagedPluginDomainFix_PostBuild";

        private static readonly string _managedDir;
        private static readonly string _scriptAssembliesDir;
        private static readonly string _unityExtensionsDir;
        private static readonly string _pendingManifestPath;

        // Cached reflection — resolved once, valid for AppDomain lifetime.
        private static readonly FieldInfo _cachedPtrField = typeof(UnityEngine.Object).GetField(
            "m_CachedPtr", BindingFlags.NonPublic | BindingFlags.Instance);

        // Cached module handles — resolved lazily, valid for process lifetime.
        private static IntPtr _monoModule;
        private static IntPtr _unityModule;

        // MonoTypeEnum names shared across diagnostic methods.
        private static readonly string[] _monoTypeNames = new string[] {
            "END", "VOID", "BOOLEAN", "CHAR", "I1", "U1", "I2", "U2",
            "I4", "U4", "I8", "U8", "R4", "R8", "STRING", "PTR",
            "BYREF", "VALUETYPE", "CLASS", "VAR", "ARRAY", "GENERICINST",
            "TYPEDBYREF", "?23", "I", "U", "?26", "FNPTR", "OBJECT",
            "SZARRAY", "MVAR"
        };

        private static IntPtr GetMonoModule()
        {
            if (_monoModule == IntPtr.Zero)
            {
                _monoModule = GetModuleHandle("mono");
                if (_monoModule == IntPtr.Zero) _monoModule = GetModuleHandle("mono.dll");
            }
            return _monoModule;
        }

        private static IntPtr GetUnityModule()
        {
            if (_unityModule == IntPtr.Zero)
            {
                _unityModule = GetModuleHandle("Unity");
                if (_unityModule == IntPtr.Zero) _unityModule = GetModuleHandle("Unity.exe");
            }
            return _unityModule;
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

            CopyMissingToScriptAssemblies();

            NativeHookManager.BeforeShutdownCallbacks.Add(CopyAllToScriptAssemblies);

            // Note: PIOLA and RUA hooks cannot be installed safely from an [InitializeOnLoad]
            // static ctor. ShutdownManaged + BeforeEATI callbacks cover all required work.

            // ── MonoManager fix (MUST run before any CSD regeneration) ──────────────────────────
            // Root cause: MonoManager's assembly image array doesn't include H3VRCode-CSharp
            // because RenewMonoScriptsFromAssemblies runs before the DLL is loaded.
            // This causes the serialization gatekeeper (sub_140E3F670) to reject custom types
            // like Anvil.AssetID, making m_anvilPrefab disappear from TypeTree/CSD.
            // Fix: inject H3VRCode's MonoImage into a null slot in the array EARLY,
            // so any subsequent CSD generation (lazy, during asset access) includes it.
            EnsureH3VRCodeInMonoManagerImageArray();

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
                    // NOTE: SuppressRequestScriptReload is still active from the ctor.
                    
                    // Ensure H3VRCode DLLs are fresh (not stale from a prior build).
                    EnsureH3VRCodeInScriptAssemblies();
                    
                    VerifyH3VRCodeMonoScripts();
                    
                    bool isPostBuild = EditorPrefs.GetBool(PostBuildFlagKey, false);
                    
                    EditorPrefs.DeleteKey(H3VRCodeReimportFlagKey);
                    EditorPrefs.DeleteKey(PostBuildReimportKey);
                    EditorPrefs.DeleteKey(PostBuildFlagKey);
                    NativeHookManager.SuppressRequestScriptReload = false;
                    NativeHookManager.DiscardPendingScriptReload();
                    
                    if (isPostBuild)
                    {
                        // Post-build: CSD may have been cached incorrectly from a prior
                        // domain reload (before our cctor ran). Check and fix if needed.
                        string probeResult = ProbeFirstAnvilPrefab();
                        Log("Post-build probe: " + probeResult);
                        
                        // NOPROP means the TypeTree is missing m_anvilPrefab entirely.
                        // EMPTY means the field EXISTS but values are empty (stale in-memory objects) — this is OK.
                        bool typeTreeBroken = probeResult.Contains("NOPROP=") && !probeResult.Contains("NOPROP=0");
                        if (typeTreeBroken)
                        {
                            Log("Post-build: TypeTree still broken (NOPROP>0) — clearing CSD + reimporting...");
                            ClearAllTypeTreeCaches();
                            ReimportObjectAssets();
                            
                            string probeAfter = ProbeFirstAnvilPrefab();
                            Log("Post-build probe AFTER CSD clear: " + probeAfter);
                            
                            bool stillBroken = probeAfter.Contains("NOPROP=") && !probeAfter.Contains("NOPROP=0");
                            if (stillBroken)
                            {
                                Log("Post-build: FIX FAILED — m_anvilPrefab still missing from TypeTree");
                                DiagGatekeeperStepByStep();
                            }
                            else
                            {
                                Log("Post-build: FIX SUCCEEDED — m_anvilPrefab restored in TypeTree");
                            }
                        }
                        else
                        {
                            Log("Post-build: m_anvilPrefab present in TypeTree, no CSD fix needed.");
                        }
                    }
                    
                    // ── Verification diagnostic ──
                    DiagProbeTypeTree();
                };
            }
            else
            {
                // Hook failed to install (wrong RVA? unsupported version?). Fall back to the
                // safe delayCall path, which avoids the reload loop but leaves the inspector
                // with broken components until the next domain reload.
                Debug.LogWarning("[ManagedPluginDomainFix] RequestScriptReload hook not installed — falling back to delayCall repair (inspector may show broken components)");
                EditorApplication.delayCall += delegate 
                { 
                    RepairH3VRCodeMonoScripts("delayCall");
                    EnsureH3VRCodeInScriptAssemblies();
                };
            }
        }

        // ── Mono assembly search path setup ────────────────────────────────────────

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void d_mono_set_assemblies_path([MarshalAs(UnmanagedType.LPStr)] string path);

        // mono_class_get_parent and mono_class_get_name for native class chain walking
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr d_mono_class_get_parent(IntPtr klass);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr d_mono_class_get_name(IntPtr klass);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr d_mono_class_get_namespace(IntPtr klass);

        private static d_mono_class_get_parent _monoClassGetParent;
        private static d_mono_class_get_name _monoClassGetName;
        private static d_mono_class_get_namespace _monoClassGetNamespace;

        private static void InitMonoClassHelpers()
        {
            if (_monoClassGetParent != null) return;
            IntPtr monoModule = GetMonoModule();
            if (monoModule == IntPtr.Zero) return;

            IntPtr pParent = GetProcAddress(monoModule, "mono_class_get_parent");
            IntPtr pName = GetProcAddress(monoModule, "mono_class_get_name");
            IntPtr pNs = GetProcAddress(monoModule, "mono_class_get_namespace");
            if (pParent != IntPtr.Zero)
                _monoClassGetParent = (d_mono_class_get_parent)Marshal.GetDelegateForFunctionPointer(
                    pParent, typeof(d_mono_class_get_parent));
            if (pName != IntPtr.Zero)
                _monoClassGetName = (d_mono_class_get_name)Marshal.GetDelegateForFunctionPointer(
                    pName, typeof(d_mono_class_get_name));
            if (pNs != IntPtr.Zero)
                _monoClassGetNamespace = (d_mono_class_get_namespace)Marshal.GetDelegateForFunctionPointer(
                    pNs, typeof(d_mono_class_get_namespace));
        }

        // Returns a formatted native parent-class chain string starting from pClass.
        private static string WalkNativeClassChain(IntPtr pClass)
        {
            InitMonoClassHelpers();
            if (_monoClassGetParent == null || _monoClassGetName == null) return "no_mono_helpers";

            var chain = new List<string>();
            IntPtr cur = pClass;
            int safety = 0;
            while (cur != IntPtr.Zero && safety < 20)
            {
                string name = Marshal.PtrToStringAnsi(_monoClassGetName(cur));
                string ns = "";
                if (_monoClassGetNamespace != null)
                    ns = Marshal.PtrToStringAnsi(_monoClassGetNamespace(cur));
                chain.Add(string.Format("{0}.{1}(0x{2:X})", ns, name, cur.ToInt64()));
                cur = _monoClassGetParent(cur);
                safety++;
            }
            return string.Join(" → ", chain.ToArray());
        }

        // Logs native stop-class comparison for ScriptableObject/MonoBehaviour/Object/Component.
        private static void DiagStopClasses(IntPtr pClassFVRObject)
        {
            InitMonoClassHelpers();
            if (_monoClassGetParent == null || _monoClassGetName == null) return;

            try
            {
                // Get MonoClass* for known Unity base types via RuntimeTypeHandle
                Type tScriptableObject = typeof(ScriptableObject);
                Type tMonoBehaviour = typeof(MonoBehaviour);
                Type tObject = typeof(UnityEngine.Object);
                Type tComponent = typeof(Component);

                // MonoType->data.klass gives us MonoClass*  
                // In Mono 2.0, RuntimeTypeHandle.Value points to MonoType struct
                // MonoType { MonoClass* data.klass (offset 0); ... }
                IntPtr pSO = Marshal.ReadIntPtr(tScriptableObject.TypeHandle.Value);
                IntPtr pMB = Marshal.ReadIntPtr(tMonoBehaviour.TypeHandle.Value);
                IntPtr pObj = Marshal.ReadIntPtr(tObject.TypeHandle.Value);
                IntPtr pComp = Marshal.ReadIntPtr(tComponent.TypeHandle.Value);

                Log(string.Format("StopClassDiag: ScriptableObject=0x{0:X} MonoBehaviour=0x{1:X} Object=0x{2:X} Component=0x{3:X}",
                    pSO.ToInt64(), pMB.ToInt64(), pObj.ToInt64(), pComp.ToInt64()));

                // Walk FVRObject's parent chain and flag matches
                IntPtr cur = pClassFVRObject;
                int depth = 0;
                while (cur != IntPtr.Zero && depth < 20)
                {
                    string name = Marshal.PtrToStringAnsi(_monoClassGetName(cur));
                    string flags = "";
                    if (cur == pSO) flags += "[STOP:ScriptableObject] ";
                    if (cur == pMB) flags += "[STOP:MonoBehaviour] ";
                    if (cur == pObj) flags += "[STOP:Object] ";
                    if (cur == pComp) flags += "[STOP:Component] ";

                    Log(string.Format("  chain[{0}]: {1} (0x{2:X}) {3}", depth, name, cur.ToInt64(), flags));
                    cur = _monoClassGetParent(cur);
                    depth++;
                }
            }
            catch (Exception ex)
            {
                Log("DiagStopClasses error: " + ex.Message);
            }
        }

        // mono_class_get_fields(MonoClass*, gpointer* iter) returns MonoClassField*
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr d_mono_class_get_fields(IntPtr klass, ref IntPtr iter);

        // mono_field_get_name(MonoClassField*) returns const char*
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr d_mono_field_get_name(IntPtr field);

        // mono_class_num_fields(MonoClass*)
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int d_mono_class_num_fields(IntPtr klass);

        // mono_field_get_type(MonoClassField*) returns MonoType*
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr d_mono_field_get_type(IntPtr field);

        // mono_field_get_flags(MonoClassField*) returns uint (FieldAttributes)
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint d_mono_field_get_flags(IntPtr field);

        // mono_type_get_type(MonoType*) returns int (MonoTypeEnum)
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int d_mono_type_get_type(IntPtr type);

        // mono_type_get_class(MonoType*) returns MonoClass*
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr d_mono_type_get_class(IntPtr type);

        private static d_mono_class_get_fields _monoClassGetFields;
        private static d_mono_field_get_name _monoFieldGetName;
        private static d_mono_class_num_fields _monoClassNumFields;
        private static d_mono_field_get_type _monoFieldGetType;
        private static d_mono_field_get_flags _monoFieldGetFlags;
        private static d_mono_type_get_type _monoTypeGetType;
        private static d_mono_type_get_class _monoTypeGetClass;

        // mono_class_get_flags(MonoClass*) returns uint (TypeAttributes)
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint d_mono_class_get_flags(IntPtr klass);

        // mono_class_get_image(MonoClass*) returns MonoImage*
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr d_mono_class_get_image(IntPtr klass);

        // mono_class_is_valuetype(MonoClass*) returns mono_bool
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int d_mono_class_is_valuetype(IntPtr klass);

        // mono_image_get_name(MonoImage*) returns const char*
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr d_mono_image_get_name(IntPtr image);

        private static d_mono_class_get_flags _monoClassGetFlags;
        private static d_mono_class_get_image _monoClassGetImage;
        private static d_mono_class_is_valuetype _monoClassIsValuetype;
        private static d_mono_image_get_name _monoImageGetName;

        private static void InitMonoClassMetaHelpers()
        {
            if (_monoClassGetFlags != null) return;
            IntPtr monoModule = GetMonoModule();
            if (monoModule == IntPtr.Zero) return;

            IntPtr p1 = GetProcAddress(monoModule, "mono_class_get_flags");
            IntPtr p2 = GetProcAddress(monoModule, "mono_class_get_image");
            IntPtr p3 = GetProcAddress(monoModule, "mono_class_is_valuetype");
            IntPtr p4 = GetProcAddress(monoModule, "mono_image_get_name");
            if (p1 != IntPtr.Zero)
                _monoClassGetFlags = (d_mono_class_get_flags)Marshal.GetDelegateForFunctionPointer(
                    p1, typeof(d_mono_class_get_flags));
            if (p2 != IntPtr.Zero)
                _monoClassGetImage = (d_mono_class_get_image)Marshal.GetDelegateForFunctionPointer(
                    p2, typeof(d_mono_class_get_image));
            if (p3 != IntPtr.Zero)
                _monoClassIsValuetype = (d_mono_class_is_valuetype)Marshal.GetDelegateForFunctionPointer(
                    p3, typeof(d_mono_class_is_valuetype));
            if (p4 != IntPtr.Zero)
                _monoImageGetName = (d_mono_image_get_name)Marshal.GetDelegateForFunctionPointer(
                    p4, typeof(d_mono_image_get_name));
        }

        private static void InitMonoFieldHelpers()
        {
            if (_monoClassGetFields != null) return;
            IntPtr monoModule = GetMonoModule();
            if (monoModule == IntPtr.Zero) return;

            IntPtr p1 = GetProcAddress(monoModule, "mono_class_get_fields");
            IntPtr p2 = GetProcAddress(monoModule, "mono_field_get_name");
            IntPtr p3 = GetProcAddress(monoModule, "mono_class_num_fields");
            IntPtr p4 = GetProcAddress(monoModule, "mono_field_get_type");
            IntPtr p5 = GetProcAddress(monoModule, "mono_field_get_flags");
            IntPtr p6 = GetProcAddress(monoModule, "mono_type_get_type");
            IntPtr p7 = GetProcAddress(monoModule, "mono_type_get_class");
            if (p1 != IntPtr.Zero)
                _monoClassGetFields = (d_mono_class_get_fields)Marshal.GetDelegateForFunctionPointer(
                    p1, typeof(d_mono_class_get_fields));
            if (p2 != IntPtr.Zero)
                _monoFieldGetName = (d_mono_field_get_name)Marshal.GetDelegateForFunctionPointer(
                    p2, typeof(d_mono_field_get_name));
            if (p3 != IntPtr.Zero)
                _monoClassNumFields = (d_mono_class_num_fields)Marshal.GetDelegateForFunctionPointer(
                    p3, typeof(d_mono_class_num_fields));
            if (p4 != IntPtr.Zero)
                _monoFieldGetType = (d_mono_field_get_type)Marshal.GetDelegateForFunctionPointer(
                    p4, typeof(d_mono_field_get_type));
            if (p5 != IntPtr.Zero)
                _monoFieldGetFlags = (d_mono_field_get_flags)Marshal.GetDelegateForFunctionPointer(
                    p5, typeof(d_mono_field_get_flags));
            if (p6 != IntPtr.Zero)
                _monoTypeGetType = (d_mono_type_get_type)Marshal.GetDelegateForFunctionPointer(
                    p6, typeof(d_mono_type_get_type));
            if (p7 != IntPtr.Zero)
                _monoTypeGetClass = (d_mono_type_get_class)Marshal.GetDelegateForFunctionPointer(
                    p7, typeof(d_mono_type_get_class));
        }

        // Enumerates native fields for each class in the parent chain (up to 5 levels).
        private static void DiagNativeFieldsInChain(IntPtr pClassFVRObject)
        {
            InitMonoClassHelpers();
            InitMonoFieldHelpers();
            if (_monoClassGetFields == null || _monoFieldGetName == null) return;
            if (_monoClassGetParent == null || _monoClassGetName == null) return;

            try
            {
                IntPtr cur = pClassFVRObject;
                int depth = 0;
                while (cur != IntPtr.Zero && depth < 5)
                {
                    string className = Marshal.PtrToStringAnsi(_monoClassGetName(cur));
                    int numFields = (_monoClassNumFields != null) ? _monoClassNumFields(cur) : -1;

                    // Enumerate fields with detailed type info
                    var fieldDetails = new List<string>();
                    IntPtr iter = IntPtr.Zero;
                    int safety = 0;
                    while (safety < 200)
                    {
                        IntPtr field = _monoClassGetFields(cur, ref iter);
                        if (field == IntPtr.Zero) break;
                        IntPtr namep = _monoFieldGetName(field);
                        string fname = (namep != IntPtr.Zero) ? Marshal.PtrToStringAnsi(namep) : "?";

                        string typeInfo = "";
                        if (_monoFieldGetType != null)
                        {
                            IntPtr mtype = _monoFieldGetType(field);
                            if (mtype != IntPtr.Zero && _monoTypeGetType != null)
                            {
                                int typeEnum = _monoTypeGetType(mtype);
                                string typeName = (typeEnum >= 0 && typeEnum < _monoTypeNames.Length)
                                    ? _monoTypeNames[typeEnum] : string.Format("?{0}", typeEnum);
                                typeInfo = typeName;

                                // For VALUETYPE and CLASS, get the class name
                                if ((typeEnum == 17 || typeEnum == 18) && _monoTypeGetClass != null)
                                {
                                    IntPtr tClass = _monoTypeGetClass(mtype);
                                    if (tClass != IntPtr.Zero)
                                    {
                                        string tName = Marshal.PtrToStringAnsi(_monoClassGetName(tClass));
                                        string tNs = "";
                                        if (_monoClassGetNamespace != null)
                                            tNs = Marshal.PtrToStringAnsi(_monoClassGetNamespace(tClass));
                                        typeInfo += string.Format("({0}.{1})", tNs, tName);
                                    }
                                }
                            }
                        }

                        string flagInfo = "";
                        if (_monoFieldGetFlags != null)
                        {
                            uint flags = _monoFieldGetFlags(field);
                            // FieldAttributes: 0x0001=FieldAccessMask, 0x0006=Public, 0x0001=Private
                            // 0x0010=Static, 0x0040=InitOnly, 0x0080=NotSerialized
                            bool isPublic = (flags & 0x7) == 0x6;
                            bool isPrivate = (flags & 0x7) == 0x1;
                            bool isStatic = (flags & 0x10) != 0;
                            bool isNotSerialized = (flags & 0x80) != 0;
                            flagInfo = string.Format("flags=0x{0:X}", flags);
                            if (isPublic) flagInfo += "(pub)";
                            if (isPrivate) flagInfo += "(priv)";
                            if (isStatic) flagInfo += "(STATIC)";
                            if (isNotSerialized) flagInfo += "(NOTSERIALIZED)";
                        }

                        fieldDetails.Add(string.Format("{0}:{1}:{2}", fname, typeInfo, flagInfo));
                        safety++;
                    }

                    Log(string.Format("NativeFields[{0}] {1}: numFields={2} fields=[{3}]",
                        depth, className, numFields, string.Join(", ", fieldDetails.ToArray())));

                    cur = _monoClassGetParent(cur);
                    depth++;
                }
            }
            catch (Exception ex)
            {
                Log("DiagNativeFields error: " + ex.Message);
            }
        }

        // Returns a diagnostic string describing FVRObject's MonoScriptCache native state.
        private static string GetFVRObjectCSDState()
        {
            try
            {
                if (_cachedPtrField == null) return "no_cachedPtr";
                FieldInfo cachedPtrField = _cachedPtrField;

                string[] allPaths = AssetDatabase.GetAllAssetPaths();
                foreach (string path in allPaths)
                {
                    if (!path.EndsWith(".object.asset")) continue;
                    UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath(path, typeof(UnityEngine.Object));
                    if (asset == null || !(asset is ScriptableObject)) continue;

                    MonoScript ms = MonoScript.FromScriptableObject(asset as ScriptableObject);
                    if (ms == null) continue;

                    IntPtr nMS = (IntPtr)cachedPtrField.GetValue(ms);
                    if (nMS == IntPtr.Zero) return "null_nMS";

                    IntPtr cache = Marshal.ReadIntPtr(new IntPtr(nMS.ToInt64() + MonoScriptCacheOffset));
                    if (cache == IntPtr.Zero) return "null_cache";

                    IntPtr pClass = Marshal.ReadIntPtr(new IntPtr(cache.ToInt64() + CacheClassOffset));
                    IntPtr csd = Marshal.ReadIntPtr(new IntPtr(cache.ToInt64() + CacheSerDataOffset));

                    return string.Format("cache=0x{0:X} pClass=0x{1:X} csd={2}",
                        cache.ToInt64(), pClass.ToInt64(),
                        csd == IntPtr.Zero ? "NULL" : string.Format("0x{0:X}", csd.ToInt64()));
                }
                return "no_object_asset";
            }
            catch (Exception ex) { return "error:" + ex.Message; }
        }

        private static string GetFirstObjectAssetPath()
        {
            string[] allPaths = AssetDatabase.GetAllAssetPaths();
            foreach (string path in allPaths)
                if (path.EndsWith(".object.asset"))
                    return path;
            return null;
        }

        private static void DiagGatekeeperForAnvilFields(IntPtr pClassFVRObject)
        {
            InitMonoClassHelpers();
            InitMonoFieldHelpers();
            InitMonoClassMetaHelpers();
            if (_monoClassGetParent == null || _monoClassGetFields == null) return;

            try
            {
                // Get AnvilAsset class from FVRObject's parent
                IntPtr pAnvilAsset = _monoClassGetParent(pClassFVRObject);
                if (pAnvilAsset == IntPtr.Zero) { Log("DiagGatekeeper: AnvilAsset parent is NULL"); return; }

                string anvilName = Marshal.PtrToStringAnsi(_monoClassGetName(pAnvilAsset));
                Log(string.Format("DiagGatekeeper: Checking AnvilAsset (0x{0:X}) = {1}", pAnvilAsset.ToInt64(), anvilName));

                // For each field in AnvilAsset, check its type's gatekeeper conditions
                IntPtr iter = IntPtr.Zero;
                while (true)
                {
                    IntPtr field = _monoClassGetFields(pAnvilAsset, ref iter);
                    if (field == IntPtr.Zero) break;

                    string fname = Marshal.PtrToStringAnsi(_monoFieldGetName(field));
                    IntPtr mtype = _monoFieldGetType(field);
                    if (mtype == IntPtr.Zero) { Log(string.Format("  {0}: null MonoType", fname)); continue; }

                    int typeEnum = _monoTypeGetType(mtype);
                    // Only check VALUETYPE(17) and CLASS(18) and GENERICINST(21) - the complex types
                    if (typeEnum != 17 && typeEnum != 18 && typeEnum != 21) {
                        Log(string.Format("  {0}: simple type ({1}), passes gatekeeper", fname, typeEnum));
                        continue;
                    }

                    IntPtr fieldClass = IntPtr.Zero;
                    if (_monoTypeGetClass != null && (typeEnum == 17 || typeEnum == 18))
                        fieldClass = _monoTypeGetClass(mtype);

                    if (fieldClass == IntPtr.Zero) {
                        Log(string.Format("  {0}: type={1} but can't get MonoClass", fname, typeEnum));
                        continue;
                    }

                    string fieldClassName = Marshal.PtrToStringAnsi(_monoClassGetName(fieldClass));
                    string fieldClassNs = "";
                    if (_monoClassGetNamespace != null)
                        fieldClassNs = Marshal.PtrToStringAnsi(_monoClassGetNamespace(fieldClass));

                    // Check 1: [Serializable] flag (0x2000)
                    uint typeFlags = 0;
                    bool hasSerializable = false;
                    if (_monoClassGetFlags != null)
                    {
                        typeFlags = _monoClassGetFlags(fieldClass);
                        hasSerializable = (typeFlags & 0x2000) != 0;
                    }

                    // Check 2: abstract/interface
                    bool isAbstract = false;
                    bool isInterface = false;
                    try
                    {
                        Type managedType = null;
                        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                        {
                            managedType = asm.GetType(fieldClassNs + "." + fieldClassName, false);
                            if (managedType == null)
                                managedType = asm.GetType(fieldClassName, false);
                            if (managedType != null) break;
                        }
                        if (managedType != null)
                        {
                            isAbstract = managedType.IsAbstract;
                            isInterface = managedType.IsInterface;
                        }
                    }
                    catch (Exception) { }

                    // Check 3: MonoImage pointer comparison
                    string imageName = "unknown";
                    string imagePtr = "null";
                    string fvrImagePtr = "null";
                    if (_monoClassGetImage != null)
                    {
                        IntPtr img = _monoClassGetImage(fieldClass);
                        imagePtr = string.Format("0x{0:X}", img.ToInt64());
                        if (img != IntPtr.Zero && _monoImageGetName != null)
                        {
                            IntPtr imgNameP = _monoImageGetName(img);
                            if (imgNameP != IntPtr.Zero)
                                imageName = Marshal.PtrToStringAnsi(imgNameP);
                        }

                        // Compare with FVRObject's MonoImage (should be same DLL)
                        IntPtr fvrImg = _monoClassGetImage(pClassFVRObject);
                        fvrImagePtr = string.Format("0x{0:X}", fvrImg.ToInt64());

                        // Also check first enum type's image for comparison
                        IntPtr firstEnumImg = IntPtr.Zero;
                        IntPtr fvrIter = IntPtr.Zero;
                        while (true)
                        {
                            IntPtr fvrField = _monoClassGetFields(pClassFVRObject, ref fvrIter);
                            if (fvrField == IntPtr.Zero) break;
                            IntPtr fvrMType = _monoFieldGetType(fvrField);
                            if (fvrMType == IntPtr.Zero) continue;
                            int fvrTypeEnum = _monoTypeGetType(fvrMType);
                            if (fvrTypeEnum == 17 && _monoTypeGetClass != null)
                            {
                                IntPtr enumClass = _monoTypeGetClass(fvrMType);
                                if (enumClass != IntPtr.Zero)
                                {
                                    firstEnumImg = _monoClassGetImage(enumClass);
                                    string enumName = Marshal.PtrToStringAnsi(_monoClassGetName(enumClass));
                                    Log(string.Format("    FVRObject enum {0} image=0x{1:X}",
                                        enumName, firstEnumImg.ToInt64()));
                                    break;
                                }
                            }
                        }
                    }

                    Log(string.Format(
                        "  {0}: type={1} class={2}.{3} flags=0x{4:X} [Serializable]={5} abstract={6} interface={7} image={8} imgPtr={9} fvrImgPtr={10}",
                        fname, typeEnum, fieldClassNs, fieldClassName,
                        typeFlags, hasSerializable, isAbstract, isInterface, imageName, imagePtr, fvrImagePtr));
                }
            }
            catch (Exception ex)
            {
                Log("DiagGatekeeper error: " + ex.Message);
            }
        }

        // ── Native gatekeeper (sub_140E3F670) direct invocation ─────────────
        // RVA 0xE3F670 — Takes MonoClass*, returns byte (0=reject, 1=accept).
        // Checks [Serializable], not abstract, not interface, not corlib,
        // MonoManager::GetAssemblyIndexFromImage != -1.

        private const long GatekeeperRVA = 0xE3F670;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate byte d_NativeGatekeeper(IntPtr monoClass);

        private static d_NativeGatekeeper _nativeGatekeeper;

        private static void InitNativeGatekeeper()
        {
            if (_nativeGatekeeper != null) return;
            IntPtr unityBase = GetUnityModule();
            if (unityBase == IntPtr.Zero) { Log("InitNativeGatekeeper: Unity.exe not found"); return; }

            IntPtr funcAddr = new IntPtr(unityBase.ToInt64() + GatekeeperRVA);
            _nativeGatekeeper = (d_NativeGatekeeper)Marshal.GetDelegateForFunctionPointer(
                funcAddr, typeof(d_NativeGatekeeper));
        }

        // Directly calls native gatekeeper (sub_140E3F670) for Anvil.AssetID and each sub-field.
        private static void DiagCallNativeGatekeeper()
        {
            InitMonoClassHelpers();
            InitMonoFieldHelpers();
            InitMonoClassMetaHelpers();
            InitNativeGatekeeper();

            if (_nativeGatekeeper == null) { Log("DIAG-GK: gatekeeper not initialized"); return; }
            if (_monoClassGetParent == null || _monoClassGetFields == null) return;

            try
            {
                // Get FVRObject's MonoClass* from its MonoScript
                if (_cachedPtrField == null) { Log("DIAG-GK: no m_CachedPtr"); return; }
                FieldInfo cachedPtrField = _cachedPtrField;

                string[] allPaths = AssetDatabase.GetAllAssetPaths();
                IntPtr pClassFVRObject = IntPtr.Zero;
                foreach (string path in allPaths)
                {
                    if (!path.EndsWith(".object.asset")) continue;
                    UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath(path, typeof(UnityEngine.Object));
                    if (asset == null || !(asset is ScriptableObject)) continue;
                    MonoScript ms = MonoScript.FromScriptableObject(asset as ScriptableObject);
                    if (ms == null) continue;
                    IntPtr nMS = (IntPtr)cachedPtrField.GetValue(ms);
                    if (nMS == IntPtr.Zero) continue;
                    IntPtr cache = Marshal.ReadIntPtr(new IntPtr(nMS.ToInt64() + MonoScriptCacheOffset));
                    if (cache == IntPtr.Zero) continue;
                    pClassFVRObject = Marshal.ReadIntPtr(new IntPtr(cache.ToInt64() + CacheClassOffset));
                    break;
                }

                if (pClassFVRObject == IntPtr.Zero) { Log("DIAG-GK: could not find FVRObject pClass"); return; }

                // Test gatekeeper on FVRObject itself
                byte gkFVRObject = _nativeGatekeeper(pClassFVRObject);
                Log(string.Format("DIAG-GK: FVRObject(0x{0:X}) gatekeeper={1}",
                    pClassFVRObject.ToInt64(), gkFVRObject));

                // Walk to AnvilAsset
                IntPtr pAnvilAsset = _monoClassGetParent(pClassFVRObject);
                if (pAnvilAsset == IntPtr.Zero) { Log("DIAG-GK: AnvilAsset parent is NULL"); return; }

                byte gkAnvilAsset = _nativeGatekeeper(pAnvilAsset);
                string anvilName = Marshal.PtrToStringAnsi(_monoClassGetName(pAnvilAsset));
                Log(string.Format("DIAG-GK: {0}(0x{1:X}) gatekeeper={2}",
                    anvilName, pAnvilAsset.ToInt64(), gkAnvilAsset));

                // Find m_anvilPrefab field in AnvilAsset
                IntPtr pAssetIDClass = IntPtr.Zero;
                IntPtr iter = IntPtr.Zero;
                while (true)
                {
                    IntPtr field = _monoClassGetFields(pAnvilAsset, ref iter);
                    if (field == IntPtr.Zero) break;
                    string fname = Marshal.PtrToStringAnsi(_monoFieldGetName(field));
                    IntPtr mtype = _monoFieldGetType(field);
                    if (mtype == IntPtr.Zero) continue;
                    int typeEnum = _monoTypeGetType(mtype);

                    // For ALL fields, log gatekeeper result if it's a complex type
                    if (typeEnum == 17 || typeEnum == 18)
                    {
                        IntPtr fieldClass = _monoTypeGetClass(mtype);
                        if (fieldClass != IntPtr.Zero)
                        {
                            byte gk = _nativeGatekeeper(fieldClass);
                            string cn = Marshal.PtrToStringAnsi(_monoClassGetName(fieldClass));
                            Log(string.Format("DIAG-GK: AnvilAsset.{0} type={1}({2}) gatekeeper={3}",
                                fname, cn, typeEnum, gk));

                            if (fname == "m_anvilPrefab")
                                pAssetIDClass = fieldClass;
                        }
                    }
                }

                if (pAssetIDClass == IntPtr.Zero) { Log("DIAG-GK: AssetID class not found"); return; }

                // Enumerate AssetID's own sub-fields and test gatekeeper on each
                Log(string.Format("DIAG-GK: Enumerating AssetID(0x{0:X}) sub-fields:",
                    pAssetIDClass.ToInt64()));

                // Also walk AssetID's parent chain
                string assetIdChain = WalkNativeClassChain(pAssetIDClass);
                Log("DIAG-GK: AssetID chain: " + assetIdChain);

                IntPtr iter2 = IntPtr.Zero;
                while (true)
                {
                    IntPtr field = _monoClassGetFields(pAssetIDClass, ref iter2);
                    if (field == IntPtr.Zero) break;
                    string fname = Marshal.PtrToStringAnsi(_monoFieldGetName(field));
                    IntPtr mtype = _monoFieldGetType(field);
                    uint fflags = _monoFieldGetFlags(field);
                    int typeEnum = (mtype != IntPtr.Zero && _monoTypeGetType != null)
                        ? _monoTypeGetType(mtype) : -1;
                    string typeName = (typeEnum >= 0 && typeEnum < _monoTypeNames.Length)
                        ? _monoTypeNames[typeEnum] : string.Format("?{0}", typeEnum);

                    string classInfo = "";
                    string gkInfo = "";
                    if ((typeEnum == 17 || typeEnum == 18) && _monoTypeGetClass != null)
                    {
                        IntPtr subClass = _monoTypeGetClass(mtype);
                        if (subClass != IntPtr.Zero)
                        {
                            string sn = Marshal.PtrToStringAnsi(_monoClassGetName(subClass));
                            string sns = (_monoClassGetNamespace != null)
                                ? Marshal.PtrToStringAnsi(_monoClassGetNamespace(subClass)) : "";
                            classInfo = string.Format(" class={0}.{1}", sns, sn);

                            byte gk = _nativeGatekeeper(subClass);
                            gkInfo = string.Format(" gatekeeper={0}", gk);

                            // Also check image
                            if (_monoClassGetImage != null && _monoImageGetName != null)
                            {
                                IntPtr img = _monoClassGetImage(subClass);
                                IntPtr imgN = _monoImageGetName(img);
                                string imgName = (imgN != IntPtr.Zero)
                                    ? Marshal.PtrToStringAnsi(imgN) : "null";
                                classInfo += string.Format(" image={0}", imgName);
                            }
                        }
                    }

                    Log(string.Format("DIAG-GK:   {0}: {1} flags=0x{2:X}{3}{4}",
                        fname, typeName, fflags, classInfo, gkInfo));
                }
            }
            catch (Exception ex)
            {
                Log("DIAG-GK error: " + ex.ToString());
            }
        }

        // ── Per-step gatekeeper decomposition ──────────────────────────
        // Calls GetScriptingManager and GetAssemblyIndexFromImage to check
        // exactly which gatekeeper step fails.

        private const long GetScriptingManagerRVA = 0x106CD80;
        private const long GetAssemblyIndexFromImageRVA = 0x14C25D0;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr d_GetScriptingManager();

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate int d_GetAssemblyIndexFromImage(IntPtr thisMonoManager, IntPtr monoImage);

        // Decomposes gatekeeper logic step-by-step for AssetID; also dumps the image array.
        private static void DiagGatekeeperStepByStep()
        {
            InitMonoClassHelpers();
            InitMonoFieldHelpers();
            InitMonoClassMetaHelpers();

            try
            {
                IntPtr unityBase = GetUnityModule();
                if (unityBase == IntPtr.Zero) { Log("DIAG-GKSTEP: Unity not found"); return; }

                // Get GetScriptingManager
                IntPtr gsmAddr = new IntPtr(unityBase.ToInt64() + GetScriptingManagerRVA);
                var getScriptingManager = (d_GetScriptingManager)Marshal.GetDelegateForFunctionPointer(
                    gsmAddr, typeof(d_GetScriptingManager));

                // Get GetAssemblyIndexFromImage
                IntPtr gaifiAddr = new IntPtr(unityBase.ToInt64() + GetAssemblyIndexFromImageRVA);
                var getAssemblyIndexFromImage = (d_GetAssemblyIndexFromImage)Marshal.GetDelegateForFunctionPointer(
                    gaifiAddr, typeof(d_GetAssemblyIndexFromImage));

                // Get MonoManager
                IntPtr monoManager = getScriptingManager();
                Log(string.Format("DIAG-GKSTEP: MonoManager=0x{0:X}", monoManager.ToInt64()));

                // Dump the image array (offset 520 = qword[65], offset 528 = qword[66])
                IntPtr arrayStart = Marshal.ReadIntPtr(new IntPtr(monoManager.ToInt64() + 520));
                IntPtr arrayEnd = Marshal.ReadIntPtr(new IntPtr(monoManager.ToInt64() + 528));
                long count = (arrayEnd.ToInt64() - arrayStart.ToInt64()) / 8;
                Log(string.Format("DIAG-GKSTEP: Image array: start=0x{0:X} end=0x{1:X} count={2}",
                    arrayStart.ToInt64(), arrayEnd.ToInt64(), count));

                // List all images in the array
                for (long i = 0; i < count && i < 100; i++)
                {
                    IntPtr img = Marshal.ReadIntPtr(new IntPtr(arrayStart.ToInt64() + i * 8));
                    string imgName = "null";
                    if (img != IntPtr.Zero && _monoImageGetName != null)
                    {
                        IntPtr namePtr = _monoImageGetName(img);
                        if (namePtr != IntPtr.Zero)
                            imgName = Marshal.PtrToStringAnsi(namePtr);
                    }
                    Log(string.Format("DIAG-GKSTEP:   imageArray[{0}]: 0x{1:X} = {2}", i, img.ToInt64(), imgName));
                }

                // Now get AssetID's MonoClass* and check each gatekeeper step
                if (_cachedPtrField == null) return;
                FieldInfo cachedPtrField = _cachedPtrField;

                IntPtr pClassFVRObject = IntPtr.Zero;
                foreach (string path in AssetDatabase.GetAllAssetPaths())
                {
                    if (!path.EndsWith(".object.asset")) continue;
                    UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath(path, typeof(UnityEngine.Object));
                    if (asset == null || !(asset is ScriptableObject)) continue;
                    MonoScript ms = MonoScript.FromScriptableObject(asset as ScriptableObject);
                    if (ms == null) continue;
                    IntPtr nMS = (IntPtr)cachedPtrField.GetValue(ms);
                    if (nMS == IntPtr.Zero) continue;
                    IntPtr cache = Marshal.ReadIntPtr(new IntPtr(nMS.ToInt64() + MonoScriptCacheOffset));
                    if (cache == IntPtr.Zero) continue;
                    pClassFVRObject = Marshal.ReadIntPtr(new IntPtr(cache.ToInt64() + CacheClassOffset));
                    break;
                }
                if (pClassFVRObject == IntPtr.Zero) return;

                // Get AssetID's MonoClass*
                IntPtr pAnvilAsset = _monoClassGetParent(pClassFVRObject);
                if (pAnvilAsset == IntPtr.Zero) return;

                IntPtr pAssetIDClass = IntPtr.Zero;
                IntPtr iter = IntPtr.Zero;
                while (true)
                {
                    IntPtr field = _monoClassGetFields(pAnvilAsset, ref iter);
                    if (field == IntPtr.Zero) break;
                    string fname = Marshal.PtrToStringAnsi(_monoFieldGetName(field));
                    if (fname == "m_anvilPrefab")
                    {
                        IntPtr mtype = _monoFieldGetType(field);
                        if (mtype != IntPtr.Zero && _monoTypeGetClass != null)
                            pAssetIDClass = _monoTypeGetClass(mtype);
                        break;
                    }
                }

                if (pAssetIDClass == IntPtr.Zero) { Log("DIAG-GKSTEP: AssetID not found"); return; }

                // Step 1: scripting_class_get_flags → check 0x2000
                uint flags = _monoClassGetFlags(pAssetIDClass);
                bool hasSerializable = (flags & 0x2000) != 0;
                Log(string.Format("DIAG-GKSTEP: AssetID flags=0x{0:X} [Serializable]={1}", flags, hasSerializable));

                // Step 2/3: abstract/interface — use managed reflection
                Type assetIDType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    assetIDType = asm.GetType("Anvil.AssetID", false);
                    if (assetIDType != null) break;
                }

                if (assetIDType != null)
                {
                    Log(string.Format("DIAG-GKSTEP: AssetID abstract={0} interface={1} isValueType={2}",
                        assetIDType.IsAbstract, assetIDType.IsInterface, assetIDType.IsValueType));
                }

                // Step 4: image != corlib
                IntPtr assetIDImage = _monoClassGetImage(pAssetIDClass);
                string assetIDImageName = "null";
                if (assetIDImage != IntPtr.Zero && _monoImageGetName != null)
                {
                    IntPtr namePtr = _monoImageGetName(assetIDImage);
                    if (namePtr != IntPtr.Zero)
                        assetIDImageName = Marshal.PtrToStringAnsi(namePtr);
                }
                Log(string.Format("DIAG-GKSTEP: AssetID image=0x{0:X} ({1})", assetIDImage.ToInt64(), assetIDImageName));

                // Step 5: GetAssemblyIndexFromImage
                int asmIndex = getAssemblyIndexFromImage(monoManager, assetIDImage);
                Log(string.Format("DIAG-GKSTEP: GetAssemblyIndexFromImage(AssetID)={0}", asmIndex));

                // Also check FVRObject's image for comparison
                IntPtr fvrImage = _monoClassGetImage(pClassFVRObject);
                int fvrAsmIndex = getAssemblyIndexFromImage(monoManager, fvrImage);
                string fvrImageName = "null";
                if (fvrImage != IntPtr.Zero && _monoImageGetName != null)
                {
                    IntPtr namePtr = _monoImageGetName(fvrImage);
                    if (namePtr != IntPtr.Zero)
                        fvrImageName = Marshal.PtrToStringAnsi(namePtr);
                }
                Log(string.Format("DIAG-GKSTEP: FVRObject image=0x{0:X} ({1}) asmIndex={2}",
                    fvrImage.ToInt64(), fvrImageName, fvrAsmIndex));
            }
            catch (Exception ex)
            {
                Log("DIAG-GKSTEP error: " + ex.ToString());
            }
        }

        // ── FIX: Inject H3VRCode MonoImage into MonoManager's assembly image array ──
        // Without this, GetAssemblyIndexFromImage returns -1 for H3VRCode types and the
        // serialization gatekeeper rejects Anvil types, making m_anvilPrefab vanish from CSD.
        private static bool EnsureH3VRCodeInMonoManagerImageArray()
        {
            InitMonoClassHelpers();
            InitMonoClassMetaHelpers();
            InitMonoFieldHelpers();

            try
            {
                IntPtr unityBase = GetUnityModule();
                if (unityBase == IntPtr.Zero) { Log("EnsureH3VRCode: Unity.exe not found"); return false; }

                // Get MonoManager singleton
                IntPtr gsmAddr = new IntPtr(unityBase.ToInt64() + GetScriptingManagerRVA);
                var getScriptingManager = (d_GetScriptingManager)Marshal.GetDelegateForFunctionPointer(
                    gsmAddr, typeof(d_GetScriptingManager));
                IntPtr monoManager = getScriptingManager();
                if (monoManager == IntPtr.Zero) { Log("EnsureH3VRCode: MonoManager is null"); return false; }

                // Read image array bounds (offset +520 = start, +528 = end)
                IntPtr arrayStart = Marshal.ReadIntPtr(new IntPtr(monoManager.ToInt64() + 520));
                IntPtr arrayEnd = Marshal.ReadIntPtr(new IntPtr(monoManager.ToInt64() + 528));
                long count = (arrayEnd.ToInt64() - arrayStart.ToInt64()) / 8;

                if (count <= 0 || count > 1000) { Log("EnsureH3VRCode: invalid array count=" + count); return false; }

                // Get H3VRCode-CSharp's MonoImage from a loaded FVRObject type
                IntPtr h3vrImage = IntPtr.Zero;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name == "H3VRCode-CSharp")
                    {
                        // Get a type from this assembly and use mono_class_get_image
                        Type anyType = asm.GetType("FistVR.FVRObject", false);
                        if (anyType != null && _monoClassGetImage != null)
                        {
                            IntPtr pClass = Marshal.ReadIntPtr(anyType.TypeHandle.Value);
                            h3vrImage = _monoClassGetImage(pClass);
                        }
                        break;
                    }
                }

                if (h3vrImage == IntPtr.Zero) { Log("EnsureH3VRCode: H3VRCode MonoImage not found"); return false; }

                // Verify it's the right image
                if (_monoImageGetName != null)
                {
                    IntPtr namePtr = _monoImageGetName(h3vrImage);
                    string imgName = (namePtr != IntPtr.Zero) ? Marshal.PtrToStringAnsi(namePtr) : "null";
                    Log(string.Format("EnsureH3VRCode: H3VRCode image=0x{0:X} name={1}", h3vrImage.ToInt64(), imgName));
                }

                // Check if already in array
                for (long i = 0; i < count; i++)
                {
                    IntPtr entry = Marshal.ReadIntPtr(new IntPtr(arrayStart.ToInt64() + i * 8));
                    if (entry == h3vrImage)
                    {
                        Log(string.Format("EnsureH3VRCode: already in array at index {0}", i));
                        return true;
                    }
                }

                // Find first null slot and write our image there
                for (long i = 0; i < count; i++)
                {
                    IntPtr entry = Marshal.ReadIntPtr(new IntPtr(arrayStart.ToInt64() + i * 8));
                    if (entry == IntPtr.Zero)
                    {
                        IntPtr slotAddr = new IntPtr(arrayStart.ToInt64() + i * 8);
                        Marshal.WriteIntPtr(slotAddr, h3vrImage);
                        Log(string.Format("EnsureH3VRCode: INJECTED at index {0} (0x{1:X})", i, slotAddr.ToInt64()));

                        // Verify via GetAssemblyIndexFromImage
                        IntPtr gaifiAddr = new IntPtr(unityBase.ToInt64() + GetAssemblyIndexFromImageRVA);
                        var getAsmIdx = (d_GetAssemblyIndexFromImage)Marshal.GetDelegateForFunctionPointer(
                            gaifiAddr, typeof(d_GetAssemblyIndexFromImage));
                        int idx = getAsmIdx(monoManager, h3vrImage);
                        Log(string.Format("EnsureH3VRCode: verified GetAssemblyIndexFromImage={0}", idx));

                        return idx != -1;
                    }
                }

                Log("EnsureH3VRCode: no null slot found in array!");
                return false;
            }
            catch (Exception ex)
            {
                Log("EnsureH3VRCode error: " + ex.ToString());
                return false;
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
                var src  = Path.Combine(_managedDir, name + ".dll");
                var dest = Path.Combine(_scriptAssembliesDir, name + ".dll");
                try { CopyDllIfChanged(src, dest); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("[ManagedPluginDomainFix] Copy failed: " + name + ".dll: " + ex.Message);
                }
            }

            // Signal the next domain that post-build TypeTree recovery is needed.
            // _pendingShutdownRestore is non-null ONLY after a build (set by Build.cs _afterEATI).
            if (_pendingShutdownRestore != null)
                EditorPrefs.SetBool(PostBuildFlagKey, true);
            _pendingShutdownRestore = null;
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

        private static void CopyDllIfChanged(string src, string dest)
        {
            if (!File.Exists(src)) return;
            if (File.Exists(dest) && FileContentsEqual(src, dest)) return;
            File.Copy(src, dest, true);
            File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(src));
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

        // Copies H3VRCode DLLs from Assets/Managed/ to Library/ScriptAssemblies/ if changed.
        // Called before every EATI to prevent stale standalone MVIDs causing null MonoScripts.
        internal static void EnsureH3VRCodeInScriptAssemblies()
        {
            if (!Directory.Exists(_managedDir)) return;
            EnsureDir();
            foreach (var name in new[] { MeatKit.AssemblyRename, MeatKit.AssemblyFirstpassRename })
            {
                var src  = Path.Combine(_managedDir, name + ".dll");
                var dest = Path.Combine(_scriptAssembliesDir, name + ".dll");
                try { CopyDllIfChanged(src, dest); }
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

        // Called by AssemblyImporter when a direct Write failed (file locked). Stages the copy
        // for ApplyPendingDllImports() on the next domain reload.
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
            // Editor platform must match Any — when the DLL is enabled, the editor compiler
            // needs it as a reference.  Hardcoding Editor:0 caused post-build CS0246 errors
            // because GetCompatibleWithEditorOrAnyPlatform excluded the DLL before ACSC ran.
            return "fileFormatVersion: 2\nguid: " + guid + "\nPluginImporter:\n" +
                   "  serializedVersion: 2\n  iconMap: {}\n  executionOrder: {}\n" +
                   "  isPreloaded: 0\n  isOverridable: 0\n  platformData:\n" +
                   "    data:\n      first:\n        Any:\n      second:\n" +
                   "        enabled: " + (anyEnabled ? "1" : "0") + "\n        settings: {}\n" +
                   "    data:\n      first:\n        Editor: Editor\n      second:\n" +
                   "        enabled: " + (anyEnabled ? "1" : "0") + "\n        settings:\n          DefaultValueInitialized: true\n" +
                   "    data:\n      first:\n        Windows Store Apps: WindowsStoreApps\n      second:\n" +
                   "        enabled: 0\n        settings:\n          CPU: AnyCPU\n" +
                   "  userData:\n  assetBundleName:\n  assetBundleVariant:";
        }

        // Tracks whether PrimeTypeTreesForBuild() has already primed in this domain.
        // Reset automatically on every domain reload (static field lifetime = domain lifetime).
        private static bool _typeTreePrimed = false;

        // Tracks whether a forced reimport of H3VRCode has been scheduled in this domain.
        // Prevents infinite loops if broken scripts cannot be repaired. Reset on domain reload.
        private static bool _forcedReimportScheduled = false;

        // Cross-domain guard: limits ForceUpdate reimport attempts across domain reloads.
        // _forcedReimportScheduled is per-domain; EditorPrefs survive domain reloads.
        // Reset automatically when scripts become healthy.
        private const string ReimportAttemptsPrefKey = "MPF_ReimportAttempts";
        private const int    MaxReimportAttempts = 2;

        // Logs GetClass() state of FVRObject (field presence, base class, assembly name) to BuildLog.
        internal static void DiagLogFVRObject(string tag)
        {
            try
            {
                foreach (MonoScript s in MonoImporter.GetAllRuntimeMonoScripts())
                {
                    if ((UnityEngine.Object)s == null || s.name != "FVRObject") continue;
                    string ap = AssetDatabase.GetAssetPath(s);
                    if (string.IsNullOrEmpty(ap) || ap.IndexOf("H3VRCode", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    System.Type cls = s.GetClass();
                    if (cls == null)
                    {
                        BuildLog.WriteLine("DIAG[" + tag + "] FVRObject.GetClass()=NULL");
                    }
                    else
                    {
                        // Check for backing field (private m_anvilPrefab)
                        FieldInfo fBacking = cls.GetField("m_anvilPrefab",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (fBacking == null && cls.BaseType != null)
                        {
                            fBacking = cls.BaseType.GetField("m_anvilPrefab",
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        }
                        
                        // Check for public property (anvilPrefab)
                        PropertyInfo p = cls.GetProperty("anvilPrefab",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (p == null && cls.BaseType != null)
                        {
                            p = cls.BaseType.GetProperty("anvilPrefab",
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        }
                        
                        // Check for field (public anvilPrefab)
                        FieldInfo f = cls.GetField("anvilPrefab",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (f == null && cls.BaseType != null)
                        {
                            f = cls.BaseType.GetField("anvilPrefab",
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        }
                        
                        string fieldStatus = (fBacking != null ? "FIELD_m_anvilPrefab" : 
                            (f != null ? "FIELD_anvilPrefab" : (p != null ? "PROPERTY_anvilPrefab" : "MISSING")));
                        BuildLog.WriteLine("DIAG[" + tag + "] FVRObject.GetClass()=" + cls.FullName +
                            " anvilPrefab=" + fieldStatus +
                            " baseClass=" + (cls.BaseType != null ? cls.BaseType.Name : "?") +
                            " asm=" + (cls.Assembly != null ? cls.Assembly.GetName().Name : "?"));
                    }
                    break;
                }
            }
            catch (Exception ex) { BuildLog.WriteLine("DIAG[" + tag + "] FVRObject check failed: " + ex.Message); }
        }

        /// <summary>
        // Calls MonoScript.Init() for all H3VRCode scripts to refresh the per-GUID TypeTree cache
        // before BuildAssetBundles. No-op after first call per domain (_typeTreePrimed guard).
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
                string h3vr   = MeatKit.AssemblyRename + ".dll";
                string h3vrFp = MeatKit.AssemblyFirstpassRename + ".dll";
                int primed = 0;
                // StartAssetEditing pauses the file-system watcher. After StopAssetEditing we
                // call AssetDatabase.SaveAssets() to write dirty MonoScript sub-assets to the
                // Library cache and clear their dirty flags. Without this, Unity serialises the
                // dirty flag on shutdown and on the next boot passes the DLL as a source file to
                // the C# compiler, crashing with:
                //   "Unable to find a suitable compiler for sources with extension 'dll'"
                // Note: EditorUtility.ClearDirty() does not exist in Unity 5.6 (added 2017.1).
                AssetDatabase.StartAssetEditing();
                try
                {
                    foreach (MonoScript script in MonoImporter.GetAllRuntimeMonoScripts())
                    {
                        if ((UnityEngine.Object)script == null) continue;
                        string assetPath = AssetDatabase.GetAssetPath(script);
                        if (string.IsNullOrEmpty(assetPath)) continue;
                        string file = Path.GetFileName(assetPath);
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

        // Byte offsets within the native MonoScript C++ object.
        // These are stable for Unity 5.6.7f1 x64 and verified by IDA Pro decompilation.
        private const int MonoScriptCacheOffset   = 216;  // MonoScript::m_ScriptCache (MonoScriptCache*)
        // Byte offsets within the native MonoScriptCache C++ object (size=0x90=144 bytes).
        private const int CacheClassOffset        = 8;    // MonoScriptCache::m_pClass (ScriptingClassPtr)
        private const int CacheSerDataOffset      = 136;  // MonoScriptCache::CachedSerializationData*
        // (TransferScriptingObject<GenerateTypeTreeTransfer> reads from CacheSerDataOffset and
        //  CacheClassOffset when generating the TypeTree for a MonoBehaviour component.)

        // UNUSED: Pre-collected MB/MonoScript pointer pairs, populated by PreCollectMBPatches().
        // Superseded by ReprimeSilentAfterEATI Part 2 (which builds its own scan inline).
        // Parallel lists: _preMBPtrs[i] is an instance whose MB+160 should track _preMSPtrs[i]+216.
        private static readonly List<IntPtr> _preMBPtrs = new List<IntPtr>();
        private static readonly List<IntPtr> _preMSPtrs = new List<IntPtr>();

        // UNUSED: Was intended to feed ReprimeSilentAfterEATI Part 2, but that method builds its
        // own MB scan instead. Kept for potential future use; safe to remove if not needed.
        internal static void PreCollectMBPatches()
        {
            _preMBPtrs.Clear();
            _preMSPtrs.Clear();
            FieldInfo cpf = _cachedPtrField;
            if (cpf == null)
            {
                BuildLog.WriteLine("PreCollectMBPatches: m_CachedPtr field not found");
                return;
            }
            try
            {
                foreach (MonoScript ms in MonoImporter.GetAllRuntimeMonoScripts())
                {
                    if ((UnityEngine.Object)ms == null) continue;
                    string ap = AssetDatabase.GetAssetPath(ms);
                    if (string.IsNullOrEmpty(ap)) continue;
                    if (!ap.StartsWith("Assets/Managed/", StringComparison.OrdinalIgnoreCase)) continue;
                    System.Type cls = ms.GetClass();
                    if (cls == null) continue;
                    IntPtr nMS = IntPtr.Zero;
                    try { nMS = (IntPtr)cpf.GetValue(ms); } catch (Exception) { }
                    if (nMS == IntPtr.Zero) continue;
                    UnityEngine.Object[] instances = null;
                    try { instances = Resources.FindObjectsOfTypeAll(cls); } catch (Exception) { }
                    if (instances == null) continue;
                    foreach (UnityEngine.Object obj in instances)
                    {
                        if ((UnityEngine.Object)obj == null) continue;
                        IntPtr nMB = IntPtr.Zero;
                        try { nMB = (IntPtr)cpf.GetValue(obj); } catch (Exception) { }
                        if (nMB == IntPtr.Zero) continue;
                        _preMBPtrs.Add(nMB);
                        _preMSPtrs.Add(nMS);
                    }
                }
                BuildLog.WriteLine("PreCollectMBPatches: collected=" + _preMBPtrs.Count + " instances");
            }
            catch (Exception ex)
            {
                BuildLog.WriteLine("PreCollectMBPatches failed: " + ex.Message);
            }
        }

        // Clears CachedSerializationData (cache+136) for all MBs and MonoScripts before EATI,
        // forcing TypeTree rebuild from live pClass. Fixes Build 2 anvilPrefab regression where
        // stale cache+136 (from startup EATI reading stub Library/metadata) gets reused.
        internal static void ReprimeMBCachesBeforeEATI()
        {
            try
            {
                if (_cachedPtrField == null) return;
                FieldInfo cachedPtrField = _cachedPtrField;

                // ── Step 1: Build lookup of H3VRCode-CSharp MonoScript native pointers ─────
                // Used to identify which MBs are H3VRCode-derived (for extra MB+160 patching).
                var msLookup = new HashSet<IntPtr>();
                foreach (MonoScript ms in MonoImporter.GetAllRuntimeMonoScripts())
                {
                    if ((UnityEngine.Object)ms == null) continue;
                    string ap = AssetDatabase.GetAssetPath(ms);
                    if (string.IsNullOrEmpty(ap)) continue;
                    if (!ap.StartsWith("Assets/Managed/", StringComparison.OrdinalIgnoreCase)) continue;
                    IntPtr nMS = IntPtr.Zero;
                    try { nMS = (IntPtr)cachedPtrField.GetValue(ms); } catch (Exception) { }
                    if (nMS != IntPtr.Zero) msLookup.Add(nMS);
                }

                // ── Step 2: Scan ALL MonoBehaviours and clear their CachedSerializationData ──
                // Clearing cache+136 for EVERY component forces EATI to call BuildSerializationCacheFor
                // for each, which uses the live pClass (correct class from current domain).
                // This fixes the Build 2 regression where Domain B startup EATI populates
                // cache+136 from stale Assembly-CSharp Library/metadata (built from stub DLL).
                int fixed0 = 0, cleared = 0, alreadyOk = 0;
                string firstDiag = null;

                UnityEngine.Object[] allMBs = null;
                try { allMBs = Resources.FindObjectsOfTypeAll(typeof(MonoBehaviour)); }
                catch (Exception ex) { BuildLog.WriteLine("ReprimeMBCachesBeforeEATI: FindObjectsOfTypeAll failed: " + ex.Message); return; }

                foreach (UnityEngine.Object obj in allMBs)
                {
                    MonoBehaviour mb = obj as MonoBehaviour;
                    if (mb == null) continue;

                    IntPtr nMB = IntPtr.Zero;
                    try { nMB = (IntPtr)cachedPtrField.GetValue(obj); } catch (Exception) { }
                    if (nMB == IntPtr.Zero) continue;

                    try
                    {
                        // (a) For H3VRCode MBs: also patch MB+160 to match MonoScript+216
                        MonoScript ms2 = MonoScript.FromMonoBehaviour(mb);
                        if ((UnityEngine.Object)ms2 != null)
                        {
                            IntPtr nMS2 = IntPtr.Zero;
                            try { nMS2 = (IntPtr)cachedPtrField.GetValue(ms2); } catch (Exception) { }
                            if (nMS2 != IntPtr.Zero && msLookup.Contains(nMS2))
                            {
                                IntPtr correctCache = Marshal.ReadIntPtr(new IntPtr(nMS2.ToInt64() + MonoScriptCacheOffset));
                                if (correctCache != IntPtr.Zero)
                                {
                                    IntPtr pClass = Marshal.ReadIntPtr(new IntPtr(correctCache.ToInt64() + CacheClassOffset));
                                    if (pClass != IntPtr.Zero)
                                    {
                                        IntPtr mbCache = Marshal.ReadIntPtr(new IntPtr(nMB.ToInt64() + 160));
                                        if (firstDiag == null)
                                        {
                                            firstDiag = "PRE-EATI MB160: nMB=0x" + nMB.ToString("X") +
                                                        " MB+160=0x" + mbCache.ToString("X") +
                                                        " correctCache=0x" + correctCache.ToString("X") +
                                                        " pClass=0x" + pClass.ToString("X") +
                                                        " already_ok=" + (mbCache == correctCache);
                                        }
                                        if (mbCache != correctCache)
                                        {
                                            Marshal.WriteIntPtr(new IntPtr(nMB.ToInt64() + 160), correctCache);
                                            fixed0++;
                                        }
                                        else { alreadyOk++; }
                                    }
                                }
                            }
                        }

                        // (b) Clear CachedSerializationData (cache+136) from MB+160 cache for
                        //     ALL MonoBehaviours (H3VRCode AND custom scripts).
                        //     This forces EATI to rebuild from pClass (correct live class).
                        IntPtr mbCacheToCheck = Marshal.ReadIntPtr(new IntPtr(nMB.ToInt64() + 160));
                        if (mbCacheToCheck != IntPtr.Zero)
                        {
                            IntPtr serData = Marshal.ReadIntPtr(new IntPtr(mbCacheToCheck.ToInt64() + CacheSerDataOffset));
                            if (serData != IntPtr.Zero)
                            {
                                Marshal.WriteIntPtr(new IntPtr(mbCacheToCheck.ToInt64() + CacheSerDataOffset), IntPtr.Zero);
                                cleared++;
                            }
                        }
                    }
                    catch (Exception) { }
                }
                BuildLog.WriteLine(string.Format(
                    "ReprimeMBCachesBeforeEATI: scanned {0} MBs, patched MB+160={1}, alreadyOk={2}, clearedSerData(MB)={3}",
                    allMBs.Length, fixed0, alreadyOk, cleared));
                if (firstDiag != null) BuildLog.WriteLine(firstDiag);

                // ── Step 3: Clear MonoScript+216+136 for ALL MonoScripts ─────────────────
                // EATI uses the MonoScript's own cache (MonoScript+216) to check for a cached
                // TypeTree (cache+136 = CachedSerializationData). If non-null, it reuses the
                // stale TypeTree from Domain B startup EATI (which read from wrong Library/metadata).
                // The MB+160 cache clear above may not reach the MonoScript+216 cache (different ptr).
                // Clearing MonoScript+216+136 directly forces EATI to rebuild from pClass (correct).
                int msCleared = 0;
                foreach (MonoScript ms3 in MonoImporter.GetAllRuntimeMonoScripts())
                {
                    if ((UnityEngine.Object)ms3 == null) continue;
                    IntPtr nMS3 = IntPtr.Zero;
                    try { nMS3 = (IntPtr)cachedPtrField.GetValue(ms3); } catch (Exception) { }
                    if (nMS3 == IntPtr.Zero) continue;
                    try
                    {
                        IntPtr msCache = Marshal.ReadIntPtr(new IntPtr(nMS3.ToInt64() + MonoScriptCacheOffset));
                        if (msCache != IntPtr.Zero)
                        {
                            IntPtr serData = Marshal.ReadIntPtr(new IntPtr(msCache.ToInt64() + CacheSerDataOffset));
                            if (serData != IntPtr.Zero)
                            {
                                Marshal.WriteIntPtr(new IntPtr(msCache.ToInt64() + CacheSerDataOffset), IntPtr.Zero);
                                msCleared++;
                            }
                        }
                    }
                    catch (Exception) { }
                }
                BuildLog.WriteLine("ReprimeMBCachesBeforeEATI: cleared MonoScript+216+136 for " + msCleared + " scripts");
            }
            catch (Exception ex)
            {
                BuildLog.WriteLine("ReprimeMBCachesBeforeEATI failed: " + ex.Message);
            }
        }

        // Re-primes MonoScript caches after EATI by calling Init() for all Assets/Managed/ scripts.
        // Skips StartAssetEditing/SaveAssets to avoid import cycles mid-build.
        internal static void ReprimeSilentAfterEATI()
        {
            try
            {
                var initMethod = typeof(MonoScript).GetMethod("Init",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var getNsMethod = typeof(MonoScript).GetMethod("GetNamespace",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (initMethod == null || getNsMethod == null) return;

                // Used to read the native MonoScript pointer (m_CachedPtr) for cache-patching below.
                FieldInfo cachedPtrField = _cachedPtrField;

                // Ensure H3VRCode DLLs are in Library/ScriptAssemblies/ before calling Init().
                // Unity's native getClass(assemblyName, ...) resolves types from ScriptAssemblies/.
                // If the standalone compile that runs inside BuildAssetBundles has overwritten
                // H3VRCode-CSharp.dll in ScriptAssemblies/ with an empty stub (Anvil namespace
                // absent), Init() for classes like Anvil.AnvilPrefabSpawn would resolve to null
                // and produce an empty TypeTree, causing the anvilPrefab field to vanish from
                // the bundle. Calling EnsureH3VRCodeInScriptAssemblies here guarantees the
                // correct game DLL is present before any Init() call.
                EnsureH3VRCodeInScriptAssemblies();

                int primed = 0;
                foreach (MonoScript script in MonoImporter.GetAllRuntimeMonoScripts())
                {
                    if ((UnityEngine.Object)script == null) continue;
                    string assetPath = AssetDatabase.GetAssetPath(script);
                    if (string.IsNullOrEmpty(assetPath)) continue;
                    // Prime every script that lives under Assets/Managed/ (the plugin DLLs:
                    // H3VRCode-CSharp.dll, OtherLoader.dll, Sodalite.dll, etc.)
                    if (!assetPath.StartsWith("Assets/Managed/", StringComparison.OrdinalIgnoreCase)) continue;
                    string file = Path.GetFileName(assetPath);
                    string ns   = (string)getNsMethod.Invoke(script, null);

                    initMethod.Invoke(script, new object[] { "", script.name, ns, file, false });
                    // Clear CachedSerializationData (MonoScriptCache+136) AFTER Init() sets m_pClass.
                    // EATI may have populated CachedSerializationData from the stub DLL (which lacks
                    // anvilPrefab). TransferScriptingObject<GenerateTypeTreeTransfer> checks this field
                    // first; if non-null it uses the cached (stub) TypeTree and skips m_pClass entirely.
                    // Clearing it forces Unity to recompute the TypeTree fresh from the (now-correct)
                    // m_pClass on the next bundle serialization, ensuring anvilPrefab appears.
                    if (cachedPtrField != null)
                    {
                        IntPtr nMS = IntPtr.Zero;
                        try { nMS = (IntPtr)cachedPtrField.GetValue(script); } catch (Exception) { }
                        if (nMS != IntPtr.Zero)
                        {
                            try
                            {
                                IntPtr cache = Marshal.ReadIntPtr(new IntPtr(nMS.ToInt64() + MonoScriptCacheOffset));
                                if (cache != IntPtr.Zero)
                                {
                                    IntPtr oldSerData = Marshal.ReadIntPtr(new IntPtr(cache.ToInt64() + CacheSerDataOffset));
                                    if (oldSerData != IntPtr.Zero)
                                    {
                                        Marshal.WriteIntPtr(new IntPtr(cache.ToInt64() + CacheSerDataOffset), IntPtr.Zero);
                                    }
                                }
                            }
                            catch (Exception) { }
                        }
                    }
                    primed++;
                }
                BuildLog.WriteLine("ReprimeSilentAfterEATI: primed " + primed + " plugin-DLL scripts");

                // Spot-check: verify FVRObject resolved correctly and has the anvilPrefab field.
                DiagLogFVRObject("POST-EATI");

                // Spot-check: verify the H3VRCode DLL in ScriptAssemblies actually contains
                // the Anvil namespace (if not, Init() will resolve to null for AnvilPrefabSpawn).
                try
                {
                    string h3vrSA = Path.Combine(_scriptAssembliesDir, MeatKit.AssemblyRename + ".dll");
                    if (File.Exists(h3vrSA))
                    {
                        byte[] dllBytes = File.ReadAllBytes(h3vrSA);
                        bool hasAnvil = System.Text.Encoding.ASCII.GetString(dllBytes).IndexOf("AnvilPrefabSpawn", StringComparison.Ordinal) >= 0;
                        BuildLog.WriteLine("DIAG: SA/H3VRCode.dll AnvilPrefabSpawn=" + hasAnvil + " size=" + dllBytes.Length);
                    }
                    else
                    {
                        BuildLog.WriteLine("DIAG: SA/H3VRCode.dll NOT FOUND");
                    }
                }
                catch (Exception diagEx) { BuildLog.WriteLine("DIAG: SA-check failed: " + diagEx.Message); }

                // Part 2: patch MB+160 for ALL MonoBehaviour instances whose script
                // comes from Assets/Managed/ (H3VRCode, OtherLoader, etc.).
                // SAFETY: FindObjectsOfTypeAll(typeof(MonoBehaviour)) is safe — MonoBehaviour
                // is a plain Unity base type with no Anvil-referenced fields, so
                // compute_class_bitmap never crashes here (unlike FindObjectsOfTypeAll(H3VRCodeType)
                // which crashes because H3VRCode types have Anvil.AssetID struct fields with
                // string refs that lose their MonoClass* after domain reload).
                if (cachedPtrField != null)
                {
                    // Build lookup: native MonoScript ptr → native MonoScript ptr for managed scripts.
                    var msLookup = new HashSet<IntPtr>();
                    foreach (MonoScript ms2 in MonoImporter.GetAllRuntimeMonoScripts())
                    {
                        if ((UnityEngine.Object)ms2 == null) continue;
                        string ap2 = AssetDatabase.GetAssetPath(ms2);
                        if (string.IsNullOrEmpty(ap2)) continue;
                        if (!ap2.StartsWith("Assets/Managed/", StringComparison.OrdinalIgnoreCase)) continue;
                        IntPtr nMS2 = IntPtr.Zero;
                        try { nMS2 = (IntPtr)cachedPtrField.GetValue(ms2); } catch (Exception) { }
                        if (nMS2 != IntPtr.Zero) msLookup.Add(nMS2);
                    }

                    int mbPart2Fixed = 0;
                    string diagMB = null;
                    UnityEngine.Object[] allMBs = null;
                    try { allMBs = Resources.FindObjectsOfTypeAll(typeof(MonoBehaviour)); }
                    catch (Exception ex2) { BuildLog.WriteLine("ReprimeSilentAfterEATI Part2: FindObjectsOfTypeAll(MB) failed: " + ex2.Message); }

                    if (allMBs != null)
                    {
                        foreach (UnityEngine.Object obj2 in allMBs)
                        {
                            MonoBehaviour mb2 = obj2 as MonoBehaviour;
                            if (mb2 == null) continue;
                            MonoScript ms3 = MonoScript.FromMonoBehaviour(mb2);
                            if ((UnityEngine.Object)ms3 == null) continue;
                            IntPtr nMS3 = IntPtr.Zero;
                            try { nMS3 = (IntPtr)cachedPtrField.GetValue(ms3); } catch (Exception) { }
                            if (nMS3 == IntPtr.Zero || !msLookup.Contains(nMS3)) continue;
                            IntPtr nMB2 = IntPtr.Zero;
                            try { nMB2 = (IntPtr)cachedPtrField.GetValue(obj2); } catch (Exception) { }
                            if (nMB2 == IntPtr.Zero) continue;
                            try
                            {
                                IntPtr correctCache2 = Marshal.ReadIntPtr(new IntPtr(nMS3.ToInt64() + MonoScriptCacheOffset));
                                if (correctCache2 == IntPtr.Zero) continue;
                                IntPtr correctClass2 = Marshal.ReadIntPtr(new IntPtr(correctCache2.ToInt64() + CacheClassOffset));
                                if (correctClass2 == IntPtr.Zero) continue;
                                IntPtr mbCachePtr = Marshal.ReadIntPtr(new IntPtr(nMB2.ToInt64() + 160));
                                if (diagMB == null)
                                {
                                    diagMB = "DIAG[MB160] nMB=0x" + nMB2.ToString("X") +
                                             " MB+160=0x" + mbCachePtr.ToString("X") +
                                             " correctCache=0x" + correctCache2.ToString("X") +
                                             " correct=0x" + correctClass2.ToString("X") +
                                             " already_ok=" + (mbCachePtr == correctCache2);
                                }
                                if (mbCachePtr == correctCache2) continue;
                                Marshal.WriteIntPtr(new IntPtr(nMB2.ToInt64() + 160), correctCache2);
                                mbPart2Fixed++;
                            }
                            catch (Exception) { }
                        }
                        BuildLog.WriteLine("ReprimeSilentAfterEATI Part2: scanned " + allMBs.Length + " MBs, fixed=" + mbPart2Fixed);
                    }
                    if (diagMB != null) BuildLog.WriteLine(diagMB);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ManagedPluginDomainFix] ReprimeSilentAfterEATI failed: " + ex);
            }
        }

        // Snapshots Library/metadata files for H3VRCode DLLs (main + .info + .resource).
        // Other plugin DLLs are protected by the AHVTI hook and don't need backup.
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
                        // Simple Adler32 checksum for B1 vs B2 comparison (no System.Security dependency)
                        long s1 = 1L, s2 = 0L;
                        uint adlerMod = 65521;
                        int sampleN = Math.Min(backups[infoFile].Length, 262144);
                        for (int bi = 0; bi < sampleN; bi++) { s1 = (s1 + backups[infoFile][bi]) % adlerMod; s2 = (s2 + s1) % adlerMod; }
                        string infoChecksum = string.Format("{0:x8}", (s2 << 16) | s1);
                        BuildLog.WriteLine("BackupTypeTree: saved " + infoFile + " (" + backups[infoFile].Length + " bytes) check=" + infoChecksum);
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

        // Writes backed-up Library/metadata bytes back to disk after BuildAssetBundles.
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
                    EditorPrefs.DeleteKey(ReimportAttemptsPrefKey);  // reset for future fresh runs
                    Log("RepairH3VRCodeMonoScripts(" + caller + "): all " + total + " scripts healthy");
                    return;
                }

                Debug.LogWarning(string.Format(
                    "[ManagedPluginDomainFix] RepairH3VRCodeMonoScripts({2}): {0}/{1} H3VRCode scripts null — rebuilding ALL {1} scripts (stale m_pClass fix)",
                    broken, total, caller));

                IntPtr unityBase = GetModuleHandle("Unity.exe");
                if (unityBase == IntPtr.Zero) unityBase = GetUnityModule();
                if (unityBase == IntPtr.Zero)
                {
                    Debug.LogWarning("[ManagedPluginDomainFix] RepairH3VRCodeMonoScripts: Unity.exe module not found");
                    return;
                }

                IntPtr fnPtr = new IntPtr(unityBase.ToInt64() + RebuildFromAwakeRva);
                var rebuildFromAwake = (d_MonoScriptRebuildFromAwake)Marshal.GetDelegateForFunctionPointer(
                    fnPtr, typeof(d_MonoScriptRebuildFromAwake));

                if (_cachedPtrField == null) return;
                FieldInfo cachedPtrField = _cachedPtrField;

                // CRITICAL: Rebuild ALL scripts, not just null ones.
                // After a build + domain reload, ReprimeSilentAfterEATI (domain A) set m_pClass to
                // domain A's MonoClass pointers. When domain B loads, these become dangling.
                // RebuildFromAwake updates m_pClass to the current domain's MonoClass and clears
                // the stale CachedSerializationData.  Without this, TypeTree generation via stale
                // m_pClass produces a partial TypeTree (has FVRObject fields like Category, but
                // misses inherited AnvilAsset fields like m_anvilPrefab because the base class
                // pointer in the old MonoClass is dangling).
                int repaired = 0, skipped = 0;
                foreach (MonoScript s in MonoImporter.GetAllRuntimeMonoScripts())
                {
                    if ((UnityEngine.Object)s == null) continue;
                    string f = Path.GetFileName(AssetDatabase.GetAssetPath(s));
                    if (f != h3vr && f != h3vrFp) continue;

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

                // If repair was successful, discard any RequestScriptReload that was triggered
                // by RebuildFromAwake as an internal side effect. Those calls should NOT cause
                // another domain reload (the scripts are now healthy; no compilation is needed).
                if (stillBroken == 0)
                {
                    NativeHookManager.DiscardPendingScriptReload();
                    return;
                }

                // If RebuildFromAwake couldn't fix all null scripts, schedule a force-reimport.
                // However, only schedule it once per domain — if a prior reimport in this domain
                // also left scripts broken, scheduling another reimport would create an infinite
                // loop of domain reloads. AssetDatabase.ImportAsset uses Unity's GUID-based pipeline
                // which correctly re-binds m_Class without relying on assembly-name lookup.
                //
                // Discard any RequestScriptReload triggered by RebuildFromAwake as a side effect.
                // If we scheduled an ImportAsset below, that ImportAsset will cause its own domain
                // reload; the repair-triggered reload would be redundant and must be discarded.
                NativeHookManager.DiscardPendingScriptReload();
                int _reimportAttempts = EditorPrefs.GetInt(ReimportAttemptsPrefKey, 0);
                if (stillBroken > 0 && !_forcedReimportScheduled && _reimportAttempts < MaxReimportAttempts)
                {
                    EditorPrefs.SetInt(ReimportAttemptsPrefKey, _reimportAttempts + 1);
                    _forcedReimportScheduled = true;
                    string mainPath = "Assets/Managed/" + h3vr;
                    string fpPath   = "Assets/Managed/" + h3vrFp;
                    Debug.LogWarning(string.Format(
                        "[ManagedPluginDomainFix] RepairH3VRCodeMonoScripts({0}): {1} scripts still null — scheduling force reimport to restore inspector",
                        caller, stillBroken));
                    EditorApplication.delayCall += delegate
                    {
                        try
                        {
                            AssetDatabase.ImportAsset(mainPath, ImportAssetOptions.ForceUpdate);
                            AssetDatabase.ImportAsset(fpPath,   ImportAssetOptions.ForceUpdate);
                        }
                        catch (Exception reimportEx)
                        {
                            Debug.LogWarning("[ManagedPluginDomainFix] Force reimport failed: " + reimportEx.Message);
                        }
                    };
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ManagedPluginDomainFix] RepairH3VRCodeMonoScripts(" + caller + "): " + ex);
            }
        }

        // Cached delegate for hot-path calls from OnMonoScriptTransferWrite.
        private static d_MonoScriptRebuildFromAwake _rebuildFromAwakeDelegate;

        // Calls RebuildFromAwake + clears CachedSerializationData on a native MonoScript pointer
        // immediately before bundle serialization to ensure the TypeTree uses the live H3VRCode pClass.
        internal static void ReprimeSingleNativeScript(IntPtr nativePtr)
        {
            if (nativePtr == IntPtr.Zero) return;
            try
            {
                if (_rebuildFromAwakeDelegate == null)
                {
                    IntPtr unityBase = GetModuleHandle("Unity.exe");
                    if (unityBase == IntPtr.Zero) unityBase = GetUnityModule();
                    if (unityBase == IntPtr.Zero) return;
                    IntPtr fnPtr = new IntPtr(unityBase.ToInt64() + RebuildFromAwakeRva);
                    _rebuildFromAwakeDelegate = (d_MonoScriptRebuildFromAwake)
                        Marshal.GetDelegateForFunctionPointer(fnPtr, typeof(d_MonoScriptRebuildFromAwake));
                }
                _rebuildFromAwakeDelegate(nativePtr);
                // RebuildFromAwake queues a RequestScriptReload as a side effect; discard it to
                // prevent accumulating pending reloads that would fire after BuildAssetBundles.
                NativeHookManager.DiscardPendingScriptReload();
                // After RebuildFromAwake, FindOrCreateMonoScriptCache may return an existing shared
                // cache (keyed by MonoClass*) that still carries stale CachedSerializationData from
                // the stub assembly's domain.  Nulling it out forces TransferScriptingObject to
                // recompute the TypeTree entirely from m_pClass (which RebuildFromAwake just set to
                // the full H3VRCode class with anvilPrefab fields).
                IntPtr freshCache = Marshal.ReadIntPtr(new IntPtr(nativePtr.ToInt64() + MonoScriptCacheOffset));
                if (freshCache != IntPtr.Zero)
                {
                    IntPtr pClass = Marshal.ReadIntPtr(new IntPtr(freshCache.ToInt64() + CacheClassOffset));
                    IntPtr oldSerData = Marshal.ReadIntPtr(new IntPtr(freshCache.ToInt64() + CacheSerDataOffset));
                    Marshal.WriteIntPtr(new IntPtr(freshCache.ToInt64() + CacheSerDataOffset), IntPtr.Zero);
                    BuildLog.WriteLine(string.Format(
                        "RSN post-RBA: cache=0x{0:X} pClass=0x{1:X} clearedSerData={2}",
                        freshCache.ToInt64(), pClass.ToInt64(), oldSerData != IntPtr.Zero));
                }
                else
                {
                    BuildLog.WriteLine("RSN post-RBA: freshCache=null (RebuildFromAwake may have failed)");
                }
            }
            catch (Exception ex)
            {
                BuildLog.WriteLine("ReprimeSingleNativeScript: " + ex.Message);
            }
        }

        // ── Post-repair health check ────────────────────────────────────────────────

        // Logs H3VRCode script health after ctor repair; warns if any are still null.
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

        // ── Reimport .object.asset files ────────────────────────────────────
        // After reimporting H3VRCode DLLs (with suppressed domain reload), the
        // native TypeTree database is correct.  But .object.asset ScriptableObjects
        // already in memory were deserialized with the stale TypeTree.
        // Force-reimporting them re-reads the YAML from disk and deserializes
        // with the now-correct TypeTree, restoring m_anvilPrefab and all other
        // H3VRCode fields.  Typically ~199 files × ~4ms each ≈ <1 second.
        private static void ReimportObjectAssets()
        {
            try
            {
                string[] allPaths = AssetDatabase.GetAllAssetPaths();
                int reimportCount = 0;
                System.Diagnostics.Stopwatch sw = new System.Diagnostics.Stopwatch();
                sw.Start();

                foreach (string path in allPaths)
                {
                    if (!path.EndsWith(".object.asset")) continue;
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                    reimportCount++;
                }

                sw.Stop();
                Log(string.Format("ReimportObjectAssets: reimported {0} .object.asset files in {1}ms",
                    reimportCount, sw.ElapsedMilliseconds));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ManagedPluginDomainFix] ReimportObjectAssets: " + ex);
            }
        }

        // ── Clear stale TypeTree caches ─────────────────────────────────────
        // After RepairH3VRCodeMonoScripts fixes m_pClass (MonoScriptCache+8),
        // the CachedSerializationData (MonoScriptCache+136) still holds the stale
        // TypeTree from EATI running without hooks.  Clearing it forces Unity to
        // regenerate the TypeTree from the correct m_pClass on the next
        // SerializedObject access, eliminating the need for a DLL reimport.
        private static void ClearStaleTypeTreeCaches()
        {
            try
            {
                if (_cachedPtrField == null) return;
                FieldInfo cachedPtrField = _cachedPtrField;

                string h3vr   = MeatKit.AssemblyRename + ".dll";
                string h3vrFp = MeatKit.AssemblyFirstpassRename + ".dll";

                int cleared = 0;
                foreach (MonoScript ms in MonoImporter.GetAllRuntimeMonoScripts())
                {
                    if ((UnityEngine.Object)ms == null) continue;
                    string f = Path.GetFileName(AssetDatabase.GetAssetPath(ms));
                    if (f != h3vr && f != h3vrFp) continue;

                    IntPtr nativePtr = IntPtr.Zero;
                    try { nativePtr = (IntPtr)cachedPtrField.GetValue(ms); }
                    catch (Exception) { }
                    if (nativePtr == IntPtr.Zero) continue;

                    IntPtr cache = Marshal.ReadIntPtr(new IntPtr(nativePtr.ToInt64() + MonoScriptCacheOffset));
                    if (cache == IntPtr.Zero) continue;

                    IntPtr serData = Marshal.ReadIntPtr(new IntPtr(cache.ToInt64() + CacheSerDataOffset));
                    if (serData != IntPtr.Zero)
                    {
                        Marshal.WriteIntPtr(new IntPtr(cache.ToInt64() + CacheSerDataOffset), IntPtr.Zero);
                        cleared++;
                    }
                }

                Log(string.Format("ClearStaleTypeTreeCaches: cleared CachedSerializationData for {0} H3VRCode MonoScripts", cleared));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ManagedPluginDomainFix] ClearStaleTypeTreeCaches: " + ex);
            }
        }

        // ── Restore AnvilAsset field values from on-disk YAML ───────────────────
        // After clearing stale TypeTree caches, SerializedObject regenerates from
        // the correct m_pClass.  Assets that were loaded during the stale-TypeTree
        // domain reload still have zeroed m_anvilPrefab values; read the correct
        // values from YAML and patch them.
        private static void RestoreAnvilFieldsFromDisk()
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string[] allPaths = AssetDatabase.GetAllAssetPaths();
            int checkedCount = 0;
            int restoredCount = 0;

            foreach (string path in allPaths)
            {
                if (!path.EndsWith(".object.asset")) continue;
                checkedCount++;

                string fullPath = Path.Combine(projectRoot, path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(fullPath)) continue;

                string diskText = File.ReadAllText(fullPath);

                // Extract AssetName from the m_anvilPrefab block in Unity YAML:
                //   m_anvilPrefab:
                //     Guid: ...
                //     Bundle: ...
                //     AssetName: <value>
                Match match = Regex.Match(diskText,
                    @"m_anvilPrefab:[^\n]*\n\s*Guid:[^\n]*\n\s*Bundle:[^\n]*\n\s*AssetName:\s*(.+)");
                if (!match.Success) continue;
                string diskAssetName = match.Groups[1].Value.Trim();
                // Strip YAML quotes — Unity YAML may nest ' and " for paths with spaces:
                //   AssetName: '"Assets/.../Long Hand.prefab"'   (becomes: Assets/.../Long Hand.prefab)
                while (diskAssetName.Length >= 2 &&
                    ((diskAssetName[0] == '"' && diskAssetName[diskAssetName.Length - 1] == '"') ||
                     (diskAssetName[0] == '\'' && diskAssetName[diskAssetName.Length - 1] == '\'')))
                {
                    diskAssetName = diskAssetName.Substring(1, diskAssetName.Length - 2);
                }
                if (string.IsNullOrEmpty(diskAssetName)) continue;

                // Load the (already in-memory) asset and check SerializedObject
                UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath(path, typeof(UnityEngine.Object));
                if (asset == null) continue;

                SerializedObject so = new SerializedObject(asset);
                SerializedProperty anvilProp = so.FindProperty("m_anvilPrefab");
                if (anvilProp == null) continue;

                SerializedProperty assetNameProp = anvilProp.FindPropertyRelative("AssetName");
                if (assetNameProp == null) continue;

                if (string.IsNullOrEmpty(assetNameProp.stringValue))
                {
                    assetNameProp.stringValue = diskAssetName;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    restoredCount++;
                    if (DebugLogging)
                        Log(string.Format("  Restored {0} -> {1}", path, diskAssetName));
                }
            }

            Log(string.Format("RestoreAnvilFieldsFromDisk: checked {0} .object.asset files, restored m_anvilPrefab on {1}",
                checkedCount, restoredCount));
        }

        private static void Log(string msg)
        {
#pragma warning disable 0162
            if (DebugLogging) Debug.Log("[ManagedPluginDomainFix] " + msg);
#pragma warning restore 0162
        }

        // Logs FVRObject cache state (m_pClass, CSD) and spot-checks m_anvilPrefab on first .object.asset.
        private static void DiagProbeTypeTree()
        {
            try
            {
                FieldInfo cachedPtrField = _cachedPtrField;

                string h3vr = MeatKit.AssemblyRename + ".dll";

                // Find the FVRObject MonoScript
                MonoScript fvrObjectScript = null;
                foreach (MonoScript ms in MonoImporter.GetAllRuntimeMonoScripts())
                {
                    if ((UnityEngine.Object)ms == null) continue;
                    string f = Path.GetFileName(AssetDatabase.GetAssetPath(ms));
                    if (f != h3vr) continue;
                    if (ms.GetClass() != null && ms.GetClass().Name == "FVRObject")
                    {
                        fvrObjectScript = ms;
                        break;
                    }
                }

                if (fvrObjectScript == null)
                {
                    Log("DiagProbe: FVRObject MonoScript not found");
                    return;
                }

                // Check native CachedSerializationData
                IntPtr nativePtr = IntPtr.Zero;
                try { nativePtr = (IntPtr)cachedPtrField.GetValue(fvrObjectScript); }
                catch (Exception) { }
                if (nativePtr == IntPtr.Zero)
                {
                    Log("DiagProbe: FVRObject nativePtr is null");
                    return;
                }

                IntPtr cache = Marshal.ReadIntPtr(new IntPtr(nativePtr.ToInt64() + MonoScriptCacheOffset));
                string cacheInfo = (cache == IntPtr.Zero) ? "NULL" : string.Format("0x{0:X}", cache.ToInt64());

                IntPtr serData = IntPtr.Zero;
                if (cache != IntPtr.Zero)
                    serData = Marshal.ReadIntPtr(new IntPtr(cache.ToInt64() + CacheSerDataOffset));
                string serDataInfo = (serData == IntPtr.Zero) ? "NULL" : string.Format("0x{0:X}", serData.ToInt64());

                IntPtr pClass = IntPtr.Zero;
                if (cache != IntPtr.Zero)
                    pClass = Marshal.ReadIntPtr(new IntPtr(cache.ToInt64() + 8));
                string pClassInfo = (pClass == IntPtr.Zero) ? "NULL" : string.Format("0x{0:X}", pClass.ToInt64());

                Log(string.Format("DiagProbe: FVRObject cache={0} m_pClass={1} CachedSerData={2}",
                    cacheInfo, pClassInfo, serDataInfo));

                // Check managed type has m_anvilPrefab via reflection
                Type fvrType = fvrObjectScript.GetClass();
                string baseTypeName = (fvrType != null && fvrType.BaseType != null)
                    ? fvrType.BaseType.FullName : "null";
                FieldInfo anvilField = (fvrType != null)
                    ? fvrType.GetField("m_anvilPrefab", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
                    : null;
                string anvilFieldInfo = (anvilField != null) 
                    ? string.Format("FOUND(in {0})", anvilField.DeclaringType.Name) 
                    : "NULL";
                Log(string.Format("DiagProbe: FVRObject type={0} base={1} m_anvilPrefab={2}",
                    fvrType != null ? fvrType.FullName : "null", baseTypeName, anvilFieldInfo));

                // Probe SerializedObject on first .object.asset
                string[] allPaths = AssetDatabase.GetAllAssetPaths();
                foreach (string path in allPaths)
                {
                    if (!path.EndsWith(".object.asset")) continue;
                    UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath(path, typeof(UnityEngine.Object));
                    if (asset == null) continue;
                    SerializedObject so = new SerializedObject(asset);
                    SerializedProperty prop = so.FindProperty("m_anvilPrefab");
                    string propInfo = (prop == null) ? "NULL" : "FOUND";
                    SerializedProperty catProp = so.FindProperty("Category");
                    string catInfo = (catProp == null) ? "NULL" : catProp.intValue.ToString();
                    Log(string.Format("DiagProbe: {0} m_anvilPrefab={1} Category={2}",
                        path, propInfo, catInfo));
                    break;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ManagedPluginDomainFix] DiagProbeTypeTree: " + ex);
            }
        }

        // Dumps the top-level SerializedObject property list for the first .object.asset.
        private static void DumpSerializedObjectFields()
        {
            try
            {
                string[] allPaths = AssetDatabase.GetAllAssetPaths();
                foreach (string path in allPaths)
                {
                    if (!path.EndsWith(".object.asset")) continue;
                    UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath(path, typeof(UnityEngine.Object));
                    if (asset == null) continue;
                    SerializedObject so = new SerializedObject(asset);
                    SerializedProperty iter = so.GetIterator();
                    var propNames = new List<string>();
                    if (iter.Next(true)) // enter children
                    {
                        int depth = iter.depth;
                        do
                        {
                            if (iter.depth <= 1) // top-level or one level deep
                                propNames.Add(string.Format("{0}({1}d{2})", iter.name, iter.propertyType, iter.depth));
                        }
                        while (iter.Next(iter.depth < 1)); // only expand top-level
                    }
                    Log(string.Format("DumpSO: {0} type={1} propCount={2} props=[{3}]",
                        path, asset.GetType().Name, propNames.Count,
                        string.Join(", ", propNames.ToArray())));
                    break;
                }
            }
            catch (Exception ex)
            {
                Log("DumpSO error: " + ex.Message);
            }
        }

        // Logs native m_pClass, MonoScriptCache, and CSD state for the first .object.asset.
        private static void DiagNativeMonoScriptState()
        {
            try
            {
                if (_cachedPtrField == null) return;
                FieldInfo cachedPtrField = _cachedPtrField;

                // Find the first .object.asset's MonoScript reference
                string[] allPaths = AssetDatabase.GetAllAssetPaths();
                foreach (string path in allPaths)
                {
                    if (!path.EndsWith(".object.asset")) continue;
                    UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath(path, typeof(UnityEngine.Object));
                    if (asset == null) continue;

                    // Get the MonoScript for this asset
                    MonoScript ms = null;
                    if (asset is MonoBehaviour)
                        ms = MonoScript.FromMonoBehaviour(asset as MonoBehaviour);
                    else if (asset is ScriptableObject)
                        ms = MonoScript.FromScriptableObject(asset as ScriptableObject);

                    if (ms == null) { Log("DiagNative: no MonoScript for " + path); break; }

                    Type cls = ms.GetClass();
                    string clsInfo = (cls != null) ? cls.FullName : "null";
                    string baseInfo = (cls != null && cls.BaseType != null) ? cls.BaseType.FullName : "null";
                    string assetPath = AssetDatabase.GetAssetPath(ms);

                    IntPtr nMS = IntPtr.Zero;
                    try { nMS = (IntPtr)cachedPtrField.GetValue(ms); } catch (Exception) { }

                    string cacheHex = "null";
                    string pClassHex = "null";
                    string csdHex = "null";
                    if (nMS != IntPtr.Zero)
                    {
                        IntPtr cache = Marshal.ReadIntPtr(new IntPtr(nMS.ToInt64() + MonoScriptCacheOffset));
                        cacheHex = (cache == IntPtr.Zero) ? "null" : string.Format("0x{0:X}", cache.ToInt64());
                        if (cache != IntPtr.Zero)
                        {
                            IntPtr pClass = Marshal.ReadIntPtr(new IntPtr(cache.ToInt64() + CacheClassOffset));
                            pClassHex = (pClass == IntPtr.Zero) ? "null" : string.Format("0x{0:X}", pClass.ToInt64());
                            IntPtr csd = Marshal.ReadIntPtr(new IntPtr(cache.ToInt64() + CacheSerDataOffset));
                            csdHex = (csd == IntPtr.Zero) ? "null" : string.Format("0x{0:X}", csd.ToInt64());
                        }
                    }

                    Log(string.Format(
                        "DiagNative: asset={0} assetType={1} ms={2} msPath={3} class={4} base={5} nMS=0x{6:X} cache={7} pClass={8} csd={9}",
                        path, asset.GetType().FullName, ms.name, assetPath,
                        clsInfo, baseInfo, nMS.ToInt64(), cacheHex, pClassHex, csdHex));

                    // Walk native parent chain from m_pClass
                    if (nMS != IntPtr.Zero)
                    {
                        IntPtr cache = Marshal.ReadIntPtr(new IntPtr(nMS.ToInt64() + MonoScriptCacheOffset));
                        if (cache != IntPtr.Zero)
                        {
                            IntPtr pClass = Marshal.ReadIntPtr(new IntPtr(cache.ToInt64() + CacheClassOffset));
                            if (pClass != IntPtr.Zero)
                            {
                                string chain = WalkNativeClassChain(pClass);
                                Log("NativeClassChain(m_pClass): " + chain);
                                DiagStopClasses(pClass);
                                DiagNativeFieldsInChain(pClass);
                                DiagGatekeeperForAnvilFields(pClass);
                            }
                        }
                    }

                    // Also walk the managed Type chain for comparison
                    if (cls != null)
                    {
                        var managedChain = new List<string>();
                        Type t = cls;
                        while (t != null)
                        {
                            managedChain.Add(t.FullName);
                            t = t.BaseType;
                        }
                        Log("ManagedTypeChain: " + string.Join(" → ", managedChain.ToArray()));
                    }
                    break;
                }
            }
            catch (Exception ex)
            {
                Log("DiagNative error: " + ex.Message);
            }
        }

        // Clears CachedSerializationData (cache+136) for all MonoScripts to force TypeTree regeneration.
        private static void ClearAllTypeTreeCaches()
        {
            try
            {
                if (_cachedPtrField == null) return;
                FieldInfo cachedPtrField = _cachedPtrField;

                int cleared = 0;
                foreach (MonoScript ms in MonoImporter.GetAllRuntimeMonoScripts())
                {
                    if ((UnityEngine.Object)ms == null) continue;
                    IntPtr nativePtr = IntPtr.Zero;
                    try { nativePtr = (IntPtr)cachedPtrField.GetValue(ms); }
                    catch (Exception) { }
                    if (nativePtr == IntPtr.Zero) continue;

                    IntPtr cache = Marshal.ReadIntPtr(new IntPtr(nativePtr.ToInt64() + MonoScriptCacheOffset));
                    if (cache == IntPtr.Zero) continue;

                    IntPtr serData = Marshal.ReadIntPtr(new IntPtr(cache.ToInt64() + CacheSerDataOffset));
                    if (serData != IntPtr.Zero)
                    {
                        Marshal.WriteIntPtr(new IntPtr(cache.ToInt64() + CacheSerDataOffset), IntPtr.Zero);
                        cleared++;
                    }
                }
                Log(string.Format("ClearAllTypeTreeCaches: cleared CSD for {0} MonoScripts", cleared));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ManagedPluginDomainFix] ClearAllTypeTreeCaches: " + ex);
            }
        }

        // Probes all .object.assets; returns counts of populated/empty/missing m_anvilPrefab.
        private static string ProbeFirstAnvilPrefab()
        {
            try
            {
                string[] allPaths = AssetDatabase.GetAllAssetPaths();
                int total = 0, populated = 0, empty = 0, noProp = 0;
                string firstDetail = "";
                foreach (string path in allPaths)
                {
                    if (!path.EndsWith(".object.asset")) continue;
                    total++;
                    UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath(path, typeof(UnityEngine.Object));
                    if (asset == null) continue;
                    SerializedObject so = new SerializedObject(asset);
                    SerializedProperty anvilProp = so.FindProperty("m_anvilPrefab");
                    if (anvilProp == null) { noProp++; continue; }
                    SerializedProperty assetNameProp = anvilProp.FindPropertyRelative("AssetName");
                    if (assetNameProp == null) { noProp++; continue; }
                    if (string.IsNullOrEmpty(assetNameProp.stringValue))
                    {
                        empty++;
                        if (string.IsNullOrEmpty(firstDetail))
                            firstDetail = string.Format("EMPTY at {0}", path);
                    }
                    else
                    {
                        populated++;
                        if (string.IsNullOrEmpty(firstDetail))
                            firstDetail = string.Format("FOUND: {0} at {1}", assetNameProp.stringValue, path);
                    }
                }
                return string.Format("total={0} populated={1} empty={2} NOPROP={3} detail=[{4}]",
                    total, populated, empty, noProp, firstDetail);
            }
            catch (Exception ex)
            {
                return "ERROR: " + ex.Message;
            }
        }
    }
}
