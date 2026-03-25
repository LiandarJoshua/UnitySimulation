using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Advanced analytics layer that collects per-frame per-agent data for deeper metrics.
/// Tracks: agent speed samples, zone occupancy, bottleneck intensity near the exit,
/// cumulative evacuation curve, and an overall efficiency score.
///
/// All data is exposed as read-only snapshots for the dashboard to consume.
/// </summary>
public class SimulationAnalytics : MonoBehaviour
{
    public static SimulationAnalytics Instance { get; private set; }

    // ─── Configuration ────────────────────────────────────────────────────────

    private const float SampleInterval        = 0.5f;  // seconds between each analytics sample
    private const int   MaxSpeedSamples       = 120;   // rolling window length for speed history
    private const int   MaxBottleneckSamples  = 60;    // rolling window for bottleneck intensity
    private const float BottleneckRadius      = 5f;    // metres around exit considered "funnel"
    private const int   ZoneCount             = 4;     // number of spatial zones tracked

    // ─── Serialized ───────────────────────────────────────────────────────────

    [Header("Zone Boundaries (auto-sized from spawn/exit if left at 0)")]
    [SerializeField] private float zoneDepth = 20f;   // depth of each zone along evac corridor

    // ─── Runtime Data ─────────────────────────────────────────────────────────

    // Rolling average speed of all active agents, sampled every SampleInterval.
    private readonly List<float> avgSpeedHistory      = new List<float>();

    // Rolling bottleneck intensity (agents / s in the exit funnel).
    private readonly List<float> bottleneckHistory    = new List<float>();

    // Cumulative evacuated count sampled over time for the S-curve chart.
    private readonly List<float> cumulativeEvacCurve  = new List<float>();

    // Zone occupancy snapshots: zone index → agent count at each sample.
    // Stored as a list of int[] snapshots.
    private readonly List<int[]> zoneOccupancyHistory = new List<int[]>();

    // Zone labels
    private static readonly string[] ZoneLabels = { "Zone A", "Zone B", "Zone C", "Zone D" };

    private float sampleTimer;
    private int   lastEvacCount;

    // ─── Public Properties ────────────────────────────────────────────────────

    /// <summary>Rolling average agent speed history (m/s), most-recent last.</summary>
    public IReadOnlyList<float> AvgSpeedHistory      => avgSpeedHistory;

    /// <summary>Rolling bottleneck intensity near the exit, most-recent last.</summary>
    public IReadOnlyList<float> BottleneckHistory    => bottleneckHistory;

    /// <summary>Cumulative fraction of agents evacuated over simulation time (0–1).</summary>
    public IReadOnlyList<float> CumulativeEvacCurve  => cumulativeEvacCurve;

    /// <summary>Most-recent zone occupancy snapshot (count per zone).</summary>
    public int[] LatestZoneOccupancy { get; private set; } = new int[ZoneCount];

    /// <summary>Human-readable zone labels.</summary>
    public static IReadOnlyList<string> Zones => ZoneLabels;

    /// <summary>
    /// Efficiency score 0–100. Combines evacuation speed, injury rate, and flow consistency.
    /// </summary>
    public float EfficiencyScore { get; private set; }

    /// <summary>Standard deviation of individual evacuation times (seconds).</summary>
    public float EvacuationTimeStdDev { get; private set; }

    /// <summary>Median evacuation time (seconds), or -1 if fewer than 2 agents evacuated.</summary>
    public float MedianEvacuationTime { get; private set; } = -1f;

    /// <summary>Survival rate: fraction of total agents that evacuated (not injured).</summary>
    public float SurvivalRate { get; private set; }

    /// <summary>Current average speed of all evacuating agents (m/s).</summary>
    public float CurrentAvgSpeed { get; private set; }

    /// <summary>Number of agents currently congested near the exit funnel.</summary>
    public int CurrentBottleneckCount { get; private set; }

    // ─── Private ──────────────────────────────────────────────────────────────

    private Transform exitTransform;

    // ─── Unity Lifecycle ─────────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void Update()
    {
        var sim = SimulationManager.Instance;
        if (sim == null || !sim.SimulationRunning) return;

        sampleTimer += Time.deltaTime;
        if (sampleTimer < SampleInterval) return;
        sampleTimer = 0f;

        TakeSample(sim);
    }

    // ─── Sampling ─────────────────────────────────────────────────────────────

