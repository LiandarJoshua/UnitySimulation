using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Controls individual agent behaviour during the evacuation simulation.
///
/// States: Idle → Wandering → Evacuating → Evacuated / Injured
///
/// Realism features:
///   - Panic response: agents near fire briefly freeze (shock) then sprint at increased speed.
///   - Injury: a configurable proportion of agents within fire radius become incapacitated.
///   - Crowd slowdown: agents decelerate when many neighbours are inside the exit funnel.
///   - Overhead status indicator: colour-coded disc reflects current state visually.
///
/// Fire avoidance:
///   - Steering-level only. NavMeshObstacle carving is NOT used because a large carve
///     radius severs NavMesh connectivity in narrow corridors.
/// </summary>
public class AgentController : MonoBehaviour
{
    // ─── Agent States ─────────────────────────────────────────────────────────

    public enum AgentState { Idle, Wandering, Evacuating, Evacuated, Injured }

    // ─── Tuning ───────────────────────────────────────────────────────────────

    private const float WanderRadius         = 10f;
    private const float WanderIntervalMin    = 2f;
    private const float WanderIntervalMax    = 5f;

    /// <summary>Agents steer away from fire when inside this radius.</summary>
    private const float FireSafetyRadius     = 3f;
    private const float FireDetourOffset     = 3f;

    private const float ArrivalThreshold    = 1.5f;
    private const float WanderSpeed         = 2f;
    private const float EvacuationSpeed     = 5.5f;

    /// <summary>Panic speed multiplier applied after the shock freeze ends.</summary>
    private const float PanicSpeedMultiplier = 1.35f;
    private const float ShockDuration        = 1.5f;
    private const float PanicDuration        = 12f;

    /// <summary>Probability an agent is injured rather than evacuating when hit by fire.</summary>
    private const float InjuryProbability    = 0.08f;

    /// <summary>Radius around the exit in which crowd slowdown applies.</summary>
    private const float CrowdSlowdownRadius  = 4f;
    private const int   CrowdSlowdownThresh  = 5;
    private const float CrowdSpeedMultiplier = 0.55f;

    private const float PathRefreshInterval  = 0.5f;
    private const float SteerCheckInterval   = 0.15f;
    private const float DetourResumeDelay    = 1.2f;
    private const float CrowdCheckInterval   = 0.8f;

    private const string AnimParamSpeed      = "Speed";

    // ─── Components ───────────────────────────────────────────────────────────

    private NavMeshAgent         navMeshAgent;
    private Animator             animator;
    private Transform            exitPoint;
    private AgentStatusIndicator indicator;

    private Vector3 cachedExitPos;
    private bool    exitPosCached;

    private float wanderTimer;
    private float pathRefreshTimer;
    private float steerCheckTimer;
    private float detourClearTimer;
    private float crowdCheckTimer;
    private float panicTimer;
    private float shockTimer;

    private bool hasValidAgent;
    private bool onDetour;
    private bool isPanicked;
    private bool inShock;
    private bool isCrowded;

    private static readonly List<Transform>       FireSources = new List<Transform>();
    private static readonly List<AgentController> AllAgents   = new List<AgentController>();

    public AgentState CurrentState { get; private set; } = AgentState.Idle;
    public bool IsPanicked => isPanicked;
    public bool IsInjured  => CurrentState == AgentState.Injured;

    // ─── Static API ───────────────────────────────────────────────────────────

    /// <summary>Registers a fire transform so all agents steer away from it.</summary>
    public static void RegisterFireSource(Transform t)
    {
        if (t != null && !FireSources.Contains(t)) FireSources.Add(t);
    }

    /// <summary>Clears all registered fire sources. Call on simulation reset.</summary>
    public static void ClearFireSources()
    {
        FireSources.Clear();
        AllAgents.Clear();
    }

    /// <summary>Returns a snapshot of all agent state counts for the dashboard.</summary>
    public static AgentStateCounts GetStateCounts()
    {
        var counts = new AgentStateCounts();
        foreach (var agent in AllAgents)
        {
            if (agent == null) continue;
            switch (agent.CurrentState)
            {
                case AgentState.Idle:       counts.Idle++;       break;
                case AgentState.Wandering:  counts.Wandering++;  break;
                case AgentState.Evacuating: counts.Evacuating++; break;
                case AgentState.Evacuated:  counts.Evacuated++;  break;
                case AgentState.Injured:    counts.Injured++;    break;
            }
            if (agent.isPanicked) counts.Panicked++;
        }
        return counts;
    }

