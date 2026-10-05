using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;

namespace RadarCrowd
{
    /// <summary>All factors of one validation run, loaded from JSON (JsonUtility) passed with -config.</summary>
    [Serializable]
    public class ValidationConfig
    {
        public string runId = "uo-180-180-180";
        public string trajectoryPath = "Data/external/juelich/uo/uo-180-180-180.txt";   // id frame x y z (cm)
        public float frameRate = 16f;          // fps of the trajectory file (Jülich data archive, DOI 10.34735/ped.2009.14)
        public float corridorWidth = 1.8f;     // b_cor (m); the corridor spans x = 0 .. b_cor
        public float exitWidth = 1.8f;         // b_exit (m), centred on the corridor axis
        public float corridorEntryY = 4f;      // m, end of the 4 m passage = start of the corridor (from the trajectories)
        public float exitY = -4f;              // m, end of the corridor with the exit
        public float passageEntryY = 8f;       // m, start of the 4 m passage
        public float passageLeftX = -0.73f;    // m, passage walls (outermost positions in the trajectories +- 0.25 m)
        public float passageRightX = 2.68f;
        public float speedMean = Weidmann.FreeSpeed;   // m/s, free speeds: Weidmann (1993) as in the hospital runs,
        public float speedSd = Weidmann.FreeSpeedSd;   // or the participants' own distribution (sensitivity run)
        public float speedMin = 0.6f;
        public float speedMax = 2.0f;
        public float dt = 0.04f;               // s, fixed step as in the hospital runs
        public int seed = 1;
        public float maxDurationSec = 900f;
    }

    /// <summary>
    /// Validation of the pedestrian model against the Jülich unidirectional corridor experiments (Düsseldorf 2009, open
    /// boundary; Zhang et al. 2011, J. Stat. Mech. P06004). The geometry of one run (4 m passage, corridor of width b_cor
    /// from y = 4 m to y = -4 m, exit of width b_exit) is built from colliders and baked at start-up. Every participant of
    /// the run is replaced by a NavMesh agent with the settings of the hospital simulation: it appears at the time and
    /// place where the participant entered the camera view and walks straight on through the exit, with a free speed
    /// drawn from Weidmann's distribution (nothing is fitted to the experiment; a sensitivity run uses the participants' own free speeds). The trajectories are written in the
    /// format of the experiment, so that both go through the same Voronoi analysis (Tools/juelich_validation.py).
    /// Command line: -config validation.json -out output_dir (one run per process).
    /// </summary>
    public class CorridorValidation : MonoBehaviour
    {
        const float WallThickness = 0.1f;
        const float WallHeight = 2f;
        const float OutflowLength = 5f;      // m of open floor behind the exit
        const float OutflowHalfWidth = 4f;
        const float DespawnBehindExit = 3f;  // m behind the exit; the cameras tracked people to about 2 m behind it
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        [SerializeField] ValidationConfig editorConfig = new ValidationConfig();
        [SerializeField] string editorOutputDir = "Runs/validation_editor";

        struct Entry
        {
            public int Id;
            public float Time;
            public Vector3 Position;
        }

        ValidationConfig cfg;
        string outDir;
        readonly List<Entry> entries = new List<Entry>();
        readonly List<ValidationAgent> active = new List<ValidationAgent>();
        readonly StringBuilder trajectories = new StringBuilder();
        readonly StringBuilder agents = new StringBuilder("id,t_spawn,speed,t_exit,t_despawn,walked_m\n");
        SeededRandom speeds;
        Transform agentRoot;
        int step;
        int next;
        bool finished;

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

