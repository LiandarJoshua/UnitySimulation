using UnityEngine;

/// <summary>
/// Supplementary component that handles the Phase 1 panic duration and casualty roll
/// for individual agents near the gas/fire source.
/// Attach alongside AgentBrain on every agent prefab.
/// </summary>
[RequireComponent(typeof(AgentBrain))]
public class PanicBehavior : MonoBehaviour
{
    [Header("Panic Settings")]
    [Tooltip("Maximum duration (seconds) of the panic phase before the agent calms down.")]
    [SerializeField] private float maxPanicDuration = 60f;

    [Tooltip("Probability that an agent near the gas source becomes a casualty on fire trigger.")]
    [Range(0f, 1f)]
    [SerializeField] private float casualtyProbability = 0.12f;

    [Tooltip("Radius around a fire/gas source within which the casualty roll applies.")]
    [SerializeField] private float casualtyRadius = 8f;

    // ─── Runtime ──────────────────────────────────────────────────────────────

    private AgentBrain brain;
    private float      panicTimer;
    private bool       panicActive;

    // ─── Lifecycle ────────────────────────────────────────────────────────────

    private void Awake()
    {
        brain = GetComponent<AgentBrain>();
    }

    private void Update()
    {
        if (!panicActive) return;

        panicTimer -= Time.deltaTime;
        if (panicTimer <= 0f)
            EndPanic();
    }

    // ─── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Called by EvacSimManager when gas/fire breaks out.
    /// Checks casualty probability and either kills or panics the agent.
    /// </summary>
    public void OnBreakout(Transform[] gasSources)
    {
        if (brain.IsCasualty || brain.IsEvacuated) return;

        // Casualty roll: agents near gas source have a chance of becoming casualties.
        bool nearGas = false;
        foreach (Transform src in gasSources)
        {
            if (src != null && Vector3.Distance(transform.position, src.position) <= casualtyRadius)
            {
                nearGas = true;
                break;
            }
        }

        if (nearGas && UnityEngine.Random.value < casualtyProbability)
        {
            brain.BecomeCasualty();
            return;
        }

        StartPanic();
    }

    private void StartPanic()
    {
        panicActive = true;
        panicTimer  = maxPanicDuration;
        brain.TriggerPanic();
    }

    private void EndPanic()
    {
        panicActive = false;
        // After panic expires agents still need an explicit evacuation signal from a leader.
        // They revert to wandering until a leader broadcasts.
        if (brain.CurrentState == AgentBrain.AgentState.Panicking)
            brain.StartWandering();
    }
}
