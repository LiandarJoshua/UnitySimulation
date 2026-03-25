using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Third-person follow camera for the observer character.
///
/// - Orbits the character pivot with mouse look (mouse delta / right stick).
/// - Maintains a configurable distance behind and above the character.
/// - Pulls toward the character if a solid object is between them (collision).
/// - Clamps vertical pitch so the camera never flips.
///
/// This script never reads from or writes to any simulation state.
/// </summary>
public class ObserverFollowCamera : MonoBehaviour
{
    // ─── Serialized ───────────────────────────────────────────────────────────

    [Header("Target")]
    [Tooltip("The observer character transform the camera orbits around.")]
    [SerializeField] private Transform target;

    [Tooltip("Height offset above the target root to use as the look-at pivot.")]
    [SerializeField] private float pivotHeight = 1.6f;

    [Header("Orbit")]
    [Tooltip("Horizontal mouse sensitivity (degrees per pixel).")]
    [SerializeField] private float sensitivityX = 1.5f;

    [Tooltip("Vertical mouse sensitivity (degrees per pixel).")]
    [SerializeField] private float sensitivityY = 1f;

    [Tooltip("Minimum vertical pitch in degrees (looking up).")]
    [SerializeField] private float minPitch = -20f;

    [Tooltip("Maximum vertical pitch in degrees (looking down).")]
    [SerializeField] private float maxPitch = 60f;

    [Header("Distance")]
    [Tooltip("Desired follow distance from the pivot.")]
    [SerializeField] private float followDistance = 4.5f;

    [Tooltip("Closest the camera is pulled toward the pivot on collision.")]
    [SerializeField] private float minDistance = 1f;

    [Tooltip("Camera collision radius used for the SphereCast. Increase to prevent clipping.")]
    [SerializeField] private float collisionRadius = 0.2f;

    [Header("Smoothing")]
    [Tooltip("Position follow smoothing time. Lower = snappier.")]
    [SerializeField] private float positionSmoothing = 0.08f;

    // ─── Runtime ──────────────────────────────────────────────────────────────

    private InputAction lookAction;

    private float yaw;
    private float pitch = 15f; // Start slightly above horizontal.

    private Vector3 currentVelocity;

    // ─── Unity Lifecycle ──────────────────────────────────────────────────────

    private void Awake()
    {
        lookAction = new InputAction(name: "ObserverLook", type: InputActionType.Value, expectedControlType: "Vector2");
        lookAction.AddBinding("<Mouse>/delta");
        lookAction.AddBinding("<Gamepad>/rightStick");
    }

    private void OnEnable()
    {
        lookAction.Enable();

        // Initialise yaw from the current camera angle so there is no snap on enable.
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
        pitch -= look.y * sensitivityY;  // Inverted so moving mouse up pitches camera down.
        pitch  = Mathf.Clamp(pitch, minPitch, maxPitch);

        // ── Desired camera position ───────────────────────────────────────────
        Vector3 pivot          = target.position + Vector3.up * pivotHeight;
        Quaternion orbitRot    = Quaternion.Euler(pitch, yaw, 0f);
        Vector3    desiredPos  = pivot - orbitRot * Vector3.forward * followDistance;

        // ── Collision push-in (SphereCast from pivot to desired position) ─────
        float actualDistance = followDistance;
        Vector3 dir = (desiredPos - pivot).normalized;
        if (Physics.SphereCast(pivot, collisionRadius, dir, out RaycastHit hit,
                               followDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
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