    /// <summary>Returns a read-only view of all registered agent controllers.</summary>
    public static IReadOnlyList<AgentController> GetAllAgents() => AllAgents;

    /// <summary>Exposes the underlying NavMeshAgent for analytics sampling.</summary>
    public NavMeshAgent GetNavAgent() => navMeshAgent;

    // ─── Public API ───────────────────────────────────────────────────────────

    /// <summary>Assigns the exit point. Called by SimulationManager after spawn.</summary>
    public void Initialize(Transform exit)
    {
        exitPoint     = exit;
        exitPosCached = false;
        AllAgents.Add(this);
    }

    /// <summary>Begins random wandering. Called immediately after spawn.</summary>
    public void StartWandering()
    {
        if (navMeshAgent == null) return;
        CurrentState           = AgentState.Wandering;
        wanderTimer            = UnityEngine.Random.Range(0f, WanderIntervalMax);
        navMeshAgent.speed     = WanderSpeed;
        navMeshAgent.isStopped = false;
        UpdateIndicator();
    }

    /// <summary>Switches the agent into evacuation mode toward the exit.</summary>
    public void StartEvacuating()
    {
        if (CurrentState == AgentState.Evacuated || CurrentState == AgentState.Injured) return;
        if (navMeshAgent == null) return;

        // Injury check: a small fraction of agents near fire become incapacitated.
        if (IsNearAnyFire() && UnityEngine.Random.value < InjuryProbability)
        {
            BecomeInjured();
            return;
        }

        CurrentState           = AgentState.Evacuating;
        pathRefreshTimer       = 0f;
        steerCheckTimer        = 0f;
        crowdCheckTimer        = 0f;
        onDetour               = false;
        navMeshAgent.speed     = EvacuationSpeed;
        navMeshAgent.isStopped = false;

        if (!exitPosCached) CacheExitPosition();
        navMeshAgent.SetDestination(cachedExitPos);
        UpdateIndicator();
    }

    // ─── Unity Lifecycle ─────────────────────────────────────────────────────

    private void Awake()
    {
        navMeshAgent = GetComponent<NavMeshAgent>();
        animator     = GetComponentInChildren<Animator>();
        indicator    = GetComponentInChildren<AgentStatusIndicator>();
    }

    private void Start()
    {
        hasValidAgent = navMeshAgent != null && navMeshAgent.isOnNavMesh;
        if (!hasValidAgent)
            Debug.LogWarning($"[AgentController] {name} is not on a NavMesh.", this);

        if (exitPoint != null && !exitPosCached)
            CacheExitPosition();
    }

    private void Update()
    {
        if (!hasValidAgent)
        {
            hasValidAgent = navMeshAgent != null && navMeshAgent.isOnNavMesh;
            return;
        }

        switch (CurrentState)
        {
            case AgentState.Wandering:  UpdateWander();   break;
            case AgentState.Evacuating: UpdateEvacuate(); break;
        }

        DriveAnimator();
    }

    // ─── State Updates ────────────────────────────────────────────────────────

    private void UpdateWander()
    {
        wanderTimer -= Time.deltaTime;
        bool reached = !navMeshAgent.pathPending && navMeshAgent.remainingDistance < 0.5f;

        if (IsTooCloseToFire(transform.position) || wanderTimer <= 0f || reached)
        {
            wanderTimer = UnityEngine.Random.Range(WanderIntervalMin, WanderIntervalMax);
            SetSafeWanderDestination();
        }
    }

