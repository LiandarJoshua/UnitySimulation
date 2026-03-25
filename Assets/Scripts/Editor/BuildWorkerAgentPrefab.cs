#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Builds Assets/Prefabs/WorkerAgent.prefab from scratch.
/// The prefab uses newcharidle.fbx as the skinned mesh and avatar source,
/// since that FBX is already imported as Humanoid and contains the full skeleton.
/// Run via: Tools/Build WorkerAgent Prefab
/// Then assign WorkerAgent.prefab to the agentPrefab field on EvacSimManager.
/// </summary>
public static class BuildWorkerAgentPrefab
{
    private const string OutputPrefabPath   = "Assets/Prefabs/WorkerAgent.prefab";
    private const string MeshFbxPath        = "Assets/OilRefinery/newcharidle.fbx";
    private const string ControllerPath     = "Assets/Scripts/Simulation/AgentAnimatorController.controller";
    private const string IdleFbxPath        = "Assets/OilRefinery/newcharidle.fbx";
    private const string WalkFbxPath        = "Assets/OilRefinery/newcharwalk.fbx";
    private const string RunFbxPath         = "Assets/OilRefinery/newcharrun.fbx";

    [MenuItem("Tools/Build WorkerAgent Prefab")]
    public static void Build()
    {
        // ── 1. Fix FBX clip loop settings ────────────────────────────────────
        FixClip(IdleFbxPath, "Idle", 0,   250);
        FixClip(WalkFbxPath, "Walk", 0,   50);
        FixClip(RunFbxPath,  "Run",  0,   21);

        // ── 2. Rebuild the AnimatorController ─────────────────────────────────
        AnimatorController controller = BuildController();
        if (controller == null)
        {
            Debug.LogError("[BuildWorkerAgentPrefab] Controller build failed — aborting.");
            return;
        }

        // ── 3. Load avatar from the idle FBX (already Humanoid) ───────────────
        Avatar avatar = null;
        foreach (Object obj in AssetDatabase.LoadAllAssetsAtPath(MeshFbxPath))
        {
            if (obj is Avatar a) { avatar = a; break; }
        }
        if (avatar == null)
        {
            Debug.LogError("[BuildWorkerAgentPrefab] No Avatar found in newcharidle.fbx. " +
                           "Ensure it is imported as Humanoid.");
            return;
        }

        // ── 4. Load the skinned mesh GameObject from the idle FBX ─────────────
        GameObject meshSource = AssetDatabase.LoadAssetAtPath<GameObject>(MeshFbxPath);
        if (meshSource == null)
        {
            Debug.LogError($"[BuildWorkerAgentPrefab] Could not load GameObject from {MeshFbxPath}.");
            return;
        }

        // ── 5. Build the prefab in a temporary scene ──────────────────────────
        // Create a root GameObject then instantiate the FBX mesh as a child.
        GameObject root = new GameObject("WorkerAgent");

        // NavMeshAgent on root.
        NavMeshAgent nav      = root.AddComponent<NavMeshAgent>();
        nav.radius            = 0.3f;
        nav.height            = 1.8f;
        nav.speed             = 2f;
        nav.acceleration      = 8f;
        nav.angularSpeed      = 180f;
        nav.stoppingDistance  = 0.1f;
        nav.obstacleAvoidanceType = ObstacleAvoidanceType.HighQualityObstacleAvoidance;

        // CapsuleCollider on root.
        CapsuleCollider col = root.AddComponent<CapsuleCollider>();
        col.center = new Vector3(0f, 0.9f, 0f);
        col.radius = 0.3f;
        col.height = 1.8f;

        // Animator on root — drives the humanoid skeleton inside the FBX child.
        Animator anim              = root.AddComponent<Animator>();
        anim.runtimeAnimatorController = controller;
        anim.avatar                = avatar;
        anim.applyRootMotion       = false;
        anim.cullingMode           = AnimatorCullingMode.AlwaysAnimate;

        // AgentBrain on root.
        root.AddComponent<AgentBrain>();

        // PanicBehavior on root.
        root.AddComponent<PanicBehavior>();

        // Instantiate the FBX mesh as a child (provides the skinned meshes + skeleton).
        GameObject meshChild = (GameObject)PrefabUtility.InstantiatePrefab(meshSource);
        meshChild.transform.SetParent(root.transform, false);
        meshChild.transform.localPosition = Vector3.zero;
        meshChild.transform.localRotation = Quaternion.identity;
        meshChild.transform.localScale    = Vector3.one;

        // Remove any Animator baked into the FBX child — root Animator drives it.
        Animator childAnim = meshChild.GetComponent<Animator>();
        if (childAnim != null) Object.DestroyImmediate(childAnim);

        // StatusDisc — small quad above the agent head for EvacAgentIndicator.
        GameObject disc = GameObject.CreatePrimitive(PrimitiveType.Quad);
        disc.name = "StatusDisc";
        disc.transform.SetParent(root.transform, false);
        disc.transform.localPosition = new Vector3(0f, 2.2f, 0f);
        disc.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        disc.transform.localScale    = new Vector3(0.5f, 0.5f, 0.5f);
        Object.DestroyImmediate(disc.GetComponent<MeshCollider>());

        EvacAgentIndicator indicator = disc.AddComponent<EvacAgentIndicator>();

        // Wire statusIndicator on AgentBrain via SerializedObject.
        AgentBrain brain = root.GetComponent<AgentBrain>();
        var so   = new SerializedObject(brain);
        var prop = so.FindProperty("statusIndicator");
        if (prop != null)
        {
            prop.objectReferenceValue = indicator;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // ── 6. Save as prefab ─────────────────────────────────────────────────
        GameObject savedPrefab = PrefabUtility.SaveAsPrefabAsset(root, OutputPrefabPath);
        Object.DestroyImmediate(root);

        if (savedPrefab == null)
        {
            Debug.LogError($"[BuildWorkerAgentPrefab] Failed to save prefab to {OutputPrefabPath}.");
            return;
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"[BuildWorkerAgentPrefab] Done — saved to {OutputPrefabPath}. " +
                  "Assign it to the agentPrefab field on EvacSimManager.");
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Rebuilds AgentAnimatorController with a correct 1D Speed blend tree.
    /// Thresholds: 0 = Idle, 2 = Walk, 5.5 = Run.
    /// </summary>
    private static AnimatorController BuildController()
    {
        AnimationClip idle = LoadClip(IdleFbxPath, "Idle");
        AnimationClip walk = LoadClip(WalkFbxPath, "Walk");
        AnimationClip run  = LoadClip(RunFbxPath,  "Run");

        if (idle == null || walk == null || run == null)
        {
            Debug.LogError("[BuildWorkerAgentPrefab] One or more animation clips missing after reimport.");
            return null;
        }

        AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
        controller.AddParameter("Speed", AnimatorControllerParameterType.Float);

        AnimatorStateMachine sm = controller.layers[0].stateMachine;
        foreach (var s in sm.states) sm.RemoveState(s.state);

        BlendTree tree;
        AnimatorState state = controller.CreateBlendTreeInController("Locomotion", out tree);
        tree.blendType              = BlendTreeType.Simple1D;
        tree.blendParameter         = "Speed";
        tree.useAutomaticThresholds = false;
        tree.AddChild(idle, 0f);
        tree.AddChild(walk, 2f);
        tree.AddChild(run,  5.5f);

        // Set the blend tree range explicitly after children are added.
        // Unity derives minThreshold/maxThreshold from the first/last child
        // thresholds when useAutomaticThresholds is true, but when false we
        // must set them so the parameter range covers 0..5.5 correctly.
        tree.minThreshold = 0f;
        tree.maxThreshold = 5.5f;

        sm.defaultState = state;

        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();
        return controller;
    }

    /// <summary>
    /// Ensures a named looping clip exists in the FBX and reimports if needed.
    /// </summary>
    private static void FixClip(string fbxPath, string clipName, int first, int last)
    {
        var importer = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
        if (importer == null) { Debug.LogError($"ModelImporter missing: {fbxPath}"); return; }

        ModelImporterClipAnimation[] clips = importer.clipAnimations;
        if (clips == null || clips.Length == 0)
            clips = importer.defaultClipAnimations;

        for (int i = 0; i < clips.Length; i++)
        {
            clips[i].name                   = clipName;
            clips[i].firstFrame             = first;
            clips[i].lastFrame              = last;
            clips[i].loopTime               = true;
            clips[i].loopPose               = true;
            clips[i].lockRootRotation       = true;
            clips[i].lockRootHeightY        = true;
            clips[i].lockRootPositionXZ     = true;
            clips[i].keepOriginalOrientation = true;
            clips[i].keepOriginalPositionY   = true;
            clips[i].keepOriginalPositionXZ  = true;
        }

        importer.clipAnimations = clips;
        importer.SaveAndReimport();
    }

    /// <summary>Loads the first non-preview AnimationClip from an FBX by name.</summary>
    private static AnimationClip LoadClip(string fbxPath, string clipName)
    {
        AnimationClip fallback = null;
        foreach (Object obj in AssetDatabase.LoadAllAssetsAtPath(fbxPath))
        {
            if (obj is not AnimationClip c || c.name.Contains("__preview__")) continue;
            if (c.name == clipName) return c;
            fallback = c;
        }
        return fallback;
    }
}
#endif
