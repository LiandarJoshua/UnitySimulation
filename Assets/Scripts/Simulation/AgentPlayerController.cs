using UnityEngine;
using UnityEngine.AI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

/// <summary>
/// Lets the player possess any agent by left-clicking on it during simulation.
///
/// POSSESSION:
///   Left-click an agent → camera switches to follow it; WASD overrides its
///   NavMeshAgent and drives movement directly.
///   Escape or right-click → releases the agent back to AI control.
///
/// MOVEMENT while possessed:
///   WASD / Arrow keys — move relative to the current camera yaw.
///   Left Shift        — sprint (2× speed).
///   The agent's NavMeshAgent is suspended (isStopped = true, velocity = 0)
///   and Warp() is used each frame to keep it on the NavMesh while player-driven.
///   On release, isStopped is reset so AI resumes normally.
///
/// ANIMATION:
///   The agent's Animator Speed float is driven with the same 0.08s damp as
///   AgentBrain.DriveAnimator(), keeping blending consistent.
///
/// The <see cref="EvacSimCameraController"/> follow-mode is activated/deactivated
/// automatically so the camera always tracks the possessed agent.
/// </summary>
public class AgentPlayerController : MonoBehaviour
{
    private const string AnimParamSpeed  = "Speed";
    private const float  AnimDampTime    = 0.08f;
    private const float  WalkSpeed       = 3f;
    private const float  SprintSpeed     = 5.5f;
    private const float  WarpSampleRange = 2f;

    // ─── Inspector ────────────────────────────────────────────────────────────

    [Tooltip("Camera controller used to enter follow mode on the possessed agent.")]
    [SerializeField] private EvacSimCameraController cameraController;

    // ─── Input ────────────────────────────────────────────────────────────────

    private InputAction moveAction;
    private InputAction sprintAction;
    private InputAction releaseAction;
    private InputAction clickAction;

    // ─── Runtime ──────────────────────────────────────────────────────────────

    private AgentBrain   possessedAgent;
    private NavMeshAgent possessedNav;
    private Animator     possessedAnimator;
    private bool         isPossessing;

    // ─── Lifecycle ────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (cameraController == null)
            cameraController = FindAnyObjectByType<EvacSimCameraController>();

