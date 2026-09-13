using Avalonia;
using Avalonia.Media;
using DiskStats.App;
using DiskStats.Core.Storage;

using DiskStats.App.Localization;

namespace DiskStats.App.Rendering.Engines;

/// <summary>Gemeinsame Hilfen der Ansichten: sortierte Kinder, Groessenformat, Teilbaumlauf.</summary>
internal static class LayoutHelp
{
    /// <summary>Kinder eines Knotens, absteigend nach Groesse, ohne die mit Groesse 0.</summary>
    public static int[] SortedChildren(NodeStore store, int node)
    {
        int count = store.ChildCount[node];
        if (count == 0) return [];

        int start = store.ChildStart[node];
        var order = new int[count];
        for (int i = 0; i < count; i++) order[i] = start + i;
        Array.Sort(order, (a, b) => store.Size[b].CompareTo(store.Size[a]));

        int usable = count;
        while (usable > 0 && store.Size[order[usable - 1]] <= 0) usable--;
        return usable == count ? order : order[..usable];
    }

    /// <summary>Laeuft alle Dateien unterhalb eines Knotens ab — iterativ, wegen tiefer Baeume.</summary>
    public static void WalkFiles(NodeStore store, int root, Action<int> visit)
    {
        var stack = new Stack<int>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            int node = stack.Pop();
            int start = store.ChildStart[node];
            int count = store.ChildCount[node];

            for (int i = start; i < start + count; i++)
            {
                if (store.IsRemoved(i)) continue;
                if (store.IsDirectory(i)) stack.Push(i);
                else visit(i);
            }
        }
    }

    /// <summary>
    /// Gesetzter Text, aus dem Zwischenspeicher: Die Overlays zeichnen dieselben Zeilen Bild
    /// fuer Bild, und das Setzen ist der teure Teil daran.
    /// </summary>
    public static FormattedText Text(
        string value, Typeface face, double size, IBrush brush, double maxWidth = double.PositiveInfinity)
        => RenderCache.Text(value, face, size, brush, maxWidth);
}

/// <summary>
/// Squarified Treemap nach Bruls, Huizing und van Wijk. Bevorzugt Kacheln mit einem
/// Seitenverhaeltnis nahe 1 — lange duenne Streifen sind flaechentreu, aber weder lesbar
/// noch anklickbar.
/// </summary>
public sealed class TreemapEngine : ILayoutEngine
{
    public ViewKind Kind => ViewKind.Treemap;
    public string Title => Loc.T("View_Treemap");
    public string Hint => Loc.T("View_TreemapHint");

    /// <summary>
    /// Erst ab dieser Flaeche — als Vielfaches der Mindestflaeche des Budgets — bekommt eine
    /// Ordnerkachel ihre Kinder gezeichnet. Bei 46 px² Mindestflaeche sind das rund 44 x 44.
    /// </summary>
    private const double ChildrenAreaFactor = 44 * 44 / LayoutBudget.BaseMinArea;

    /// <summary>Rand, den ein Ordner seinen Kindern laesst.</summary>
    private const double Padding = 3;

    /// <summary>Streifen oben in einer Ordnerkachel, in dem sein Name steht.</summary>
    private const double HeaderHeight = 16;

    private const double MinWidthForHeader = 52;
    private const double MinHeightForHeader = 42;

    public IReadOnlyList<VisualItem> Layout(NodeStore store, int rootIndex, Rect bounds, LayoutBudget budget)
    {
        var items = new List<VisualItem>(Math.Min(4096, budget.MaxItems));
        if (bounds.Width > 2 && bounds.Height > 2)
            LayoutChildren(store, rootIndex, bounds, 0, budget, items);
        return items;
    }

    private static void LayoutChildren(
        NodeStore store, int node, Rect rect, int depth, LayoutBudget budget, List<VisualItem> items)
    {
        if (!budget.Allows(items.Count)) return;
        int[] order = LayoutHelp.SortedChildren(store, node);
        if (order.Length == 0) return;

        Squarify(store, order, 0, order.Length, rect, depth, budget, items);
    }

