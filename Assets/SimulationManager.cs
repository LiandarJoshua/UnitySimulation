using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Audio;

/// <summary>
/// Central controller for the oil rig evacuation simulation.
/// Handles agent spawning, fire triggering, and orchestrating evacuation.
/// </summary>
public class SimulationManager : MonoBehaviour
{
    public static SimulationManager Instance { get; private set; }

    [Header("Agent Settings")]
    [SerializeField] private GameObject[] agentPrefabs;
    [SerializeField] private int agentCount = 50;
    [SerializeField] private Transform spawnCenter;
    [SerializeField] private float spawnRadius = 12f;

    [Header("Exit Point")]
    [SerializeField] private Transform exitPoint;

    [Header("Fire Settings")]
    [SerializeField] private GameObject firePrefab;
    [SerializeField] private Transform[] fireSpawnPoints;
    [SerializeField] private int fireCount = 5;
    [SerializeField] private float fireScale = 3f;

    [Header("Audio")]
    [SerializeField] private AudioSource sirenAudioSource;

    [Header("Auto-Start (Testing)")]
    [SerializeField] private bool autoStart = true;
    [SerializeField] private float autoFireDelay = 15f;

    private readonly List<AgentController> agents = new List<AgentController>();
    private readonly List<GameObject> activeFires = new List<GameObject>();

    private float simulationStartTime;
    private float evacuationStartTime;
    private int evacuatedCount;
    private int injuredCount;

    public bool SimulationRunning { get; private set; }
    public bool FireTriggered { get; private set; }
    public int TotalAgents => agents.Count;
    public int EvacuatedCount => evacuatedCount;
    public int InjuredCount => injuredCount;
    public float SimulationStartTime => simulationStartTime;
    public float EvacuationStartTime => evacuationStartTime;

