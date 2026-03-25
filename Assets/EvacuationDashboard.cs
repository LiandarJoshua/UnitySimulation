using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Advanced evacuation dashboard with live metrics, five chart panels, and an
/// efficiency score rating.
///
/// Chart panels:
///   1. Flow Rate Bar Graph        — scrolling agents/s per second
///   2. Evacuation Time Histogram  — distribution of individual agent times
///   3. Cumulative Evac Curve      — S-curve of evacuated fraction vs time
///   4. Agent Speed Trend          — rolling average speed (m/s) line chart
///   5. Exit Bottleneck            — agents jammed near exit over time
///
/// Core KPI tiles:
///   Evacuated, Injured, Elapsed, First Out, Avg Time, Peak Flow, Avg Flow
///
/// Advanced KPI tiles:
///   Median Time, Std Dev, Survival %, Efficiency Score, Bottleneck Peak,
///   Current Speed, Zone Load
///
/// All GameObjects are located by name from the hierarchy so the layout can be
/// restructured in the Inspector without touching this script.
/// </summary>
public class EvacuationDashboard : MonoBehaviour
{
    // ─── Constants ────────────────────────────────────────────────────────────

    private const float RefreshInterval    = 0.5f;
    private const int   HistogramBins      = 10;
    private const float HistogramBinWidth  = 5f;
    private const int   FlowBarCount       = 30;
    private const int   CurveBarCount      = 40;
    private const int   SpeedBarCount      = 30;
    private const int   BottleneckBarCount = 30;

    // ─── Serialized References ────────────────────────────────────────────────

    [Header("Dashboard Body (hidden when minimised)")]
    [SerializeField] private GameObject dashboardBody;

    [Header("Toggle Button")]
    [SerializeField] private Button          toggleButton;
    [SerializeField] private TextMeshProUGUI toggleButtonLabel;

    [Header("Core KPI Tiles")]
    [SerializeField] private TextMeshProUGUI tileEvacuated;
    [SerializeField] private TextMeshProUGUI tileInjured;
    [SerializeField] private TextMeshProUGUI tileElapsed;
    [SerializeField] private TextMeshProUGUI tileFirstOut;
    [SerializeField] private TextMeshProUGUI tileAvgTime;
    [SerializeField] private TextMeshProUGUI tilePeakFlow;
    [SerializeField] private TextMeshProUGUI tileAvgFlow;

    [Header("Advanced KPI Tiles")]
    [SerializeField] private TextMeshProUGUI tileMedianTime;
    [SerializeField] private TextMeshProUGUI tileStdDev;
    [SerializeField] private TextMeshProUGUI tileSurvivalRate;
    [SerializeField] private TextMeshProUGUI tileEfficiency;
    [SerializeField] private TextMeshProUGUI tileBottleneckPeak;
    [SerializeField] private TextMeshProUGUI tileCurrentSpeed;
    [SerializeField] private TextMeshProUGUI tileZoneLoad;

    [Header("Progress Bar")]
    [SerializeField] private Image           progressBarFill;
    [SerializeField] private TextMeshProUGUI progressLabel;

    [Header("Agent Status Breakdown Bar")]
    [SerializeField] private RectTransform   statusBar;
    [SerializeField] private Image           statusEvacuating;
    [SerializeField] private Image           statusPanicked;
    [SerializeField] private Image           statusInjured;
    [SerializeField] private Image           statusSafe;
    [SerializeField] private TextMeshProUGUI statusLabel;

    [Header("Graph 1 – Flow Rate")]
    [SerializeField] private RectTransform   flowGraphContainer;
    [SerializeField] private GameObject      flowBarPrefab;
    [SerializeField] private TextMeshProUGUI flowGraphTitle;

    [Header("Graph 2 – Time Distribution Histogram")]
    [SerializeField] private RectTransform   histogramContainer;
    [SerializeField] private GameObject      histogramBarPrefab;
    [SerializeField] private TextMeshProUGUI histogramTitle;

