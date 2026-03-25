using UnityEngine;

/// <summary>
/// Renders a coloured overhead disc above each agent to communicate state at a glance.
/// Uses a scaled child quad with a simple unlit material so it is always readable.
/// </summary>
public class AgentStatusIndicator : MonoBehaviour
{
    private static readonly Color ColorSafe      = new Color(0.18f, 0.80f, 0.44f); // green
    private static readonly Color ColorPanicked  = new Color(1.00f, 0.65f, 0.00f); // amber
    private static readonly Color ColorInjured   = new Color(0.90f, 0.10f, 0.10f); // red
    private static readonly Color ColorEvacuated = new Color(0.40f, 0.40f, 0.40f); // grey

    private Renderer indicatorRenderer;
    private MaterialPropertyBlock propertyBlock;
    private static readonly int ColorID = Shader.PropertyToID("_Color");

    private void Awake()
    {
        propertyBlock = new MaterialPropertyBlock();
        indicatorRenderer = GetComponent<Renderer>();
    }

    /// <summary>Updates the indicator colour based on the agent's current state.</summary>
    public void SetState(AgentController.AgentState state, bool isPanicked)
    {
        if (indicatorRenderer == null) return;

        Color target = state switch
        {
            AgentController.AgentState.Evacuated => ColorEvacuated,
            AgentController.AgentState.Evacuating when isPanicked => ColorPanicked,
            AgentController.AgentState.Injured => ColorInjured,
            _ => ColorSafe
        };

        indicatorRenderer.GetPropertyBlock(propertyBlock);
        propertyBlock.SetColor(ColorID, target);
        indicatorRenderer.SetPropertyBlock(propertyBlock);
    }
}
