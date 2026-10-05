using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace RadarCrowd.EditorTools
{
    /// <summary>
    /// Re-bakes both NavMeshes with fine voxels. Unity carves NavMeshObstacles voxel by voxel, so with 0.167 m voxels
    /// the 0.94 m gap between two trolleys facing each other in a 2.44 m corridor closed for pedestrians (a person passes
    /// there) and a stretcher needed about 1.33 m instead of 1.2 m. Pedestrians: 0.0833 m (Unity's default, radius / 3);
    /// stretchers: 0.1 m (radius / 6).
    /// Batch mode: -executeMethod RadarCrowd.EditorTools.NavMeshRebake.Run
    /// </summary>
    public static class NavMeshRebake
    {
        const string ScenePath = "Assets/RadarCrowd/Scenes/Hospital.unity";
        const string WalkingAsset = "Assets/RadarCrowd/Scenes/Hospital_NavMesh.asset";
        const string StretcherAsset = "Assets/RadarCrowd/Scenes/Hospital_NavMesh_Stretcher.asset";
        const float WalkingVoxel = 0.25f / 3f;
        const float StretcherVoxel = 0.1f;

        [MenuItem("RadarCrowd/Re-bake NavMeshes (fine voxels)")]
        public static void Run()
        {
            Scene scene = EditorSceneManager.OpenScene(ScenePath);
            foreach (NavMeshSurface surface in Object.FindObjectsByType<NavMeshSurface>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                bool walking = surface.agentTypeID == 0;
                string path = walking ? WalkingAsset : StretcherAsset;
                surface.overrideVoxelSize = true;
                surface.voxelSize = walking ? WalkingVoxel : StretcherVoxel;
                float started = Time.realtimeSinceStartup;
                surface.RemoveData();
                surface.BuildNavMesh();
                if (AssetDatabase.LoadAssetAtPath<NavMeshData>(path) != null)
                    AssetDatabase.DeleteAsset(path);
                NavMeshData data = surface.navMeshData;
                AssetDatabase.CreateAsset(data, path);
                surface.navMeshData = data;
                surface.AddData();
                EditorUtility.SetDirty(surface);
                Debug.Log($"NavMeshRebake: agent type {surface.agentTypeID}, voxel {surface.voxelSize:F4} m, " +
                          $"{Time.realtimeSinceStartup - started:F1} s -> {path}");
            }
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
        }
    }
}
