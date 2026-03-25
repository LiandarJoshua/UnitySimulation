using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using TMPro;

/// <summary>
/// Evacuation simulation HUD — polished simulator-grade dashboard.
///
/// Layout:
///   TOP-LEFT     — Simulation header + phase badge + status message + action buttons
///   TOP-RIGHT    — Live statistics counters + evacuation progress bar
///   BELOW-RIGHT  — Simulation parameter sliders
///   BOTTOM-RIGHT — Selected agent inspector (shown on agent click)
/// </summary>
public class EvacSimDashboardUI : MonoBehaviour
{
    private const float RefreshInterval = 0.35f;
    private const float PanelWidth      = 380f;

    // ─── Inspector overrides (all optional) ───────────────────────────────────

    [Header("Optional manual overrides")]
    [SerializeField] private Button          btnReset;
    [SerializeField] private Button          btnTriggerFire;
    [SerializeField] private Button          btnAddAgents;
    [SerializeField] private Button          btnRemoveAgents;
    [SerializeField] private TextMeshProUGUI txtPhase;
    [SerializeField] private TextMeshProUGUI txtStatus;
    [SerializeField] private TextMeshProUGUI txtTotal;
    [SerializeField] private TextMeshProUGUI txtEvacuated;
    [SerializeField] private TextMeshProUGUI txtCasualties;
    [SerializeField] private TextMeshProUGUI txtRemaining;
    [SerializeField] private TextMeshProUGUI progressLabel;
    [SerializeField] private Slider          sliderAgentSpeed;
    [SerializeField] private Slider          sliderPanicDuration;
    [SerializeField] private Slider          sliderLeaderRadius;
    [SerializeField] private Slider          sliderFireSafeDistance;
    [SerializeField] private TextMeshProUGUI lblAgentSpeed;
    [SerializeField] private TextMeshProUGUI lblPanicDuration;
    [SerializeField] private TextMeshProUGUI lblLeaderRadius;
    [SerializeField] private TextMeshProUGUI lblFireSafeDistance;
    [SerializeField] private GameObject      agentInspectorPanel;
    [SerializeField] private TextMeshProUGUI txtAgentName;
    [SerializeField] private TextMeshProUGUI txtAgentState;
    [SerializeField] private TextMeshProUGUI txtAgentRole;
    [SerializeField] private TextMeshProUGUI txtAgentDest;

    // ─── Colours ──────────────────────────────────────────────────────────────

    private static readonly Color BgPanel       = new Color(0.07f, 0.09f, 0.13f, 0.92f);
    private static readonly Color BgAccent      = new Color(0.10f, 0.13f, 0.18f, 0.95f);
    private static readonly Color BgCounter     = new Color(0.10f, 0.13f, 0.18f, 1.00f);
    private static readonly Color BgSlider      = new Color(0.14f, 0.18f, 0.24f, 1.00f);
    private static readonly Color ColSeparator  = new Color(0.28f, 0.35f, 0.45f, 0.60f);

    private static readonly Color BtnPrimary    = new Color(0.18f, 0.38f, 0.72f, 1f);
    private static readonly Color BtnDanger     = new Color(0.62f, 0.12f, 0.12f, 1f);
    private static readonly Color BtnWarning    = new Color(0.60f, 0.32f, 0.05f, 1f);
    private static readonly Color BtnMuted      = new Color(0.18f, 0.22f, 0.28f, 1f);

    private static readonly Color ColWhite      = new Color(0.95f, 0.95f, 1.00f);
    private static readonly Color ColHeading    = new Color(0.65f, 0.80f, 1.00f);
    private static readonly Color ColSubheading = new Color(0.45f, 0.60f, 0.85f);
    private static readonly Color ColSubtle     = new Color(0.45f, 0.55f, 0.65f);
    private static readonly Color ColGreen      = new Color(0.22f, 0.90f, 0.48f);
    private static readonly Color ColRed        = new Color(0.96f, 0.28f, 0.28f);
    private static readonly Color ColOrange     = new Color(1.00f, 0.70f, 0.20f);
    private static readonly Color ColYellow     = new Color(1.00f, 0.88f, 0.22f);
    private static readonly Color ColCyan       = new Color(0.25f, 0.85f, 1.00f);
    private static readonly Color ColBarBg      = new Color(0.15f, 0.20f, 0.28f, 1f);
    private static readonly Color ColBarFill    = new Color(0.22f, 0.80f, 0.45f, 1f);

