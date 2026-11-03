namespace AoS2.Shared.Voxels
{
    /// <summary>Die sechs Seiten eines Blocks. Reihenfolge ist Teil der Rendering-Tabellen im Client.</summary>
    public enum BlockFace
    {
        /// <summary>+X (rechts/Osten)</summary>
        PosX = 0,
        /// <summary>-X (links/Westen)</summary>
        NegX = 1,
        /// <summary>+Y (oben)</summary>
        Top = 2,
        /// <summary>-Y (unten)</summary>
        Bottom = 3,
        /// <summary>+Z (vorne/Norden)</summary>
        PosZ = 4,
        /// <summary>-Z (hinten/Süden)</summary>
        NegZ = 5,
    }

    public static class BlockFaces
    {
        public const int Count = 6;

        public static readonly int[] DirX = { 1, -1, 0, 0, 0, 0 };
        public static readonly int[] DirY = { 0, 0, 1, -1, 0, 0 };
        public static readonly int[] DirZ = { 0, 0, 0, 0, 1, -1 };
    }
}
