using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

/// <summary>
/// Dual-mode camera for the evacuation simulation.
///
/// FREE-FLY MODE (default):
///   WASD / Arrow Keys — lateral movement relative to camera heading
///   Q / E            — ascend / descend
///   Right Mouse Hold + drag — look around
///   Mouse scroll     — zoom (adjust move speed)
///   Shift held       — sprint multiplier
///
/// FOLLOW MODE (click an agent or press F):
///   Camera orbits the selected agent at a configurable offset.
///   Click anywhere on empty space or press Escape to exit follow mode.
///
/// Keys 1–5 switch between pre-set cinematic camera positions if configured.
/// </summary>
public class EvacSimCameraController : MonoBehaviour
{
    // ─── Inspector ────────────────────────────────────────────────────────────

    [Header("Free-Fly Settings")]
    [SerializeField] private float baseSpeed      = 15f;
    [SerializeField] private float sprintMultiplier = 3f;
    [SerializeField] private float lookSensitivity = 2f;
    [SerializeField] private float scrollSpeedMult = 2f;
    [SerializeField] private float smoothTime      = 0.08f;

    [Header("Follow Mode Settings")]
    [SerializeField] private float followDistance = 6f;
    [SerializeField] private float followHeight   = 3f;
    [SerializeField] private float followSmoothTime = 0.12f;

    [Header("Cinematic Presets (optional)")]
    [SerializeField] private Transform[] cameraPresets;

    // ─── Runtime ──────────────────────────────────────────────────────────────

    private bool       isFollowing;
    private AgentBrain followTarget;

    private float   yaw;
    private float   pitch;
    private Vector3 smoothVelocity;

    private float   dynamicSpeed;
    private bool    rightMouseHeld;

    private InputAction moveAction;
    private InputAction lookAction;
    private InputAction vertAction;
    private InputAction sprintAction;
    private InputAction followAction;
    private InputAction exitAction;

    // ─── Lifecycle ────────────────────────────────────────────────────────────

    private void Awake()
    {
        dynamicSpeed = baseSpeed;

        // WASD composite.
        moveAction = new InputAction(name: "CameraMove", type: InputActionType.Value, expectedControlType: "Vector2");
        moveAction.AddCompositeBinding("2DVector")
            .With("Up",    "<Keyboard>/w").With("Up",    "<Keyboard>/upArrow")
            .With("Down",  "<Keyboard>/s").With("Down",  "<Keyboard>/downArrow")
            .With("Left",  "<Keyboard>/a").With("Left",  "<Keyboard>/leftArrow")
            .With("Right", "<Keyboard>/d").With("Right", "<Keyboard>/rightArrow");

        // Mouse delta for look.
        lookAction = new InputAction(name: "CameraLook", type: InputActionType.Value, expectedControlType: "Vector2");
        lookAction.AddBinding("<Mouse>/delta");

        // Q/E vertical.
        vertAction = new InputAction(name: "CameraVert", type: InputActionType.Value, expectedControlType: "Axis");
        vertAction.AddCompositeBinding("1DAxis")
            .With("Negative", "<Keyboard>/q")
            .With("Positive", "<Keyboard>/e");

        sprintAction = new InputAction(name: "CameraSprint", binding: "<Keyboard>/leftShift");
        followAction = new InputAction(name: "CameraFollow", binding: "<Keyboard>/f");
        exitAction   = new InputAction(name: "CameraExit",   binding: "<Keyboard>/escape");

        followAction.performed += _ => TryFollowSelected();
        exitAction.performed   += _ => ExitFollowMode();
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
    }

    private void Start()
    {
        Vector3 ea = transform.eulerAngles;
        yaw   = ea.y;
        pitch = ea.x;
    }

    private void Update()
    {
        HandleScrollSpeed();
        HandlePresetKeys();
        HandleAgentClickSelection();

        if (isFollowing && followTarget != null && followTarget.gameObject.activeSelf)
            UpdateFollowMode();
        else
        {
            if (isFollowing) ExitFollowMode();
            UpdateFreeFlyCam();
        }
    }

    // ─── Free-Fly ─────────────────────────────────────────────────────────────

