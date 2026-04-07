using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using MonoMod.RuntimeDetour;
using MonoMod.Utils;
using UnityEditor;
using UnityEngine;

namespace MeatKit
{
    /// <summary>
    /// This class helps manage detours into the native code of the Editor.
    /// Any detours into native code should be registered using this as it will automatically dispose of them
    /// right before the Editor reloads the mono domain, preventing editor crashes.
    /// </summary>
    [InitializeOnLoad]
    public static class NativeHookManager
    {
        // Actual name: ShutdownPlatformSupportModulesInManaged(void)
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ShutdownManaged();

        private static readonly ShutdownManaged OrigShutdownManaged;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate byte ExtractAssemblyTypeInfoAllDelegate(int policyIndex, uint assemblyId, byte buildTarget, long typeInfoCollector);

        private static ExtractAssemblyTypeInfoAllDelegate _origEATI;

        /// <summary>True when the ExtractAssemblyTypeInfoAll native hook was successfully installed.</summary>
        internal static bool EATIHookInstalled { get { return _origEATI != null; } }

        // Fired before/after EATI runs; safe to modify from main thread
        internal static readonly List<Action> BeforeEATICallbacks = new List<Action>();
        internal static readonly List<Action> AfterEATICallbacks = new List<Action>();

        // Gates EATI per-assembly; return false to skip TypeTree extraction for that DLL.
        // Hooked to block H3VRCode re-extraction during builds (when InsideEATI is true).
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate byte AssemblyHasValidTypeInfoDelegate(long a1);

        private static AssemblyHasValidTypeInfoDelegate _origAHVTI;

        /// <summary>True when the AssemblyHasValidTypeInfo native hook was successfully installed.</summary>
        internal static bool AHVTIHookInstalled { get { return _origAHVTI != null; } }

        // Post-bundle standalone script compile step; made a no-op during MeatKit builds to prevent
        // H3VRCode compile failure (it's absent from the freshly-cleared ScriptAssemblies).
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate byte BuildPlayerExtractAndValidateDelegate(
            long a1, IntPtr a2, uint platformGroup, uint platform, IntPtr a5, long a6, long a7);

        private static BuildPlayerExtractAndValidateDelegate _origBPEVST;

        /// <summary>True when the BuildPlayerExtractAndValidateScriptTypes native hook was successfully installed.</summary>
        internal static bool BPEVSTHookInstalled { get { return _origBPEVST != null; } }

        // Decides whether to include a plugin DLL in a script compile's reference list.
        // Hooked to force-include H3VRCode during post-build editor recompiles (when a5=1).
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate byte AssemblyCheckSkipConditionDelegate(
            long a1, long a2, uint a3, uint a4, byte a5, byte a6, long a7);

        private static AssemblyCheckSkipConditionDelegate _origACSC;

        // Schedules a domain reload. Suppressed during MonoScript repair to prevent an infinite
        // reload loop (CheckConsistency → FixRuntimeScriptReference → reload → repeat).
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void d_RequestScriptReload(IntPtr thisApp);

        private static d_RequestScriptReload _origRequestScriptReload;

        /// <summary>
        /// When true, the Application_RequestScriptReload hook is a no-op.
        /// Set by ManagedPluginDomainFix before ctor-time H3VRCode repair; cleared in delayCall.
        /// </summary>
        internal static volatile bool SuppressRequestScriptReload = false;

        /// <summary>True when the Application_RequestScriptReload suppression hook was successfully installed.</summary>
        internal static bool RequestScriptReloadHookInstalled { get { return _origRequestScriptReload != null; } }

        // Set true by Build.cs _beforeEATI callback during active MeatKit builds.
        // The AHVTI hook only skips H3VRCode while this is true; cleared by _afterEATI.
        internal static volatile bool InsideEATI = false;

        // Set true during BuildAssetBundles to block ALL Assets/Managed/ DLL processing
        // (not just H3VRCode). Cleared after BuildAssetBundles returns so post-build
        // standalone EATIs do NOT block other plugin DLLs the editor compiler needs.
        internal static volatile bool InsideBundleEATI = false;

        // Keep track of all the applied detours so we can quickly undo them before the mono domain is reloaded
        private static readonly List<NativeDetour> Detours = new List<NativeDetour>();

