using Avalonia.Media;
using DiskStats.Core.Storage;

namespace DiskStats.App.Rendering;

public enum ColorMode { ByFolder, ByType, ByAge }

/// <summary>
/// Erzeugt die Flaechenfarben.
///
/// Saturated branch colors separate neighboring folders while keeping labels readable.
/// </summary>
public static class Palette
{
    /// <summary>
    /// Zwoelf Farbtoene fuer die Ordnerfaerbung, gleichmaessig ueber den Farbkreis verteilt.
    /// Gelbgruen und Cyan sind ausgespart, weil sie neben den Nachbartoenen aus der Reihe fallen.
    /// </summary>
    private static readonly double[] FolderHues =
        [8, 218, 48, 285, 168, 340, 92, 250, 32, 196, 316, 132];

    public static Color ForFolderBranch(int branchIndex, int depth, bool dark)
    {
        double hue = FolderHues[Math.Abs(branchIndex) % FolderHues.Length];

        // Mit der Tiefe leicht aufhellen: Kinder heben sich vom Elternfeld ab,
        // bleiben aber erkennbar derselbe Zweig.
        double lightness = dark ? 0.32 + depth * 0.025 : 0.76 - depth * 0.022;
        double saturation = dark ? 0.65 : 0.68 - depth * 0.015;

        return FromHsl(hue, Math.Clamp(saturation, 0.50, 0.75), Math.Clamp(lightness, 0.30, 0.94));
    }

    /// <summary>
    /// Kategoriefarben in derselben Pastellfamilie wie die Ordnertoene.
    ///
    /// Die Helligkeiten sind bewusst gestaffelt: Wer Rot und Gruen nicht auseinanderhaelt,
    /// sieht von diesen Toenen nur Helligkeit und die Blau-Gelb-Achse. Auf denen liegen die
    /// acht Toene so weit auseinander, wie es in Pastell geht — geprueft in den
    /// Accessibility-Tests unter Deuteranopie-Simulation.
    /// </summary>
    private static readonly (double Hue, double Sat, double Light)[] CategoryTones =
    [
        (26, 0.12, 0.810),  // Sonstiges — warmes Grau
        (8, 0.44, 0.780),   // Video     — Altrosa
        (35, 0.50, 0.785),  // Bilder    — Sandgold
        (278, 0.33, 0.875), // Audio     — Flieder
        (159, 0.31, 0.890), // Dokumente — Salbei
        (222, 0.37, 0.780), // Code      — Puderblau
        (323, 0.35, 0.785), // Archive   — Rose
        (89, 0.32, 0.850),  // Programme — Blassgruen
    ];

    public static Color ForCategory(FileCategory category, bool dark)
        => ForCategory(category, 0, dark);

    /// <summary>
    /// Dieselbe Kategorie in leicht abgestufter Helligkeit. In einer Endungsliste gehoeren
    /// .exe, .dll und .vhdx alle zu "Programme" und saehen sonst gleich aus — die Abstufung
    /// haelt die Zeilen auseinander, ohne die Zuordnung zu verfaelschen.
    /// </summary>
    public static Color ForCategory(FileCategory category, int variant, bool dark)
    {
        (double hue, double saturation, double lightness) = CategoryTones[(int)category];

        saturation = Math.Min(0.85, saturation * 1.3);
        double step = (variant % 4) * (dark ? 0.045 : -0.045);
        double shifted = Math.Clamp(lightness + step, 0.18, 0.94);

        return dark
            ? FromHsl(hue, saturation * 0.72, shifted - 0.38)
            : FromHsl(hue, saturation, shifted);
    }

    /// <summary>
    /// Alter als Farbverlauf: frisch ist warm, alt kuehlt aus. Die Skala ist bewusst grob —
    /// niemand liest ein Datum aus einem Farbton ab, aber "das da drueben ist alles alt"
    /// erkennt man auf einen Blick.
    /// </summary>
    public static Color ForAge(long mtimeUtcTicks, long nowTicks, bool dark)
    {
        if (mtimeUtcTicks <= 0) return FromHsl(36, 0.10, dark ? 0.34 : 0.86);

        double years = (nowTicks - mtimeUtcTicks) / (double)TimeSpan.TicksPerDay / 365.25;
        double t = Math.Clamp(years / 5.0, 0, 1);

        double hue = 32 + t * 178;                    // Bernstein nach Blaugruen
        double saturation = 0.72 - t * 0.14;
        double lightness = dark ? 0.44 - t * 0.06 : 0.82 - t * 0.06;

        return FromHsl(hue, saturation, lightness);
    }

    /// <summary>Sehr helle Fassung eines Tons — Untergrund einer Ordnerkachel.</summary>
    public static Color Wash(Color color, bool dark)
    {
        (double hue, double saturation, double lightness) = ToHsl(color);
        return dark
            ? FromHsl(hue, saturation * 0.55, Math.Max(0.16, lightness - 0.20))
            : FromHsl(hue, saturation * 0.50, Math.Min(0.955, lightness + 0.11));
    }

