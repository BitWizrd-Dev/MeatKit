using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

// Bootstraps the minimum FistVR manager singletons needed for FVRPhysicalObject and
// FVRFireArm to initialise in editor play mode.  All types are resolved from
// H3VRCode-CSharp so the correct ManagerSingleton tables are used.  C# 4.0 only.
public static class PlayHelper
{
#if UNITY_EDITOR

    // Set to true to print verbose bootstrap messages to the console.
    private const bool VerboseLogs = false;

    // -------------------------------------------------------------------------
    // Modder opt-out toggle
    // -------------------------------------------------------------------------
    // If your mod supplies its own FistVR manager singletons (GM, AM, SM, FXM,
    // FVRSceneSettings) and/or its own VR camera setup, you can disable
    // PlayHelper entirely so the two bootstraps don't conflict.
    //
    // Usage — create an Editor script (any .cs file inside an Editor/ folder)
    // with an [InitializeOnLoad] class and set the flag in its static constructor,
    // BEFORE Unity enters play mode:
    //
    //   using UnityEditor;
    //
    //   [InitializeOnLoad]
    //   public static class MyModPlayHelperOverride
    //   {
    //       static MyModPlayHelperOverride()
    //       {
    //           PlayHelper.Enabled = false;
    //       }
    //   }
    //
    // With Enabled = false, both OnBeforeSceneLoad (singleton bootstrap + Harmony
    // patches) and OnAfterSceneLoad (VR camera creation) are completely skipped.
    // -------------------------------------------------------------------------
    public static bool Enabled = true;

    private static Harmony _harmony;

    // Harmony prefix — returning false skips the patched method entirely.
    private static bool SkipMethod() { return false; }

    private static void Log(string msg) { if (VerboseLogs) Debug.Log("[PlayHelper] " + msg); }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void OnBeforeSceneLoad()
    {
        if (!Enabled) return;

        Type gmType  = ResolveFromH3VR("FistVR.GM");
        Type amType  = ResolveFromH3VR("FistVR.AM");
        Type smType  = ResolveFromH3VR("FistVR.SM");
        Type fxmType = ResolveFromH3VR("FistVR.FXM");
        Type ssType  = ResolveFromH3VR("FistVR.FVRSceneSettings");

        if (gmType == null)
        {
            // H3VRCode-CSharp not imported yet; nothing to bootstrap.
            return;
        }

        // Patch crashing methods before AddComponent fires — Unity swallows Awake exceptions
        // internally so our try/catch never sees them.  Patches stay alive for the session.
        _harmony = new Harmony("com.bitwizrd.playhelper.editorsafety");
        ApplyInitPatches(gmType, ssType, amType, smType, fxmType);

        // GM first — FVRSceneSettings.Awake needs IsAsyncLoading already set.
        // Awake is no-op'd (9 cascading ES2 crashes), so Instance is set manually.
        GameObject gmGO = CreateEditorManager(gmType, "[Editor GM]");
        if (gmGO != null) SetManagerSingletonInstance(gmType, gmGO);

        // FVRSceneSettings — must exist before FVRFireArm.Awake reads GM.CurrentSceneSettings.
        if (ssType != null && gmGO != null)
        {
            GameObject ssGO = CreateEditorManager(ssType, "[Editor FVRSceneSettings]");
            if (ssGO != null)
            {
                SetCurrentSceneSettings(gmType, ssType, ssGO);
            }
        }

        // AM — needed for AM.GetFireArmMechanicalSpread; dict seeded after creation.
        // AM.Awake is not no-op'd, but on re-entry no new component fires Awake so
        // Instance must be set manually like the other managers.
        if (amType != null)
        {
            GameObject amGO = CreateEditorManager(amType, "[Editor AM]");
            if (amGO != null) SetManagerSingletonInstance(amType, amGO);
            PopulateAMAccuracyDictionary(amType);
        }

        // SM — audio pool dicts seeded manually; Awake no-op'd, Instance set manually.
        if (smType != null)
        {
            GameObject smGO = CreateEditorManager(smType, "[Editor SM]");
            if (smGO != null) SetManagerSingletonInstance(smType, smGO);
            PopulateSMPrefabBindingDictionary(smType);
            WarmupSMGenericPools(smType);
            SeedSMImpactDictionary(smType);
        }

        // FXM — muzzle dict seeded with dummy configs; Awake no-op'd, Instance set manually.
        // Component disabled to prevent Update() NullRef on the null MuzzleFireLight.
        if (fxmType != null)
        {
            GameObject fxmGO = CreateEditorManager(fxmType, "[Editor FXM]");
            if (fxmGO != null)
            {
                SetManagerSingletonInstance(fxmType, fxmGO);
                DisableComponent(fxmGO, fxmType);
                PopulateFXMMuzzleDictionary(fxmType);
            }
        }

        Log("Harmony patches active, all managers bootstrapped.");
    }

