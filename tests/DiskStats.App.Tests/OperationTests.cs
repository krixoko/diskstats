using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using DiskStats.App.Localization;
using DiskStats.Core.Cleanup;
using DiskStats.Core.Diagnostics;
using DiskStats.Core.Scanning;
using DiskStats.Core.Storage;

namespace DiskStats.App.Tests;

public sealed class OperationTests : IDisposable
{
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), "diskstats-operations-" + Guid.NewGuid());
    private readonly string _settings = AppSettings.Folder;
    private readonly string _snapshots = SnapshotStore.Folder;
    private readonly string _logs = DiagnosticLog.Folder;
    private readonly List<MainWindow> _windows = [];
    private string Tree => Path.Combine(_sandbox, "tree");
    private string Other => Path.Combine(_sandbox, "other");

    public OperationTests()
    {
        Directory.CreateDirectory(Tree);
        Directory.CreateDirectory(Other);
        File.WriteAllBytes(Path.Combine(Tree, "a.txt"), new byte[100]);
        File.WriteAllBytes(Path.Combine(Tree, "b.txt"), new byte[200]);
        File.WriteAllBytes(Path.Combine(Other, "other.txt"), new byte[700]);
        AppSettings.Folder = Path.Combine(_sandbox, "settings");
        SnapshotStore.Folder = Path.Combine(_sandbox, "snapshots");
        DiagnosticLog.Folder = Path.Combine(_sandbox, "logs");
        AppSettings.Current.ElevationPrompt = false;
        AppSettings.Current.AutoElevate = false;
        AppSettings.Current.LastPath = string.Empty;
        Loc.Current = Language.English;
    }

    public void Dispose()
    {
        foreach (MainWindow window in _windows) window.Close();
        AppSettings.Folder = _settings;
        SnapshotStore.Folder = _snapshots;
        DiagnosticLog.Folder = _logs;
        Directory.Delete(_sandbox, recursive: true);
    }

    private MainWindow NewWindow()
    {
        var window = new MainWindow([]);
        _windows.Add(window);
        return window;
    }

    private async Task<MainWindow> Scan()
    {
        MainWindow window = NewWindow();
        await window.RunScanAsync(Tree);
        await SnapshotWork(window);
        Assert.NotNull(window.Store);
        return window;
    }

    [AvaloniaFact]
    public async Task Multiple_selection_toggles_and_stages_every_selected_file()
    {
        MainWindow window = await Scan();
        int a = Node(window, "a.txt"), b = Node(window, "b.txt");
        window.SelectNodes([a]);
        window.ToggleNode(b);
        Assert.Equal("2 entries", window.InspectorName.Text);
        Assert.Equal(Sizes.Format(300), window.InspectorSize.Text);
        Assert.True(window.SelectionTree.IsVisible);
        Assert.Single(window.SelectionTree.Items);
        window.ToggleNode(a);
        Assert.Equal("b.txt", window.InspectorName.Text);
        window.ToggleNode(a);
        Click(window.StageButton);
        Assert.Equal(2, window.StagedCount);
        bool recycled = false;
        await window.RunCleanup(_ => Task.FromResult(false), _ => {
            recycled = true;
            return Task.FromResult(new RecycleBin.Result(true, false, 0));
        });
        Assert.False(recycled);
        Assert.True(File.Exists(Path.Combine(Tree, "a.txt")));
        window.SelectNodes([]);
        Assert.False(window.SelectionTree.IsVisible);
    }

    [AvaloniaFact]
    public async Task Selected_parent_and_child_are_not_double_counted_and_scan_clears_tree()
    {
        MainWindow window = await Scan();
        window.SelectNodes([0, Node(window, "a.txt")]);
        Assert.Equal(Sizes.Format(300), window.InspectorSize.Text);
        await window.RunScanAsync(Other);
        await SnapshotWork(window);
        Assert.False(window.SelectionTree.IsVisible);
        Assert.Empty(window.SelectionTree.Items);
    }

    [AvaloniaFact]
    public async Task Cleanup_dialog_lists_all_paths_and_cancel_preserves_files()
    {
        MainWindow window = await Scan();
        window.Show();
        window.Stage(Node(window, "a.txt"));
        window.Stage(Node(window, "b.txt"));
        Task cleanup = window.RunCleanup();
        Window dialog = Assert.Single(window.OwnedWindows);
        try
        {
            var paths = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(dialog)
                .OfType<SelectableTextBlock>().Single();
            Assert.Contains(Path.Combine(Tree, "a.txt"), paths.Text);
            Assert.Contains(Path.Combine(Tree, "b.txt"), paths.Text);
            Button cancel = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(dialog)
                .OfType<Button>().Single(b => b.IsCancel);
            Click(cancel);
            await cleanup;
            Assert.Equal(2, window.StagedCount);
            Assert.True(File.Exists(Path.Combine(Tree, "a.txt")));
            Assert.True(File.Exists(Path.Combine(Tree, "b.txt")));
        }
        finally { dialog.Close(false); }
    }

    [AvaloniaFact]
    public async Task Move_cancellation_never_calls_shell_and_confirmed_move_refreshes_scan()
    {
        MainWindow window = await Scan();
        window.SelectNodes([Node(window, "a.txt"), Node(window, "b.txt")]);
        bool called = false;
        await window.MoveSelectionAsync(Other, _ => Task.FromResult(false), _ => {
            called = true;
            return Task.FromResult(new RecycleBin.Result(true, false, 0));
        });
        Assert.False(called);
        Assert.True(File.Exists(Path.Combine(Tree, "a.txt")));
        await window.MoveSelectionAsync(Other, _ => Task.FromResult(true), plan => {
            foreach (string source in plan.Sources) File.Move(source, Path.Combine(plan.Destination, Path.GetFileName(source)));
            return Task.FromResult(new RecycleBin.Result(true, false, 0));
        });
        await SnapshotWork(window);
        Assert.True(File.Exists(Path.Combine(Other, "a.txt")));
        Assert.False(File.Exists(Path.Combine(Tree, "a.txt")));
        Assert.Equal(0, window.Store!.Size[0]);
    }

    [AvaloniaFact]
    public async Task Saved_filter_applies_query_without_changing_scan_exclusions()
    {
        MainWindow window = await Scan();
        var exclusions = AppSettings.Current.ExcludedPaths;
        window.ApplySavedFilter(new SavedFilter("Only b", new FileQuery { Name = "b.txt" }));
        await window.FilesTable.Pending;
        Assert.Same(exclusions, AppSettings.Current.ExcludedPaths);
        Assert.True(window.FilesTable.IsVisible);
        Assert.Equal("Only b", window.StatusText.Text);
        var rows = window.FilesTable.FindControl<ListBox>("Rows")!;
        Assert.Single(rows.Items);
    }

    private static Task SnapshotWork(MainWindow window)
        => (Task)typeof(MainWindow).GetField("_snapshotWork", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;

    private static int Node(MainWindow window, string name)
        => Enumerable.Range(1, window.Store!.Count - 1).Single(i => window.Store.GetName(i) == name);

    private static T Control<T>(MainWindow window, string name) where T : Control
        => window.FindControl<T>(name)!;

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static ScanSession.Update Update(string root, bool final = true)
    {
        var buffer = new NodeBuffer();
        DirectoryWalker.Walk(root, buffer, new WalkOptions { WorkerCount = 1 });
        NodeStore store = NodeStoreBuilder.Build(buffer, root);
        SizeAggregator.Aggregate(store);
        return new ScanSession.Update(store, store.Count - 1, store.Size[0], TimeSpan.Zero, final, 0);
    }

    [AvaloniaFact]
    public async Task Bestaetigter_auftrag_bleibt_fest_und_blockiert_parallelstart()
    {
        MainWindow window = await Scan();
        window.Stage(Node(window, "a.txt"));
        var confirmation = new TaskCompletionSource<bool>();
        var shell = new TaskCompletionSource<RecycleBin.Result>();
        var started = new TaskCompletionSource();
        string? question = null;
        string[]? sent = null;
        Task cleanup = window.RunCleanup(q => { question = q; return confirmation.Task; }, paths =>
        {
            sent = paths.ToArray();
            started.SetResult();
            return shell.Task;
        });

        Assert.Contains("one entry", question);
        Click(Control<Button>(window, "CleanupClear"));
        window.Stage(Node(window, "b.txt"));
        confirmation.SetResult(true);
        await started.Task;
        Assert.Equal(new[] { Path.Combine(Tree, "a.txt") }, sent);

        bool invoked = false;
        await window.RunCleanup(_ => { invoked = true; return Task.FromResult(true); });
        await window.RunScanAsync(Other, (_, _, _, _) => { invoked = true; return Task.CompletedTask; });
        Assert.False(invoked);
        Assert.Equal(Tree, window.Store!.RootPath);

        File.Delete(Path.Combine(Tree, "a.txt"));
        shell.SetResult(new RecycleBin.Result(true, false, 0));
        await cleanup;
        Assert.Equal(1, window.StagedCount); // b was not part of the confirmed operation
        Assert.Equal(200, window.Store.Size[0]);
        Assert.Empty(NodeSearch.Find(window.Store, "a.txt"));
        Assert.Single(NodeSearch.Find(window.Store, "b.txt"));
        Assert.True(Control<Button>(window, "CleanupRun").IsEnabled);
    }

    [AvaloniaFact]
    public async Task Abbruch_der_rueckfrage_loescht_nichts_und_gibt_aktionen_frei()
    {
        MainWindow window = await Scan();
        window.Stage(Node(window, "a.txt"));
        bool sent = false;
        await window.RunCleanup(_ => Task.FromResult(false), _ =>
        {
            sent = true;
            return Task.FromResult(new RecycleBin.Result(true, false, 0));
        });
        Assert.False(sent);
        Assert.True(File.Exists(Path.Combine(Tree, "a.txt")));
        Assert.Equal(1, window.StagedCount);
        await window.RunScanAsync(Other);
        await SnapshotWork(window);
        Assert.Equal(Other, window.Store!.RootPath);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Waehrend_des_loeschens_vorgemerkte_verwandte_pfade_werden_abgeglichen(bool wholeFolder)
    {
        string folder = Path.Combine(Tree, "folder");
        string first = Path.Combine(folder, "first.bin");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(first, new byte[900]);
        File.WriteAllBytes(Path.Combine(folder, "second.bin"), new byte[400]);
        MainWindow window = await Scan();
        window.Stage(Node(window, wholeFolder ? "folder" : "first.bin"));
        await window.RunCleanup(_ => Task.FromResult(true), _ =>
        {
            Click(Control<Button>(window, "CleanupClear"));
            window.Stage(Node(window, wholeFolder ? "first.bin" : "folder"));
            if (wholeFolder) Directory.Delete(folder, recursive: true);
            else File.Delete(first);
            return Task.FromResult(new RecycleBin.Result(true, false, 0));
        });
        Assert.Equal(wholeFolder ? 0 : 1, window.StagedCount);
        Assert.Equal(Sizes.Format(wholeFolder ? 0 : 400), Control<TextBlock>(window, "CleanupTotal").Text);
        Assert.Equal(wholeFolder ? 300 : 700, window.Store!.Size[0]);
    }

    [AvaloniaFact]
    public async Task Shell_erfolg_allein_entfernt_keine_noch_vorhandenen_dateien_aus_der_ansicht()
    {
        MainWindow window = await Scan();
        window.Stage(Node(window, "a.txt"));
        await window.RunCleanup(_ => Task.FromResult(true),
            _ => Task.FromResult(new RecycleBin.Result(true, false, 0)));
        Assert.Equal(1, window.StagedCount);
        Assert.Equal(300, window.Store!.Size[0]);
        Assert.Single(NodeSearch.Find(window.Store, "a.txt"));
        Assert.Contains("Some paths still exist", Control<TextBlock>(window, "StatusText").Text);
    }

    [AvaloniaFact]
    public async Task Quick_win_oeffnet_fundstellen_statt_ungeprueft_vorzumerken()
    {
        // "Temp" ist ein Fund zum Ansehen — und gross genug fuer die Mindestgroesse der Quick Wins.
        Directory.CreateDirectory(Path.Combine(Tree, "Temp"));
        File.WriteAllBytes(Path.Combine(Tree, "Temp", "unsaved-document.txt"), new byte[1_100_000]);
        MainWindow window = await Scan();
        Assert.True(Control<Control>(window, "QuickWinsBlock").IsVisible);
        typeof(MainWindow).GetMethod("OnQuickWinClicked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [new Button { Tag = 0 }, new RoutedEventArgs()]);
        Assert.Equal(0, window.StagedCount);
        Assert.True(Control<Control>(window, "SearchPanel").IsVisible);
        Assert.NotEmpty(Control<ItemsControl>(window, "SearchResults").ItemsSource!);
        window.Stage(Node(window, "Temp"));
        Assert.Equal(1, window.StagedCount); // individual, deliberate staging is still available
    }

    [AvaloniaFact]
    public async Task Teilerfolg_entfernt_nur_verschwundene_auftraege()
    {
        MainWindow window = await Scan();
        window.Stage(Node(window, "a.txt"));
        window.Stage(Node(window, "b.txt"));
        await window.RunCleanup(_ => Task.FromResult(true), _ =>
        {
            File.Delete(Path.Combine(Tree, "a.txt"));
            return Task.FromResult(new RecycleBin.Result(false, true, 0));
        });
        Assert.Equal(1, window.StagedCount);
        Assert.Equal(200, window.Store!.Size[0]);
        Assert.Empty(NodeSearch.Find(window.Store, "a.txt"));
        Assert.Contains("Not everything moved", Control<TextBlock>(window, "StatusText").Text);
        Assert.Equal(Sizes.Format(200), Control<TextBlock>(window, "CleanupTotal").Text);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Teilweise_entleerter_ordner_wird_auch_bei_shell_ausnahme_neu_eingelesen(bool throws)
    {
        string folder = Path.Combine(Tree, "folder");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "gone.bin"), new byte[900]);
        File.WriteAllBytes(Path.Combine(folder, "left.bin"), new byte[400]);
        MainWindow window = await Scan();
        window.Stage(Node(window, "folder"));
        await window.RunCleanup(_ => Task.FromResult(true), _ =>
        {
            File.Delete(Path.Combine(folder, "gone.bin"));
            if (throws) throw new IOException("simulated Shell failure after partial progress");
            return Task.FromResult(new RecycleBin.Result(false, true, 0));
        });
        Assert.Equal(1, window.StagedCount);
        Assert.Equal(700, window.Store!.Size[0]);
        Assert.Equal(700, ExtensionStats.Of(window.Store, 0).Sum(x => x.Bytes));
        Assert.Empty(NodeSearch.Find(window.Store, "gone.bin"));
        Assert.Equal(Sizes.Format(400), Control<TextBlock>(window, "CleanupTotal").Text);
        Assert.True(Control<Button>(window, "CleanupRun").IsEnabled);
    }

    [AvaloniaFact]
    public async Task Ordner_entfernen_aktualisiert_auch_seitenleiste_und_navigation()
    {
        string folder = Path.Combine(Tree, "bin");
        Directory.CreateDirectory(Path.Combine(folder, "deep"));
        File.WriteAllBytes(Path.Combine(folder, "deep", "gone.txt"), new byte[900]);
        MainWindow window = await Scan();
        int removed = Node(window, "bin");
        window.OnNodeOpened(removed);
        window.Stage(removed);
        await window.RunCleanup(_ => Task.FromResult(true), _ =>
        {
            Directory.Delete(folder, recursive: true);
            return Task.FromResult(new RecycleBin.Result(true, false, 0));
        });
        Assert.Equal(300, window.Store!.Size[0]);
        Assert.Empty(NodeSearch.Find(window.Store, "gone"));
        Assert.False(Control<Control>(window, "QuickWinsBlock").IsVisible);
        Assert.False(Control<Button>(window, "UpButton").IsVisible);
        Assert.Equal(Loc.T("Head_Counts", Sizes.Format(300), "2", "0"),
            Control<TextBlock>(window, "StatsText").Text);
        window.Stage(removed);
        Assert.Equal(0, window.StagedCount);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Alte_scanmeldungen_und_alter_abschluss_ueberschreiben_neuen_scan_nicht(bool fails)
    {
        MainWindow window = NewWindow();
        var endA = new TaskCompletionSource();
        var endB = new TaskCompletionSource();
        Action<ScanSession.Update>? updateA = null, updateB = null;
        CancellationToken tokenA = default;
        Task a = window.RunScanAsync(Tree, (_, _, update, token) =>
        {
            updateA = update;
            tokenA = token;
            return endA.Task;
        });
        updateA!(Update(Tree, final: false));
        Task b = window.RunScanAsync(Other, (_, _, update, _) =>
        {
            updateB = update;
            return endB.Task;
        });
        Assert.True(tokenA.IsCancellationRequested);
        Assert.Null(window.Store);
        updateA(Update(Tree));
        Assert.Null(window.Store);
        string? pendingStatus = Control<TextBlock>(window, "StatusText").Text;
        endA.SetException(fails ? new IOException("old scan failed") : new OperationCanceledException(tokenA));
        await a;
        Assert.True(window.ScanRunning);
        Assert.True(Control<Button>(window, "CancelButton").IsVisible);
        Assert.Equal(pendingStatus, Control<TextBlock>(window, "StatusText").Text);

        updateB!(Update(Other));
        endB.SetResult();
        await b;
        await SnapshotWork(window);
        string? status = Control<TextBlock>(window, "StatusText").Text;
        updateA(Update(Tree));
        Assert.Equal(Other, window.Store!.RootPath);
        Assert.Equal(700, window.Store.Size[0]);
        Assert.Equal(status, Control<TextBlock>(window, "StatusText").Text);
        Assert.False(window.ScanRunning);
        Assert.Empty(SnapshotStore.History(Tree));
    }

    [AvaloniaFact]
    public async Task Abgebrochener_scan_veroeffentlicht_keinen_spaeten_endstand()
    {
        MainWindow window = NewWindow();
        var finish = new TaskCompletionSource();
        Action<ScanSession.Update>? publish = null;
        Task scan = window.RunScanAsync(Tree, (_, _, update, _) =>
        {
            publish = update;
            return finish.Task;
        });
        Click(Control<Button>(window, "CancelButton"));
        publish!(Update(Tree));
        finish.SetCanceled();
        await scan;
        Assert.Null(window.Store);
        Assert.False(window.ScanRunning);
        Assert.Empty(SnapshotStore.History(Tree));
    }

    [AvaloniaFact]
    public async Task Ordneraktualisierung_erhaelt_navigation_und_laesst_andere_teilbaeume_unberuehrt()
    {
        string folder = Path.Combine(Tree, "sub");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "inside.bin"), new byte[100]);
        MainWindow window = await Scan();
        int node = Node(window, "sub");
        window.OnNodeOpened(node);
        File.WriteAllBytes(Path.Combine(Tree, "a.txt"), new byte[9999]);
        File.WriteAllBytes(Path.Combine(folder, "inside.bin"), new byte[500]);
        await window.RefreshFolderAsync(node);
        await SnapshotWork(window);
        Assert.Equal(800, window.Store!.Size[0]);
        Assert.Equal(folder, Control<TextBlock>(window, "InspectorPath").Text);
        Assert.True(Control<Button>(window, "UpButton").IsVisible);
    }

    [AvaloniaFact]
    public async Task Live_ueberwachung_aktualisiert_geaenderten_ordner_und_laesst_sich_abschalten()
    {
        string folder = Path.Combine(Tree, "sub");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "inside.bin"), new byte[100]);
        MainWindow window = await Scan();
        File.WriteAllBytes(Path.Combine(Tree, "a.txt"), new byte[9999]); // before monitoring starts
        Control<CheckBox>(window, "WatchToggle").IsChecked = true;
        File.WriteAllBytes(Path.Combine(folder, "inside.bin"), new byte[500]);
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (window.Store!.Size[0] != 800 && DateTime.UtcNow < deadline)
            await Task.Delay(40, TestContext.Current.CancellationToken);
        Assert.Equal(800, window.Store.Size[0]);
        Control<CheckBox>(window, "WatchToggle").IsChecked = false;
        await SnapshotWork(window);
        File.WriteAllBytes(Path.Combine(folder, "inside.bin"), new byte[900]);
        await Task.Delay(800, TestContext.Current.CancellationToken);
        Assert.Equal(800, window.Store.Size[0]);
    }
}
