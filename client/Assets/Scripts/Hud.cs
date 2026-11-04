using System.Collections.Generic;
using AoS2.Shared.Combat;
using AoS2.Shared.Maps;
using AoS2.Shared.Net;
using AoS2.Shared.Net.Client;
using AoS2.Shared.Voxels;
using UnityEngine;

namespace AoS2.Client
{
    /// <summary>
    /// Platzhalter-HUD: Fadenkreuz mit Trefferanzeige, Leben, Munition und Waffenplätze, Kill-Anzeige, Todesbildschirm mit
    /// Respawn-Countdown, roter Rand bei Schaden, gewählter Baublock, Steuerungshilfe, Ladefehler und im Netzspiel Server,
    /// Ping und die Namen der anderen Spieler. Seit M6 dazu: Punktestand der Teams, Team-Menü, Punktetabelle (Tab) und
    /// Anzeige am Matchende. Seit M8: Klassenwahl im selben Menü (M, K), Waffen und Blockvorrat der Klasse, Zielfernrohr.
    /// Erzeugt sich beim Start selbst. Wird in M9 durch richtiges UI ersetzt.
    /// </summary>
    public sealed class Hud : MonoBehaviour
    {
        private const float TeamMessageSeconds = 4f;
        private const float HitMarkerSeconds = 0.2f;
        private const float DamageFlashSeconds = 0.5f;
        private const float KillFeedSeconds = 6f;

