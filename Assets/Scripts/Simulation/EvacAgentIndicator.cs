using UnityEngine;

/// <summary>
/// Renders a colour-coded overhead disc on each agent to communicate state at a glance.
/// Requires a child Renderer (quad/sphere) with an unlit material that exposes _Color.
/// </summary>
public class EvacAgentIndicator : MonoBehaviour
{
    private static readonly Color ColSafe      = new Color(0.18f, 0.80f, 0.44f); // green  — wandering/idle
    private static readonly Color ColPanic     = new Color(1.00f, 0.65f, 0.00f); // amber  — panicking
    private static readonly Color ColEvac      = new Color(0.18f, 0.55f, 0.90f); // blue   — evacuating
    private static readonly Color ColMask      = new Color(0.20f, 0.85f, 1.00f); // cyan   — seeking mask
    private static readonly Color ColLeader    = new Color(1.00f, 1.00f, 0.20f); // yellow — leader
    private static readonly Color ColMedical   = new Color(1.00f, 0.30f, 0.90f); // pink   — medical
    private static readonly Color ColCasualty  = new Color(0.90f, 0.10f, 0.10f); // red    — casualty
    private static readonly Color ColEvacuated = new Color(0.40f, 0.40f, 0.40f); // grey   — evacuated

    private static readonly int ColorID = Shader.PropertyToID("_Color");

    private Renderer             disc;
    private MaterialPropertyBlock propBlock;

    private void Awake()
    {
        disc      = GetComponent<Renderer>();
        propBlock = new MaterialPropertyBlock();
    }

    /// <summary>Updates the disc colour to reflect the agent's current state and role.</summary>
    public void SetState(AgentBrain.AgentState state, AgentRole role = AgentRole.Civilian)
    {
        if (disc == null) return;

        Color col = state switch
        {
            AgentBrain.AgentState.Casualty        => ColCasualty,
            AgentBrain.AgentState.Evacuated        => ColEvacuated,
            AgentBrain.AgentState.Panicking        => ColPanic,
            AgentBrain.AgentState.Evacuating when role == AgentRole.EvacuationLeader => ColLeader,
            AgentBrain.AgentState.Evacuating when role == AgentRole.MedicalTeam      => ColMedical,
            AgentBrain.AgentState.Evacuating        => ColEvac,
            AgentBrain.AgentState.SeekingMask       => ColMask,
            AgentBrain.AgentState.MedicalResponse   => ColMedical,
            _                                        => ColSafe
        };

        disc.GetPropertyBlock(propBlock);
        propBlock.SetColor(ColorID, col);
        disc.SetPropertyBlock(propBlock);
    }
}
