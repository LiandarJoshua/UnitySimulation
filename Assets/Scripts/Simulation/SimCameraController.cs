using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Camera controller for the evacuation simulation.
///
/// Registers named camera slots and switches between them with keyboard keys 1–5.
/// Also supports free-fly navigation and agent follow-mode.
///
/// Camera slots (auto-discovered by GameObject name or assigned in Inspector):
///   1 — Overview       (CamOverview)
///   2 — Top-Down       (CamTopDown)
///   3 — Exit Watch     (CamExitWatch)
///   4 — Spawn Zone     (CamSpawnZone)
///   5 — Cinematic Pan  (CamCinematic)
///
/// Free-fly controls (active on the Overview camera when no slot camera exists):
///   WASD / Arrows — move
///   Q / E         — descend / ascend
///   Right-drag    — look
///   Shift         — sprint
///   Scroll        — adjust speed
///   F             — follow selected agent
///   Escape        — exit follow mode
///
/// Based on CameraRigController.cs from the reference project.
/// </summary>
public class SimCameraController : MonoBehaviour
{
    public static SimCameraController Instance { get; private set; }

    // ─── Inspector ────────────────────────────────────────────────────────────

    [Header("Camera Slots (auto-found by name if not assigned)")]
    [SerializeField] private Camera overviewCamera;
    [SerializeField] private Camera topDownCamera;
    [SerializeField] private Camera exitWatchCamera;
    [SerializeField] private Camera spawnZoneCamera;
    [SerializeField] private Camera cinematicCamera;

    [Header("Cinematic Pan")]
    [SerializeField] private Vector3 cinematicTarget = Vector3.zero;
    [SerializeField] private float   cinematicRadius = 30f;
    [SerializeField] private float   cinematicHeight = 18f;
    [SerializeField] private float   cinematicSpeed  = 7f;

    [Header("Free-Fly (used on Overview when no fixed cameras)")]
    [SerializeField] private float baseSpeed       = 15f;
    [SerializeField] private float sprintMult      = 3f;
    [SerializeField] private float lookSensitivity = 2f;
    [SerializeField] private float scrollSpeedMult = 2f;
    [SerializeField] private float smoothTime      = 0.08f;

    [Header("Follow Mode")]
    [SerializeField] private float followDistance = 7f;
    [SerializeField] private float followHeight   = 3.5f;
    [SerializeField] private float followSmooth   = 0.12f;

    [Header("UI (auto-found by name if not assigned)")]
    [SerializeField] private TextMeshProUGUI cameraLabel;

    // ─── Slots ────────────────────────────────────────────────────────────────

    private struct CameraSlot
    {
        public Camera Camera;
        public string Label;
    }

    private readonly List<CameraSlot> slots = new List<CameraSlot>();
    private int   activeIndex;
    private float cinematicAngle;

    // Keys 1–5 map to slot indices 0–4.
    private static readonly Key[] SwitchKeys =
        { Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4, Key.Digit5 };

    // ─── Free-Fly ────────────────────────────────────────────────────────────

    private float   yaw, pitch;
    private float   dynamicSpeed;
    private Vector3 smoothVelocity;
    private bool    rightMouseHeld;

    private InputAction moveAction;
    private InputAction lookAction;
    private InputAction vertAction;
    private InputAction sprintAction;
    private InputAction followAction;
    private InputAction exitAction;

    // ─── Follow Mode ─────────────────────────────────────────────────────────

    private bool       isFollowing;
    private AgentBrain followTarget;

    // ─── Lifecycle ────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;

