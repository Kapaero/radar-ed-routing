using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RadarCrowd.EditorTools
{
    /// <summary>
    /// Creates the scene of the Jülich corridor validation and builds its Windows player, separate from the hospital player:
    /// JuelichValidation.exe -batchmode -nographics -config validation.json -out output_dir -logFile run.log
    /// </summary>
    public static class ValidationBuilder
    {
        const string ScenePath = "Assets/RadarCrowd/Scenes/JuelichCorridor.unity";
        const string Output = "Builds/Validation/JuelichValidation.exe";

        [MenuItem("RadarCrowd/Create validation scene")]
        public static void CreateScene()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            new GameObject("Corridor validation").AddComponent<CorridorValidation>();
            var camera = new GameObject("Main Camera") { tag = "MainCamera" };
            camera.AddComponent<Camera>();
            camera.transform.SetPositionAndRotation(new Vector3(1.5f, 16f, 0f), Quaternion.Euler(90f, 0f, 0f));
            var light = new GameObject("Light").AddComponent<Light>();
            light.type = LightType.Directional;
            light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log("ValidationBuilder: scene saved to " + ScenePath);
        }

        [MenuItem("RadarCrowd/Build validation player")]
        public static void Build()
        {
            if (!File.Exists(ScenePath))
                CreateScene();
            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = Output,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            };
            BuildReport report = BuildPipeline.BuildPlayer(options);
            Debug.Log("ValidationBuilder: " + report.summary.result + ", " + report.summary.totalSize / (1024 * 1024) + " MB -> " + Output);
            if (report.summary.result != BuildResult.Succeeded && Application.isBatchMode)
                EditorApplication.Exit(1);
        }
    }
}