        // P/Invoke declarations for detecting and terminating the Mono IO-worker thread before
        // OrigShutdownManaged runs, to avoid a STOA deadlock on post-build close.
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenThread(uint dwDesiredAccess, bool bInheritHandle, uint dwThreadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateThread(IntPtr hThread, uint dwExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        // NtQueryInformationThread class 9 = ThreadQuerySetWin32StartAddress
        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationThread(
            IntPtr ThreadHandle, int ThreadInformationClass,
            out IntPtr ThreadInformation, int ThreadInformationLength, IntPtr ReturnLength);

        // ExitProcess: terminates the process with DLL_PROCESS_DETACH notifications
        // but WITHOUT running CRT atexit handlers (which is where Mono deadlocks
        // during cleanup when NativeDetour objects have been created).
        [DllImport("kernel32.dll")]
        private static extern void ExitProcess(uint uExitCode);

        // Fired after compilation but before domain reload; only reliable window for survival code
        internal static readonly List<Action> BeforeShutdownCallbacks = new List<Action>();

        static NativeHookManager()
        {
            if (!EditorVersion.IsSupportedVersion) return;

            // Check whether H3VRCode DLLs are present before installing hooks that reference them.
            string managedDir = System.IO.Path.Combine(Application.dataPath, "Managed");
            _h3vrCodeExists = System.IO.File.Exists(System.IO.Path.Combine(managedDir, "H3VRCode-CSharp.dll"))
                           || System.IO.File.Exists(System.IO.Path.Combine(managedDir, "H3VRCode-CSharp-firstpass.dll"));

            NativeHookFunctionOffsets offsets = EditorVersion.Current.FunctionOffsets;

            // Apply our detours here and save the trampoline to call the original function.
            // Wrapped in try/catch: if ApplyEditorDetour throws (e.g. wrong binary offset) we must NOT
            // let the exception propagate out of the static constructor -- a throwing static constructor
            // causes TypeInitializationException on every subsequent access to any member of this type,
            // which would break BeforeShutdownCallbacks.Add() in ManagedPluginDomainFix.
            try
            {
                OrigShutdownManaged = ApplyEditorDetour<ShutdownManaged>(offsets.ShutdownManaged, new ShutdownManaged(OnShutdownManaged));
            }
            catch (Exception ex)
            {
                Debug.LogError("[NativeHookManager] Failed to install ShutdownManaged detour. " +
                               "Domain reload file-copy safety net will not function. Exception: " + ex.Message);
            }

            // Install EATI hook when offset is available for this Unity version.
            long eatIOffset = offsets.ExtractAssemblyTypeInfoAll;
            if (eatIOffset != 0)
            {
                try
                {
                    _origEATI = ApplyEditorDetour<ExtractAssemblyTypeInfoAllDelegate>(
                        eatIOffset,
                        new ExtractAssemblyTypeInfoAllDelegate(OnExtractAssemblyTypeInfoAll));
                }
                catch (Exception ex)
                {
                    Debug.LogError("[NativeHookManager] Failed to install EATI detour. " +
                                   "Pre/post-EATI callbacks will not fire; build will use fallback TypeTree guard. Exception: " + ex.Message);
                }
            }

            // Install AssemblyHasValidTypeInfo hook to skip H3VRCode assemblies during EATI.
            // When InsideEATI is true, returning false prevents EATI from overwriting
            // Library/metadata for H3VRCode, eliminating TypeTree corruption.
            long ahvtiOffset = offsets.AssemblyHasValidTypeInfo;
            if (ahvtiOffset != 0)
            {
                try
                {
                    _origAHVTI = ApplyEditorDetour<AssemblyHasValidTypeInfoDelegate>(
                        ahvtiOffset,
                        new AssemblyHasValidTypeInfoDelegate(OnAssemblyHasValidTypeInfo));
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[NativeHookManager] Failed to install AHVTI detour. " +
                                     "H3VRCode TypeTree will still be guarded by backup/restore. Exception: " + ex.Message);
                }
            }

            // Install BuildPlayer_ExtractAndValidateScriptTypes hook.
            // Makes it a no-op (return 1 = success) so the already-valid bundle manifest is
            // preserved and the H3VRCode-less standalone compile doesn't block the build.
            long bpevstOffset = offsets.BuildPlayerExtractAndValidateScriptTypes;
            if (bpevstOffset != 0)
            {
                try
                {
                    _origBPEVST = ApplyEditorDetour<BuildPlayerExtractAndValidateDelegate>(
                        bpevstOffset,
                        new BuildPlayerExtractAndValidateDelegate(OnBuildPlayerExtractAndValidateScriptTypes));
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[NativeHookManager] Failed to install BPEVST detour. " +
                                     "Standalone script compile may fail and block the build. Exception: " + ex.Message);
                }
            }

            // Install Assembly_CheckSkipCondition hook.
            // Forces H3VRCode to be included in the post-build editor recompile reference list.
            long acscOffset = offsets.AssemblyCheckSkipCondition;
            if (acscOffset != 0)
            {
                try
                {
                    _origACSC = ApplyEditorDetour<AssemblyCheckSkipConditionDelegate>(
                        acscOffset,
                        new AssemblyCheckSkipConditionDelegate(OnAssemblyCheckSkipCondition));
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[NativeHookManager] Failed to install ACSC detour. " +
                                     "FistVR scripts will break after every build. Exception: " + ex.Message);
                }
            }