        ValidationConfig LoadConfig(out string outputDir)
        {
            string configPath = Argument("-config");
            outputDir = Argument("-out") ?? editorOutputDir;
            if (string.IsNullOrEmpty(configPath))
                return editorConfig;
            ValidationConfig loaded = JsonUtility.FromJson<ValidationConfig>(File.ReadAllText(configPath));
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

        void Start()
        {
            Directory.CreateDirectory(outDir);
            BuildGeometry();
            LoadEntries();
            speeds = SeededRandom.For(cfg.seed, "validation-speeds");
            agentRoot = new GameObject("Agents").transform;
            trajectories.Append("# description: RadarCrowd NavMesh replay of the Juelich run ").Append(cfg.runId).Append('\n')
                .Append("# framerate: ").Append((1f / cfg.dt).ToString("0.###", Inv)).Append('\n')
                .Append("# unit: cm\n# ID frame x/cm y/cm z/cm\n");
            Debug.Log($"CorridorValidation {cfg.runId}: b_cor {cfg.corridorWidth} m, b_exit {cfg.exitWidth} m, {entries.Count} participants, output {outDir}");
        }

        // ------------------------------------------------------------------ geometry

        void BuildGeometry()
        {
            var root = new GameObject("Geometry").transform;
            float b = cfg.corridorWidth, cx = 0.5f * b, t = WallThickness;
            float pl = Mathf.Min(cfg.passageLeftX, 0f), pr = Mathf.Max(cfg.passageRightX, b);
            float back = cfg.passageEntryY + 0.5f;

            Slab(root, "Floor passage", pl, pr, cfg.corridorEntryY, back);
            Slab(root, "Floor corridor", 0f, b, cfg.exitY, cfg.corridorEntryY);
            Slab(root, "Floor outflow", cx - OutflowHalfWidth, cx + OutflowHalfWidth, cfg.exitY - OutflowLength, cfg.exitY);

            Wall(root, "Corridor wall left", -t, 0f, cfg.exitY, cfg.corridorEntryY);
            Wall(root, "Corridor wall right", b, b + t, cfg.exitY, cfg.corridorEntryY);
            Wall(root, "Passage wall left", pl - t, pl, cfg.corridorEntryY, back + t);
            Wall(root, "Passage wall right", pr, pr + t, cfg.corridorEntryY, back + t);
            Wall(root, "Passage back wall", pl - t, pr + t, back, back + t);
            if (pl < 0f)
                Wall(root, "Corridor entry left", pl - t, 0f, cfg.corridorEntryY, cfg.corridorEntryY + t);
            if (pr > b)
                Wall(root, "Corridor entry right", b, pr + t, cfg.corridorEntryY, cfg.corridorEntryY + t);
            float half = 0.5f * Mathf.Min(cfg.exitWidth, b);
            if (half < 0.5f * b)
            {
                Wall(root, "Exit wall left", -t, cx - half, cfg.exitY - t, cfg.exitY);
                Wall(root, "Exit wall right", cx + half, b + t, cfg.exitY - t, cfg.exitY);
            }

            var surface = root.gameObject.AddComponent<NavMeshSurface>();
            surface.agentTypeID = 0;
            surface.collectObjects = CollectObjects.Children;
            surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            surface.overrideVoxelSize = true;
            surface.voxelSize = 0.05f;
            Physics.SyncTransforms();
            surface.BuildNavMesh();
        }

        static void Slab(Transform root, string name, float x0, float x1, float z0, float z1)
        {
            Box(root, name, x0, x1, -0.1f, 0f, z0, z1);
        }

        static void Wall(Transform root, string name, float x0, float x1, float z0, float z1)
        {
            Box(root, name, x0, x1, 0f, WallHeight, z0, z1);
        }

        static void Box(Transform root, string name, float x0, float x1, float y0, float y1, float z0, float z1)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(root, false);
            go.transform.localPosition = new Vector3(0.5f * (x0 + x1), 0.5f * (y0 + y1), 0.5f * (z0 + z1));
            go.transform.localScale = new Vector3(x1 - x0, y1 - y0, z1 - z0);
        }

        // ------------------------------------------------------------------ participants