    // Disables all existing scene cameras and spawns [EditorVRCamera] at the SceneView
    // eye position so the VR headset has a sensible render origin.
    //
    // The SceneView camera is explicitly disconnected from VR stereo rendering
    // (stereoTargetEye = None) so mouse/keyboard scene navigation remains independent
    // from what the HMD is doing.  In Unity 5.6 editor VR mode, the scene view camera
    // is by default also driven by HMD pose data; opting it out here means the two
    // cameras are fully independent:
    //   [EditorVRCamera]  — rendered to the HMD; position driven by the VR SDK
    //   SceneView camera  — navigated with mouse/keyboard as usual in edit mode
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void OnAfterSceneLoad()
    {
        if (!Enabled) return;

        Camera[] existing = UnityEngine.Object.FindObjectsOfType<Camera>();
        for (int i = 0; i < existing.Length; i++)
        {
            existing[i].enabled = false;
            Log("Disabled existing camera: " + existing[i].gameObject.name);
        }

        // Read scene-view position BEFORE creating the VR camera so the spawn point
        // is where the user was looking from, then immediately detach the scene-view
        // camera from VR tracking.
        Vector3 spawnPos;
        Quaternion spawnRot;
        UnityEditor.SceneView sv = UnityEditor.SceneView.lastActiveSceneView;
        if (sv != null && sv.camera != null)
        {
            spawnPos = sv.camera.transform.position;
            spawnRot = sv.camera.transform.rotation;
            sv.camera.stereoTargetEye = StereoTargetEyeMask.None;
            Log("[EditorVRCamera] placed at SceneView position " + spawnPos);
        }
        else
        {
            spawnPos = new Vector3(0f, 1.8f, 0f);
            spawnRot = Quaternion.identity;
            Log("[EditorVRCamera] placed at default standing origin.");
        }

        GameObject camGO = new GameObject("[EditorVRCamera]");
        camGO.hideFlags = HideFlags.DontSave;
        Camera vrCam = camGO.AddComponent<Camera>();
        vrCam.tag             = "MainCamera";
        vrCam.nearClipPlane   = 0.01f;
        vrCam.farClipPlane    = 1000f;
        vrCam.clearFlags      = CameraClearFlags.Skybox;
        vrCam.depth           = 10;
        vrCam.stereoTargetEye = StereoTargetEyeMask.Both;  // explicit: this camera renders to the HMD
        camGO.transform.position = spawnPos;
        camGO.transform.rotation = spawnRot;

        // Unity's VR system can re-enable stereo on the scene-view camera each frame.
        // Register an editor-update callback to keep it detached for the entire play session.
        UnityEditor.EditorApplication.update += KeepSceneViewDetachedFromVR;
        UnityEditor.EditorApplication.playmodeStateChanged += OnPlaymodeChanged;
    }

    // Unsubscribes the per-frame scene-view guard when play mode ends.
    private static void OnPlaymodeChanged()
    {
        if (!UnityEditor.EditorApplication.isPlaying &&
            !UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode)
        {
            UnityEditor.EditorApplication.update -= KeepSceneViewDetachedFromVR;
            UnityEditor.EditorApplication.playmodeStateChanged -= OnPlaymodeChanged;
            Log("Play mode ended – SceneView VR-detach guard removed.");
        }
    }

