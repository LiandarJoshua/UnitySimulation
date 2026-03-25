using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Core agent brain — drives NavMesh navigation, animation, role-based behaviour,
/// mask pickup, exit queuing and all phase transitions.
///
/// Animator parameters (must exist in the AnimatorController):
///   float  Speed       — normalised movement speed
///   bool   isRunning   — true when evacuating at full speed
///   bool   isPanicking — true while in panic phase
///   bool   isPointing  — true for EvacuationLeader in Phase 2
///   bool   isDead      — true when casualty
///   bool   hasMask     — true after collecting a mask
/// </summary>
[RequireComponent(typeof(NavMeshAgent))]
public class AgentBrain : MonoBehaviour
{
    // ─── Agent States ──────────────────────────────────────────────────────────

    public enum AgentState
    {
        Idle,
        Wandering,
        Panicking,
        SeekingMask,
        Evacuating,
        Evacuated,
        Casualty,
        MedicalResponse
    }

    // ─── Tuning ────────────────────────────────────────────────────────────────

    private const float WanderRadius          = 18f;
    private const float WanderIntervalMin     = 2f;
    private const float WanderIntervalMax     = 5f;
    private const float PanicWanderRadius     = 8f;
    private const float WanderSpeed           = 3f;
    private const float EvacuationSpeed       = 5.5f;
    private const float PanicSpeedMultiplier  = 1.3f;
    private const float CohortFollowRadius    = 12f;
    private const float ArrivalThreshold      = 1.8f;
    private const float PathRefreshInterval   = 0.6f;
    private const float SteerCheckInterval    = 0.15f;
    private const float CrowdCheckInterval    = 1.2f;  // raised for perf at 500 agents
    private const float CrowdSlowdownRadius   = 4f;
    private const int   CrowdSlowdownThresh   = 6;
    private const float CrowdSpeedMultiplier  = 0.55f;
    private const float CasualtyAvoidRadius   = 2.5f;
    private const float DetourResumeDelay     = 1.4f;
    private const float MaskPickupRange       = 2.5f;

    // Stagger cohort checks across agents using a per-agent random offset so
    // not all 500 agents scan the list on the same frame.
    private const float CohortCheckInterval   = 1.8f;

    // ─── Animator Parameters ──────────────────────────────────────────────────

    private static readonly int ParamSpeed       = Animator.StringToHash("Speed");
    private static readonly int ParamIsRunning   = Animator.StringToHash("isRunning");
    private static readonly int ParamIsPanicking = Animator.StringToHash("isPanicking");
    private static readonly int ParamIsPointing  = Animator.StringToHash("isPointing");
    private static readonly int ParamIsDead      = Animator.StringToHash("isDead");
    private static readonly int ParamHasMask     = Animator.StringToHash("hasMask");

    // ─── Inspector ────────────────────────────────────────────────────────────

    [Header("Role")]
    [SerializeField] private AgentRole role = AgentRole.Civilian;

    [Header("Mask")]
    [SerializeField] private GameObject maskMeshObject;

    [Header("Indicator")]
    [SerializeField] private EvacAgentIndicator statusIndicator;

    // ─── Static Shared State ──────────────────────────────────────────────────

    private static readonly List<Transform>  FireSources  = new List<Transform>();
    private static readonly List<AgentBrain> AllAgents    = new List<AgentBrain>();

    // ─── Components ───────────────────────────────────────────────────────────

    private NavMeshAgent navAgent;
    private Animator     animator;

    // ─── Runtime State ────────────────────────────────────────────────────────

    private AgentState currentState = AgentState.Idle;
    private ExitPoint  assignedExit;
    private MaskStation targetMaskStation;

    private bool hasMask;
    private bool isCrowded;
    private bool onDetour;
    private bool isFollowingLeader;

    private float wanderTimer;
    private float pathRefreshTimer;
    private float steerCheckTimer;
    private float crowdCheckTimer;
    private float detourClearTimer;
    private float cohortCheckTimer;

    private Vector3 cachedExitPos;
    private bool    exitPosCached;

    // ─── Public Accessors ─────────────────────────────────────────────────────

    public AgentState CurrentState     => currentState;
    public AgentRole  Role             => role;
    public bool       HasMask          => hasMask;
    public bool       IsCasualty       => currentState == AgentState.Casualty;
    public bool       IsEvacuated      => currentState == AgentState.Evacuated;
    public bool       IsPanicking      => currentState == AgentState.Panicking;

