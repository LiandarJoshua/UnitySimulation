#if UNITY_EDITOR
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Editor tool that builds the WorkerAnimatorController and fixes the Worker prefab
/// to exactly match the reference project specification:
///
///   WorkerAnimatorController (Assets/OilRefinery/WorkerAnimatorController.controller)
///     - One layer, one Locomotion state with a 1D BlendTree on float "Speed"
///     - Thresholds: 0 = newcharidle, 2 = newcharwalk, 5.5 = newcharrun
///     - Each clip sourced from the mixamo.com clip inside the respective FBX
///
///   Worker.prefab (Assets/OilRefinery/Worker.prefab)
///     Root GameObject:
///       NavMeshAgent  (radius 0.3, height 1.8, speed 2, accel 8, High Quality avoidance)
///       CapsuleCollider (center 0,0.9,0 radius 0.3 height 1.8)
///       Animator      (applyRootMotion=false, cullingMode=AlwaysAnimate,
///                      runtimeAnimatorController = WorkerAnimatorController,
///                      avatar extracted from Ch17_nonPBR.fbx)
///       AgentController (OilRefinery system) or AgentBrain (Scripts/Simulation system)
///     Child mesh: Ch17_nonPBR FBX mesh object (NO Animator component on it)
/// </summary>
public static class WorkerAnimatorSetup
{
    // ─── Paths ────────────────────────────────────────────────────────────────

    private const string ControllerPath = "Assets/OilRefinery/WorkerAnimatorController.controller";
    private const string PrefabPath     = "Assets/OilRefinery/Worker.prefab";
    private const string WorkerFbxPath  = "Assets/OilRefinery/worker.fbx";
    private const string MeshFbxPath    = "Assets/OilRefinery/Ch17_nonPBR.fbx";
    private const string MeshPrefabPath = "Assets/OilRefinery/Ch17_nonPBR.prefab";
    private const string IdleFbxPath    = "Assets/OilRefinery/newcharidle.fbx";
    private const string WalkFbxPath    = "Assets/OilRefinery/newcharwalk.fbx";
    private const string RunFbxPath     = "Assets/OilRefinery/newcharrun.fbx";

    // ─── Menu Items ───────────────────────────────────────────────────────────

    [MenuItem("Tools/Worker/Setup Animator + Prefab")]
    public static void SetupAll()
    {
        RepairYamlGuids();
        AssetDatabase.Refresh();

        AnimatorController controller = BuildController();
        if (controller == null)
        {
            Debug.LogError("[WorkerAnimatorSetup] Controller build failed — aborting prefab setup.");
            return;
        }

        FixPrefab(controller);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[WorkerAnimatorSetup] Done — WorkerAnimatorController and Worker.prefab are ready.");
    }

    [MenuItem("Tools/Worker/Rebuild Animator Controller Only")]
    public static AnimatorController BuildControllerOnly()
    {
        AnimatorController c = BuildController();
        AssetDatabase.SaveAssets();
        return c;
    }

