using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Drives the observer character's movement using the new Input System
/// (InputActions created entirely in code — no InputActionAsset editing required).
///
/// Features:
///   - WASD composite binding for keyboard + left-stick for gamepad.
///   - Sprint (Left/Right Shift or gamepad left-shoulder).
///   - Camera-relative movement: only the camera's yaw is used so the character
///     stays upright regardless of camera pitch.
///   - Smooth-damped rotation toward the movement direction.
///   - Manual gravity applied via CharacterController.
///   - Animator "Speed" float driven with 0.08s damp — same as agent locomotion,
///     ensuring consistent blending with the <c>WorkerAnimatorController</c>.
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class SimObserverCharacterController : MonoBehaviour
{
    private const string AnimParamSpeed = "Speed";
    private const float  AnimDampTime   = 0.08f;

    // ─── Inspector ────────────────────────────────────────────────────────────

    [Header("Movement")]
    [Tooltip("Normal walk speed (m/s).")]
    [SerializeField] private float walkSpeed = 4f;

    [Tooltip("Sprint speed multiplier applied while Shift is held.")]
    [SerializeField] private float sprintMultiplier = 2f;

    [Tooltip("Smooth-damp time for body rotation toward movement direction (s).")]
    [SerializeField] private float turnSmoothTime = 0.1f;

    [Header("Physics")]
    [Tooltip("Gravity acceleration (m/s²). Use a negative value.")]
    [SerializeField] private float gravity = -18f;

    [Header("References")]
    [Tooltip("Follow camera whose yaw is used to make movement camera-relative. Auto-resolved if null.")]
    [SerializeField] private Camera followCamera;

    [Tooltip("Animator on the character mesh. Auto-resolved from children if null.")]
    [SerializeField] private Animator animator;

    // ─── Runtime ──────────────────────────────────────────────────────────────

    private CharacterController characterController;
    private InputAction         moveAction;
    private InputAction         sprintAction;

    private float verticalVelocity;
    private float turnVelocity;

    // ─── Lifecycle ────────────────────────────────────────────────────────────

    private void Awake()
    {
        characterController = GetComponent<CharacterController>();

        if (animator == null)
            animator = GetComponentInChildren<Animator>();

        if (followCamera == null)
            followCamera = Camera.main;

        BuildInputActions();
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

        // Reset animator to idle when observer mode is exited.
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
        Vector2 input        = moveAction.ReadValue<Vector2>();
        bool    isSprinting  = sprintAction.ReadValue<float>() > 0.5f;
        float   speed        = walkSpeed * (isSprinting ? sprintMultiplier : 1f);
        float   inputMag     = Mathf.Clamp01(input.magnitude);

        Vector3 moveDir = Vector3.zero;
        if (inputMag > 0.01f)
        {
            // Rotate input by camera yaw only so movement is camera-relative.
            float   camYaw    = followCamera != null ? followCamera.transform.eulerAngles.y : 0f;
            Vector3 inputDir  = new Vector3(input.x, 0f, input.y).normalized;
            float   targetYaw = Mathf.Atan2(inputDir.x, inputDir.z) * Mathf.Rad2Deg + camYaw;

            // Smooth-damp the body rotation.
            float smoothYaw = Mathf.SmoothDampAngle(
                transform.eulerAngles.y, targetYaw, ref turnVelocity, turnSmoothTime);
            transform.rotation = Quaternion.Euler(0f, smoothYaw, 0f);

            moveDir = Quaternion.Euler(0f, targetYaw, 0f) * Vector3.forward;
        }

        Vector3 motion = moveDir * (speed * inputMag) + Vector3.up * verticalVelocity;
        characterController.Move(motion * Time.deltaTime);

        // Drive animator — actual horizontal speed so animation matches movement.
        if (animator != null)
        {
            float animSpeed = inputMag * speed;
            animator.SetFloat(AnimParamSpeed, animSpeed, AnimDampTime, Time.deltaTime);
        }
    }

    private void ApplyGravity()
    {
        if (characterController.isGrounded && verticalVelocity < 0f)
            verticalVelocity = -2f; // Small negative keeps the grounded-check reliable.
        else
            verticalVelocity += gravity * Time.deltaTime;
    }

    // ─── Input Actions ────────────────────────────────────────────────────────

    private void BuildInputActions()
    {
        // WASD composite + gamepad left stick.
        moveAction = new InputAction(
            name: "ObserverMove",
            type: InputActionType.Value,
            expectedControlType: "Vector2");

        moveAction.AddCompositeBinding("2DVector")
            .With("Up",    "<Keyboard>/w")
            .With("Down",  "<Keyboard>/s")
            .With("Left",  "<Keyboard>/a")
            .With("Right", "<Keyboard>/d");

        moveAction.AddBinding("<Gamepad>/leftStick");

        // Sprint: Left Shift, Right Shift, or gamepad left shoulder.
        sprintAction = new InputAction(name: "ObserverSprint", type: InputActionType.Button);
        sprintAction.AddBinding("<Keyboard>/leftShift");
        sprintAction.AddBinding("<Keyboard>/rightShift");
        sprintAction.AddBinding("<Gamepad>/leftShoulder");
    }
}