    // ─── Static API ───────────────────────────────────────────────────────────

    /// <summary>Registers a fire/gas source transform for all-agent avoidance.</summary>
    public static void RegisterFireSource(Transform t)
    {
        if (t != null && !FireSources.Contains(t)) FireSources.Add(t);
    }

    /// <summary>Clears all fire sources and the agent registry. Call on reset.</summary>
    public static void ClearAll()
    {
        FireSources.Clear();
        AllAgents.Clear();
    }

    /// <summary>Returns counts of all agent states for the dashboard.</summary>
    public static EvacSimStateCounts GetStateCounts()
    {
        var c = new EvacSimStateCounts();
        foreach (var a in AllAgents)
        {
            if (a == null) continue;
            switch (a.currentState)
            {
                case AgentState.Wandering:       c.Wandering++;       break;
                case AgentState.Panicking:       c.Panicking++;       break;
                case AgentState.SeekingMask:     c.SeekingMask++;     break;
                case AgentState.Evacuating:      c.Evacuating++;      break;
                case AgentState.Evacuated:       c.Evacuated++;       break;
                case AgentState.Casualty:        c.Casualty++;        break;
                case AgentState.MedicalResponse: c.MedicalResponse++; break;
            }
        }
        return c;
    }

    public static IReadOnlyList<AgentBrain> GetAllAgents() => AllAgents;

    public NavMeshAgent GetNavAgent() => navAgent;

    // ─── Initialization ────────────────────────────────────────────────────────

    /// <summary>Called by EvacSimManager after spawn.</summary>
    public void Initialize(AgentRole assignedRole, ExitPoint exit)
    {
        role         = assignedRole;
        assignedExit = exit;
        exitPosCached = false;
        AllAgents.Add(this);

        // Stagger per-agent expensive checks so all agents don't scan on the same frame.
        cohortCheckTimer = UnityEngine.Random.Range(0f, CohortCheckInterval);
        crowdCheckTimer  = UnityEngine.Random.Range(0f, CrowdCheckInterval);

        if (maskMeshObject != null)
            maskMeshObject.SetActive(false);
    }

    /// <summary>Begins pre-phase wandering.</summary>
    public void StartWandering()
    {
        if (navAgent == null || !navAgent.isOnNavMesh) return;
        SetState(AgentState.Wandering);
        navAgent.speed     = WanderSpeed;
        navAgent.isStopped = false;
        wanderTimer        = UnityEngine.Random.Range(0f, WanderIntervalMax);
    }

    /// <summary>Triggers Phase 1 panic for this agent.</summary>
    public void TriggerPanic()
    {
        if (currentState == AgentState.Casualty || currentState == AgentState.Evacuated) return;
        SetState(AgentState.Panicking);
        navAgent.speed     = EvacuationSpeed;   // panic = full run speed
        navAgent.isStopped = false;
        wanderTimer        = 0f;
    }

    /// <summary>Transitions this agent into Phase 2 evacuation.</summary>
    public void StartEvacuating()
    {
        if (currentState == AgentState.Casualty || currentState == AgentState.Evacuated) return;
        if (role == AgentRole.MedicalTeam)
        {
            SetState(AgentState.MedicalResponse);
            navAgent.speed     = EvacuationSpeed;
            navAgent.isStopped = false;
            return;
        }

        // Check if agent needs to collect a mask first.
        if (!hasMask)
        {
            MaskStation nearest = MaskStation.FindNearest(transform.position);
            if (nearest != null)
            {
                targetMaskStation = nearest;
                SetState(AgentState.SeekingMask);
                navAgent.speed     = EvacuationSpeed * 0.85f;
                navAgent.isStopped = false;
                navAgent.SetDestination(nearest.transform.position);
                return;
            }
        }

        BeginDirectEvacuation();
    }

    /// <summary>Makes this agent a casualty (remains on ground).</summary>
    public void BecomeCasualty()
    {
        if (currentState == AgentState.Casualty) return;
        SetState(AgentState.Casualty);
        navAgent.isStopped = true;

        // Do NOT call r.material.color here — that creates new material instances
        // and conflicts with AgentProceduralAnimator's MaterialPropertyBlock.
        // Colour is handled by AgentProceduralAnimator.UpdateColour() via ColCasualty.

        EvacSimManager.Instance?.OnAgentBecameCasualty();
    }

    // ─── Unity Lifecycle ──────────────────────────────────────────────────────