    private static void Squarify(
        NodeStore store, int[] order, int from, int to, Rect rect, int depth,
        LayoutBudget budget, List<VisualItem> items)
    {
        while (from < to && budget.Allows(items.Count))
        {
            if (rect.Width <= 1 || rect.Height <= 1) return;

            long remaining = 0;
            for (int k = from; k < to; k++) remaining += store.Size[order[k]];
            if (remaining <= 0) return;

            double scale = rect.Width * rect.Height / remaining;
            double side = Math.Min(rect.Width, rect.Height);

            int rowEnd = from + 1;
            double rowArea = store.Size[order[from]] * scale;
            double maxArea = rowArea, minArea = rowArea;
            double worst = Worst(rowArea, maxArea, minArea, side);

            while (rowEnd < to)
            {
                double area = store.Size[order[rowEnd]] * scale;
                double nextSum = rowArea + area;
                double nextWorst = Worst(nextSum, Math.Max(maxArea, area), Math.Min(minArea, area), side);
                if (nextWorst > worst) break;

                rowArea = nextSum;
                maxArea = Math.Max(maxArea, area);
                minArea = Math.Min(minArea, area);
                worst = nextWorst;
                rowEnd++;
            }

            double thickness = rowArea / side;
            bool stackVertically = rect.Width >= rect.Height;
            double offset = 0;

            for (int k = from; k < rowEnd; k++)
            {
                double length = side * (store.Size[order[k]] * scale / rowArea);
                Rect tile = stackVertically
                    ? new Rect(rect.X, rect.Y + offset, thickness, length)
                    : new Rect(rect.X + offset, rect.Y, length, thickness);
                offset += length;

                Emit(store, order[k], tile, depth, budget, items);
                if (!budget.Allows(items.Count)) return;
            }

            rect = stackVertically
                ? new Rect(rect.X + thickness, rect.Y, Math.Max(0, rect.Width - thickness), rect.Height)
                : new Rect(rect.X, rect.Y + thickness, rect.Width, Math.Max(0, rect.Height - thickness));

            from = rowEnd;
        }
    }

    private static void Emit(
        NodeStore store, int node, Rect tile, int depth, LayoutBudget budget, List<VisualItem> items)
    {
        double area = tile.Width * tile.Height;
        if (!budget.Fits(area)) return;

        bool isDirectory = store.IsDirectory(node);
        items.Add(VisualItem.OfRect(node, depth, isDirectory, tile));

        if (!isDirectory || store.ChildCount[node] == 0) return;
        if (area < budget.MinArea * ChildrenAreaFactor) return;
        if (tile.Width <= 2 * Padding + 2 || tile.Height <= 2 * Padding + 2) return;

        // Passt der Name in eine Kopfzeile, ruecken die Kinder darunter — so bleibt sichtbar,
        // wozu eine Gruppe von Kacheln gehoert, ohne dass der Name etwas verdeckt.
        double top = tile.Width >= MinWidthForHeader && tile.Height >= MinHeightForHeader
            ? HeaderHeight
            : Padding;

        LayoutChildren(store, node, new Rect(
            tile.X + Padding, tile.Y + top,
            tile.Width - 2 * Padding, tile.Height - top - Padding), depth + 1, budget, items);
    }

    private static double Worst(double rowArea, double maxArea, double minArea, double side)
    {
        if (rowArea <= 0 || minArea <= 0) return double.MaxValue;
        double s2 = rowArea * rowArea;
        double w2 = side * side;
        return Math.Max(w2 * maxArea / s2, s2 / (w2 * minArea));
    }
}

/// <summary>
/// Flame Graph: Tiefe nach unten, Groesse nach rechts. Jede Zeile ist eine Ebene, jedes Kind
/// bekommt den Anteil der Elternbreite, der seiner Groesse entspricht. Zeigt die Form des
/// Baums deutlicher als eine Treemap — man sieht sofort, wie tief eine Last verschachtelt ist.
/// </summary>
public sealed class FlameEngine : ILayoutEngine
{
    public ViewKind Kind => ViewKind.Flame;
    public string Title => Loc.T("View_Flame");
    public string Hint => Loc.T("View_FlameHint");

    private const double RowGap = 1;
    private const double MinWidth = 1.5;
    private const double MaxRowHeight = 44;
    private const double ProbeRowHeight = 22;

    /// <summary>
    /// Zwei Durchlaeufe: Der erste misst, wie tief der Baum hier ueberhaupt reicht, der zweite
    /// verteilt die vorhandene Hoehe auf genau diese Ebenen. Ohne das saesse ein flacher Baum
    /// als schmaler Streifen am oberen Rand und liesse den Rest der Flaeche leer.
    /// </summary>
    public IReadOnlyList<VisualItem> Layout(NodeStore store, int rootIndex, Rect bounds, LayoutBudget budget)
    {
        int maxRows = Math.Max(1, (int)(bounds.Height / ProbeRowHeight));

        var probe = new List<VisualItem>(2048);
        LayoutRow(store, rootIndex, bounds.X, bounds.Width, 0, maxRows, ProbeRowHeight, bounds, budget, probe);
        if (probe.Count == 0) return probe;

        int used = 0;
        foreach (VisualItem item in probe) used = Math.Max(used, item.Depth + 1);

        double rowHeight = Math.Min(MaxRowHeight, bounds.Height / used);
        if (Math.Abs(rowHeight - ProbeRowHeight) < 0.5) return probe;

        var items = new List<VisualItem>(probe.Count);
        LayoutRow(store, rootIndex, bounds.X, bounds.Width, 0,
            Math.Max(1, (int)(bounds.Height / rowHeight)), rowHeight, bounds, budget, items);
        return items;
    }