    // ─── Runtime ──────────────────────────────────────────────────────────────

    private float      nextRefresh;
    private AgentBrain selectedAgent;
    private Camera     mainCamera;
    private Canvas     rootCanvas;
    private Image      progressBarFill;

    // ─── Lifecycle ────────────────────────────────────────────────────────────

    private void Awake()
    {
        EnsureEventSystem();

        // Create a clean canvas as a NEW root GameObject in the SAME scene.
        // Must NOT use DontDestroyOnLoad — GraphicRaycaster only works with an
        // EventSystem in the same scene. Must NOT disable this gameObject —
        // the MonoBehaviour needs Update() alive to refresh button states.
        var canvasGO = new GameObject("SimHUD_Canvas");

        rootCanvas = canvasGO.AddComponent<Canvas>();
        rootCanvas.renderMode   = RenderMode.ScreenSpaceOverlay;
        rootCanvas.sortingOrder = 20;

        var scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight  = 0.5f;
        scaler.screenMatchMode     = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;

        canvasGO.AddComponent<GraphicRaycaster>();

        uiRoot = canvasGO.transform;
        BuildTopLeftPanel();
        BuildRightStatsPanel();
        BuildRightControlsPanel();
        BuildAgentInspectorPanel();
    }

    private void Start()
    {
        mainCamera = Camera.main;
        SetStatus("Press START to begin simulation.");
    }

    private void Update()
    {
        HandleAgentClick();

        if (Time.time >= nextRefresh)
        {
            nextRefresh = Time.time + RefreshInterval;
            RefreshCounters();
            RefreshAgentInspector();
        }
    }

    private Transform uiRoot; // root transform of our clean canvas

    // ─── UI Construction ──────────────────────────────────────────────────────

    private void BuildUIInto(Transform canvasTransform)
    {
        uiRoot = canvasTransform;
        BuildTopLeftPanel();
        BuildRightStatsPanel();
        BuildRightControlsPanel();
        BuildAgentInspectorPanel();
    }

    // Legacy entry kept for consistency — not called any more.
    private void BuildUI()
    {
        BuildTopLeftPanel();
        BuildRightStatsPanel();
        BuildRightControlsPanel();
        BuildAgentInspectorPanel();
    }

    // TOP-LEFT: header + status + action buttons ───────────────────────────────

    private Button btnStart;
    private Button btnEnterCharacter;

    private void BuildTopLeftPanel()
    {
        var panel = MakeAnchoredPanel("TL_Panel", BgPanel,
            new Vector2(0, 1), new Vector2(0, 1),
            new Vector2(16f, -16f), PanelWidth);

        AddVLayout(panel, new RectOffset(14, 14, 12, 14), 6f);

        // Header row: title + phase badge
        var headerRow = MakeHRow(panel, 26f, 8f);
        MakeTMP(headerRow, "SimLabel", "EVAC SIM", 13f, ColHeading, FontStyles.Bold);
        txtPhase = MakeTMP(headerRow, "TxtPhase", "STANDBY", 10f, ColYellow, FontStyles.Bold);
        txtPhase.alignment       = TextAlignmentOptions.Right;
        txtPhase.textWrappingMode = TextWrappingModes.NoWrap;

        MakeSeparator(panel);

        txtStatus = MakeTMP(panel, "TxtStatus", "Press START to begin…", 13f, ColSubtle, FontStyles.Normal);
        SetLayoutHeight(txtStatus.gameObject, 20f);

        MakeSeparator(panel);

        // Row 0: Start / Reset
        var row0 = MakeHRow(panel, 52f, 8f);
        btnStart = MakeBtn(row0, "BtnStart", "START SIMULATION", new Color(0.12f, 0.52f, 0.22f, 1f), OnStartClicked);
        btnReset = MakeBtn(row0, "BtnReset", "RESET",            BtnDanger,                          OnResetClicked);

        // Row 1: Trigger Fire / Enter Character
        var row1 = MakeHRow(panel, 52f, 8f);
        btnTriggerFire    = MakeBtn(row1, "BtnFire",      "TRIGGER FIRE",   BtnWarning,                         OnTriggerFireClicked);
        btnEnterCharacter = MakeBtn(row1, "BtnCharacter", "CHARACTER VIEW",  new Color(0.25f, 0.18f, 0.45f, 1f), OnEnterCharacterClicked);

        // Row 2: Add / Remove agents
        var row2 = MakeHRow(panel, 44f, 8f);
        btnAddAgents    = MakeBtn(row2, "BtnAdd",    "+ 10 AGENTS", BtnPrimary, () => EvacSimManager.Instance?.AddAgents(10));
        btnRemoveAgents = MakeBtn(row2, "BtnRemove", "− 10 AGENTS", BtnMuted,   () => EvacSimManager.Instance?.RemoveAgents(10));
    }

