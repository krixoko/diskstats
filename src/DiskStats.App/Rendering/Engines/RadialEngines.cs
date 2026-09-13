using Avalonia;
using Avalonia.Media;
using DiskStats.Core.Storage;

using DiskStats.App.Localization;

namespace DiskStats.App.Rendering.Engines;

/// <summary>
/// Sunburst: Ringe vom Mittelpunkt nach aussen. Der Winkel traegt die Groesse, der Radius die
/// Tiefe. Beantwortet eine andere Frage als die Treemap — nicht "was ist gross", sondern
/// "wie verzweigt sich der Platz".
/// </summary>
public sealed class SunburstEngine : ILayoutEngine
{
    public ViewKind Kind => ViewKind.Sunburst;
    public string Title => Loc.T("View_Sunburst");
    public string Hint => Loc.T("View_SunburstHint");

    private const int MaxRings = 7;
    private const double MinSweep = 0.012;    // schmaler als das ist unklickbar

    public IReadOnlyList<VisualItem> Layout(NodeStore store, int rootIndex, Rect bounds, LayoutBudget budget)
    {
        var items = new List<VisualItem>(2048);

        var center = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        double maxRadius = Math.Min(bounds.Width, bounds.Height) / 2 - 6;
        if (maxRadius < 30) return items;

        double ring = maxRadius / (MaxRings + 1);

        // Der Mittelkreis ist der aktuelle Wurzelknoten selbst.
        items.Add(VisualItem.OfCircle(rootIndex, 0, true, center, ring));

        Emit(store, rootIndex, center, ring, ring, -Math.PI / 2, Math.Tau, 1, budget, items);
        return items;
    }

    private static void Emit(
        NodeStore store, int node, Point center, double ringWidth, double innerRadius,
        double startAngle, double sweep, int depth, LayoutBudget budget, List<VisualItem> items)
    {
        if (depth > MaxRings || sweep < MinSweep || !budget.Allows(items.Count)) return;

        int[] order = LayoutHelp.SortedChildren(store, node);
        if (order.Length == 0) return;

        long total = 0;
        foreach (int child in order) total += store.Size[child];
        if (total <= 0) return;

        double angle = startAngle;
        double outerRadius = innerRadius + ringWidth;

        // Flaeche eines Segments je Bogenmass: der Rest des Rings, den ein Kind anteilig bekommt.
        double areaPerRadian = 0.5 * (outerRadius * outerRadius - innerRadius * innerRadius);

        foreach (int child in order)
        {
            if (!budget.Allows(items.Count)) return;

            double childSweep = sweep * (store.Size[child] / (double)total);
            if (childSweep < MinSweep || !budget.Fits(childSweep * areaPerRadian)) break;

            items.Add(VisualItem.OfArc(
                child, depth, store.IsDirectory(child),
                center, innerRadius, outerRadius, angle, childSweep));

            if (store.IsDirectory(child))
                Emit(store, child, center, ringWidth, outerRadius, angle, childSweep, depth + 1, budget, items);

            angle += childSweep;
        }
    }
}

/// <summary>
/// Bubbles: verschachtelte Kreise, einer je Eintrag. Die Flaeche traegt die Groesse.
///
/// Kreise lassen sich nicht luecklos packen, deshalb ist diese Ansicht weniger flaechentreu
/// als eine Treemap — dafuer trennt sie Geschwister sichtbar voneinander, was das Zaehlen
/// erleichtert. Die Platzierung sucht spiralfoermig die erste ueberschneidungsfreie Stelle;
/// das ist nicht optimal, aber schnell und stabil.
/// </summary>
public sealed class BubblesEngine : ILayoutEngine
{
    public ViewKind Kind => ViewKind.Bubbles;
    public string Title => Loc.T("View_Bubbles");
    public string Hint => Loc.T("View_BubblesHint");

    private const double PackingEfficiency = 0.62;
    private const double MinRadiusToDraw = 3.5;
    private const double MinRadiusForChildren = 26;
    private const int MaxDepth = 4;

    public IReadOnlyList<VisualItem> Layout(NodeStore store, int rootIndex, Rect bounds, LayoutBudget budget)
    {
        var items = new List<VisualItem>(2048);

        var center = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        double radius = Math.Min(bounds.Width, bounds.Height) / 2 - 4;
        if (radius < 20) return items;

        Pack(store, rootIndex, center, radius, 0, budget, items);
        return items;
    }