    [MenuItem("Tools/Worker/Fix Prefab Only")]
    public static void FixPrefabOnly()
    {
        RepairYamlGuids();
        AssetDatabase.Refresh();

        AnimatorController controller =
            AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
        if (controller == null)
        {
            Debug.LogError("[WorkerAnimatorSetup] Controller not found. Run 'Setup Animator + Prefab' first.");
            return;
        }
        FixPrefab(controller);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    [MenuItem("Tools/Worker/Repair Prefab YAML GUIDs")]
    public static void RepairPrefabYamlGuids()
    {
        RepairYamlGuids();
        AssetDatabase.Refresh();
        Debug.Log("[WorkerAnimatorSetup] Worker.prefab YAML GUIDs repaired. " +
                  "Run 'Setup Animator + Prefab' next.");
    }

    /// <summary>
    /// Fixes the EvacAgent.prefab Animator (assigns missing avatar) and replaces
    /// the broken AgentStatusIndicator script on StatusDisc with EvacAgentIndicator.
    /// Also repairs the scene Worker instance overrides (null controller / null avatar).
    /// Run this once after reimporting the project.
    /// </summary>
    [MenuItem("Tools/Worker/Fix EvacAgent Prefab + Scene Worker")]
    public static void FixEvacAgentPrefab()
    {
        const string evacPrefabPath = "Assets/Prefabs/EvacAgent.prefab";
        const string scenePath      = "Assets/OilRefinery/Scenes/DaySample.unity";

        // ── 1. Load avatar from Ch17_nonPBR.fbx ──────────────────────────────
        Avatar avatar = LoadAvatar(MeshFbxPath);
        if (avatar == null)
        {
            Debug.LogError("[WorkerAnimatorSetup] Could not load avatar from Ch17_nonPBR.fbx.");
            return;
        }

        // ── 2. Patch EvacAgent.prefab ─────────────────────────────────────────
        using (var scope = new PrefabUtility.EditPrefabContentsScope(evacPrefabPath))
        {
            GameObject root = scope.prefabContentsRoot;

            // Assign avatar on root Animator.
            Animator anim = root.GetComponent<Animator>();
            if (anim != null)
            {
                anim.avatar          = avatar;
                anim.applyRootMotion = false;
                anim.cullingMode     = AnimatorCullingMode.AlwaysAnimate;
                Debug.Log("[WorkerAnimatorSetup] EvacAgent: avatar assigned.");
            }
            else
            {
                Debug.LogWarning("[WorkerAnimatorSetup] EvacAgent: no Animator found on root.");
            }

            // Fix StatusDisc: replace broken AgentStatusIndicator with EvacAgentIndicator.
            Transform statusDisc = root.transform.Find("StatusDisc");
            if (statusDisc != null)
            {
                // Remove any MonoBehaviours with missing scripts first.
                int removed = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(statusDisc.gameObject);
                if (removed > 0)
                    Debug.Log($"[WorkerAnimatorSetup] EvacAgent StatusDisc: removed {removed} missing script(s).");

                // Add EvacAgentIndicator if not present.
                EvacAgentIndicator indicator = statusDisc.GetComponent<EvacAgentIndicator>();
                if (indicator == null)
                {
                    indicator = statusDisc.gameObject.AddComponent<EvacAgentIndicator>();
                    Debug.Log("[WorkerAnimatorSetup] EvacAgent StatusDisc: added EvacAgentIndicator.");
                }

                // Wire the AgentBrain's statusIndicator reference.
                AgentBrain brain = root.GetComponent<AgentBrain>();
                if (brain != null)
                {
                    // Use SerializedObject so private field is reachable from the editor.
                    var so = new UnityEditor.SerializedObject(brain);
                    var prop = so.FindProperty("statusIndicator");
                    if (prop != null)
                    {
                        prop.objectReferenceValue = indicator;
                        so.ApplyModifiedPropertiesWithoutUndo();
                        Debug.Log("[WorkerAnimatorSetup] EvacAgent: wired statusIndicator reference.");
                    }
                }
            }
            else
            {
                Debug.LogWarning("[WorkerAnimatorSetup] EvacAgent: StatusDisc child not found.");
            }

            EditorUtility.SetDirty(root);
        }

        // ── 3. Fix scene Worker instance (clear null-override on Animator) ────
        // Open scene without loading it additively so we can get the object.
        var scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
            scenePath, UnityEditor.SceneManagement.OpenSceneMode.Additive);

        try
        {
            AnimatorController workerController =
                AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);

            foreach (GameObject go in scene.GetRootGameObjects())
            {
                // Find the Worker inside EventSystem or at root.
                Animator[] animators = go.GetComponentsInChildren<Animator>(true);
                foreach (Animator a in animators)
                {
                    if (a.gameObject.name != "Worker") continue;

                    if (a.runtimeAnimatorController == null && workerController != null)
                    {
                        a.runtimeAnimatorController = workerController;
                        Debug.Log("[WorkerAnimatorSetup] Scene Worker: assigned WorkerAnimatorController.");
                    }
                    if (a.avatar == null)
                    {
                        a.avatar = avatar;
                        Debug.Log("[WorkerAnimatorSetup] Scene Worker: assigned avatar.");
                    }
                    a.applyRootMotion = false;
                    a.cullingMode     = AnimatorCullingMode.AlwaysAnimate;
                    EditorUtility.SetDirty(a);
                }
            }

            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
        }
        finally
        {
            UnityEditor.SceneManagement.EditorSceneManager.CloseScene(scene, false);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[WorkerAnimatorSetup] Done — EvacAgent.prefab and scene Worker are fixed.");
    }