    // TOP-RIGHT: live stats counters + progress bar ────────────────────────────

    private void BuildRightStatsPanel()
    {
        var panel = MakeAnchoredPanel("Stats_Panel", BgAccent,
            new Vector2(1, 1), new Vector2(1, 1),
            new Vector2(-16f, -16f), PanelWidth);

        AddVLayout(panel, new RectOffset(12, 12, 12, 12), 8f);

        var title = MakeTMP(panel, "StatsTitle", "LIVE STATISTICS", 10f, ColSubheading, FontStyles.Bold);
        SetLayoutHeight(title.gameObject, 14f);
        MakeSeparator(panel);

        var topRow = MakeHRow(panel, 56f, 6f);
        txtTotal     = MakeCounterTile(topRow, "Total",     "0", "TOTAL",     ColWhite);
        txtEvacuated = MakeCounterTile(topRow, "Evacuated", "0", "EVACUATED", ColGreen);

        var botRow = MakeHRow(panel, 56f, 6f);
        txtCasualties = MakeCounterTile(botRow, "Casualties", "0", "CASUALTIES", ColRed);
        txtRemaining  = MakeCounterTile(botRow, "Remaining",  "0", "REMAINING",  ColOrange);

        MakeSeparator(panel);

        var progHeader = MakeTMP(panel, "ProgHeader", "EVACUATION PROGRESS", 9f, ColSubtle, FontStyles.Bold);
        SetLayoutHeight(progHeader.gameObject, 12f);

        progressLabel = MakeTMP(panel, "ProgressPct", "0%  (0 / 0)", 11f, ColGreen, FontStyles.Normal);
        SetLayoutHeight(progressLabel.gameObject, 15f);

        // Progress bar
        var barBgGO = new GameObject("BarBg", typeof(RectTransform), typeof(Image));
        barBgGO.transform.SetParent(panel.transform, false);
        barBgGO.GetComponent<Image>().color = ColBarBg;
        SetLayoutHeight(barBgGO, 7f);

        var barFillGO = new GameObject("BarFill", typeof(RectTransform), typeof(Image));
        barFillGO.transform.SetParent(barBgGO.transform, false);
        progressBarFill = barFillGO.GetComponent<Image>();
        progressBarFill.color = ColBarFill;

        var frt = barFillGO.GetComponent<RectTransform>();
        frt.anchorMin = new Vector2(0, 0); frt.anchorMax = new Vector2(0, 1);
        frt.pivot     = new Vector2(0, 0.5f);
        frt.sizeDelta = Vector2.zero;

        var brt = barBgGO.GetComponent<RectTransform>();
        brt.anchorMin = Vector2.zero; brt.anchorMax = Vector2.one;
    }

    // RIGHT: simulation controls sliders ──────────────────────────────────────