    private static void LayoutRow(
        NodeStore store, int node, double x, double width, int depth,
        int maxRows, double rowHeight, Rect bounds, LayoutBudget budget, List<VisualItem> items)
    {
        if (depth >= maxRows || width < MinWidth || !budget.Allows(items.Count)) return;

        int[] order = LayoutHelp.SortedChildren(store, node);
        if (order.Length == 0) return;

        long total = 0;
        foreach (int child in order) total += store.Size[child];
        if (total <= 0) return;

        double y = bounds.Y + depth * rowHeight;
        double cursor = x;

        foreach (int child in order)
        {
            if (!budget.Allows(items.Count)) return;

            double w = width * (store.Size[child] / (double)total);
            if (w < MinWidth) break;      // Rest ist zu schmal, Reihenfolge ist absteigend

            var bar = new Rect(cursor, y, Math.Max(0.5, w - RowGap), rowHeight - RowGap);
            if (!budget.Fits(bar.Width * bar.Height)) break;

            items.Add(VisualItem.OfRect(child, depth, store.IsDirectory(child), bar));

            if (store.IsDirectory(child))
                LayoutRow(store, child, cursor, w, depth + 1, maxRows, rowHeight, bounds, budget, items);

            cursor += w;
        }
    }
}

/// <summary>
/// Ordner-Grid: keine Karte des ganzen Baums, sondern eine Ebene zum Durchblaettern.
/// Die direkten Kinder als gleich grosse Karten, sortiert nach Groesse — dieselbe Ordnung
/// wie im Explorer, nur mit dem Gewicht davor.
/// </summary>
public sealed class FoldersEngine : ILayoutEngine
{
    public ViewKind Kind => ViewKind.Folders;
    public string Title => Loc.T("View_Folders");
    public string Hint => Loc.T("View_FoldersHint");
    public bool WantsNodeLabels => false;     // die Karten beschriften sich selbst

    private const double CardWidth = 178;
    private const double CardHeight = 74;
    private const double Gap = 10;

    public IReadOnlyList<VisualItem> Layout(NodeStore store, int rootIndex, Rect bounds, LayoutBudget budget)
    {
        int[] order = LayoutHelp.SortedChildren(store, rootIndex);
        if (order.Length == 0) return [];

        int columns = Math.Max(1, (int)((bounds.Width + Gap) / (CardWidth + Gap)));
        int rows = Math.Max(1, (int)((bounds.Height + Gap) / (CardHeight + Gap)));
        int capacity = Math.Min(columns * rows, budget.MaxItems);

        double width = (bounds.Width - (columns - 1) * Gap) / columns;
        if (!budget.Fits(width * CardHeight)) return [];

        var items = new List<VisualItem>(Math.Min(order.Length, capacity));

        for (int i = 0; i < order.Length && i < capacity; i++)
        {
            int column = i % columns, row = i / columns;
            var rect = new Rect(
                bounds.X + column * (width + Gap),
                bounds.Y + row * (CardHeight + Gap),
                width, CardHeight);

            items.Add(VisualItem.OfRect(order[i], 0, store.IsDirectory(order[i]), rect));
        }

        return items;
    }

    public void DrawOverlay(
        DrawingContext context, NodeStore store,
        IReadOnlyList<VisualItem> items, Rect bounds, ChartTheme theme)
    {
        long largest = 0;
        foreach (VisualItem item in items) largest = Math.Max(largest, store.Size[item.NodeIndex]);
        if (largest <= 0) largest = 1;

        foreach (VisualItem item in items)
        {
            Rect r = item.Bounds;
            int node = item.NodeIndex;

            FormattedText name = LayoutHelp.Text(
                store.GetName(node), theme.UiBold, 12, theme.Ink, r.Width - 20);
            context.DrawText(name, new Point(r.X + 10, r.Y + 9));

            FormattedText size = LayoutHelp.Text(
                Sizes.Format(store.Size[node]), theme.Mono, 15, theme.Ink);
            context.DrawText(size, new Point(r.X + 10, r.Y + 28));

            // Gewichtsbalken relativ zum groessten Eintrag dieser Ebene
            double track = r.Width - 20;
            var trackRect = new Rect(r.X + 10, r.Bottom - 14, track, 3);
            context.FillRectangle(theme.Track, trackRect, 1.5f);

            double filled = track * (store.Size[node] / (double)largest);
            context.FillRectangle(theme.FillFor(store, node),
                new Rect(trackRect.X, trackRect.Y, Math.Max(2, filled), 3), 1.5f);
        }
    }
}
