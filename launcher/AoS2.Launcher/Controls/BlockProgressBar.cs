using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AoS2.Launcher.Controls;

/// <summary>
/// Fortschrittsbalken aus Blöcken, der sich Block für Block füllt. Bei <see cref="IsIndeterminate"/>
/// wandert ein kurzer Block-Zug hin und her.
/// </summary>
public sealed class BlockProgressBar : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<BlockProgressBar, double>(nameof(Value));

    public static readonly StyledProperty<int> SegmentsProperty =
        AvaloniaProperty.Register<BlockProgressBar, int>(nameof(Segments), 30);

    public static readonly StyledProperty<bool> IsIndeterminateProperty =
        AvaloniaProperty.Register<BlockProgressBar, bool>(nameof(IsIndeterminate));

    private const double Gap = 4, Edge = 4;
    private static readonly IBrush Fill = new SolidColorBrush(Color.Parse("#F2C14E"));
    private static readonly IBrush FillEdge = new SolidColorBrush(Color.Parse("#A9801F"));
    private static readonly IBrush Empty = new SolidColorBrush(Color.Parse("#232A36"));
    private static readonly IBrush EmptyEdge = new SolidColorBrush(Color.Parse("#181D26"));

    private DispatcherTimerHandle? _animation;
    private int _tick;

    static BlockProgressBar()
    {
        AffectsRender<BlockProgressBar>(ValueProperty, SegmentsProperty, IsIndeterminateProperty);
    }

    /// <summary>0 bis 100.</summary>
    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public int Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public bool IsIndeterminate
    {
        get => GetValue(IsIndeterminateProperty);
        set => SetValue(IsIndeterminateProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsIndeterminateProperty || change.Property == IsVisibleProperty)
            UpdateAnimation();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateAnimation();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _animation?.Dispose();
        _animation = null;
    }

    private void UpdateAnimation()
    {
        var run = IsIndeterminate && IsVisible && VisualRoot is not null;
        if (run && _animation is null)
        {
            _animation = new DispatcherTimerHandle(TimeSpan.FromMilliseconds(70), () =>
            {
                _tick++;
                InvalidateVisual();
            });
        }
        else if (!run && _animation is not null)
        {
            _animation.Dispose();
            _animation = null;
        }
    }

    public override void Render(DrawingContext context)
    {
        var count = Math.Max(1, Segments);
        var width = (Bounds.Width - Gap * (count - 1)) / count;
        var height = Bounds.Height;
        if (width <= 0 || height <= Edge)
            return;

        // Unbestimmt: 5 Blöcke laufen von links nach rechts und wieder zurück.
        const int Train = 5;
        var period = (count - Train) * 2;
        var position = period > 0 ? _tick % period : 0;
        var start = position < count - Train ? position : period - position;
        var filled = (int)Math.Round(Math.Clamp(Value, 0, 100) / 100 * count);

        for (var i = 0; i < count; i++)
        {
            var on = IsIndeterminate ? i >= start && i < start + Train : i < filled;
            var x = i * (width + Gap);
            context.FillRectangle(on ? Fill : Empty, new Rect(x, 0, width, height - Edge));
            context.FillRectangle(on ? FillEdge : EmptyEdge, new Rect(x, height - Edge, width, Edge));
        }
    }

    /// <summary>Kleiner Wrapper um den UI-Timer, damit er sich sauber abmelden lässt.</summary>
    private sealed class DispatcherTimerHandle : IDisposable
    {
        private readonly Avalonia.Threading.DispatcherTimer _timer;

        public DispatcherTimerHandle(TimeSpan interval, Action onTick)
        {
            _timer = new Avalonia.Threading.DispatcherTimer { Interval = interval };
            _timer.Tick += (_, _) => onTick();
            _timer.Start();
        }

        public void Dispose() => _timer.Stop();
    }
}
