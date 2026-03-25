using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Camera rig that manages five named camera slots and switches between them.
///
/// Camera slots (auto-discovered by name if not assigned in the Inspector):
///   0 — Overview       (CamOverview)
///   1 — Top-Down       (CamTopDown)
///   2 — Exit Watch     (CamExitWatch)
///   3 — Spawn Zone     (CamSpawnZone)
///   4 — Cinematic Pan  (CamCinematic)
///
/// Switching:
///   Keyboard: keys 1–5 switch to the corresponding slot.
///   UI:       <see cref="CycleNext"/> / <see cref="CyclePrev"/> wired to BtnCameraNext / BtnCameraPrev.
///   Code:     Call <see cref="SwitchTo(int)"/>.
///
/// The Cinematic Pan camera orbits a configurable target using sine/cosine each frame.
/// Camera.main is disabled on Start so only rig cameras are rendered.
/// </summary>
public class SimCameraRigController : MonoBehaviour
{
    public static SimCameraRigController Instance { get; private set; }

    // ─── Inspector ────────────────────────────────────────────────────────────

    [Header("Camera Slots (auto-found by name if null)")]
    [SerializeField] private Camera overviewCamera;
    [SerializeField] private Camera topDownCamera;
    [SerializeField] private Camera exitWatchCamera;
    [SerializeField] private Camera spawnZoneCamera;
    [SerializeField] private Camera cinematicCamera;

    [Header("Cinematic Pan Settings")]
    [Tooltip("World-space point the cinematic camera orbits around.")]
    [SerializeField] private Vector3 cinematicTarget = Vector3.zero;

    [Tooltip("Orbit radius from the target (m).")]
    [SerializeField] private float cinematicRadius = 28f;

    [Tooltip("Camera height above the target during orbit (m).")]
    [SerializeField] private float cinematicHeight = 14f;

    [Tooltip("Orbit speed (degrees per second).")]
    [SerializeField] private float cinematicSpeed = 8f;

    [Header("UI (auto-found by name if null)")]
    [SerializeField] private Button          nextButton;
    [SerializeField] private Button          prevButton;
    [SerializeField] private TextMeshProUGUI cameraLabel;

    // ─── Inner type ───────────────────────────────────────────────────────────

    private struct CameraSlot
    {
        public Camera Camera;
        public string Label;
        public string Icon;
    }

    // ─── State ────────────────────────────────────────────────────────────────

    private readonly List<CameraSlot> slots = new List<CameraSlot>();
    private int   activeIndex;
    private float cinematicAngle;

    private static readonly Key[] SwitchKeys =
        { Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4, Key.Digit5 };

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

        // Disable Camera.main so only rig cameras are active.
        Camera mainCam = Camera.main;
        if (mainCam != null && !IsRigCamera(mainCam))
            mainCam.gameObject.SetActive(false);

        for (int i = 0; i < slots.Count; i++)
            SetCameraActive(i, i == 0);

        activeIndex    = 0;
        cinematicAngle = 0f;
        UpdateLabel();
    }

    private void OnDestroy()
    {
        // Restore Camera.main when the rig is removed (e.g., on stop-play-mode).
        GameObject mc = GameObject.FindWithTag("MainCamera");
        if (mc != null) mc.SetActive(true);
    }

    private void Update()
    {
        HandleKeyboardSwitch();
        AnimateCinematic();
    }

    // ─── Public API ───────────────────────────────────────────────────────────

    /// <summary>Switches to the camera slot at <paramref name="index"/> (0-based).</summary>
    public void SwitchTo(int index)
    {
        if (index < 0 || index >= slots.Count || index == activeIndex) return;
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

    // ─── Keyboard ─────────────────────────────────────────────────────────────

    private void HandleKeyboardSwitch()
    {
        Keyboard kb = Keyboard.current;
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

    // ─── Cinematic Pan ────────────────────────────────────────────────────────

    private void AnimateCinematic()
    {
        if (cinematicCamera == null || !cinematicCamera.gameObject.activeSelf) return;

        cinematicAngle += cinematicSpeed * Time.deltaTime;
        if (cinematicAngle >= 360f) cinematicAngle -= 360f;

        float   rad = cinematicAngle * Mathf.Deg2Rad;
        Vector3 pos = cinematicTarget + new Vector3(
            Mathf.Sin(rad) * cinematicRadius,
            cinematicHeight,
            Mathf.Cos(rad) * cinematicRadius);

        cinematicCamera.transform.position = pos;
        cinematicCamera.transform.LookAt(cinematicTarget + Vector3.up * 2f);
    }

    // ─── Slot Management ──────────────────────────────────────────────────────

    private void SetCameraActive(int index, bool active)
    {
        if (index < 0 || index >= slots.Count) return;
        Camera cam = slots[index].Camera;
        if (cam != null) cam.gameObject.SetActive(active);
    }

    private void BuildSlots()
    {
        slots.Clear();
        AddSlot(overviewCamera,   "Overview",           "◉");
        AddSlot(topDownCamera,    "Top-Down Tactical",  "⊞");
        AddSlot(exitWatchCamera,  "Exit Watch",         "⛶");
        AddSlot(spawnZoneCamera,  "Spawn Zone",         "▣");
        AddSlot(cinematicCamera,  "Cinematic Pan",      "⟳");
    }

    private void AddSlot(Camera cam, string label, string icon)
    {
        if (cam == null) return;
        slots.Add(new CameraSlot { Camera = cam, Label = label, Icon = icon });
    }

    private void UpdateLabel()
    {
        if (cameraLabel == null || slots.Count == 0) return;
        CameraSlot slot = slots[activeIndex];
        cameraLabel.text =
            $"{slot.Icon}  <b>{slot.Label}</b>  <size=75%>[{activeIndex + 1}/{slots.Count}]</size>";
    }

    // ─── Auto-Discovery ───────────────────────────────────────────────────────

    private void AutoFindCameras()
    {
        overviewCamera  ??= FindCamByName("CamOverview");
        topDownCamera   ??= FindCamByName("CamTopDown");
        exitWatchCamera ??= FindCamByName("CamExitWatch");
        spawnZoneCamera ??= FindCamByName("CamSpawnZone");
        cinematicCamera ??= FindCamByName("CamCinematic");
    }

    private void AutoFindUI()
    {
        if (nextButton  == null) nextButton  = FindButtonByName("BtnCameraNext");
        if (prevButton  == null) prevButton  = FindButtonByName("BtnCameraPrev");
        if (cameraLabel == null) cameraLabel = FindTMPByName("CameraLabel");
    }

    private static Camera FindCamByName(string n)
    {
        GameObject go = GameObject.Find(n);
        return go != null ? go.GetComponent<Camera>() : null;
    }

    private static Button FindButtonByName(string n)
    {
        GameObject go = GameObject.Find(n);
        return go != null ? go.GetComponent<Button>() : null;
    }

    private static TextMeshProUGUI FindTMPByName(string n)
    {
        GameObject go = GameObject.Find(n);
        return go != null ? go.GetComponent<TextMeshProUGUI>() : null;
    }

    private bool IsRigCamera(Camera cam) =>
        cam == overviewCamera || cam == topDownCamera ||
        cam == exitWatchCamera || cam == spawnZoneCamera ||
        cam == cinematicCamera;
}
