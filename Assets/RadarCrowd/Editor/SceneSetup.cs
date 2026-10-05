using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace RadarCrowd.EditorTools
{
    /// <summary>
    /// One-time conversion of the original hospital scene into the RadarCrowd scene: removes the old GOAP/experiment
    /// components and objects, re-bakes the NavMesh for 0.25 m agents, adds the corridor graph, the corridor cells,
    /// the sensor hub and the experiment runner, and creates the agent prefabs.
    /// Batch mode: Unity -batchmode -quit -projectPath ... -executeMethod RadarCrowd.EditorTools.SceneSetup.Run
    /// </summary>
    public static class SceneSetup
    {
        const string SourceScene = "Assets/Scenes/SampleScene.unity";
        const string TargetScene = "Assets/RadarCrowd/Scenes/Hospital.unity";
        const string NavMeshAssetPath = "Assets/RadarCrowd/Scenes/Hospital_NavMesh.asset";
        const string PrefabDir = "Assets/RadarCrowd/Prefabs";
        const float AgentRadius = 0.25f;   // m, equals CrowdAgent.BodyRadius

        static readonly string[] ObsoleteObjects = { "ExperimentRunner", "Updte world", "debugger", "Spawn", "Canvas", "EventSystem" };
        static readonly HashSet<string> KeptLegacyScripts = new HashSet<string> { "TopDownCameraController" };

        [MenuItem("RadarCrowd/Build hospital scene")]
        public static void Run()
        {
            Scene scene = EditorSceneManager.OpenScene(SourceScene);
            RemoveObsolete(scene);
            SetAgentRadius(AgentRadius);
            RebakeNavMesh();

            var root = new GameObject("RadarCrowd");
            HospitalLayout layout = BuildLayout(root.transform);
            var hub = new GameObject("Sensors").AddComponent<SensorHub>();
            hub.transform.SetParent(root.transform, false);
            var runner = new GameObject("Runner").AddComponent<ExperimentRunner>();
            runner.transform.SetParent(root.transform, false);
            (GameObject patient, GameObject walker) = BuildPrefabs();
            runner.Configure(layout, hub, patient, walker);
            EditorUtility.SetDirty(runner);

            Directory.CreateDirectory(Path.GetDirectoryName(TargetScene));
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, TargetScene);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(TargetScene, true) };
            AssetDatabase.SaveAssets();
            Debug.Log("SceneSetup: written " + TargetScene + ", agent radius " + NavMesh.GetSettingsByID(0).agentRadius);
        }

        static void RemoveObsolete(Scene scene)
        {
            var all = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Transform>(true)).Select(t => t.gameObject).ToList();
            foreach (GameObject go in all)
            {
                if (go == null)
                    continue;
                if (ObsoleteObjects.Contains(go.name) || IsOldPatientInstance(go))
                {
                    Object.DestroyImmediate(go);
                    continue;
                }
                foreach (MonoBehaviour mb in go.GetComponents<MonoBehaviour>())
                {
                    if (mb == null)
                        continue;
                    System.Type type = mb.GetType();
                    if (type.Assembly.GetName().Name == "Assembly-CSharp" && type.Namespace != "RadarCrowd" && !KeptLegacyScripts.Contains(type.Name))
                        Object.DestroyImmediate(mb);
                }
                GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go);
            }
        }

        static bool IsOldPatientInstance(GameObject go)
        {
            if (!PrefabUtility.IsAnyPrefabInstanceRoot(go))
                return false;
            string path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
            return path != null && path.StartsWith("Assets/GOAP/Patient");
        }

        static void SetAgentRadius(float radius)
        {
            Object settingsAsset = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/NavMeshAreas.asset")[0];
            var so = new SerializedObject(settingsAsset);
            SerializedProperty settings = so.FindProperty("m_Settings");
            for (int i = 0; i < settings.arraySize; i++)
            {
                SerializedProperty s = settings.GetArrayElementAtIndex(i);
                if (s.FindPropertyRelative("agentTypeID").intValue == 0)
                    s.FindPropertyRelative("agentRadius").floatValue = radius;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void RebakeNavMesh()
        {
            NavMeshSurface[] surfaces = Object.FindObjectsByType<NavMeshSurface>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (surfaces.Length != 1)
                Debug.LogWarning("SceneSetup: expected one NavMeshSurface, found " + surfaces.Length);
            foreach (NavMeshSurface surface in surfaces)
            {
                surface.RemoveData();
                surface.BuildNavMesh();
                NavMeshData data = surface.navMeshData;
                Directory.CreateDirectory(Path.GetDirectoryName(NavMeshAssetPath));
                AssetDatabase.CreateAsset(data, NavMeshAssetPath);
                surface.navMeshData = data;
                surface.AddData();
                EditorUtility.SetDirty(surface);
            }
        }

        static HospitalLayout BuildLayout(Transform root)
        {
            var layoutGo = new GameObject("Layout");
            layoutGo.transform.SetParent(root, false);
            var layout = layoutGo.AddComponent<HospitalLayout>();

            // corridor graph (junctions measured on the NavMesh; see Docs/radar_spec)
            var nodesRoot = new GameObject("Nodes").transform;
            nodesRoot.SetParent(layoutGo.transform, false);
            CorridorNode Node(string id, float x, float z, bool entry, bool walkers)
            {
                var go = new GameObject("Node " + id);
                go.transform.SetParent(nodesRoot, false);
                go.transform.position = new Vector3(x, 0f, z);
                var n = go.AddComponent<CorridorNode>();
                n.Configure(id, entry, walkers);
                return n;
            }
            CorridorNode ja = Node("JA", 61.7f, -27.9f, true, true);
            CorridorNode jb = Node("JB", 83.0f, -28.1f, true, true);
            CorridorNode ma = Node("MA", 61.7f, -51.5f, false, true);
            CorridorNode mb = Node("MB", 83.0f, -51.5f, false, true);
            CorridorNode sa = Node("SA", 61.7f, -82.6f, false, true);
            CorridorNode sb = Node("SB", 83.0f, -82.6f, false, true);
            CorridorNode ww = Node("WW", 45.0f, -27.9f, false, true);   // walker end points on the top corridor
            CorridorNode we = Node("WE", 89.0f, -28.1f, false, true);
            ja.SetNeighbours(new[] { jb, ma });
            jb.SetNeighbours(new[] { ja, mb });
            ma.SetNeighbours(new[] { ja, mb, sa });
            mb.SetNeighbours(new[] { jb, ma, sb });
            sa.SetNeighbours(new[] { ma, sb });
            sb.SetNeighbours(new[] { mb, sa });
            CorridorNode[] nodes = { ja, jb, ma, mb, sa, sb, ww, we };
            foreach (CorridorNode n in nodes) EditorUtility.SetDirty(n);

            // corridor cells: physical corridor = NavMesh walkable band (baked with r = 0.5 m) widened by 2 x 0.5 m
            var cellsRoot = new GameObject("Cells").transform;
            cellsRoot.SetParent(layoutGo.transform, false);
            var cells = new List<CorridorCell>();
            void Cell(string id, string segment, float x, float z, float width, float length, float yaw)
            {
                var go = new GameObject("Cell " + id);
                go.transform.SetParent(cellsRoot, false);
                go.transform.SetPositionAndRotation(new Vector3(x, 0f, z), Quaternion.Euler(0f, yaw, 0f));
                var c = go.AddComponent<CorridorCell>();
                c.Configure(id, segment, width, length);
                EditorUtility.SetDirty(c);
                cells.Add(c);
            }
            // top corridor between the A and B junctions (runs along +x)
            Cell("W2-1", "W2", 68.15f, -27.9f, 4.8f, 8.5f, 90f);
            Cell("W2-2", "W2", 76.65f, -28.1f, 3.7f, 8.5f, 90f);
            // corridor A (x 59.5-63.9) and B (x 80.9-85.1), running south (forward = -z)
            float[] upper = { -35.675f, -45.025f };
            float[] lower = { -57.9f, -67.1f, -76.3f };
            for (int i = 0; i < upper.Length; i++) Cell("A1-" + (i + 1), "A1", 61.7f, upper[i], 4.4f, 9.35f, 180f);
            for (int i = 0; i < lower.Length; i++) Cell("A2-" + (i + 1), "A2", 61.7f, lower[i], 4.4f, 9.2f, 180f);
            for (int i = 0; i < upper.Length; i++) Cell("B1-" + (i + 1), "B1", 83.0f, upper[i], 4.2f, 9.35f, 180f);
            for (int i = 0; i < lower.Length; i++) Cell("B2-" + (i + 1), "B2", 83.0f, lower[i], 4.2f, 9.2f, 180f);
            // middle and bottom cross corridors (run along +x)
            Cell("M-1", "M", 68.15f, -51.5f, 3.6f, 8.5f, 90f);
            Cell("M-2", "M", 76.65f, -51.5f, 3.6f, 8.5f, 90f);
            Cell("C-1", "C", 68.15f, -82.6f, 3.4f, 8.5f, 90f);
            Cell("C-2", "C", 76.65f, -82.6f, 3.4f, 8.5f, 90f);

            layout.Configure(FindTagged("Entrance"), FindTagged("WaitingAria"), FindTagged("Wing"), nodes, cells.ToArray());
            EditorUtility.SetDirty(layout);
            return layout;
        }

        static Transform FindTagged(string tag)
        {
            GameObject go = GameObject.FindWithTag(tag);
            if (go == null)
                throw new System.InvalidOperationException("SceneSetup: no object tagged " + tag);
            return go.transform;
        }

        static (GameObject, GameObject) BuildPrefabs()
        {
            Directory.CreateDirectory(PrefabDir);
            GameObject patient = MakeAgentPrefab("Patient", new Color(0.15f, 0.45f, 0.95f), typeof(PatientAgent));
            GameObject walker = MakeAgentPrefab("Walker", new Color(0.6f, 0.6f, 0.6f), typeof(WalkerAgent));
            return (patient, walker);
        }

        static GameObject MakeAgentPrefab(string name, Color color, System.Type agentType)
        {
            var root = new GameObject(name);
            NavMeshAgent nav = root.AddComponent<NavMeshAgent>();
            nav.radius = AgentRadius;
            nav.height = 1.75f;
            root.AddComponent(agentType);

            GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            Object.DestroyImmediate(visual.GetComponent<Collider>());
            visual.name = "Body";
            visual.transform.SetParent(root.transform, false);
            visual.transform.localPosition = new Vector3(0f, 0.875f, 0f);
            visual.transform.localScale = new Vector3(2f * AgentRadius, 0.875f, 2f * AgentRadius);
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader != null)
            {
                var material = new Material(shader);
                material.SetColor("_BaseColor", color);
                AssetDatabase.CreateAsset(material, PrefabDir + "/" + name + ".mat");
                visual.GetComponent<MeshRenderer>().sharedMaterial = material;
            }
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabDir + "/" + name + ".prefab");
            Object.DestroyImmediate(root);
            return prefab;
        }
    }
}
