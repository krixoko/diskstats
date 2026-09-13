using Avalonia;
using Avalonia.Media;
using DiskStats.Core.Storage;

using DiskStats.App.Localization;

namespace DiskStats.App.Rendering.Engines;

/// <summary>
/// Sammelt die k groessten Eintraege, ohne den ganzen Teilbaum zu sortieren.
///
/// Eine Halde mit fester Kapazitaet: Oben liegt stets der kleinste der bisher besten k, und
/// jedes Angebot kostet hoechstens log k — ein Lauf ueber zwei Millionen Dateien bleibt damit
/// bei Millisekunden. Eine sortierte Liste mit Einfuegen brauchte k Schritte je Angebot.
///
/// Die Reihenfolge des Ergebnisses ist die einer vollstaendigen, stabilen Sortierung:
/// absteigend nach Groesse, bei Gleichstand in der Reihenfolge des Angebots. Dafuer traegt
/// jeder Eintrag seine laufende Nummer, und unter gleich grossen weicht der zuletzt gekommene.
/// </summary>
internal sealed class TopList(int capacity)
{
    private readonly PriorityQueue<(int Node, long Size, int Order), (long Size, int Order)> _heap =
        new(capacity + 1, Worst);

    private int _next;
    private (int Node, long Size)[]? _sorted;

    /// <summary>Kleinste Groesse zuerst; unter gleichen der juengste Eintrag — er muss als erster weichen.</summary>
    private static readonly Comparer<(long Size, int Order)> Worst = Comparer<(long Size, int Order)>.Create(
        (a, b) => a.Size != b.Size ? a.Size.CompareTo(b.Size) : b.Order.CompareTo(a.Order));

    public void Offer(int node, long size)
    {
        if (capacity <= 0) return;

        int order = _next++;
        _sorted = null;

        if (_heap.Count < capacity)
        {
            _heap.Enqueue((node, size, order), (size, order));
            return;
        }

        // Voll: Nur wer den kleinsten uebertrifft, kommt hinein — bei Gleichstand bleibt der
        // aeltere, wie in der stabilen Sortierung.
        if (size <= _heap.Peek().Size) return;
        _heap.EnqueueDequeue((node, size, order), (size, order));
    }

    public IReadOnlyList<(int Node, long Size)> Entries
    {
        get
        {
            if (_sorted is not null) return _sorted;

            var all = new (int Node, long Size, int Order)[_heap.Count];
            int i = 0;
            foreach (((int Node, long Size, int Order) entry, _) in _heap.UnorderedItems) all[i++] = entry;

            Array.Sort(all, (a, b) => a.Size != b.Size ? b.Size.CompareTo(a.Size) : a.Order.CompareTo(b.Order));

            _sorted = new (int Node, long Size)[all.Length];
            for (int k = 0; k < all.Length; k++) _sorted[k] = (all[k].Node, all[k].Size);
            return _sorted;
        }
    }
}

/// <summary>
/// Top Sizes: die groessten Dateien des Teilbaums als Rangliste. Die einzige Ansicht ohne
/// Geometrie-Metapher — und oft die schnellste Antwort, weil sie die Frage "was kann weg"
/// direkt beantwortet statt sie zeigen zu lassen.
/// </summary>
public sealed class TopSizesEngine : ILayoutEngine
{
    public ViewKind Kind => ViewKind.TopSizes;
    public string Title => Loc.T("View_TopSizes");
    public string Hint => Loc.T("View_TopSizesHint");
    public bool WantsNodeLabels => false;
    public bool PaintsShapes => false;

    private const double RowHeight = 30;

    public IReadOnlyList<VisualItem> Layout(NodeStore store, int rootIndex, Rect bounds, LayoutBudget budget)
    {
        if (!budget.Fits(bounds.Width * (RowHeight - 2))) return [];

        int capacity = Math.Min(budget.MaxItems, Math.Max(1, (int)(bounds.Height / RowHeight)));
        var top = new TopList(capacity);
        LayoutHelp.WalkFiles(store, rootIndex, node => top.Offer(node, store.Size[node]));

        var items = new List<VisualItem>(top.Entries.Count);
        for (int i = 0; i < top.Entries.Count; i++)
        {
            items.Add(VisualItem.OfRect(top.Entries[i].Node, 0, false,
                new Rect(bounds.X, bounds.Y + i * RowHeight, bounds.Width, RowHeight - 2)));
        }

        return items;
    }

