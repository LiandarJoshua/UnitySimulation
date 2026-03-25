using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A mask collection zone near the gas area. Agents within range detour here
/// before evacuating in order to collect a face mask.
/// Draws a gizmo cylinder in the editor for placement.
/// </summary>
public class MaskStation : MonoBehaviour
{
    [Header("Station Settings")]
    [SerializeField] private int  maskCapacity = 40;
    [SerializeField] private float gizmoRadius = 2f;
    [SerializeField] private Color gizmoColor  = new Color(0.2f, 0.8f, 1f, 0.4f);

    // ─── Static registry ──────────────────────────────────────────────────────

    private static readonly List<MaskStation> AllStations = new List<MaskStation>();

    public static IReadOnlyList<MaskStation> GetAll() => AllStations;

    /// <summary>Returns the nearest MaskStation that still has masks available.</summary>
    public static MaskStation FindNearest(Vector3 from)
    {
        MaskStation nearest = null;
        float       best    = float.MaxValue;

        foreach (var s in AllStations)
        {
            if (s == null || s.masksRemaining <= 0) continue;
            float d = Vector3.Distance(from, s.transform.position);
            if (d < best) { best = d; nearest = s; }
        }

        return nearest;
    }

    // ─── Instance ─────────────────────────────────────────────────────────────

    private int masksRemaining;

    public bool HasMasks => masksRemaining > 0;

    private void Awake() => masksRemaining = maskCapacity;

    private void OnEnable()
    {
        if (!AllStations.Contains(this)) AllStations.Add(this);
    }

    private void OnDisable() => AllStations.Remove(this);

    private void OnDestroy() => AllStations.Remove(this);

    /// <summary>Called by AgentBrain when an agent collects a mask from this station.</summary>
    public void NotifyCollected()
    {
        masksRemaining = Mathf.Max(0, masksRemaining - 1);
    }

    public void Reset() => masksRemaining = maskCapacity;

    // ─── Gizmo ────────────────────────────────────────────────────────────────

    private void OnDrawGizmos()
    {
        Gizmos.color = gizmoColor;
        Gizmos.DrawSphere(transform.position, gizmoRadius);
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(transform.position, gizmoRadius);

#if UNITY_EDITOR
        UnityEditor.Handles.Label(transform.position + Vector3.up * (gizmoRadius + 0.5f),
            $"Mask Station [{masksRemaining}/{maskCapacity}]");
#endif
    }
}