    public static Color FromHsl(double hue, double saturation, double lightness)
    {
        hue = ((hue % 360) + 360) % 360;
        double c = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        double x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        double m = lightness - c / 2;

        (double r, double g, double b) = hue switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    private static (double Hue, double Saturation, double Lightness) ToHsl(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double lightness = (max + min) / 2;

        if (Math.Abs(max - min) < 1e-6) return (0, 0, lightness);

        double delta = max - min;
        double saturation = delta / (1 - Math.Abs(2 * lightness - 1));

        double hue;
        if (Math.Abs(max - r) < 1e-6) hue = 60 * (((g - b) / delta) % 6);
        else if (Math.Abs(max - g) < 1e-6) hue = 60 * ((b - r) / delta + 2);
        else hue = 60 * ((r - g) / delta + 4);

        return (hue, saturation, lightness);
    }
}

/// <summary>
/// Ordnet jedem Knoten seine Farbe zu und merkt sich das Ergebnis. Ohne Zwischenspeicher
/// muesste fuer jede Flaeche bei jedem Bild erneut der Zweig oder die groesste Datei
/// bestimmt werden.
/// </summary>
public sealed class NodeColoring
{
    private readonly Dictionary<int, IBrush> _fills = [];
    private readonly Dictionary<int, IBrush> _washes = [];
    private int[] _branchOf = [];
    private NodeStore? _store;

    public ColorMode Mode { get; private set; } = ColorMode.ByFolder;
    public bool Dark { get; private set; }

    public void Configure(NodeStore? store, ColorMode mode, bool dark)
    {
        bool sameTree = ReferenceEquals(store, _store);
        if (sameTree && mode == Mode && dark == Dark) return;

        Mode = mode;
        Dark = dark;
        _store = store;
        _fills.Clear();
        _washes.Clear();

        if (!sameTree) BuildBranchIndex(store);
    }

    /// <summary>
    /// Ordnet jeden Knoten dem obersten Ordner zu, unter dem er liegt. Diese Zuordnung traegt
    /// die Ordnerfaerbung: Ein ganzer Zweig teilt sich einen Farbton.
    /// </summary>
    private void BuildBranchIndex(NodeStore? store)
    {
        if (store is null) { _branchOf = []; return; }

        _branchOf = new int[store.Count];
        Array.Fill(_branchOf, -1);

        int start = store.ChildStart[0];
        int count = store.ChildCount[0];
        for (int i = 0; i < count; i++) _branchOf[start + i] = i;

        // Kinder haben stets einen groesseren Index als ihr Elternteil, ein Vorwaertslauf genuegt.
        for (int i = 1; i < store.Count; i++)
        {
            if (_branchOf[i] >= 0) continue;
            int parent = store.ParentIndex[i];
            if (parent >= 0) _branchOf[i] = _branchOf[parent];
        }
    }

    public IBrush Fill(NodeStore store, int node, int depth)
    {
        if (_fills.TryGetValue(node, out IBrush? cached)) return cached;

        var brush = new SolidColorBrush(ColorFor(store, node, depth));
        _fills[node] = brush;
        return brush;
    }

    public IBrush Wash(NodeStore store, int node, int depth)
    {
        if (_washes.TryGetValue(node, out IBrush? cached)) return cached;

        var brush = new SolidColorBrush(Palette.Wash(ColorFor(store, node, depth), Dark));
        _washes[node] = brush;
        return brush;
    }

    private Color ColorFor(NodeStore store, int node, int depth) => Mode switch
    {
        ColorMode.ByType => Palette.ForCategory(CategoryOf(store, node), Dark),
        ColorMode.ByAge => Palette.ForAge(store.MTime[node], DateTime.UtcNow.Ticks, Dark),
        _ => Palette.ForFolderBranch(
            node < _branchOf.Length && _branchOf[node] >= 0 ? _branchOf[node] : node,
            Math.Min(depth, 5), Dark),
    };

    /// <summary>
    /// Kategorie eines Knotens. Ordner erben die ihres groessten Inhalts — pauschales Grau
    /// faellt in der Treemap nicht auf, weil Kinder den Ordner ueberdecken, wuerde in Sunburst
    /// und Bubbles aber das halbe Bild entfaerben.
    /// </summary>
    public static FileCategory CategoryOf(NodeStore store, int node)
    {
        if (!store.IsDirectory(node)) return FileCategories.Of(store.GetName(node));

        for (int guard = 0; guard < 64; guard++)
        {
            int start = store.ChildStart[node];
            int count = store.ChildCount[node];
            if (count == 0) return FileCategory.Other;

            int best = -1;
            long bestSize = -1;
            for (int i = start; i < start + count; i++)
            {
                if (store.Size[i] <= bestSize) continue;
                bestSize = store.Size[i];
                best = i;
            }

            if (best < 0) return FileCategory.Other;
            if (!store.IsDirectory(best)) return FileCategories.Of(store.GetName(best));
            node = best;
        }

        return FileCategory.Other;
    }
}
