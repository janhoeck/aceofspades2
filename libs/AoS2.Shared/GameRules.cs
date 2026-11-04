using System;
using AoS2.Shared.Combat;
using AoS2.Shared.Movement;
using AoS2.Shared.Net;

namespace AoS2.Shared
{
    /// <summary>
    /// Die Spielregeln als Text, so wie sie in <c>data/</c> stehen: Bewegung, Kampf, Waffen und Klassen. Der Server schickt
    /// sie beim Beitritt an den Client (<see cref="MessageType.GameData"/>). So rechnen beide immer mit denselben Werten, auch
    /// wenn der Betreiber eines Servers sie geändert hat (Balancing ohne neues Spiel-Update).
    /// </summary>
    public sealed class GameDataFiles
    {
        /// <summary>Höchstens so groß darf jede Datei in der Nachricht sein.</summary>
        public const int MaxFileBytes = 64 * 1024;

        public string Movement = "";
        public string Combat = "";
        public string Weapons = "";
        public string Classes = "";

        /// <summary>Liest die vier Dateien aus einem Ordner (<c>movement.json</c>, <c>combat.json</c>, <c>weapons.json</c>, <c>classes.json</c>).</summary>
        public static GameDataFiles Load(Func<string, string> readFile) => new GameDataFiles
        {
            Movement = readFile("movement.json"),
            Combat = readFile("combat.json"),
            Weapons = readFile("weapons.json"),
            Classes = readFile("classes.json"),
        };

        public void Write(NetWriter w)
        {
            w.WriteByte((byte)MessageType.GameData);
            w.WriteString(Movement);
            w.WriteString(Combat);
            w.WriteString(Weapons);
            w.WriteString(Classes);
        }

        /// <summary>Liest den Inhalt nach dem Typ-Byte.</summary>
        public static GameDataFiles Read(NetReader r)
        {
            var files = new GameDataFiles
            {
                Movement = r.ReadString(MaxFileBytes),
                Combat = r.ReadString(MaxFileBytes),
                Weapons = r.ReadString(MaxFileBytes),
                Classes = r.ReadString(MaxFileBytes),
            };
            r.ExpectEnd();
            return files;
        }
    }

    /// <summary>
    /// Alle Spielregeln zusammen, gelesen aus <see cref="GameDataFiles"/> und dem Spielermodell (Kollisionshöhe je Ausrüstung).
    /// Ungültige Daten werden mit einer Meldung abgelehnt, die Datei und Eintrag nennt (<see cref="FormatException"/>).
    /// </summary>
    public sealed class GameRules
    {
        private GameRules(GameDataFiles files, MovementSettings movement, CombatSettings combat, PlayerClasses classes)
        {
            Files = files;
            Movement = movement;
            Combat = combat;
            Classes = classes;
        }

        /// <summary>Die Texte, aus denen die Regeln stammen (der Server schickt sie so an die Clients).</summary>
        public GameDataFiles Files { get; }

        /// <summary>Bewegung ohne Klasse (Zuschauer); Spieler rechnen mit <see cref="PlayerClass.Movement"/>.</summary>
        public MovementSettings Movement { get; }

        public CombatSettings Combat { get; }
        public PlayerClasses Classes { get; }

        public static GameRules Parse(GameDataFiles files, string playerRigJson)
        {
            var movement = MovementSettings.Parse(files.Movement, playerRigJson);
            var combat = CombatSettings.Parse(files.Combat, files.Weapons, movement.TickRate);
            var classes = PlayerClasses.Parse(files.Classes, combat, files.Movement, playerRigJson);
            return new GameRules(files, movement, combat, classes);
        }
    }
}