    /// <summary>The exit point Transform, exposed for analytics.</summary>
    public Transform ExitPoint => exitPoint;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        // Auto-resolve AudioSource on this GameObject if not wired in Inspector.
        if (sirenAudioSource == null)
            sirenAudioSource = GetComponent<AudioSource>();
    }

    private void Start()
    {
        if (autoStart)
            StartCoroutine(AutoStartSequence());
    }

    private IEnumerator AutoStartSequence()
    {
        // Wait two frames for all scene components to initialise before sampling the NavMesh
        yield return null;
        yield return null;

        StartSimulation();

        if (autoFireDelay > 0f)
        {
            yield return new WaitForSeconds(autoFireDelay);
            TriggerFire();
        }
    }

    /// <summary>Spawns agents on the NavMesh and begins random wandering.</summary>
    public void StartSimulation()
    {
        if (SimulationRunning)
        {
            Debug.LogWarning("[SimulationManager] Simulation is already running.");
            return;
        }

        SimulationRunning = true;
        FireTriggered = false;
        evacuatedCount = 0;
        injuredCount = 0;
        simulationStartTime = Time.time;

        SpawnAgents();
        Debug.Log($"[SimulationManager] Simulation started with {agents.Count} agents.");
    }

    /// <summary>Spawns fires across the refinery and triggers full evacuation.</summary>
    public void TriggerFire()
    {
        if (!SimulationRunning)
        {
            Debug.LogWarning("[SimulationManager] Start the simulation first.");
            return;
        }

        if (FireTriggered)
        {
            Debug.LogWarning("[SimulationManager] Fire already triggered.");
            return;
        }

        FireTriggered = true;
        evacuationStartTime = Time.time;

        SpawnFires();
        PlaySiren();

        // Delay evacuation so NavMeshObstacle carving has time to punch holes in the
        // NavMesh before agents calculate their paths. Without this, paths are computed
        // on the pre-carve mesh and agents walk straight through fire.
        StartCoroutine(DelayedEvacuate());

        Debug.Log($"[SimulationManager] Fire triggered! {agents.Count} agents evacuating.");
    }

    private IEnumerator DelayedEvacuate()
    {
        // Brief delay so fire GameObjects are fully instantiated and their transforms
        // registered before agents start computing evacuation paths.
        yield return new WaitForSeconds(0.2f);
        EvacuateAllAgents();
    }

    /// <summary>Clears all agents and fires, returning the simulation to initial state.</summary>
    public void ResetSimulation()
    {
        StopAllCoroutines();
        CancelInvoke();

        foreach (var agent in agents)
        {
            if (agent != null)
                Destroy(agent.gameObject);
        }
        agents.Clear();

        foreach (var fire in activeFires)
        {
            if (fire != null)
                Destroy(fire);
        }
        activeFires.Clear();

        AgentController.ClearFireSources();

        SimulationRunning = false;
        FireTriggered = false;
        evacuatedCount = 0;
        injuredCount = 0;

        StopSiren();
        EvacuationMetrics.Instance?.ResetMetrics();
        SimulationAnalytics.Instance?.ResetAnalytics();
        Debug.Log("[SimulationManager] Simulation reset.");
    }

    /// <summary>Called by AgentController when an agent reaches the exit.</summary>
    public void OnAgentEvacuated()
    {
        evacuatedCount++;
        float elapsed = FireTriggered ? Time.time - evacuationStartTime : 0f;
        EvacuationMetrics.Instance?.RecordEvacuation(evacuatedCount, elapsed);
    }

    /// <summary>Called by AgentController when an agent becomes incapacitated.</summary>
    public void OnAgentInjured()
    {
        injuredCount++;
        float elapsed = FireTriggered ? Time.time - evacuationStartTime : 0f;
        EvacuationMetrics.Instance?.RecordInjury(injuredCount, elapsed);
    }

    private void SpawnAgents()
    {
        if (agentPrefabs == null || agentPrefabs.Length == 0)
        {
            Debug.LogError("[SimulationManager] No agent prefabs assigned!");
            return;
        }

        Vector3 center = spawnCenter != null ? spawnCenter.position : Vector3.zero;
        int spawned = 0;

        for (int i = 0; i < agentCount; i++)
        {
            // Find a valid NavMesh position near the compound centre
            Vector3 spawnPos = SampleRandomNavMeshPoint(center, spawnRadius);
            if (spawnPos == Vector3.zero)
            {
                Debug.LogWarning($"[SimulationManager] Skipping agent {i}: no NavMesh point found near {center} within radius {spawnRadius}.");
                continue;
            }

            GameObject prefab = agentPrefabs[UnityEngine.Random.Range(0, agentPrefabs.Length)];
            Quaternion rotation = Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f);

            // Spawn slightly above ground to avoid precision issues, then Warp onto the NavMesh
            GameObject agentGO = Instantiate(prefab, spawnPos + Vector3.up * 0.1f, rotation);
            agentGO.name = $"Agent_{i:00}";

            // Guarantee NavMeshAgent exists
            NavMeshAgent nav = agentGO.GetComponent<NavMeshAgent>();
            if (nav == null)
                nav = agentGO.AddComponent<NavMeshAgent>();

            // Warp forces the agent onto the nearest NavMesh surface regardless of spawn offset
            if (!nav.Warp(spawnPos))
                Debug.LogWarning($"[SimulationManager] Agent_{i:00} Warp failed — position may be off-mesh.");

            AgentController controller = agentGO.GetComponent<AgentController>();
            if (controller == null)
                controller = agentGO.AddComponent<AgentController>();

            controller.Initialize(exitPoint);
            controller.StartWandering();
            agents.Add(controller);
            spawned++;
        }

        Debug.Log($"[SimulationManager] Spawned {spawned}/{agentCount} agents.");
    }

    private void SpawnFires()
    {
        if (firePrefab == null)
        {
            Debug.LogError("[SimulationManager] No fire prefab assigned!");
            return;
        }

        if (fireSpawnPoints != null && fireSpawnPoints.Length > 0)
        {
            // Shuffle the fire spawn points and pick fireCount of them
            List<Transform> shuffled = new List<Transform>(fireSpawnPoints);
            for (int i = 0; i < shuffled.Count; i++)
            {
                int j = UnityEngine.Random.Range(i, shuffled.Count);
                (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
            }

            int count = Mathf.Min(fireCount, shuffled.Count);
            for (int i = 0; i < count; i++)
            {
                GameObject fire = Instantiate(firePrefab, shuffled[i].position, Quaternion.identity);
                fire.transform.localScale = Vector3.one * fireScale;
                AddFireObstacle(fire);
                activeFires.Add(fire);
            }
        }
        else
        {
            // Fall back to random positions near spawn center
            Vector3 center = spawnCenter != null ? spawnCenter.position : Vector3.zero;
            for (int i = 0; i < fireCount; i++)
            {
                Vector3 pos = center + new Vector3(
                    UnityEngine.Random.Range(-spawnRadius * 0.8f, spawnRadius * 0.8f),
                    0f,
                    UnityEngine.Random.Range(-spawnRadius * 0.8f, spawnRadius * 0.8f));
                GameObject fire = Instantiate(firePrefab, pos, Quaternion.identity);
                fire.transform.localScale = Vector3.one * fireScale;
                AddFireObstacle(fire);
                activeFires.Add(fire);
            }
        }
    }

    /// <summary>
    /// Registers the fire's transform with AgentController for steering-level avoidance.
    /// NavMeshObstacle carving is intentionally NOT used here — a carving obstacle with
    /// a radius large enough to keep agents clear will sever NavMesh connectivity in
    /// narrow corridors, making the exit permanently unreachable. Steering-only avoidance
    /// keeps the NavMesh intact while still routing agents around the fire.
    /// </summary>
    private void AddFireObstacle(GameObject fire)
    {
        // Remove any pre-existing NavMeshObstacle on the fire prefab to prevent carving.
        NavMeshObstacle existing = fire.GetComponent<NavMeshObstacle>();
        if (existing != null)
        {
            existing.carving = false;
            Destroy(existing);
        }

        AgentController.RegisterFireSource(fire.transform);
    }

    private void EvacuateAllAgents()
    {
        foreach (var agent in agents)
        {
            if (agent != null && agent.gameObject.activeSelf)
                agent.StartEvacuating();
        }
    }

    // ─── Audio ────────────────────────────────────────────────────────────────

    /// <summary>Starts the air-raid siren when evacuation is triggered.</summary>
    private void PlaySiren()
    {
        if (sirenAudioSource == null) return;
        if (sirenAudioSource.isPlaying) sirenAudioSource.Stop();
        sirenAudioSource.Play();
    }

    /// <summary>Stops the siren when the simulation resets.</summary>
    private void StopSiren()
    {
        if (sirenAudioSource != null && sirenAudioSource.isPlaying)
            sirenAudioSource.Stop();
    }

    private Vector3 SampleRandomNavMeshPoint(Vector3 center, float radius)
    {
        for (int attempt = 0; attempt < 15; attempt++)
        {
            Vector3 candidate = center + new Vector3(
                UnityEngine.Random.Range(-radius, radius),
                0f,
                UnityEngine.Random.Range(-radius, radius));

            if (NavMesh.SamplePosition(candidate, out NavMeshHit hit, radius, NavMesh.AllAreas))
                return hit.position;
        }
        return Vector3.zero;
    }
}