    public void DrawOverlay(
        DrawingContext context, NodeStore store,
        IReadOnlyList<VisualItem> items, Rect bounds, ChartTheme theme)
    {
        if (items.Count == 0)
        {
            context.DrawText(
                LayoutHelp.Text(Loc.T("View_NoFiles"), theme.Ui, 13, theme.InkFaint),
                new Point(bounds.X + 4, bounds.Y + 4));
            return;
        }

        long largest = store.Size[items[0].NodeIndex];
        if (largest <= 0) largest = 1;

        const double RankWidth = 34;
        const double SizeWidth = 96;

        for (int i = 0; i < items.Count; i++)
        {
            Rect row = items[i].Bounds;
            int node = items[i].NodeIndex;
            long size = store.Size[node];

            context.DrawText(
                LayoutHelp.Text($"{i + 1}", theme.Mono, 11, theme.InkFaint),
                new Point(row.X + 4, row.Y + 7));

            double nameX = row.X + RankWidth;
            double nameWidth = row.Width - RankWidth - SizeWidth - 8;

            context.DrawText(
                LayoutHelp.Text(store.GetName(node), theme.Ui, 12.5, theme.Ink, nameWidth),
                new Point(nameX, row.Y + 3));

            // Balken unter dem Namen: Laenge relativ zur groessten Datei der Liste.
            var track = new Rect(nameX, row.Y + 21, nameWidth, 3);
            context.FillRectangle(theme.Track, track, 1.5f);
            context.FillRectangle(theme.FillFor(store, node),
                new Rect(track.X, track.Y, Math.Max(2, nameWidth * (size / (double)largest)), 3), 1.5f);

            FormattedText sizeText = LayoutHelp.Text(Sizes.Format(size), theme.Mono, 12, theme.Ink);
            context.DrawText(sizeText, new Point(row.Right - sizeText.Width - 4, row.Y + 6));
        }
    }
}

/// <summary>
/// Age Map: wo die Bytes auf der Zeitachse liegen — und darunter das, was sich daraus ergibt,
/// naemlich grosse Dateien, die seit ueber einem Jahr niemand angefasst hat.
///
/// Bewusst keine reine Statistik: Jede Zeile ist eine echte Datei und damit auswaehlbar.
/// Ein Diagramm aus Jahresbalken saehe aehnlich aus, liesse sich aber nicht benutzen.
/// </summary>
public sealed class AgeMapEngine : ILayoutEngine
{
    public ViewKind Kind => ViewKind.AgeMap;
    public string Title => Loc.T("View_AgeMap");
    public string Hint => Loc.T("View_AgeMapHint");
    public bool WantsNodeLabels => false;
    public bool PaintsShapes => false;

    private const double RowHeight = 30;
    private const double HistogramHeight = 126;

    /// <summary>
    /// Bytes je Jahr, im Layoutschritt berechnet und vom Overlay gezeichnet. Beides laeuft
    /// nacheinander im selben UI-Durchlauf, deshalb genuegt ein Feld.
    /// </summary>
    private (int Year, long Bytes)[] _histogram = [];

    private long _staleBytes;

    public IReadOnlyList<VisualItem> Layout(NodeStore store, int rootIndex, Rect bounds, LayoutBudget budget)
    {
        DateTime now = DateTime.UtcNow;
        long cutoff = now.AddYears(-1).Ticks;

        var perYear = new Dictionary<int, long>();

        // Passt keine Zeile ins Budget, bleibt die Liste leer — das Histogramm oben zeichnet
        // das Overlay trotzdem, es haengt nicht an den Zeilen.
        int capacity = budget.Fits(bounds.Width * (RowHeight - 2))
            ? Math.Min(budget.MaxItems, Math.Max(1, (int)((bounds.Height - HistogramHeight) / RowHeight)))
            : 0;
        var stale = new TopList(capacity);
        long staleTotal = 0;

        LayoutHelp.WalkFiles(store, rootIndex, node =>
        {
            long size = store.Size[node];
            long mtime = store.MTime[node];

            int year = mtime > 0 ? new DateTime(mtime, DateTimeKind.Utc).Year : 0;
            if (year > 1980)
            {
                perYear.TryGetValue(year, out long sum);
                perYear[year] = sum + size;
            }

            if (mtime >= cutoff || mtime <= 0) return;
            stale.Offer(node, size);
            staleTotal += size;
        });

        _staleBytes = staleTotal;
        _histogram = perYear.OrderBy(e => e.Key).Select(e => (e.Key, e.Value)).ToArray();

        var items = new List<VisualItem>(stale.Entries.Count);
        for (int i = 0; i < stale.Entries.Count; i++)
        {
            items.Add(VisualItem.OfRect(stale.Entries[i].Node, 0, false,
                new Rect(bounds.X, bounds.Y + HistogramHeight + i * RowHeight, bounds.Width, RowHeight - 2)));
        }

        return items;
    }

