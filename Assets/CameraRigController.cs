using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Manages all simulation-overview cameras and switches between them at runtime.
///
/// Registered cameras (set via Inspector or discovered by tag/name):
///   Slot 0 – Overview          : elevated wide angle (replaces raw Main Camera)
///   Slot 1 – Top-Down Tactical : orthographic bird's-eye
///   Slot 2 – Exit Watch        : tight fixed shot on the muster/exit point
///   Slot 3 – Spawn Zone        : wide angle covering the spawn area
///   Slot 4 – Cinematic Pan     : automated slow orbit around the scene centre
///
/// Switching:
///   Keyboard: 1–5 switches to camera index 0–4.
///   UI:       BtnCameraNext / BtnCameraPrev buttons (found by name in the Canvas).
///   Code:     Call SwitchTo(int index) or CycleNext() / CyclePrev().
///
/// The ObserverCamera is left untouched; ObserverModeController manages it.
/// </summary>
public class CameraRigController : MonoBehaviour
{
    public static CameraRigController Instance { get; private set; }

    // ─── Serialized ───────────────────────────────────────────────────────────

    [Header("Camera Slots (assign in Inspector or auto-found by name)")]
    [SerializeField] private Camera overviewCamera;
    [SerializeField] private Camera topDownCamera;
    [SerializeField] private Camera exitWatchCamera;
    [SerializeField] private Camera spawnZoneCamera;
    [SerializeField] private Camera cinematicCamera;

    [Header("Cinematic Pan Settings")]
    [Tooltip("World-space point the cinematic camera orbits around.")]
    [SerializeField] private Vector3 cinematicTarget = Vector3.zero;

    [Tooltip("Orbit radius from the target.")]
    [SerializeField] private float cinematicRadius = 28f;

    [Tooltip("Camera height above the target during the cinematic orbit.")]
    [SerializeField] private float cinematicHeight = 14f;

    [Tooltip("Orbit speed in degrees per second.")]
    [SerializeField] private float cinematicSpeed = 8f;

    [Header("UI (auto-found by name if null)")]
    [SerializeField] private Button            nextButton;
    [SerializeField] private Button            prevButton;
    [SerializeField] private TextMeshProUGUI   cameraLabel;
    [SerializeField] private Image             cameraLabelBg;

    // ─── Internal ─────────────────────────────────────────────────────────────

    private readonly List<CameraSlot> slots = new List<CameraSlot>();
    private int   activeIndex;
    private float cinematicAngle;

    // Key bindings: keys 1–5 map to camera indices 0–4
    private static readonly Key[] SwitchKeys =
    {
        Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4, Key.Digit5,
    };

    // ─── Inner Type ───────────────────────────────────────────────────────────

    private struct CameraSlot
    {
        public Camera      Camera;
        public string      Label;
        public string      Icon;  // emoji/symbol for the label badge
    }

    // ─── Lifecycle ────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;

