using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Third-person orbit camera for the observer character.
///
/// Features:
///   - Mouse delta (or gamepad right stick) adjusts yaw and pitch.
///   - Pitch is clamped to prevent flipping.
///   - Camera position is offset behind the pivot at <see cref="followDistance"/>.
///   - SphereCast collision pulls the camera toward the character if obstructed.
///   - Position is smoothed with <see cref="Vector3.SmoothDamp"/> each LateUpdate.
///   - InputActions created entirely in code — no InputActionAsset editing needed.
/// </summary>
public class SimObserverFollowCamera : MonoBehaviour
{
    // ─── Inspector ────────────────────────────────────────────────────────────

    [Header("Target")]
    [Tooltip("The observer character transform the camera orbits.")]
    [SerializeField] private Transform target;

    [Tooltip("Height offset above the target root used as the look-at pivot (m).")]
    [SerializeField] private float pivotHeight = 1.6f;

    [Header("Orbit Sensitivity")]
    [Tooltip("Horizontal mouse sensitivity (degrees per pixel delta).")]
    [SerializeField] private float sensitivityX = 1.5f;

    [Tooltip("Vertical mouse sensitivity (degrees per pixel delta).")]
    [SerializeField] private float sensitivityY = 1f;

    [Header("Pitch Clamp")]
    [Tooltip("Minimum pitch in degrees (looking up).")]
    [SerializeField] private float minPitch = -20f;

    [Tooltip("Maximum pitch in degrees (looking down).")]
    [SerializeField] private float maxPitch = 60f;

    [Header("Distance")]
    [Tooltip("Desired follow distance behind the pivot (m).")]
    [SerializeField] private float followDistance = 4.5f;

    [Tooltip("Closest the camera is pulled toward the pivot on geometry collision (m).")]
    [SerializeField] private float minDistance = 1f;

    [Tooltip("SphereCast radius used for collision detection. Increase to prevent edge clipping.")]
    [SerializeField] private float collisionRadius = 0.2f;

    [Header("Smoothing")]
    [Tooltip("Position follow smoothing time (s). Lower values = snappier.")]
    [SerializeField] private float positionSmoothing = 0.08f;

    // ─── Runtime ──────────────────────────────────────────────────────────────

    private InputAction lookAction;

    private float yaw;
    private float pitch = 15f; // Start slightly above horizontal.

    private Vector3 currentVelocity;

    // ─── Lifecycle ────────────────────────────────────────────────────────────

    private void Awake()
    {
        // Mouse delta + gamepad right stick — created in code, no asset needed.
        lookAction = new InputAction(
            name: "ObserverLook",
            type: InputActionType.Value,
            expectedControlType: "Vector2");

        lookAction.AddBinding("<Mouse>/delta");
        lookAction.AddBinding("<Gamepad>/rightStick");
    }

    private void OnEnable()
    {
        lookAction.Enable();

        // Initialise from current camera angles so there is no snap on enable.
        yaw   = transform.eulerAngles.y;
        pitch = transform.eulerAngles.x;
    }

    private void OnDisable() => lookAction.Disable();

    private void OnDestroy() => lookAction?.Dispose();

    private void LateUpdate()
    {
        if (target == null) return;

        // ── Orbit input ───────────────────────────────────────────────────────
        Vector2 look = lookAction.ReadValue<Vector2>();
        yaw   += look.x * sensitivityX;
        pitch -= look.y * sensitivityY; // Inverted: moving mouse up pitches camera down.
        pitch  = Mathf.Clamp(pitch, minPitch, maxPitch);

        // ── Desired camera position ───────────────────────────────────────────
        Vector3    pivot      = target.position + Vector3.up * pivotHeight;
        Quaternion orbitRot   = Quaternion.Euler(pitch, yaw, 0f);
        Vector3    desiredPos = pivot - orbitRot * Vector3.forward * followDistance;

        // ── Collision: pull camera in if an object is between pivot and desired ─
        float   actualDistance = followDistance;
        Vector3 dir            = (desiredPos - pivot).normalized;

        if (Physics.SphereCast(pivot, collisionRadius, dir, out RaycastHit hit,
                               followDistance, Physics.DefaultRaycastLayers,
                               QueryTriggerInteraction.Ignore))
        {
            actualDistance = Mathf.Max(hit.distance - collisionRadius, minDistance);
        }

        Vector3 targetPos = pivot - orbitRot * Vector3.forward * actualDistance;

        // ── Smooth follow ─────────────────────────────────────────────────────
        transform.position = Vector3.SmoothDamp(
            transform.position, targetPos, ref currentVelocity, positionSmoothing);

        // ── Always look at the pivot ──────────────────────────────────────────
        transform.LookAt(pivot);
    }
}