    private static void Pack(
        NodeStore store, int node, Point center, double radius, int depth,
        LayoutBudget budget, List<VisualItem> items)
    {
        if (depth >= MaxDepth || radius < MinRadiusForChildren || !budget.Allows(items.Count)) return;

        int[] order = LayoutHelp.SortedChildren(store, node);
        if (order.Length == 0) return;

        long total = 0;
        foreach (int child in order) total += store.Size[child];
        if (total <= 0) return;

        // Radius aus der Flaeche: die Summe der Kindflaechen fuellt den Elternkreis nur
        // teilweise, weil Kreise Zwischenraum lassen.
        double available = Math.PI * radius * radius * PackingEfficiency;
        var placed = new List<(Point Center, double Radius)>();

        foreach (int child in order)
        {
            if (!budget.Allows(items.Count)) return;

            double childRadius = Math.Sqrt(available * (store.Size[child] / (double)total) / Math.PI);
            if (childRadius < MinRadiusToDraw || !budget.Fits(Math.PI * childRadius * childRadius)) break;

            if (!TryPlace(center, radius, childRadius, placed, out Point position)) continue;

            placed.Add((position, childRadius));
            items.Add(VisualItem.OfCircle(child, depth + 1, store.IsDirectory(child), position, childRadius));

            if (store.IsDirectory(child))
                Pack(store, child, position, childRadius, depth + 1, budget, items);
        }
    }

    /// <summary>Sucht spiralfoermig die erste Stelle, an der der Kreis passt.</summary>
    private static bool TryPlace(
        Point parentCenter, double parentRadius, double radius,
        List<(Point Center, double Radius)> placed, out Point result)
    {
        double limit = parentRadius - radius - 1;
        if (limit <= 0) { result = default; return false; }

        double step = Math.Max(1.5, radius * 0.35);

        for (double r = 0; r <= limit; r += step)
        {
            int samples = r < 0.01 ? 1 : Math.Max(8, (int)(Math.Tau * r / step));

            for (int i = 0; i < samples; i++)
            {
                double angle = Math.Tau * i / samples;
                var candidate = new Point(
                    parentCenter.X + r * Math.Cos(angle),
                    parentCenter.Y + r * Math.Sin(angle));

                bool clear = true;
                foreach ((Point other, double otherRadius) in placed)
                {
                    double dx = candidate.X - other.X, dy = candidate.Y - other.Y;
                    double needed = radius + otherRadius + 1;
                    if (dx * dx + dy * dy >= needed * needed) continue;
                    clear = false;
                    break;
                }

                if (!clear) continue;
                result = candidate;
                return true;
            }
        }

        result = default;
        return false;
    }
}

/// <summary>
/// Mind Map: Aeste vom Mittelpunkt, gewichtet nach Groesse. Jede Ebene sitzt auf einem
/// weiteren Ring, und jedes Kind bekommt den Winkelsektor, der seinem Anteil entspricht.
/// Zeigt Struktur statt Flaeche — nuetzlich, wenn man wissen will, wo die Verzweigungen sind.
/// </summary>
public sealed class MindMapEngine : ILayoutEngine
{
    public ViewKind Kind => ViewKind.MindMap;
    public string Title => Loc.T("View_MindMap");
    public string Hint => Loc.T("View_MindMapHint");

    /// <summary>Die Namen zeichnet diese Ansicht selbst — sie muessen neben die Knoten.</summary>
    public bool WantsNodeLabels => false;

    /// <summary>
    /// Zehn, weil eine Uebersicht sich merken lassen soll. Bis hierher bleiben die Namen
    /// lesbar und die Abstaende gleichmaessig; darueber wird daraus wieder eine Punktwolke.
    /// </summary>
    private const int Branches = 10;

    /// <summary>Platz, den eine Beschriftung neben dem Ring braucht.</summary>
    private const double LabelRoom = 108;

    private int _rest;
    private long _restBytes;

    public IReadOnlyList<VisualItem> Layout(NodeStore store, int rootIndex, Rect bounds, LayoutBudget budget)
    {
        var items = new List<VisualItem>(Branches + 1);
        _rest = 0;
        _restBytes = 0;

        var center = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);

        double ring = Math.Min(bounds.Width / 2 - LabelRoom, bounds.Height / 2 - 38);
        if (ring < 70) return items;

        // Zehn Aeste plus Mitte; ein knapperes Budget kuerzt die Aeste, nie die Mitte.
        int[] folders = Largest(store, rootIndex, Math.Clamp(budget.MaxItems - 1, 0, Branches));
        if (folders.Length == 0) return items;

        items.Add(VisualItem.OfCircle(rootIndex, 0, true, center, 30));

        long biggest = store.Size[folders[0]];

        for (int i = 0; i < folders.Length; i++)
        {
            // Gleiche Winkelabstaende statt Anteile am Ganzen: Die Groesse steckt im Radius des
            // Knotens und in der Zahl daneben. Winkel nach Anteil zu vergeben hiesse, gerade
            // den kleineren Ordnern die Beschriftung zu nehmen — und die ist hier der Zweck.
            //
            // Der halbe Schritt Versatz laesst genau senkrecht oben und unten frei: Dort steht
            // der Name der Mitte und die Zeile mit dem Rest, und ein Ast liefe mitten hindurch.
            double angle = -Math.PI / 2 + (i + 0.5) * Math.Tau / folders.Length;

            var position = new Point(
                center.X + ring * Math.Cos(angle),
                center.Y + ring * Math.Sin(angle));

            // Flaeche proportional zur Groesse, also Radius ueber die Wurzel.
            double share = biggest > 0 ? store.Size[folders[i]] / (double)biggest : 0;
            items.Add(VisualItem.OfCircle(
                folders[i], 1, true, position, 9 + 15 * Math.Sqrt(share)));
        }

