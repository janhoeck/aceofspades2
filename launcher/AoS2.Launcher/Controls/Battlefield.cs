using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AoS2.Launcher.Controls;

/// <summary>Ein Block des Schlachtfelds: Spalte, Zeile, Farbe.</summary>
public readonly record struct Block(int X, int Y, Color Color);

/// <summary>
/// Erzeugt das Voxel-Schlachtfeld im Kopf des Launchers: Gelände mit Gras und Erde, links blaue,
/// rechts grüne Burgen, in der Mitte ein Krater mit losen Blöcken, oben ein paar Wolken.
/// Gleicher <c>seed</c> ergibt immer dasselbe Bild.
/// </summary>
public static class BattlefieldGenerator
{
    public static readonly Color Sky = Color.Parse("#1A2333");
    private static readonly Color Cloud = Color.Parse("#222D40");
    private static readonly Color Grass = Color.Parse("#7E9A4A");
    private static readonly Color Dirt = Color.Parse("#6B5236");
    private static readonly Color DeepDirt = Color.Parse("#4A3926");
    private static readonly Color Blue = Color.Parse("#2F6FE4");
    private static readonly Color BlueDark = Color.Parse("#2659B8");
    private static readonly Color Green = Color.Parse("#3FA34D");
    private static readonly Color GreenDark = Color.Parse("#33853E");

    public static IReadOnlyList<Block> Generate(int columns, int rows, int seed)
    {
        var blocks = new List<Block>();
        var random = new Lehmer(seed);
        void Put(int x, int y, Color c)
        {
            if (x >= 0 && x < columns && y >= 0 && y < rows)
                blocks.Add(new Block(x, y, c));
        }

        for (var i = 0; i < 6; i++)
        {
            int cx = (int)(random.Next() * columns), cy = 1 + (int)(random.Next() * 3), length = 3 + (int)(random.Next() * 5);
            for (var k = 0; k < length; k++)
                Put(cx + k, cy, Cloud);
        }

        // Geländehöhe je Spalte, in der Mitte ein Krater.
        var ground = new int[columns];
        for (var x = 0; x < columns; x++)
        {
            var distance = Math.Abs((double)x / columns - 0.5);
            var g = rows - 5 + (int)Math.Round(Math.Sin(x * 0.35) + Math.Sin(x * 0.11) * 1.5);
            if (distance < 0.1)
                g += (int)Math.Round((0.1 - distance) * 40);
            ground[x] = Math.Min(rows - 1, g);
        }
        for (var x = 0; x < columns; x++)
            for (var y = ground[x]; y < rows; y++)
                Put(x, y, y == ground[x] ? Grass : y < ground[x] + 3 ? Dirt : DeepDirt);

        void Fort(int x0, int width, int height, Color c1, Color c2)
        {
            for (var x = Math.Max(0, x0); x < Math.Min(columns, x0 + width); x++)
            {
                var g = ground[x];
                var h = height + ((x - x0) % 3 == 0 ? 1 : 0); // Zinnen
                for (var y = g - h; y < g; y++)
                {
                    // Einschusslöcher
                    if (x > x0 + 1 && x < x0 + width - 2 && y > g - height + 2 && y < g - 1 && (x + y) % 5 == 0)
                        continue;
                    Put(x, y, (x + y) % 3 != 0 ? c1 : c2);
                }
            }
        }
        Fort(4, 9, 7, Blue, BlueDark);
        Fort(15, 6, 4, Blue, BlueDark);
        Fort(columns - 13, 9, 7, Green, GreenDark);
        Fort(columns - 21, 6, 4, Green, GreenDark);

        for (var i = 0; i < 16; i++)
        {
            var x = (int)(columns * (0.4 + random.Next() * 0.2));
            var y = ground[x] - 1 - (int)(random.Next() * 5);
            Put(x, y, random.Next() < 0.5 ? Dirt : Grass);
        }

        return blocks;
    }

    /// <summary>Einfacher, reproduzierbarer Zufall (gleiche Formel wie im Design-Entwurf).</summary>
    private sealed class Lehmer(int seed)
    {
        private long _state = (long)seed * 7919 % 2147483646 + 1;

        public double Next()
        {
            _state = _state * 16807 % 2147483647;
            return _state / 2147483647.0;
        }
    }
}

/// <summary>Zeichnet das Schlachtfeld. Jeder Block bekommt oben eine helle und rechts/unten eine dunkle Kante.</summary>
public sealed class BattlefieldView : Control
{
    public const int BlockSize = 16;

    public static readonly StyledProperty<int> SeedProperty = AvaloniaProperty.Register<BattlefieldView, int>(nameof(Seed), 7);

    private static readonly IBrush Highlight = new SolidColorBrush(Color.FromArgb(26, 255, 255, 255));
    private static readonly IBrush Shade = new SolidColorBrush(Color.FromArgb(71, 0, 0, 0));
    private static readonly IBrush SkyBrush = new SolidColorBrush(BattlefieldGenerator.Sky);

    private readonly Dictionary<Color, IBrush> _brushes = [];
    private IReadOnlyList<Block> _blocks = [];
    private Size _generatedFor;

    static BattlefieldView()
    {
        AffectsRender<BattlefieldView>(SeedProperty);
    }

    public int Seed
    {
        get => GetValue(SeedProperty);
        set => SetValue(SeedProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SeedProperty)
            _generatedFor = default;
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        context.FillRectangle(SkyBrush, new Rect(size));

        if (size != _generatedFor)
        {
            var columns = (int)Math.Ceiling(size.Width / BlockSize);
            var rows = (int)Math.Ceiling(size.Height / BlockSize);
            _blocks = columns > 0 && rows > 0 ? BattlefieldGenerator.Generate(columns, rows, Seed) : [];
            _generatedFor = size;
        }

        foreach (var block in _blocks)
        {
            if (!_brushes.TryGetValue(block.Color, out var brush))
                _brushes[block.Color] = brush = new SolidColorBrush(block.Color);

            double x = block.X * BlockSize, y = block.Y * BlockSize;
            context.FillRectangle(brush, new Rect(x, y, BlockSize, BlockSize));
            context.FillRectangle(Highlight, new Rect(x, y, BlockSize, 2));
            context.FillRectangle(Shade, new Rect(x, y + BlockSize - 2, BlockSize, 2));
            context.FillRectangle(Shade, new Rect(x + BlockSize - 2, y, 2, BlockSize - 2));
        }
    }
}
