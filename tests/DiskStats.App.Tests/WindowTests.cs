using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DiskStats.App.Localization;
using DiskStats.App.Rendering;

namespace DiskStats.App.Tests;

/// <summary>
/// Tests auf dem gebauten Fenster.
///
/// Der Anlass: Alle Fehler dieses Projekts, die bis ins Bild durchkamen, sassen hier — eine
/// Bindung ohne Datentyp, ein Schildchen, das zwei Zeilen spaeter ueberschrieben wurde, ein
/// Klickpfad, der ins Leere greift. Der Uebersetzer sah nichts davon, die Kerntests auch
/// nicht, weil keiner von beiden das Fenster je zusammenbaut.
/// </summary>
public class WindowTests : IDisposable
{
    [AvaloniaFact]
    public async Task Benchmark_dialog_blocks_scans_and_duplicate_dialogs_until_closed()
    {
        var window = new MainWindow([]);
        window.Show();
        var dismiss = new TaskCompletionSource();
        try
        {
            Task dialog = window.ShowBenchmarkAsync(() => dismiss.Task);
            bool secondOpened = false;
            await window.ShowBenchmarkAsync(() => { secondOpened = true; return Task.CompletedTask; });
            Assert.False(secondOpened);
            bool scanned = false;
            await window.RunScanAsync(_sandbox, (_, _, _, _) => { scanned = true; return Task.CompletedTask; });
            Assert.False(scanned);
            dismiss.SetResult();
            await dialog;
            await window.ShowBenchmarkAsync(() => { secondOpened = true; return Task.CompletedTask; });
            Assert.True(secondOpened);
        }
        finally { dismiss.TrySetResult(); window.Close(); }
    }

    [AvaloniaFact]
    public void Settings_provide_privacy_and_support_links()
    {
        var window = new SettingsWindow();
        window.Show();
        try
        {
            var links = window.GetVisualDescendants().OfType<HyperlinkButton>()
                .Select(link => link.NavigateUri?.AbsoluteUri).ToArray();
            Assert.Contains("https://diskstats-privacy.krokkono.chatgpt.site/", links);
            Assert.Contains("mailto:kroxoko@gmail.com", links);
        }
        finally { window.Close(); }
    }

    private readonly string _sandbox;
    private readonly string _settings;
    private readonly string _snapshots;
    private readonly string _logs;

    /// <summary>
    /// Alles, was die Anwendung schreibt, in ein eigenes Verzeichnis umlenken. Ein Test darf
    /// die Einstellungen des Benutzers weder lesen noch ueberschreiben — sonst haengt sein
    /// Ausgang davon ab, wer ihn ausfuehrt.
    /// </summary>
    public WindowTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "diskstats-ui-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_sandbox);

        _settings = AppSettings.Folder;
        _snapshots = Core.Storage.SnapshotStore.Folder;
        _logs = Core.Diagnostics.DiagnosticLog.Folder;

