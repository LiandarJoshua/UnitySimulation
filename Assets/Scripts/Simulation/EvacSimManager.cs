using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using System.Linq;

/// <summary>
/// Central simulation controller for the gas breakout evacuation.
///
/// Phase 1 (Breakout, 0–60 s):
///   - Gas/fire spawned on NavMesh at start.
///   - Alarm plays immediately.
///   - All agents enter panic with erratic movement and cohort following.
///   - Agents near gas source may become casualties.
///
/// Phase 2 (Directed Evacuation, 60 s+):
///   - Evacuation Leaders switch to pointing animation and broadcast evacuation signal.
///   - Civilians follow leader instructions if within perception radius and line of sight.
///   - Agents navigate to nearest exit, collect masks en route if needed.
///
/// Phase 3 (Medical Response — optional):
///   - Medical Team agents navigate toward casualties.
///
/// All configuration variables are exposed via Inspector.
/// </summary>
public class EvacSimManager : MonoBehaviour
{
    public static EvacSimManager Instance { get; private set; }

    // ─── Simulation Phase ──────────────────────────────────────────────────────

    public enum SimPhase { Idle, Phase1_Breakout, Phase2_Evacuation, Phase3_Medical, Complete }

    // ─── Inspector: Agent Settings ────────────────────────────────────────────

    [Header("Agent Settings")]
    [Tooltip("Prefab used for civilian/leader/medical agents. Assign the Worker prefab (has humanoid Animator).")]
    [SerializeField] private GameObject agentPrefab;

    [Tooltip("Maximum number of agents to spawn (up to 500).")]
    [SerializeField, Range(1, 500)] private int agentCount = 100;

    [Tooltip("Number of Evacuation Leaders (3–5 recommended).")]
    [SerializeField, Range(1, 10)] private int leaderCount = 4;

    [Tooltip("Number of Medical Team agents.")]
    [SerializeField, Range(0, 20)] private int medicalCount = 3;

    [Tooltip("Centre of the agent spawn zone. Leave empty to use scene origin.")]
    [SerializeField] private Transform spawnCenter;

    [Tooltip("Radius around spawnCenter in which agents are placed on the NavMesh.")]
    [SerializeField] private float spawnRadius = 40f;

    // ─── Inspector: Fire / Gas ────────────────────────────────────────────────

    [Header("Fire & Gas Settings")]
    [Tooltip("Fire/gas VFX prefab placed at breakout location.")]
    [SerializeField] private GameObject firePrefab;

    [Tooltip("Number of fire/gas emitters to spawn.")]
    [SerializeField, Range(1, 10)] private int fireCount = 3;

    [Tooltip("Scale applied to the fire prefab.")]
    [SerializeField] private float fireScale = 3f;

    [Tooltip("Optional fixed fire spawn points. If empty, random NavMesh positions are used.")]
    [SerializeField] private Transform[] fireSpawnPoints;

    // ─── Inspector: Phase Timing ──────────────────────────────────────────────

    [Header("Phase Timing")]
    [Tooltip("Minimum seconds after spawn before fire/gas ignites.")]
    [SerializeField] private float fireDelayMin = 15f;

    [Tooltip("Maximum seconds after spawn before fire/gas ignites.")]
    [SerializeField] private float fireDelayMax = 20f;

    [Tooltip("Duration of Phase 1 panic (seconds) before Phase 2 begins.")]
    [SerializeField] private float phase1Duration = 60f;

    // ─── Inspector: Agent Behaviour ───────────────────────────────────────────

    [Header("Agent Behaviour")]
    [Tooltip("Agent movement speed while evacuating (m/s).")]
    [SerializeField] private float agentEvacSpeed = 5.5f;

    [Tooltip("Agent movement speed while wandering (m/s).")]
    [SerializeField] private float agentWanderSpeed = 2f;

    [Tooltip("Safe distance agents keep from fires/gas (m).")]
    [SerializeField] private float fireSafeDistance = 5f;

    [Tooltip("Evacuation Leader perception radius (m).")]
    [SerializeField] private float leaderPerceptionRadius = 20f;

    // ─── Inspector: Audio ─────────────────────────────────────────────────────

    [Header("Audio")]
    [SerializeField] private AudioSource alarmAudioSource;
    [SerializeField, Range(0f, 1f)] private float alarmVolume = 0.3f;

    // ─── Inspector: Exit Points ───────────────────────────────────────────────

