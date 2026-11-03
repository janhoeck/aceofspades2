using System.Collections.Generic;

namespace AoS2.Shared.Voxels
{
    /// <summary>Ein Blocktyp aus <c>data/blocks.json</c>.</summary>
    public sealed class BlockType
    {
        private readonly string[][] _faceTextures;

        public BlockType(byte id, string name, string displayName, string[][] faceTextures, int health, bool indestructible, bool buildable)
        {
            Id = id;
            Name = name;
            DisplayName = displayName;
            _faceTextures = faceTextures;
            Health = health;
            Indestructible = indestructible;
            Buildable = buildable;
        }

        /// <summary>Feste ID (1–255). 0 ist Luft. Steht so in der Welt im Speicher und im Netzwerk.</summary>
        public byte Id { get; }

        /// <summary>Stabiler Name, über den Maps und Zuordnungsdateien den Typ finden (z. B. <c>grass</c>).</summary>
        public string Name { get; }

        public string DisplayName { get; }

        /// <summary>Lebenspunkte (Schaden durch Waffen ab M5).</summary>
        public int Health { get; }

        /// <summary>Kann weder abgebaut noch gesprengt werden (Grundgestein, Spawn-Blöcke).</summary>
        public bool Indestructible { get; }

        /// <summary>Spieler können diesen Typ bauen.</summary>
        public bool Buildable { get; }

        /// <summary>Texturnamen (ohne <c>.png</c>) einer Seite; mehrere Einträge sind Varianten.</summary>
        public IReadOnlyList<string> GetTextures(BlockFace face) => _faceTextures[(int)face];

        public override string ToString() => $"{Name} ({Id})";
    }
}