        private GUIStyle _text;
        private GUIStyle _center;
        private GUIStyle _name;
        private GUIStyle _big;
        private GUIStyle _right;
        private GUIStyle _ammo;
        private GUIStyle _scoreStyle;
        private GUIStyle _scoreboardTitle;
        private GUIStyle _button;
        private GUIStyle _small;
        private GUIStyle _huge;
        private GUIStyle _cardTitle;
        private GUIStyle _badge;
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
            _big ??= new GUIStyle(GUI.skin.label) { fontSize = 30, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            _right ??= new GUIStyle(GUI.skin.label) { fontSize = 16, alignment = TextAnchor.MiddleRight, richText = true };
            _ammo ??= new GUIStyle(GUI.skin.label) { fontSize = 28, alignment = TextAnchor.MiddleRight, fontStyle = FontStyle.Bold };
            _scoreStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 20, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            _scoreboardTitle ??= new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold };
            _button ??= new GUIStyle(GUI.skin.button) { fontSize = 18, fontStyle = FontStyle.Bold };
            _small ??= new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = true };
            _huge ??= new GUIStyle(GUI.skin.label) { fontSize = 44, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            _cardTitle ??= new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
            _badge ??= new GUIStyle(GUI.skin.label) { fontSize = 26, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            if (_white == null) _white = Texture2D.whiteTexture;

            if (game.LoadError != null)
            {
                Shadowed(new Rect(0, Screen.height / 2f - 40, Screen.width, 80), "Spieldaten konnten nicht geladen werden:\n" + game.LoadError, _center);
                return;
            }
            if (!game.IsReady) return;

            var online = game.Mode == GameMode.Online;
            var session = game.Session;
            var alive = !online || session.Alive;
            if (online)
            {
                DrawNames(game);
                DrawDamageFlash(game.Feed);
            }

            // Fadenkreuz: zwei dünne Balken mit dunklem Rand (mit Zielfernrohr ein Absehen über den ganzen Schirm)
            float cx = Screen.width / 2f, cy = Screen.height / 2f;
            if (alive && game.Player.Zoomed) DrawScope(cx, cy);
            else if (alive)
            {
                DrawCross(cx, cy, 10, 2, new Color(0, 0, 0, 0.6f), 1);
                DrawCross(cx, cy, 10, 2, Color.white, 0);
            }
            if (online) DrawHitMarker(game.Feed, cx, cy);

            var player = game.Player;
            if (player.Slot == 0)
            {
                var selected = player.SelectedBlock;
                var supply = online ? $"  ·  {session.BlocksLeft} / {session.Class.Blocks} Blöcke" : "";
                if (selected != null)
                    Shadowed(new Rect(0, Screen.height - 100, Screen.width, 30), $"Baublock: {selected.DisplayName} (B wechselt){supply}", _center);
            }
            if (player.LastEditResult != BlockEditResult.Ok && Time.time - player.LastEditTime < 2f)
                Shadowed(new Rect(0, Screen.height - 130, Screen.width, 26), EditMessage(player.LastEditResult), _center);

            var help = online
                ? "WASD laufen · Leertaste springen · Strg ducken · Shift sprinten · 1 Spaten, 2–5 Waffen (Mausrad) · Linksklick feuern/abbauen · Rechtsklick bauen/zielen · R nachladen · K Klasse · M Team · Tab Punkte · Esc 2× Menü"
                : "WASD laufen · Leertaste springen · Strg ducken · Shift sprinten · Linksklick abbauen · Rechtsklick bauen · B Baublock · Esc 2× Menü (Waffen nur online)";
            Shadowed(new Rect(12, 8, 1600, 24), help, _text);
            if (Cursor.lockState != CursorLockMode.Locked && !game.TeamMenuOpen)
                Shadowed(new Rect(0, cy + 30, Screen.width, 30), "Klicken, um zu spielen (Esc gibt die Maus frei, zweimal: zurück ins Menü)", _center);

            var p = player.State.Position;
            Shadowed(new Rect(12, 30, 700, 24), $"{game.Metadata.Name}  ·  Position {p.X:0.0} / {p.Y:0.0} / {p.Z:0.0}  ·  {1f / Mathf.Max(Time.smoothDeltaTime, 0.0001f):0} FPS", _text);
            if (!online) return;

            Shadowed(new Rect(12, 52, 700, 24),
                $"Online: {game.ServerAddressText}  ·  {session.PlayerName}  ·  Ping {game.Net.RoundTripTime} ms  ·  {session.RemotePlayers.Count + 1} Spieler",
                _text);
            DrawScore(game);
            DrawKillFeed(game.Feed);
            DrawTeamMessage(game.Feed);
            if (session.Team == Team.None)
            {
                if (game.TeamMenuOpen) DrawTeamMenu(game);
                return;
            }
            DrawHealth(session.Health, session.Class.Health, session.Team, session.Class, session.NextClass);
            DrawWeapons(game, player);
            if (alive && !session.MatchOver && session.Zones.InSpawnZone(player.State.Position))
                Shadowed(new Rect(0, Screen.height - 160, Screen.width, 26), "Spawnzone: Hier kann niemand schießen und niemand getroffen werden.", _center);
            if (!session.Alive && !session.MatchOver) DrawDeath(game);
            if (session.MatchOver) DrawMatchEnd(game);
            else if (player.ScoreboardHeld) DrawScoreboard(game, Screen.height / 2f - 200);
            if (game.TeamMenuOpen) DrawTeamMenu(game);
        }

        // ---- Team Deathmatch ----

        /// <summary>Oben in der Mitte: Punkte beider Teams, Punkteziel, Restzeit.</summary>
        private void DrawScore(GameClient game)
        {
            var session = game.Session;
            var match = session.Match;
            const float w = 90, h = 34;
            var x = Screen.width / 2f;
            const float y = 10;
            DrawTeamScore(new Rect(x - w - 4, y, w, h), match.ScoreBlue, Team.Blue, session.Team == Team.Blue);
            DrawTeamScore(new Rect(x + 4, y, w, h), match.ScoreGreen, Team.Green, session.Team == Team.Green);
            var info = $"Ziel {match.ScoreLimit}";
            var left = SecondsLeft(game);
            if (match.Phase == MatchPhase.Playing && left >= 0) info += $"  ·  {left / 60}:{left % 60:00}";
            Shadowed(new Rect(x - 150, y + h + 2, 300, 22), info, _name);
        }

        private void DrawTeamScore(Rect rect, int score, Team team, bool mine)
        {
            var color = GameClient.TeamColor(team);
            Fill(rect, new Color(color.r * 0.35f, color.g * 0.35f, color.b * 0.35f, 0.75f));
            if (mine) Frame(rect, color, 2);
            var old = GUI.color;
            GUI.color = color;
            GUI.Label(rect, $"{GameClient.TeamName(team)} {score}", _scoreStyle);
            GUI.color = old;
        }

        /// <summary>Sekunden bis zum Ende der Phase (Zeitlimit oder Neustart), -1 ohne Zeitlimit.</summary>
        private static int SecondsLeft(GameClient game)
        {
            var session = game.Session;
            if (session.Match.PhaseEndTick == 0) return -1;
            var now = session.RenderTick + ClientSession.InterpolationDelayTicks;
            return Mathf.Max(0, Mathf.CeilToInt((float)((session.Match.PhaseEndTick - now) / game.Movement.TickRate)));
        }

        private void DrawTeamMessage(CombatFeed feed)
        {
            if (feed.TeamMessage == null || Time.time - feed.TeamMessageTime > TeamMessageSeconds) return;
            Shadowed(new Rect(0, 90, Screen.width, 30), feed.TeamMessage, _center);
        }

        /// <summary>
        /// Menü „Klasse und Team“: oben die Klassen als Karten (Waffen, Blöcke, Tempo, Treffer bis zum Tod), unten die Teams
        /// (Blau, Grün oder automatisch; gesperrt ist, was der Server ohnehin ablehnen würde). Wer noch kein Team hat, wählt
        /// zuerst die Klasse und erscheint mit ihr, sobald er ein Team wählt. Mit K zeigt das Menü nur die Klassen.
        /// </summary>
        private void DrawTeamMenu(GameClient game)
        {
            var session = game.Session;
            var classes = session.Classes.All;
            const float cardWidth = 250, cardHeight = 250, gap = 12;
            var w = Mathf.Max(560, classes.Count * (cardWidth + gap) + gap);
            var h = game.ClassMenuOnly ? 70 + cardHeight + 50 : 70 + cardHeight + 230;
            var rect = new Rect((Screen.width - w) / 2, Mathf.Max(10, (Screen.height - h) / 2), w, h);
            Fill(rect, new Color(0, 0, 0, 0.8f));
            var title = game.ClassMenuOnly ? "Klasse wählen" : session.Team == Team.None ? "Klasse und Team wählen" : "Klasse und Team";
            Shadowed(new Rect(rect.x, rect.y + 12, w, 36), title, _big);

            // ---- Klassen ----
            var selected = game.MenuClass;
            var card = new Rect(rect.x + gap, rect.y + 60, cardWidth, cardHeight);
            for (var i = 0; i < classes.Count; i++)
            {
                DrawClassCard(card, classes[i], i + 1, classes[i] == selected, classes[i] == session.Class && session.Team != Team.None, game);
                card.x += cardWidth + gap;
            }
            var classHint = session.Team == Team.None ? "Du erscheinst mit der markierten Klasse, sobald du ein Team wählst."
                : session.Alive ? "Ein Wechsel gilt ab dem nächsten Spawn (nach dem nächsten Tod oder Teamwechsel)."
                : "Du erscheinst beim Respawn mit der markierten Klasse.";
            GUI.Label(new Rect(rect.x + gap, rect.y + 64 + cardHeight, w - 2 * gap, 22), classHint + (game.ClassMenuOnly ? "  ·  K oder Esc: schließen" : ""), _small);
            if (game.ClassMenuOnly) return;

            // ---- Teams ----
            var top = rect.y + 100 + cardHeight;
            Shadowed(new Rect(rect.x, top, w, 30), session.Team == Team.None ? "Team" : "Team wechseln", _center);
            var max = session.Match.PlayersPerTeam;
            var button = new Rect(rect.x + 20, top + 36, (w - 60) / 2, 70);
            foreach (var team in new[] { Team.Blue, Team.Green })
            {
                var old = GUI.backgroundColor;
                GUI.backgroundColor = GameClient.TeamColor(team);
                GUI.enabled = game.CanJoin(team);
                var label = $"{GameClient.TeamName(team)}\n{session.TeamSize(team)} / {max} Spieler · {session.Match.Score(team)} Punkte";
                if (GUI.Button(button, label, _button)) game.ChooseTeam(team);
                GUI.enabled = true;
                GUI.backgroundColor = old;
                button.x += button.width + 20;
            }
            if (GUI.Button(new Rect(rect.x + 20, top + 116, w - 40, 36), "Automatisch (kleineres Team)", _button)) game.ChooseTeam(Team.None);

            var hint = session.Team == Team.None
                ? "Ein Team darf höchstens einen Spieler mehr haben als das andere."
                : "Beim Wechsel stirbst du und erscheinst nach der Respawn-Zeit im neuen Team.  ·  M oder Esc: schließen";
            GUI.Label(new Rect(rect.x + 10, top + 160, w - 20, 40), hint, _small);
        }

        /// <summary>Eine Klasse als Karte: Abzeichen, Name, Beschreibung, Waffen mit Tasten, Blöcke und Tempo. Klick wählt sie.</summary>
        private void DrawClassCard(Rect r, PlayerClass cls, int number, bool selected, bool current, GameClient game)
        {
            var color = ClassColor(cls.Id);
            Fill(r, selected ? new Color(color.r * 0.45f, color.g * 0.45f, color.b * 0.45f, 0.95f) : new Color(0.12f, 0.12f, 0.14f, 0.95f));
            Frame(r, selected ? color : new Color(1, 1, 1, 0.2f), selected ? 3 : 1);
            if (GUI.Button(r, GUIContent.none, GUIStyle.none)) game.ChooseClass(cls);

            // Abzeichen (Platzhalter für die Klassen-Icons aus M9): Anfangsbuchstabe auf Klassenfarbe
            var badge = new Rect(r.x + 10, r.y + 10, 44, 44);
            Fill(badge, color);
            var old = GUI.color;
            GUI.color = Color.black;
            GUI.Label(badge, cls.DisplayName.Substring(0, 1), _badge);
            GUI.color = old;
            GUI.Label(new Rect(r.x + 62, r.y + 8, r.width - 70, 26), cls.DisplayName, _cardTitle);
            GUI.Label(new Rect(r.x + 62, r.y + 32, r.width - 70, 22), current ? "aktuell" : selected ? "gewählt" : $"Klasse {number}", _small);
            GUI.Label(new Rect(r.x + 10, r.y + 60, r.width - 20, 54), cls.Description, _small);

            var combat = game.Combat;
            var y = r.y + 116;
            GUI.Label(new Rect(r.x + 10, y, r.width - 20, 20), $"1  Spaten (Pause {cls.EditCooldownTicks / (float)combat.TickRate:0.0#} s)", _small);
            for (var slot = 1; slot <= cls.SlotCount; slot++)
            {
                y += 19;
                GUI.Label(new Rect(r.x + 10, y, r.width - 20, 20), $"{slot + 1}  {WeaponLine(cls.Weapon(slot), combat, cls.Health)}", _small);
            }
            var speed = cls.SpeedFactor > 1.001f ? $"schneller (+{(cls.SpeedFactor - 1) * 100:0} %)" : cls.SpeedFactor < 0.999f ? $"langsamer ({(cls.SpeedFactor - 1) * 100:0} %)" : "normales Tempo";
            GUI.Label(new Rect(r.x + 10, r.yMax - 46, r.width - 20, 20), $"{cls.Blocks} Blöcke  ·  {speed}", _small);
            GUI.Label(new Rect(r.x + 10, r.yMax - 26, r.width - 20, 20), $"{cls.Health} Leben", _small);
        }

        /// <summary>Waffe in einer Zeile: Name und wie viele Rumpftreffer aus der Nähe töten (Sprengstoff: Anzahl).</summary>
        private static string WeaponLine(WeaponSettings w, CombatSettings combat, int health)
        {
            if (w.IsProjectile) return w.Reserve > 0 ? $"{w.DisplayName} ({w.Magazine} + {w.Reserve})" : $"{w.DisplayName} ×{w.Magazine}";
            var hits = BalanceMath.HitsToKill(w, AoS2.Shared.Rig.HitZone.Torso, 0, combat, health);
            return $"{w.DisplayName} ({hits} Rumpftreffer)";
        }

        /// <summary>Farbe des Abzeichens je Klasse (Reihenfolge aus classes.json).</summary>
        private static Color ClassColor(int id)
        {
            switch (id)
            {
                case 1: return new Color(0.95f, 0.75f, 0.25f); // Miner: Gelb wie der Helm
                case 2: return new Color(0.55f, 0.75f, 0.45f);
                case 3: return new Color(0.7f, 0.6f, 0.45f);
                case 4: return new Color(0.9f, 0.45f, 0.3f);
                default: return new Color(0.7f, 0.7f, 0.75f);
            }
        }

        /// <summary>Punktetabelle: beide Teams nebeneinander, Spieler nach Kills sortiert; der eigene Name ist hervorgehoben.</summary>
        private void DrawScoreboard(GameClient game, float top)
        {
            var session = game.Session;
            const float colWidth = 330, rowHeight = 22;
            var left = Screen.width / 2f - colWidth - 10;
            var rows = 0;
            foreach (var team in new[] { Team.Blue, Team.Green })
            {
                var players = PlayersOf(session, team);
                rows = Mathf.Max(rows, players.Count);
                var x = team == Team.Blue ? left : Screen.width / 2f + 10;
                var color = GameClient.TeamColor(team);
                var height = 56 + Mathf.Max(players.Count, 1) * rowHeight;
                Fill(new Rect(x, top, colWidth, height), new Color(0, 0, 0, 0.7f));
                var old = GUI.color;
                GUI.color = color;
                GUI.Label(new Rect(x + 10, top + 4, colWidth - 20, 26), $"{GameClient.TeamName(team)}  ·  {session.Match.Score(team)} Punkte", _scoreboardTitle);
                GUI.color = old;
                GUI.Label(new Rect(x + 10, top + 30, colWidth - 20, rowHeight), "Spieler", _small);
                GUI.Label(new Rect(x + colWidth - 220, top + 30, 90, rowHeight), "Klasse", _small);
                GUI.Label(new Rect(x + colWidth - 130, top + 30, 60, rowHeight), "Kills", _small);
                GUI.Label(new Rect(x + colWidth - 65, top + 30, 60, rowHeight), "Tode", _small);
                var y = top + 52;
                foreach (var row in players)
                {
                    if (row.Me) Fill(new Rect(x + 4, y, colWidth - 8, rowHeight), new Color(1, 1, 1, 0.15f));
                    GUI.Label(new Rect(x + 10, y, colWidth - 240, rowHeight), row.Name, _text);
                    GUI.Label(new Rect(x + colWidth - 220, y, 90, rowHeight), row.Class, _text);
                    GUI.Label(new Rect(x + colWidth - 130, y, 60, rowHeight), row.Kills.ToString(), _text);
                    GUI.Label(new Rect(x + colWidth - 65, y, 60, rowHeight), row.Deaths.ToString(), _text);
                    y += rowHeight;
                }
            }

            var spectators = PlayersOf(session, Team.None);
            if (spectators.Count == 0) return;
            var names = string.Join(", ", spectators.ConvertAll(r => r.Name));
            Shadowed(new Rect(0, top + 62 + rows * rowHeight, Screen.width, 24), "Wählen noch ein Team: " + names, _name);
        }

        private struct Row
        {
            public string Name;
            public string Class;
            public int Kills;
            public int Deaths;
            public bool Me;
        }

        private static string ClassName(PlayerClass cls) => cls == null || cls.Id == 0 ? "–" : cls.DisplayName;

        private static List<Row> PlayersOf(ClientSession session, Team team)
        {
            var rows = new List<Row>();
            if (session.Team == team) rows.Add(new Row { Name = session.PlayerName, Class = ClassName(session.Class), Kills = session.Kills, Deaths = session.Deaths, Me = true });
            foreach (var p in session.RemotePlayers.Values)
                if (p.Team == team) rows.Add(new Row { Name = p.Name, Class = ClassName(session.ClassOf(p.Id)), Kills = p.Kills, Deaths = p.Deaths });
            rows.Sort((a, b) => a.Kills != b.Kills ? b.Kills.CompareTo(a.Kills) : a.Deaths.CompareTo(b.Deaths));
            return rows;
        }

        /// <summary>Matchende: Sieger, Endstand, Countdown bis zum nächsten Match und die Punktetabelle.</summary>
        private void DrawMatchEnd(GameClient game)
        {
            var session = game.Session;
            var match = session.Match;
            Fill(new Rect(0, 0, Screen.width, Screen.height), new Color(0, 0, 0, 0.35f));
            var title = match.Winner == Team.None ? "Unentschieden!" : $"Team {GameClient.TeamName(match.Winner)} gewinnt!";
            var old = GUI.color;
            GUI.color = match.Winner == Team.None ? Color.white : GameClient.TeamColor(match.Winner);
            GUI.Label(new Rect(0, 90, Screen.width, 50), title, _huge);
            GUI.color = old;
            var result = match.Winner == Team.None ? "" : match.Winner == session.Team ? "Dein Team hat gewonnen.  ·  " : "Dein Team hat verloren.  ·  ";
            Shadowed(new Rect(0, 140, Screen.width, 30), $"{result}Blau {match.ScoreBlue} : {match.ScoreGreen} Grün", _center);
            var left = SecondsLeft(game);
            Shadowed(new Rect(0, 170, Screen.width, 30), left > 0 ? $"Nächstes Match in {left} s" : "Nächstes Match …", _center);
            DrawScoreboard(game, 215);
        }

        // ---- Kampf ----

        private void DrawHealth(int health, int max, Team team, PlayerClass cls, PlayerClass next)
        {
            const float w = 220, h = 22;
            float x = 24, y = Screen.height - 50;
            var old = GUI.color;
            GUI.color = GameClient.TeamColor(team);
            GUI.Label(new Rect(x, y - 26, 400, 24), $"Team {GameClient.TeamName(team)}  ·  {ClassName(cls)}", _scoreboardTitle);
            GUI.color = old;
            if (next != null && next != cls) Shadowed(new Rect(x, y - 48, 400, 22), $"Nächster Spawn: {next.DisplayName} (K)", _text);
            var fraction = Mathf.Clamp01(health / (float)max);
            Fill(new Rect(x - 2, y - 2, w + 4, h + 4), new Color(0, 0, 0, 0.6f));
            Fill(new Rect(x, y, w * fraction, h), Color.Lerp(new Color(0.85f, 0.15f, 0.1f), new Color(0.3f, 0.8f, 0.3f), fraction));
            Shadowed(new Rect(x + 8, y - 1, w, h + 2), $"Leben {health}", _text);
        }

        private void DrawWeapons(GameClient game, Player.LocalPlayer player)
        {
            var cls = game.Session.Class;
            var weapons = player.Weapons;
            float right = Screen.width - 24, bottom = Screen.height - 24;

            var weapon = cls.Weapon(player.Slot);
            if (weapon != null)
            {
                var ammo = weapons.GetAmmo(player.Slot);
                Shadowed(new Rect(right - 300, bottom - 40, 300, 40), weapon.ReloadTicks > 0 ? $"{ammo.Magazine} / {ammo.Reserve}" : $"{ammo.Magazine}", _ammo);
                var status = weapons.Reload > 0 ? "lädt nach …" : weapons.Switch > 0 ? "…" : ammo.Magazine == 0 && ammo.Reserve == 0 ? "leer" : "";
                Shadowed(new Rect(right - 300, bottom - 64, 300, 24), $"{weapon.DisplayName}  {status}", _right);
            }
            else
            {
                Shadowed(new Rect(right - 300, bottom - 40, 300, 40), $"{game.Session.BlocksLeft}", _ammo);
                Shadowed(new Rect(right - 300, bottom - 64, 300, 24), "Spaten  ·  Blöcke", _right);
            }

            // Plätze unten in der Mitte
            var labels = new string[cls.SlotCount + 1];
            labels[0] = "1 Spaten";
            for (var s = 1; s <= cls.SlotCount; s++) labels[s] = $"{s + 1} {cls.Weapon(s).DisplayName}";
            const float slotWidth = 150;
            var x0 = Screen.width / 2f - labels.Length * slotWidth / 2;
            for (var s = 0; s < labels.Length; s++)
            {
                var rect = new Rect(x0 + s * slotWidth, Screen.height - 48, slotWidth - 8, 28);
                Fill(rect, s == player.Slot ? new Color(1, 1, 1, 0.35f) : new Color(0, 0, 0, 0.35f));
                Shadowed(rect, labels[s], _name);
            }
        }

        private void DrawKillFeed(CombatFeed feed)
        {
            var y = 80f;
            for (var i = feed.Kills.Count - 1; i >= 0; i--)
            {
                var k = feed.Kills[i];
                if (Time.time - k.Time > KillFeedSeconds) continue;
                var how = k.Explosion ? " (Explosion)" : k.Headshot ? " (Kopfschuss)" : "";
                var victim = Colored(k.Victim, k.VictimTeam);
                var text = k.Killer == k.Victim ? $"{victim} hat sich selbst erwischt [{k.Weapon}]" : $"{Colored(k.Killer, k.KillerTeam)}  [{k.Weapon}{how}]  {victim}";
                var rect = new Rect(Screen.width - 520, y, 500, 24);
                Fill(new Rect(rect.x + 100, rect.y, 400, rect.height), k.Mine ? new Color(1, 1, 1, 0.25f) : new Color(0, 0, 0, 0.35f));
                GUI.Label(rect, text, _right); // Namen in Teamfarbe (Rich Text), deshalb ohne Schatten
                y += 26;
            }
        }

        private static string Colored(string text, Team team) => $"<color=#{ColorUtility.ToHtmlStringRGB(GameClient.TeamColor(team))}>{text}</color>";

        private void DrawDeath(GameClient game)
        {
            var feed = game.Feed;
            var session = game.Session;
            var cause = feed.SwitchedTeam ? $"Teamwechsel zu {GameClient.TeamName(session.Team)}"
                : feed.KilledBy != null ? $"Getötet von {feed.KilledBy} ({feed.KilledWith})" : "Du hast dich selbst erwischt";
            var seconds = Mathf.CeilToInt(session.RespawnTicks / (float)game.Movement.TickRate);
            var next = game.MenuClass?.DisplayName ?? "";
            Fill(new Rect(0, 0, Screen.width, Screen.height), new Color(0.3f, 0, 0, 0.25f));
            Shadowed(new Rect(0, Screen.height / 2f - 70, Screen.width, 40), cause, _big);
            Shadowed(new Rect(0, Screen.height / 2f - 20, Screen.width, 30), seconds > 0 ? $"Respawn in {seconds} s als {next}" : $"Respawn als {next} …", _center);
            if (!game.TeamMenuOpen) Shadowed(new Rect(0, Screen.height / 2f + 12, Screen.width, 26), "K: Klasse wechseln", _name);
        }

        /// <summary>Zielfernrohr: schwarzer Rand mit rundem Ausschnitt (aus Balken angenähert) und Absehen über den ganzen Schirm.</summary>
        private void DrawScope(float cx, float cy)
        {
            var radius = Screen.height * 0.45f;
            var black = new Color(0, 0, 0, 0.92f);
            Fill(new Rect(0, 0, cx - radius, Screen.height), black);
            Fill(new Rect(cx + radius, 0, Screen.width - cx - radius, Screen.height), black);
            const int steps = 24;
            for (var i = 0; i < steps; i++)
            {
                // Je Zeile über und unter dem Kreis: was außerhalb des Kreises liegt, schwarz füllen.
                var y0 = cy - radius + i * radius / steps;
                var dy = cy - (y0 + radius / steps);
                var half = Mathf.Sqrt(Mathf.Max(0, radius * radius - dy * dy));
                var band = radius / steps + 1;
                Fill(new Rect(cx - radius, y0, radius - half, band), black);
                Fill(new Rect(cx + half, y0, radius - half, band), black);
                var y1 = cy + radius - (i + 1) * radius / steps;
                Fill(new Rect(cx - radius, y1, radius - half, band), black);
                Fill(new Rect(cx + half, y1, radius - half, band), black);
            }
            Fill(new Rect(cx - radius, cy - 0.5f, 2 * radius, 1), new Color(0, 0, 0, 0.9f));
            Fill(new Rect(cx - 0.5f, cy - radius, 1, 2 * radius), new Color(0, 0, 0, 0.9f));
            Fill(new Rect(cx - 2, cy - 2, 4, 4), new Color(0.9f, 0.1f, 0.1f, 0.9f));
        }

        /// <summary>Kreuz aus vier Strichen schräg ums Fadenkreuz, wenn der Server einen eigenen Treffer meldet (rot bei Kill).</summary>
        private void DrawHitMarker(CombatFeed feed, float cx, float cy)
        {
            var age = Time.time - feed.LastHitTime;
            if (age > HitMarkerSeconds) return;
            var color = feed.LastHitKilled ? new Color(1, 0.2f, 0.2f) : feed.LastHitHeadshot ? new Color(1, 0.85f, 0.2f) : Color.white;
            color.a = 1 - age / HitMarkerSeconds;
            var old = GUI.color;
            var matrix = GUI.matrix;
            foreach (var angle in new[] { 45f, 135f, 225f, 315f })
            {
                GUI.matrix = matrix;
                GUIUtility.RotateAroundPivot(angle, new Vector2(cx, cy));
                GUI.color = new Color(0, 0, 0, color.a * 0.6f);
                GUI.DrawTexture(new Rect(cx + 7, cy - 2, 12, 4), _white);
                GUI.color = color;
                GUI.DrawTexture(new Rect(cx + 8, cy - 1, 10, 2), _white);
            }
            GUI.matrix = matrix;
            GUI.color = old;
        }

        /// <summary>Roter Rand bei Schaden; auf der Seite, von der der Angreifer kam, stärker.</summary>
        private void DrawDamageFlash(CombatFeed feed)
        {
            var age = Time.time - feed.LastDamageTime;
            if (age > DamageFlashSeconds) return;
            var a = (1 - age / DamageFlashSeconds) * Mathf.Clamp01(feed.LastDamageAmount / 40f + 0.3f) * 0.5f;
            float left = a, right = a, top = a, bottom = a;
            var camera = Camera.main;
            if (feed.DamageFrom is { } from && camera != null)
            {
                var local = camera.transform.InverseTransformPoint(from);
                var angle = Mathf.Atan2(local.x, local.z);
                right = a * Mathf.Clamp01(0.3f + Mathf.Sin(angle));
                left = a * Mathf.Clamp01(0.3f - Mathf.Sin(angle));
                top = a * Mathf.Clamp01(0.3f + Mathf.Cos(angle));
                bottom = a * Mathf.Clamp01(0.3f - Mathf.Cos(angle));
            }
            var red = new Color(0.8f, 0, 0);
            const float edge = 60;
            Fill(new Rect(0, 0, edge, Screen.height), new Color(red.r, red.g, red.b, left));
            Fill(new Rect(Screen.width - edge, 0, edge, Screen.height), new Color(red.r, red.g, red.b, right));
            Fill(new Rect(0, 0, Screen.width, edge), new Color(red.r, red.g, red.b, top));
            Fill(new Rect(0, Screen.height - edge, Screen.width, edge), new Color(red.r, red.g, red.b, bottom));
        }

        // ---- Allgemein ----

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
                var old = GUI.color;
                GUI.color = new Color(0, 0, 0, 0.8f);
                GUI.Label(new Rect(screen.x - 99, Screen.height - screen.y - 11, 200, 24), remote.Name, _name);
                GUI.color = GameClient.TeamColor(remote.Team);
                GUI.Label(new Rect(screen.x - 100, Screen.height - screen.y - 12, 200, 24), remote.Name, _name);
                GUI.color = old;
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
                case BlockEditResult.ProtectedZone: return "Hier kann man nichts verändern (Spawnzone oder außerhalb der Kampfzone).";
                case BlockEditResult.MatchOver: return "Das Match ist vorbei.";
                case BlockEditResult.NoBlocks: return "Keine Blöcke mehr. Abbauen gibt Blöcke zurück, der Respawn füllt auf.";
                case BlockEditResult.Dead: return "";
                default: return "";
            }
        }

        private void Fill(Rect rect, Color color)
        {
            var old = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, _white);
            GUI.color = old;
        }

        private void Frame(Rect rect, Color color, float thickness)
        {
            Fill(new Rect(rect.x, rect.y, rect.width, thickness), color);
            Fill(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), color);
            Fill(new Rect(rect.x, rect.y, thickness, rect.height), color);
            Fill(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), color);
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