    private void UpdateEvacuate()
    {
        // ── Panic: shock phase (briefly frozen on first fire detection) ────────
        if (inShock)
        {
            shockTimer -= Time.deltaTime;
            if (shockTimer <= 0f)
            {
                inShock                = false;
                isPanicked             = true;
                panicTimer             = PanicDuration;
                navMeshAgent.speed     = EvacuationSpeed * PanicSpeedMultiplier;
                navMeshAgent.isStopped = false;
                UpdateIndicator();
            }
            return;
        }

        // ── Panic timer countdown ─────────────────────────────────────────────
        if (isPanicked)
        {
            panicTimer -= Time.deltaTime;
            if (panicTimer <= 0f)
            {
                isPanicked         = false;
                navMeshAgent.speed = isCrowded ? EvacuationSpeed * CrowdSpeedMultiplier : EvacuationSpeed;
                UpdateIndicator();
            }
        }

        // ── Arrival ───────────────────────────────────────────────────────────
        if (Vector3.Distance(transform.position, cachedExitPos) <= ArrivalThreshold)
        {
            MarkEvacuated();
            return;
        }

        // ── Detour resume ─────────────────────────────────────────────────────
        if (onDetour)
        {
            detourClearTimer -= Time.deltaTime;
            if (detourClearTimer <= 0f)
            {
                onDetour = false;
                navMeshAgent.SetDestination(cachedExitPos);
            }
            return;
        }

        // ── Crowd slowdown check ──────────────────────────────────────────────
        crowdCheckTimer -= Time.deltaTime;
        if (crowdCheckTimer <= 0f)
        {
            crowdCheckTimer = CrowdCheckInterval;
            CheckCrowdSlowdown();
        }

        // ── Periodic path refresh ─────────────────────────────────────────────
        pathRefreshTimer -= Time.deltaTime;
        if (pathRefreshTimer <= 0f)
        {
            pathRefreshTimer = PathRefreshInterval;
            navMeshAgent.SetDestination(cachedExitPos);
        }

        // ── Fire steering ─────────────────────────────────────────────────────
        steerCheckTimer -= Time.deltaTime;
        if (steerCheckTimer <= 0f)
        {
            steerCheckTimer = SteerCheckInterval;
            ApplyFireSteering();
        }
    }

    // ─── Panic & Injury ──────────────────────────────────────────────────────

    private void TriggerPanic()
    {
        if (isPanicked || inShock) return;
        inShock                = true;
        shockTimer             = ShockDuration;
        navMeshAgent.isStopped = true;
        UpdateIndicator();
    }

    private void BecomeInjured()
    {
        CurrentState           = AgentState.Injured;
        navMeshAgent.isStopped = true;
        isPanicked             = false;
        UpdateIndicator();
        SimulationManager.Instance?.OnAgentInjured();
    }

    // ─── Crowd Slowdown ───────────────────────────────────────────────────────

    private void CheckCrowdSlowdown()
    {
        if (exitPoint == null) return;

        float distToExit = Vector3.Distance(transform.position, cachedExitPos);
        if (distToExit > CrowdSlowdownRadius * 2.5f)
        {
            if (isCrowded && !isPanicked) { isCrowded = false; navMeshAgent.speed = EvacuationSpeed; }
            return;
        }

        int nearby = 0;
        foreach (var other in AllAgents)
        {
            if (other == null || other == this) continue;
            if (other.CurrentState != AgentState.Evacuating) continue;
            if (Vector3.Distance(transform.position, other.transform.position) < CrowdSlowdownRadius)
                nearby++;
        }

        bool shouldSlow = nearby >= CrowdSlowdownThresh;
        if (shouldSlow == isCrowded) return;

        isCrowded = shouldSlow;
        if (!isPanicked)
            navMeshAgent.speed = isCrowded ? EvacuationSpeed * CrowdSpeedMultiplier : EvacuationSpeed;
    }

    // ─── Fire Steering ────────────────────────────────────────────────────────

    private void ApplyFireSteering()
    {
        Vector3 agentPos = transform.position;

        foreach (Transform fire in FireSources)
        {
            if (fire == null) continue;

            float fireDist = Vector3.Distance(agentPos, fire.position);
            if (fireDist >= FireSafetyRadius) continue;

            TriggerPanic();

            Vector3 awayDir = (agentPos - fire.position).normalized;
            Vector3 perp    = Vector3.Cross(awayDir, Vector3.up).normalized;

            Vector3 opt1 = fire.position + awayDir * (FireSafetyRadius + FireDetourOffset) + perp * FireDetourOffset;
            Vector3 opt2 = fire.position + awayDir * (FireSafetyRadius + FireDetourOffset) - perp * FireDetourOffset;

            bool opt1Closer   = Vector3.Distance(opt1, cachedExitPos) < Vector3.Distance(opt2, cachedExitPos);
            Vector3 preferred = opt1Closer ? opt1 : opt2;
            Vector3 fallback  = opt1Closer ? opt2 : opt1;

            if (TrySampleNavMesh(preferred, out Vector3 detourPos)
                || TrySampleNavMesh(fallback, out detourPos))
            {
                navMeshAgent.SetDestination(detourPos);
                onDetour         = true;
                detourClearTimer = DetourResumeDelay;
            }

            return;
        }
    }

