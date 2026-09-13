using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using DiskStats.App.Controls;
using DiskStats.App.Rendering;
using DiskStats.App.Rendering.Engines;
using DiskStats.App.Localization;
using DiskStats.Core.Analysis;
using DiskStats.Core.Cleanup;
using DiskStats.Core.Diagnostics;
using DiskStats.Core.Scanning;
using DiskStats.Core.Storage;

namespace DiskStats.App;

public partial class MainWindow : Window
{
    private const double DriveBarWidth = 196;
    private const double ChildBarWidth = 240;
    private const double TypeBarWidth = 196;

    /// <summary>Wieviel vorgemerkt ist. Fuer Tests, die pruefen, ob eine Aktion gewirkt hat.</summary>
    internal int StagedCount => _cleanup.Count;

    /// <summary>Der gescannte Baum. Fuer Tests, die pruefen, was unter dem Zeiger liegt.</summary>
    internal NodeStore? Store => _store;

    private readonly string[] _args;

    private NodeStore? _store;
    private string _scanRoot = string.Empty;
    private int _workers = Environment.ProcessorCount;
    private long _driveTotal;
    private string _driveLabel = string.Empty;

    /// <summary>Belegte Bytes des Laufwerks — Bezugsgroesse fuer den Fortschrittsring.</summary>
    private long _expectedBytes;
    private readonly Stack<int> _history = new();
    private int _currentRoot;
    private int _inspected = -1;
    private int _selected = -1;
    private readonly CleanupList _cleanup = new();
    private bool _byExtension = true;
    private string? _highlight;
    private IReadOnlyList<QuickWins.Result> _wins = [];
    private IReadOnlyList<DuplicateGroup> _duplicates = [];
    private CancellationTokenSource? _dupSearch;

    /// <summary>Worauf rechtsgeklickt wurde. Siehe <see cref="BuildContextMenu"/>.</summary>
    private int _menuNode = -1;
    private CancellationTokenSource? _scan;
    private bool _scanRunning;
    private bool _cleanupRunning;
    private FileQuery _fileQuery = new();
    // Alle Snapshot-Speicherungen laufen durch dieselbe Warteschlange — nie zwei zugleich.
    private readonly SnapshotQueue _snapshots = new();
    private Task _snapshotWork = Task.CompletedTask;
    internal bool ScanRunning => _scanRunning;

    public MainWindow() : this(Environment.GetCommandLineArgs()) { }

    /// <summary>
    /// Die Argumente werden hereingereicht statt aus der Umgebung geholt: Im Testlauf sind das
    /// die des Testrunners, und ein Pfad darunter wuerde einen echten Scan ausloesen.
    /// </summary>
    internal MainWindow(string[] args)
    {
        _args = args;
        InitializeComponent();

        BrowseButton.Click += OnBrowseClicked;
        CancelButton.Click += (_, _) => _scan?.Cancel();
        SettingsButton.Click += (_, _) => Guard(new SettingsWindow().ShowDialog(this), "Einstellungen");
        BenchmarkButton.Click += (_, _) => Guard(ShowBenchmarkAsync(), "Laufwerkstempo");
        HealthButton.Click += (_, _) => Guard(new HealthWindow().ShowDialog(this), "Laufwerkszustand");
        ThemeButton.Click += OnThemeClicked;
        UpButton.Click += OnUpClicked;
        RevealButton.Click += OnRevealClicked;
        CopyPathButton.Click += OnCopyPathClicked;

        Chart.NodeHovered += OnNodeHovered;
        Chart.NodeSelected += OnNodeSelected;
        Chart.NodeOpened += OnNodeOpened;

        ActualThemeVariantChanged += (_, _) =>
        {
            UpdateThemeGlyph();
            LoadDrives();
            BuildLegend();
            ShowFileTypes();
            UpdateViewButtons();
            Chart.Invalidate();
            if (_inspected >= 0) ShowNode(_inspected);
        };

        AppSettings.Changed += ApplySettings;
        ApplySettings();

        StageButton.Click += (_, _) => Stage(_inspected);
        CleanupClear.Click += (_, _) => { _cleanup.Clear(); ShowCleanup(); };
        CleanupRun.Click += (_, _) => Guard(RunCleanup(), "Aufraeumen");

        BuildTypeModeSwitch();
        BuildSearch();
        KeyDown += OnWindowKey;
        BuildContextMenu();
        RestoreWindow();
        Closing += (_, _) => RememberWindow();

        FilesTable.NodeSelected += OnNodeSelected;
        FilesTable.NodeOpened += OnNodeOpened;
        FilesTable.NodesStaged += nodes => { foreach (int node in nodes) Stage(node); };
        FilterButton.Click += (_, _) => Guard(EditFilters(), "Filter");
        RefreshFolderButton.Click += (_, _) => Guard(RefreshFolderAsync(_currentRoot), "Ordner aktualisieren");
        WatchToggle.IsCheckedChanged += (_, _) => StartWatching();
        Closed += (_, _) =>
        {
            _closed = true;
            _watcher?.Dispose();
            _refresh?.Cancel();
            _scan?.Cancel();
            _dupSearch?.Cancel();
            AppSettings.Changed -= ApplySettings;
        };
        BuildViewSwitcher();
        BuildColorSwitcher();
        UpdateThemeGlyph();
        LoadDrives();
        BuildLegend();
        ScanFromCommandLine();
    }

