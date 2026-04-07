using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace MeatKit
{
    public class NativeHookFunctionOffsets
    {
        public long MonoScriptTransferWrite { get; set; }
        public long MonoScriptTransferRead { get; set; }
        public long ShutdownManaged { get; set; }
        public long StringAssign { get; set; }
        public long ExtractAssemblyTypeInfoAll { get; set; }
        public long AssemblyHasValidTypeInfo { get; set; }
        public long BuildPlayerExtractAndValidateScriptTypes { get; set; }
        public long AssemblyCheckSkipCondition { get; set; }
        public long RequestScriptReload { get; set; }
        public long MonoThreadSuspendAllOtherThreads { get; set; }
    }

    public class EditorVersion
    {
        public NativeHookFunctionOffsets FunctionOffsets { get; set; }
        public bool IsLegacyUnsupported { get; set; }

        private static bool _hasShownPopup = false;
        
        public static bool IsSupportedVersion
        {
            get
            {
                EditorVersion version;
                bool exists = SupportedVersions.TryGetValue(Application.unityVersion, out version);
                bool supported = exists && !version.IsLegacyUnsupported;
                
                if (!supported && !_hasShownPopup)
                {
                    // Show the warning popup about the wrong version if is hasn't come up already.
                    string validVersion = string.Join(", ", SupportedVersions.Keys.ToArray());
                    EditorUtility.DisplayDialog("Wrong editor version",
                        "You are using Unity version " + Application.unityVersion + ", MeatKit requires one of the following: " + validVersion,
                        "I'll go install that.");
                    _hasShownPopup = true;
                }

                return supported;
            }
        }

        public static EditorVersion Current
        {
            get
            {
                EditorVersion currentVersion;
                if (SupportedVersions.TryGetValue(Application.unityVersion, out currentVersion) && !currentVersion.IsLegacyUnsupported)
                    return currentVersion;
                throw new NotSupportedException("The current editor version is not in the list of supported versions.");
            }
        }

        private static readonly Dictionary<string, EditorVersion> SupportedVersions = new Dictionary<string, EditorVersion>()
        {
            {
                "5.6.3p4", new EditorVersion
                {
                    FunctionOffsets = new NativeHookFunctionOffsets(),
                    IsLegacyUnsupported = true
                }
            },
            {
                "5.6.7f1", new EditorVersion
                {
                    FunctionOffsets = new NativeHookFunctionOffsets
                    {
                        MonoScriptTransferWrite = 0xE39BF0,
                        MonoScriptTransferRead = 0xE3BA10,
                        ShutdownManaged = 0x175D2C0,
                        StringAssign = 0x1480,
                        ExtractAssemblyTypeInfoAll = 0x2BAAD0,
                        AssemblyHasValidTypeInfo = 0x1233CB0,
                        BuildPlayerExtractAndValidateScriptTypes = 0x2BD9A0,
                        AssemblyCheckSkipCondition = 0x1B7570,
                        RequestScriptReload = 0x1741A30,
                        MonoThreadSuspendAllOtherThreads = 0xA51C0
                    }
                }
            },
        };
    }
}