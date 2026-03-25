using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Drives the observer character's movement via WASD (or left stick) and a CharacterController.
///
/// Responsibilities:
///   - Read movement input using the new Input System (in-code InputActions, no asset editing).
///   - Move the CharacterController horizontally relative to the camera's yaw.
///   - Apply gravity so the character stays grounded.
///   - Drive the Animator Speed float for walk/idle/run blending (matches the existing
///     WorkerAnimatorController parameter used by simulation agents).
///
/// This script never reads from or writes to any simulation state.
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class ObserverCharacterController : MonoBehaviour
{
    // ─── Constants ────────────────────────────────────────────────────────────

    private const string AnimParamSpeed = "Speed";

    // ─── Serialized ───────────────────────────────────────────────────────────

    [Header("Movement")]
    [Tooltip("Normal walk speed in units/s.")]
    [SerializeField] private float walkSpeed = 4f;

    [Tooltip("Sprint speed multiplier applied while Shift is held.")]
    [SerializeField] private float sprintMultiplier = 2f;

    [Tooltip("How quickly the character turns to face movement direction.")]
    [SerializeField] private float turnSmoothTime = 0.1f;

    [Header("Physics")]
    [Tooltip("Downward gravity acceleration in units/s².")]
    [SerializeField] private float gravity = -18f;

    [Header("References")]
    [Tooltip("The follow camera — used to make movement camera-relative. Auto-found if null.")]
    [SerializeField] private Camera followCamera;

    [Tooltip("Animator on the character mesh. Auto-found in children if null.")]
    [SerializeField] private Animator animator;

    // ─── Runtime ──────────────────────────────────────────────────────────────

    private CharacterController characterController;
    private InputAction         moveAction;
    private InputAction         sprintAction;

    private float verticalVelocity;
    private float turnVelocity;

    // ─── Unity Lifecycle ──────────────────────────────────────────────────────

    private void Awake()
    {
        characterController = GetComponent<CharacterController>();

        if (animator == null)
            animator = GetComponentInChildren<Animator>();

        if (followCamera == null)
            followCamera = Camera.main;

        // Build input actions in code — no InputActionAsset editing.
        moveAction   = new InputAction(name: "ObserverMove",
                           type: InputActionType.Value,
                           binding: "<Keyboard>/w",
                           expectedControlType: "Vector2");

        // Composite WASD binding
        moveAction = new InputAction(name: "ObserverMove", type: InputActionType.Value, expectedControlType: "Vector2");
        moveAction.AddCompositeBinding("2DVector")
            .With("Up",    "<Keyboard>/w")
            .With("Down",  "<Keyboard>/s")
            .With("Left",  "<Keyboard>/a")
            .With("Right", "<Keyboard>/d");

        // Gamepad left stick as an additional binding
        moveAction.AddBinding("<Gamepad>/leftStick");

        sprintAction = new InputAction(name: "ObserverSprint", binding: "<Keyboard>/leftShift");
        sprintAction.AddBinding("<Keyboard>/rightShift");
    }

    private void OnEnable()
    {
        moveAction.Enable();
        sprintAction.Enable();
    }

    private void OnDisable()
    {
        moveAction.Disable();
        sprintAction.Disable();

        // Zero out animator speed so the character returns to idle when mode is exited.
        if (animator != null)
            animator.SetFloat(AnimParamSpeed, 0f);
    }

    private void OnDestroy()
    {
        moveAction?.Dispose();
        sprintAction?.Dispose();
    }

    private void Update()
    {
        ApplyGravity();
        HandleMovement();
    }

    // ─── Movement ─────────────────────────────────────────────────────────────

    private void HandleMovement()
    {
        Vector2 input = moveAction.ReadValue<Vector2>();
        bool    isSprinting = sprintAction.ReadValue<float>() > 0.5f;

        float speed = walkSpeed * (isSprinting ? sprintMultiplier : 1f);
        float inputMagnitude = Mathf.Clamp01(input.magnitude);

        // Translate input to world-space direction relative to camera yaw only.
        Vector3 moveDir = Vector3.zero;
        if (inputMagnitude > 0.01f)
        {
            float   camYaw    = followCamera != null ? followCamera.transform.eulerAngles.y : 0f;
            Vector3 inputDir  = new Vector3(input.x, 0f, input.y).normalized;
            float   targetYaw = Mathf.Atan2(inputDir.x, inputDir.z) * Mathf.Rad2Deg + camYaw;

            // Smooth-turn the character body toward the movement direction.
            float smoothedYaw = Mathf.SmoothDampAngle(
                transform.eulerAngles.y, targetYaw, ref turnVelocity, turnSmoothTime);
            transform.rotation = Quaternion.Euler(0f, smoothedYaw, 0f);

            moveDir = Quaternion.Euler(0f, targetYaw, 0f) * Vector3.forward;
        }

        // Combine horizontal movement with vertical velocity (gravity).
        Vector3 motion = moveDir * (speed * inputMagnitude) + Vector3.up * verticalVelocity;
        characterController.Move(motion * Time.deltaTime);

        // Drive animator — use actual horizontal speed so animation matches movement.
        if (animator != null)
        {
            float animSpeed = inputMagnitude * speed;
            animator.SetFloat(AnimParamSpeed, animSpeed, 0.08f, Time.deltaTime);
        }
    }

    private void ApplyGravity()
    {
        if (characterController.isGrounded && verticalVelocity < 0f)
            verticalVelocity = -2f; // Small negative value keeps the CC grounded-check reliable.
        else
            verticalVelocity += gravity * Time.deltaTime;
    }
}