    private void BuildRightControlsPanel()
    {
        var panel = MakeAnchoredPanel("Controls_Panel", BgPanel,
            new Vector2(1, 1), new Vector2(1, 1),
            new Vector2(-16f, -230f), PanelWidth);

        AddVLayout(panel, new RectOffset(12, 12, 12, 14), 5f);

        var title = MakeTMP(panel, "CtrlTitle", "SIMULATION CONTROLS", 10f, ColSubheading, FontStyles.Bold);
        SetLayoutHeight(title.gameObject, 14f);
        MakeSeparator(panel);

        BuildSliderRow(panel, out sliderAgentSpeed,       out lblAgentSpeed,       "Agent Speed",    "m/s", 1f,  10f,  5.5f);
        BuildSliderRow(panel, out sliderPanicDuration,    out lblPanicDuration,    "Panic Duration", "s",   10f, 120f, 60f);
        BuildSliderRow(panel, out sliderLeaderRadius,     out lblLeaderRadius,     "Leader Radius",  "m",   5f,  50f,  20f);
        BuildSliderRow(panel, out sliderFireSafeDistance, out lblFireSafeDistance, "Fire Safe Dist", "m",   2f,  15f,  5f);

        WireSlider(sliderAgentSpeed,       1f,  10f,  5.5f, v => { EvacSimManager.Instance?.SetAgentSpeed(v);             lblAgentSpeed.text       = $"{v:F1} m/s"; });
        WireSlider(sliderPanicDuration,    10f, 120f, 60f,  v => { EvacSimManager.Instance?.SetPanicDuration(v);          lblPanicDuration.text    = $"{v:F0} s"; });
        WireSlider(sliderLeaderRadius,     5f,  50f,  20f,  v => { EvacSimManager.Instance?.SetLeaderPerceptionRadius(v); lblLeaderRadius.text     = $"{v:F0} m"; });
        WireSlider(sliderFireSafeDistance, 2f,  15f,  5f,   v => { EvacSimManager.Instance?.SetFireSafeDistance(v);       lblFireSafeDistance.text = $"{v:F0} m"; });
    }

    private void BuildSliderRow(GameObject parent,
        out Slider slider, out TextMeshProUGUI valueLabel,
        string displayName, string unit, float min, float max, float def)
    {
        var row = new GameObject($"Row_{displayName}", typeof(RectTransform));
        row.transform.SetParent(parent.transform, false);
        var rowHL = row.AddComponent<HorizontalLayoutGroup>();
        rowHL.childControlWidth        = true;
        rowHL.childControlHeight       = false;
        rowHL.childForceExpandWidth    = true;
        rowHL.childForceExpandHeight   = false;
        SetLayoutHeight(row, 15f);

        var nameLbl = MakeTMP(row, $"Name_{displayName}", displayName, 9f, ColSubtle, FontStyles.Normal);
        nameLbl.textWrappingMode = TextWrappingModes.NoWrap;

        valueLabel = MakeTMP(row, $"Val_{displayName}", $"{def} {unit}", 9f, ColCyan, FontStyles.Bold);
        valueLabel.alignment        = TextAlignmentOptions.Right;
        valueLabel.textWrappingMode = TextWrappingModes.NoWrap;

        var sliderGO = new GameObject($"Slider_{displayName}", typeof(RectTransform));
        sliderGO.transform.SetParent(parent.transform, false);
        SetLayoutHeight(sliderGO, 16f);

        slider = sliderGO.AddComponent<Slider>();

        var bg   = MakeImageChild(sliderGO, "Background", BgSlider);
        var bgRT = bg.GetComponent<RectTransform>();
        bgRT.anchorMin = Vector2.zero; bgRT.anchorMax = Vector2.one;
        bgRT.offsetMin = new Vector2(0, 3); bgRT.offsetMax = new Vector2(0, -3);

        var fillArea = new GameObject("Fill Area", typeof(RectTransform));
        fillArea.transform.SetParent(sliderGO.transform, false);
        var faRT = fillArea.GetComponent<RectTransform>();
        faRT.anchorMin = Vector2.zero; faRT.anchorMax = Vector2.one;
        faRT.offsetMin = new Vector2(4, 3); faRT.offsetMax = new Vector2(-14, -3);

        var fill   = MakeImageChild(fillArea, "Fill", new Color(0.22f, 0.55f, 1f));
        var fillRT = fill.GetComponent<RectTransform>();
        fillRT.anchorMin = Vector2.zero; fillRT.anchorMax = Vector2.one;
        fillRT.sizeDelta = Vector2.zero;

        var handleArea = new GameObject("Handle Slide Area", typeof(RectTransform));
        handleArea.transform.SetParent(sliderGO.transform, false);
        var haRT = handleArea.GetComponent<RectTransform>();
        haRT.anchorMin = Vector2.zero; haRT.anchorMax = Vector2.one;
        haRT.offsetMin = new Vector2(8, 0); haRT.offsetMax = new Vector2(-8, 0);

        var handle   = MakeImageChild(handleArea, "Handle", ColCyan);
        var handleRT = handle.GetComponent<RectTransform>();
        handleRT.sizeDelta = new Vector2(14, 0);
        handleRT.anchorMin = new Vector2(0, 0.1f); handleRT.anchorMax = new Vector2(0, 0.9f);

        slider.fillRect      = fill.GetComponent<RectTransform>();
        slider.handleRect    = handle.GetComponent<RectTransform>();
        slider.targetGraphic = handle.GetComponent<Image>();
        slider.direction     = Slider.Direction.LeftToRight;
        slider.minValue      = min;
        slider.maxValue      = max;
        slider.value         = def;
    }

