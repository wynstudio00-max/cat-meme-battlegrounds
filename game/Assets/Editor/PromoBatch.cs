using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace CatBattleground
{
    // Command-line recorder (no editor UI needed, Unity must be closed):
    //   Unity.exe -batchmode -quit -projectPath CatBattleground -buildTarget Win64 -executeMethod CatBattleground.PromoBatch.BuildRecorder
    //   Build/PromoRecorder/PromoRecorder.exe -promoOut <folder>      (writes frames + cues.json, quits by itself)
    public static class PromoBatch
    {
        public const string ExePath = "../Build/PromoRecorder/PromoRecorder.exe";

        public static void BuildRecorder()
        {
            PromoCutsceneSetup.Build();
            var opts = new BuildPlayerOptions
            {
                scenes = new[] { "Assets/Scenes/PromoCutscene.unity" },
                locationPathName = ExePath,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            };
            var report = BuildPipeline.BuildPlayer(opts);
            Debug.Log("[Promo] recorder build: " + report.summary.result + " -> " + ExePath);
            if (Application.isBatchMode) EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
        }
    }
}
