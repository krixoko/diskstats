using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using DiskStats.App.Rendering.Engines;
using DiskStats.Core.Storage;

namespace DiskStats.App.Rendering;

/// <summary>
/// Zeichnet den NodeStore in der gerade gewaehlten Ansicht. Ein einzelner Control mit eigener
/// Zeichenroutine statt tausender Avalonia-Elemente: Bei zehntausend Formen waere ein Control
/// je Form hoffnungslos, ein Zeichenaufruf je Form dagegen unproblematisch.
///
/// Die Ansichten selbst stecken in den Layout-Engines; hier bleibt nur, was allen gemeinsam
/// ist — Farbe, Treffererkennung, Beschriftung, Hover und Auswahl.
/// </summary>
public sealed class ChartView : Control
{
    private static readonly ILayoutEngine[] Engines =
    [
        new TreemapEngine(), new FoldersEngine(), new SunburstEngine(),
        new FlameEngine(), new BubblesEngine(), new MindMapEngine(), new TopSizesEngine(),
        new AgeMapEngine(),
    ];

    public static IReadOnlyList<ILayoutEngine> AllEngines => Engines;

    /// <summary>Fuge zwischen Nachbarkacheln. Traegt die Gliederung, ohne Linien zu ziehen.</summary>
    private const double TileGap = 0.75;

    private const double MaxCornerRadius = 5;

    private ILayoutEngine _engine = Engines[0];
    private ViewKind _kind = ViewKind.Treemap;
    private NodeStore? _store;
    private int _rootIndex;
    private ColorMode _colorMode = ColorMode.ByFolder;
    private string? _highlightExtension;

    private readonly NodeColoring _coloring = new();
    private readonly ShapeAnimator _animator = new();
    private readonly DispatcherTimer _frames;
    private bool _animateNextLayout = true;
    private IReadOnlyList<VisualItem> _items = [];
    private Geometry?[] _geometries = [];
    private Rect _laidOutFor;
    private int _hoverItem = -1;
    private int _selectedNode = -1;

    private ChartTheme? _theme;
    private IPen? _hoverPen;
    private IPen? _selectionPen;
    private IPen? _focusPen;
    private IBrush? _labelBrush;
    private IBrush? _labelHalo;
    private IBrush? _rowHighlight;
    private bool _darkTheme;

    /// <summary>Gestrichelter Fokusrahmen. Ein Muster fuer alle, deshalb einmal je Prozess.</summary>
    private static readonly DashStyle FocusDash = new([4, 3], 0);

    private Point _pointer;
    private IBrush? _tipBack;
    private IBrush? _tipInk;
    private IPen? _tipEdge;

    /// <summary>
    /// Gesetzte Beschriftung einer Form: Text und Schein dahinter, gueltig fuer eine Breite.
    /// Der Text wird nur neu gesetzt, wenn sich die Breite aendert — also waehrend eine Kachel
    /// wandert, nicht bei jeder Mausbewegung.
    /// </summary>
    private sealed record Label(FormattedText Text, FormattedText Halo, double MaxWidth);

    private Label?[] _labels = [];

    /// <summary>Belegte Textflaechen des laufenden Bildes; eine Liste fuer alle Bilder statt einer je Bild.</summary>
    private readonly List<Rect> _placedLabels = [];

    /// <summary>Das Schild am Zeiger, gesetzt fuer genau einen Knoten mit genau einer Groesse.</summary>
    private int _tipNode = -1;
    private long _tipSize = -1;
    private FormattedText? _tipName;
    private FormattedText? _tipSizeText;

    /// <summary>Wieviele Formen gerade angeordnet sind. Fuer Tests: zeigt an, dass ein Scan angekommen ist.</summary>
    internal int NodeCount => _items.Count;

    /// <summary>Fuer Tests: ob die Bilduhr laeuft.</summary>
    internal bool FrameClockRunning => _frames.IsEnabled;

    /// <summary>Fuer Tests: ob noch Formen in Bewegung sind.</summary>
    internal bool AnimationRunning => _animator.IsRunning;

    /// <summary>Knoten unter dem Zeiger, oder -1. Fuer das Kontextmenue.</summary>
    public int HoveredNode => _hoverItem >= 0 && _hoverItem < _items.Count
        ? _items[_hoverItem].NodeIndex
        : -1;

