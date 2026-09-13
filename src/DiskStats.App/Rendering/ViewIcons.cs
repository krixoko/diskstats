namespace DiskStats.App.Rendering;

/// <summary>
/// Welches Lucide-Zeichen zu welcher Ansicht gehoert.
///
/// Jedes bildet nach, wie die Ansicht anordnet, nicht was sie misst: Das Treemap-Zeichen zeigt
/// ungleich grosse Felder, das Flame-Zeichen eine Flamme, die Rangliste absteigende Balken.
/// Wo ein Satzzeichen die Anordnung nicht trifft, gewinnt die Bedeutung — die Altersansicht
/// bekommt ein Raster, weil sie eines ist.
/// </summary>
public static class ViewIcons
{
    public static string For(ViewKind kind) => kind switch
    {
        ViewKind.Treemap => Icons.LayoutDashboard,
        ViewKind.Folders => Icons.Folder,
        ViewKind.Files => Icons.ListTree,
        ViewKind.Sunburst => Icons.ChartPie,
        ViewKind.Flame => Icons.Flame,
        ViewKind.Bubbles => Icons.ChartScatter,
        ViewKind.MindMap => Icons.GitFork,
        ViewKind.TopSizes => Icons.ChartBarDecreasing,
        ViewKind.AgeMap => Icons.Grid3x3,
        _ => Icons.Folder,
    };
}