    [Header("Graph 3 – Cumulative Evacuation Curve")]
    [SerializeField] private RectTransform   curveContainer;
    [SerializeField] private GameObject      curveBarPrefab;
    [SerializeField] private TextMeshProUGUI curveTitle;

    [Header("Graph 4 – Agent Speed Trend")]
    [SerializeField] private RectTransform   speedContainer;
    [SerializeField] private GameObject      speedBarPrefab;
    [SerializeField] private TextMeshProUGUI speedTitle;

    [Header("Graph 5 – Exit Bottleneck Intensity")]
    [SerializeField] private RectTransform   bottleneckContainer;
    [SerializeField] private GameObject      bottleneckBarPrefab;
    [SerializeField] private TextMeshProUGUI bottleneckTitle;

    [Header("Phase Banner & Efficiency Badge")]
    [SerializeField] private TextMeshProUGUI phaseBanner;
    [SerializeField] private TextMeshProUGUI efficiencyBadge;
    [SerializeField] private Image           efficiencyBadgeBg;

    // ─── Runtime ──────────────────────────────────────────────────────────────

    private float nextRefresh;
    private bool  isMinimised;
    private int   peakBottleneck;

    private readonly List<RectTransform> flowBars       = new List<RectTransform>();
    private readonly List<RectTransform> histogramBars  = new List<RectTransform>();
    private readonly List<RectTransform> curveBars      = new List<RectTransform>();
    private readonly List<RectTransform> speedBars      = new List<RectTransform>();
    private readonly List<RectTransform> bottleneckBars = new List<RectTransform>();

    // ─── Colour Palette ────────────────────────────────────────────────────────

    private static readonly Color ColEvacuating = new Color(0.18f, 0.55f, 0.90f);
    private static readonly Color ColPanicked   = new Color(1.00f, 0.65f, 0.00f);
    private static readonly Color ColInjured    = new Color(0.90f, 0.18f, 0.18f);
    private static readonly Color ColSafe       = new Color(0.18f, 0.75f, 0.44f);
    private static readonly Color ColFlowBar    = new Color(0.25f, 0.65f, 1.00f);
    private static readonly Color ColHistogram  = new Color(1.00f, 0.80f, 0.20f);
    private static readonly Color ColCurve      = new Color(0.30f, 0.90f, 0.55f);
    private static readonly Color ColSpeed      = new Color(0.80f, 0.45f, 1.00f);
    private static readonly Color ColBottleneck = new Color(1.00f, 0.38f, 0.20f);
    private static readonly Color ColGradeA     = new Color(0.10f, 0.80f, 0.35f);
    private static readonly Color ColGradeB     = new Color(0.55f, 0.85f, 0.20f);
    private static readonly Color ColGradeC     = new Color(1.00f, 0.75f, 0.10f);
    private static readonly Color ColGradeD     = new Color(1.00f, 0.38f, 0.10f);

    // ─── Unity Lifecycle ─────────────────────────────────────────────────────

    private void Awake()
    {
        AutoFind();

        if (progressBarFill != null)
        {
            progressBarFill.type       = Image.Type.Filled;
            progressBarFill.fillMethod = Image.FillMethod.Horizontal;
            progressBarFill.fillOrigin = 0;
        }

        BuildBarList(flowBars,       flowGraphContainer,  flowBarPrefab,       FlowBarCount,       ColFlowBar);
        BuildBarList(histogramBars,  histogramContainer,  histogramBarPrefab,  HistogramBins,      ColHistogram);
        BuildBarList(curveBars,      curveContainer,      curveBarPrefab,      CurveBarCount,      ColCurve);
        BuildBarList(speedBars,      speedContainer,      speedBarPrefab,      SpeedBarCount,      ColSpeed);
        BuildBarList(bottleneckBars, bottleneckContainer, bottleneckBarPrefab, BottleneckBarCount, ColBottleneck);
    }

    private void Start()
    {
        if (toggleButton != null)
            toggleButton.onClick.AddListener(OnToggleClicked);

        SetMinimised(false);
        nextRefresh = 0f;
    }

