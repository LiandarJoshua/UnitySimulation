using System.Collections.Generic;
using UnityEngine;

[System.Serializable]
public struct EvacuationDataPoint
{
    public int AgentsEvacuated;
    public float TimeFromEvacuationTrigger;
}

/// <summary>
/// Records and calculates evacuation performance metrics throughout the simulation.
/// Tracks evacuation events, injuries, live flow rate, and exposes structured data
/// for the dashboard visualisations.
/// </summary>
public class EvacuationMetrics : MonoBehaviour
{
    public static EvacuationMetrics Instance { get; private set; }

    private const float PeakFlowWindowSeconds = 10f;
    private const float FlowSampleInterval    = 1f;
    private const int   MaxFlowSamples        = 60;

    private readonly List<EvacuationDataPoint> dataPoints      = new List<EvacuationDataPoint>();
    private readonly List<float>               injuryTimes     = new List<float>();
    private readonly List<float>               flowRateHistory = new List<float>();

    private float flowBucketTimer;
    private int   agentsEvacuatedLastBucket;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void Update()
    {
        var sim = SimulationManager.Instance;
        if (sim == null || !sim.FireTriggered) return;

        flowBucketTimer += Time.deltaTime;
        if (flowBucketTimer >= FlowSampleInterval)
        {
            flowBucketTimer = 0f;
            int newThisSec = sim.EvacuatedCount - agentsEvacuatedLastBucket;
            agentsEvacuatedLastBucket = sim.EvacuatedCount;
            flowRateHistory.Add(newThisSec / FlowSampleInterval);
            if (flowRateHistory.Count > MaxFlowSamples)
                flowRateHistory.RemoveAt(0);
        }
    }

    // ─── Record Events ────────────────────────────────────────────────────────

    /// <summary>Records an agent evacuation event.</summary>
    public void RecordEvacuation(int evacuatedCount, float timeElapsed)
    {
        dataPoints.Add(new EvacuationDataPoint
        {
            AgentsEvacuated           = evacuatedCount,
            TimeFromEvacuationTrigger = timeElapsed
        });

        var sim = SimulationManager.Instance;
        if (sim != null && evacuatedCount >= sim.TotalAgents)
            LogFinalReport();
    }

    /// <summary>Records an agent injury event.</summary>
    public void RecordInjury(int injuredCount, float timeElapsed) => injuryTimes.Add(timeElapsed);

    // ─── Dashboard Data Access ─────────────────────────────────────────────────

    /// <summary>Returns current evacuation percentage (0–1).</summary>
    public float GetEvacuationProgress()
    {
        var sim = SimulationManager.Instance;
        if (sim == null || sim.TotalAgents == 0) return 0f;
        return (float)sim.EvacuatedCount / sim.TotalAgents;
    }

    /// <summary>Returns elapsed evacuation time in seconds.</summary>
    public float GetElapsedEvacuationTime()
    {
        var sim = SimulationManager.Instance;
        if (sim == null || !sim.FireTriggered) return 0f;
        return Time.time - sim.EvacuationStartTime;
    }

    /// <summary>Returns a read-only view of the per-second flow rate history.</summary>
    public IReadOnlyList<float> GetFlowRateHistory() => flowRateHistory;

    /// <summary>
    /// Returns normalised bar heights (0–1) for a time-to-evacuate histogram.
    /// Bins agents by the number of seconds after fire trigger at which they evacuated.
    /// </summary>
    public float[] GetEvacuationHistogram(int binCount, float binWidthSeconds)
    {
        float[] bins = new float[binCount];
        foreach (var pt in dataPoints)
        {
            int idx = Mathf.Clamp(Mathf.FloorToInt(pt.TimeFromEvacuationTrigger / binWidthSeconds), 0, binCount - 1);
            bins[idx]++;
        }
        float max = 0f;
        foreach (float v in bins) if (v > max) max = v;
        if (max > 0f) for (int i = 0; i < bins.Length; i++) bins[i] /= max;
        return bins;
    }

    /// <summary>Returns the peak flow rate (agents/s) using a sliding window.</summary>
    public float GetPeakFlowRate()
    {
        if (dataPoints.Count == 0) return 0f;
        int peak = 0;
        for (int i = 0; i < dataPoints.Count; i++)
        {
            float end = dataPoints[i].TimeFromEvacuationTrigger + PeakFlowWindowSeconds;
            int count = 0;
            for (int j = i; j < dataPoints.Count; j++)
            {
                if (dataPoints[j].TimeFromEvacuationTrigger <= end) count++;
                else break;
            }
            if (count > peak) peak = count;
        }
        return peak / PeakFlowWindowSeconds;
    }