        AutoFindCameras();
        AutoFindUI();
        BuildSlots();
    }

    private void Start()
    {
        if (nextButton != null) nextButton.onClick.AddListener(CycleNext);
        if (prevButton != null) prevButton.onClick.AddListener(CyclePrev);

        // Disable the legacy Main Camera so the rig cameras take over.
        // Audio Listener stays on Main Camera — it is just visually disabled.
        var mainCam = Camera.main;
        if (mainCam != null)
            mainCam.gameObject.SetActive(false);

        // Activate the first slot; disable the rest.
        for (int i = 0; i < slots.Count; i++)
            SetCameraActive(i, i == 0);

        activeIndex    = 0;
        cinematicAngle = 0f;
        UpdateLabel();
    }

    private void OnDestroy()
    {
        // Restore Main Camera when the rig is removed (e.g., in Edit mode play/stop cycles).
        var mainCam = GameObject.FindWithTag("MainCamera");
        if (mainCam != null) mainCam.SetActive(true);
    }

    private void Update()
    {
        HandleKeyboardInput();
        AnimateCinematic();
    }

    // ─── Public API ───────────────────────────────────────────────────────────

    /// <summary>Switches to the camera at the given index (0-based). Ignored if out of range.</summary>
    public void SwitchTo(int index)
    {
        if (index < 0 || index >= slots.Count) return;
        if (index == activeIndex) return;

        SetCameraActive(activeIndex, false);
        activeIndex = index;
        SetCameraActive(activeIndex, true);
        UpdateLabel();
    }

    /// <summary>Cycles to the next camera slot, wrapping around.</summary>
    public void CycleNext() => SwitchTo((activeIndex + 1) % slots.Count);

    /// <summary>Cycles to the previous camera slot, wrapping around.</summary>
    public void CyclePrev() => SwitchTo((activeIndex - 1 + slots.Count) % slots.Count);

    /// <summary>Returns the currently active camera.</summary>
    public Camera ActiveCamera => slots.Count > 0 ? slots[activeIndex].Camera : null;

    // ─── Keyboard Input ───────────────────────────────────────────────────────

    private void HandleKeyboardInput()
    {
        var kb = Keyboard.current;
        if (kb == null) return;

        for (int i = 0; i < SwitchKeys.Length && i < slots.Count; i++)
        {
            if (kb[SwitchKeys[i]].wasPressedThisFrame)
            {
                SwitchTo(i);
                return;
            }
        }
    }

    // ─── Cinematic Animation ──────────────────────────────────────────────────

    private void AnimateCinematic()
    {
        if (cinematicCamera == null || !cinematicCamera.gameObject.activeSelf) return;

        cinematicAngle += cinematicSpeed * Time.deltaTime;
        if (cinematicAngle >= 360f) cinematicAngle -= 360f;

        float rad = cinematicAngle * Mathf.Deg2Rad;
        Vector3 pos = cinematicTarget + new Vector3(
            Mathf.Sin(rad) * cinematicRadius,
            cinematicHeight,
            Mathf.Cos(rad) * cinematicRadius);

        cinematicCamera.transform.position = pos;
        cinematicCamera.transform.LookAt(cinematicTarget + Vector3.up * 2f);
    }

    // ─── Camera Enable / Disable ──────────────────────────────────────────────

    private void SetCameraActive(int index, bool active)
    {
        if (index < 0 || index >= slots.Count) return;
        var cam = slots[index].Camera;
        if (cam != null)
            cam.gameObject.SetActive(active);
    }

    // ─── Label ────────────────────────────────────────────────────────────────

    private void UpdateLabel()
    {
        if (cameraLabel == null || slots.Count == 0) return;
        var slot = slots[activeIndex];
        cameraLabel.text = $"{slot.Icon}  <b>{slot.Label}</b>  <size=75%>[{activeIndex + 1}/{slots.Count}]</size>";

        if (cameraLabelBg != null)
            cameraLabelBg.color = new Color(0f, 0f, 0f, 0.55f);
    }

    // ─── Slot Building ────────────────────────────────────────────────────────

    private void BuildSlots()
    {
        slots.Clear();

        AddSlot(overviewCamera,    "Overview",           "◉");
        AddSlot(topDownCamera,     "Top-Down Tactical",  "⊞");
        AddSlot(exitWatchCamera,   "Exit Watch",         "⛶");
        AddSlot(spawnZoneCamera,   "Spawn Zone",         "▣");
        AddSlot(cinematicCamera,   "Cinematic Pan",      "⟳");
    }

    private void AddSlot(Camera cam, string label, string icon)
    {
        if (cam == null) return;
        slots.Add(new CameraSlot { Camera = cam, Label = label, Icon = icon });
    }

    // ─── Auto-Discovery ───────────────────────────────────────────────────────

    private void AutoFindCameras()
    {
        overviewCamera   ??= FindCamByName("CamOverview");
        topDownCamera    ??= FindCamByName("CamTopDown");
        exitWatchCamera  ??= FindCamByName("CamExitWatch");
        spawnZoneCamera  ??= FindCamByName("CamSpawnZone");
        cinematicCamera  ??= FindCamByName("CamCinematic");
    }

    private void AutoFindUI()
    {
        if (nextButton   == null) nextButton   = FindButtonInScene("BtnCameraNext");
        if (prevButton   == null) prevButton   = FindButtonInScene("BtnCameraPrev");
        if (cameraLabel  == null) cameraLabel  = FindTMPInScene("CameraLabel");
        if (cameraLabelBg == null) cameraLabelBg = FindImageInScene("CameraLabelBg");
    }

    private static Camera FindCamByName(string n)
    {
        var go = GameObject.Find(n);
        return go != null ? go.GetComponent<Camera>() : null;
    }

    private static Button FindButtonInScene(string n)
    {
        var go = GameObject.Find(n);
        return go != null ? go.GetComponent<Button>() : null;
    }

    private static TextMeshProUGUI FindTMPInScene(string n)
    {
        var go = GameObject.Find(n);
        return go != null ? go.GetComponent<TextMeshProUGUI>() : null;
    }

    private static Image FindImageInScene(string n)
    {
        var go = GameObject.Find(n);
        return go != null ? go.GetComponent<Image>() : null;
    }
}
