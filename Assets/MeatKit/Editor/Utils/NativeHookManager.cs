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

        // Fired inside OnExtractAssemblyTypeInfoAll BEFORE the original function runs.
        // Safe to add/remove from the main (editor) thread.
        internal static readonly List<Action> BeforeEATICallbacks = new List<Action>();

        // Fired inside OnExtractAssemblyTypeInfoAll AFTER the original function returns.
        internal static readonly List<Action> AfterEATICallbacks = new List<Action>();

        // Gates EATI per-assembly; return false to skip TypeTree extraction for that DLL.
        // Hooked to block H3VRCode re-extraction during builds (when InsideEATI is true).
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate byte AssemblyHasValidTypeInfoDelegate(long a1);

        private static AssemblyHasValidTypeInfoDelegate _origAHVTI;

        /// <summary>True when the AssemblyHasValidTypeInfo native hook was successfully installed.</summary>
        internal static bool AHVTIHookInstalled { get { return _origAHVTI != null; } }

        // Post-bundle standalone script compile step; made a no-op during MeatKit builds
        // to prevent H3VRCode compile failure (it's absent from the freshly-cleared ScriptAssemblies).
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

        // Schedules a domain reload. Suppressed during MonoScript repair to prevent an
        // infinite reload loop (CheckConsistency → FixRuntimeScriptReference → reload → repeat).
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

        // Keep track of all the applied detours so we can quickly undo them before the mono domain is reloaded
        private static readonly List<NativeDetour> Detours = new List<NativeDetour>();

        // P/Invoke declarations for detecting and terminating the Mono IO-worker thread
        // before OrigShutdownManaged runs, to avoid a STOA deadlock on post-build close.
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

        // Delegate for mono_thread_suspend_all_other_threads (void, no args, cdecl in mono.dll).
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void StoaDelegate();

        private static StoaDelegate _origStoa;

        // The STOA detour is intentionally NOT added to Detours -- see OnShutdownManaged.
        private static NativeDetour _stoaDetourInstance;

        // Set true in OnShutdownManaged when the Mono IO-worker thread is detected.
        // When true, OnSuspendAllOtherThreads skips calling the original STOA.
        private static bool _skipStoa = false;

        // Callbacks fired inside OnShutdownManaged, i.e. after script compilation completes but before
        // the Mono domain is torn down. This is the ONLY reliable window to run managed code that must
        // survive into the next domain reload. Register callbacks here instead of AppDomain.DomainUnload
        // because Unity's Mono embedding does not reliably raise that event.
        internal static readonly List<Action> BeforeShutdownCallbacks = new List<Action>();

        static NativeHookManager()
        {
            if (!EditorVersion.IsSupportedVersion) return;

            // Apply our detours here and save the trampoline to call the original function.
            // Wrapped in try/catch: if ApplyEditorDetour throws (e.g. wrong binary offset) we must NOT
            // let the exception propagate out of the static constructor -- a throwing static constructor
            // causes TypeInitializationException on every subsequent access to any member of this type,
            // which would break BeforeShutdownCallbacks.Add() in ManagedPluginDomainFix.
            try
            {
                OrigShutdownManaged = ApplyEditorDetour<ShutdownManaged>(EditorVersion.Current.FunctionOffsets.ShutdownManaged, new ShutdownManaged(OnShutdownManaged));
            }
            catch (Exception ex)
            {
                Debug.LogError("[NativeHookManager] Failed to install ShutdownManaged detour. " +
                               "Domain reload file-copy safety net will not function. Exception: " + ex.Message);
            }

            // Install EATI hook when offset is available for this Unity version.
            long eatIOffset = EditorVersion.Current.FunctionOffsets.ExtractAssemblyTypeInfoAll;
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
            long ahvtiOffset = EditorVersion.Current.FunctionOffsets.AssemblyHasValidTypeInfo;
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
            long bpevstOffset = EditorVersion.Current.FunctionOffsets.BuildPlayerExtractAndValidateScriptTypes;
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
            long acscOffset = EditorVersion.Current.FunctionOffsets.AssemblyCheckSkipCondition;
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

            // Install Application_RequestScriptReload hook.
            // Suppresses reload requests during the ctor-time H3VRCode MonoScript repair window
            // to prevent an infinite domain-reload loop caused by CheckConsistency firing on
            // newly-valid H3VRCode class pointers.
            long rrOffset = EditorVersion.Current.FunctionOffsets.RequestScriptReload;
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

            // Hook mono_thread_suspend_all_other_threads in mono.dll.
            // Skips the call when a Mono IO-worker thread is present to prevent a deadlock on
            // domain close (the IO worker is blocked in a non-alertable wait and never ACKs).
            long stoaRva = EditorVersion.Current.FunctionOffsets.MonoThreadSuspendAllOtherThreads;
            if (stoaRva != 0)
            {
                try
                {
                    IntPtr monoBase = DynDll.OpenLibrary("mono.dll");
                    if (monoBase != IntPtr.Zero)
                    {
                        IntPtr stoaAddr = (IntPtr)(monoBase.ToInt64() + stoaRva);
                        var stoaTo = Marshal.GetFunctionPointerForDelegate(new StoaDelegate(OnSuspendAllOtherThreads));
                        // NOT added to Detours: STOA fires after OnShutdownManaged returns, so this
                        // hook must outlive the other detours. Self-disposed in OnSuspendAllOtherThreads.
                        _stoaDetourInstance = new NativeDetour(stoaAddr, stoaTo, new NativeDetourConfig { ManualApply = true });
                        _origStoa = _stoaDetourInstance.GenerateTrampoline(typeof(StoaDelegate).GetMethod("Invoke")).CreateDelegate(typeof(StoaDelegate)) as StoaDelegate;
                        _stoaDetourInstance.Apply();
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[NativeHookManager] Failed to install STOA hook: " + ex.Message);
                }
            }

            // NOTE: PIOLA and ReloadAllUsedAssemblies hooks are not installed — both are
            // re-entrant from an [InitializeOnLoad] context and cause crashes in mono.dll.
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

        // RequestScriptReload hook: returns early when suppression is active.
        private static void OnRequestScriptReload(IntPtr thisApp)
        {
            if (SuppressRequestScriptReload)
                return;
            _origRequestScriptReload(thisApp);
        }

        // Assembly_CheckSkipCondition hook: return 0 (include) for H3VRCode paths when a5=1
        // (editor compile), bypassing IsCompatibleWithEditorCPUAndOS exclusion.
        // Unity string layout at a2: *(long*)a2==0 → small string inline at a2+8; else heap char*.
        private static byte OnAssemblyCheckSkipCondition(long a1, long a2, uint a3, uint a4, byte a5, byte a6, long a7)
        {
            if (a5 != 0 && a2 != 0)
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
                // Set _skipStoa if the Mono IO-worker thread is present. When set, the STOA hook
                // skips the call to prevent a deadlock (IO worker is blocked in a non-alertable wait).
                _skipStoa = MonoIOWorkerExists();

                try
                {
                    OrigShutdownManaged();
                }
                finally
                {
                    // STOA fires after this block returns; do NOT dispose _stoaDetourInstance here.
                    // OnSuspendAllOtherThreads self-disposes it after it fires.
                    foreach (var detour in Detours) detour.Dispose();
                }
            }
        }

        // Called when mono_thread_suspend_all_other_threads fires.
        // Skips the call when _skipStoa is set; self-disposes the hook either way.
        private static void OnSuspendAllOtherThreads()
        {
            bool skip = _skipStoa;
            _skipStoa = false;

            // Self-dispose now -- STOA is already in progress, hook no longer needed.
            if (_stoaDetourInstance != null)
            {
                try { _stoaDetourInstance.Dispose(); }
                catch { }
                _stoaDetourInstance = null;
            }

            if (skip)
            {
                Debug.Log("[NativeHookManager] STOA suppressed (Mono IO-worker present)");
                return;
            }
            if (_origStoa != null) _origStoa();
        }

        /// <summary>
        /// Returns true if any thread whose Win32 start address lies within mono.dll is running.
        /// This indicates the Mono socket IO-worker is active and will block STOA indefinitely.
        /// </summary>
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
