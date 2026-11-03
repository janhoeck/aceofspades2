using AoS2.Shared.Voxels;
using UnityEngine;

namespace AoS2.Client
{
    /// <summary>
    /// Platzhalter-HUD: Fadenkreuz, gewählter Baublock, Steuerungshilfe, Ladefehler und im Netzspiel
    /// Server, Ping und die Namen der anderen Spieler. Erzeugt sich beim Start selbst. Wird in M5/M6 durch richtiges UI ersetzt.
    /// </summary>
    public sealed class Hud : MonoBehaviour
    {
        private GUIStyle _text;
        private GUIStyle _center;
        private GUIStyle _name;
        private Texture2D _white;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Create()
        {
            var go = new GameObject(nameof(Hud));
            go.AddComponent<Hud>();
            DontDestroyOnLoad(go);
        }

        private void OnGUI()
        {
            var game = GameClient.Instance;
            if (game == null) return;
            _text ??= new GUIStyle(GUI.skin.label) { fontSize = 16 };
            _center ??= new GUIStyle(GUI.skin.label) { fontSize = 20, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            _name ??= new GUIStyle(GUI.skin.label) { fontSize = 14, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            if (_white == null) _white = Texture2D.whiteTexture;

            if (game.LoadError != null)
            {
                Shadowed(new Rect(0, Screen.height / 2f - 40, Screen.width, 80), "Spieldaten konnten nicht geladen werden:\n" + game.LoadError, _center);
                return;
            }
            if (!game.IsReady) return;

            if (game.Mode == GameMode.Online) DrawNames(game);

            // Fadenkreuz: zwei dünne Balken mit dunklem Rand
            float cx = Screen.width / 2f, cy = Screen.height / 2f;
            DrawCross(cx, cy, 10, 2, new Color(0, 0, 0, 0.6f), 1);
            DrawCross(cx, cy, 10, 2, Color.white, 0);

            var player = game.Player;
            var selected = player.SelectedBlock;
            if (selected != null)
                Shadowed(new Rect(0, Screen.height - 70, Screen.width, 30), $"Baublock: {selected.DisplayName}", _center);
            if (player.LastEditResult != BlockEditResult.Ok && Time.time - player.LastEditTime < 2f)
                Shadowed(new Rect(0, Screen.height - 40, Screen.width, 26), EditMessage(player.LastEditResult), _center);

            Shadowed(new Rect(12, 8, 1000, 24), "WASD laufen · Leertaste springen · Strg ducken · Shift sprinten · Linksklick abbauen · Rechtsklick bauen · 1–9/Mausrad Block · Esc 2× Menü", _text);
            if (Cursor.lockState != CursorLockMode.Locked)
                Shadowed(new Rect(0, cy + 30, Screen.width, 30), "Klicken, um zu spielen (Esc gibt die Maus frei, zweimal: zurück ins Menü)", _center);

            var p = player.State.Position;
            Shadowed(new Rect(12, 30, 700, 24), $"{game.Metadata.Name}  ·  Position {p.X:0.0} / {p.Y:0.0} / {p.Z:0.0}  ·  {1f / Mathf.Max(Time.smoothDeltaTime, 0.0001f):0} FPS", _text);
            if (game.Mode == GameMode.Online)
            {
                var session = game.Session;
                Shadowed(new Rect(12, 52, 700, 24),
                    $"Online: {game.ServerAddressText}  ·  {session.PlayerName}  ·  Ping {game.Net.RoundTripTime} ms  ·  {session.RemotePlayers.Count + 1} Spieler",
                    _text);
            }
        }

        /// <summary>Namen über den anderen Spielern.</summary>
        private void DrawNames(GameClient game)
        {
            var camera = Camera.main;
            if (camera == null || game.RemotePlayers == null) return;
            foreach (var avatar in game.RemotePlayers.Avatars)
            {
                if (!game.Session.RemotePlayers.TryGetValue(avatar.Key, out var remote)) continue;
                var screen = camera.WorldToScreenPoint(avatar.Value.position + Vector3.up * 2.2f);
                if (screen.z <= 0 || screen.z > 60) continue;
                Shadowed(new Rect(screen.x - 100, Screen.height - screen.y - 12, 200, 24), remote.Name, _name);
            }
        }

        private static string EditMessage(BlockEditResult result)
        {
            switch (result)
            {
                case BlockEditResult.Indestructible: return "Dieser Block ist unzerstörbar.";
                case BlockEditResult.BlockedByPlayer: return "Hier steht ein Spieler.";
                case BlockEditResult.NotBuildable: return "Diesen Block kann man nicht bauen.";
                case BlockEditResult.OutsideWorld: return "Außerhalb der Map.";
                case BlockEditResult.TooFast: return "Zu schnell.";
                case BlockEditResult.NoLineOfSight: return "Der Server sieht den Block von dir aus nicht.";
                default: return "";
            }
        }

        private void DrawCross(float cx, float cy, float length, float thickness, Color color, float border)
        {
            var old = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(new Rect(cx - length - border, cy - thickness / 2 - border, 2 * (length + border), thickness + 2 * border), _white);
            GUI.DrawTexture(new Rect(cx - thickness / 2 - border, cy - length - border, thickness + 2 * border, 2 * (length + border)), _white);
            GUI.color = old;
        }

        private static void Shadowed(Rect rect, string text, GUIStyle style)
        {
            var old = GUI.color;
            GUI.color = new Color(0, 0, 0, 0.8f);
            GUI.Label(new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height), text, style);
            GUI.color = old;
            GUI.Label(rect, text, style);
        }
    }
}
