using System.IO;
using UnityEngine;

namespace AoS2.Client
{
    /// <summary>
    /// Wo Spieldaten liegen. Im Editor direkt im Repository (data/, maps/, assets-src/textures/blocks),
    /// damit Änderungen sofort wirken. Im fertigen Spiel in StreamingAssets; dorthin kopiert sie
    /// <c>AoS2.Editor.GameDataBuildStep</c> beim Bauen.
    /// </summary>
    public static class GameDataPaths
    {
        /// <summary>Repository-Wurzel (nur im Editor sinnvoll): client/Assets → zwei Ebenen höher.</summary>
        public static string RepositoryRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));

        public static string DataFolder => Application.isEditor ? Path.Combine(RepositoryRoot, "data") : Path.Combine(Application.streamingAssetsPath, "data");

        public static string MapsFolder => Application.isEditor ? Path.Combine(RepositoryRoot, "maps") : Path.Combine(Application.streamingAssetsPath, "maps");

        public static string BlockTexturesFolder => Application.isEditor
            ? Path.Combine(RepositoryRoot, "assets-src", "textures", "blocks")
            : Path.Combine(Application.streamingAssetsPath, "textures", "blocks");

        public static string DataFile(string name) => Path.Combine(DataFolder, name);
    }
}
