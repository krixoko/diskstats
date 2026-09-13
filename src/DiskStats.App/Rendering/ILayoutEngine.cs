using Avalonia;
using Avalonia.Media;
using DiskStats.Core.Storage;

namespace DiskStats.App.Rendering;

public enum ViewKind
{
    Treemap, Folders, Files, Sunburst, Flame, Bubbles, MindMap, TopSizes, AgeMap,
}

/// <summary>Farben und Stifte, die eine Ansicht zum Zeichnen ihrer Zusatzelemente braucht.</summary>
public sealed class ChartTheme
{
    public required bool Dark { get; init; }
    public required IBrush Ink { get; init; }
    public required IBrush InkMuted { get; init; }
    public required IBrush InkFaint { get; init; }
    public required IBrush Track { get; init; }
    public required IPen Hairline { get; init; }
    public required Func<FileCategory, IBrush> Fill { get; init; }

    // Schriften sind teuer im Aufbau und ueberall dieselben — einmal je Prozess genuegt.
    public Typeface Ui => RenderCache.Ui;
    public Typeface UiBold => RenderCache.UiBold;
    public Typeface Mono => RenderCache.Mono;

    public required NodeColoring Coloring { get; init; }

    public IBrush FillFor(NodeStore store, int node, int depth = 0) => Coloring.Fill(store, node, depth);

    public IBrush WashFor(NodeStore store, int node, int depth = 0) => Coloring.Wash(store, node, depth);
}

/// <summary>
/// Bildet einen Ausschnitt des Baums auf Formen ab. Bewusst eine reine Funktion:
/// Baum und Flaeche hinein, Geometrie heraus — dadurch ohne Fenster testbar.
/// </summary>
public interface ILayoutEngine
{
    ViewKind Kind { get; }

    /// <summary>Kurzer Name fuer den Umschalter.</summary>
    string Title { get; }

    /// <summary>Eine Zeile, die erklaert, was man hier sieht.</summary>
    string Hint { get; }

    /// <summary>Ob diese Ansicht Namen auf ihre Formen schreiben laesst.</summary>
    bool WantsNodeLabels => true;

    /// <summary>
    /// Ob die Zeichenflaeche die Formen selbst einfaerbt. Ranglisten setzen das auf false:
    /// Dort ist die Form die Zeile — sie ganz einzufaerben waere ein Farbteppich statt einer
    /// Liste. Solche Ansichten malen ihre Balken im Overlay selbst.
    /// </summary>
    bool PaintsShapes => true;

    /// <summary>Anordnung mit dem Budget aus den Einstellungen.</summary>
    IReadOnlyList<VisualItem> Layout(NodeStore store, int rootIndex, Rect bounds)
        => Layout(store, rootIndex, bounds, LayoutBudget.Current);

    /// <summary>
    /// Anordnung mit ausdruecklichem Budget. Keine Ansicht liefert mehr als
    /// <see cref="LayoutBudget.MaxItems"/> Formen oder eine Form unter
    /// <see cref="LayoutBudget.MinArea"/> — die Wurzel ausgenommen, sie ist der Bezugspunkt.
    /// </summary>
    IReadOnlyList<VisualItem> Layout(NodeStore store, int rootIndex, Rect bounds, LayoutBudget budget);

    /// <summary>Zeichnet unter die Formen — etwa Verbindungslinien einer Baumdarstellung.</summary>
    void DrawUnderlay(
        DrawingContext context, NodeStore store,
        IReadOnlyList<VisualItem> items, Rect bounds, ChartTheme theme)
    { }

    /// <summary>
    /// Zeichnet, was nicht aus Knotenformen besteht — Achsen, Messwerte, Beschriftungen.
    /// Laeuft nach den Formen und vor den Namen.
    /// </summary>
    void DrawOverlay(
        DrawingContext context, NodeStore store,
        IReadOnlyList<VisualItem> items, Rect bounds, ChartTheme theme)
    { }
}
