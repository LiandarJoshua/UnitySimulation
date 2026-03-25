using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Supplementary component on EvacuationLeader agents.
/// In Phase 2 the leader broadcasts evacuation instructions to nearby civilians
/// that are within the configurable perception radius AND have unobstructed line of sight.
/// Agents that receive the signal switch from Panicking to Evacuating.
/// </summary>
[RequireComponent(typeof(AgentBrain))]
public class EvacuationLeader : MonoBehaviour
{
    [Header("Leader Settings")]
    [Tooltip("Maximum distance at which a civilian can hear the leader.")]
    [SerializeField] private float perceptionRadius = 20f;

    [Tooltip("Layers that block line-of-sight raycasts.")]
    [SerializeField] private LayerMask sightBlockMask = ~0;

    [Tooltip("How often (seconds) the leader scans for nearby civilians to direct.")]
    [SerializeField] private float broadcastInterval = 1.5f;

    // ─── Runtime ──────────────────────────────────────────────────────────────

    private AgentBrain brain;
    private float      broadcastTimer;
    private bool       phase2Active;

    // ─── Public API ───────────────────────────────────────────────────────────

    /// <summary>Overrides the perception radius at runtime (e.g. from Dashboard slider).</summary>
    public float PerceptionRadius
    {
        get => perceptionRadius;
        set => perceptionRadius = Mathf.Max(0f, value);
    }

    // ─── Lifecycle ────────────────────────────────────────────────────────────

    private void Awake()
    {
        brain = GetComponent<AgentBrain>();
    }

    private void Update()
    {
        if (!phase2Active) return;

        broadcastTimer -= Time.deltaTime;
        if (broadcastTimer <= 0f)
        {
            broadcastTimer = broadcastInterval;
            BroadcastEvacuation();
        }
    }

    /// <summary>Called by EvacSimManager when Phase 2 starts.</summary>
    public void ActivatePhase2()
    {
        phase2Active   = true;
        broadcastTimer = 0f;
    }

    // ─── Broadcast ────────────────────────────────────────────────────────────

    private void BroadcastEvacuation()
    {
        Vector3 origin = transform.position + Vector3.up * 1.5f;

        foreach (AgentBrain agent in AgentBrain.GetAllAgents())
        {
            if (agent == null || agent == brain) continue;
            if (agent.Role == AgentRole.EvacuationLeader) continue;
            if (agent.CurrentState != AgentBrain.AgentState.Panicking &&
                agent.CurrentState != AgentBrain.AgentState.Wandering)
                continue;

            float dist = Vector3.Distance(transform.position, agent.transform.position);
            if (dist > perceptionRadius) continue;

            // Line of sight check.
            Vector3 targetPos = agent.transform.position + Vector3.up * 1.5f;
            Vector3 dir       = targetPos - origin;
            if (Physics.Raycast(origin, dir.normalized, dir.magnitude, sightBlockMask,
                                QueryTriggerInteraction.Ignore))
                continue;

            // The civilian receives instructions — begin evacuation.
            agent.StartEvacuating();
        }
    }
}
