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
    /// Adds the stretcher agent type (radius 0.6 m, i.e. a stretcher needs 1.2 m of free width), bakes its NavMesh with
    /// the same sources as the pedestrian NavMesh, and sets the ambulance entrance (the EmergencyArea marker nearest to the
    /// wing entry). Batch mode: -executeMethod RadarCrowd.EditorTools.StretcherSetup.Run
    /// </summary>
    public static class StretcherSetup
    {
        const string ScenePath = "Assets/RadarCrowd/Scenes/Hospital.unity";
        const string NavMeshAssetPath = "Assets/RadarCrowd/Scenes/Hospital_NavMesh_Stretcher.asset";
        const string AgentTypeName = "Stretcher";
        const float StretcherRadius = 0.6f;

        [MenuItem("RadarCrowd/Add stretcher NavMesh")]
        public static void Run()
        {
            int typeId = EnsureAgentType(AgentTypeName, StretcherRadius);
            Scene scene = EditorSceneManager.OpenScene(ScenePath);
            NavMeshSurface[] surfaces = Object.FindObjectsByType<NavMeshSurface>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            NavMeshSurface walking = surfaces.First(s => s.agentTypeID == 0);
            NavMeshSurface stretcher = surfaces.FirstOrDefault(s => s.agentTypeID == typeId);
            if (stretcher == null)
            {
                stretcher = walking.gameObject.AddComponent<NavMeshSurface>();
                stretcher.agentTypeID = typeId;
                stretcher.collectObjects = walking.collectObjects;
                stretcher.size = walking.size;
                stretcher.center = walking.center;
                stretcher.layerMask = walking.layerMask;
                stretcher.useGeometry = walking.useGeometry;
                stretcher.defaultArea = walking.defaultArea;
                stretcher.ignoreNavMeshAgent = walking.ignoreNavMeshAgent;
                stretcher.ignoreNavMeshObstacle = walking.ignoreNavMeshObstacle;
                stretcher.overrideTileSize = walking.overrideTileSize;
                stretcher.tileSize = walking.tileSize;
                stretcher.overrideVoxelSize = walking.overrideVoxelSize;
                stretcher.voxelSize = walking.voxelSize;
                stretcher.minRegionArea = walking.minRegionArea;
                stretcher.buildHeightMesh = walking.buildHeightMesh;
            }
            stretcher.RemoveData();
            stretcher.BuildNavMesh();
            if (AssetDatabase.LoadAssetAtPath<NavMeshData>(NavMeshAssetPath) != null)
                AssetDatabase.DeleteAsset(NavMeshAssetPath);
            NavMeshData data = stretcher.navMeshData;
            AssetDatabase.CreateAsset(data, NavMeshAssetPath);
            stretcher.navMeshData = data;
            stretcher.AddData();
            EditorUtility.SetDirty(stretcher);

            Transform wing = GameObject.FindWithTag("Wing").transform;
            Transform ambulance = GameObject.FindGameObjectsWithTag("EmergencyArea")
                .OrderBy(g => Vector3.Distance(g.transform.position, wing.position)).First().transform;
            HospitalLayout layout = Object.FindObjectsByType<HospitalLayout>(FindObjectsInactive.Include, FindObjectsSortMode.None).First();
            layout.ConfigureStretchers(ambulance, typeId);
            EditorUtility.SetDirty(layout);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            NavMeshBuildSettings settings = NavMesh.GetSettingsByID(typeId);
            Debug.Log($"StretcherSetup: agent type {typeId} (radius {settings.agentRadius}), ambulance entrance '{ambulance.name}' at {ambulance.position}");
        }

        static int EnsureAgentType(string name, float radius)
        {
            Object asset = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/NavMeshAreas.asset")[0];
            var so = new SerializedObject(asset);
            SerializedProperty settings = so.FindProperty("m_Settings");
            SerializedProperty names = so.FindProperty("m_SettingNames");
            for (int i = 0; i < names.arraySize; i++)
            {
                if (names.GetArrayElementAtIndex(i).stringValue != name)
                    continue;
                SerializedProperty existing = settings.GetArrayElementAtIndex(i);
                existing.FindPropertyRelative("agentRadius").floatValue = radius;
                so.ApplyModifiedPropertiesWithoutUndo();
                return existing.FindPropertyRelative("agentTypeID").intValue;
            }
            SerializedProperty last = so.FindProperty("m_LastAgentTypeID");
            int id = last.intValue - 1;
            last.intValue = id;
            settings.InsertArrayElementAtIndex(settings.arraySize - 1);
            SerializedProperty created = settings.GetArrayElementAtIndex(settings.arraySize - 1);
            created.FindPropertyRelative("agentTypeID").intValue = id;
            created.FindPropertyRelative("agentRadius").floatValue = radius;
            names.InsertArrayElementAtIndex(names.arraySize - 1);
            names.GetArrayElementAtIndex(names.arraySize - 1).stringValue = name;
            so.ApplyModifiedPropertiesWithoutUndo();
            return id;
        }
    }
}