    [MenuItem("Tools/Worker/Remove Missing Scripts from Worker Prefab")]
    public static void RemoveMissingScripts()
    {
        using (var scope = new PrefabUtility.EditPrefabContentsScope(PrefabPath))
        {
            GameObject root   = scope.prefabContentsRoot;
            int        removed = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(root);

            // Also check all children recursively.
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
            {
                removed += GameObjectUtility.RemoveMonoBehavioursWithMissingScript(child.gameObject);
            }

            if (removed > 0)
            {
                EditorUtility.SetDirty(root);
                Debug.Log($"[WorkerAnimatorSetup] Removed {removed} missing script(s) from Worker prefab.");
            }
            else
            {
                Debug.Log("[WorkerAnimatorSetup] No missing scripts found in Worker prefab.");
            }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    // ─── Controller Builder ───────────────────────────────────────────────────

    private static AnimatorController BuildController()
    {
        // Load FBX clips — each animation FBX contains exactly one clip named "mixamo.com".
        AnimationClip idleClip = LoadFbxClip(IdleFbxPath);
        AnimationClip walkClip = LoadFbxClip(WalkFbxPath);
        AnimationClip runClip  = LoadFbxClip(RunFbxPath);

        if (idleClip == null || walkClip == null || runClip == null)
        {
            Debug.LogError("[WorkerAnimatorSetup] One or more FBX animation clips could not be loaded. " +
                           "Check that newcharidle.fbx / newcharwalk.fbx / newcharrun.fbx exist in Assets/OilRefinery/.");
            return null;
        }

        // Create or overwrite the controller asset.
        AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);

        // ── Speed parameter ───────────────────────────────────────────────────
        controller.AddParameter("Speed", AnimatorControllerParameterType.Float);

        // ── Blend tree ────────────────────────────────────────────────────────
        AnimatorStateMachine sm = controller.layers[0].stateMachine;

        // Remove the default empty state Unity inserts.
        foreach (var s in sm.states)
            sm.RemoveState(s.state);

        BlendTree blendTree;
        AnimatorState locomotionState = controller.CreateBlendTreeInController(
            "Locomotion", out blendTree);

        blendTree.blendType      = BlendTreeType.Simple1D;
        blendTree.blendParameter = "Speed";
        blendTree.useAutomaticThresholds = false;

        blendTree.AddChild(idleClip, 0f);
        blendTree.AddChild(walkClip, 2f);
        blendTree.AddChild(runClip,  5.5f);

        sm.defaultState = locomotionState;

        EditorUtility.SetDirty(controller);
        Debug.Log($"[WorkerAnimatorSetup] Created {ControllerPath} with Idle/Walk/Run blend tree.");
        return controller;
    }

    // ─── Prefab Fixer ─────────────────────────────────────────────────────────

    private static void FixPrefab(AnimatorController controller)
    {
        GameObject prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (prefabAsset == null)
        {
            Debug.LogError($"[WorkerAnimatorSetup] Worker.prefab not found at '{PrefabPath}'.");
            return;
        }

        // Load avatar from Ch17_nonPBR.fbx
        Avatar avatar = LoadAvatar(MeshFbxPath);

        using (var scope = new PrefabUtility.EditPrefabContentsScope(PrefabPath))
        {
            GameObject root = scope.prefabContentsRoot;

            // ── Remove any broken (missing prefab) child objects ──────────────
            // These are children whose underlying nested prefab asset is missing,
            // which prevents saving. Collect them first, then destroy.
            var childrenToRemove = root.transform
                .Cast<Transform>()
                .Where(t => PrefabUtility.GetPrefabAssetType(t.gameObject) == PrefabAssetType.MissingAsset)
                .Select(t => t.gameObject)
                .ToList();

            foreach (GameObject broken in childrenToRemove)
            {
                Debug.Log($"[WorkerAnimatorSetup] Removing broken child '{broken.name}' (missing prefab asset).");
                Object.DestroyImmediate(broken);
            }

            // ── Re-add the mesh child from the repaired prefab asset ──────────
            GameObject meshPrefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(MeshPrefabPath);
            if (meshPrefabAsset != null)
            {
                bool meshChildExists = root.transform
                    .Cast<Transform>()
                    .Any(t => t.name == meshPrefabAsset.name);

                if (!meshChildExists)
                {
                    GameObject meshInstance = (GameObject)PrefabUtility.InstantiatePrefab(meshPrefabAsset, root.transform);
                    meshInstance.transform.localPosition = Vector3.zero;
                    meshInstance.transform.localRotation = Quaternion.identity;
                    meshInstance.transform.localScale    = Vector3.one;
                    Debug.Log($"[WorkerAnimatorSetup] Added '{meshPrefabAsset.name}' mesh child to Worker root.");
                }
            }
            else
            {
                Debug.LogWarning($"[WorkerAnimatorSetup] Mesh prefab not found at '{MeshPrefabPath}'. " +
                                 "Run 'Repair Missing Mesh Prefab' first.");
            }

            // ── Remove any Animator on child mesh objects ─────────────────────
            foreach (Animator childAnim in root.GetComponentsInChildren<Animator>(true))
            {
                if (childAnim.gameObject != root)
                {
                    Object.DestroyImmediate(childAnim);
                    Debug.Log($"[WorkerAnimatorSetup] Removed Animator from child '{childAnim?.name}'.");
                }
            }

            // ── Animator on root ──────────────────────────────────────────────
            Animator anim = root.GetComponent<Animator>();
            if (anim == null) anim = root.AddComponent<Animator>();

            anim.runtimeAnimatorController = controller;
            anim.applyRootMotion           = false;
            anim.cullingMode               = AnimatorCullingMode.AlwaysAnimate;

            if (avatar != null)
                anim.avatar = avatar;
            else
                Debug.LogWarning("[WorkerAnimatorSetup] Ch17_nonPBR avatar not found — avatar not set on Animator.");

            // ── NavMeshAgent ──────────────────────────────────────────────────
            NavMeshAgent nav = root.GetComponent<NavMeshAgent>();
            if (nav == null) nav = root.AddComponent<NavMeshAgent>();

            nav.radius           = 0.3f;
            nav.height           = 1.8f;
            nav.speed            = 2f;
            nav.acceleration     = 8f;
            nav.angularSpeed     = 180f;
            nav.stoppingDistance = 0.1f;
            nav.obstacleAvoidanceType = ObstacleAvoidanceType.HighQualityObstacleAvoidance;

            // ── CapsuleCollider ───────────────────────────────────────────────
            CapsuleCollider col = root.GetComponent<CapsuleCollider>();
            if (col == null) col = root.AddComponent<CapsuleCollider>();

            col.center = new Vector3(0f, 0.9f, 0f);
            col.radius = 0.3f;
            col.height = 1.8f;

            // ── Ensure AgentController is present (OilRefinery system) ────────
            if (root.GetComponent<AgentController>() == null &&
                root.GetComponent<AgentBrain>() == null)
            {
                root.AddComponent<AgentController>();
                Debug.Log("[WorkerAnimatorSetup] Added AgentController to Worker root.");
            }

            EditorUtility.SetDirty(root);
        }

        Debug.Log($"[WorkerAnimatorSetup] Fixed {PrefabPath}.");
    }

    /// <summary>
    /// Ensures the Ch17_nonPBR prefab asset exists, creating it from the FBX if missing.
    /// Returns true if the prefab is ready to use.
    /// </summary>
    private static bool EnsureMeshPrefab()
    {
        GameObject existing = AssetDatabase.LoadAssetAtPath<GameObject>(MeshPrefabPath);
        if (existing != null)
        {
            Debug.Log($"[WorkerAnimatorSetup] Mesh prefab already exists at '{MeshPrefabPath}'.");
            return true;
        }

        GameObject fbxRoot = AssetDatabase.LoadAssetAtPath<GameObject>(MeshFbxPath);
        if (fbxRoot == null)
        {
            Debug.LogError($"[WorkerAnimatorSetup] FBX not found at '{MeshFbxPath}'.");
            return false;
        }

        // Instantiate temporarily to create a proper prefab from the FBX.
        GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(fbxRoot);
        if (instance == null)
        {
            Debug.LogError($"[WorkerAnimatorSetup] Failed to instantiate FBX '{MeshFbxPath}'.");
            return false;
        }

        string dir = Path.GetDirectoryName(MeshPrefabPath);
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        GameObject savedPrefab = PrefabUtility.SaveAsPrefabAsset(instance, MeshPrefabPath);
        Object.DestroyImmediate(instance);

        if (savedPrefab == null)
        {
            Debug.LogError($"[WorkerAnimatorSetup] Failed to save prefab to '{MeshPrefabPath}'.");
            return false;
        }

        Debug.Log($"[WorkerAnimatorSetup] Created mesh prefab at '{MeshPrefabPath}'.");
        AssetDatabase.Refresh();
        return true;
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Patches the Worker.prefab YAML on disk before Unity loads it, replacing any
    /// stale/broken GUIDs with the correct ones resolved from the live AssetDatabase.
    /// This must run before EditPrefabContentsScope is opened.
    /// </summary>
    private static void RepairYamlGuids()
    {
        if (!File.Exists(PrefabPath))
        {
            Debug.LogWarning($"[WorkerAnimatorSetup] Worker.prefab not found at '{PrefabPath}' — nothing to repair.");
            return;
        }

        string yaml = File.ReadAllText(PrefabPath);
        bool changed = false;

        // ── Broken FBX GUID → real Ch17_nonPBR.fbx GUID ──────────────────────
        const string BrokenFbxGuid = "472d0e167d6e5c14897a36ae995f0b4d";
        string realFbxGuid = AssetDatabase.AssetPathToGUID(MeshFbxPath);
        if (!string.IsNullOrEmpty(realFbxGuid) && yaml.Contains(BrokenFbxGuid))
        {
            yaml    = yaml.Replace(BrokenFbxGuid, realFbxGuid);
            changed = true;
            Debug.Log($"[WorkerAnimatorSetup] Replaced broken FBX GUID {BrokenFbxGuid} → {realFbxGuid}");
        }

        // ── Broken AgentController script GUID → real GUID ───────────────────
        const string BrokenScriptGuid = "b10ab68b2f10b12429b6076c556190c6";
        MonoScript agentScript = FindMonoScript<AgentController>();
        if (agentScript != null)
        {
            string realScriptGuid = AssetDatabase.AssetPathToGUID(
                AssetDatabase.GetAssetPath(agentScript));
            if (!string.IsNullOrEmpty(realScriptGuid) && yaml.Contains(BrokenScriptGuid))
            {
                yaml    = yaml.Replace(BrokenScriptGuid, realScriptGuid);
                changed = true;
                Debug.Log($"[WorkerAnimatorSetup] Replaced broken AgentController GUID {BrokenScriptGuid} → {realScriptGuid}");
            }
        }
        else
        {
            Debug.LogWarning("[WorkerAnimatorSetup] AgentController MonoScript not found — script GUID not repaired.");
        }

        if (changed)
            File.WriteAllText(PrefabPath, yaml);
        else
            Debug.Log("[WorkerAnimatorSetup] Worker.prefab YAML GUIDs are already correct — no changes made.");
    }

    /// <summary>
    /// Finds the MonoScript asset for a given type using the AssetDatabase.
    /// </summary>
    private static MonoScript FindMonoScript<T>()
    {
        string typeName = typeof(T).Name;
        string[] guids  = AssetDatabase.FindAssets($"t:MonoScript {typeName}");
        foreach (string guid in guids)
        {
            string path   = AssetDatabase.GUIDToAssetPath(guid);
            MonoScript ms = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
            if (ms != null && ms.GetClass() == typeof(T))
                return ms;
        }
        return null;
    }

    /// <summary>
    /// Loads the first AnimationClip from an FBX asset (the mixamo.com clip).
    /// </summary>
    private static AnimationClip LoadFbxClip(string fbxPath)
    {
        Object[] assets = AssetDatabase.LoadAllAssetsAtPath(fbxPath);
        foreach (Object obj in assets)
        {
            if (obj is AnimationClip clip && !clip.name.Contains("__preview__"))
                return clip;
        }
        Debug.LogWarning($"[WorkerAnimatorSetup] No AnimationClip found in '{fbxPath}'.");
        return null;
    }

    /// <summary>
    /// Loads the Avatar sub-asset from the Ch17_nonPBR.fbx model importer.
    /// </summary>
    private static Avatar LoadAvatar(string fbxPath)
    {
        Object[] assets = AssetDatabase.LoadAllAssetsAtPath(fbxPath);
        foreach (Object obj in assets)
        {
            if (obj is Avatar avatar)
                return avatar;
        }
        return null;
    }
}
#endif