    private void Update()
    {
        if (isMinimised) return;
        if (Time.time < nextRefresh) return;
        nextRefresh = Time.time + RefreshInterval;
        Refresh();
    }

    // ─── Toggle ───────────────────────────────────────────────────────────────

    /// <summary>Toggles the dashboard body visibility.</summary>
    public void OnToggleClicked() => SetMinimised(!isMinimised);

    private void SetMinimised(bool minimised)
    {
        isMinimised = minimised;
        if (dashboardBody     != null) dashboardBody.SetActive(!isMinimised);
        if (toggleButtonLabel != null) toggleButtonLabel.text = isMinimised ? "▲ Show" : "▼ Hide";
    }

    // ─── Main Refresh ─────────────────────────────────────────────────────────

    private void Refresh()
    {
        var sim       = SimulationManager.Instance;
        var metrics   = EvacuationMetrics.Instance;
        var analytics = SimulationAnalytics.Instance;

        if (sim == null || metrics == null) return;

        bool  fireTriggered = sim.FireTriggered;
        int   total         = sim.TotalAgents;
        int   evacuated     = sim.EvacuatedCount;
        int   injured       = sim.InjuredCount;
        float elapsed       = metrics.GetElapsedEvacuationTime();
        float progress      = metrics.GetEvacuationProgress();

        // ── Phase banner ──────────────────────────────────────────────────────
        if (phaseBanner != null)
        {
            if (!sim.SimulationRunning)           phaseBanner.text = "STANDBY";
            else if (!fireTriggered)              phaseBanner.text = "PRE-EVACUATION";
            else if (evacuated >= total && total > 0) phaseBanner.text = "EVACUATION COMPLETE";
            else                                  phaseBanner.text = "EVACUATION IN PROGRESS";
        }

        // ── Core KPI tiles ────────────────────────────────────────────────────
        SetTile(tileEvacuated, $"{evacuated} / {total}", "Evacuated");
        SetTile(tileInjured,   $"{injured}",             "Injured");
        SetTile(tileElapsed,   fireTriggered ? FormatTime(elapsed) : "--", "Elapsed");
        SetTile(tileFirstOut,  metrics.GetFirstAgentOutTime() >= 0f
                                    ? $"{metrics.GetFirstAgentOutTime():F1}s" : "--", "First Out");
        SetTile(tileAvgTime,   fireTriggered && evacuated > 0
                                    ? $"{metrics.GetAverageEvacuationTime():F1}s" : "--", "Avg Time");
        SetTile(tilePeakFlow,  fireTriggered ? $"{metrics.GetPeakFlowRate():F2}/s" : "--", "Peak Flow");
        SetTile(tileAvgFlow,   fireTriggered ? $"{metrics.GetAverageFlowRate():F2}/s" : "--", "Avg Flow");

        // ── Advanced KPI tiles ────────────────────────────────────────────────
        if (analytics != null)
        {
            float median  = analytics.MedianEvacuationTime;
            float stdDev  = analytics.EvacuationTimeStdDev;
            float survive = analytics.SurvivalRate;
            float effic   = analytics.EfficiencyScore;
            int   bnNow   = analytics.CurrentBottleneckCount;
            if (bnNow > peakBottleneck) peakBottleneck = bnNow;
            float speed   = analytics.CurrentAvgSpeed;

            SetTile(tileMedianTime,     fireTriggered && median >= 0f ? $"{median:F1}s"       : "--", "Median Time");
            SetTile(tileStdDev,         fireTriggered && evacuated > 1 ? $"±{stdDev:F1}s"      : "--", "Std Dev");
            SetTile(tileSurvivalRate,   fireTriggered ? $"{survive * 100f:F1}%"                : "--", "Survival Rate");
            SetTile(tileEfficiency,     fireTriggered ? $"{effic:F0}"                          : "--", "Efficiency");
            SetTile(tileBottleneckPeak, fireTriggered ? $"{peakBottleneck}"                    : "--", "Bottleneck Peak");
            SetTile(tileCurrentSpeed,   $"{speed:F1} m/s",                                            "Avg Speed");
            SetTile(tileZoneLoad,       BuildZoneLoadString(analytics.LatestZoneOccupancy),            "Zone Load");

            RefreshEfficiencyBadge(effic, fireTriggered);
        }

        // ── Progress bar ──────────────────────────────────────────────────────
        if (progressBarFill != null) progressBarFill.fillAmount = progress;
        if (progressLabel   != null) progressLabel.text = $"{progress * 100f:F0}%";

        // ── Status breakdown bar ──────────────────────────────────────────────
        RefreshStatusBar(total, evacuated, injured);

        // ── Graph 1: Flow rate ────────────────────────────────────────────────
        RefreshBarGraph(flowBars, metrics.GetFlowRateHistory(), FlowBarCount, flowGraphTitle,
            max => $"Flow Rate (agents/s)  —  peak: {max:F1}");

        // ── Graph 2: Histogram ────────────────────────────────────────────────
        RefreshHistogram(metrics.GetEvacuationHistogram(HistogramBins, HistogramBinWidth));

        if (analytics != null)
        {
            // ── Graph 3: Cumulative curve ─────────────────────────────────────
            RefreshBarGraph(curveBars, analytics.CumulativeEvacCurve, CurveBarCount, curveTitle,
                _ => "Cumulative Evacuated (fraction of total)");

            // ── Graph 4: Speed trend ──────────────────────────────────────────
            RefreshBarGraph(speedBars, analytics.AvgSpeedHistory, SpeedBarCount, speedTitle,
                max => $"Avg Agent Speed (m/s)  —  cur: {analytics.CurrentAvgSpeed:F1}");

            // ── Graph 5: Bottleneck ───────────────────────────────────────────
            RefreshBarGraph(bottleneckBars, analytics.BottleneckHistory, BottleneckBarCount, bottleneckTitle,
                _ => $"Exit Bottleneck (agents in funnel)  —  peak: {peakBottleneck}");
        }
    }