            // Install Application_RequestScriptReload hook. Suppresses reload requests during the
            // ctor-time H3VRCode MonoScript repair window to prevent an infinite domain-reload loop
            // caused by CheckConsistency firing on newly-valid H3VRCode class pointers.
            long rrOffset = offsets.RequestScriptReload;
            if (rrOffset != 0)
            {
                try
                {
                    _origRequestScriptReload = ApplyEditorDetour<d_RequestScriptReload>(
                        rrOffset,
                        new d_RequestScriptReload(OnRequestScriptReload));
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[NativeHookManager] Failed to install RequestScriptReload hook. " +
                                     "H3VRCode ctor repair will fall back to delayCall (broken inspector). Exception: " + ex.Message);
                }
            }

            // STOA hook REMOVED — permanently making mono_thread_suspend_all_other_threads a no-op
            // prevented Unity from closing (WM_CLOSE hang: Mono's exit sequence needs STOA to
            // actually suspend threads before teardown). The original deadlock during domain reload
            // (caused by Mono IO-worker threads stuck in uninterruptible I/O) is now handled by
            // TerminateMonoIOWorkers() in OnShutdownManaged, which kills those threads before
            // OrigShutdownManaged calls into Mono's domain teardown path.

            // NOTE: PIOLA and ReloadAllUsedAssemblies hooks are not installed — both are
            // re-entrant from an [InitializeOnLoad] context and cause crashes in mono.dll.

