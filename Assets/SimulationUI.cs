using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using TMPro;

/// <summary>
/// Manages the simulation control panel UI.
/// Buttons are found by their child-name in the hierarchy so they work even when
/// the Inspector references are not manually wired.
/// An EventSystem is created automatically if the scene does not have one.
/// </summary>
public class SimulationUI : MonoBehaviour
{
    private const float MetricsRefreshInterval = 0.5f;

    [Header("Control Buttons (auto-found if null)")]
    [SerializeField] private Button startButton;
    [SerializeField] private Button triggerFireButton;
    [SerializeField] private Button resetButton;
    [SerializeField] private Button logReportButton;
    [SerializeField] private Button observerButton;

    [Header("Display (auto-found if null)")]
    [SerializeField] private TextMeshProUGUI statusText;
    [SerializeField] private TextMeshProUGUI metricsText;
    [SerializeField] private TextMeshProUGUI counterText;

    private float nextRefreshTime;

    private void Awake()
    {
        EnsureEventSystem();
        AutoFindReferences();
    }

    private void Start()
    {
        if (startButton != null)
            startButton.onClick.AddListener(OnStartClicked);

        if (triggerFireButton != null)
            triggerFireButton.onClick.AddListener(OnTriggerFireClicked);

        if (resetButton != null)
            resetButton.onClick.AddListener(OnResetClicked);

        if (logReportButton != null)
            logReportButton.onClick.AddListener(OnLogReportClicked);

        if (observerButton != null)
            observerButton.onClick.AddListener(OnObserverClicked);

        RefreshButtonStates();
        SetStatus("Press 'Start Simulation' to begin.");
    }

    private void Update()
    {
        if (Time.time >= nextRefreshTime)
        {
            nextRefreshTime = Time.time + MetricsRefreshInterval;
            RefreshMetricsDisplay();
            RefreshButtonStates();
        }
    }

    // ─── Button Handlers ─────────────────────────────────────────────────────

    private void OnStartClicked()
    {
        SimulationManager.Instance?.StartSimulation();
        SetStatus("Simulation running. Agents wandering randomly.");
        RefreshButtonStates();
    }

    private void OnTriggerFireClicked()
    {
        SimulationManager.Instance?.TriggerFire();
        SetStatus("FIRE TRIGGERED — All agents evacuating to exit!");
        RefreshButtonStates();
    }

    private void OnResetClicked()
    {
        SimulationManager.Instance?.ResetSimulation();
        SetStatus("Simulation reset. Press 'Start Simulation' to begin again.");
        if (metricsText != null) metricsText.text = "";
        RefreshButtonStates();
    }

    private void OnLogReportClicked()
    {
        EvacuationMetrics.Instance?.LogFinalReport();
        SetStatus("Final report logged to Console.");
    }

    private void OnObserverClicked()
    {
        FindAnyObjectByType<ObserverModeController>()?.EnterObserverMode();
    }

    // ─── UI State ─────────────────────────────────────────────────────────────

    private void RefreshButtonStates()
    {
        var sim = SimulationManager.Instance;
        if (sim == null) return;

        if (startButton != null)
            startButton.interactable = !sim.SimulationRunning;

        if (triggerFireButton != null)
            triggerFireButton.interactable = sim.SimulationRunning && !sim.FireTriggered;

        if (resetButton != null)
            resetButton.interactable = sim.SimulationRunning;

        if (logReportButton != null)
            logReportButton.interactable = sim.FireTriggered;
    }

    private void RefreshMetricsDisplay()
    {
        if (metricsText == null) return;
        var metrics = EvacuationMetrics.Instance;
        metricsText.text = metrics != null ? metrics.GetMetricsSummary() : "";

        if (counterText != null)
            counterText.text = metrics != null ? metrics.GetCounterString() : "";
    }

    private void SetStatus(string message)
    {
        if (statusText != null)
            statusText.text = message;
    }

    // ─── Auto-Setup ───────────────────────────────────────────────────────────

    /// <summary>
    /// Finds all button and text references by searching the Canvas hierarchy by name.
    /// This avoids dependency on Inspector wiring, so buttons always work.
    /// </summary>
    private void AutoFindReferences()
    {
        if (startButton == null)        startButton        = FindButton("BtnStart");
        if (triggerFireButton == null)  triggerFireButton  = FindButton("BtnFire");
        if (resetButton == null)        resetButton        = FindButton("BtnReset");
        if (logReportButton == null)    logReportButton    = FindButton("BtnLogReport");
        if (observerButton == null)     observerButton     = FindButton("BtnObserver");

        if (statusText == null)  statusText  = FindTMP("StatusText");
        if (metricsText == null) metricsText = FindTMP("MetricsText");
        if (counterText == null) counterText = FindTMP("CounterText");
    }

    private Button FindButton(string goName)
    {
        Transform t = transform.FindDeepChild(goName);
        if (t == null)
        {
            Debug.LogWarning($"[SimulationUI] Could not find button GameObject '{goName}'.");
            return null;
        }
        Button btn = t.GetComponent<Button>();
        if (btn == null)
            Debug.LogWarning($"[SimulationUI] '{goName}' has no Button component.");
        return btn;
    }

    private TextMeshProUGUI FindTMP(string goName)
    {
        Transform t = transform.FindDeepChild(goName);
        return t != null ? t.GetComponent<TextMeshProUGUI>() : null;
    }

    /// <summary>
    /// Creates an EventSystem if none exists in the scene.
    /// Uses InputSystemUIInputModule (new Input System package) instead of
    /// StandaloneInputModule, which relies on the legacy UnityEngine.Input class.
    /// </summary>
    private static void EnsureEventSystem()
    {
        if (FindAnyObjectByType<EventSystem>() != null) return;

        GameObject es = new GameObject("EventSystem");
        es.AddComponent<EventSystem>();
        es.AddComponent<InputSystemUIInputModule>();
        Debug.Log("[SimulationUI] Created missing EventSystem with InputSystemUIInputModule.");
    }
}

/// <summary>Extension helper to search a Transform hierarchy by name.</summary>
public static class TransformExtensions
{
    /// <summary>Recursively finds the first child Transform with the given name.</summary>
    public static Transform FindDeepChild(this Transform parent, string name)
    {
        foreach (Transform child in parent)
        {
            if (child.name == name) return child;
            Transform result = child.FindDeepChild(name);
            if (result != null) return result;
        }
        return null;
    }
}
