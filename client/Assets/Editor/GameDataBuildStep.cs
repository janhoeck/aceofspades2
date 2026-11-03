using System.IO;
using AoS2.Client;
using AoS2.Shared.Voxels;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace AoS2.Editor
{
    /// <summary>
    /// Kopiert vor jedem Build die Spieldaten aus dem Repository nach Assets/StreamingAssets
    /// (data/*.json, maps/*.aos2map und die Blocktexturen aus blocks.json). Im Editor liest das Spiel
    /// sie direkt aus dem Repository, siehe <see cref="GameDataPaths"/>. Die Kopien stehen in .gitignore.
    /// </summary>
    public sealed class GameDataBuildStep : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report) => Copy();

        [MenuItem("AoS2/Spieldaten nach StreamingAssets kopieren")]
        public static void Copy()
        {
            var root = GameDataPaths.RepositoryRoot;
            var target = Application.streamingAssetsPath;

            var data = Recreate(Path.Combine(target, "data"));
            foreach (var file in Directory.GetFiles(Path.Combine(root, "data"), "*.json"))
                File.Copy(file, Path.Combine(data, Path.GetFileName(file)));

            var maps = Recreate(Path.Combine(target, "maps"));
            foreach (var file in Directory.GetFiles(Path.Combine(root, "maps"), "*.aos2map"))
                File.Copy(file, Path.Combine(maps, Path.GetFileName(file)));

            // Nur Texturen, die blocks.json wirklich nutzt
            var textures = Recreate(Path.Combine(target, "textures", "blocks"));
            var registry = BlockRegistry.Parse(File.ReadAllText(Path.Combine(root, "data", "blocks.json")));
            var count = 0;
            foreach (var block in registry.All)
                for (var f = 0; f < BlockFaces.Count; f++)
                    foreach (var name in block.GetTextures((BlockFace)f))
                    {
                        var destination = Path.Combine(textures, name + ".png");
                        if (File.Exists(destination)) continue;
                        File.Copy(Path.Combine(root, "assets-src", "textures", "blocks", name + ".png"), destination);
                        count++;
                    }

            // Teamtexturen des Spielermodells (das Modell selbst steht in data/player_rig.json)
            var models = Recreate(Path.Combine(target, "models", "player"));
            foreach (var file in Directory.GetFiles(Path.Combine(root, "assets-src", "models", "player"), "player_*.png"))
                if (!file.EndsWith("_mask.png")) File.Copy(file, Path.Combine(models, Path.GetFileName(file)));

            AssetDatabase.Refresh();
            Debug.Log($"[AoS2] Spieldaten nach {target} kopiert ({count} Blocktexturen).");
        }

        private static string Recreate(string folder)
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Directory.CreateDirectory(folder);
            return folder;
        }
    }
}
