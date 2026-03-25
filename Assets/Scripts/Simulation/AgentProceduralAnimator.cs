using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Procedural visual animator for evacuation agents.
///
/// Drives all visible motion entirely in code since agents use a capsule mesh
/// with no skeletal rig or imported animation clips:
///   - Walk/run body bob (vertical oscillation scaled by speed)
///   - Forward lean on a dedicated body child pivot (NavMeshAgent root rotation is left alone)
///   - Squash and stretch pulses on start/stop
///   - Smooth colour tinting per AgentState / AgentRole via MaterialPropertyBlock
///
/// IMPORTANT: NavMeshAgent.updateRotation must stay TRUE so the agent faces its
/// path correctly. This component applies lean via a dedicated child "BodyPivot"
/// transform so it never fights the NavMeshAgent root rotation.
/// </summary>
[RequireComponent(typeof(NavMeshAgent))]
public class AgentProceduralAnimator : MonoBehaviour
{
    // ─── Tuning ────────────────────────────────────────────────────────────────

    private const float BobFrequencyWalk  = 2.8f;
    private const float BobFrequencyRun   = 5.5f;
    private const float BobFrequencyPanic = 7.5f;
    private const float BobAmplitude      = 0.08f;   // world-space Y offset on body pivot
    private const float LeanMaxAngle      = 14f;      // applied to body pivot X rotation
    private const float LeanSmoothing     = 8f;
    private const float SquashDuration    = 0.20f;
    private const float ColorSmoothing    = 5f;

    // ─── State colours ────────────────────────────────────────────────────────

    private static readonly Color ColIdle      = new Color(0.70f, 0.78f, 0.88f);
    private static readonly Color ColWander    = new Color(0.55f, 0.80f, 0.55f);
    private static readonly Color ColPanic     = new Color(1.00f, 0.50f, 0.08f);
    private static readonly Color ColEvac      = new Color(0.25f, 0.55f, 1.00f);
    private static readonly Color ColMask      = new Color(0.10f, 0.90f, 0.95f);
    private static readonly Color ColLeader    = new Color(1.00f, 0.88f, 0.08f);
    private static readonly Color ColMedical   = new Color(1.00f, 0.25f, 0.85f);
    private static readonly Color ColCasualty  = new Color(0.80f, 0.06f, 0.06f);
    private static readonly Color ColEvacuated = new Color(0.30f, 0.30f, 0.30f);

    private static readonly int ShaderBaseColor = Shader.PropertyToID("_BaseColor");
    private static readonly int ShaderColor     = Shader.PropertyToID("_Color");

    // ─── Components ───────────────────────────────────────────────────────────

    private NavMeshAgent navAgent;
    private AgentBrain   brain;
    private Renderer[]   bodyRenderers;

    // The body pivot is a child transform that sits between the NavMeshAgent root
    // (which NavMeshAgent rotates freely) and the visible mesh.
    // We apply bob and lean to this pivot only — never to the root.
    private Transform bodyPivot;

    private MaterialPropertyBlock mpb;

    // ─── Runtime state ────────────────────────────────────────────────────────

    private float   bobPhase;
    private float   squashTimer;
    private float   squashDirection; // +1 stretch, -1 squash
    private bool    wasMoving;
    private Vector3 baseScale;

    private Color currentColor;
    private Color targetColor;

    private AgentBrain.AgentState lastState;
    private AgentRole             lastRole;

    // ─── Lifecycle ────────────────────────────────────────────────────────────

    // Set true when the agent has a SkinnedMeshRenderer rig.
    // Bob/lean/squash are skipped entirely for skeletal characters —
    // manipulating the root transform fights the NavMeshAgent and breaks FBX animations.
    private bool isSkeletal;

    private void Awake()
    {
        navAgent = GetComponent<NavMeshAgent>();
        brain    = GetComponent<AgentBrain>();
        mpb      = new MaterialPropertyBlock();

        // Detect whether this is an FBX-rigged (skeletal) character.
        // If it has a SkinnedMeshRenderer the skeleton must not be re-parented —
        // doing so severs root-bone references and breaks all FBX animations.
        isSkeletal = GetComponentInChildren<SkinnedMeshRenderer>(true) != null;

        if (isSkeletal)
        {
            // For rigged characters, drive bob/lean via the root transform directly
            // (or skip it entirely and let the Animator handle all motion).
            // We just use the root itself as the pivot reference so nothing breaks.
            Transform existing = transform.Find("BodyPivot");
            bodyPivot = existing != null ? existing : transform;
        }
        else
        {
            // Capsule/primitive agents — safe to create BodyPivot and re-parent.
            Transform existing = transform.Find("BodyPivot");
            if (existing != null)
            {
                bodyPivot = existing;
            }
            else
            {
                var pivotGO = new GameObject("BodyPivot");
                pivotGO.transform.SetParent(transform, false);
                pivotGO.transform.localPosition = Vector3.zero;
                pivotGO.transform.localRotation = Quaternion.identity;
                pivotGO.transform.localScale    = Vector3.one;
                bodyPivot = pivotGO.transform;

                var toReparent = new System.Collections.Generic.List<Transform>();
                for (int i = 0; i < transform.childCount; i++)
                {
                    Transform ch = transform.GetChild(i);
                    if (ch != bodyPivot && ch.name != "BodyPivot")
                        toReparent.Add(ch);
                }
                foreach (var ch in toReparent)
                    ch.SetParent(bodyPivot, true);
            }
        }

        baseScale = bodyPivot.localScale;

        // Gather renderers — for skeletal agents search the whole hierarchy.
        var all      = isSkeletal
            ? GetComponentsInChildren<Renderer>(true)
            : bodyPivot.GetComponentsInChildren<Renderer>(true);

        var filtered = new System.Collections.Generic.List<Renderer>();
        foreach (var r in all)
        {
            string n = r.gameObject.name;
            if (!n.Contains("StatusDisc") && !n.Contains("Status"))
                filtered.Add(r);
        }
        bodyRenderers = filtered.ToArray();

        currentColor = ColIdle;
        targetColor  = ColIdle;
    }