    public event Action<int>? NodeHovered;
    public event Action<int>? NodeSelected;
    public event Action<int>? NodeOpened;

    public ChartView()
    {
        ClipToBounds = true;
        Focusable = true;

        // Ohne sichtbaren Fokus weiss beim Tabben niemand, dass die Pfeiltasten jetzt die
        // Karte bewegen — die Tastaturbedienung waere vorhanden, aber unauffindbar.
        GotFocus += (_, _) => InvalidateVisual();
        LostFocus += (_, _) => InvalidateVisual();

        // Eigene Bilduhr statt einer Avalonia-Animation: Hier bewegen sich tausende Formen
        // gleichzeitig, die kein eigenes Element haben. Sie laeuft nur, solange etwas lebt.
        _frames = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render,
            (_, _) => { if (_animator.IsRunning) InvalidateVisual(); else _frames!.Stop(); });

        // Dieser Konstruktor startet die Uhr sofort; gestartet wird sie aber erst, wenn etwas
        // sich bewegt (EnsureLayout).
        _frames.Stop();
    }

    /// <summary>
    /// Ausserhalb des Fensters darf die Bilduhr nicht weiterlaufen: Sie hielte die Karte am
    /// Leben und zeichnete sechzigmal je Sekunde in ein Bild, das niemand sieht.
    /// </summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _frames.Stop();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_animator.IsRunning) _frames.Start();
    }

    public ViewKind CurrentView => _kind;
    public ColorMode ColorMode => _colorMode;

    public void SetView(ViewKind kind)
    {
        if (_kind == kind) return;
        _kind = kind;

        ILayoutEngine? found = Array.Find(Engines, e => e.Kind == kind);
        if (found is null || ReferenceEquals(found, _engine)) return;

        _engine = found;
        _animator.Reset();
        _animateNextLayout = true;
        Invalidate();
    }

    /// <summary>
    /// Hebt alle Dateien einer Endung hervor, indem alles andere zuruecktritt. Abdunkeln der
    /// Umgebung zeigt die Verteilung besser als ein Rahmen um jeden Treffer: Man sieht auf
    /// einen Blick, wo sich diese Endung sammelt.
    /// </summary>
    public void SetHighlight(string? extension)
    {
        if (_highlightExtension == extension) return;
        _highlightExtension = extension;
        InvalidateVisual();
    }

    public void SetColorMode(ColorMode mode)
    {
        if (_colorMode == mode) return;
        _colorMode = mode;
        InvalidateVisual();
    }

    public void SetStore(NodeStore? store, int rootIndex)
    {
        // Wechselt der Ausschnitt, ist der alte Verlauf bedeutungslos — beim Hineingehen
        // waeren die alten Rechtecke voellig andere Flaechen.
        if (rootIndex != _rootIndex) _animator.Reset();

        _store = store;
        _rootIndex = rootIndex;
        _hoverItem = -1;
        _tipNode = -1;      // gleicher Index, anderer Baum: das Schild darf nicht den alten Namen zeigen
        _animateNextLayout = true;
        Invalidate();
    }

    public void SetSelected(int nodeIndex)
    {
        if (_selectedNode == nodeIndex) return;
        _selectedNode = nodeIndex;
        InvalidateVisual();
    }

    public void Invalidate()
    {
        _laidOutFor = default;
        InvalidateVisual();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Invalidate();
    }

    private void EnsureTheme()
    {
        bool dark = ActualThemeVariant == ThemeVariant.Dark;
        if (_theme is not null && dark == _darkTheme) return;

        _darkTheme = dark;

        // Pinsel und Stifte aus dem Zwischenspeicher: je Farbe einer, ueber Themenwechsel hinweg.
        _hoverPen = RenderCache.Pen(RenderCache.Brush(dark
            ? Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0xCC, 0x2A, 0x27, 0x24)), 1.5);
        _selectionPen = RenderCache.Pen(RenderCache.Brush(dark
            ? Color.FromRgb(0xF0, 0xEC, 0xE4)
            : Color.FromRgb(0x2A, 0x27, 0x24)), 2);

        _labelBrush = RenderCache.Brush(dark
            ? Color.FromRgb(0xEC, 0xE7, 0xDE)
            : Color.FromRgb(0x3A, 0x35, 0x2E));

        // Auf Pastellflaechen genuegt ein heller Schein hinter der Schrift; ein harter
        // Schlagschatten wie auf gesaettigten Farben waere hier zu laut.
        _labelHalo = RenderCache.Brush(dark
            ? Color.FromArgb(0x70, 0, 0, 0)
            : Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF));

        _rowHighlight = RenderCache.Brush(dark
            ? Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x30, 0x2A, 0x27, 0x24));

        _tipBack = RenderCache.Brush(dark
            ? Color.FromRgb(0x2A, 0x26, 0x22) : Color.FromRgb(0xFC, 0xFA, 0xF5));
        _tipInk = RenderCache.Brush(dark
            ? Color.FromRgb(0xEC, 0xE7, 0xDE) : Color.FromRgb(0x2A, 0x27, 0x24));
        _tipEdge = RenderCache.Pen(RenderCache.Brush(dark
            ? Color.FromRgb(0x3D, 0x39, 0x33) : Color.FromRgb(0xE0, 0xD9, 0xCB)), 1);

        IBrush inkMuted = RenderCache.Brush(dark ? Color.FromRgb(0x9B, 0x93, 0x87) : Color.FromRgb(0x7A, 0x73, 0x6A));
        _focusPen = new Pen(inkMuted, 2, FocusDash);

        _theme = new ChartTheme
        {
            Dark = dark,
            Ink = RenderCache.Brush(dark ? Color.FromRgb(0xEC, 0xE7, 0xDE) : Color.FromRgb(0x2A, 0x27, 0x24)),
            InkMuted = inkMuted,
            InkFaint = RenderCache.Brush(dark ? Color.FromRgb(0x6B, 0x64, 0x5A) : Color.FromRgb(0xA9, 0xA1, 0x96)),
            Track = RenderCache.Brush(dark ? Color.FromRgb(0x33, 0x30, 0x2B) : Color.FromRgb(0xEA, 0xE4, 0xD8)),
            Hairline = RenderCache.Pen(RenderCache.Brush(dark
                ? Color.FromRgb(0x33, 0x30, 0x2B) : Color.FromRgb(0xE8, 0xE2, 0xD6)), 1),
            Fill = category => RenderCache.Brush(Palette.ForCategory(category, dark)),
            Coloring = _coloring,
        };

        // Beschriftungen und Schild tragen die alten Pinsel — neu setzen.
        _tipNode = -1;
        _laidOutFor = default;
    }

    private void EnsureLayout()
    {
        var area = new Rect(Bounds.Size);
        if (_store is null || area.Width < 4 || area.Height < 4)
        {
            _items = [];
            _geometries = [];
            _labels = [];
            return;
        }

        if (_laidOutFor == area && _items.Count > 0) return;

        _items = _engine.Layout(_store, _rootIndex, area, LayoutBudget.Current);
        _laidOutFor = area;
        _hoverItem = -1;

        // Neue Formen, neue Beschriftungen: Der Text je Form wird beim ersten Zeichnen gesetzt
        // und danach wiederverwendet, bis das Layout wechselt.
        _labels = new Label?[_items.Count];

        // Beim blossen Groessenaendern des Fensters wird nicht animiert: Dort soll die Karte
        // dem Rahmen folgen, nicht neu einlaufen.
        _animator.Prepare(_store, _items, _animateNextLayout);
        _animateNextLayout = false;
        if (_animator.IsRunning && !_frames.IsEnabled) _frames.Start();

        // Ringsegmente einmal je Layout zu Geometrie machen, nicht je Bild — sonst waere
        // jede Mausbewegung ein Neuaufbau von tausenden Pfaden.
        _geometries = new Geometry?[_items.Count];
        for (int i = 0; i < _items.Count; i++)
            if (_items[i].Kind == ShapeKind.Arc)
                _geometries[i] = BuildArc(_items[i]);
    }

    /// <summary>
    /// Zeigt, dass die Karte am Zug ist.
    ///
    /// Ein Rahmen innen statt aussen: Die Karte fuellt ihren Bereich ganz aus, ein Rahmen auf
    /// der Kante waere zur Haelfte abgeschnitten.
    /// </summary>
    private void DrawFocusRing(DrawingContext context)
    {
        if (!IsFocused || _focusPen is null) return;

        context.DrawRectangle(null, _focusPen, new Rect(Bounds.Size).Deflate(1.5), 6, 6);
    }

    public override void Render(DrawingContext context)
    {
        EnsureTheme();
        _coloring.Configure(_store, _colorMode, _darkTheme);
        EnsureLayout();

        DrawFocusRing(context);

        if (_store is null || _items.Count == 0) return;
        NodeStore store = _store;
        var area = new Rect(Bounds.Size);

        _engine.DrawUnderlay(context, store, _items, area, _theme!);

        if (_engine.PaintsShapes)
        {
            // In Erzeugungsreihenfolge: Ordner zuerst, ihre Kinder darueber. Ein Ordner
            // bekommt eine zarte Toenung seines eigenen Farbtons, sodass die Gruppe als
            // Feld lesbar bleibt und die Fugen zwischen den Kindern hell durchscheinen.
            for (int i = 0; i < _items.Count; i++)
            {
                ShapeAnimator.Frame frame = _animator.At(i, _items[i]);
                if (frame.Opacity <= 0.01) continue;

                if (_highlightExtension is not null && !Highlighted(store, _items[i].NodeIndex))
                    frame = frame with { Opacity = frame.Opacity * 0.18 };

                DrawAnimated(context, store, _items[i], _geometries[i], frame);
            }
        }

        // Hervorhebung vor dem Overlay: Ranglisten zeichnen ihren Text selbst, und eine
        // gefuellte Flaeche darueber wuerde ihn zudecken. Text gehoert immer nach oben.
        DrawHighlights(context, store);

        _engine.DrawOverlay(context, store, _items, area, _theme!);

        if (_engine.WantsNodeLabels) DrawLabels(context, store);

        DrawTooltip(context, store);
        _animator.Settle(_items);
    }

    /// <summary>
    /// Zeichnet Auswahl und Hover. Bei Ansichten, die ihre Inhalte selbst malen, wird nur
    /// zart hinterlegt statt voll gefuellt — sonst verschwaende die Zeile unter ihrer
    /// eigenen Hervorhebung.
    /// </summary>
    private void DrawHighlights(DrawingContext context, NodeStore store)
    {
        if (_selectedNode >= 0)
        {
            int index = IndexOfNode(_selectedNode);
            if (index >= 0)
                DrawShape(context, store, _items[index], _geometries[index], false, _selectionPen);
        }

        if (_hoverItem < 0 || _hoverItem >= _items.Count) return;

        VisualItem hovered = _items[_hoverItem];

        if (!_engine.PaintsShapes)
        {
            // Rangliste: nur eine ruhige Unterlegung, der Text kommt gleich darueber.
            context.DrawRectangle(_rowHighlight, null, hovered.Bounds, 7, 7);
            return;
        }

        // Karte: die Flaeche hebt sich leicht heraus. Das Anwachsen ist auf einen Blick zu
        // sehen, eine duenne Linie nicht.
        if (hovered.Kind == ShapeKind.Rect && hovered.Bounds.Width > 8 && hovered.Bounds.Height > 8)
            hovered = hovered with { Bounds = hovered.Bounds.Inflate(2) };

        DrawShape(context, store, hovered, _geometries[_hoverItem], filled: true);
        DrawShape(context, store, hovered, _geometries[_hoverItem], false, _hoverPen);
    }

    /// <summary>
    /// Zeichnet eine Form in ihrem aktuellen Bewegungszustand. Rechtecke wandern ueber ihre
    /// Kanten, alles andere ueber eine Skalierung um den eigenen Mittelpunkt — Ringsegmente
    /// und Kreise liessen sich sonst nur durch Neuaufbau ihrer Geometrie bewegen, und das je Bild.
    /// </summary>
    private bool Highlighted(NodeStore store, int node)
        => !store.IsDirectory(node)
           && ExtensionStats.Matches(store.GetName(node), _highlightExtension!);

    private void DrawAnimated(
        DrawingContext context, NodeStore store, VisualItem item,
        Geometry? geometry, ShapeAnimator.Frame frame)
    {
        VisualItem shaped = item.Kind == ShapeKind.Rect && frame.Bounds != item.Bounds
            ? item with { Bounds = frame.Bounds }
            : item;

        if (frame.IsIdle)
        {
            DrawShape(context, store, shaped, geometry, filled: true);
            return;
        }

        Point center = item.Kind == ShapeKind.Rect ? shaped.Bounds.Center : item.Center;

        using (context.PushOpacity(frame.Opacity))
        using (context.PushTransform(
            Matrix.CreateTranslation(-center.X, -center.Y)
            * Matrix.CreateScale(frame.Scale, frame.Scale)
            * Matrix.CreateTranslation(center.X, center.Y)))
        {
            DrawShape(context, store, shaped, geometry, filled: true);
        }
    }

    private void DrawShape(
        DrawingContext context, NodeStore store, VisualItem item, Geometry? geometry,
        bool filled, IPen? pen = null)
    {
        IBrush? brush = null;
        if (filled)
        {
            brush = item.IsDirectory
                ? _theme!.WashFor(store, item.NodeIndex, item.Depth)
                : _theme!.FillFor(store, item.NodeIndex, item.Depth);
        }

        switch (item.Kind)
        {
            case ShapeKind.Rect:
            {
                Rect r = item.Bounds;
                if (r.Width > 2 * TileGap && r.Height > 2 * TileGap)
                    r = r.Deflate(TileGap);

                double radius = Math.Min(MaxCornerRadius, Math.Min(r.Width, r.Height) / 4);
                context.DrawRectangle(brush, pen, r, radius, radius);
                break;
            }

            case ShapeKind.Circle:
                context.DrawEllipse(brush, pen, item.Center, item.OuterRadius, item.OuterRadius);
                break;

            case ShapeKind.Arc:
                if (geometry is not null) context.DrawGeometry(brush, pen, geometry);
                break;
        }
    }

    private static Geometry BuildArc(VisualItem item)
    {
        var geometry = new StreamGeometry();
        using StreamGeometryContext ctx = geometry.Open();

        double a0 = item.StartAngle;
        double a1 = item.StartAngle + item.SweepAngle;
        bool large = item.SweepAngle > Math.PI;

        Point outerStart = Polar(item.Center, item.OuterRadius, a0);
        Point outerEnd = Polar(item.Center, item.OuterRadius, a1);
        Point innerEnd = Polar(item.Center, item.InnerRadius, a1);
        Point innerStart = Polar(item.Center, item.InnerRadius, a0);

        ctx.BeginFigure(outerStart, isFilled: true);
        ctx.ArcTo(outerEnd, new Size(item.OuterRadius, item.OuterRadius), 0, large, SweepDirection.Clockwise);
        ctx.LineTo(innerEnd);
        ctx.ArcTo(innerStart, new Size(item.InnerRadius, item.InnerRadius), 0, large, SweepDirection.CounterClockwise);
        ctx.EndFigure(isClosed: true);

        return geometry;
    }

    private static Point Polar(Point center, double radius, double angle)
        => new(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));

    private int IndexOfNode(int node)
    {
        for (int i = 0; i < _items.Count; i++)
            if (_items[i].NodeIndex == node) return i;
        return -1;
    }

    /// <summary>
    /// Beschriftet Ordner in ihrer Kopfzeile und Dateien auf ihrer Flaeche. Von innen nach
    /// aussen, weil die tiefste Form an einer Stelle die konkreteste ist; was mit einer bereits
    /// gesetzten Beschriftung kollidiert, entfaellt.
    ///
    /// Gekuerzt statt weggelassen: Dateinamen tragen das Kennzeichnende vorn.
    /// </summary>
    private void DrawLabels(DrawingContext context, NodeStore store)
    {
        List<Rect> placed = _placedLabels;
        placed.Clear();

        for (int i = _items.Count - 1; i >= 0; i--)
        {
            VisualItem item = _items[i];

            // Namen gehoeren zu ihrer Flaeche: Sie laufen mit ihr ein und wandern mit ihr,
            // statt am Ende an fertiger Stelle aufzublitzen.
            ShapeAnimator.Frame frame = _animator.At(i, item);
            if (frame.Opacity < 0.2) continue;

            if (item.Kind == ShapeKind.Arc)
            {
                DrawArcLabel(context, store, i, item, frame, placed);
                continue;
            }

            if (item.Kind == ShapeKind.Rect && frame.Bounds != item.Bounds)
                item = item with { Bounds = frame.Bounds };

            Rect box = item.Kind == ShapeKind.Circle
                ? new Rect(item.Center.X - item.OuterRadius * 0.86, item.Center.Y - 8,
                           item.OuterRadius * 1.72, 16)
                : item.Bounds;

            double minWidth = item.Kind == ShapeKind.Circle ? 40 : 52;
            if (box.Width < minWidth || box.Height < 14) continue;

            Label label = LabelFor(store, i, item, item.IsDirectory ? 11 : 10.5, box.Width - 8);
            FormattedText text = label.Text;
            if (text.Height > box.Height - 1) continue;

            var origin = item.Kind == ShapeKind.Circle
                ? new Point(box.X + (box.Width - text.Width) / 2, item.Center.Y - text.Height / 2)
                : new Point(box.X + 5, box.Y + 2);

            var occupied = new Rect(origin.X, origin.Y, text.Width, text.Height);

            bool blocked = false;
            foreach (Rect other in placed)
            {
                if (!other.Intersects(occupied)) continue;
                blocked = true;
                break;
            }
            if (blocked) continue;

            placed.Add(occupied);

            using (context.PushOpacity(frame.Opacity))
            {
                context.DrawText(label.Halo, origin + new Vector(0.7, 0.7));
                context.DrawText(text, origin);
            }
        }
    }

    /// <summary>
    /// Die Beschriftung einer Form, aus dem Speicher oder frisch gesetzt. Neu gesetzt wird nur,
    /// wenn die verfuegbare Breite sich geaendert hat — bei stehender Karte also nie.
    /// </summary>
    private Label LabelFor(NodeStore store, int index, VisualItem item, double size, double maxWidth)
    {
        Label? label = _labels[index];
        if (label is not null && Math.Abs(label.MaxWidth - maxWidth) < 0.01) return label;

        string name = store.GetName(item.NodeIndex);
        Typeface face = item.IsDirectory ? RenderCache.UiBold : RenderCache.Ui;

        FormattedText Compose(IBrush brush) => new(
            name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, brush)
        {
            MaxTextWidth = maxWidth,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };

        label = new Label(Compose(_labelBrush!), Compose(_labelHalo!), maxWidth);
        _labels[index] = label;
        return label;
    }

    /// <summary>
    /// Beschriftet ein Ringsegment. Der Text laeuft entlang des Bogens statt waagerecht —
    /// waagerecht passte in einen Ring nur bei den allergroessten Segmenten etwas hinein.
    ///
    /// Die Drehung wird auf plus/minus 90 Grad normalisiert, damit kein Name auf dem Kopf steht.
    /// </summary>
    private void DrawArcLabel(
        DrawingContext context, NodeStore store, int index, VisualItem item,
        ShapeAnimator.Frame frame, List<Rect> placed)
    {
        double middle = item.StartAngle + item.SweepAngle / 2;
        double radius = (item.InnerRadius + item.OuterRadius) / 2;
        double along = item.SweepAngle * radius;
        double thickness = item.OuterRadius - item.InnerRadius;

        if (along < 46 || thickness < 13) return;

        Label label = LabelFor(store, index, item, 10.5, along - 10);
        FormattedText text = label.Text;
        if (text.Height > thickness - 3) return;

        Point at = Polar(item.Center, radius, middle);

        // Grobe Sperre: zwei Namen an fast derselben Stelle waeren unlesbar uebereinander.
        var occupied = new Rect(at.X - 9, at.Y - 9, 18, 18);
        foreach (Rect other in placed)
            if (other.Intersects(occupied)) return;
        placed.Add(occupied);

        // Auf lesbaren Bereich normalisieren: Alles ausserhalb von plus/minus 90 Grad stuende
        // auf dem Kopf. Die Pruefung auf die linke Haelfte allein genuegt nicht — am unteren
        // Rand liegt der Text sonst verkehrt herum.
        double rotation = Math.IEEERemainder(middle + Math.PI / 2, Math.Tau);
        if (rotation > Math.PI / 2 || rotation < -Math.PI / 2) rotation += Math.PI;

        using (context.PushOpacity(frame.Opacity))
        using (context.PushTransform(
            Matrix.CreateTranslation(-text.Width / 2, -text.Height / 2)
            * Matrix.CreateRotation(rotation)
            * Matrix.CreateTranslation(at.X, at.Y)))
        {
            context.DrawText(label.Halo, new Point(0.7, 0.7));
            context.DrawText(text, default);
        }
    }

    /// <summary>
    /// Schild am Zeiger mit Name und Groesse.
    ///
    /// Ohne das muss der Blick fuer jede kleine Kachel quer durch das Fenster zum Inspector
    /// wandern und wieder zurueck — bei einer Karte, die man ueberstreicht, ist das der
    /// haeufigste Weg ueberhaupt.
    /// </summary>
    private void DrawTooltip(DrawingContext context, NodeStore store)
    {
        if (_hoverItem < 0 || _hoverItem >= _items.Count) return;

        int node = _items[_hoverItem].NodeIndex;
        long bytes = store.Size[node];

        // Neu gesetzt nur, wenn ein anderer Knoten unter dem Zeiger liegt oder seine Groesse
        // sich geaendert hat — waehrend eines Scans wachsen Ordner noch.
        if (_tipName is null || _tipSizeText is null || _tipNode != node || _tipSize != bytes)
        {
            _tipNode = node;
            _tipSize = bytes;

            _tipName = new FormattedText(
                store.GetName(node), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                RenderCache.UiBold, 12, _tipInk!)
            { MaxTextWidth = 420, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };

            _tipSizeText = new FormattedText(
                Sizes.Format(bytes), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                RenderCache.Ui, 11.5, _tipInk!);
        }

        FormattedText name = _tipName;
        FormattedText size = _tipSizeText;

        const double padX = 9, padY = 6, gap = 12;
        double width = name.Width + gap + size.Width + padX * 2;
        double height = Math.Max(name.Height, size.Height) + padY * 2;

        // Am Rand kippt das Schild auf die andere Seite, statt abgeschnitten zu werden.
        double x = _pointer.X + 16;
        double y = _pointer.Y + 18;
        if (x + width > Bounds.Width - 4) x = _pointer.X - width - 10;
        if (y + height > Bounds.Height - 4) y = _pointer.Y - height - 10;

        var box = new Rect(Math.Max(2, x), Math.Max(2, y), width, height);
        context.DrawRectangle(_tipBack, _tipEdge, box, 7, 7);

        context.DrawText(name, new Point(box.X + padX, box.Y + padY));
        context.DrawText(size, new Point(box.Right - padX - size.Width, box.Y + padY));
    }

    /// <summary>Rueckwaerts suchen: die zuletzt erzeugte Form liegt oben und ist die konkreteste.</summary>
    private int HitTest(Point point)
    {
        for (int i = _items.Count - 1; i >= 0; i--)
            if (_items[i].Contains(point)) return i;
        return -1;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_items.Count == 0) return;

        Point at = e.GetPosition(this);
        int hit = HitTest(at);

        // Bei gleicher Form nichts neu zeichnen: Jede Mausbewegung wuerde sonst die ganze
        // Karte mit tausenden Formen neu malen. Das Schild bleibt dort stehen, wo der Zeiger
        // die Form betreten hat — es haengt an der Form, nicht am Zeiger.
        if (hit == _hoverItem) return;

        _pointer = at;
        _hoverItem = hit;
        InvalidateVisual();
        NodeHovered?.Invoke(hit >= 0 ? _items[hit].NodeIndex : -1);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hoverItem < 0) return;

        _hoverItem = -1;
        InvalidateVisual();
        NodeHovered?.Invoke(-1);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        // Wer auf die Karte klickt, will danach mit den Pfeiltasten weiter — ohne Fokus
        // gingen die Tasten an das Element, das ihn zufaellig gerade hat.
        Focus();

        if (_items.Count == 0) return;

        Point at = e.GetPosition(this);
        int hit = HitTest(at);

        if (hit < 0)
        {
            // Klick ins Leere hebt die Auswahl auf — sonst bleibt man daran haengen.
            NodeSelected?.Invoke(-1);
            return;
        }

        int node = _items[hit].NodeIndex;
        if (e.ClickCount >= 2 && _store is not null && _store.IsDirectory(node))
            NodeOpened?.Invoke(node);
        else
            NodeSelected?.Invoke(node);
    }
}