    private void Awake()
    {
        navAgent  = GetComponent<NavMeshAgent>();
        animator  = GetComponentInChildren<Animator>();

        if (animator != null)
            EnsureAnimatorSetup(animator);

        if (statusIndicator == null)
            statusIndicator = GetComponentInChildren<EvacAgentIndicator>();
    }

    /// <summary>
    /// Ensures the Animator is ready to play looping humanoid clips.
    /// The controller and avatar are baked into the prefab — this only
    /// guarantees layer weights are non-zero (controllers can be saved with
    /// Base Layer weight 0, which silences all output).
    /// </summary>
    private static void EnsureAnimatorSetup(Animator anim)
    {
        anim.cullingMode     = AnimatorCullingMode.AlwaysAnimate;
        anim.applyRootMotion = false;

        for (int i = 0; i < anim.layerCount; i++)
            anim.SetLayerWeight(i, 1f);
    }

    // Set to true while the player is possessing this agent so AgentBrain
    // does not overwrite the animator Speed that AgentPlayerController drives.
    private bool isPlayerControlled;

    /// <summary>
    /// Suppresses AI animator driving while the player is possessing this agent.
    /// Call with true on possess and false on release.
    /// </summary>
    public void SetPlayerControlled(bool controlled) => isPlayerControlled = controlled;

    private void Update()
    {
        if (navAgent == null || !navAgent.isOnNavMesh) return;

        if (!isPlayerControlled)
        {
            switch (currentState)
            {
                case AgentState.Wandering:       UpdateWander(false);     break;
                case AgentState.Panicking:       UpdatePanic();           break;
                case AgentState.SeekingMask:     UpdateSeekMask();        break;
                case AgentState.Evacuating:      UpdateEvacuating();      break;
                case AgentState.MedicalResponse: UpdateMedical();         break;
            }

            DriveAnimator();
        }
    }

    // ─── State Updates ────────────────────────────────────────────────────────

    private void UpdateWander(bool isPanic)
    {
        wanderTimer -= Time.deltaTime;
        bool reached = !navAgent.pathPending && navAgent.remainingDistance < 0.6f;

        if (wanderTimer <= 0f || reached)
        {
            wanderTimer = UnityEngine.Random.Range(WanderIntervalMin, WanderIntervalMax);
            float radius = isPanic ? PanicWanderRadius : WanderRadius;
            SetSafeWanderDestination(radius);
        }
    }

    private void UpdatePanic()
    {
        // Phase 1: erratic movement + cohort following.
        wanderTimer -= Time.deltaTime;
        bool reached = !navAgent.pathPending && navAgent.remainingDistance < 0.6f;

        if (wanderTimer <= 0f || reached)
        {
            wanderTimer = UnityEngine.Random.Range(0.8f, 2.5f);

            // Cohort follow: drift toward the average position of nearby moving agents.
            // Only recalculate on a throttled timer to avoid O(N²) every frame at 500 agents.
            cohortCheckTimer -= Time.deltaTime;
            if (cohortCheckTimer <= 0f)
            {
                cohortCheckTimer = CohortCheckInterval + UnityEngine.Random.Range(-0.2f, 0.2f);
                cachedCohortTarget = ComputeCohortTarget();
            }

            if (cachedCohortTarget != Vector3.zero)
            {
                Vector3 mid = Vector3.Lerp(transform.position + UnityEngine.Random.insideUnitSphere * PanicWanderRadius,
                                           cachedCohortTarget, 0.5f);
                mid.y = transform.position.y;
                if (TrySampleNavMesh(mid, out Vector3 sampled))
                {
                    navAgent.SetDestination(sampled);
                    return;
                }
            }

            SetSafeWanderDestination(PanicWanderRadius);
        }
    }

    // Cached result of the last cohort computation — avoids scanning AllAgents every tick.
    private Vector3 cachedCohortTarget;

    private Vector3 ComputeCohortTarget()
    {
        Vector3 sum   = Vector3.zero;
        int     count = 0;
        Vector3 pos   = transform.position;

        foreach (var other in AllAgents)
        {
            if (other == null || other == this) continue;
            if (other.currentState != AgentState.Panicking && other.currentState != AgentState.Wandering) continue;
            float dist = Vector3.Distance(pos, other.transform.position);
            if (dist < CohortFollowRadius && other.navAgent != null && other.navAgent.velocity.magnitude > 0.5f)
            {
                sum += other.transform.position;
                count++;
            }
        }

        return count > 0 ? sum / count : Vector3.zero;
    }

