using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using TMPro;

/// <summary>
/// Control-panel UI for the evacuation simulation.
///
/// Manages buttons: Reset, Trigger Fire, Add Agents, Remove Agents, and Observer Mode.
/// All UI elements are resolved by name through <see cref="FindDeepChild"/> so
/// Inspector wiring is optional.
///
/// Button state is refreshed every 0.5 s based on <see cref="EvacSimManager"/> flags.
/// An EventSystem with <see cref="InputSystemUIInputModule"/> is created automatically
/// if none exists in the scene.
/// </summary>
public class SimControlUI : MonoBehaviour
{
    private const float RefreshInterval = 0.5f;

    // ─── Inspector overrides (all optional) ───────────────────────────────────

    [Header("Control Buttons (auto-found by name if null)")]
    [SerializeField] private Button btnReset;
    [SerializeField] private Button btnTriggerFire;
    [SerializeField] private Button btnAddAgents;
    [SerializeField] private Button btnRemoveAgents;
    [SerializeField] private Button btnObserver;

    [Header("Display (auto-found by name if null)")]
    [SerializeField] private TextMeshProUGUI txtPhase;
    [SerializeField] private TextMeshProUGUI txtStatus;

    // ─── Runtime ──────────────────────────────────────────────────────────────

    private float nextRefreshTime;

    // ─── Lifecycle ────────────────────────────────────────────────────────────

    private void Awake()
    {
        EnsureEventSystem();
        AutoFindReferences();
    }

    private void Start()
    {
        WireButtons();
        RefreshButtonStates();
        SetStatus("Simulation initialising…");
    }

    private void Update()
    {
        if (Time.time >= nextRefreshTime)
        {
            nextRefreshTime = Time.time + RefreshInterval;
            RefreshButtonStates();
            RefreshPhaseLabel();
        }
    }

    // ─── Button Handlers ─────────────────────────────────────────────────────

    private void OnResetClicked()
    {
        EvacSimManager.Instance?.ResetSimulation();
        SetStatus("Simulation reset.");
        RefreshButtonStates();
    }

    private void OnTriggerFireClicked()
    {
        EvacSimManager.Instance?.TriggerAdditionalFire();
        SetStatus("Additional fire triggered.");
        RefreshButtonStates();
    }

    private void OnAddAgentsClicked()  => EvacSimManager.Instance?.AddAgents(10);
    private void OnRemoveAgentsClicked() => EvacSimManager.Instance?.RemoveAgents(10);

    private void OnObserverClicked()
    {
        FindAnyObjectByType<SimObserverModeController>()?.EnterObserverMode();
    }

    // ─── UI State ─────────────────────────────────────────────────────────────

    /// <summary>Updates button interactability based on current simulation state.</summary>
    private void RefreshButtonStates()
    {
        var sim = EvacSimManager.Instance;
        bool active = sim != null && sim.SimulationActive;

        if (btnReset       != null) btnReset.interactable       = active;
        if (btnTriggerFire != null) btnTriggerFire.interactable = active;
        if (btnAddAgents   != null) btnAddAgents.interactable   = true;
        if (btnRemoveAgents != null) btnRemoveAgents.interactable = active && sim.TotalAgents > 0;
        if (btnObserver    != null) btnObserver.interactable    = true;
    }

    private void RefreshPhaseLabel()
    {
        if (txtPhase == null) return;
        var sim = EvacSimManager.Instance;
        if (sim == null) return;

        txtPhase.text = sim.CurrentPhase switch
        {
            EvacSimManager.SimPhase.Idle               => "STANDBY",
            EvacSimManager.SimPhase.Phase1_Breakout    => "BREAKOUT",
            EvacSimManager.SimPhase.Phase2_Evacuation  => "EVACUATING",
            EvacSimManager.SimPhase.Phase3_Medical     => "MEDICAL",
            EvacSimManager.SimPhase.Complete           => "COMPLETE",
            _                                          => "STANDBY"
        };
    }

    private void SetStatus(string message)
    {
        if (txtStatus != null)
            txtStatus.text = message;
    }

    // ─── Wiring ───────────────────────────────────────────────────────────────

    private void WireButtons()
    {
        if (btnReset        != null) btnReset.onClick.AddListener(OnResetClicked);
        if (btnTriggerFire  != null) btnTriggerFire.onClick.AddListener(OnTriggerFireClicked);
        if (btnAddAgents    != null) btnAddAgents.onClick.AddListener(OnAddAgentsClicked);
        if (btnRemoveAgents != null) btnRemoveAgents.onClick.AddListener(OnRemoveAgentsClicked);
        if (btnObserver     != null) btnObserver.onClick.AddListener(OnObserverClicked);
    }

    // ─── Auto-Discovery ───────────────────────────────────────────────────────

    private void AutoFindReferences()
    {
        if (btnReset        == null) btnReset        = FindButtonDeep("BtnReset");
        if (btnTriggerFire  == null) btnTriggerFire  = FindButtonDeep("BtnFire");
        if (btnAddAgents    == null) btnAddAgents    = FindButtonDeep("BtnAddAgents");
        if (btnRemoveAgents == null) btnRemoveAgents = FindButtonDeep("BtnRemoveAgents");
        if (btnObserver     == null) btnObserver     = FindButtonDeep("BtnObserver");
        if (txtPhase        == null) txtPhase        = FindTMPDeep("TxtPhase");
        if (txtStatus       == null) txtStatus       = FindTMPDeep("TxtStatus");
    }

    private Button FindButtonDeep(string goName)
    {
        Transform t = transform.FindDeepChild(goName);
        return t != null ? t.GetComponent<Button>() : null;
    }

    private TextMeshProUGUI FindTMPDeep(string goName)
    {
        Transform t = transform.FindDeepChild(goName);
        return t != null ? t.GetComponent<TextMeshProUGUI>() : null;
    }

    /// <summary>
    /// Creates an EventSystem if none exists in the scene, using
    /// <see cref="InputSystemUIInputModule"/> for the new Input System.
    /// </summary>
    private static void EnsureEventSystem()
    {
        if (FindAnyObjectByType<EventSystem>() != null) return;

        GameObject es = new GameObject("EventSystem");
        es.AddComponent<EventSystem>();
        es.AddComponent<InputSystemUIInputModule>();
        Debug.Log("[SimControlUI] Created missing EventSystem with InputSystemUIInputModule.");
    }
}
