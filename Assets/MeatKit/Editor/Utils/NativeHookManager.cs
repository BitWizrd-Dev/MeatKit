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

        // Keep track of all the applied detours so we can quickly undo them before the mono domain is reloaded
        private static readonly List<NativeDetour> Detours = new List<NativeDetour>();

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

        private static void OnShutdownManaged()
        {
            // Fire any registered pre-shutdown callbacks (e.g. file copies that must be done
            // after compilation but before the new domain starts loading assemblies).
            // try/finally guarantees OrigShutdownManaged() is called even if a callback or
            // Debug.LogException somehow throws -- without it platform modules would not shut
            // down cleanly and subsequent domain reloads could be unstable.
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
                // Unity is about to shutdown the mono runtime! Quickly dispose of our detours!
                OrigShutdownManaged();
                foreach (var detour in Detours) detour.Dispose();
            }
        }
    }
}