    private void UpdateSeekMask()
    {
        if (targetMaskStation == null)
        {
            BeginDirectEvacuation();
            return;
        }

        if (Vector3.Distance(transform.position, targetMaskStation.transform.position) <= MaskPickupRange)
        {
            CollectMask();
            BeginDirectEvacuation();
        }
    }

    private void UpdateEvacuating()
    {
        if (!exitPosCached)
        {
            CacheExitPosition();
            if (!exitPosCached) return;
            navAgent.SetDestination(cachedExitPos);
        }

        // Arrival check.
        if (Vector3.Distance(transform.position, cachedExitPos) <= ArrivalThreshold)
        {
            MarkEvacuated();
            return;
        }

        // Detour resume.
        if (onDetour)
        {
            detourClearTimer -= Time.deltaTime;
            if (detourClearTimer <= 0f)
            {
                onDetour = false;
                navAgent.SetDestination(cachedExitPos);
            }
            return;
        }

        // Crowd slowdown.
        crowdCheckTimer -= Time.deltaTime;
        if (crowdCheckTimer <= 0f)
        {
            crowdCheckTimer = CrowdCheckInterval;
            CheckCrowdSlowdown();
        }

        // Periodic destination refresh.
        pathRefreshTimer -= Time.deltaTime;
        if (pathRefreshTimer <= 0f)
        {
            pathRefreshTimer = PathRefreshInterval;
            navAgent.SetDestination(cachedExitPos);
        }

        // Fire and casualty avoidance steering.
        steerCheckTimer -= Time.deltaTime;
        if (steerCheckTimer <= 0f)
        {
            steerCheckTimer = SteerCheckInterval;
            ApplyObstacleAvoidance();
        }
    }

    private void UpdateMedical()
    {
        // Navigate toward the nearest casualty agent.
        AgentBrain nearestCasualty = FindNearestCasualty();
        if (nearestCasualty == null)
        {
            // No more casualties — move toward exit.
            if (!exitPosCached) CacheExitPosition();
            if (exitPosCached) navAgent.SetDestination(cachedExitPos);
            return;
        }

        pathRefreshTimer -= Time.deltaTime;
        if (pathRefreshTimer <= 0f)
        {
            pathRefreshTimer = PathRefreshInterval;
            navAgent.SetDestination(nearestCasualty.transform.position);
        }
    }

    private AgentBrain FindNearestCasualty()
    {
        AgentBrain nearest = null;
        float      best    = float.MaxValue;

        foreach (var a in AllAgents)
        {
            if (a == null || !a.IsCasualty) continue;
            float d = Vector3.Distance(transform.position, a.transform.position);
            if (d < best) { best = d; nearest = a; }
        }

        return nearest;
    }

    // ─── Mask ─────────────────────────────────────────────────────────────────

    private void CollectMask()
    {
        hasMask = true;
        if (maskMeshObject != null)
            maskMeshObject.SetActive(true);
        targetMaskStation?.NotifyCollected();
    }

    // ─── Evacuation Helpers ───────────────────────────────────────────────────

    private void BeginDirectEvacuation()
    {
        SetState(AgentState.Evacuating);
        navAgent.speed     = EvacuationSpeed;
        navAgent.isStopped = false;
        pathRefreshTimer   = 0f;
        steerCheckTimer    = 0f;
        crowdCheckTimer    = 0f;
        onDetour           = false;

        if (!exitPosCached) CacheExitPosition();
        if (exitPosCached) navAgent.SetDestination(cachedExitPos);
    }

    private void MarkEvacuated()
    {
        SetState(AgentState.Evacuated);
        navAgent.isStopped = true;
        navAgent.velocity  = Vector3.zero;

        // Register with the exit queue and position the agent in line.
        if (assignedExit != null)
        {
            int queueIndex = assignedExit.NotifyArrival(this);
            PositionInQueue(queueIndex);
        }

        EvacSimManager.Instance?.OnAgentEvacuated();
        // Agent stays active and visible — queued at the exit.
    }