    public void DrawOverlay(
        DrawingContext context, NodeStore store,
        IReadOnlyList<VisualItem> items, Rect bounds, ChartTheme theme)
    {
        DrawHistogram(context, bounds, theme);

        context.FillRectangle(theme.Track,
            new Rect(bounds.X, bounds.Y + HistogramHeight - 40, bounds.Width, 1));

        context.DrawText(
            LayoutHelp.Text(
                items.Count == 0
                    ? Loc.T("View_NothingStale")
                    : Loc.T("View_StaleTotal", Sizes.Format(_staleBytes)),
                theme.UiBold, 11.5, theme.InkMuted),
            new Point(bounds.X + 2, bounds.Y + HistogramHeight - 30));

        if (items.Count == 0) return;

        long largest = store.Size[items[0].NodeIndex];
        if (largest <= 0) largest = 1;

        foreach (VisualItem item in items)
        {
            Rect row = item.Bounds;
            int node = item.NodeIndex;
            long size = store.Size[node];

            // Monat und Jahr in der Schreibweise der Sprache: Punkt im Deutschen, Schraegstrich im Englischen.
            string age = store.MTime[node] > 0
                ? new DateTime(store.MTime[node], DateTimeKind.Utc).ToLocalTime().ToString(Loc.T("View_MonthYear"))
                : Loc.T("Common_Empty");

            double nameWidth = row.Width - 190;

            context.DrawText(
                LayoutHelp.Text(store.GetName(node), theme.Ui, 12.5, theme.Ink, nameWidth),
                new Point(row.X + 2, row.Y + 3));

            var track = new Rect(row.X + 2, row.Y + 21, nameWidth, 3);
            context.FillRectangle(theme.Track, track, 1.5f);
            context.FillRectangle(theme.FillFor(store, node),
                new Rect(track.X, track.Y, Math.Max(2, nameWidth * (size / (double)largest)), 3), 1.5f);

            context.DrawText(
                LayoutHelp.Text(age, theme.Mono, 11.5, theme.InkMuted),
                new Point(row.Right - 168, row.Y + 6));

            FormattedText sizeText = LayoutHelp.Text(Sizes.Format(size), theme.Mono, 12, theme.Ink);
            context.DrawText(sizeText, new Point(row.Right - sizeText.Width - 4, row.Y + 6));
        }
    }

    private void DrawHistogram(DrawingContext context, Rect bounds, ChartTheme theme)
    {
        context.DrawText(
            LayoutHelp.Text(Loc.T("View_AgeAxis"), theme.Ui, 10, theme.InkFaint),
            new Point(bounds.X + 2, bounds.Y));

        if (_histogram.Length == 0) return;

        long peak = 0;
        foreach ((_, long value) in _histogram) peak = Math.Max(peak, value);
        if (peak <= 0) return;

        double chartTop = bounds.Y + 17;
        double chartHeight = HistogramHeight - 86;
        double slot = Math.Min(52, bounds.Width / _histogram.Length);
        double barWidth = Math.Max(3, slot - 5);

        IBrush recent = theme.Fill(FileCategory.Code);
        IBrush old = theme.Fill(FileCategory.Archive);
        int currentYear = DateTime.Now.Year;

        for (int i = 0; i < _histogram.Length; i++)
        {
            (int year, long value) = _histogram[i];
            double height = Math.Max(1, chartHeight * (value / (double)peak));
            double x = bounds.X + i * slot;

            // Aelter als ein Jahr wird abgesetzt eingefaerbt — das ist die Menge,
            // um die es in der Liste darunter geht.
            context.FillRectangle(year >= currentYear - 1 ? recent : old,
                new Rect(x, chartTop + chartHeight - height, barWidth, height), 2);

            if (slot < 26) continue;
            context.DrawText(
                LayoutHelp.Text($"{year % 100:00}", theme.Mono, 9.5, theme.InkFaint),
                new Point(x, chartTop + chartHeight + 3));
        }
    }
}
