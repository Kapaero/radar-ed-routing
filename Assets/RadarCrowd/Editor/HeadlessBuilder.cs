using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace RadarCrowd.EditorTools
{
    /// <summary>
    /// Builds the Windows player used for experiment campaigns. Run it with:
    /// RadarCrowd.exe -batchmode -nographics -config run.json -out output_dir -logFile run.log
    /// Build with -buildOutput path/to/RadarCrowd.exe to build next to a player that is still running.
    /// </summary>
    public static class HeadlessBuilder
    {
        const string Scene = "Assets/RadarCrowd/Scenes/Hospital.unity";
        const string Output = "Builds/RadarCrowd/RadarCrowd.exe";

        static string OutputPath()
        {
            string[] args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == "-buildOutput")
                    return args[i + 1];
            return Output;
        }

        [MenuItem("RadarCrowd/Build player")]
        public static void Build()
        {
            PlayerSettings.runInBackground = true;
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.defaultScreenWidth = 960;
            PlayerSettings.defaultScreenHeight = 720;
            var options = new BuildPlayerOptions
            {
                scenes = new[] { Scene },
                locationPathName = OutputPath(),
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            };
            BuildReport report = BuildPipeline.BuildPlayer(options);
            Debug.Log("HeadlessBuilder: " + report.summary.result + ", " + report.summary.totalSize / (1024 * 1024) + " MB -> " + OutputPath());
            if (report.summary.result != BuildResult.Succeeded && Application.isBatchMode)
                EditorApplication.Exit(1);
        }
    }
}