    // AGENT INSPECTOR ─────────────────────────────────────────────────────────

    private void BuildAgentInspectorPanel()
    {
        agentInspectorPanel = MakeAnchoredPanel("AgentInspector", new Color(0.05f, 0.06f, 0.14f, 0.94f),
            new Vector2(1, 0), new Vector2(1, 0),
            new Vector2(-16f, 16f), PanelWidth);

        AddVLayout(agentInspectorPanel, new RectOffset(12, 12, 10, 10), 4f);

        var hdr = MakeTMP(agentInspectorPanel, "InspectorHeader", "SELECTED AGENT", 9f, ColSubheading, FontStyles.Bold);
        SetLayoutHeight(hdr.gameObject, 12f);
        MakeSeparator(agentInspectorPanel);

        txtAgentName  = MakeTMP(agentInspectorPanel, "TxtAgentName",  "—",        13f, ColWhite,  FontStyles.Bold);
        txtAgentState = MakeTMP(agentInspectorPanel, "TxtAgentState", "State: —", 10f, ColCyan,   FontStyles.Normal);
        txtAgentRole  = MakeTMP(agentInspectorPanel, "TxtAgentRole",  "Role:  —", 10f, ColSubtle, FontStyles.Normal);
        txtAgentDest  = MakeTMP(agentInspectorPanel, "TxtAgentDest",  "Dest:  —",  9f, ColSubtle, FontStyles.Normal);

        foreach (var t in new[] { txtAgentName, txtAgentState, txtAgentRole, txtAgentDest })
            SetLayoutHeight(t.gameObject, 15f);

        agentInspectorPanel.SetActive(false);
    }

    // ─── Button Handlers ──────────────────────────────────────────────────────

    private void OnStartClicked()
    {
        EvacSimManager.Instance?.StartSimulation();
        SetStatus("Simulation started.");
        if (btnStart != null) btnStart.interactable = false;
    }

    private void OnResetClicked()
    {
        EvacSimManager.Instance?.ResetSimulation();
        EvacSimMetrics.Instance?.ResetMetrics();
        selectedAgent = null;
        if (agentInspectorPanel != null) agentInspectorPanel.SetActive(false);
        if (btnStart != null) btnStart.interactable = true;
        SetStatus("Simulation reset. Press START to begin.");
    }

    private void OnTriggerFireClicked()
    {
        EvacSimManager.Instance?.TriggerAdditionalFire();
        SetStatus("Additional fire triggered!");
    }

    private void OnEnterCharacterClicked()
    {
        var playerCtrl = FindAnyObjectByType<AgentPlayerController>();
        if (playerCtrl == null) { SetStatus("No AgentPlayerController found."); return; }

        // Possess the first alive agent if none is selected, otherwise possess selected.
        AgentBrain target = selectedAgent;
        if (target == null)
        {
            var all = FindObjectsByType<AgentBrain>(FindObjectsSortMode.None);
            if (all.Length > 0) target = all[0];
        }

        if (target != null)
        {
            playerCtrl.PossessAgent(target);
            SetStatus($"Controlling {target.name}. ESC to release.");
        }
        else
        {
            SetStatus("No agents to control yet.");
        }
    }