    // ─── Status Bar ───────────────────────────────────────────────────────────

    private void RefreshStatusBar(int total, int evacuated, int injured)
    {
        if (statusBar == null || total == 0) return;

        var counts = AgentController.GetStateCounts();

        float fEvacuating = (float)counts.Evacuating / total;
        float fPanicked   = (float)counts.Panicked   / total;
        float fInjured    = (float)injured            / total;
        float fSafe       = Mathf.Max(0f, 1f - fEvacuating - fPanicked - fInjured);

        float cursor = 0f;
        PositionSegment(statusSafe,       ref cursor, fSafe,       ColSafe);
        PositionSegment(statusEvacuating, ref cursor, fEvacuating, ColEvacuating);
        PositionSegment(statusPanicked,   ref cursor, fPanicked,   ColPanicked);
        PositionSegment(statusInjured,    ref cursor, fInjured,    ColInjured);

        if (statusLabel != null)
            statusLabel.text = $"Evacuating {counts.Evacuating}  |  Panicked {counts.Panicked}  |  Injured {injured}  |  Safe {counts.Wandering + counts.Idle}  |  Evacuated {counts.Evacuated}";
    }

    private static void PositionSegment(Image img, ref float cursor, float fraction, Color col)
    {
        if (img == null) return;
        img.color = col;
        var rt = img.rectTransform;
        rt.anchorMin = new Vector2(cursor, 0f);
        rt.anchorMax = new Vector2(Mathf.Min(cursor + fraction, 1f), 1f);
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        cursor += fraction;
    }

    // ─── Generic Bar Graph ────────────────────────────────────────────────────

