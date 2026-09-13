using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using DiskStats.Core.Scanning;

using DiskStats.App.Localization;
using DiskStats.App.Rendering;

namespace DiskStats.App.Controls;

/// <summary>
/// Zeigt, worauf gerade gescannt wird — und wie weit.
///
/// In der Mitte das Symbol des Datentraegertyps, darum ein Ring. Solange ein Scan laeuft,
/// wandert der Ring. Ist der Umfang bekannt (beim Scan eines ganzen Laufwerks liefert Windows
/// die belegten Bytes), fuellt er sich anteilig; sonst kreist ein Segment. Das ist die
/// ehrlichere Loesung als ein erfundener Prozentwert: Bei einem Unterordner weiss niemand
/// vorher, wie viel kommt.
/// </summary>
public sealed class DriveBadge : Control
{
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private StorageKind _kind = StorageKind.Unknown;
    private bool _busy;
    private double? _progress;

    public DriveBadge()
    {
        ActualThemeVariantChanged += OnThemeChanged;
        Width = 44;
        Height = 44;

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render,
            (_, _) => InvalidateVisual());

        // Dieser Konstruktor startet die Uhr sofort. Ohne den Stopp tickte sie von der ersten
        // Sekunde an mit sechzig Bildern, obwohl noch gar kein Scan laeuft.
        _timer.Stop();
    }

    /// <summary>Fuer Tests: ob die Bilduhr laeuft.</summary>
    internal bool AnimationRunning => _timer.IsEnabled;

    /// <summary>
    /// Ausserhalb des Fensters tickt die Uhr ins Leere. Ohne den Stopp liefe sie weiter, hielte
    /// das Abzeichen am Leben und zeichnete sechzigmal je Sekunde, was niemand sieht.
    /// </summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_busy) _timer.Start();
    }

    public StorageKind Kind
    {
        get => _kind;
        set { if (_kind == value) return; _kind = value; InvalidateVisual(); }
    }

    /// <summary>Anteil zwischen 0 und 1, oder null, wenn der Umfang unbekannt ist.</summary>
    public double? Progress
    {
        get => _progress;
        set { _progress = value; if (_busy) InvalidateVisual(); }
    }

    public bool Busy
    {
        get => _busy;
        set
        {
            if (_busy == value) return;
            _busy = value;

            if (_busy) _timer.Start();
            else _timer.Stop();

            InvalidateVisual();
        }
    }

    public string Description => NameFor(_kind);

    /// <summary>Die Bezeichnung der Art, in der eingestellten Sprache.</summary>
    public static string NameFor(StorageKind kind) => kind switch
    {
        StorageKind.Nvme => Loc.T("Disk_Nvme"),
        StorageKind.Ssd => Loc.T("Disk_Ssd"),
        StorageKind.Hdd => Loc.T("Disk_Hdd"),
        StorageKind.Removable => Loc.T("Disk_Removable"),
        StorageKind.Network => Loc.T("Disk_Network"),
        StorageKind.Optical => Loc.T("Disk_Optical"),
        StorageKind.Ram => Loc.T("Disk_Ram"),
        StorageKind.Cloud => Loc.T("Disk_Cloud"),
        _ => Loc.T("Disk_Unknown"),
    };

    /// <summary>
    /// Beim Themenwechsel neu zeichnen. Ohne das behielt das Abzeichen die Farben des vorigen
    /// Themas — im dunklen Thema ein dunkles Zeichen auf dunklem Grund, praktisch unsichtbar.
    /// Die uebrige Oberflaeche faellt das nicht auf, weil sie ueber DynamicResource bindet;
    /// hier werden die Farben beim Zeichnen geholt, und dazu muss gezeichnet werden.
    /// </summary>
    private void OnThemeChanged(object? sender, EventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        double radius = Math.Min(Bounds.Width, Bounds.Height) / 2 - 3;

        IBrush ink = GetBrush("Ink", Color.FromRgb(0x2A, 0x27, 0x24));
        IBrush track = GetBrush("Track", Color.FromRgb(0xEA, 0xE4, 0xD8));

        // Stifte aus dem Zwischenspeicher: Waehrend eines Scans laeuft das hier sechzigmal je
        // Sekunde, und die Pinsel wechseln nur mit dem Thema.
        context.DrawEllipse(null, RenderCache.Pen(track, 2.5), center, radius, radius);

        if (_busy) DrawProgress(context, center, radius, ink);

        DrawGlyph(context, center, ink);
    }

    private void DrawProgress(DrawingContext context, Point center, double radius, IBrush ink)
    {
        IPen pen = RenderCache.RoundPen(ink, 2.5);
        double turn = _clock.Elapsed.TotalSeconds;

        double start, sweep;
        if (_progress is { } value)
        {
            // Bekannter Umfang: der Ring fuellt sich von oben im Uhrzeigersinn.
            start = -Math.PI / 2;
            sweep = Math.Clamp(value, 0.01, 1) * Math.Tau;
        }
        else
        {
            // Unbekannter Umfang: ein Segment kreist. Die schwankende Laenge macht sichtbar,
            // dass gearbeitet wird, ohne einen Fortschritt zu behaupten.
            start = turn * 2.6 % Math.Tau;
            sweep = 1.1 + 0.55 * Math.Sin(turn * 1.7);
        }

        context.DrawGeometry(null, pen, ArcOf(center, radius, start, sweep));
    }

    private static Geometry ArcOf(Point center, double radius, double start, double sweep)
    {
        var geometry = new StreamGeometry();
        using StreamGeometryContext ctx = geometry.Open();

        Point from = Polar(center, radius, start);
        Point to = Polar(center, radius, start + sweep);

        ctx.BeginFigure(from, isFilled: false);
        ctx.ArcTo(to, new Size(radius, radius), 0, sweep > Math.PI, SweepDirection.Clockwise);
        ctx.EndFigure(isClosed: false);

        return geometry;
    }

    private static Point Polar(Point center, double radius, double angle)
        => new(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));

    /// <summary>
    /// Welches Zeichen zu welcher Art Datentraeger gehoert. Oeffentlich, weil die Liste in der
    /// Seitenleiste dieselbe Zuordnung braucht — zwei Listen waeren zwei Gelegenheiten,
    /// auseinanderzulaufen.
    /// </summary>
    public static string IconFor(StorageKind kind) => kind switch
    {
        StorageKind.Nvme => Icons.MemoryStick,
        StorageKind.Ssd => Icons.Microchip,
        StorageKind.Hdd => Icons.HardDrive,
        StorageKind.Removable => Icons.Usb,
        StorageKind.Network => Icons.Network,
        StorageKind.Optical => Icons.Disc3,
        StorageKind.Ram => Icons.Cpu,
        StorageKind.Cloud => Icons.Cloud,
        _ => Icons.Database,
    };

    /// <summary>
    /// Zeichnet das Typsymbol mittig, als Kontur auf dem 24er-Feld der Lucide-Zeichen.
    ///
    /// Skaliert wird das Feld, nicht der Pfad: Sonst waere ein schmales Zeichen wie der
    /// USB-Stecker so breit gezogen wie das quadratische Laufwerkssymbol, und die
    /// Strichstaerken lieafen auseinander.
    /// </summary>
    private void DrawGlyph(DrawingContext context, Point center, IBrush ink)
    {
        // Das Zeichen einmal zerlegen, nicht je Bild: Der Pfadtext aendert sich nur mit der Art.
        if (_glyph is null || _glyphFor != _kind)
        {
            _glyphFor = _kind;
            _glyph = Geometry.Parse(IconFor(_kind));
        }

        const double target = 19;
        double scale = target / Icons.Grid;
        IPen pen = RenderCache.RoundPen(ink, Icons.Stroke);

        using (context.PushTransform(
            Matrix.CreateTranslation(-Icons.Grid / 2, -Icons.Grid / 2)
            * Matrix.CreateScale(scale, scale)
            * Matrix.CreateTranslation(center.X, center.Y)))
        {
            context.DrawGeometry(null, pen, _glyph);
        }
    }

    private Geometry? _glyph;
    private StorageKind _glyphFor;

    private IBrush GetBrush(string key, Color fallback)
        => this.TryFindResource(key, ActualThemeVariant, out object? value) && value is IBrush brush
            ? brush
            : RenderCache.Brush(fallback);
}