    private void Update()
    {
        if (navAgent == null || brain == null) return;

        float speed = navAgent.velocity.magnitude;

        // Only apply procedural bob/lean/squash to non-skeletal (capsule) agents.
        // For skeletal (FBX) agents the Animator drives all movement — touching the
        // root transform here would fight the NavMeshAgent and break bone animations.
        if (!isSkeletal)
        {
            UpdateBobAndLean(speed);
            UpdateSquash(speed);
        }

        UpdateColour();
    }

    // ─── Bob and Lean ─────────────────────────────────────────────────────────

    private void UpdateBobAndLean(float speed)
    {
        AgentBrain.AgentState state = brain.CurrentState;

        // Casualties stay collapsed.
        if (state == AgentBrain.AgentState.Casualty)
        {
            bodyPivot.localRotation = Quaternion.Slerp(bodyPivot.localRotation,
                Quaternion.Euler(85f, 0f, 0f), Time.deltaTime * 4f);
            bodyPivot.localScale = Vector3.Lerp(bodyPivot.localScale, baseScale, Time.deltaTime * 4f);
            return;
        }

        if (speed < 0.08f)
        {
            // Idle — gently return to neutral, keep a slight idle sway.
            bobPhase += BobFrequencyWalk * 0.3f * Time.deltaTime;
            float idleSway = Mathf.Sin(bobPhase) * 0.015f;
            Quaternion idleRot = Quaternion.Euler(idleSway * 10f, 0f, idleSway * 5f);
            bodyPivot.localRotation = Quaternion.Slerp(bodyPivot.localRotation, idleRot, Time.deltaTime * 4f);
            bodyPivot.localScale    = Vector3.Lerp(bodyPivot.localScale, baseScale, Time.deltaTime * 6f);
            return;
        }

        float freq = state switch
        {
            AgentBrain.AgentState.Panicking  => BobFrequencyPanic,
            AgentBrain.AgentState.Evacuating => BobFrequencyRun,
            _                                 => BobFrequencyWalk
        };

        bobPhase += freq * Time.deltaTime;

        // Vertical bob → applied as localPosition Y offset on pivot.
        float bobY = Mathf.Abs(Mathf.Sin(bobPhase)) * BobAmplitude * Mathf.Clamp01(speed / 2f);
        bodyPivot.localPosition = new Vector3(0f, bobY, 0f);

        // Forward lean → applied as X rotation on pivot (root handles facing direction).
        float speedNorm  = Mathf.Clamp01(speed / 6f);
        float leanTarget = -speedNorm * LeanMaxAngle;

        // Side sway synced with bob phase for a natural gait.
        float sway = Mathf.Sin(bobPhase * 0.5f) * speedNorm * 4f;

        Quaternion targetRot = Quaternion.Euler(leanTarget, 0f, sway);
        bodyPivot.localRotation = Quaternion.Slerp(bodyPivot.localRotation, targetRot,
            Time.deltaTime * LeanSmoothing);
    }

    // ─── Squash and Stretch ───────────────────────────────────────────────────

    private void UpdateSquash(float speed)
    {
        bool moving = speed > 0.25f;

        if (moving && !wasMoving)
        {
            squashTimer     = SquashDuration;
            squashDirection = 1f; // stretch up on launch
        }
        else if (!moving && wasMoving)
        {
            squashTimer     = SquashDuration;
            squashDirection = -1f; // squash on landing
        }
        wasMoving = moving;

        if (squashTimer > 0f)
        {
            squashTimer -= Time.deltaTime;
            float t = 1f - Mathf.Clamp01(squashTimer / SquashDuration);
            float curve = Mathf.Sin(t * Mathf.PI); // 0→1→0 bell

            Vector3 s = baseScale;
            if (squashDirection > 0f) // stretch
            {
                s.y *= 1f + curve * 0.14f;
                s.x *= 1f - curve * 0.08f;
                s.z *= 1f - curve * 0.08f;
            }
            else // squash
            {
                s.y *= 1f - curve * 0.12f;
                s.x *= 1f + curve * 0.10f;
                s.z *= 1f + curve * 0.10f;
            }
            bodyPivot.localScale = s;
        }
    }

