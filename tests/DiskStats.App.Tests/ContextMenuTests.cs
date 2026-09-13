using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using DiskStats.App.Localization;

namespace DiskStats.App.Tests;

/// <summary>
/// Das Kontextmenue der Karte, an einem wirklich gescannten Ordner.
///
/// Der Anlass: Es sah einmal aus, als taete es nichts — weil "Hineingehen" bei einer Datei
/// ausgegraut ist. Das ist richtig so, sieht aber aus wie ein Fehler. Dieser Test haelt beides
/// fest: dass der Punkt bei einem Ordner dasteht und bei einer Datei nicht.
/// </summary>
public class ContextMenuTests : IDisposable
{
    private readonly string _sandbox;
    private readonly string _tree;
    private readonly string _settings;
    private readonly string _snapshots;
    private readonly string _logs;

    public ContextMenuTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "diskstats-ctx-" + Guid.NewGuid().ToString("N")[..8]);
        _tree = Path.Combine(_sandbox, "baum");

        Directory.CreateDirectory(Path.Combine(_tree, "ordner"));
        File.WriteAllBytes(Path.Combine(_tree, "ordner", "drin.bin"), new byte[200_000]);
        File.WriteAllBytes(Path.Combine(_tree, "datei.bin"), new byte[100_000]);

        _settings = AppSettings.Folder;
        _snapshots = Core.Storage.SnapshotStore.Folder;
        _logs = Core.Diagnostics.DiagnosticLog.Folder;

        AppSettings.Folder = _sandbox;
        Core.Storage.SnapshotStore.Folder = Path.Combine(_sandbox, "snapshots");
        Core.Diagnostics.DiagnosticLog.Folder = Path.Combine(_sandbox, "logs");
        Loc.Current = Language.English;

        // Die Rechtefrage wuerde den Start anhalten, bis jemand antwortet.
        AppSettings.Current.ElevationPrompt = false;
        AppSettings.Current.AutoElevate = false;
    }

    public void Dispose()
    {
        AppSettings.Folder = _settings;
        Core.Storage.SnapshotStore.Folder = _snapshots;
        Core.Diagnostics.DiagnosticLog.Folder = _logs;

        try { Directory.Delete(_sandbox, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// Oeffnet das Fenster mit einem Scanziel und wartet, bis der Baum steht.
    ///
    /// Der Scan laeuft nebenlaeufig und meldet sich ueber den Nachrichtenfaden zurueck; ohne
    /// das Abarbeiten der Warteschlange kaeme das Ergebnis nie an.
    /// </summary>
    private MainWindow Scan()
    {
        var window = new MainWindow(["diskstats", _tree]);
        window.Show();

        for (int i = 0; i < 200 && window.Chart.NodeCount == 0; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(25);
        }

        return window;
    }

    [AvaloniaFact]
    public void Ctrl_click_preserves_selection_and_hover_path_is_independent()
    {
        MainWindow window = Scan();
        try
        {
            var points = new Dictionary<int, Point>();
            for (int y = 1; y < 40 && points.Count < 2; y++)
                for (int x = 1; x < 40 && points.Count < 2; x++)
                {
                    var point = new Point(window.Chart.Bounds.Width * x / 40, window.Chart.Bounds.Height * y / 40);
                    Hover(window, point);
                    int node = window.Chart.HoveredNode;
                    if (node >= 0 && !window.Store!.IsDirectory(node)) points.TryAdd(node, point);
                }
            Assert.Equal(2, points.Count);
            var files = points.ToArray();
            Point At(int i) => window.Chart.TranslatePoint(files[i].Value, window)!.Value;
            window.MouseDown(At(0), MouseButton.Left);
            window.MouseUp(At(0), MouseButton.Left);
            Hover(window, files[1].Value);
            Assert.Equal(window.Store!.PathOf(files[1].Key), window.HoverPath.Text);
            Assert.Equal(window.Store.GetName(files[0].Key), window.InspectorName.Text);
            window.MouseDown(At(1), MouseButton.Left, RawInputModifiers.Control);
            window.MouseUp(At(1), MouseButton.Left, RawInputModifiers.Control);
            Assert.Equal("2 entries", window.InspectorName.Text);
            window.MouseDown(At(1), MouseButton.Left, RawInputModifiers.Control);
            window.MouseUp(At(1), MouseButton.Left, RawInputModifiers.Control);
            Assert.Equal(window.Store.GetName(files[0].Key), window.InspectorName.Text);
            window.MouseMove(new Point(0, 0));
            Assert.Equal(string.Empty, window.HoverPath.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Der_scan_kommt_wirklich_an()
    {
        MainWindow window = Scan();

        Assert.True(window.Chart.NodeCount > 0, "Es wurde nichts gescannt.");
    }

    /// <summary>
    /// Zeigt auf eine Stelle der Karte.
    ///
    /// Ueber die Eingabehilfe des kopflosen Betriebs, nicht ueber ein selbst gebautes
    /// Ereignis: Ein von Hand erzeugtes PointerEventArgs kam beim Steuerelement nicht an, und
    /// weil das Menue dann auf -1 stand, bestand der erste Entwurf dieses Tests aus dem
    /// falschen Grund. Die Punktangabe ist fensterbezogen, deshalb die Umrechnung.
    /// </summary>
    private static void Hover(MainWindow window, Point inChart)
    {
        Point? inWindow = window.Chart.TranslatePoint(inChart, window);
        if (inWindow is null) return;

        window.MouseMove(inWindow.Value);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Sucht eine Stelle, unter der eine Kachel der gewuenschten Art liegt.
    ///
    /// Feste Koordinaten waeren geraten: Wo die Treemap welche Kachel hinlegt, haengt vom
    /// Seitenverhaeltnis ab. Der erste Versuch traf die Datei nicht und der Test log.
    /// </summary>
    private static Point PointOver(MainWindow window, bool directory)
    {
        double w = window.Chart.Bounds.Width;
        double h = window.Chart.Bounds.Height;

        // Fein, weil in einer Treemap das Kind seinen Ordner ueberdeckt: Vom Ordner selbst
        // bleibt nur der Streifen mit seinem Namen uebrig.
        for (int y = 1; y < 80; y++)
        {
            for (int x = 1; x < 80; x++)
            {
                var at = new Point(w * x / 80, h * y / 80);
                Hover(window, at);

                int node = window.Chart.HoveredNode;
                if (node > 0 && window.Store!.IsDirectory(node) == directory) return at;
            }
        }

        throw new InvalidOperationException(
            directory ? "Keine Ordnerkachel gefunden." : "Keine Dateikachel gefunden.");
    }

    /// <summary>
    /// Rechtsklick an eine Stelle der Karte und den Menuepunkt zurueckgeben.
    ///
    /// Wirklich klicken, nicht ContextMenu.Open() rufen: Der Aufruf loest das Opening-Ereignis
    /// nicht aus, und genau darin merkt sich das Menue, worauf geklickt wurde. Der erste
    /// Entwurf dieses Tests pruefte deshalb nichts.
    /// </summary>
    private static MenuItem OpenMenuOver(MainWindow window, Point at)
    {
        Hover(window, at);

        Point inWindow = window.Chart.TranslatePoint(at, window)!.Value;
        window.MouseDown(inWindow, MouseButton.Right);
        window.MouseUp(inWindow, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();

        return window.Chart.ContextMenu!.ItemsSource!.OfType<MenuItem>()
            .First(i => Equals(i.Header, Loc.T("Menu_Open")));
    }

    /// <summary>
    /// Ueber einem Ordner ist "Hineingehen" nutzbar. Waere es das nicht, taete das Menue
    /// tatsaechlich nichts — genau der Eindruck, der den Anlass gab.
    /// </summary>
    [AvaloniaFact]
    public void Ueber_einem_ordner_greift_hineingehen()
    {
        MainWindow window = Scan();
        Assert.True(window.Chart.NodeCount > 0);

        MenuItem open = OpenMenuOver(window, PointOver(window, directory: true));

        Assert.True(open.IsVisible, "Ueber einem Ordner muesste Hineingehen dastehen.");
    }

    /// <summary>
    /// Und ueber einer Datei steht er gar nicht erst da — in eine Datei fuehrt kein Weg
    /// hinein. Ausgegraut sah es aus, als taete das ganze Menue nichts.
    /// </summary>
    [AvaloniaFact]
    public void Ueber_einer_datei_fehlt_hineingehen()
    {
        MainWindow window = Scan();
        Assert.True(window.Chart.NodeCount > 0);

        MenuItem open = OpenMenuOver(window, PointOver(window, directory: false));

        Assert.False(open.IsVisible);
    }

    /// <summary>Holt einen Punkt aus dem geoeffneten Menue.</summary>
    private static MenuItem ItemNamed(MainWindow window, string key)
        => window.Chart.ContextMenu!.ItemsSource!.OfType<MenuItem>()
            .First(i => Equals(i.Header, Loc.T(key)));

    /// <summary>
    /// Dass ein Punkt dasteht, heisst nicht, dass er etwas tut.
    ///
    /// Genau diese Luecke wurde gemeldet: Bis zum Fix las jeder Punkt den Knoten erst beim
    /// Klick, und bis dahin war der Zeiger laengst auf dem Menue — alle Punkte taten wortlos
    /// nichts. Ein Test auf Sichtbarkeit haette das nicht bemerkt.
    /// </summary>
    [AvaloniaFact]
    public void Vormerken_landet_wirklich_auf_der_liste()
    {
        MainWindow window = Scan();
        OpenMenuOver(window, PointOver(window, directory: false));

        ItemNamed(window, "Menu_Stage").RaiseEvent(
            new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, window.StagedCount);
    }

    [AvaloniaFact]
    public void Pfad_kopieren_meldet_sich_in_der_statuszeile()
    {
        MainWindow window = Scan();
        OpenMenuOver(window, PointOver(window, directory: false));

        ItemNamed(window, "Menu_CopyPath").RaiseEvent(
            new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Loc.T("Stat_PathCopied"), window.StatusText.Text);
    }

    /// <summary>Ohne Ziel darf ein Klick nichts anrichten — der Fall, der einmal abstuerzte.</summary>
    [AvaloniaFact]
    public void Ein_klick_ohne_ziel_richtet_nichts_an()
    {
        MainWindow window = Scan();

        // Kein Rechtsklick vorher: Das Menue kennt kein Ziel.
        ItemNamed(window, "Menu_Stage").RaiseEvent(
            new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, window.StagedCount);
        Assert.True(window.IsVisible);
    }

    /// <summary>Die uebrigen Punkte gelten immer, sonst waere das Menue oft leer.</summary>
    [AvaloniaFact]
    public void Die_uebrigen_punkte_bleiben_nutzbar()
    {
        MainWindow window = Scan();

        MenuItem[] items =
        [
            .. window.Chart.ContextMenu!.ItemsSource!.OfType<MenuItem>()
                .Where(i => !Equals(i.Header, Loc.T("Menu_Open"))),
        ];

        Assert.Equal(6, items.Length);
        Assert.All(items, i => Assert.True(i.IsEnabled));
    }
}