    [Header("Exit Points")]
    [Tooltip("Assign 4+ ExitPoint GameObjects. Agents find the nearest one.")]
    [SerializeField] private ExitPoint[] exitPoints;

    // ─── Inspector: Auto-start ────────────────────────────────────────────────

    [Header("Auto-Start")]
    [SerializeField] private bool autoStart = true;

    // ─── Public Properties ────────────────────────────────────────────────────

    public SimPhase CurrentPhase     { get; private set; } = SimPhase.Idle;
    public bool     SimulationActive { get; private set; }
    public int      TotalAgents      => spawnedAgents.Count;
    public int      EvacuatedCount   => evacuatedCount;
    public int      CasualtyCount    => casualtyCount;
    public int      RemainingCount   => TotalAgents - evacuatedCount - casualtyCount;

    /// <summary>Exposed for fire-avoidance in AgentBrain.</summary>
    public float FireSafeDistance => fireSafeDistance;

    // ─── Runtime ──────────────────────────────────────────────────────────────

    private readonly List<AgentBrain>      spawnedAgents   = new List<AgentBrain>();
    private readonly List<EvacuationLeader> leaders        = new List<EvacuationLeader>();
    private readonly List<GameObject>      activeFires     = new List<GameObject>();
    private readonly List<Transform>       gasSourceXforms = new List<Transform>();

    private int   evacuatedCount;
    private int   casualtyCount;

    // ─── Lifecycle ────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;

        if (alarmAudioSource == null)
            alarmAudioSource = GetComponent<AudioSource>();

        // ── Auto-resolve prefab — prefer Worker.prefab (has humanoid Animator) ──
        if (agentPrefab == null)
            agentPrefab = Resources.Load<GameObject>("Worker");

        if (agentPrefab == null)
            agentPrefab = Resources.Load<GameObject>("EvacAgent");

#if UNITY_EDITOR
        if (agentPrefab == null)
        {
            // Search for Worker.prefab first, fall back to EvacAgent.prefab
            string[] guids = UnityEditor.AssetDatabase.FindAssets("Worker t:Prefab");
            if (guids.Length == 0)
                guids = UnityEditor.AssetDatabase.FindAssets("EvacAgent t:Prefab");
            if (guids.Length > 0)
            {
                string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[0]);
                agentPrefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(path);
                Debug.Log($"[EvacSimManager] Auto-located prefab: {path}");
            }
        }