    // ─── Colour ───────────────────────────────────────────────────────────────

    private void UpdateColour()
    {
        AgentBrain.AgentState state = brain.CurrentState;
        AgentRole             role  = brain.Role;

        if (state != lastState || role != lastRole)
        {
            lastState = state;
            lastRole  = role;

            targetColor = state switch
            {
                AgentBrain.AgentState.Casualty       => ColCasualty,
                AgentBrain.AgentState.Evacuated       => ColEvacuated,
                AgentBrain.AgentState.Panicking       => ColPanic,
                AgentBrain.AgentState.SeekingMask     => ColMask,
                AgentBrain.AgentState.MedicalResponse => ColMedical,
                AgentBrain.AgentState.Evacuating when role == AgentRole.EvacuationLeader => ColLeader,
                AgentBrain.AgentState.Evacuating when role == AgentRole.MedicalTeam      => ColMedical,
                AgentBrain.AgentState.Evacuating => ColEvac,
                AgentBrain.AgentState.Wandering  => ColWander,
                _                                 => ColIdle
            };
        }

        currentColor = Color.Lerp(currentColor, targetColor, Time.deltaTime * ColorSmoothing);
        ApplyColour(currentColor);
    }

    private void ApplyColour(Color col)
    {
        foreach (var r in bodyRenderers)
        {
            if (r == null) continue;
            r.GetPropertyBlock(mpb);
            mpb.SetColor(ShaderBaseColor, col);
            mpb.SetColor(ShaderColor,     col);
            r.SetPropertyBlock(mpb);
        }
    }
}
#if false // stale dead code — excluded from compilation


    private const float BobAmplitude_REMOVED = 0f;
    private const float LeanMaxAngle      = 12f;
    private const float LeanSmoothing     = 6f;
    private const float SquashDuration    = 0.18f;
    private const float SquashAmount      = 0.85f;
    private const float StretchAmount     = 1.12f;
    private const float ScaleSmoothing    = 10f;

    // ─── State colours ────────────────────────────────────────────────────────

    private static readonly Color ColIdle      = new Color(0.70f, 0.78f, 0.88f); // light blue-grey
    private static readonly Color ColWander    = new Color(0.55f, 0.80f, 0.55f); // soft green
    private static readonly Color ColPanic     = new Color(1.00f, 0.55f, 0.10f); // orange
    private static readonly Color ColEvac      = new Color(0.25f, 0.55f, 1.00f); // vivid blue
    private static readonly Color ColMask      = new Color(0.10f, 0.90f, 0.95f); // cyan
    private static readonly Color ColLeader    = new Color(1.00f, 0.90f, 0.10f); // yellow
    private static readonly Color ColMedical   = new Color(1.00f, 0.25f, 0.85f); // pink
    private static readonly Color ColCasualty  = new Color(0.75f, 0.08f, 0.08f); // deep red
    private static readonly Color ColEvacuated = new Color(0.35f, 0.35f, 0.35f); // grey

    private static readonly int ShaderColorID = Shader.PropertyToID("_Color");
    private static readonly int ShaderBaseColorID = Shader.PropertyToID("_BaseColor");

    // ─── Components ───────────────────────────────────────────────────────────

    private NavMeshAgent navAgent;
    private AgentBrain   brain;
    private Renderer[]   renderers;
    private Transform    bodyTransform;

    private MaterialPropertyBlock propBlock;

    // ─── Runtime state ────────────────────────────────────────────────────────

    private float   bobPhase;
    private float   squashTimer;
    private bool    wasMoving;
    private Vector3 baseScale;
    private Vector3 targetScale;
    private Vector3 prevVelocity;
    private Color   currentColor;
    private Color   targetColor;

    private AgentBrain.AgentState lastState;
    private AgentRole             lastRole;

    // ─── Lifecycle ────────────────────────────────────────────────────────────

    private void Awake()
    {
        navAgent      = GetComponent<NavMeshAgent>();
        brain         = GetComponent<AgentBrain>();
        bodyTransform = transform;
        baseScale     = transform.localScale;
        targetScale   = baseScale;
        propBlock     = new MaterialPropertyBlock();

        // Collect all child renderers, excluding any named "StatusDisc".
        var allRenderers = GetComponentsInChildren<Renderer>();
        var filtered     = new System.Collections.Generic.List<Renderer>();
        foreach (var r in allRenderers)
        {
            if (!r.gameObject.name.Contains("StatusDisc") && !r.gameObject.name.Contains("Status"))
                filtered.Add(r);
        }
        renderers = filtered.ToArray();

        currentColor = ColIdle;
        targetColor  = ColIdle;
    }

    private void Update()
    {
        if (navAgent == null || brain == null) return;

        float speed = navAgent.velocity.magnitude;
        UpdateBobAndLean(speed);
        UpdateSquash(speed);
        UpdateColour();
    }

#endif