    private void PositionInQueue(int queueIndex)
    {
        if (assignedExit == null) return;

        // Space agents 1.2 m apart, fanning away from the exit in a line.
        const float Spacing = 1.2f;
        Vector3 exitPos  = assignedExit.transform.position;
        Vector3 toExit   = (exitPos - transform.position);
        Vector3 dir      = toExit.magnitude > 0.1f ? toExit.normalized : -transform.forward;

        // Offset each agent back along the approach direction.
        Vector3 queuePos = exitPos - dir * (Spacing * (queueIndex + 1));
        queuePos.y = transform.position.y;

        if (NavMesh.SamplePosition(queuePos, out NavMeshHit hit, 3f, NavMesh.AllAreas))
            navAgent.Warp(hit.position);

        // Face the exit.
        Vector3 faceDir = (exitPos - transform.position);
        faceDir.y = 0f;
        if (faceDir.sqrMagnitude > 0.01f)
            transform.rotation = Quaternion.LookRotation(faceDir);
    }

    // ─── Exit Caching ─────────────────────────────────────────────────────────

    private void CacheExitPosition()
    {
        if (assignedExit == null) return;

        Vector3 marker  = assignedExit.transform.position;
        Vector3 pathRef = transform.position;
        Vector3 best    = Vector3.zero;
        float   bestD   = float.MaxValue;

        float[] radii = { 0f, 0.5f, 1f, 1.5f, 2f, 3f, 5f };
        int     dirs  = 12;
        float   step  = 360f / dirs;

        foreach (float r in radii)
        {
            int count = r < 0.1f ? 1 : dirs;
            for (int i = 0; i < count; i++)
            {
                float   ang       = i * step * Mathf.Deg2Rad;
                Vector3 candidate = marker + new Vector3(Mathf.Sin(ang) * r, 0f, Mathf.Cos(ang) * r);

                if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit, 2f, NavMesh.AllAreas)) continue;

                NavMeshPath path = new NavMeshPath();
                bool ok = NavMesh.CalculatePath(pathRef, hit.position, NavMesh.AllAreas, path)
                          && path.status == NavMeshPathStatus.PathComplete;
                if (!ok) continue;

                float d = Vector3.Distance(hit.position, marker);
                if (d < bestD) { bestD = d; best = hit.position; }
            }
        }

        if (best != Vector3.zero)
        {
            cachedExitPos = best;
            exitPosCached = true;
            return;
        }

        if (NavMesh.SamplePosition(marker, out NavMeshHit fb, 20f, NavMesh.AllAreas))
            cachedExitPos = fb.position;
        else
            cachedExitPos = marker;

        exitPosCached = true;
    }

    // ─── Crowd Slowdown ───────────────────────────────────────────────────────

    private void CheckCrowdSlowdown()
    {
        if (!exitPosCached) return;

        float distToExit = Vector3.Distance(transform.position, cachedExitPos);
        if (distToExit > CrowdSlowdownRadius * 3f)
        {
            if (isCrowded) { isCrowded = false; navAgent.speed = EvacuationSpeed; }
            return;
        }

        int nearby = 0;
        foreach (var other in AllAgents)
        {
            if (other == null || other == this) continue;
            if (other.currentState != AgentState.Evacuating) continue;
            if (Vector3.Distance(transform.position, other.transform.position) < CrowdSlowdownRadius)
                nearby++;
        }

        bool shouldSlow = nearby >= CrowdSlowdownThresh;
        if (shouldSlow == isCrowded) return;

        isCrowded          = shouldSlow;
        navAgent.speed     = isCrowded ? EvacuationSpeed * CrowdSpeedMultiplier : EvacuationSpeed;
    }

    // ─── Obstacle Avoidance (fire + casualties) ───────────────────────────────

    private void ApplyObstacleAvoidance()
    {
        Vector3 pos      = transform.position;
        float   safeDist = EvacSimManager.Instance != null ? EvacSimManager.Instance.FireSafeDistance : 5f;

        // Avoid fire/gas sources.
        foreach (Transform fire in FireSources)
        {
            if (fire == null) continue;
            float dist = Vector3.Distance(pos, fire.position);
            if (dist >= safeDist) continue;

            TryDetourAround(fire.position, safeDist);
            return;
        }

        // Avoid casualty agents.
        foreach (var other in AllAgents)
        {
            if (other == null || !other.IsCasualty) continue;
            float dist = Vector3.Distance(pos, other.transform.position);
            if (dist < CasualtyAvoidRadius)
            {
                TryDetourAround(other.transform.position, CasualtyAvoidRadius);
                return;
            }
        }
    }

    private void TryDetourAround(Vector3 obstaclePos, float avoidRadius)
    {
        Vector3 awayDir = (transform.position - obstaclePos).normalized;
        Vector3 perp    = Vector3.Cross(awayDir, Vector3.up).normalized;

        float   offset  = avoidRadius + 3f;
        Vector3 opt1    = obstaclePos + awayDir * offset + perp * offset;
        Vector3 opt2    = obstaclePos + awayDir * offset - perp * offset;

        bool   opt1Closer = exitPosCached && (Vector3.Distance(opt1, cachedExitPos) < Vector3.Distance(opt2, cachedExitPos));
        Vector3 preferred = opt1Closer ? opt1 : opt2;
        Vector3 fallback  = opt1Closer ? opt2 : opt1;

        if (TrySampleNavMesh(preferred, out Vector3 dp) || TrySampleNavMesh(fallback, out dp))
        {
            navAgent.SetDestination(dp);
            onDetour         = true;
            detourClearTimer = DetourResumeDelay;
        }
    }

    // ─── Wander Helpers ───────────────────────────────────────────────────────

    private void SetSafeWanderDestination(float radius)
    {
        for (int i = 0; i < 20; i++)
        {
            Vector2 r2d   = UnityEngine.Random.insideUnitCircle * radius;
            Vector3 cand  = transform.position + new Vector3(r2d.x, 0f, r2d.y);

            if (!TrySampleNavMesh(cand, out Vector3 sampled)) continue;
            if (IsTooCloseToFire(sampled, 3f)) continue;

            navAgent.SetDestination(sampled);
            return;
        }
    }

    // ─── NavMesh Helpers ──────────────────────────────────────────────────────

    private bool TrySampleNavMesh(Vector3 candidate, out Vector3 result)
    {
        if (NavMesh.SamplePosition(candidate, out NavMeshHit hit, 2.5f, navAgent.areaMask))
        {
            result = hit.position;
            return true;
        }
        result = Vector3.zero;
        return false;
    }

    private static bool IsTooCloseToFire(Vector3 pos, float safeRadius)
    {
        foreach (Transform f in FireSources)
        {
            if (f != null && Vector3.Distance(pos, f.position) < safeRadius)
                return true;
        }
        return false;
    }

    // ─── State Machine ────────────────────────────────────────────────────────

    private void SetState(AgentState next)
    {
        currentState = next;
        UpdateIndicator();
    }

    private void UpdateIndicator()
    {
        statusIndicator?.SetState(currentState, role);
    }

    // ─── Animator ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Drives the Animator using only the Speed float — matching AgentController.cs.
    /// WorkerAnimatorController uses a single Speed blend-tree (Idle → Walk → Run).
    /// Bool params (isRunning, isPanicking, etc.) are set only when the parameter
    /// actually exists in the controller, to avoid console spam on mismatched controllers.
    /// </summary>
    private void DriveAnimator()
    {
        if (animator == null) return;

        float speed = navAgent.velocity.magnitude;

        // Primary: Speed float — always present in WorkerAnimatorController.
        // Damp value matches AgentController.cs exactly (smoothTime 0.08f).
        animator.SetFloat(ParamSpeed, speed, 0.08f, Time.deltaTime);

        // Secondary bool params — only touch if the param exists in the controller.
        // This prevents "Parameter 'X' does not exist" warnings when using
        // WorkerAnimatorController which only has Speed.
        TrySetBool(ParamIsRunning,   currentState == AgentState.Evacuating && speed > 2.5f);
        TrySetBool(ParamIsPanicking, currentState == AgentState.Panicking);
        TrySetBool(ParamIsPointing,  role == AgentRole.EvacuationLeader && currentState == AgentState.Evacuating);
        TrySetBool(ParamIsDead,      currentState == AgentState.Casualty);
        TrySetBool(ParamHasMask,     hasMask);
    }

    // Cache which bool param hashes are valid for the current controller.
    private System.Collections.Generic.HashSet<int> validParams;

    private void TrySetBool(int hash, bool value)
    {
        if (validParams == null) BuildValidParamCache();
        if (validParams.Contains(hash))
            animator.SetBool(hash, value);
    }

    private void BuildValidParamCache()
    {
        validParams = new System.Collections.Generic.HashSet<int>();
        if (animator == null || animator.runtimeAnimatorController == null) return;
        foreach (var p in animator.parameters)
            validParams.Add(p.nameHash);
    }
}

/// <summary>Snapshot of all agent state counts for the dashboard.</summary>
public struct EvacSimStateCounts
{
    public int Wandering;
    public int Panicking;
    public int SeekingMask;
    public int Evacuating;
    public int Evacuated;
    public int Casualty;
    public int MedicalResponse;
}