    private void RefreshBarGraph(List<RectTransform> bars, IReadOnlyList<float> history,
        int barCount, TextMeshProUGUI title, System.Func<float, string> titleFn)
    {
        if (bars.Count == 0) return;

        float max = 1f;
        foreach (float v in history) if (v > max) max = v;

        int histCount = history.Count;
        for (int i = 0; i < bars.Count; i++)
        {
            var rt = bars[i];
            if (rt == null) continue;

            int   histIdx    = histCount - barCount + i;
            float normalised = (histIdx >= 0 && histIdx < histCount)
                ? Mathf.Clamp01(history[histIdx] / max)
                : 0f;

            rt.anchorMin = new Vector2((float)i       / barCount, 0f);
            rt.anchorMax = new Vector2((float)(i + 1) / barCount, normalised);
            rt.offsetMin = new Vector2(1f,  0f);
            rt.offsetMax = new Vector2(-1f, 0f);
        }

        if (title != null)
            title.text = titleFn(max);
    }

    // ─── Histogram ────────────────────────────────────────────────────────────

    private void RefreshHistogram(float[] bins)
    {
        if (bins == null || histogramBars.Count == 0) return;

        for (int i = 0; i < histogramBars.Count && i < bins.Length; i++)
        {
            var rt = histogramBars[i];
            if (rt == null) continue;
            rt.anchorMin = new Vector2((float)i       / bins.Length, 0f);
            rt.anchorMax = new Vector2((float)(i + 1) / bins.Length, Mathf.Clamp01(bins[i]));
            rt.offsetMin = new Vector2(2f,  0f);
            rt.offsetMax = new Vector2(-2f, 0f);
        }

        if (histogramTitle != null)
            histogramTitle.text = $"Evacuation Time Distribution  (bin = {HistogramBinWidth:F0}s)";
    }

    // ─── Efficiency Badge ─────────────────────────────────────────────────────

    private void RefreshEfficiencyBadge(float score, bool active)
    {
        if (efficiencyBadge == null) return;
        if (!active) { efficiencyBadge.text = ""; return; }

        string grade;
        Color  col;

        if      (score >= 80f) { grade = "A"; col = ColGradeA; }
        else if (score >= 60f) { grade = "B"; col = ColGradeB; }
        else if (score >= 40f) { grade = "C"; col = ColGradeC; }
        else                   { grade = "D"; col = ColGradeD; }

        efficiencyBadge.text  = $"<b>{score:F0}</b>  <size=80%>{grade}</size>";
        efficiencyBadge.color = col;

        if (efficiencyBadgeBg != null)
            efficiencyBadgeBg.color = new Color(col.r, col.g, col.b, 0.18f);
    }

    // ─── Bar Pool Builder ─────────────────────────────────────────────────────

