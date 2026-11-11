using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace CatBattleground
{
    /// <summary>
    /// One-click builds for the web demo:
    ///   WebGL client           -> Builds/WebGL            (joins a dedicated server over WebSocket)
    ///   Windows server         -> Builds/Server_Win       (headless dedicated server, for testing on a PC)
    ///   Linux server           -> Builds/Server_Linux     (for a VPS; needs "Linux Dedicated Server Build Support" in Unity Hub)
    /// Switching the active platform re-imports assets, so the first build of each kind is slow.
    /// </summary>
    public static class BuildTools
    {
        const string Scene = "Assets/Scenes/Phase2_Network.unity";

        [MenuItem("CatBattleground/Build/WebGL Client")]
        public static void BuildWebGL()
        {
            if (!Ready()) return;
            // 2026-10-28 size pass: Brotli is ~15-20% smaller than gzip; decompressionFallback lets the loader unpack it itself when
            // the host does not send Content-Encoding: br (any static host keeps working).
            PlayerSettings.WebGL.template = "PROJECT:CMBG";                         // Assets/WebGLTemplates/CMBG: full window + CMBG loading screen
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Brotli;
            PlayerSettings.WebGL.decompressionFallback = true;
            PlayerSettings.WebGL.dataCaching = true;                                 // the browser keeps the data file between visits
            PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.ExplicitlyThrownExceptionsOnly;
            PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.WebGL, ManagedStrippingLevel.High);
            PlayerSettings.SetIl2CppCodeGeneration(NamedBuildTarget.WebGL, Il2CppCodeGeneration.OptimizeSize);
            PlayerSettings.stripEngineCode = true;
            // the "Made with Unity" splash is optional since Unity 6; its logo alone was 2.7 MB of the data file (build 2026-10-28)
            PlayerSettings.SplashScreen.show = false;
            PlayerSettings.SplashScreen.showUnityLogo = false;
            PlayerSettings.runInBackground = true;   // keep the connection alive when the tab loses focus
            UseWebPostProcessData();
            ExcludePcQualityFromWebGL();
            // URP's 10 film-grain noise textures (~2.5 MB that barely compress) were in every build although the game never uses film
            // grain (only the editor-only promo does). Blank them in the global settings for this build only (2026-10-31).
            var tex = UnityEngine.Rendering.GraphicsSettings.TryGetRenderPipelineSettings<PostProcessData.TextureResources>(out var res) ? res : null;
            var grain = tex?.filmGrainTex;
            if (tex != null) tex.filmGrainTex = new Texture2D[grain != null ? grain.Length : 10];
            try { Build(BuildTarget.WebGL, NamedBuildTarget.WebGL, "Builds/WebGL", null, StandaloneBuildSubtarget.Default); }
            finally { if (tex != null) tex.filmGrainTex = grain; }
        }

        // The WebGL / phone renderer (Mobile_Renderer) gets its own copy of URP's PostProcessData without the film-grain textures, so
        // they are not pulled into the build through the renderer either. PC_Renderer (editor, promo recording) keeps the original.
        const string WebPostData = "Assets/Settings/PostProcessData_Web.asset";
        static void UseWebPostProcessData()
        {
            var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>("Assets/Settings/Mobile_Renderer.asset");
            if (renderer == null) { Debug.LogWarning("[BuildTools] Mobile_Renderer.asset not found - film grain textures stay in the build"); return; }
            var web = AssetDatabase.LoadAssetAtPath<PostProcessData>(WebPostData);
            if (web == null)
            {
                var source = renderer.postProcessData;
                if (source == null) return;
                web = Object.Instantiate(source);
                AssetDatabase.CreateAsset(web, WebPostData);
            }
            if (web.textures != null && web.textures.filmGrainTex != null && System.Array.Exists(web.textures.filmGrainTex, t => t != null))
            {
                web.textures.filmGrainTex = new Texture2D[web.textures.filmGrainTex.Length];   // only read when a Film Grain override is active
                EditorUtility.SetDirty(web);
            }
            if (renderer.postProcessData != web) { renderer.postProcessData = web; EditorUtility.SetDirty(renderer); }
            AssetDatabase.SaveAssets();
        }

        // 2026-11-10: the "PC" quality level was also shipped in the WebGL build (only Android / iPhone were excluded), and through
        // PC_RPAsset -> PC_Renderer -> URP's own PostProcessData it pulled the 10 film-grain textures (~2.5 MB) back in. WebGL always
        // runs the "Mobile" level (nothing switches quality at runtime), so the PC level is excluded for WebGL.
        static void ExcludePcQualityFromWebGL()
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/QualitySettings.asset");
            if (assets == null || assets.Length == 0) return;
            var so = new SerializedObject(assets[0]);
            var levels = so.FindProperty("m_QualitySettings");
            if (levels == null) return;
            bool changed = false;
            for (int i = 0; i < levels.arraySize; i++)
            {
                var level = levels.GetArrayElementAtIndex(i);
                if (level.FindPropertyRelative("name").stringValue != "PC") continue;
                var ex = level.FindPropertyRelative("excludedTargetPlatforms");
                if (ex == null) continue;
                bool has = false;
                for (int k = 0; k < ex.arraySize; k++) if (ex.GetArrayElementAtIndex(k).stringValue == "WebGL") has = true;
                if (has) continue;
                ex.InsertArrayElementAtIndex(ex.arraySize);
                ex.GetArrayElementAtIndex(ex.arraySize - 1).stringValue = "WebGL";
                changed = true;
            }
            if (changed) { so.ApplyModifiedPropertiesWithoutUndo(); AssetDatabase.SaveAssets(); Debug.Log("[BuildTools] PC quality level excluded from WebGL"); }
        }

        [MenuItem("CatBattleground/Build/Windows Dedicated Server")]
        public static void BuildWindowsServer()
        {
            if (!Ready()) return;
            Build(BuildTarget.StandaloneWindows64, NamedBuildTarget.Server, "Builds/Server_Win", "CatBattlegroundServer.exe", StandaloneBuildSubtarget.Server);
        }

        [MenuItem("CatBattleground/Build/Linux Dedicated Server")]
        public static void BuildLinuxServer()
        {
            if (!Ready()) return;
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneLinux64))
            {
                EditorUtility.DisplayDialog("Linux server build support missing",
                    "Install the module \"Linux Dedicated Server Build Support\" for this Unity version in Unity Hub, then run this again.", "OK");
                return;
            }
            Build(BuildTarget.StandaloneLinux64, NamedBuildTarget.Server, "Builds/Server_Linux", "CatBattlegroundServer.x86_64", StandaloneBuildSubtarget.Server);
        }

        static bool Ready()
        {
            if (File.Exists(Scene)) return true;
            EditorUtility.DisplayDialog("Scene missing", "Run CatBattleground > Build Phase 2 Multiplayer Scene first.", "OK");
            return false;
        }

        static void Build(BuildTarget target, NamedBuildTarget named, string folder, string exeName, StandaloneBuildSubtarget subtarget)
        {
            Directory.CreateDirectory(folder);
            var options = new BuildPlayerOptions
            {
                scenes = new[] { Scene },
                target = target,
                targetGroup = BuildPipeline.GetBuildTargetGroup(target),
                locationPathName = exeName == null ? folder : Path.Combine(folder, exeName),
                options = BuildOptions.None,
            };
            if (target != BuildTarget.WebGL) options.subtarget = (int)subtarget;

            var report = BuildPipeline.BuildPlayer(options);
            var summary = report.summary;
            if (summary.result == BuildResult.Succeeded)
                Debug.Log($"CatBattleground: {target} build OK -> {Path.GetFullPath(folder)} ({summary.totalSize / (1024 * 1024)} MB, {summary.totalTime.TotalSeconds:0} s)");
            else
                Debug.LogError($"CatBattleground: {target} build {summary.result} ({summary.totalErrors} errors). See the Console / Editor.log.");
            if (summary.result == BuildResult.Succeeded) WriteSizeReport(report, folder, target);
        }

        // What the player has to download and what takes the room inside it, for the size pass (2026-10-28):
        //   <repo>/Tools/build_report_<target>.txt  - the built files, totals per asset kind, the 40 biggest assets.
        static void WriteSizeReport(BuildReport report, string folder, BuildTarget target)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"{target} build {System.DateTime.Now:yyyy-MM-dd HH:mm}  ({report.summary.totalTime.TotalSeconds:0} s)");
            sb.AppendLine("== files (download, compressed) ==");
            long download = 0;
            foreach (var f in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
            {
                long len = new FileInfo(f).Length; download += len;
                sb.AppendLine($"{len / 1048576.0,9:0.00} MB  {f.Substring(folder.Length).TrimStart('/', '\\')}");
            }
            sb.AppendLine($"{download / 1048576.0,9:0.00} MB  TOTAL");

            var perAsset = new System.Collections.Generic.Dictionary<string, (ulong size, string type)>();
            foreach (var pack in report.packedAssets)
                foreach (var info in pack.contents)
                {
                    var key = string.IsNullOrEmpty(info.sourceAssetPath) ? "(built-in / generated)" : info.sourceAssetPath;
                    perAsset.TryGetValue(key, out var cur);
                    perAsset[key] = (cur.size + info.packedSize, info.type != null ? info.type.Name : "?");
                }
            var perType = new System.Collections.Generic.Dictionary<string, ulong>();
            foreach (var kv in perAsset) { perType.TryGetValue(kv.Value.type, out var t); perType[kv.Value.type] = t + kv.Value.size; }
            sb.AppendLine("== inside the data file, uncompressed, by kind ==");
            foreach (var kv in System.Linq.Enumerable.OrderByDescending(perType, x => x.Value))
                sb.AppendLine($"{kv.Value / 1048576.0,9:0.00} MB  {kv.Key}");
            sb.AppendLine("== 40 biggest assets ==");
            foreach (var kv in System.Linq.Enumerable.Take(System.Linq.Enumerable.OrderByDescending(perAsset, x => x.Value.size), 40))
                sb.AppendLine($"{kv.Value.size / 1048576.0,9:0.00} MB  {kv.Value.type,-16} {kv.Key}");

            var path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "Tools", $"build_report_{target}.txt"));
            File.WriteAllText(path, sb.ToString());
            Debug.Log("CatBattleground: size report -> " + path);
        }
    }
}