    private bool Dark => ActualThemeVariant == ThemeVariant.Dark;

    /// <summary>Zieht die Oberflaeche nach einer Aenderung der Einstellungen nach.</summary>
    private void ApplySettings()
    {
        Avalonia.Application.Current!.RequestedThemeVariant = AppSettings.Current.Theme switch
        {
            ThemeChoice.Light => ThemeVariant.Light,
            ThemeChoice.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };

        Chart.Invalidate();
        UpdateHeader();
        ShowFileTypes();
        if (_inspected >= 0) ShowNode(_inspected);
    }

    /// <summary>
    /// Schreibt einen Teilbaum als CSV. Der meistgenannte Wunsch bei WinDirStat, den es dort
    /// bis heute nicht gibt: Wer aufraeumt, will das Ergebnis weiterreichen oder ueber
    /// Zeitraeume vergleichen.
    /// </summary>
    private async Task ExportCsv(int node)
    {
        if (_store is null || node < 0) return;
        NodeStore store = _store;

        IStorageProvider? storage = GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;

        string suggestion = (node == 0
            ? Path.GetFileName(store.RootPath.TrimEnd(Path.DirectorySeparatorChar))
            : store.GetName(node));

        if (suggestion.Length == 0) suggestion = "diskstats";

        try
        {
            IStorageFile? file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Loc.T("Menu_Export"),
                SuggestedFileName = $"{suggestion}-{DateTime.Now:yyyy-MM-dd}.csv",
                DefaultExtension = "csv",
                FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }],
            });

            string? path = file?.TryGetLocalPath();
            if (path is null) return;

            var labels = new CsvLabels(
                [Loc.T("Csv_Path"), Loc.T("Csv_Name"), Loc.T("Csv_Type"),
                 Loc.T("Csv_Bytes"), Loc.T("Csv_Modified")],
                Loc.T("Csv_Folder"), Loc.T("Csv_File"));

            await Task.Run(() => CsvExport.WriteFile(store, node, path, labels: labels));
            StatusText.Text = Loc.T("Stat_Exported", path);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(Loc.T("Csv_Failed"), ex);
            StatusText.Text = Loc.T("Stat_ExportFailed", ex.Message);
        }
    }

    // ---------- Kontextmenue ----------

    /// <summary>
    /// Rechtsklick auf der Karte. Ohne das fuehrt jede Aktion quer durch das Fenster zum
    /// Inspector — bei einer Karte, auf der man ohnehin schon zeigt, ein unnoetiger Weg.
    /// </summary>
    private void BuildContextMenu()
    {
        // Jeder Eintrag wirkt auf _menuNode, nicht auf den Knoten unter dem Zeiger: Sobald der
        // Zeiger zum Menue wandert, verlaesst er die Karte, und die haelt dann nichts mehr.
        var open = new MenuItem { Header = Loc.T("Menu_Open") };
        open.Click += (_, _) => OnNodeOpened(_menuNode);

        var reveal = new MenuItem { Header = Loc.T("Menu_Reveal") };
        reveal.Click += (_, _) => RevealNode(_menuNode);

        var copy = new MenuItem { Header = Loc.T("Menu_CopyPath") };
        copy.Click += (_, _) => Guard(CopyPathOf(_menuNode), "Pfad kopieren");

        var stage = new MenuItem { Header = Loc.T("Menu_Stage") };
        stage.Click += (_, _) => Stage(_menuNode);

        var export = new MenuItem { Header = Loc.T("Menu_Export") };
        export.Click += (_, _) => Guard(ExportCsv(_menuNode), "CSV-Export");

        var refresh = new MenuItem { Header = Loc.T("Refresh_Folder") };
        refresh.Click += (_, _) => Guard(RefreshFolderAsync(_menuNode), "Ordner aktualisieren");
        var exclude = new MenuItem { Header = Loc.T("Filter_ExcludeSelected") };
        exclude.Click += (_, _) =>
        {
            if (!IsLive(_menuNode) || _menuNode == 0 || _scanRunning || _cleanupRunning || _refreshRunning) return;
            string relative = Path.GetRelativePath(_store.RootPath, _store.PathOf(_menuNode)).Replace('\\', '/');
            // Kein Eintrag doppelt: derselbe Ausschluss zweimal kostet nur Zeit beim Abgleich.
            AppSettings.Apply(s =>
            {
                if (!s.ExcludedPaths.Contains(relative, StringComparer.OrdinalIgnoreCase))
                    s.ExcludedPaths = [.. s.ExcludedPaths, relative];
            });
            Guard(RunScanAsync(_scanRoot), "Ausschluss");
        };
        var afterOpen = new Separator();

        var menu = new ContextMenu
        {
            ItemsSource = new object[]
            {
                open, afterOpen, reveal, copy, export, refresh, exclude, new Separator(), stage,
            },
        };

        menu.Opening += (_, e) =>
        {
            int node = _menuNode = Chart.HoveredNode;
            if (!IsLive(node)) { e.Cancel = true; return; }

            // Der Rechtsklick waehlt zugleich aus: sonst wirkt das Menue auf etwas anderes,
            // als der Inspector gerade zeigt.
            OnNodeSelected(node);

            // Ausblenden statt ausgrauen: In eine Datei fuehrt kein Weg hinein, und ein toter
            // Eintrag ganz oben liest sich als kaputtes Menue — genau so wurde er gemeldet.
            // Ein Menue soll aufzaehlen, was geht, nicht was hier nie ginge.
            bool canOpen = _store.IsDirectory(node) && _store.ChildCount[node] > 0;
            open.IsVisible = canOpen;
            afterOpen.IsVisible = canOpen;
        };

        Chart.ContextMenu = menu;
    }

    // ---------- Fenster ----------

    private void RestoreWindow()
    {
        AppSettings settings = AppSettings.Current;
        if (settings.WindowWidth < 400 || settings.WindowHeight < 300) return;

        Width = settings.WindowWidth;
        Height = settings.WindowHeight;

        // Nur wiederherstellen, wenn die Stelle noch auf einem Bildschirm liegt — ein
        // abgestecktes zweites Display wuerde das Fenster sonst unerreichbar machen.
        var target = new Avalonia.PixelPoint((int)settings.WindowX, (int)settings.WindowY);
        if (Screens.All.Any(screen => screen.Bounds.Contains(target)))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = target;
        }

        if (settings.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void RememberWindow() => AppSettings.Apply(settings =>
    {
        settings.WindowMaximized = WindowState == WindowState.Maximized;

        // Im maximierten Zustand ist die Groesse die des Bildschirms — gemerkt wird die,
        // auf die das Fenster beim Wiederherstellen zurueckfaellt.
        if (settings.WindowMaximized) return;

        settings.WindowWidth = Width;
        settings.WindowHeight = Height;
        settings.WindowX = Position.X;
        settings.WindowY = Position.Y;
    });

    // ---------- Laufwerke ----------

    private void LoadDrives()
    {
        var drives = new List<DriveEntry>();
        int index = 0;

        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady) continue;

            try
            {
                long total = drive.TotalSize;
                long free = drive.AvailableFreeSpace;
                double used = total > 0 ? (total - free) / (double)total : 0;
                string name = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    ? drive.Name.TrimEnd('\\')
                    : $"{drive.Name.TrimEnd('\\')}  {drive.VolumeLabel}";

                StorageKind kind = VolumeProbe.KindOf(drive.RootDirectory.FullName);

                drives.Add(new DriveEntry(
                    name, drive.RootDirectory.FullName, Loc.T("Side_FreeOf", Sizes.Format(free)),
                    Math.Round(used * DriveBarWidth),
                    new SolidColorBrush(Palette.ForFolderBranch(index++, 0, Dark)),
                    DriveBadge.IconFor(kind), DriveBadge.NameFor(kind)));
            }
            catch (IOException) { /* Laufwerk zwischenzeitlich verschwunden */ }
            catch (UnauthorizedAccessException) { }
        }

        DriveList.ItemsSource = drives;
    }

    private void OnDriveClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path }) StartScan(path);
    }

    // ---------- Aktionen ----------

    private void OnRevealClicked(object? sender, RoutedEventArgs e) => RevealNode(_inspected);

    private void RevealNode(int node)
    {
        if (_store is null || node < 0) return;

        string path = _store.PathOf(node);
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Explorer für {path} nicht geöffnet", ex);
            StatusText.Text = Loc.T("Stat_ExplorerFailed", ex.Message);
        }
    }

    private void OnCopyPathClicked(object? sender, RoutedEventArgs e)
        => Guard(CopyPathOf(_inspected), "Pfad kopieren");

    private async Task CopyPathOf(int node)
    {
        if (_store is null || node < 0) return;

        IClipboard? clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;

        await clipboard.SetTextAsync(_store.PathOf(node));
        StatusText.Text = Loc.T("Stat_PathCopied");
    }

}
