using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Scene-level singleton that tracks all casualty agents and exposes them
/// to Medical Team agents and the dashboard.
/// </summary>
public class CasualtyHandler : MonoBehaviour
{
    public static CasualtyHandler Instance { get; private set; }

    private readonly List<AgentBrain> casualties = new List<AgentBrain>();

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    /// <summary>Registers a newly created casualty.</summary>
    public void RegisterCasualty(AgentBrain agent)
    {
        if (!casualties.Contains(agent))
            casualties.Add(agent);
    }

    /// <summary>Returns the nearest registered casualty to the given position, or null.</summary>
    public AgentBrain GetNearest(Vector3 from)
    {
        AgentBrain nearest = null;
        float      best    = float.MaxValue;

        foreach (var c in casualties)
        {
            if (c == null) continue;
            float d = Vector3.Distance(from, c.transform.position);
            if (d < best) { best = d; nearest = c; }
        }

        return nearest;
    }

    /// <summary>Returns a read-only view of all casualties.</summary>
    public IReadOnlyList<AgentBrain> Casualties => casualties;

    /// <summary>Clears casualty list on reset.</summary>
    public void ResetCasualties() => casualties.Clear();
}
