using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Marks an exit point on the refinery map. Agents navigate to the nearest ExitPoint.
/// Tracks the queue of arrived agents. Draws a gizmo sphere in the Editor for easy placement.
/// </summary>
public class ExitPoint : MonoBehaviour
{
    [Header("Exit Settings")]
    [SerializeField] private string exitName = "Exit";

    [Header("Queue Visual")]
    [SerializeField] private float gizmoRadius = 2f;
    [SerializeField] private Color gizmoColor  = new Color(0.2f, 1f, 0.4f, 0.5f);

    // ─── Static registry ──────────────────────────────────────────────────────

    private static readonly List<ExitPoint> AllExits = new List<ExitPoint>();

    public static IReadOnlyList<ExitPoint> GetAll() => AllExits;

    /// <summary>
    /// Finds the nearest ExitPoint to the given world position.
    /// Returns null if no exit points are registered.
    /// </summary>
    public static ExitPoint FindNearest(Vector3 from)
    {
        ExitPoint nearest = null;
        float     best    = float.MaxValue;

        foreach (var e in AllExits)
        {
            if (e == null) continue;
            float d = Vector3.Distance(from, e.transform.position);
            if (d < best) { best = d; nearest = e; }
        }

        return nearest;
    }

    // ─── Instance ─────────────────────────────────────────────────────────────

    private readonly List<AgentBrain> queuedAgents = new List<AgentBrain>();

    public string ExitName       => exitName;
    public int    AgentsQueued   => queuedAgents.Count;

    private void OnEnable()
    {
        if (!AllExits.Contains(this)) AllExits.Add(this);
    }

    private void OnDisable() => AllExits.Remove(this);

    private void OnDestroy() => AllExits.Remove(this);

    /// <summary>Called when an agent arrives at this exit. Returns the agent's queue index.</summary>
    public int NotifyArrival(AgentBrain agent)
    {
        if (!queuedAgents.Contains(agent))
            queuedAgents.Add(agent);
        return queuedAgents.IndexOf(agent);
    }

    // ─── AgentController overload ─────────────────────────────────────────────

    private readonly List<AgentController> queuedControllers = new List<AgentController>();

    /// <summary>Called when an AgentController arrives at this exit. Returns queue index.</summary>
    public int NotifyArrival(AgentController agent)
    {
        if (!queuedControllers.Contains(agent))
            queuedControllers.Add(agent);
        return queuedControllers.IndexOf(agent);
    }

    /// <summary>Total agents queued (both AgentBrain and AgentController).</summary>
    public int TotalQueued => queuedAgents.Count + queuedControllers.Count;

    public void ResetQueue() { queuedAgents.Clear(); queuedControllers.Clear(); }

    // ─── Gizmo ────────────────────────────────────────────────────────────────

    private void OnDrawGizmos()
    {
        Gizmos.color = gizmoColor;
        Gizmos.DrawSphere(transform.position, gizmoRadius);
        Gizmos.color = Color.green;
        Gizmos.DrawWireSphere(transform.position, gizmoRadius);

#if UNITY_EDITOR
        UnityEditor.Handles.Label(transform.position + Vector3.up * (gizmoRadius + 0.5f), exitName);
#endif
    }
}
