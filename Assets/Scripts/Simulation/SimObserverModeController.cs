using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Handles entering and exiting 3rd-person observer mode within the evacuation simulation.
///
/// On entry:
///   - Disables the active simulation camera (managed by <see cref="SimCameraRigController"/>).
///   - Enables the observer camera and character.
///   - Hides the simulation UI so WASD input is not blocked by UI focus.
///   - Locks and hides the cursor.
///   - Cursor lock is reapplied every frame to survive Unity EventSystem resets after UI clicks.
///
/// On exit (Escape key):
///   - Restores the simulation camera and UI.
///   - Unlocks the cursor.
///
/// The simulation continues running untouched in both modes.
/// </summary>
public class SimObserverModeController : MonoBehaviour
{
    // ─── Inspector ────────────────────────────────────────────────────────────

    [Header("Observer Camera")]
    [Tooltip("Camera used while in observer mode. Enabled only when active.")]
    [SerializeField] private Camera observerCamera;

    [Header("Observer Character")]
    [Tooltip("Root GameObject of the observer character. Enabled only when active.")]
    [SerializeField] private GameObject observerCharacter;

    [Tooltip("Optional fixed spawn point. Falls back to the last active rig camera position if null.")]
    [SerializeField] private Transform observerSpawnPoint;

    [Header("UI")]
    [Tooltip("Simulation UI root to hide while in observer mode.")]
    [SerializeField] private GameObject simulationUI;

    [Tooltip("Overlay hint shown top-centre during observer mode (e.g. 'Press ESC to exit').")]
    [SerializeField] private GameObject exitHintOverlay;

    // ─── Runtime ──────────────────────────────────────────────────────────────

    private bool        isObserverActive;
    private InputAction exitAction;

    // ─── Lifecycle ────────────────────────────────────────────────────────────

    private void Awake()
    {
        exitAction = new InputAction(name: "ExitObserverMode", binding: "<Keyboard>/escape");
        exitAction.performed += _ => ExitObserverMode();
    }

    private void Start() => ApplyState(false);

    private void Update()
    {
        // Unity's EventSystem resets CursorLockMode to None on the frame after any UI click.
        // Re-applying every frame is the reliable fix — zero cost when already correct.
        if (isObserverActive)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible   = false;
        }
    }

    private void OnDestroy() => exitAction?.Dispose();

    // ─── Public API ───────────────────────────────────────────────────────────

    /// <summary>Enters 3rd-person observer mode. Wire to an Observer button in the control panel.</summary>
    public void EnterObserverMode()
    {
        if (isObserverActive) return;
        isObserverActive = true;
        exitAction.Enable();
        ApplyState(true);
    }

    /// <summary>Returns to the simulation rig camera. Triggered by pressing Escape.</summary>
    public void ExitObserverMode()
    {
        if (!isObserverActive) return;
        isObserverActive = false;
        exitAction.Disable();
        ApplyState(false);
    }

    // ─── State Application ────────────────────────────────────────────────────

    private void ApplyState(bool observerActive)
    {
        // Disable / re-enable the simulation camera rig.
        SimCameraRigController rig = SimCameraRigController.Instance;
        Camera rigCam = rig != null ? rig.ActiveCamera : null;
        if (rigCam != null) rigCam.gameObject.SetActive(!observerActive);

        // Toggle observer camera.
        if (observerCamera != null)
            observerCamera.gameObject.SetActive(observerActive);

        // Toggle observer character.
        if (observerCharacter != null)
        {
            if (observerActive)
                RepositionCharacter();
            observerCharacter.SetActive(observerActive);
        }

        // Toggle simulation UI.
        if (simulationUI != null)
            simulationUI.SetActive(!observerActive);

        // Toggle exit hint.
        if (exitHintOverlay != null)
            exitHintOverlay.SetActive(observerActive);

        Cursor.lockState = observerActive ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible   = !observerActive;
    }

    // ─── Character Spawn ──────────────────────────────────────────────────────

    private void RepositionCharacter()
    {
        Vector3    pos = Vector3.zero;
        Quaternion rot = Quaternion.identity;

        if (observerSpawnPoint != null)
        {
            pos = observerSpawnPoint.position;
            rot = Quaternion.Euler(0f, observerSpawnPoint.eulerAngles.y, 0f);
        }
        else
        {
            // Fall back to the currently active rig camera's ground position.
            SimCameraRigController rig = SimCameraRigController.Instance;
            Camera rigCam = rig != null ? rig.ActiveCamera : null;
            if (rigCam != null)
            {
                Vector3 camPos = rigCam.transform.position;
                pos = new Vector3(camPos.x, 0f, camPos.z);
                rot = Quaternion.Euler(0f, rigCam.transform.eulerAngles.y, 0f);
            }
        }

        // Disable CharacterController before moving to avoid physics conflicts.
        CharacterController cc = observerCharacter.GetComponent<CharacterController>();
        if (cc != null) cc.enabled = false;
        observerCharacter.transform.SetPositionAndRotation(pos, rot);
        if (cc != null) cc.enabled = true;
    }
}
