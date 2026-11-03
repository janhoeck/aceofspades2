using System.Linq;
using AoS2.Client;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AoS2.Editor
{
    /// <summary>
    /// Richtet die Spielszene ein: ein GameObject „LocalGame“ mit den Shadern der Blockwelt.
    /// Kann beliebig oft laufen. Aufruf im Batchmode:
    /// <code>Unity.exe -batchmode -quit -projectPath client -executeMethod AoS2.Editor.SceneSetup.Run</code>
    /// </summary>
    public static class SceneSetup
    {
        public const string ScenePath = "Assets/Scenes/Game.unity";
        private const string OldScenePath = "Assets/Scenes/SampleScene.unity";

        [MenuItem("AoS2/Spielszene einrichten")]
        public static void Run()
        {
            // Die Vorlage hieß SampleScene; umbenennen behält die GUID, Verweise bleiben gültig.
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) == null && AssetDatabase.LoadAssetAtPath<SceneAsset>(OldScenePath) != null)
            {
                var error = AssetDatabase.MoveAsset(OldScenePath, ScenePath);
                if (!string.IsNullOrEmpty(error)) throw new System.InvalidOperationException(error);
            }

            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var game = Object.FindAnyObjectByType<LocalGame>();
            if (game == null) game = new GameObject("LocalGame").AddComponent<LocalGame>();

            var serialized = new SerializedObject(game);
            serialized.FindProperty("voxelShader").objectReferenceValue = Shader.Find("AoS2/Voxel");
            serialized.FindProperty("lineShader").objectReferenceValue = Shader.Find("AoS2/Unlit Color");
            serialized.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);

            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) }
                .Concat(EditorBuildSettings.scenes.Where(s => s.path != ScenePath && s.path != OldScenePath))
                .ToArray();
            AssetDatabase.SaveAssets();
            Debug.Log($"[AoS2] Szene {ScenePath} eingerichtet.");
        }
    }
}