        /// <summary>First appearance (frame and position) of every participant; times relative to the first frame of the run.</summary>
        void LoadEntries()
        {
            var first = new Dictionary<int, (int frame, float x, float y)>();
            int firstFrame = int.MaxValue;
            foreach (string line in File.ReadLines(cfg.trajectoryPath))
            {
                if (line.Length == 0 || line[0] == '#')
                    continue;
                string[] p = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 4)
                    continue;
                int id = int.Parse(p[0], Inv);
                int frame = (int)Math.Round(double.Parse(p[1], Inv));
                float x = float.Parse(p[2], Inv) / 100f, y = float.Parse(p[3], Inv) / 100f;
                if (!first.TryGetValue(id, out var f) || frame < f.frame)
                    first[id] = (frame, x, y);
                firstFrame = Math.Min(firstFrame, frame);
            }
            foreach (KeyValuePair<int, (int frame, float x, float y)> kv in first)
                entries.Add(new Entry { Id = kv.Key, Time = (kv.Value.frame - firstFrame) / cfg.frameRate, Position = new Vector3(kv.Value.x, 0f, kv.Value.y) });
            entries.Sort((a, c) => a.Time != c.Time ? a.Time.CompareTo(c.Time) : a.Id.CompareTo(c.Id));
        }

        void Spawn(Entry e, float now)
        {
            Vector3 position = e.Position;
            if (NavMesh.SamplePosition(position, out NavMeshHit hit, 1.5f, NavMesh.AllAreas))
                position = hit.position;
            var go = new GameObject("Participant " + e.Id);
            go.transform.SetParent(agentRoot, false);
            go.transform.SetPositionAndRotation(position, Quaternion.LookRotation(Vector3.back));
            go.AddComponent<NavMeshAgent>();
            var agent = go.AddComponent<ValidationAgent>();
            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            Destroy(body.GetComponent<Collider>());
            body.transform.SetParent(go.transform, false);
            body.transform.localPosition = new Vector3(0f, 0.875f, 0f);
            body.transform.localScale = new Vector3(2f * CrowdAgent.BodyRadius, 0.875f, 2f * CrowdAgent.BodyRadius);
            float speed = (float)speeds.TruncatedNormal(cfg.speedMean, cfg.speedSd, cfg.speedMin, cfg.speedMax);
            // straight on: the goal lies behind the exit at the lateral position of the entry (the path bends through a narrow exit)
            agent.Init(e.Id, speed, new Vector3(position.x, 0f, cfg.exitY - OutflowLength + 0.5f), now);
            active.Add(agent);
        }

        // ------------------------------------------------------------------ simulation step

        void Update()
        {
            if (finished)
                return;
            float now = step * cfg.dt;
            while (next < entries.Count && entries[next].Time <= now + 1e-4f)
                Spawn(entries[next++], now);

            for (int i = active.Count - 1; i >= 0; i--)
            {
                ValidationAgent a = active[i];
                a.Tick();
                Vector3 p = a.transform.position;
                if (a.TExit < 0f && p.z < cfg.exitY)
                    a.TExit = now;
                if (p.z < cfg.exitY - DespawnBehindExit)
                {
                    agents.Append(string.Join(",", a.SourceId, F(a.TSpawn), F(a.DesiredSpeed), F(a.TExit), F(now), F(a.WalkedDistance))).Append('\n');
                    active.RemoveAt(i);
                    Destroy(a.gameObject);
                    continue;
                }
                trajectories.Append(a.SourceId).Append(' ').Append(step).Append(' ')
                    .Append((p.x * 100f).ToString("0.##", Inv)).Append(' ').Append((p.z * 100f).ToString("0.##", Inv)).Append(" 175\n");
            }

            if ((next >= entries.Count && active.Count == 0) || now >= cfg.maxDurationSec)
                Finish(now);
            step++;
        }

        static string F(float v) => v.ToString("0.###", Inv);

        void Finish(float now)
        {
            finished = true;
            foreach (ValidationAgent a in active)
                agents.Append(string.Join(",", a.SourceId, F(a.TSpawn), F(a.DesiredSpeed), F(a.TExit), "", F(a.WalkedDistance))).Append('\n');
            File.WriteAllText(Path.Combine(outDir, "sim_trajectories.txt"), trajectories.ToString());
            File.WriteAllText(Path.Combine(outDir, "agents.csv"), agents.ToString());
            Debug.Log($"CorridorValidation {cfg.runId} finished at t={now:F1} s ({active.Count} agents still inside), realtime {Time.realtimeSinceStartup:F1} s");
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
