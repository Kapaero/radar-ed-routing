using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.AI;

namespace RadarCrowd
{
    /// <summary>
    /// Drives one simulation run with a fixed time step. All simulation logic is ticked from here in a fixed order
    /// (arrivals, obstacles, sensors, placement, staff, incidents, agents in registration order, metrics), so a run is
    /// reproducible from its configuration and seed.
    /// Command line: -config path/to/run.json -out path/to/output_dir (one run per process).
    /// </summary>
    public class ExperimentRunner : MonoBehaviour
    {
        [SerializeField] RunConfig editorConfig = new RunConfig();
        [SerializeField] string editorOutputDir = "Runs/editor";
        [SerializeField] HospitalLayout layout;
        [SerializeField] SensorHub hub;
        [SerializeField] GameObject patientPrefab;
        [SerializeField] GameObject walkerPrefab;

        static readonly Color CompanionColor = new Color(0.95f, 0.75f, 0.2f);
        static readonly Color NurseColor = new Color(0.2f, 0.75f, 0.35f);
        static readonly Color PhysicianColor = Color.white;
        static readonly Color PorterColor = new Color(0.55f, 0.35f, 0.75f);

        RunConfig cfg;
        string outDir;
        SimContext ctx;
        IncidentManager incidents;
        ClosureSchedule closures;
        List<PatientSpec> patientSchedule;
        List<WalkerSpec> walkerSchedule;
        readonly List<(float time, PatientAgent patient, int order)> delayedCompanions = new List<(float, PatientAgent, int)>();
        int nextPatient;
        int nextWalker;
        GameObject obstacle;
        bool obstacleDone;
        bool finished;
        Transform agentRoot;
        readonly Dictionary<Color, Material> materials = new Dictionary<Color, Material>();
        readonly List<CrowdAgent> tickList = new List<CrowdAgent>();

        public void Configure(HospitalLayout hospitalLayout, SensorHub sensorHub, GameObject patient, GameObject walker)
        {
            layout = hospitalLayout;
            hub = sensorHub;
            patientPrefab = patient;
            walkerPrefab = walker;
        }

        void Awake()
        {
            cfg = LoadConfig(out outDir);
            Time.captureDeltaTime = cfg.dt;
            Time.timeScale = 1f;
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;
            AgentRegistry.Clear();
            CrowdAgent.ResetIds();
        }

        RunConfig LoadConfig(out string outputDir)
        {
            string configPath = Argument("-config");
            outputDir = Argument("-out") ?? editorOutputDir;
            if (string.IsNullOrEmpty(configPath))
                return editorConfig;
            RunConfig loaded = JsonUtility.FromJson<RunConfig>(File.ReadAllText(configPath));
            if (Argument("-out") == null)
                outputDir = Path.Combine("Runs", loaded.runId);
            return loaded;
        }

        static string Argument(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == name)
                    return args[i + 1];
            return null;
        }

