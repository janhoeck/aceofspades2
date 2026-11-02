using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace AoS2.Editor
{
    /// <summary>
    /// Baut den Windows-Client ohne Editor-Fenster. Aufruf (lokal oder über GameCI):
    /// <code>
    /// Unity.exe -batchmode -quit -projectPath client -executeMethod AoS2.Editor.BuildScript.BuildWindows
    ///           -buildVersion 0.1.0 -customBuildPath ../artifacts/unity-build/AoS2.exe
    /// </code>
    /// Die Argumentnamen sind dieselben, die GameCI (game-ci/unity-builder) übergibt.
    /// </summary>
    public static class BuildScript
    {
        private const string DefaultBuildPath = "Builds/StandaloneWindows64/AoS2.exe";

        [MenuItem("AoS2/Windows-Build erstellen")]
        public static void BuildWindowsFromMenu() => Build(PlayerSettings.bundleVersion, DefaultBuildPath);

        public static void BuildWindows()
        {
            var version = GetArgument("-buildVersion") ?? PlayerSettings.bundleVersion;
            var path = GetArgument("-customBuildPath") ?? DefaultBuildPath;

            // GameCI übergibt bei -customBuildPath manchmal nur den Ordner.
            if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                path = System.IO.Path.Combine(path, "AoS2.exe");

            var succeeded = Build(version, path);
            if (Application.isBatchMode)
                EditorApplication.Exit(succeeded ? 0 : 1);
        }

        private static bool Build(string version, string path)
        {
            // Wird im Spiel als Application.version angezeigt. Danach zurücksetzen,
            // damit ein lokaler Build die ProjectSettings im Repo nicht verändert.
            var previousVersion = PlayerSettings.bundleVersion;
            PlayerSettings.bundleVersion = version;
            try
            {
                var options = new BuildPlayerOptions
                {
                    scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray(),
                    locationPathName = path,
                    target = BuildTarget.StandaloneWindows64,
                    options = BuildOptions.None,
                };

                var summary = BuildPipeline.BuildPlayer(options).summary;
                Debug.Log($"[AoS2] Build {version}: {summary.result}, {summary.totalSize / 1024 / 1024} MB, {summary.totalErrors} Fehler → {path}");
                return summary.result == BuildResult.Succeeded;
            }
            finally
            {
                PlayerSettings.bundleVersion = previousVersion;
                AssetDatabase.SaveAssets();
            }
        }

        private static string GetArgument(string name)
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
    }
}