    // ─── Counter Refresh ──────────────────────────────────────────────────────

    private void RefreshCounters()
    {
        var sim = EvacSimManager.Instance;
        if (sim == null) return;

        if (txtTotal      != null) txtTotal.text      = CounterText(sim.TotalAgents,    "TOTAL");
        if (txtEvacuated  != null) txtEvacuated.text  = CounterText(sim.EvacuatedCount, "EVACUATED");
        if (txtCasualties != null) txtCasualties.text = CounterText(sim.CasualtyCount,  "CASUALTIES");
        if (txtRemaining  != null) txtRemaining.text  = CounterText(sim.RemainingCount, "REMAINING");

        if (txtPhase != null)
        {
            txtPhase.text  = PhaseLabel(sim.CurrentPhase);
            txtPhase.color = PhaseColour(sim.CurrentPhase);
        }

        float prog = sim.TotalAgents > 0 ? (float)sim.EvacuatedCount / sim.TotalAgents : 0f;
        if (progressLabel != null)
            progressLabel.text = $"{prog * 100f:F0}%   ({sim.EvacuatedCount} / {sim.TotalAgents})";

        // Animate progress bar fill.
        if (progressBarFill != null)
        {
            var barBg = progressBarFill.transform.parent.GetComponent<RectTransform>();
            if (barBg != null)
            {
                float w = barBg.rect.width * prog;
                progressBarFill.GetComponent<RectTransform>().sizeDelta = new Vector2(w, 0);
            }
        }

        if (btnStart        != null) btnStart.interactable        = !sim.SimulationActive;
        if (btnTriggerFire  != null) btnTriggerFire.interactable  = sim.SimulationActive;
        if (btnAddAgents    != null) btnAddAgents.interactable    = sim.SimulationActive;
        if (btnRemoveAgents != null) btnRemoveAgents.interactable = sim.SimulationActive && sim.TotalAgents > 0;
    }

    private static string CounterText(int val, string label) =>
        $"<size=130%><b>{val}</b></size>\n<size=70%><color=#99AABB>{label}</color></size>";

    private static string PhaseLabel(EvacSimManager.SimPhase p) => p switch
    {
        EvacSimManager.SimPhase.Phase1_Breakout   => "PHASE 1  BREAKOUT",
        EvacSimManager.SimPhase.Phase2_Evacuation => "PHASE 2  EVACUATION",
        EvacSimManager.SimPhase.Phase3_Medical    => "PHASE 3  MEDICAL",
        EvacSimManager.SimPhase.Complete           => "COMPLETE",
        _                                           => "STANDBY"
    };

    private static Color PhaseColour(EvacSimManager.SimPhase p) => p switch
    {
        EvacSimManager.SimPhase.Phase1_Breakout   => ColRed,
        EvacSimManager.SimPhase.Phase2_Evacuation => ColOrange,
        EvacSimManager.SimPhase.Phase3_Medical    => ColCyan,
        EvacSimManager.SimPhase.Complete           => ColGreen,
        _                                           => ColYellow
    };

    // ─── Agent Click Inspection ───────────────────────────────────────────────

    private void HandleAgentClick()
    {
        var mouse = UnityEngine.InputSystem.Mouse.current;
        if (mouse == null || !mouse.leftButton.wasPressedThisFrame) return;
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;

        if (mainCamera == null) mainCamera = Camera.main;
        if (mainCamera == null) return;

        Ray ray = mainCamera.ScreenPointToRay(mouse.position.ReadValue());
        if (!Physics.Raycast(ray, out RaycastHit hit, 500f)) return;

        AgentBrain clicked = hit.collider.GetComponentInParent<AgentBrain>();
        selectedAgent = clicked;
        if (agentInspectorPanel != null) agentInspectorPanel.SetActive(clicked != null);
    }