        System.Collections.IEnumerator Start()
        {
            if (cfg.corridorClearWidth > 0f)
            {
                // narrow corridors: line the walls first and let the NavMesh be re-carved before routes and spots are built
                layout.LineCorridors(cfg.corridorClearWidth);
                yield return null;
                yield return null;
            }
            SimClock.Reset();
            agentRoot = new GameObject("Agents").transform;
            layout.Initialize(cfg.maxDetourRatio, cfg.maxRoutes, cfg.trolleyWidth, cfg.trolleyLength);
            var metrics = new MetricsRecorder(cfg, outDir);
            hub.Init(layout, cfg.Condition, cfg.radar, cfg.Mount, cfg.latencySec, cfg.lossProb, cfg.seed, 0f);
            hub.SetCalibration(cfg.calibrationX, cfg.calibrationY);
            metrics.Bind(layout, hub);
            var router = new Router(layout, hub, cfg.Condition, cfg.hysteresis, cfg.keepClearDensity, 2f * cfg.stretcherRadius, metrics);
            ctx = new SimContext
            {
                Cfg = cfg,
                Ed = EdInputs.Load(cfg.edInputsPath),
                Layout = layout,
                Hospital = new HospitalState(layout, router),
                Router = router,
                Hub = hub,
                Metrics = metrics,
                Runner = this,
            };
            ctx.Staff = new StaffDispatcher(ctx);
            ctx.Hospital.BindStaff(ctx.Staff);
            ctx.Hospital.SetTriageDesks(cfg.triageNurses);
            metrics.BindContext(ctx);

            int spaces = layout.Cubicles.Count;
            float meanRate = cfg.MeanArrivalsPerHour(spaces);
            SpawnStaff(spaces, meanRate);
            incidents = new IncidentManager(ctx, meanRate);

            patientSchedule = ScheduleGenerator.Patients(cfg, ctx.Ed, ctx.StreamFor("patients"), spaces);
            List<PatientSpec> initial = ScheduleGenerator.InitialPatients(cfg, ctx.Ed, ctx.StreamFor("initial-patients"), spaces, 100000);
            if (cfg.boardInCorridor)
                initial.AddRange(ScheduleGenerator.InitialBoarders(cfg, ctx.Ed, ctx.StreamFor("initial-boarders"), spaces, 200000));
            walkerSchedule = ScheduleGenerator.Walkers(cfg, layout.WalkerEndpoints.Count, ctx.StreamFor("walkers"));
            closures = new ClosureSchedule(cfg, layout.Cells, ctx.StreamFor("closures"));
            metrics.WriteSchedules(patientSchedule, initial, walkerSchedule);
            metrics.WriteClosures(closures.Closures);
            metrics.WriteRoutes();
            if (cfg.corridorTrolleys > 0)
                metrics.WriteCorridorCare(CorridorCare.Place(layout, cfg, ctx.StreamFor("corridor-care"), agentRoot));
            SpawnInitial(initial);

            Debug.Log($"RadarCrowd run {cfg.runId}: {cfg.condition}, {meanRate:F2} arrivals/h (mean), {patientSchedule.Count} arrivals, " +
                      $"{initial.Count} initial patients, staff {ctx.Staff.Nurses.Count}N/{ctx.Staff.Physicians.Count}P/{ctx.Staff.Porters.Count}T, " +
                      $"{layout.Spots.Count} corridor spots, {hub.Radars.Count} radars, output {outDir}");
        }

        void SpawnStaff(int spaces, float meanRate)
        {
            int nurses = Mathf.CeilToInt(spaces / cfg.patientsPerNurse);
            int physicians = Mathf.Max(1, Mathf.CeilToInt(meanRate * ctx.Ed.MaxRelative() / cfg.patientsPerPhysicianHour));
            for (int i = 0; i < nurses; i++) SpawnStaffMember(StaffRole.Nurse, i, NurseColor);
            for (int i = 0; i < physicians; i++) SpawnStaffMember(StaffRole.Physician, i, PhysicianColor);
            for (int i = 0; i < cfg.porters; i++) SpawnStaffMember(StaffRole.Porter, i, PorterColor);
            ctx.Staff.AssignNurseCubicles(layout.Cubicles);
        }

        void SpawnStaffMember(StaffRole role, int number, Color color)
        {
            SeededRandom rng = ctx.StreamFor("staff-" + role + "-" + number);
            Vector3 position = RouteBook.Snap(layout.StaffBasePos + new Vector3(rng.Range(-1.5f, 1.5f), 0f, rng.Range(-1.5f, 1.5f)));
            StaffAgent staff = CreateAgent<StaffAgent>(role + " " + number, position, color);
            staff.Init(role, number, ctx, (float)rng.TruncatedNormal(Weidmann.FreeSpeed, Weidmann.FreeSpeedSd, 0.6, 2.0));
            ctx.Staff.Add(staff);
        }