    private void TakeSample(SimulationManager sim)
    {
        var agents = AgentController.GetAllAgents();
        int total  = sim.TotalAgents;

        // ── Speed sample ──────────────────────────────────────────────────────
        float speedSum = 0f;
        int   speedN   = 0;
        int   exitFunnelCount = 0;

        if (exitTransform == null)
            exitTransform = FindExitTransform();

        Vector3 exitPos = exitTransform != null ? exitTransform.position : Vector3.zero;

        // ── Zone occupancy ────────────────────────────────────────────────────
        int[] zoneSnap = new int[ZoneCount];

        foreach (var agent in agents)
        {
            if (agent == null) continue;

            var state = agent.CurrentState;
            if (state == AgentController.AgentState.Evacuated ||
                state == AgentController.AgentState.Injured) continue;

            var nav = agent.GetNavAgent();
            if (nav != null)
            {
                float spd = nav.velocity.magnitude;
                speedSum += spd;
                speedN++;

                // Bottleneck: agents close to the exit and still evacuating.
                if (state == AgentController.AgentState.Evacuating &&
                    Vector3.Distance(agent.transform.position, exitPos) <= BottleneckRadius)
                {
                    exitFunnelCount++;
                }
            }

            // Zone classification: bucket by distance to exit.
            float distToExit = Vector3.Distance(agent.transform.position, exitPos);
            int zoneIdx = Mathf.Clamp(Mathf.FloorToInt(distToExit / zoneDepth), 0, ZoneCount - 1);
            zoneSnap[zoneIdx]++;
        }

        CurrentAvgSpeed       = speedN > 0 ? speedSum / speedN : 0f;
        CurrentBottleneckCount = exitFunnelCount;

        AddCapped(avgSpeedHistory,   CurrentAvgSpeed,       MaxSpeedSamples);
        AddCapped(bottleneckHistory, exitFunnelCount,       MaxBottleneckSamples);

        LatestZoneOccupancy = zoneSnap;
        if (zoneOccupancyHistory.Count > 200) zoneOccupancyHistory.RemoveAt(0);
        zoneOccupancyHistory.Add(zoneSnap);

        // ── Cumulative curve ──────────────────────────────────────────────────
        float fraction = total > 0 ? (float)sim.EvacuatedCount / total : 0f;
        if (sim.FireTriggered)
            AddCapped(cumulativeEvacCurve, fraction, 200);

        lastEvacCount = sim.EvacuatedCount;

        // ── Derived metrics ───────────────────────────────────────────────────
        ComputeDerivedMetrics(sim, total);
    }

    private void ComputeDerivedMetrics(SimulationManager sim, int total)
    {
        var metrics = EvacuationMetrics.Instance;
        if (metrics == null) return;

        int evacuated = sim.EvacuatedCount;
        int injured   = sim.InjuredCount;

        // Survival rate
        SurvivalRate = total > 0 ? (float)evacuated / total : 0f;

        // Std dev of evacuation times
        var times = metrics.GetRawEvacuationTimes();
        if (times != null && times.Count >= 2)
        {
            float mean = 0f;
            foreach (float t in times) mean += t;
            mean /= times.Count;

            float variance = 0f;
            foreach (float t in times) variance += (t - mean) * (t - mean);
            EvacuationTimeStdDev = Mathf.Sqrt(variance / times.Count);

            // Median
            var sorted = new List<float>(times);
            sorted.Sort();
            int mid = sorted.Count / 2;
            MedianEvacuationTime = sorted.Count % 2 == 0
                ? (sorted[mid - 1] + sorted[mid]) * 0.5f
                : sorted[mid];
        }
        else
        {
            EvacuationTimeStdDev = 0f;
            MedianEvacuationTime = times != null && times.Count == 1 ? times[0] : -1f;
        }

        // Efficiency score: combines speed, injury rate, flow consistency
        float elapsed     = metrics.GetElapsedEvacuationTime();
        float speedScore  = total > 0 && elapsed > 0f
            ? Mathf.Clamp01(1f - (elapsed / (total * 3f))) // 3s/agent = ideal
            : 0f;
        float injuryScore  = total > 0 ? 1f - (float)injured / total : 1f;
        float stdDevNorm   = EvacuationTimeStdDev > 0f
            ? Mathf.Clamp01(1f - (EvacuationTimeStdDev / 60f))  // 60s max expected std dev
            : 1f;

        EfficiencyScore = Mathf.Round((speedScore * 0.45f + injuryScore * 0.35f + stdDevNorm * 0.20f) * 100f);
    }

    // ─── Reset ────────────────────────────────────────────────────────────────

    /// <summary>Clears all analytics data. Called on simulation reset.</summary>
    public void ResetAnalytics()
    {
        avgSpeedHistory.Clear();
        bottleneckHistory.Clear();
        cumulativeEvacCurve.Clear();
        zoneOccupancyHistory.Clear();
        LatestZoneOccupancy   = new int[ZoneCount];
        EfficiencyScore       = 0f;
        EvacuationTimeStdDev  = 0f;
        MedianEvacuationTime  = -1f;
        SurvivalRate          = 0f;
        CurrentAvgSpeed       = 0f;
        CurrentBottleneckCount = 0;
        sampleTimer           = 0f;
        lastEvacCount         = 0;
        exitTransform         = null;
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static void AddCapped<T>(List<T> list, T value, int cap)
    {
        list.Add(value);
        if (list.Count > cap) list.RemoveAt(0);
    }

    private static Transform FindExitTransform()
    {
        // Attempt to locate the exit point via SimulationManager reflection-free lookup.
        // SimulationManager exposes ExitPoint as a property added below.
        var sim = SimulationManager.Instance;
        return sim != null ? sim.ExitPoint : null;
    }
}
