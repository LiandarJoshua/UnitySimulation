using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// One-shot editor utility that:
///   1. Sets loopTime = true on all three agent animation FBX clips.
///   2. Rebuilds the WorkerAnimatorController blend tree with correct thresholds
///      and wires up the animation clips from their respective FBX files.
///
/// Run via Tools > Fix Agent Animation Loops.
/// </summary>
public static class FixAnimationLoops
{
    private const string IdleFbx        = "Assets/OilRefinery/newcharidle.fbx";
    private const string WalkFbx        = "Assets/OilRefinery/newcharwalk.fbx";
    private const string RunFbx         = "Assets/OilRefinery/newcharrun.fbx";
    private const string ControllerPath = "Assets/OilRefinery/WorkerAnimatorController.controller";

    // Speed thresholds that match NavMeshAgent speeds in AgentBrain/AgentController.
    private const float IdleThreshold = 0f;
    private const float WalkThreshold = 2f;
    private const float RunThreshold  = 5.5f;

    [MenuItem("Tools/Fix Agent Animation Loops")]
    public static void Fix()
    {
        FixClip(IdleFbx, "Idle", 0, 250, true);
        FixClip(WalkFbx, "Walk", 0, 50,  true);
        FixClip(RunFbx,  "Run",  0, 21,  true);

        RebuildController();

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[FixAnimationLoops] Done — animation clips loop and controller is rebuilt.");
    }

    /// <summary>
    /// Rebuilds the WorkerAnimatorController with a correct 1D Speed blend tree.
    /// Thresholds: 0 = Idle, 2 = Walk, 5.5 = Run (matching NavMeshAgent speed range).
    /// </summary>
    private static void RebuildController()
    {
        AnimationClip idleClip = LoadClip(IdleFbx, "Idle");
        AnimationClip walkClip = LoadClip(WalkFbx, "Walk");
        AnimationClip runClip  = LoadClip(RunFbx,  "Run");

        if (idleClip == null || walkClip == null || runClip == null)
        {
            Debug.LogError("[FixAnimationLoops] Cannot rebuild controller — one or more clips missing. " +
                           "Re-run after FBX reimport.");
            return;
        }

        AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);

        // Add Speed float parameter.
        controller.AddParameter("Speed", AnimatorControllerParameterType.Float);

        AnimatorStateMachine sm = controller.layers[0].stateMachine;

        // Remove the default empty state Unity inserts.
        foreach (var s in sm.states)
            sm.RemoveState(s.state);

        // Create Locomotion state with a 1D blend tree.
        BlendTree blendTree;
        AnimatorState locomotionState = controller.CreateBlendTreeInController("Locomotion", out blendTree);

        blendTree.blendType              = BlendTreeType.Simple1D;
        blendTree.blendParameter         = "Speed";
        blendTree.useAutomaticThresholds = false;

        blendTree.AddChild(idleClip, IdleThreshold);
        blendTree.AddChild(walkClip, WalkThreshold);
        blendTree.AddChild(runClip,  RunThreshold);

        sm.defaultState = locomotionState;

        EditorUtility.SetDirty(controller);
        Debug.Log($"[FixAnimationLoops] Rebuilt {ControllerPath} — Idle@{IdleThreshold} / Walk@{WalkThreshold} / Run@{RunThreshold}");
    }

    /// <summary>
    /// Configures a named, looping clip in an FBX importer and triggers reimport.
    /// Uses the full frame range of the default take when clipAnimations is empty.
    /// </summary>
    private static void FixClip(string assetPath, string clipName, int firstFrame, int lastFrame, bool loop)
    {
        var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
        if (importer == null)
        {
            Debug.LogError($"[FixAnimationLoops] ModelImporter not found at: {assetPath}");
            return;
        }

        ModelImporterClipAnimation[] clips = importer.clipAnimations;
        if (clips == null || clips.Length == 0)
            clips = importer.defaultClipAnimations;

        if (clips == null || clips.Length == 0)
        {
            Debug.LogError($"[FixAnimationLoops] No clips found in {assetPath}");
            return;
        }

        for (int i = 0; i < clips.Length; i++)
        {
            clips[i].name                    = clipName;
            clips[i].firstFrame              = firstFrame;
            clips[i].lastFrame               = lastFrame;
            clips[i].loopTime                = loop;
            clips[i].loopPose                = loop;
            clips[i].lockRootRotation        = true;
            clips[i].lockRootHeightY         = true;
            clips[i].lockRootPositionXZ      = true;
            clips[i].keepOriginalOrientation = true;
            clips[i].keepOriginalPositionY   = true;
            clips[i].keepOriginalPositionXZ  = true;
        }

        importer.clipAnimations = clips;
        importer.SaveAndReimport();
        Debug.Log($"[FixAnimationLoops] Fixed: {assetPath} → '{clipName}' frames [{firstFrame}–{lastFrame}] loop={loop}");
    }

    /// <summary>
    /// Loads a named AnimationClip from an FBX, skipping preview clips.
    /// Falls back to any non-preview clip if the exact name isn't found after reimport.
    /// </summary>
    private static AnimationClip LoadClip(string assetPath, string clipName)
    {
        Object[] assets = AssetDatabase.LoadAllAssetsAtPath(assetPath);
        AnimationClip fallback = null;

        foreach (Object obj in assets)
        {
            if (obj is not AnimationClip clip) continue;
            if (clip.name.Contains("__preview__")) continue;

            if (clip.name == clipName) return clip;
            fallback = clip;
        }

        return fallback;
    }
}