        dynamicSpeed = baseSpeed;
        BuildInputActions();
        AutoFindCameras();
        BuildSlots();
        AutoFindUI();
    }

    private void Start()
    {
        // Disable legacy Main Camera so our cameras take over (keep AudioListener).
        var mainCam = Camera.main;
        if (mainCam != null && !IsOneOfOurCameras(mainCam))
            mainCam.gameObject.SetActive(false);

        ActivateSlot(0, true);
        for (int i = 1; i < slots.Count; i++) ActivateSlot(i, false);

        activeIndex    = 0;
        cinematicAngle = 0f;

        Vector3 ea = transform.eulerAngles;
        yaw   = ea.y;
        pitch = ea.x;

        UpdateLabel();
    }

    private void OnEnable()
    {
        moveAction.Enable(); lookAction.Enable(); vertAction.Enable();
        sprintAction.Enable(); followAction.Enable(); exitAction.Enable();
    }

    private void OnDisable()
    {
        moveAction.Disable(); lookAction.Disable(); vertAction.Disable();
        sprintAction.Disable(); followAction.Disable(); exitAction.Disable();
    }

    private void OnDestroy()
    {
        moveAction?.Dispose(); lookAction?.Dispose(); vertAction?.Dispose();
        sprintAction?.Dispose(); followAction?.Dispose(); exitAction?.Dispose();

        // Restore main camera.
        var mc = GameObject.FindWithTag("MainCamera");
        if (mc != null) mc.SetActive(true);
    }

    private void Update()
    {
        HandleKeyboardSwitch();
        AnimateCinematic();
        HandleScrollSpeed();
        HandleAgentClick();

        // Free-fly only applies when the overview/current camera is the active one
        // and it's this transform (i.e. not a fixed scene camera).
        bool freeFlyActive = slots.Count == 0
                          || (slots[activeIndex].Camera != null
                              && slots[activeIndex].Camera.transform == transform);

        if (freeFlyActive)
        {
            if (isFollowing && followTarget != null && followTarget.gameObject.activeSelf)
                UpdateFollowMode();
            else
            {
                if (isFollowing) ExitFollowMode();
                UpdateFreeFlyCam();
            }
        }
    }

    // ─── Public API ───────────────────────────────────────────────────────────

    /// <summary>Switches to camera slot at <paramref name="index"/> (0-based).</summary>
    public void SwitchTo(int index)
    {
        if (index < 0 || index >= slots.Count || index == activeIndex) return;
        ActivateSlot(activeIndex, false);
        activeIndex = index;
        ActivateSlot(activeIndex, true);
        UpdateLabel();
    }

    /// <summary>Cycles to the next camera slot.</summary>
    public void CycleNext() => SwitchTo((activeIndex + 1) % slots.Count);

    /// <summary>Cycles to the previous camera slot.</summary>
    public void CyclePrev() => SwitchTo((activeIndex - 1 + slots.Count) % slots.Count);

    // ─── Keyboard ─────────────────────────────────────────────────────────────

    private void HandleKeyboardSwitch()
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

    // ─── Cinematic ────────────────────────────────────────────────────────────

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

    // ─── Free-Fly ─────────────────────────────────────────────────────────────

    private void UpdateFreeFlyCam()
    {
        rightMouseHeld = Mouse.current != null && Mouse.current.rightButton.isPressed;

        if (rightMouseHeld)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible   = false;

            Vector2 look = lookAction.ReadValue<Vector2>();
            yaw   += look.x * lookSensitivity;
            pitch -= look.y * lookSensitivity;
            pitch  = Mathf.Clamp(pitch, -80f, 80f);
            transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
        }
        else
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible   = true;
        }

        Vector2 move  = moveAction.ReadValue<Vector2>();
        float   vert  = vertAction.ReadValue<float>();
        bool    sprint = sprintAction.ReadValue<float>() > 0.5f;
        float   speed  = dynamicSpeed * (sprint ? sprintMult : 1f);

        Vector3 dir = transform.right * move.x + transform.forward * move.y + Vector3.up * vert;
        Vector3 tgt = transform.position + dir * (speed * Time.deltaTime);
        transform.position = Vector3.SmoothDamp(transform.position, tgt, ref smoothVelocity, smoothTime);
    }

    private void HandleScrollSpeed()
    {
        if (Mouse.current == null) return;
        float scroll = Mouse.current.scroll.ReadValue().y;
        if (Mathf.Abs(scroll) < 0.01f) return;
        dynamicSpeed = Mathf.Clamp(dynamicSpeed + scroll * scrollSpeedMult * 0.01f, 1f, 100f);
    }

    // ─── Follow Mode ──────────────────────────────────────────────────────────

    private void UpdateFollowMode()
    {
        Vector3 pivot  = followTarget.transform.position + Vector3.up * followHeight;
        Vector3 offset = Quaternion.Euler(20f, yaw, 0f) * (Vector3.back * followDistance);
        transform.position = Vector3.SmoothDamp(transform.position, pivot + offset, ref smoothVelocity, followSmooth);
        transform.LookAt(pivot);
    }

    private void HandleAgentClick()
    {
        var mouse = Mouse.current;
        if (mouse == null || !mouse.leftButton.wasPressedThisFrame) return;

        Camera cam = slots.Count > 0 ? slots[activeIndex].Camera : Camera.main;
        if (cam == null) return;

        Ray ray = cam.ScreenPointToRay(mouse.position.ReadValue());
        if (!Physics.Raycast(ray, out RaycastHit hit, 300f)) return;

        AgentBrain agent = hit.collider.GetComponentInParent<AgentBrain>();
        if (agent != null)
        {
            followTarget = agent;
            isFollowing  = true;
            Vector3 ea = transform.eulerAngles;
            yaw   = ea.y;
            pitch = ea.x;
        }
    }

    private void ExitFollowMode()
    {
        isFollowing  = false;
        followTarget = null;
    }

    // ─── Slot Management ──────────────────────────────────────────────────────

    private void ActivateSlot(int index, bool active)
    {
        if (index < 0 || index >= slots.Count) return;
        var cam = slots[index].Camera;
        if (cam != null) cam.gameObject.SetActive(active);
    }

    private void BuildSlots()
    {
        slots.Clear();
        AddSlot(overviewCamera,   "1 — Overview");
        AddSlot(topDownCamera,    "2 — Top-Down");
        AddSlot(exitWatchCamera,  "3 — Exit Watch");
        AddSlot(spawnZoneCamera,  "4 — Spawn Zone");
        AddSlot(cinematicCamera,  "5 — Cinematic Pan");
    }

    private void AddSlot(Camera cam, string label)
    {
        if (cam == null) return;
        slots.Add(new CameraSlot { Camera = cam, Label = label });
    }

    private void UpdateLabel()
    {
        if (cameraLabel == null || slots.Count == 0) return;
        cameraLabel.text = $"CAM  {slots[activeIndex].Label}  [{activeIndex + 1}/{slots.Count}]";
    }

    // ─── Auto-Discovery ───────────────────────────────────────────────────────

    private void AutoFindCameras()
    {
        overviewCamera  ??= FindCam("CamOverview");
        topDownCamera   ??= FindCam("CamTopDown");
        exitWatchCamera ??= FindCam("CamExitWatch");
        spawnZoneCamera ??= FindCam("CamSpawnZone");
        cinematicCamera ??= FindCam("CamCinematic");
    }

    private void AutoFindUI()
    {
        if (cameraLabel == null)
        {
            var go = GameObject.Find("CameraLabel");
            if (go != null) cameraLabel = go.GetComponent<TextMeshProUGUI>();
        }
    }

    private static Camera FindCam(string n)
    {
        var go = GameObject.Find(n);
        return go != null ? go.GetComponent<Camera>() : null;
    }

    private bool IsOneOfOurCameras(Camera cam)
    {
        return cam == overviewCamera || cam == topDownCamera
            || cam == exitWatchCamera || cam == spawnZoneCamera
            || cam == cinematicCamera;
    }

    // ─── Input Actions ────────────────────────────────────────────────────────

    private void BuildInputActions()
    {
        moveAction = new InputAction("CamMove", InputActionType.Value, expectedControlType: "Vector2");
        moveAction.AddCompositeBinding("2DVector")
            .With("Up",    "<Keyboard>/w").With("Up",    "<Keyboard>/upArrow")
            .With("Down",  "<Keyboard>/s").With("Down",  "<Keyboard>/downArrow")
            .With("Left",  "<Keyboard>/a").With("Left",  "<Keyboard>/leftArrow")
            .With("Right", "<Keyboard>/d").With("Right", "<Keyboard>/rightArrow");

        lookAction = new InputAction("CamLook", InputActionType.Value, expectedControlType: "Vector2");
        lookAction.AddBinding("<Mouse>/delta");

        vertAction = new InputAction("CamVert", InputActionType.Value, expectedControlType: "Axis");
        vertAction.AddCompositeBinding("1DAxis")
            .With("Negative", "<Keyboard>/q")
            .With("Positive", "<Keyboard>/e");

        sprintAction = new InputAction("CamSprint", binding: "<Keyboard>/leftShift");
        followAction = new InputAction("CamFollow", binding: "<Keyboard>/f");
        exitAction   = new InputAction("CamExit",   binding: "<Keyboard>/escape");

        followAction.performed += _ =>
        {
            if (isFollowing) ExitFollowMode();
        };
        exitAction.performed += _ => ExitFollowMode();
    }
}