        void SpawnInitial(List<PatientSpec> initial)
        {
            foreach (PatientSpec spec in initial)
            {
                if (spec.InitialBoarder)
                {
                    CorridorSpot spot = ctx.Hospital.ClaimSpot(null);
                    if (spot == null) continue;
                    GameObject b = Instantiate(patientPrefab, spot.StandPosition, Quaternion.identity, agentRoot);
                    b.name = "Patient " + spec.Index + " (boarding in corridor)";
                    var boarder = b.GetComponent<PatientAgent>();
                    boarder.InitBoardingInCorridor(spec, spot, ctx, 0f);
                    for (int k = 0; k < spec.Companions; k++)
                        SpawnCompanion(boarder, k, spot.StandPosition, 0f);
                    continue;
                }
                Cubicle cubicle = null;
                foreach (int index in spec.CubicleOrder)
                    if (layout.Cubicles[index].IsFree) { cubicle = layout.Cubicles[index]; break; }
                if (cubicle == null) break;
                GameObject go = Instantiate(patientPrefab, cubicle.Position, Quaternion.identity, agentRoot);
                go.name = "Patient " + spec.Index + " (initial)";
                var patient = go.GetComponent<PatientAgent>();
                patient.InitInCubicle(spec, cubicle, ctx, 0f);
                for (int k = 0; k < spec.Companions; k++)
                    SpawnCompanion(patient, k, cubicle.Position, 0f);
            }
        }

        void Update()
        {
            if (finished || ctx == null)
                return;
            SimClock.Update();
            float now = SimClock.Now;

            while (nextPatient < patientSchedule.Count && patientSchedule[nextPatient].Time <= now)
                SpawnPatient(patientSchedule[nextPatient++], now);
            for (int i = delayedCompanions.Count - 1; i >= 0; i--)
            {
                (float time, PatientAgent patient, int order) = delayedCompanions[i];
                if (time > now) continue;
                delayedCompanions.RemoveAt(i);
                if (patient != null)
                    SpawnCompanion(patient, order, layout.EntrancePos, now);
            }
            while (nextWalker < walkerSchedule.Count && walkerSchedule[nextWalker].Time <= now)
                SpawnWalker(walkerSchedule[nextWalker++], now);
            UpdateObstacle(now);
            closures.Tick(now, FindCell, ctx.Metrics);

            hub.Tick(now);
            ctx.Hospital.Tick(now);
            ctx.Staff.Tick(now);
            incidents.Tick(now, AgentRegistry.Agents);
            tickList.Clear();
            tickList.AddRange(AgentRegistry.Agents);
            foreach (CrowdAgent agent in tickList)
            {
                if (agent == null) continue;
                switch (agent)
                {
                    case PatientAgent p: p.Tick(now); break;
                    case CompanionAgent c: c.Tick(now); break;
                    case StaffAgent s: s.Tick(now); break;
                    case WalkerAgent w: w.Tick(now); break;
                }
            }
            ctx.Metrics.Tick(now);

            if (now >= cfg.durationMin * 60f)
                Finish(now);
        }

        void SpawnPatient(PatientSpec spec, float now)
        {
            bool ambulance = spec.Ambulance && layout.HasStretcherNavMesh;
            Vector3 position = ambulance ? layout.AmbulanceEntrancePos : layout.EntrancePos;
            GameObject go = Instantiate(patientPrefab, position, Quaternion.identity, agentRoot);
            go.name = "Patient " + spec.Index + (spec.Critical ? " (critical)" : "") + (ambulance ? " (ambulance)" : "");
            var patient = go.GetComponent<PatientAgent>();
            patient.InitArrival(spec, ctx, now);
            for (int k = 0; k < spec.Companions; k++)
            {
                if (ambulance)
                    delayedCompanions.Add((now + spec.CompanionDelayMin * 60f, patient, k));
                else
                    SpawnCompanion(patient, k, layout.EntrancePos, now);
            }
        }