    // Called every editor frame while in play mode.  If the VR system re-enabled stereo
    // on the scene-view camera, revert it so the scene view stays free for navigation.
    private static void KeepSceneViewDetachedFromVR()
    {
        UnityEditor.SceneView sv = UnityEditor.SceneView.lastActiveSceneView;
        if (sv != null && sv.camera != null &&
            sv.camera.stereoTargetEye != StereoTargetEyeMask.None)
        {
            sv.camera.stereoTargetEye = StereoTargetEyeMask.None;
            Log("Re-detached SceneView camera from VR stereo tracking.");
        }
    }

    // No-ops all Awake/Start/Update methods that crash due to missing editor resources.
    // Patches stay alive for the session; AppDomain resets on play-stop.
    private static void ApplyInitPatches(
        Type gmType, Type ssType, Type amType, Type smType, Type fxmType)
    {
        try
        {
            HarmonyMethod skip = new HarmonyMethod(
                typeof(PlayHelper).GetMethod("SkipMethod",
                    BindingFlags.Static | BindingFlags.NonPublic));

            if (gmType != null)
            {
                PatchSafely(AccessTools.Method(gmType, "Awake"), skip);
                PatchSafely(AccessTools.Method(gmType, "Start"), skip);
            }

            if (ssType != null)
            {
                Type mbType = ResolveFromH3VR("FistVR.ManagerBootStrap");
                if (mbType != null)
                    PatchSafely(AccessTools.Method(mbType, "BootStrap"), skip);
            }

            if (amType != null)
                PatchSafely(AccessTools.Method(amType, "GenerateFireArmRoundDictionaries"), skip);

            if (smType != null)
                PatchSafely(AccessTools.Method(smType, "Awake"), skip);

            if (fxmType != null)
                PatchSafely(AccessTools.Method(fxmType, "Awake"), skip);

            if (smType != null)
                PatchSafely(AccessTools.Method(smType, "SetReverbEnvironment"), skip);

            if (ssType != null)
            {
                PatchSafely(AccessTools.Method(ssType, "Start"), skip);
                PatchSafely(AccessTools.Method(ssType, "Update"), skip);
            }

            // PlayImpactSound calls GM.CurrentPlayerBody.Head (null in editor) before any dict lookup.
            Type aicType = ResolveFromH3VR("FistVR.AudioImpactController");
            if (aicType != null)
                PatchSafely(AccessTools.Method(aicType, "OnCollisionEnter"), skip);

            // Winchester1873LoadingGate.Update reads GM.CurrentMovementManager via a property
            // getter that NullRefs when ManagerSingleton<GM>.Instance is null.  No-op the Update
            // loop so the guard check inside can never execute the crashing property access.
            Type w1873Type = ResolveFromH3VR("FistVR.Winchester1873LoadingGate");
            if (w1873Type != null)
                PatchSafely(AccessTools.Method(w1873Type, "Update"), skip);

            // ForceTubeVRInterface.OnLoadRuntimeMethod calls DllImport'd ForceTubeVR_API_x64,
            // which throws DllNotFoundException and stops all physics in the editor.
            // No-op it so play mode works without the haptic feedback DLL installed.
            Type forceTubeType = ResolveFromH3VR("ForceTubeVRInterface");
            if (forceTubeType != null)
            {
                PatchSafely(AccessTools.Method(forceTubeType, "OnLoadRuntimeMethod"), skip);
                PatchSafely(AccessTools.Method(forceTubeType, "InitAsync"), skip);
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[PlayHelper] ApplyInitPatches failed: " + ex.Message
                + ". Managers will still be created but Awake exceptions may be logged.");
        }
    }

