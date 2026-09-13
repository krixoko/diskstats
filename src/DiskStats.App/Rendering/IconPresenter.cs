using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DiskStats.App.Rendering;

/// <summary>
/// Zeichnet ein Bildzeichen aus <see cref="Icons"/>.
///
/// Es braucht dieses Steuerelement, weil die Zeichen Konturen auf einem Feld von 24x24 sind
/// und kein Path mit Stretch="Uniform" das richtig wiedergibt: Der skaliert auf die tatsaech-
/// liche Ausdehnung des jeweiligen Pfads, und die ist bei jedem Zeichen anders. Ein Pfeil
/// waere dann groesser als ein Zahnrad, und die Strichstaerken lieafen auseinander. Hier wird
/// immer das Feld skaliert, nie der Inhalt — dadurch bleiben alle Zeichen gleich schwer.
/// </summary>
public sealed class IconPresenter : Control
{
    public static readonly StyledProperty<string> DataProperty =
        AvaloniaProperty.Register<IconPresenter, string>(nameof(Data), string.Empty);

    public static readonly StyledProperty<double> IconSizeProperty =
        AvaloniaProperty.Register<IconPresenter, double>(nameof(IconSize), 18);

    public static readonly StyledProperty<IBrush?> BrushProperty =
        AvaloniaProperty.Register<IconPresenter, IBrush?>(nameof(Brush));

    static IconPresenter()
    {
        AffectsRender<IconPresenter>(DataProperty, IconSizeProperty, BrushProperty);
        AffectsMeasure<IconPresenter>(IconSizeProperty);
    }

    public string Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    /// <summary>Kantenlaenge in Bildpunkten. Das Zeichen fuellt dieses Quadrat.</summary>
    public double IconSize
    {
        get => GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public IBrush? Brush
    {
        get => GetValue(BrushProperty);
        set => SetValue(BrushProperty, value);
    }

    // Dieselben Zeichen erscheinen in vielen Zeilen; einmal zerlegen genuegt.
    private static readonly Dictionary<string, Geometry> Parsed = [];

    private IPen? _pen;
    private IBrush? _penBrush;

    protected override Size MeasureOverride(Size available) => new(IconSize, IconSize);

    public override void Render(DrawingContext context)
    {
        if (Brush is null || Data.Length == 0) return;

        if (!Parsed.TryGetValue(Data, out Geometry? geometry))
        {
            geometry = Geometry.Parse(Data);
            Parsed[Data] = geometry;
        }

        double scale = IconSize / Icons.Grid;

        // Der Stift bleibt, solange der Pinsel derselbe ist — und der wechselt nur mit dem Thema.
        // Ein neuer Stift je Bild waere bei dutzenden Zeichen in der Seitenleiste reiner Abfall.
        if (_pen is null || !ReferenceEquals(_penBrush, Brush))
        {
            _penBrush = Brush;
            _pen = new Pen(Brush, Icons.Stroke)
            {
                LineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
        }
        IPen pen = _pen;

        // Die Strichstaerke wird von der Transformation mitskaliert — genau das ist gewollt,
        // sonst waere ein kleines Zeichen im Verhaeltnis fetter als ein grosses.
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
            context.DrawGeometry(null, pen, geometry);
    }
}