        void SpawnCompanion(PatientAgent patient, int order, Vector3 near, float now)
        {
            SeededRandom rng = ctx.StreamFor("companion-speed-" + patient.Spec.Index + "-" + order);
            Vector3 position = RouteBook.Snap(near + new Vector3(rng.Range(-1f, 1f), 0f, rng.Range(-1f, 1f)));
            CompanionAgent companion = CreateAgent<CompanionAgent>("Companion " + patient.Spec.Index + "-" + order, position, CompanionColor);
            companion.Init(patient, order, ctx, (float)rng.TruncatedNormal(Weidmann.FreeSpeed, Weidmann.FreeSpeedSd, 0.6, 2.0), now);
        }

        void SpawnWalker(WalkerSpec spec, float now)
        {
            Vector3 origin = layout.WalkerEndpoints[spec.Origin];
            Vector3 destination = layout.WalkerEndpoints[spec.Destination];
            GameObject go = Instantiate(walkerPrefab, origin, Quaternion.identity, agentRoot);
            go.name = "Walker " + spec.Index;
            go.GetComponent<WalkerAgent>().Init(spec, origin, destination, ctx.Metrics, now);
        }

        T CreateAgent<T>(string name, Vector3 position, Color color) where T : CrowdAgent
        {
            var go = new GameObject(name);
            go.transform.SetParent(agentRoot, false);
            go.transform.position = position;
            go.AddComponent<NavMeshAgent>();
            T agent = go.AddComponent<T>();
            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            Destroy(body.GetComponent<Collider>());
            body.transform.SetParent(go.transform, false);
            body.transform.localPosition = new Vector3(0f, 0.875f, 0f);
            body.transform.localScale = new Vector3(2f * CrowdAgent.BodyRadius, 0.875f, 2f * CrowdAgent.BodyRadius);
            body.GetComponent<MeshRenderer>().sharedMaterial = MaterialFor(color);
            return agent;
        }

        Material MaterialFor(Color color)
        {
            if (materials.TryGetValue(color, out Material m))
                return m;
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            m = shader != null ? new Material(shader) : new Material(Shader.Find("Standard"));
            m.SetColor("_BaseColor", color);
            m.color = color;
            materials[color] = m;
            return m;
        }

        void UpdateObstacle(float now)
        {
            if (cfg.obstacleScenario == "O0" || obstacleDone)
                return;
            if (obstacle == null && now >= cfg.obstacleStartMin * 60f)
            {
                CorridorCell cell = FindCell(cfg.obstacleCell);
                obstacle = cell != null ? ObstacleScenario.Spawn(cfg.obstacleScenario, cell) : null;
                ctx.Metrics.LogEvent(now, "obstacle_on", -1, cfg.obstacleScenario + " " + cfg.obstacleCell, obstacle != null ? obstacle.transform.position : Vector3.zero);
                if (obstacle == null) obstacleDone = true;
            }
            else if (obstacle != null && now >= cfg.obstacleEndMin * 60f)
            {
                ctx.Metrics.LogEvent(now, "obstacle_off", -1, cfg.obstacleScenario + " " + cfg.obstacleCell, obstacle.transform.position);
                Destroy(obstacle);
                obstacle = null;
                obstacleDone = true;
            }
        }

        CorridorCell FindCell(string id)
        {
            foreach (CorridorCell c in layout.Cells)
                if (c.CellId == id) return c;
            Debug.LogWarning("Unknown obstacle cell " + id);
            return null;
        }

        void Finish(float now)
        {
            finished = true;
            ctx.Metrics.Finish(now, incidents.Count, incidents.Skipped);
            Debug.Log($"RadarCrowd run {cfg.runId} finished at t={now:F1} s, realtime {Time.realtimeSinceStartup:F1} s");
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