            // Register for EditorApplication.editorApplicationQuit (internal field, accessed via reflection).
            // Creating NativeDetour objects causes Mono's CRT atexit cleanup to deadlock during exit().
            // This callback fires inside Application::Terminate, BEFORE the deadlocking exit() call.
            // We call ExitProcess(0) here to bypass the CRT atexit handlers entirely; this still runs
            // DLL_PROCESS_DETACH notifications but avoids the Mono cleanup deadlock.
            RegisterQuitCallback();
        }

        private static void RegisterQuitCallback()
        {
            try
            {
                var field = typeof(EditorApplication).GetField(
                    "editorApplicationQuit",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                if (field != null)
                {
                    var existing = (UnityEngine.Events.UnityAction)field.GetValue(null);
                    field.SetValue(null, (UnityEngine.Events.UnityAction)delegate
                    {
                        // Run any previously-registered quit callbacks first
                        if (existing != null)
                        {
                            try { existing(); }
                            catch { }
                        }
                        ExitProcess(0);
                    });
                }
                else
                {
                    Debug.LogWarning("[NativeHookManager] editorApplicationQuit field not found — " +
                                     "editor close may hang. Force-kill with Task Manager if needed.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[NativeHookManager] Failed to register quit callback: " + ex.Message);
            }
        }

        public static T ApplyEditorDetour<T>(long from, Delegate to) where T : class
        {
            // Avoid crashing the editor if we're loaded in the wrong Unity version
            if (!EditorVersion.IsSupportedVersion) return null;

            // Get the base address of the Unity module and the address in memory of the function
            IntPtr editorBase = DynDll.OpenLibrary("Unity.exe");
            IntPtr fromPtr = (IntPtr)(editorBase.ToInt64() + from);

            // Get a function pointer for the managed callback
            var toPtr = Marshal.GetFunctionPointerForDelegate(to);

            // Make a detour and add it to the list
            var detour = new NativeDetour(fromPtr, toPtr, new NativeDetourConfig { ManualApply = true });
            Detours.Add(detour);

            // Apply the detour and generate a trampoline for it, which we return
            var original = detour.GenerateTrampoline(to.GetType().GetMethod("Invoke")).CreateDelegate(typeof(T)) as T;
            detour.Apply();
            return original;
        }

        public static Delegate GetDelegateForFunctionPointer<T>(long from)
        {
            // Avoid crashing the editor if we're loaded in the wrong Unity version
            if (!EditorVersion.IsSupportedVersion) return null;

            // Get the base address for the Unity module and apply the offset
            IntPtr editorBase = DynDll.OpenLibrary("Unity.exe");
            return Marshal.GetDelegateForFunctionPointer((IntPtr)(editorBase.ToInt64() + from), typeof(T));
        }

        // NOTE: GetCompatibleWithEditorOrAnyPlatform hook not installed.
        // Its prologue contains RIP-relative instructions that MonoMod copies verbatim to the
        // trampoline page, causing corrupted memory accesses. H3VRCode references are injected
        // via Assets/mcs.rsp instead.

        // Saved thisApp pointer from the most-recently suppressed RequestScriptReload call.
        // Replayed by ManagedPluginDomainFix when suppression is lifted so that any
        // compilation-complete RequestScriptReload that was swallowed during domain reload
        // still causes the next compilation pass (and final domain reload) to happen.
        private static volatile IntPtr _suppressedReloadApp = IntPtr.Zero;

        // RequestScriptReload hook: returns early when suppression is active, but records
        // the call so it can be replayed once the suppression window closes.
        private static void OnRequestScriptReload(IntPtr thisApp)
        {
            if (SuppressRequestScriptReload)
            {
                _suppressedReloadApp = thisApp; // overwrite is fine; all calls use the same App ptr
                return;
            }
            _origRequestScriptReload(thisApp);
        }

        /// <summary>
        /// Replays a RequestScriptReload call that was suppressed while
        /// SuppressRequestScriptReload was true.  Must be called AFTER setting
        /// SuppressRequestScriptReload = false.  No-op if no call was suppressed.
        /// </summary>
        internal static void ReplayPendingScriptReloadIfNeeded()
        {
            IntPtr app = _suppressedReloadApp;
            if (app == IntPtr.Zero || _origRequestScriptReload == null) return;
            _suppressedReloadApp = IntPtr.Zero; // consume the pending call
            _origRequestScriptReload(app);
        }

        /// <summary>
        /// Discards any pending suppressed RequestScriptReload without replaying it.
        /// Call this after a successful MonoScript repair (RebuildFromAwake) when all
        /// scripts are healthy — RebuildFromAwake triggers RequestScriptReload internally
        /// as a side effect, and that internal call should NOT cause another domain reload.
        /// </summary>
        internal static void DiscardPendingScriptReload()
        {
            _suppressedReloadApp = IntPtr.Zero;
        }

        // True when H3VRCode DLLs exist in Assets/Managed/ at load time.
        // Checked by the ACSC hook so we only force-include when the DLLs are actually present.
        // Without this guard, a project that never imported H3VRCode would get phantom references
        // injected into the compiler, causing missing-file errors.
        private static readonly bool _h3vrCodeExists;

        // Assembly_CheckSkipCondition hook: return 0 (include) for H3VRCode paths in ALL
        // compiles (editor AND standalone), bypassing IsCompatibleWithEditorCPUAndOS exclusion.
        // Only activates when the DLLs actually exist on disk — if a user never imported
        // H3VRCode, the hook falls through to the original behaviour.
        // Unity string layout at a2: *(long*)a2==0 → small string inline at a2+8; else heap char*.
        private static byte OnAssemblyCheckSkipCondition(long a1, long a2, uint a3, uint a4, byte a5, byte a6, long a7)
        {
            if (_h3vrCodeExists && a2 != 0)
            {
                try
                {
                    long firstWord = Marshal.ReadInt64((IntPtr)a2);
                    IntPtr namePtr = (firstWord == 0) ? (IntPtr)(a2 + 8) : (IntPtr)firstWord;
                    string path = Marshal.PtrToStringAnsi(namePtr);
                    if (path != null && path.IndexOf("H3VRCode-CSharp", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return 0; // include
                    }
                }
                catch { }
            }
            return _origACSC(a1, a2, a3, a4, a5, a6, a7);
        }

        private static byte OnBuildPlayerExtractAndValidateScriptTypes(
            long a1, IntPtr a2, uint platformGroup, uint platform, IntPtr a5, long a6, long a7)
        {
            // Skip the standalone compile. The bundle manifest was already built before this
            // call, so returning 1 (success) allows the pipeline to continue normally.
            return 1; // 1 = success
        }

        private static byte OnExtractAssemblyTypeInfoAll(int policyIndex, uint assemblyId, byte buildTarget, long typeInfoCollector)
        {
            // For standalone EATIs (buildTarget != 1): force InsideEATI=true so the AHVTI hook
            // blocks H3VRCode re-extraction regardless of whether Build.cs already set the flag.
            bool wasInsideEATI = InsideEATI;
            bool isStandaloneEATI = (buildTarget != 1);
            if (isStandaloneEATI)
            {
                InsideEATI = true;
            }

            foreach (var cb in BeforeEATICallbacks)
                try { cb(); }
                catch (Exception ex) { Debug.LogException(ex); }

            byte result = _origEATI(policyIndex, assemblyId, buildTarget, typeInfoCollector);

            foreach (var cb in AfterEATICallbacks)
                try { cb(); }
                catch (Exception ex) { Debug.LogException(ex); }

            if (isStandaloneEATI) InsideEATI = wasInsideEATI;
            return result;
        }

        // AssemblyHasValidTypeInfo hook: when InsideEATI is true, return false (0) for H3VRCode
        // assemblies so EATI skips them and does not overwrite their Library/metadata files.
        // Unity string layout at *a1: *(long*)a1==0 → small-string at a1+8; else heap char*.
        private static byte OnAssemblyHasValidTypeInfo(long a1)
        {
            if (InsideEATI && a1 != 0)
            {
                try
                {
                    long firstWord = Marshal.ReadInt64((IntPtr)a1);
                    IntPtr namePtr = (firstWord == 0) ? (IntPtr)(a1 + 8) : (IntPtr)firstWord;
                    string name = Marshal.PtrToStringAnsi(namePtr);
                    if (name != null && name.IndexOf("H3VRCode", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return 0; // false → EATI skips this assembly; Library/metadata unchanged
                    }
                }
                catch { }
            }
            return _origAHVTI(a1);
        }

        private static void OnShutdownManaged()
        {
            // Unity is about to shutdown the mono runtime! Quickly dispose of our detours!
            // Fire any registered pre-shutdown callbacks before tearing down the domain.
            // try/finally guarantees OrigShutdownManaged() is called even if a callback throws.
            try
            {
                foreach (var cb in BeforeShutdownCallbacks)
                {
                    try { cb(); }
                    catch (Exception ex) { Debug.LogException(ex); }
                }
            }
            finally
            {
                // Do NOT dispose _stoaDetourInstance -- the STOA hook must live until process exit.
                try
                {
                    OrigShutdownManaged();
                }
                finally
                {
                    foreach (var detour in Detours) detour.Dispose();
                }
            }
        }



        // Returns true if any thread whose Win32 start address lies within mono.dll is running.
        // This indicates the Mono socket IO-worker is active and will block STOA indefinitely.
        private static bool MonoIOWorkerExists()
        {
            try
            {
                IntPtr monoBase = DynDll.OpenLibrary("mono.dll");
                if (monoBase == IntPtr.Zero) return false;
                long monoStart = monoBase.ToInt64();
                long monoEnd   = monoStart + 16L * 1024 * 1024;

                uint selfTid = GetCurrentThreadId();
                const uint THREAD_QUERY_INFORMATION = 0x0040;
                const int  ThreadQuerySetWin32StartAddress = 9;

                foreach (System.Diagnostics.ProcessThread pt in
                         System.Diagnostics.Process.GetCurrentProcess().Threads)
                {
                    if ((uint)pt.Id == selfTid) continue;
                    IntPtr hThread = OpenThread(THREAD_QUERY_INFORMATION, false, (uint)pt.Id);
                    if (hThread == IntPtr.Zero) continue;
                    try
                    {
                        IntPtr startAddr;
                        int status = NtQueryInformationThread(hThread,
                            ThreadQuerySetWin32StartAddress,
                            out startAddr, IntPtr.Size, IntPtr.Zero);
                        if (status == 0)
                        {
                            long addr = startAddr.ToInt64();
                            if (addr >= monoStart && addr < monoEnd)
                            {
                                Debug.Log(string.Format(
                                    "[NativeHookManager] Mono IO-worker detected (TID 0x{0:X}); STOA will be suppressed",
                                    pt.Id));
                                return true;
                            }
                        }
                    }
                    finally
                    {
                        CloseHandle(hThread);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[NativeHookManager] MonoIOWorkerExists failed: " + ex.Message);
            }
            return false;
        }
    }
}