#endif

        // ── Auto-find exit points already in the scene ────────────────────────
        if (exitPoints == null || exitPoints.Length == 0)
            exitPoints = FindObjectsByType<ExitPoint>(FindObjectsSortMode.None);

        Debug.Log($"[EvacSimManager] Awake — prefab={agentPrefab}, exits={exitPoints?.Length}");
    }

    private void Start()
    {
        if (autoStart)
            StartCoroutine(AutoStartRoutine());
    }

    private IEnumerator AutoStartRoutine()
    {
        // Wait two frames for scene to finish initialising.
        yield return null;
        yield return null;
        StartSimulation();
    }

    // ─── Public API ───────────────────────────────────────────────────────────

    /// <summary>Spawns agents and starts Phase 1.</summary>
    public void StartSimulation()
    {
        if (SimulationActive)
        {
            Debug.LogWarning("[EvacSimManager] Simulation already running.");
            return;
        }

        SimulationActive = true;
        evacuatedCount   = 0;
        casualtyCount    = 0;

        SpawnAgents();
        // TriggerBreakout is called by StaggeredSpawnRoutine after all agents are ready.
    }

    /// <summary>Resets all agents, fires and counters.</summary>
    public void ResetSimulation()
    {
        StopAllCoroutines();

        foreach (var a in spawnedAgents)
            if (a != null) Destroy(a.gameObject);
        spawnedAgents.Clear();
        leaders.Clear();

        foreach (var f in activeFires)
            if (f != null) Destroy(f);
        activeFires.Clear();
        gasSourceXforms.Clear();

        AgentBrain.ClearAll();
        CasualtyHandler.Instance?.ResetCasualties();

        foreach (var e in exitPoints)
            e?.ResetQueue();

        SimulationActive = false;
        CurrentPhase     = SimPhase.Idle;
        evacuatedCount   = 0;
        casualtyCount    = 0;

        StopAlarm();
        Debug.Log("[EvacSimManager] Simulation reset.");
    }

    /// <summary>Manually add agents at runtime.</summary>
    public void AddAgents(int count)
    {
        for (int i = 0; i < count; i++)
            TrySpawnOneAgent(AgentRole.Civilian);
    }

    /// <summary>Removes the most recently spawned agents.</summary>
    public void RemoveAgents(int count)
    {
        for (int i = 0; i < count && spawnedAgents.Count > 0; i++)
        {
            int last = spawnedAgents.Count - 1;
            AgentBrain a = spawnedAgents[last];
            spawnedAgents.RemoveAt(last);
            if (a != null) Destroy(a.gameObject);
        }
    }

    // ─── Callbacks from AgentBrain ────────────────────────────────────────────

    /// <summary>Called by AgentBrain when an agent reaches an exit.</summary>
    public void OnAgentEvacuated()
    {
        evacuatedCount++;
        EvacSimMetrics.Instance?.RecordEvacuation(evacuatedCount,
            CurrentPhase >= SimPhase.Phase2_Evacuation ? Time.time - phase2StartTime : 0f);

        if (evacuatedCount + casualtyCount >= TotalAgents)
            CompleteSimulation();
    }

    /// <summary>Called by AgentBrain when an agent becomes a casualty.</summary>
    public void OnAgentBecameCasualty()
    {
        casualtyCount++;
        EvacSimMetrics.Instance?.RecordCasualty(casualtyCount);
    }

    // ─── Spawning ─────────────────────────────────────────────────────────────

    private const int SpawnBatchSize = 20;

    private void SpawnAgents()
    {
        if (agentPrefab == null)
        {
            Debug.LogError("[EvacSimManager] agentPrefab is null — assign the Worker prefab in the Inspector.");
            return;
        }

        if (exitPoints == null || exitPoints.Length == 0)
            exitPoints = FindObjectsByType<ExitPoint>(FindObjectsSortMode.None);

        Vector3 center = spawnCenter != null ? spawnCenter.position : Vector3.zero;

        Debug.Log($"[EvacSimManager] Spawning {agentCount} agents around {center} r={spawnRadius}");

        var roles = new System.Collections.Generic.List<AgentRole>(agentCount);
        for (int i = 0; i < leaderCount;  i++) roles.Add(AgentRole.EvacuationLeader);
        for (int i = 0; i < medicalCount; i++) roles.Add(AgentRole.MedicalTeam);
        int civ = agentCount - leaderCount - medicalCount;
        for (int i = 0; i < civ; i++) roles.Add(AgentRole.Civilian);

        StartCoroutine(StaggeredSpawnRoutine(roles, center));
    }

    private System.Collections.IEnumerator StaggeredSpawnRoutine(
        System.Collections.Generic.List<AgentRole> roles, Vector3 center)
    {
        int spawned = 0;
        int batch   = 0;

        for (int i = 0; i < roles.Count; i++)
        {
            if (TrySpawnOneAgentAt(center, roles[i]))
                spawned++;

            if (++batch >= SpawnBatchSize)
            {
                batch = 0;
                yield return null;
            }
        }

        Debug.Log($"[EvacSimManager] Spawned {spawned}/{roles.Count} agents.");

        float fireDelay = UnityEngine.Random.Range(fireDelayMin, fireDelayMax);
        Debug.Log($"[EvacSimManager] Fire will ignite in {fireDelay:F1}s.");
        StartCoroutine(DelayedBreakoutRoutine(fireDelay));
    }

    private IEnumerator DelayedBreakoutRoutine(float delay)
    {
        yield return new WaitForSeconds(delay);
        TriggerBreakout();
    }

    private bool TrySpawnOneAgent(AgentRole role)
    {
        Vector3 center = spawnCenter != null ? spawnCenter.position : Vector3.zero;
        return TrySpawnOneAgentAt(center, role);
    }

    private bool TrySpawnOneAgentAt(Vector3 center, AgentRole role)
    {
        // Sample a valid NavMesh point — same approach as SimulationManager.cs.
        Vector3 pos = SampleRandomNavMeshPoint(center, spawnRadius);
        if (pos == Vector3.zero)
        {
            Debug.LogWarning($"[EvacSimManager] No NavMesh point found within radius {spawnRadius} of {center}.");
            return false;
        }

        Quaternion rot = Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f);
        // Spawn slightly above ground; Warp snaps to the NavMesh surface.
        GameObject go = Instantiate(agentPrefab, pos + Vector3.up * 0.1f, rot);
        go.name = $"{role}_{spawnedAgents.Count:000}";

        NavMeshAgent nav = go.GetComponent<NavMeshAgent>();
        if (nav == null)
            nav = go.AddComponent<NavMeshAgent>();

        nav.speed            = agentWanderSpeed;
        nav.acceleration     = 8f;
        nav.angularSpeed     = 180f;
        nav.stoppingDistance = 0.1f;
        nav.avoidancePriority = UnityEngine.Random.Range(30, 70);

        // Warp forces the agent onto the NavMesh regardless of spawn height offset.
        if (!nav.Warp(pos))
            Debug.LogWarning($"[EvacSimManager] Warp failed for {go.name} at {pos}");

        AgentBrain brain = go.GetComponent<AgentBrain>() ?? go.AddComponent<AgentBrain>();
        ExitPoint  exit  = FindNearestExit(pos);
        brain.Initialize(role, exit);
        brain.StartWandering();

        spawnedAgents.Add(brain);

        if (role == AgentRole.EvacuationLeader)
        {
            EvacuationLeader leader = go.GetComponent<EvacuationLeader>() ?? go.AddComponent<EvacuationLeader>();
            leader.PerceptionRadius = leaderPerceptionRadius;
            leaders.Add(leader);
        }

        if (role == AgentRole.MedicalTeam)
        {
            PanicBehavior pb = go.GetComponent<PanicBehavior>();
            if (pb != null) pb.enabled = false;
        }

        return true;
    }

    private ExitPoint FindNearestExit(Vector3 from)
    {
        // First try scene-registered exits via ExitPoint.GetAll().
        var allExits = ExitPoint.GetAll();
        if (allExits.Count > 0) return ExitPoint.FindNearest(from);

        // Fall back to the Inspector-assigned array.
        ExitPoint nearest = null;
        float     best    = float.MaxValue;
        foreach (var e in exitPoints)
        {
            if (e == null) continue;
            float d = Vector3.Distance(from, e.transform.position);
            if (d < best) { best = d; nearest = e; }
        }
        return nearest;
    }

    // ─── Breakout & Phase Transitions ─────────────────────────────────────────

    private float phase2StartTime;

    private void TriggerBreakout()
    {
        CurrentPhase = SimPhase.Phase1_Breakout;
        SpawnFires();
        PlayAlarm();

        // Distribute gas-source transforms to AgentBrain static registry.
        foreach (Transform t in gasSourceXforms)
            AgentBrain.RegisterFireSource(t);

        // Tell every agent's PanicBehavior about the breakout.
        Transform[] srcArray = gasSourceXforms.ToArray();
        foreach (AgentBrain a in spawnedAgents)
        {
            if (a == null) continue;
            PanicBehavior pb = a.GetComponent<PanicBehavior>();
            pb?.OnBreakout(srcArray);

            if (a.Role != AgentRole.MedicalTeam)
                a.TriggerPanic();
        }

        Debug.Log("[EvacSimManager] Phase 1 — Gas Breakout triggered.");
        StartCoroutine(Phase1Timer());
    }

    private IEnumerator Phase1Timer()
    {
        yield return new WaitForSeconds(phase1Duration);
        StartPhase2();
    }

    private void StartPhase2()
    {
        CurrentPhase    = SimPhase.Phase2_Evacuation;
        phase2StartTime = Time.time;

        // Activate evacuation leaders.
        foreach (var leader in leaders)
            leader?.ActivatePhase2();

        // Immediately begin evacuating leaders themselves.
        foreach (var leader in leaders)
        {
            var brain = leader.GetComponent<AgentBrain>();
            brain?.StartEvacuating();
        }

        Debug.Log("[EvacSimManager] Phase 2 — Directed Evacuation started.");
    }

    private void CompleteSimulation()
    {
        CurrentPhase = SimPhase.Complete;
        StopAlarm();
        Debug.Log("[EvacSimManager] Simulation complete.");
    }

    // ─── Fire/Gas Spawning ────────────────────────────────────────────────────

    private void SpawnFires()
    {
        if (firePrefab == null)
        {
            // Try to auto-find any particle system or fire prefab in the project.
#if UNITY_EDITOR
            var guids = UnityEditor.AssetDatabase.FindAssets("fire t:Prefab");
            if (guids.Length > 0)
            {
                string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[0]);
                firePrefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(path);
                Debug.Log($"[EvacSimManager] Auto-located fire prefab: {path}");
            }
#endif
            if (firePrefab == null)
            {
                Debug.LogWarning("[EvacSimManager] firePrefab not assigned — skipping fire spawn. Drag a fire/VFX prefab onto EvacSimManager.");
                return;
            }
        }

        Vector3 center  = spawnCenter != null ? spawnCenter.position : Vector3.zero;
        float   radius  = spawnRadius * 0.6f;

        if (fireSpawnPoints != null && fireSpawnPoints.Length > 0)
        {
            // Shuffle and pick fireCount points.
            List<Transform> pool = new List<Transform>(fireSpawnPoints);
            for (int i = 0; i < pool.Count; i++)
            {
                int j = UnityEngine.Random.Range(i, pool.Count);
                (pool[i], pool[j]) = (pool[j], pool[i]);
            }

            int n = Mathf.Min(fireCount, pool.Count);
            for (int i = 0; i < n; i++)
                SpawnFireAt(pool[i].position);
        }
        else
        {
            // Random NavMesh positions.
            for (int i = 0; i < fireCount; i++)
            {
                Vector3 pos = SampleRandomNavMeshPoint(center, radius);
                if (pos != Vector3.zero)
                    SpawnFireAt(pos);
            }
        }
    }

    private void SpawnFireAt(Vector3 pos)
    {
        GameObject fire = Instantiate(firePrefab, pos, Quaternion.identity);
        fire.transform.localScale = Vector3.one * fireScale;

        // Remove any NavMeshObstacle — use steering-only avoidance to keep NavMesh connected.
        NavMeshObstacle obs = fire.GetComponent<NavMeshObstacle>();
        if (obs != null) { obs.carving = false; Destroy(obs); }

        activeFires.Add(fire);
        gasSourceXforms.Add(fire.transform);
    }

    // ─── Audio ────────────────────────────────────────────────────────────────

    private void PlayAlarm()
    {
        if (alarmAudioSource == null) return;
        alarmAudioSource.volume = alarmVolume;
        alarmAudioSource.loop   = true;
        if (!alarmAudioSource.isPlaying) alarmAudioSource.Play();
    }

    private void StopAlarm()
    {
        if (alarmAudioSource != null && alarmAudioSource.isPlaying)
            alarmAudioSource.Stop();
    }

    // ─── Runtime Slider Callbacks ─────────────────────────────────────────────

    /// <summary>Called by DashboardUI — spawns an additional fire at a random NavMesh point.</summary>
    public void TriggerAdditionalFire()
    {
        if (firePrefab == null) return;
        Vector3 center = spawnCenter != null ? spawnCenter.position : Vector3.zero;
        Vector3 pos    = SampleRandomNavMeshPoint(center, spawnRadius * 0.6f);
        if (pos == Vector3.zero) return;
        SpawnFireAt(pos);
        AgentBrain.RegisterFireSource(activeFires[activeFires.Count - 1].transform);
    }

    /// <summary>Called by DashboardUI slider — sets evacuation speed for all active agents.</summary>
    public void SetAgentSpeed(float speed)
    {
        agentEvacSpeed = speed;
        foreach (var a in spawnedAgents)
        {
            if (a == null) continue;
            var nav = a.GetNavAgent();
            if (nav != null && a.CurrentState == AgentBrain.AgentState.Evacuating)
                nav.speed = speed;
        }
    }

    /// <summary>Called by DashboardUI slider — updates leader perception radii.</summary>
    public void SetLeaderPerceptionRadius(float radius)
    {
        leaderPerceptionRadius = radius;
        foreach (var l in leaders)
            if (l != null) l.PerceptionRadius = radius;
    }

    /// <summary>Called by DashboardUI slider — updates fire safe distance.</summary>
    public void SetFireSafeDistance(float dist) => fireSafeDistance = dist;

    /// <summary>Called by DashboardUI slider — updates Phase 1 duration.</summary>
    public void SetPanicDuration(float seconds) => phase1Duration = seconds;

    // ─── NavMesh Helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Samples a random NavMesh point within <paramref name="radius"/> of
    /// <paramref name="center"/>. Mirrors the proven approach in SimulationManager.cs.
    /// </summary>
    private static Vector3 SampleRandomNavMeshPoint(Vector3 center, float radius)
    {
        for (int attempt = 0; attempt < 30; attempt++)
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