    private void RefreshAgentInspector()
    {
        if (selectedAgent == null || agentInspectorPanel == null || !agentInspectorPanel.activeSelf) return;

        if (txtAgentName  != null) txtAgentName.text  = selectedAgent.name;
        if (txtAgentState != null) txtAgentState.text = $"State:  <b>{selectedAgent.CurrentState}</b>";
        if (txtAgentRole  != null) txtAgentRole.text  = $"Role:   <b>{selectedAgent.Role}</b>";
        if (txtAgentDest  != null)
        {
            var nav = selectedAgent.GetNavAgent();
            txtAgentDest.text = nav != null && nav.hasPath
                ? $"Dest:  ({nav.destination.x:F1}, {nav.destination.z:F1})"
                : "Dest:  —";
        }
    }

    // ─── Builder Helpers ──────────────────────────────────────────────────────

    private GameObject MakeAnchoredPanel(string name, Color bg,
        Vector2 anchor, Vector2 pivot, Vector2 pos, float width)
    {
        // Parent to uiRoot (our clean canvas) if available, else this transform.
        Transform parent = uiRoot != null ? uiRoot : transform;

        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        go.GetComponent<Image>().color = bg;

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin        = anchor;
        rt.anchorMax        = anchor;
        rt.pivot            = pivot;
        rt.anchoredPosition = pos;
        rt.sizeDelta        = new Vector2(width, 10f);

        AddContentSizeFitter(go);
        return go;
    }

    private static VerticalLayoutGroup AddVLayout(GameObject go, RectOffset padding, float spacing)
    {
        var vl = go.AddComponent<VerticalLayoutGroup>();
        vl.padding               = padding;
        vl.spacing               = spacing;
        vl.childControlWidth     = true;
        vl.childControlHeight    = false;
        vl.childForceExpandWidth  = true;
        vl.childForceExpandHeight = false;
        vl.childAlignment        = TextAnchor.UpperLeft;
        return vl;
    }

    private Button MakeBtn(GameObject parent, string name, string label, Color bg,
        UnityEngine.Events.UnityAction onClick)
    {
        var go  = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent.transform, false);

        // Ensure the button fills its row height — without this the HorizontalLayoutGroup
        // collapses childControlHeight children to zero, making them invisible and un-clickable.
        var le = go.AddComponent<LayoutElement>();
        le.flexibleWidth   = 1f;
        le.flexibleHeight  = 1f;

        var img = go.GetComponent<Image>();
        img.color = bg;

        var btn = go.GetComponent<Button>();
        var cs  = btn.colors;
        cs.normalColor      = bg;
        cs.highlightedColor = Color.Lerp(bg, Color.white, 0.20f);
        cs.pressedColor     = Color.Lerp(bg, Color.black, 0.25f);
        cs.selectedColor    = bg;
        cs.disabledColor    = new Color(bg.r * 0.4f, bg.g * 0.4f, bg.b * 0.4f, 0.6f);
        btn.colors          = cs;
        btn.targetGraphic   = img;
        btn.onClick.AddListener(onClick);

        // Label must be on a child — cannot have both Image and TMP on the same GO.
        var lblGO = new GameObject($"{name}_Lbl", typeof(RectTransform));
        lblGO.transform.SetParent(go.transform, false);
        var lblRT = lblGO.GetComponent<RectTransform>();
        lblRT.anchorMin = Vector2.zero; lblRT.anchorMax = Vector2.one;
        lblRT.offsetMin = new Vector2(6f, 0f); lblRT.offsetMax = new Vector2(-6f, 0f);

        var tmp = lblGO.AddComponent<TextMeshProUGUI>();
        tmp.text             = label;
        tmp.fontSize         = 13f;
        tmp.color            = ColWhite;
        tmp.fontStyle        = FontStyles.Bold;
        tmp.alignment        = TextAlignmentOptions.Center;
        tmp.textWrappingMode = TextWrappingModes.NoWrap;
        tmp.enableAutoSizing = true;
        tmp.fontSizeMin      = 8f;
        tmp.fontSizeMax      = 14f;