        BuildInputActions();
    }

    private void OnEnable()
    {
        moveAction.Enable();
        sprintAction.Enable();
        releaseAction.Enable();
        clickAction.Enable();
    }

    private void OnDisable()
    {
        moveAction.Disable();
        sprintAction.Disable();
        releaseAction.Disable();
        clickAction.Disable();

        if (isPossessing) ReleaseAgent();
    }

    private void OnDestroy()
    {
        moveAction?.Dispose();
        sprintAction?.Dispose();
        releaseAction?.Dispose();
        clickAction?.Dispose();
    }

    private void Update()
    {
        if (!isPossessing)
        {
            HandleClickPossess();
        }
        else
        {
            DriveAgent();
        }
    }

    // ─── Possess / Release ────────────────────────────────────────────────────

    private void HandleClickPossess()
    {
        if (!clickAction.WasPressedThisFrame()) return;

        // Do not possess when clicking on a UI element.
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;

        Camera cam = Camera.main;
        if (cam == null) return;

        Vector2 mousePos = Mouse.current?.position.ReadValue() ?? Vector2.zero;
        Ray ray = cam.ScreenPointToRay(mousePos);

        if (!Physics.Raycast(ray, out RaycastHit hit, 300f)) return;

        AgentBrain brain = hit.collider.GetComponentInParent<AgentBrain>();
        if (brain == null) return;

        PossessAgent(brain);
    }

    /// <summary>Takes over the given agent, suspending its AI.</summary>
    public void PossessAgent(AgentBrain brain)
    {
        if (isPossessing) ReleaseAgent();

        possessedAgent    = brain;
        possessedNav      = brain.GetNavAgent();
        possessedAnimator = brain.GetComponentInChildren<Animator>();

        if (possessedNav != null)
        {
            possessedNav.isStopped = true;
            possessedNav.velocity  = Vector3.zero;
        }

        brain.SetPlayerControlled(true);

        // Tell the camera controller to follow this agent.
        cameraController?.StartFollowingAgent(brain);

        isPossessing = true;
        Debug.Log($"[AgentPlayerController] Possessing agent: {brain.name}");
    }

    /// <summary>Releases the agent back to AI control.</summary>
    public void ReleaseAgent()
    {
        if (possessedAgent != null)
            possessedAgent.SetPlayerControlled(false);

        if (possessedNav != null)
            possessedNav.isStopped = false;

        if (possessedAnimator != null)
            possessedAnimator.SetFloat(AnimParamSpeed, 0f);

        cameraController?.ExitFollowMode();

        possessedAgent    = null;
        possessedNav      = null;
        possessedAnimator = null;
        isPossessing      = false;

        Debug.Log("[AgentPlayerController] Agent released.");
    }

    // ─── Player-Driven Movement ───────────────────────────────────────────────

    private void DriveAgent()
    {
        // Escape releases — right mouse is now reserved for camera look.
        if (releaseAction.WasPressedThisFrame())
        {
            ReleaseAgent();
            return;
        }

        if (possessedNav == null || possessedAgent == null) { ReleaseAgent(); return; }

        Vector2 input      = moveAction.ReadValue<Vector2>();
        bool    isSprinting = sprintAction.ReadValue<float>() > 0.5f;
        float   speed       = isSprinting ? SprintSpeed : WalkSpeed;

        // Camera-relative movement — use the active sim camera yaw, not Camera.main,
        // since SimCamera (depth 0) is the rendering camera during character view.
        Transform camTransform = cameraController != null
            ? cameraController.transform
            : Camera.main?.transform;
        float camYaw = camTransform != null ? camTransform.eulerAngles.y : 0f;

        Vector3 inputDir = new Vector3(input.x, 0f, input.y).normalized;
        float   inputMag = Mathf.Clamp01(input.magnitude);

        Vector3 worldDir = Vector3.zero;
        if (inputMag > 0.01f)
            worldDir = Quaternion.Euler(0f, camYaw, 0f) * inputDir;

        Vector3 motion    = worldDir * (speed * inputMag);
        Vector3 newPos    = possessedNav.transform.position + motion * Time.deltaTime;

        // Warp keeps the agent on the NavMesh each frame.
        if (NavMesh.SamplePosition(newPos, out NavMeshHit hit, WarpSampleRange, NavMesh.AllAreas))
            possessedNav.Warp(hit.position);

        // Rotate agent toward movement direction.
        if (worldDir.sqrMagnitude > 0.001f)
        {
            Quaternion targetRot = Quaternion.LookRotation(worldDir, Vector3.up);
            possessedNav.transform.rotation = Quaternion.Slerp(
                possessedNav.transform.rotation, targetRot, 12f * Time.deltaTime);
        }

        // Drive animator.
        if (possessedAnimator != null)
            possessedAnimator.SetFloat(AnimParamSpeed, speed * inputMag, AnimDampTime, Time.deltaTime);
    }

    // ─── Input ────────────────────────────────────────────────────────────────

    private void BuildInputActions()
    {
        moveAction = new InputAction(name: "AgentMove", type: InputActionType.Value, expectedControlType: "Vector2");
        moveAction.AddCompositeBinding("2DVector")
            .With("Up",    "<Keyboard>/w")   .With("Up",    "<Keyboard>/upArrow")
            .With("Down",  "<Keyboard>/s")   .With("Down",  "<Keyboard>/downArrow")
            .With("Left",  "<Keyboard>/a")   .With("Left",  "<Keyboard>/leftArrow")
            .With("Right", "<Keyboard>/d")   .With("Right", "<Keyboard>/rightArrow");

        sprintAction  = new InputAction(name: "AgentSprint",  binding: "<Keyboard>/leftShift");
        releaseAction = new InputAction(name: "AgentRelease", binding: "<Keyboard>/escape");
        clickAction   = new InputAction(name: "AgentClick",   binding: "<Mouse>/leftButton");
    }
}