    /// <summary>Returns the average time per agent to reach the exit.</summary>
    public float GetAverageEvacuationTime()
    {
        if (dataPoints.Count == 0) return 0f;
        float sum = 0f;
        foreach (var pt in dataPoints) sum += pt.TimeFromEvacuationTrigger;
        return sum / dataPoints.Count;
    }

    /// <summary>Returns the time the first agent reached the exit, or -1 if none yet.</summary>
    public float GetFirstAgentOutTime() => dataPoints.Count > 0 ? dataPoints[0].TimeFromEvacuationTrigger : -1f;

    /// <summary>Returns the time of the last recorded evacuation, or -1 if none yet.</summary>
    public float GetTotalEvacuationTime() => dataPoints.Count > 0 ? dataPoints[dataPoints.Count - 1].TimeFromEvacuationTrigger : -1f;

    /// <summary>
    /// Returns a read-only list of every individual agent evacuation time for
    /// advanced statistical processing (std dev, median) in SimulationAnalytics.
    /// </summary>
    public IReadOnlyList<float> GetRawEvacuationTimes()
    {
        var times = new List<float>(dataPoints.Count);
        foreach (var pt in dataPoints) times.Add(pt.TimeFromEvacuationTrigger);
        return times;
    }

    /// <summary>Returns the live average throughput rate (agents/s) since fire was triggered.</summary>
    public float GetAverageFlowRate()
    {
        float elapsed = GetElapsedEvacuationTime();
        var sim = SimulationManager.Instance;
        if (sim == null || elapsed <= 0f) return 0f;
        return sim.EvacuatedCount / elapsed;
    }

    // ─── Legacy string summary (compact in-scene text panel) ─────────────────

    /// <summary>Returns a live-updating formatted metrics summary for the UI text panel.</summary>
    public string GetMetricsSummary()
    {
        var sim = SimulationManager.Instance;
        if (sim == null || !sim.FireTriggered) return "Awaiting fire trigger...";

        int   total     = sim.TotalAgents;
        int   evacuated = sim.EvacuatedCount;
        int   injured   = sim.InjuredCount;
        float elapsed   = GetElapsedEvacuationTime();
        float pct       = total > 0 ? (float)evacuated / total * 100f : 0f;

        string firstStr = GetFirstAgentOutTime() >= 0f ? $"{GetFirstAgentOutTime():F1}s" : "N/A";
        string avgStr   = dataPoints.Count > 0 ? $"{GetAverageEvacuationTime():F1}s" : "N/A";

        return $"Evacuated : {evacuated} / {total} ({pct:F1}%)\n" +
               $"Injured   : {injured}\n" +
               $"Elapsed   : {elapsed:F1}s\n" +
               $"First Out : {firstStr}\n" +
               $"Avg Time  : {avgStr}\n" +
               $"Flow Rate : {GetAverageFlowRate():F2} /s\n" +
               $"Peak Flow : {GetPeakFlowRate():F2} /s";
    }

    /// <summary>Returns the live counter string shown in the HUD.</summary>
    public string GetCounterString()
    {
        var sim = SimulationManager.Instance;
        if (sim == null || !sim.SimulationRunning) return "";
        return $"{sim.EvacuatedCount} / {sim.TotalAgents}\nEvacuated";
    }

    /// <summary>Logs the full final metrics report to the Unity console.</summary>
    public void LogFinalReport()
    {
        var sim       = SimulationManager.Instance;
        int total     = sim != null ? sim.TotalAgents    : 0;
        int evacuated = sim != null ? sim.EvacuatedCount  : 0;
        int injured   = sim != null ? sim.InjuredCount    : 0;

        Debug.Log(
            "[EVACUATION FINAL REPORT]\n" +
            $"  Total Agents     : {total}\n" +
            $"  Total Evacuated  : {evacuated} ({(total > 0 ? (float)evacuated / total * 100f : 0f):F1}%)\n" +
            $"  Injuries         : {injured}\n" +
            $"  Total Evac Time  : {GetTotalEvacuationTime():F2}s\n" +
            $"  First Agent Out  : {GetFirstAgentOutTime():F2}s\n" +
            $"  Avg Time/Agent   : {GetAverageEvacuationTime():F2}s\n" +
            $"  Avg Flow Rate    : {GetAverageFlowRate():F2} agents/s\n" +
            $"  Peak Flow Rate   : {GetPeakFlowRate():F2} agents/s  ({PeakFlowWindowSeconds:F0}s window)"
        );
    }

    /// <summary>Clears all recorded data. Called on simulation reset.</summary>
    public void ResetMetrics()
    {
        dataPoints.Clear();
        injuryTimes.Clear();
        flowRateHistory.Clear();
        flowBucketTimer           = 0f;
        agentsEvacuatedLastBucket = 0;
    }
}