        return btn;
    }

    private static TextMeshProUGUI MakeCounterTile(GameObject parent, string name,
        string value, string sub, Color col)
    {
        // Tile root — Image background only.
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent.transform, false);
        go.GetComponent<Image>().color = BgCounter;

        var le = go.AddComponent<LayoutElement>();
        le.flexibleWidth = 1f;

        // Child GO for TMP — Image and TMP cannot coexist on the same GameObject.
        var textGO = new GameObject("Text", typeof(RectTransform));
        textGO.transform.SetParent(go.transform, false);

        var textRT = textGO.GetComponent<RectTransform>();
        textRT.anchorMin = Vector2.zero;
        textRT.anchorMax = Vector2.one;
        textRT.offsetMin = Vector2.zero;
        textRT.offsetMax = Vector2.zero;

        var tmp = textGO.AddComponent<TextMeshProUGUI>();
        tmp.text             = $"<size=130%><b>{value}</b></size>\n<size=70%><color=#99AABB>{sub}</color></size>";
        tmp.fontSize         = 18f;
        tmp.color            = col;
        tmp.alignment        = TextAlignmentOptions.Center;
        tmp.textWrappingMode = TextWrappingModes.NoWrap;

        return tmp;
    }

    private static TextMeshProUGUI MakeTMP(GameObject parent, string name, string text,
        float size, Color col, FontStyles style)
    {
        var go  = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent.transform, false);

        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text      = text;
        tmp.fontSize  = size;
        tmp.color     = col;
        tmp.fontStyle = style;
        tmp.alignment = TextAlignmentOptions.TopLeft;
        tmp.textWrappingMode = TextWrappingModes.NoWrap;
        tmp.overflowMode     = TextOverflowModes.Ellipsis;
        return tmp;
    }

    private static GameObject MakeImageChild(GameObject parent, string name, Color col)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent.transform, false);
        go.GetComponent<Image>().color = col;
        return go;
    }

    private static GameObject MakeHRow(GameObject parent, float height, float spacing)
    {
        var go = new GameObject("Row", typeof(RectTransform));
        go.transform.SetParent(parent.transform, false);
        var hl = go.AddComponent<HorizontalLayoutGroup>();
        hl.spacing                = spacing;
        hl.childControlWidth      = true;
        hl.childControlHeight     = true;
        hl.childForceExpandWidth  = true;
        hl.childForceExpandHeight = true;   // children fill the row height
        SetLayoutHeight(go, height);
        return go;
    }

    private static void MakeSeparator(GameObject parent)
    {
        var go = new GameObject("Sep", typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent.transform, false);
        go.GetComponent<Image>().color = ColSeparator;
        SetLayoutHeight(go, 1f);
    }

    private static void SetLayoutHeight(GameObject go, float h)
    {
        var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
        le.preferredHeight = h;
        le.minHeight       = h;
    }

    private static void AddContentSizeFitter(GameObject go)
    {
        var csf = go.AddComponent<ContentSizeFitter>();
        csf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        csf.verticalFit   = ContentSizeFitter.FitMode.PreferredSize;
    }

    private static void WireSlider(Slider s, float min, float max, float def,
        UnityEngine.Events.UnityAction<float> onChange)
    {
        if (s == null) return;
        s.minValue = min;
        s.maxValue = max;
        s.value    = def;
        s.onValueChanged.AddListener(onChange);
        onChange(def);
    }

    private void SetStatus(string msg)
    {
        if (txtStatus != null) txtStatus.text = msg;
    }

    // ─── Canvas Bootstrap ─────────────────────────────────────────────────────

    private static void EnsureEventSystem()
    {
        if (FindAnyObjectByType<EventSystem>() != null) return;
        var es = new GameObject("EventSystem");
        es.AddComponent<EventSystem>();
        var module = es.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
        module.AssignDefaultActions();
    }
}

/// <summary>
/// Patches the scene's InputSystemUIInputModule at startup so its pointer
/// and click actions are properly initialised. Runs before all other scripts
/// (execution order -200) so the EventSystem is ready before any UI is built.
/// </summary>
[UnityEngine.DefaultExecutionOrder(-200)]
public class SimUIInputModulePatcher : UnityEngine.MonoBehaviour
{
    private void Awake()
    {
        var module = UnityEngine.Object.FindAnyObjectByType<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
        if (module == null) return;
        module.AssignDefaultActions();
    }
}