    // ─── Exit Caching ─────────────────────────────────────────────────────────

    private void CacheExitPosition()
    {
        if (exitPoint == null) return;

        Vector3 exitMarker = exitPoint.position;
        Vector3 pathRef    = transform.position;

        Vector3 bestPoint = Vector3.zero;
        float   bestDist  = float.MaxValue;

        float[] radii = { 0f, 0.5f, 1f, 1.5f, 2f, 3f, 4f, 5f, 6f, 8f };
        int     dirs  = 16;
        float   step  = 360f / dirs;

        foreach (float r in radii)
        {
            int count = r < 0.1f ? 1 : dirs;
            for (int i = 0; i < count; i++)
            {
                float   angle     = i * step * Mathf.Deg2Rad;
                Vector3 candidate = exitMarker + new Vector3(Mathf.Sin(angle) * r, 0f, Mathf.Cos(angle) * r);

                if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit, 2f, NavMesh.AllAreas)) continue;

                NavMeshPath path = new NavMeshPath();
                bool complete = NavMesh.CalculatePath(pathRef, hit.position, NavMesh.AllAreas, path)
                                && path.status == NavMeshPathStatus.PathComplete;
                if (!complete) continue;

                float dist = Vector3.Distance(hit.position, exitMarker);
                if (dist < bestDist) { bestDist = dist; bestPoint = hit.position; }
            }
        }

        if (bestPoint != Vector3.zero) { cachedExitPos = bestPoint; exitPosCached = true; return; }

        if (NavMesh.SamplePosition(exitMarker, out NavMeshHit fb, 20f, NavMesh.AllAreas))
            cachedExitPos = fb.position;
        else
            cachedExitPos = exitMarker;

        exitPosCached = true;
        Debug.LogWarning($"[AgentController] {name}: No PathComplete exit found — using {cachedExitPos}.", this);
    }

    // ─── Wander ───────────────────────────────────────────────────────────────

    private void SetSafeWanderDestination()
    {
        const int MaxAttempts = 20;
        Vector3 bestFallback  = Vector3.zero;

        for (int i = 0; i < MaxAttempts; i++)
        {
            Vector2 rand2D    = UnityEngine.Random.insideUnitCircle * WanderRadius;
            Vector3 candidate = transform.position + new Vector3(rand2D.x, 0f, rand2D.y);

            if (!TrySampleNavMesh(candidate, out Vector3 sampled)) continue;

            if (!IsTooCloseToFire(sampled)) { navMeshAgent.SetDestination(sampled); return; }

            if (bestFallback == Vector3.zero) bestFallback = sampled;
        }

        if (bestFallback != Vector3.zero) navMeshAgent.SetDestination(bestFallback);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private bool TrySampleNavMesh(Vector3 candidate, out Vector3 result)
    {
        if (NavMesh.SamplePosition(candidate, out NavMeshHit hit, 2f, navMeshAgent.areaMask))
        {
            result = hit.position;
            return true;
        }
        result = Vector3.zero;
        return false;
    }

    private static bool IsTooCloseToFire(Vector3 pos)
    {
        foreach (Transform f in FireSources)
        {
            if (f != null && Vector3.Distance(pos, f.position) < FireSafetyRadius) return true;
        }
        return false;
    }

    private bool IsNearAnyFire()
    {
        foreach (Transform f in FireSources)
        {
            if (f != null && Vector3.Distance(transform.position, f.position) < FireSafetyRadius * 1.5f)
                return true;
        }
        return false;
    }

    private void MarkEvacuated()
    {
        CurrentState           = AgentState.Evacuated;
        navMeshAgent.isStopped = true;
        UpdateIndicator();
        SimulationManager.Instance?.OnAgentEvacuated();
        gameObject.SetActive(false);
    }

    private void UpdateIndicator() => indicator?.SetState(CurrentState, isPanicked);

    // ─── Animation ────────────────────────────────────────────────────────────

    private void DriveAnimator()
    {
        if (animator == null) return;
        float speed = navMeshAgent != null ? navMeshAgent.velocity.magnitude : 0f;
        animator.SetFloat(AnimParamSpeed, speed, 0.08f, Time.deltaTime);
    }
}

/// <summary>Snapshot of all agent state counts at a point in time.</summary>
public struct AgentStateCounts
{
    public int Idle;
    public int Wandering;
    public int Evacuating;
    public int Evacuated;
    public int Injured;
    public int Panicked;
}