    private static void BuildBarList(List<RectTransform> list, RectTransform container,
        GameObject prefab, int count, Color colour)
    {
        if (container == null || prefab == null) return;

        foreach (var b in list) if (b != null) Object.Destroy(b.gameObject);
        list.Clear();

        var layout = container.GetComponent<LayoutGroup>();
        if (layout != null) layout.enabled = false;

        for (int i = 0; i < count; i++)
        {
            GameObject go = Object.Instantiate(prefab, container);
            go.SetActive(true);
            go.name = $"{container.name}_Bar_{i:00}";
            var rt = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
            list.Add(rt);
            var img = go.GetComponent<Image>();
            if (img != null) img.color = colour;
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static string FormatTime(float seconds)
    {
        int m = Mathf.FloorToInt(seconds / 60f);
        int s = Mathf.FloorToInt(seconds % 60f);
        return m > 0 ? $"{m}m {s:00}s" : $"{s}s";
    }

    private static string BuildZoneLoadString(int[] zones)
    {
        if (zones == null || zones.Length == 0) return "--";
        var labels = SimulationAnalytics.Zones;
        int max = 0;
        foreach (int v in zones) if (v > max) max = v;
        if (max == 0) return "Idle";
        string busiest = labels[0];
        for (int i = 0; i < zones.Length && i < labels.Count; i++)
            if (zones[i] == max) busiest = labels[i];
        return $"{busiest} ({max})";
    }

    /// <summary>Sets a KPI tile to show a bold value with a sub-label underneath.</summary>
    private static void SetTile(TextMeshProUGUI label, string value, string subLabel)
    {
        if (label == null) return;
        label.text = $"<size=130%><b>{value}</b></size>\n<size=70%>{subLabel}</size>";
    }

    // ─── AutoFind ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Locates all serialized references by searching child GameObjects by name.
    /// Inspector assignments are never overwritten.
    /// </summary>
    private void AutoFind()
    {
        dashboardBody     ??= FindChildGO("DashboardBody");
        toggleButton      ??= FindButton("ToggleButton");
        toggleButtonLabel ??= FindTMP("ToggleButtonLabel");

        tileEvacuated  ??= FindTMP("TileEvacuated");
        tileInjured    ??= FindTMP("TileInjured");
        tileElapsed    ??= FindTMP("TileElapsed");
        tileFirstOut   ??= FindTMP("TileFirstOut");
        tileAvgTime    ??= FindTMP("TileAvgTime");
        tilePeakFlow   ??= FindTMP("TilePeakFlow");
        tileAvgFlow    ??= FindTMP("TileAvgFlow");

        tileMedianTime     ??= FindTMP("TileMedianTime");
        tileStdDev         ??= FindTMP("TileStdDev");
        tileSurvivalRate   ??= FindTMP("TileSurvivalRate");
        tileEfficiency     ??= FindTMP("TileEfficiency");
        tileBottleneckPeak ??= FindTMP("TileBottleneckPeak");
        tileCurrentSpeed   ??= FindTMP("TileCurrentSpeed");
        tileZoneLoad       ??= FindTMP("TileZoneLoad");

        progressBarFill ??= FindImage("ProgressBarFill");
        progressLabel   ??= FindTMP("ProgressLabel");

        statusBar        ??= FindRect("StatusBar");
        statusEvacuating ??= FindImage("StatusEvacuating");
        statusPanicked   ??= FindImage("StatusPanicked");
        statusInjured    ??= FindImage("StatusInjured");
        statusSafe       ??= FindImage("StatusSafe");
        statusLabel      ??= FindTMP("StatusLabel");

        flowGraphContainer ??= FindRect("FlowGraphContainer");
        flowBarPrefab      ??= FindChildGO("FlowBarPrefab");
        flowGraphTitle     ??= FindTMP("FlowGraphTitle");

        histogramContainer  ??= FindRect("HistogramContainer");
        histogramBarPrefab  ??= FindChildGO("HistogramBarPrefab");
        histogramTitle      ??= FindTMP("HistogramTitle");

        curveContainer  ??= FindRect("CurveContainer");
        curveBarPrefab  ??= FindChildGO("CurveBarPrefab");
        curveTitle      ??= FindTMP("CurveTitle");

        speedContainer  ??= FindRect("SpeedContainer");
        speedBarPrefab  ??= FindChildGO("SpeedBarPrefab");
        speedTitle      ??= FindTMP("SpeedTitle");

        bottleneckContainer  ??= FindRect("BottleneckContainer");
        bottleneckBarPrefab  ??= FindChildGO("BottleneckBarPrefab");
        bottleneckTitle      ??= FindTMP("BottleneckTitle");

        phaseBanner       ??= FindTMP("PhaseBanner");
        efficiencyBadge   ??= FindTMP("EfficiencyBadge");
        efficiencyBadgeBg ??= FindImage("EfficiencyBadgeBg");
    }

    private TextMeshProUGUI FindTMP(string n)
    {
        Transform t = transform.FindDeepChild(n);
        return t != null ? t.GetComponent<TextMeshProUGUI>() : null;
    }

    private Image FindImage(string n)
    {
        Transform t = transform.FindDeepChild(n);
        return t != null ? t.GetComponent<Image>() : null;
    }

    private RectTransform FindRect(string n)
    {
        Transform t = transform.FindDeepChild(n);
        return t != null ? t.GetComponent<RectTransform>() : null;
    }

    private GameObject FindChildGO(string n)
    {
        Transform t = transform.FindDeepChild(n);
        return t != null ? t.gameObject : null;
    }

    private Button FindButton(string n)
    {
        Transform t = transform.FindDeepChild(n);
        return t != null ? t.GetComponent<Button>() : null;
    }
}