        AppSettings.Folder = _sandbox;
        Core.Storage.SnapshotStore.Folder = Path.Combine(_sandbox, "snapshots");
        Core.Diagnostics.DiagnosticLog.Folder = Path.Combine(_sandbox, "logs");
        Loc.Current = Language.English;
    }

    public void Dispose()
    {
        AppSettings.Folder = _settings;
        Core.Storage.SnapshotStore.Folder = _snapshots;
        Core.Diagnostics.DiagnosticLog.Folder = _logs;

        try { Directory.Delete(_sandbox, recursive: true); } catch (IOException) { }
    }

    /// <summary>Ohne Argumente: kein Scan, keine Wiederherstellung, nur der Aufbau.</summary>
    private static MainWindow Open()
    {
        var window = new MainWindow([]);
        window.Show();
        return window;
    }

    [AvaloniaFact]
    public void Das_fenster_baut_sich_auf()
    {
        MainWindow window = Open();

        Assert.True(window.IsVisible);
    }

    /// <summary>
    /// Auch die Nebenfenster: Ein Fehler in ihrem AXAML — eine Bindung ohne x:DataType etwa —
    /// faellt sonst erst auf, wenn jemand das Zahnrad anklickt.
    /// </summary>
    [AvaloniaFact]
    public void Die_nebenfenster_bauen_sich_auf()
    {
        var settings = new SettingsWindow();
        var picker = new FolderPickerWindow();

        settings.Show();
        picker.Show();

        Assert.True(settings.IsVisible);
        Assert.True(picker.IsVisible);
    }

    [AvaloniaFact]
    public void Der_umschalter_zeigt_jede_ansicht()
    {
        MainWindow window = Open();

        int buttons = window.ViewSwitcher.Children.OfType<Button>().Count();

        Assert.Equal(Enum.GetValues<ViewKind>().Length, buttons);
    }

    /// <summary>
    /// Jeder Knopf traegt seinen Hinweis.
    ///
    /// Genau das ging einmal verloren: Die Zuweisung stand da, zwei Zeilen spaeter aber eine
    /// zweite, die nur den Titel setzte. Gebaut wurde sauber, und im Schildchen stand
    /// "Sunburst" statt "Sunburst — Rings by size".
    /// </summary>
    [AvaloniaFact]
    public void Jeder_ansichtsknopf_traegt_seinen_hinweis()
    {
        MainWindow window = Open();

        Dictionary<ViewKind, string> hints = Enum.GetValues<ViewKind>()
            .ToDictionary(kind => kind, kind => kind == ViewKind.Files
                ? Loc.T("View_FilesHint")
                : ChartView.AllEngines.First(e => e.Kind == kind).Hint);

        foreach (Button button in window.ViewSwitcher.Children.OfType<Button>())
        {
            var kind = (ViewKind)button.Tag!;
            object? tip = ToolTip.GetTip(button);

            Assert.NotNull(tip);
            Assert.Contains(hints[kind], tip.ToString());
        }
    }

    [AvaloniaFact]
    public void Der_farbumschalter_bietet_alle_drei_faerbungen()
    {
        MainWindow window = Open();

        Assert.Equal(3, window.ColorSwitcher.Children.OfType<Button>().Count());
    }

    /// <summary>
    /// Das Kontextmenue haengt an der Karte und traegt alle Punkte. Fehlt einer, merkt es
    /// niemand, bis jemand rechtsklickt.
    /// </summary>
    [AvaloniaFact]
    public void Die_karte_hat_ein_kontextmenue()
    {
        MainWindow window = Open();

        ContextMenu? menu = window.Chart.ContextMenu;
        Assert.NotNull(menu);

        string[] headers =
        [
            .. menu.ItemsSource!.OfType<MenuItem>().Select(i => i.Header?.ToString() ?? string.Empty),
        ];

        Assert.Contains(Loc.T("Menu_Open"), headers);
        Assert.Contains(Loc.T("Menu_Reveal"), headers);
        Assert.Contains(Loc.T("Menu_CopyPath"), headers);
        Assert.Contains(Loc.T("Menu_Export"), headers);
        Assert.Contains(Loc.T("Menu_Stage"), headers);
    }

    /// <summary>
    /// Der Absturz von damals, als Test.
    ///
    /// Das Kontextmenue las den Knoten unter dem Zeiger erst beim Klick. Bis dahin war der
    /// Zeiger laengst auf dem Menue, hatte die Karte verlassen, und der gemerkte Knoten stand
    /// auf -1. OnNodeOpened(-1) griff ueber IsDirectory auf Flags[-1] zu und beendete den
    /// Prozess.
    ///
    /// Geprueft wird der Waechter, nicht das Menue: Den Zeigerweg kopflos nachzustellen wuerde
    /// mehr Aufbau verlangen als er beweist — der Schaden entstand hier.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void Ein_ungueltiger_knoten_beendet_nichts(int node)
    {
        MainWindow window = Open();

        window.OnNodeOpened(node);

        Assert.True(window.IsVisible);
    }

    [AvaloniaFact]
    public void Ohne_scan_steht_der_hinweis_zum_anfangen()
    {
        MainWindow window = Open();

        Assert.True(window.EmptyHint.IsVisible);
        Assert.Equal(Loc.T("Head_NothingScanned"), window.StatsText.Text);
    }
}
