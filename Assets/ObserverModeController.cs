using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Controls switching between the main simulation camera and the 3rd-person observer character.
///
/// Entering: click "Enter Character View" button in the ControlPanel.
/// Exiting:  press Escape while in character view.
///
/// While in observer mode:
///   - Main Camera is disabled; ObserverCamera is enabled.
///   - ObserverCharacter is enabled and receives WASD + mouse input.
///   - Cursor lock is enforced every frame to survive Unity EventSystem resets after UI clicks.
///   - ControlPanel hides so WASD can't accidentally focus a UI button.
///   - A passive hint label shows "Press ESC to exit" — not a button, just informational text.
///   - The simulation and dashboard keep running untouched.
///
/// This script never reads or writes any simulation state.
/// </summary>
public class ObserverModeController : MonoBehaviour
{
    // ─── Serialized ───────────────────────────────────────────────────────────

    [Header("Cameras")]
    [Tooltip("The scene's main simulation camera.")]
    [SerializeField] private Camera mainCamera;

    [Tooltip("The observer follow camera.")]
    [SerializeField] private Camera observerCamera;

    [Header("Character")]
    [Tooltip("Root GameObject of the observer character.")]
    [SerializeField] private GameObject observerCharacter;

    [Tooltip("Optional fixed spawn point. Falls back to main camera position if null.")]
    [SerializeField] private Transform observerSpawnPoint;

    [Header("UI")]
    [Tooltip("The ControlPanel to hide while in observer mode so WASD is not blocked by UI focus.")]
    [SerializeField] private GameObject controlPanel;

    [Tooltip("Passive hint label shown top-center during observer mode. Not a button.")]
    [SerializeField] private GameObject exitHintLabel;

    // ─── Runtime ──────────────────────────────────────────────────────────────

    private bool        isObserverActive;
    private InputAction exitAction;

    // ─── Unity Lifecycle ──────────────────────────────────────────────────────

    private void Awake()
    {
        // Built in code — no InputActionAsset editing needed.
        exitAction = new InputAction(name: "ExitObserverMode", binding: "<Keyboard>/escape");
        exitAction.performed += _ => ExitObserverMode();
    }

    private void Start()
    {
        ApplyState(false);
    }

    private void Update()
    {
        // Unity's EventSystem resets CursorLockMode to None on the frame after a UI click.
        // Re-applying it every frame is the reliable fix — it has no cost when already correct.
        if (isObserverActive)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible   = false;
        }
    }

    private void OnDestroy() => exitAction?.Dispose();

    // ─── Public API ───────────────────────────────────────────────────────────

    /// <summary>Switches to 3rd-person observer mode. Wired to BtnObserver in ControlPanel.</summary>
    public void EnterObserverMode()
    {
        if (isObserverActive) return;
        isObserverActive = true;
        exitAction.Enable();
        ApplyState(true);
    }

    /// <summary>Returns to simulation camera. Triggered by pressing Escape.</summary>
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
        if (mainCamera != null)
            mainCamera.gameObject.SetActive(!observerActive);

        if (observerCamera != null)
            observerCamera.gameObject.SetActive(observerActive);

        if (observerCharacter != null)
        {
            if (observerActive) RepositionCharacter();
            observerCharacter.SetActive(observerActive);
        }

        if (controlPanel != null)
            controlPanel.SetActive(!observerActive);

        if (exitHintLabel != null)
            exitHintLabel.SetActive(observerActive);

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
        else if (mainCamera != null)
        {
            Vector3 cam = mainCamera.transform.position;
            pos = new Vector3(cam.x, 0f, cam.z);
            rot = Quaternion.Euler(0f, mainCamera.transform.eulerAngles.y, 0f);
        }

        var cc = observerCharacter.GetComponent<CharacterController>();
        if (cc != null) cc.enabled = false;
        observerCharacter.transform.SetPositionAndRotation(pos, rot);
        if (cc != null) cc.enabled = true;
    }
}
