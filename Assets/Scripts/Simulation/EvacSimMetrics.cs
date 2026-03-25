using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Records and exposes evacuation metrics for the dashboard.
/// Mirrors the interface of the existing EvacuationMetrics for compatibility
/// while extending it with casualty tracking and phase-aware timing.
/// </summary>
public class EvacSimMetrics : MonoBehaviour
{
    public static EvacSimMetrics Instance { get; private set; }

    private const float PeakFlowWindow   = 10f;
    private const float FlowSampleRate   = 1f;
    private const int   MaxFlowSamples   = 60;

    private readonly List<float> evacuationTimes  = new List<float>();
    private readonly List<float> casualtyTimes    = new List<float>();
    private readonly List<float> flowRateHistory  = new List<float>();

    private float flowBucketTimer;
    private int   lastEvacCount;

    // ─── Lifecycle ────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void Update()
    {
        var sim = EvacSimManager.Instance;
        if (sim == null || !sim.SimulationActive) return;

        flowBucketTimer += Time.deltaTime;
        if (flowBucketTimer >= FlowSampleRate)
        {
            flowBucketTimer = 0f;
            int newThisSec = sim.EvacuatedCount - lastEvacCount;
            lastEvacCount  = sim.EvacuatedCount;
            flowRateHistory.Add(newThisSec / FlowSampleRate);
            if (flowRateHistory.Count > MaxFlowSamples)
                flowRateHistory.RemoveAt(0);
        }
    }

    // ─── Record ───────────────────────────────────────────────────────────────

    public void RecordEvacuation(int count, float timeElapsed)
    {
        evacuationTimes.Add(timeElapsed);
    }

    public void RecordCasualty(int count)
    {
        casualtyTimes.Add(Time.time);
    }

    // ─── Data Accessors ───────────────────────────────────────────────────────

    public IReadOnlyList<float> GetFlowRateHistory()  => flowRateHistory;
    public IReadOnlyList<float> GetRawEvacuationTimes() => evacuationTimes;

    public float GetEvacuationProgress()
    {
        var sim = EvacSimManager.Instance;
        if (sim == null || sim.TotalAgents == 0) return 0f;
        return (float)sim.EvacuatedCount / sim.TotalAgents;
    }

    public float GetElapsedEvacuationTime()
    {
        // Phase 2 elapsed time.
        var sim = EvacSimManager.Instance;
        if (sim == null || sim.CurrentPhase < EvacSimManager.SimPhase.Phase2_Evacuation) return 0f;
        return evacuationTimes.Count > 0 ? evacuationTimes[evacuationTimes.Count - 1] : 0f;
    }

    public float GetAverageEvacuationTime()
    {
        if (evacuationTimes.Count == 0) return 0f;
        float sum = 0f;
        foreach (float t in evacuationTimes) sum += t;
        return sum / evacuationTimes.Count;
    }

    public float GetPeakFlowRate()
    {
        if (flowRateHistory.Count == 0) return 0f;
        float peak = 0f;
        foreach (float v in flowRateHistory)
            if (v > peak) peak = v;
        return peak;
    }

    public float GetAverageFlowRate()
    {
        var sim = EvacSimManager.Instance;
        if (sim == null) return 0f;
        float elapsed = GetElapsedEvacuationTime();
        return elapsed > 0f ? sim.EvacuatedCount / elapsed : 0f;
    }

    public float GetFirstAgentOutTime() =>
        evacuationTimes.Count > 0 ? evacuationTimes[0] : -1f;

    public float[] GetEvacuationHistogram(int binCount, float binWidth)
    {
        float[] bins = new float[binCount];
        foreach (float t in evacuationTimes)
        {
            int idx = Mathf.Clamp(Mathf.FloorToInt(t / binWidth), 0, binCount - 1);
            bins[idx]++;
        }
        float max = 0f;
        foreach (float v in bins) if (v > max) max = v;
        if (max > 0f) for (int i = 0; i < bins.Length; i++) bins[i] /= max;
        return bins;
    }

    public void ResetMetrics()
    {
        evacuationTimes.Clear();
        casualtyTimes.Clear();
        flowRateHistory.Clear();
        flowBucketTimer = 0f;
        lastEvacCount   = 0;
    }
}