        return items;
    }

    /// <summary>
    /// Die groessten Unterordner, absteigend. Dateien bleiben aussen vor: Nach denen fragt die
    /// Rangliste, und in einer Uebersicht der Ordner waeren sie ein anderer Gegenstand.
    /// Was nicht mehr hineinpasst, wird gezaehlt und unten genannt statt verschwiegen.
    /// </summary>
    private int[] Largest(NodeStore store, int root, int limit)
    {
        var folders = new List<int>();
        int start = store.ChildStart[root];

        for (int i = start; i < start + store.ChildCount[root]; i++)
            if (store.IsDirectory(i) && store.Size[i] > 0) folders.Add(i);

        folders.Sort((a, b) => store.Size[b].CompareTo(store.Size[a]));

        if (folders.Count > limit)
        {
            for (int i = limit; i < folders.Count; i++) _restBytes += store.Size[folders[i]];

            _rest = folders.Count - limit;
            folders.RemoveRange(limit, _rest);
        }

        return [.. folders];
    }

    public void DrawUnderlay(
        DrawingContext context, NodeStore store,
        IReadOnlyList<VisualItem> items, Rect bounds, ChartTheme theme)
    {
        if (items.Count < 2) return;

        Point center = items[0].Center;
        IPen pen = RenderCache.Pen(theme.Track, 1.6);

        for (int i = 1; i < items.Count; i++)
            context.DrawLine(pen, center, items[i].Center);
    }

    public void DrawOverlay(
        DrawingContext context, NodeStore store,
        IReadOnlyList<VisualItem> items, Rect bounds, ChartTheme theme)
    {
        if (items.Count == 0)
        {
            context.DrawText(
                LayoutHelp.Text(Loc.T("View_NoFolders"), theme.Ui, 13, theme.InkFaint),
                new Point(bounds.X + 6, bounds.Y + 6));
            return;
        }

        DrawCenter(context, store, items[0], theme);

        for (int i = 1; i < items.Count; i++)
            DrawBranch(context, store, items[i], items[0].Center, theme);

        if (_rest == 0) return;

        FormattedText more = LayoutHelp.Text(
            Loc.T("View_MoreFolders", _rest, Sizes.Format(_restBytes)),
            theme.Ui, 11.5, theme.InkFaint);

        context.DrawText(more, new Point(
            bounds.X + (bounds.Width - more.Width) / 2, bounds.Bottom - more.Height - 2));
    }

    /// <summary>Name und Gesamtgroesse in der Mitte — wovon die Aeste abgehen.</summary>
    private static void DrawCenter(
        DrawingContext context, NodeStore store, VisualItem root, ChartTheme theme)
    {
        FormattedText name = LayoutHelp.Text(
            store.DisplayName(root.NodeIndex), theme.UiBold, 13.5, theme.Ink, 210);
        FormattedText size = LayoutHelp.Text(
            Sizes.Format(store.Size[root.NodeIndex]), theme.Mono, 12, theme.InkMuted);

        context.DrawText(name, new Point(
            root.Center.X - name.Width / 2, root.Center.Y - root.OuterRadius - name.Height - 7));
        context.DrawText(size, new Point(
            root.Center.X - size.Width / 2, root.Center.Y + root.OuterRadius + 6));
    }

    /// <summary>
    /// Name und Groesse eines Astes, aussen neben seinem Knoten.
    ///
    /// Die Seite richtet sich danach, wo der Knoten steht: Rechts vom Mittelpunkt laeuft der
    /// Text nach aussen, links nach innen. So zeigt kein Name ueber den Rand hinaus, und keiner
    /// laeuft zurueck durch die Mitte.
    /// </summary>
    private static void DrawBranch(
        DrawingContext context, NodeStore store, VisualItem item, Point center, ChartTheme theme)
    {
        bool right = item.Center.X >= center.X;
        double edge = item.OuterRadius + 7;

        FormattedText name = LayoutHelp.Text(
            store.GetName(item.NodeIndex), theme.UiBold, 12, theme.Ink, LabelRoom - 10);
        FormattedText size = LayoutHelp.Text(
            Sizes.Format(store.Size[item.NodeIndex]), theme.Mono, 11, theme.InkMuted);

        double x = right ? item.Center.X + edge : item.Center.X - edge;
        double top = item.Center.Y - name.Height - 1;

        context.DrawText(name, new Point(right ? x : x - name.Width, top));
        context.DrawText(size, new Point(right ? x : x - size.Width, top + name.Height + 1));
    }
}