    private static void PatchSafely(MethodInfo method, HarmonyMethod prefix)
    {
        try
        {
            if (method == null)
            {
                Debug.LogWarning("[PlayHelper] PatchSafely: resolved method is null, skipping.");
                return;
            }
            _harmony.Patch(method, prefix: prefix);
            Log("Patched " + method.DeclaringType.Name + "." + method.Name);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[PlayHelper] Failed to patch "
                + (method != null ? method.DeclaringType.Name + "." + method.Name : "null")
                + ": " + ex.Message);
        }
    }

    // Sets ManagerSingleton<T>.Instance for managers whose Awake was no-op'd.
    private static void SetManagerSingletonInstance(Type managerType, GameObject go)
    {
        try
        {
            Type genericMs = ResolveFromH3VR("FistVR.ManagerSingleton`1");
            if (genericMs == null)
            {
                Debug.LogWarning("[PlayHelper] ManagerSingleton`1 not found in H3VRCode-CSharp.");
                return;
            }

            Type closedMs = genericMs.MakeGenericType(managerType);
            PropertyInfo prop = closedMs.GetProperty("Instance",
                BindingFlags.Public | BindingFlags.Static);
            if (prop == null) return;

            Component comp = go.GetComponent(managerType);
            if (comp == null) return;

            prop.SetValue(null, comp, null);
            Log("Set ManagerSingleton<" + managerType.Name + ">.Instance.");
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[PlayHelper] SetManagerSingletonInstance failed for "
                + managerType.Name + ": " + ex.Message);
        }
    }

    // Returns the existing manager GO if one already exists, otherwise creates a new one.
    // Returning the existing GO on re-entry ensures SetManagerSingletonInstance is always called
    // (domain reload resets all statics, but DontDestroyOnLoad objects persist).
    private static GameObject CreateEditorManager(Type componentType, string goName)
    {
        try
        {
            UnityEngine.Object existing = FindObjectOfType(componentType);
            if (existing != null)
            {
                Log(componentType.Name + " already in scene, reusing.");
                return ((Component)existing).gameObject;
            }

            var go = new GameObject(goName);
            go.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent(componentType);

            Log("Created " + componentType.Name + " (" + componentType.Assembly.GetName().Name + ")");
            return go;
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[PlayHelper] Failed to create " + componentType.Name
                + ": " + ex.Message);
            return null;
        }
    }

    // Wires GM.m_currentSceneSettings and calls InitEventSignalCollections so
    // FlushSignalCollections on play-exit doesn't NullRef on uninitialised slots.
    private static void SetCurrentSceneSettings(Type gmType, Type ssType, GameObject ssGO)
    {
        try
        {
            Component ssComp = ssGO.GetComponent(ssType);
            if (ssComp == null) return;

            UnityEngine.Object gmInst = FindObjectOfType(gmType);
            if (gmInst == null) return;

            FieldInfo field = gmType.GetField("m_currentSceneSettings",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field == null) return;

            field.SetValue(gmInst, ssComp);
            Log("Set GM.m_currentSceneSettings.");

            MethodInfo init = ssType.GetMethod("InitEventSignalCollections",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (init != null)
            {
                init.Invoke(ssComp, null);
                Log("Called InitEventSignalCollections().");
            }
            else
            {
                Debug.LogWarning("[PlayHelper] InitEventSignalCollections not found on FVRSceneSettings.");
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[PlayHelper] SetCurrentSceneSettings failed: " + ex.Message);
        }
    }

    // Unity 5.6 FindObjectOfType skips inactive objects; this variant includes them.
    private static UnityEngine.Object FindObjectOfType(Type type)
    {
        UnityEngine.Object[] found = Resources.FindObjectsOfTypeAll(type);
        if (found == null) return null;
        for (int i = 0; i < found.Length; i++)
        {
            if (found[i] != null) return found[i];
        }
        return null;
    }

    // Seeds SM.m_prefabBindingDic with a dummy AudioSource GO per FVRPooledAudioType
    // so AudioSourcePool..ctor doesn't throw KeyNotFoundException.
    private static void PopulateSMPrefabBindingDictionary(Type smType)
    {
        try
        {
            UnityEngine.Object smInst = FindObjectOfType(smType);
            if (smInst == null) return;

            FieldInfo dictField = smType.GetField("m_prefabBindingDic",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (dictField == null)
            {
                Debug.LogWarning("[PlayHelper] m_prefabBindingDic field not found on SM.");
                return;
            }

            object dict = dictField.GetValue(smInst);
            if (dict == null) return;

            Type audioTypeEnum  = ResolveFromH3VR("FistVR.FVRPooledAudioType");
            Type bindingType    = ResolveFromH3VR("FistVR.PoolTypePrefabBinding");
            if (audioTypeEnum == null || bindingType == null) return;

            FieldInfo fType   = bindingType.GetField("Type",
                BindingFlags.Public | BindingFlags.Instance);
            FieldInfo fPrefab = bindingType.GetField("Prefab",
                BindingFlags.Public | BindingFlags.Instance);

            GameObject dummyAudio = new GameObject("[EditorAudioPool]");
            dummyAudio.hideFlags = HideFlags.HideAndDontSave;
            dummyAudio.AddComponent<AudioSource>();
            UnityEngine.Object.DontDestroyOnLoad(dummyAudio);

            MethodInfo addMethod   = dict.GetType().GetMethod("Add");
            MethodInfo clearMethod = dict.GetType().GetMethod("Clear");
            if (addMethod == null) return;

            // Clear first so stale entries from previous sessions don't reference dead GOs.
            if (clearMethod != null) clearMethod.Invoke(dict, null);

            Array enumValues = Enum.GetValues(audioTypeEnum);
            int added = 0;
            for (int i = 0; i < enumValues.Length; i++)
            {
                object enumVal = enumValues.GetValue(i);
                int intKey = Convert.ToInt32(enumVal);

                object binding = Activator.CreateInstance(bindingType);
                if (fType   != null) fType.SetValue(binding, enumVal);
                if (fPrefab != null) fPrefab.SetValue(binding, dummyAudio);

                addMethod.Invoke(dict, new object[] { intKey, binding });
                added++;
            }
            Log("Seeded SM.m_prefabBindingDic with " + added + " entries.");
        }
        catch (Exception ex)
        {
            Exception inner = ex.InnerException != null ? ex.InnerException : ex;
            Debug.LogWarning("[PlayHelper] PopulateSMPrefabBindingDictionary failed: " + inner.Message);
        }
    }

    // Calls SM.WarmupGenericPools so pool fields are non-null when play exits.
    private static void WarmupSMGenericPools(Type smType)
    {
        try
        {
            MethodInfo warmup = smType.GetMethod("WarmupGenericPools",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            if (warmup == null)
            {
                Debug.LogWarning("[PlayHelper] WarmupGenericPools not found on SM.");
                return;
            }
            warmup.Invoke(null, null);
            Log("SM.WarmupGenericPools completed.");
        }
        catch (Exception ex)
        {
            // MethodInfo.Invoke wraps real errors in TargetInvocationException; log InnerException.
            Exception inner = ex.InnerException != null ? ex.InnerException : ex;
            Debug.LogWarning("[PlayHelper] WarmupSMGenericPools failed: " + inner.GetType().Name + ": " + inner.Message);
        }
    }

    // Disables a component's Update loop without removing the GO or nulling Instance.
    private static void DisableComponent(GameObject go, Type componentType)
    {
        try
        {
            Component comp = go.GetComponent(componentType);
            if (comp != null)
            {
                ((MonoBehaviour)comp).enabled = false;
                Log("Disabled " + componentType.Name + " Update loop.");
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[PlayHelper] DisableComponent failed for "
                + componentType.Name + ": " + ex.Message);
        }
    }

    // Seeds AM.MechanicalAccuracyDic with zero-spread entries so GetFireArmMechanicalSpread
    // doesn't throw KeyNotFoundException on every FVRFireArm.Awake.
    private static void PopulateAMAccuracyDictionary(Type amType)
    {
        try
        {
            UnityEngine.Object amInst = FindObjectOfType(amType);
            if (amInst == null) return;

            FieldInfo dictField = amType.GetField("MechanicalAccuracyDic",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (dictField == null)
            {
                Debug.LogWarning("[PlayHelper] MechanicalAccuracyDic field not found on AM.");
                return;
            }

            object dict = dictField.GetValue(amInst);
            if (dict == null) return;

            Type accuracyClassType = ResolveFromH3VR("FistVR.FVRFireArmMechanicalAccuracyClass");
            // Nested type — reflection uses '+' as separator
            Type entryType = ResolveFromH3VR(
                "FistVR.FVRFireArmMechanicalAccuracyChart+MechanicalAccuracyEntry");
            if (accuracyClassType == null || entryType == null) return;

            MethodInfo addMethod   = dict.GetType().GetMethod("Add");
            MethodInfo clearMethod = dict.GetType().GetMethod("Clear");
            if (addMethod == null) return;

            if (clearMethod != null) clearMethod.Invoke(dict, null);

            Array enumValues = Enum.GetValues(accuracyClassType);
            int added = 0;
            for (int i = 0; i < enumValues.Length; i++)
            {
                object key   = enumValues.GetValue(i);
                object entry = Activator.CreateInstance(entryType);
                addMethod.Invoke(dict, new object[] { key, entry });
                added++;
            }
            Log("Seeded AM.MechanicalAccuracyDic with " + added + " entries.");
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[PlayHelper] PopulateAMAccuracyDictionary failed: " + ex.Message);
        }
    }

    // Seeds SM.m_impactDic with empty AudioEvents for every ImpactType/Mat/Intensity combo.
    private static void SeedSMImpactDictionary(Type smType)
    {
        try
        {
            UnityEngine.Object smInst = FindObjectOfType(smType);
            if (smInst == null) return;

            Type impactType   = ResolveFromH3VR("FistVR.ImpactType");
            Type matType      = ResolveFromH3VR("FistVR.MatSoundType");
            Type intensityType = ResolveFromH3VR("FistVR.AudioImpactIntensity");
            Type audioEvType  = ResolveFromH3VR("FistVR.AudioEvent");
            if (impactType == null || matType == null || intensityType == null || audioEvType == null)
            {
                Debug.LogWarning("[PlayHelper] SeedSMImpactDictionary: could not resolve enum/AudioEvent types.");
                return;
            }

            // Build the three-level nested generic Dictionary types at runtime.
            Type genericDic = typeof(System.Collections.Generic.Dictionary<,>);
            Type d3 = genericDic.MakeGenericType(intensityType, audioEvType);
            Type d2 = genericDic.MakeGenericType(matType, d3);
            Type d1 = genericDic.MakeGenericType(impactType, d2);

            MethodInfo add1 = d1.GetMethod("Add");
            MethodInfo add2 = d2.GetMethod("Add");
            MethodInfo add3 = d3.GetMethod("Add");

            object outerDict = Activator.CreateInstance(d1);

            Array impactValues    = Enum.GetValues(impactType);
            Array matValues       = Enum.GetValues(matType);
            Array intensityValues = Enum.GetValues(intensityType);

            for (int i = 0; i < impactValues.Length; i++)
            {
                object impactKey = impactValues.GetValue(i);
                object mid = Activator.CreateInstance(d2);
                for (int m = 0; m < matValues.Length; m++)
                {
                    object matKey = matValues.GetValue(m);
                    object inner = Activator.CreateInstance(d3);
                    for (int k = 0; k < intensityValues.Length; k++)
                    {
                        object intKey = intensityValues.GetValue(k);
                        object ae    = Activator.CreateInstance(audioEvType);
                        add3.Invoke(inner, new[] { intKey, ae });
                    }
                    add2.Invoke(mid, new[] { matKey, inner });
                }
                add1.Invoke(outerDict, new[] { impactKey, mid });
            }

            FieldInfo field = smType.GetField("m_impactDic",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
            {
                field.SetValue(smInst, outerDict);
                Log("Seeded SM.m_impactDic with " + impactValues.Length + " ImpactType entries.");
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[PlayHelper] SeedSMImpactDictionary failed: " + ex.Message);
        }
    }

    // Seeds FXM.muzzleDic with dummy MuzzleEffectConfig entries backed by invisible
    // ParticleSystem GOs so RegenerateMuzzleEffects doesn't throw KeyNotFoundException.
    private static void PopulateFXMMuzzleDictionary(Type fxmType)
    {
        try
        {
            UnityEngine.Object fxmInst = FindObjectOfType(fxmType);
            if (fxmInst == null) return;

            FieldInfo dictField = fxmType.GetField("muzzleDic",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (dictField == null)
            {
                Debug.LogWarning("[PlayHelper] muzzleDic field not found on FXM.");
                return;
            }

            object dict = dictField.GetValue(fxmInst);
            if (dict == null) return;

            Type entryEnumType = ResolveFromH3VR("FistVR.MuzzleEffectEntry");
            Type configType    = ResolveFromH3VR("FistVR.MuzzleEffectConfig");
            if (entryEnumType == null || configType == null) return;

            FieldInfo fEntry     = configType.GetField("Entry",
                BindingFlags.Public | BindingFlags.Instance);
            FieldInfo fHighlight = configType.GetField("Prefabs_Highlight",
                BindingFlags.Public | BindingFlags.Instance);
            FieldInfo fLowlight  = configType.GetField("Prefabs_Lowlight",
                BindingFlags.Public | BindingFlags.Instance);
            FieldInfo fNumHigh   = configType.GetField("NumParticles_Highlight",
                BindingFlags.Public | BindingFlags.Instance);
            FieldInfo fNumLow    = configType.GetField("NumParticles_Lowlight",
                BindingFlags.Public | BindingFlags.Instance);

            const int numSizes = 5; // one per MuzzleEffectSize: Subdued/Standard/Large/Huge/Oversize
            var dummyPrefabs = new System.Collections.Generic.List<GameObject>(numSizes);
            var dummyInts    = new System.Collections.Generic.List<int>(numSizes);
            for (int s = 0; s < numSizes; s++)
            {
                GameObject ps = new GameObject("[EditorMuzzleFX]");
                ps.hideFlags = HideFlags.HideAndDontSave;
                ps.AddComponent<ParticleSystem>();
                // Disable renderer to prevent pink blobs from missing material in editor.
                ParticleSystemRenderer psr = ps.GetComponent<ParticleSystemRenderer>();
                if (psr != null) psr.enabled = false;
                UnityEngine.Object.DontDestroyOnLoad(ps);
                dummyPrefabs.Add(ps);
                dummyInts.Add(0);
            }

            MethodInfo addMethod   = dict.GetType().GetMethod("Add");
            MethodInfo clearMethod = dict.GetType().GetMethod("Clear");
            if (addMethod == null) return;

            // Clear so stale ScriptableObject configs from prior sessions don't linger.
            if (clearMethod != null) clearMethod.Invoke(dict, null);

            object noneValue = null; // None entry is never looked up, skip it.
            try { noneValue = Enum.Parse(entryEnumType, "None"); }
            catch (Exception) { /* enum may not define None */ }

            Array enumValues = Enum.GetValues(entryEnumType);
            int added = 0;
            for (int i = 0; i < enumValues.Length; i++)
            {
                object key = enumValues.GetValue(i);
                if (noneValue != null && key.Equals(noneValue)) continue;

                ScriptableObject config = ScriptableObject.CreateInstance(configType);
                if (fEntry     != null) fEntry.SetValue(config, key);
                if (fHighlight != null) fHighlight.SetValue(config, dummyPrefabs);
                if (fLowlight  != null) fLowlight.SetValue(config, dummyPrefabs);
                if (fNumHigh   != null) fNumHigh.SetValue(config, dummyInts);
                if (fNumLow    != null) fNumLow.SetValue(config, dummyInts);

                try
                {
                    addMethod.Invoke(dict, new object[] { key, config });
                    added++;
                }
                catch (Exception)
                {
                    // Key already present from Resources.LoadAll — fine
                }
            }
            Log("Seeded FXM.muzzleDic with " + added + " entries.");
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[PlayHelper] PopulateFXMMuzzleDictionary failed: " + ex.Message);
        }
    }

    // Searches H3VRCode-CSharp (and firstpass) assemblies for a named type.
    private static Type ResolveFromH3VR(string fullTypeName)
    {
        Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
        for (int i = 0; i < assemblies.Length; i++)
        {
            Assembly asm = assemblies[i];
            if (asm == null) continue;

            string name = asm.GetName().Name;
            if (name != "H3VRCode-CSharp" && name != "H3VRCode-CSharp-firstpass")
                continue;

            Type t = asm.GetType(fullTypeName);
            if (t != null) return t;
        }
        return null;
    }

#endif // UNITY_EDITOR
}