    private void UpdateFreeFlyCam()
    {
        // Look with right mouse button held.
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

        // Movement.
        Vector2 move  = moveAction.ReadValue<Vector2>();
        float   vert  = vertAction.ReadValue<float>();
        bool    sprint = sprintAction.ReadValue<float>() > 0.5f;
        float   speed  = dynamicSpeed * (sprint ? sprintMultiplier : 1f);

        Vector3 dir = transform.right * move.x
                    + transform.forward * move.y
                    + Vector3.up * vert;

        Vector3 target = transform.position + dir * (speed * Time.deltaTime);
        transform.position = Vector3.SmoothDamp(transform.position, target, ref smoothVelocity, smoothTime);
    }

    // ─── Follow Mode ──────────────────────────────────────────────────────────

    private void UpdateFollowMode()
    {
        // Right-mouse-drag orbits the camera around the agent.
        bool rmb = Mouse.current != null && Mouse.current.rightButton.isPressed;
        if (rmb)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible   = false;

            Vector2 look = lookAction.ReadValue<Vector2>();
            yaw   += look.x * lookSensitivity;
            pitch -= look.y * lookSensitivity;
            pitch  = Mathf.Clamp(pitch, -60f, 75f);
        }
        else
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible   = true;
        }

        Vector3 pivot  = followTarget.transform.position + Vector3.up * followHeight;
        Vector3 offset = Quaternion.Euler(pitch, yaw, 0f) * (Vector3.back * followDistance);
        Vector3 target = pivot + offset;

        transform.position = Vector3.SmoothDamp(transform.position, target, ref smoothVelocity, followSmoothTime);
        transform.LookAt(pivot);
    }

    private void TryFollowSelected()
    {
        // Toggle: if already following, exit.
        if (isFollowing) { ExitFollowMode(); return; }

        // Try to find an agent under the cursor.
        if (Camera.main == null) return;
        Ray ray = Camera.main.ScreenPointToRay(Mouse.current?.position.ReadValue() ?? Vector2.zero);
        if (Physics.Raycast(ray, out RaycastHit hit, 200f))
        {
            AgentBrain brain = hit.collider.GetComponentInParent<AgentBrain>();
            if (brain != null) StartFollowingAgent(brain);
        }
    }

    /// <summary>Begins following the given agent. Called by AgentPlayerController on possession.</summary>
    public void StartFollowingAgent(AgentBrain target)
    {
        followTarget = target;
        isFollowing  = true;
        Vector3 ea   = transform.eulerAngles;
        yaw   = ea.y;
        pitch = ea.x;
    }

    /// <summary>Exits follow mode. Called by AgentPlayerController on release.</summary>
    public void ExitFollowMode()
    {
        isFollowing  = false;
        followTarget = null;
    }

    // ─── Agent Click (dashboard inspection) ───────────────────────────────────

    private void HandleAgentClickSelection()
    {
        if (Mouse.current == null || !Mouse.current.leftButton.wasPressedThisFrame) return;

        // Do not consume the click when the pointer is over a UI element so that
        // buttons and sliders in the HUD receive their events correctly.
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;

        Camera cam = Camera.main;
        if (cam == null) return;

        Ray ray = cam.ScreenPointToRay(Mouse.current.position.ReadValue());
        if (!Physics.Raycast(ray, out RaycastHit hit, 200f)) return;

        AgentBrain agent = hit.collider.GetComponentInParent<AgentBrain>();
        if (agent != null) StartFollowingAgent(agent);
    }

    // ─── Preset Positions ─────────────────────────────────────────────────────

    private void HandlePresetKeys()
    {
        if (cameraPresets == null || cameraPresets.Length == 0) return;
        var kb = Keyboard.current;
        if (kb == null) return;

        for (int i = 0; i < Mathf.Min(cameraPresets.Length, 5); i++)
        {
            Key key = i switch
            {
                0 => Key.Digit1, 1 => Key.Digit2, 2 => Key.Digit3,
                3 => Key.Digit4, 4 => Key.Digit5, _ => Key.None
            };

            if (key != Key.None && kb[key].wasPressedThisFrame && cameraPresets[i] != null)
            {
                transform.SetPositionAndRotation(
                    cameraPresets[i].position,
                    cameraPresets[i].rotation);
                ExitFollowMode();
            }
        }
    }

    // ─── Scroll Speed ─────────────────────────────────────────────────────────

    private void HandleScrollSpeed()
    {
        if (Mouse.current == null) return;
        float scroll = Mouse.current.scroll.ReadValue().y;
        if (Mathf.Abs(scroll) < 0.01f) return;
        dynamicSpeed = Mathf.Clamp(dynamicSpeed + scroll * scrollSpeedMult * 0.01f, 1f, 100f);
    }
}
