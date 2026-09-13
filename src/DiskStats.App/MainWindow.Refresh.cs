using Avalonia.Threading;
using DiskStats.App.Localization;
using DiskStats.Core.Diagnostics;
using DiskStats.Core.Scanning;
using DiskStats.Core.Storage;

namespace DiskStats.App;

public partial class MainWindow
{
    private FileChangeMonitor? _watcher;
    private readonly HashSet<string> _pendingRefresh = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _refresh;
    private bool _refreshRunning;
    private bool _closed;

    private void StartWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
        _pendingRefresh.Clear();
        if (_closed || WatchToggle.IsChecked != true || _scanRoot.Length == 0) return;
        try
        {
            var watcher = new FileChangeMonitor(_scanRoot, [SnapshotStore.Folder, AppSettings.Folder, DiagnosticLog.Folder],
                AppSettings.Current.ExcludedPaths);
            _watcher = watcher;
            watcher.Changed += (folders, overflow) => Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(watcher, _watcher) || _closed) return;
                foreach (string folder in folders) _pendingRefresh.Add(folder);
                if (overflow) StatusText.Text = Loc.T("Watch_Overflow");
                DrainChanges();
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            WatchToggle.IsChecked = false;
            StatusText.Text = Loc.T("Watch_Failed", ex.Message);
        }
    }

    private void DrainChanges() => Guard(DrainChangesAsync(), "Aenderungen nachziehen");

    private async Task DrainChangesAsync()
    {
        if (_closed || _benchmarkOpen || _scanRunning || _cleanupRunning || _refreshRunning || _store is null || _pendingRefresh.Count == 0) return;
        // Refresh the shallowest queued directory first; it covers any deeper notifications.
        string path = _pendingRefresh.OrderBy(p => p.Length).First();
        _pendingRefresh.RemoveWhere(p => p.Equals(path, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        int node = SubtreeRefresh.Find(_store, path);
        while (node < 0 && Path.GetDirectoryName(path) is { } parent)
        {
            path = parent;
            node = SubtreeRefresh.Find(_store, path);
        }
        if (node >= 0) await RefreshFolderAsync(node);
        else if (_pendingRefresh.Count > 0) Dispatcher.UIThread.Post(DrainChanges);
    }

    /// <summary>Der laufende Refresh, damit ein Scanstart auf sein Aufraeumen warten kann.</summary>
    private Task _refreshTask = Task.CompletedTask;

    internal Task RefreshFolderAsync(int node)
    {
        if (_closed || _benchmarkOpen || _scanRunning || _cleanupRunning || _refreshRunning || !IsLive(node)) return Task.CompletedTask;
        return _refreshTask = RefreshFolderCore(node);
    }

    private async Task RefreshFolderCore(int node)
    {
        if (_store is not { } original) return;
        string viewed = original.PathOf(_currentRoot);
        using var refresh = new CancellationTokenSource();
        _refresh = refresh;
        _refreshRunning = true;
        _dupSearch?.Cancel();
        FilesTable.IsEnabled = false;
        CleanupRun.IsEnabled = false;
        RefreshFolderButton.IsEnabled = false;
        Badge.Busy = true;
        StatusText.Text = Loc.T("Refresh_Running", original.PathOf(node));
        try
        {
            WalkOptions options = new() { WorkerCount = _workers, ExcludedPaths = AppSettings.Current.ExcludedPaths };
            SubtreeRefresh.Result result = await Task.Run(() => SubtreeRefresh.Refresh(original, node, options, refresh.Token), refresh.Token);
            if (_closed || refresh.IsCancellationRequested || !ReferenceEquals(original, _store)) return;
            ResetNavigation();
            _store = result.Store;
            _cleanup.RefreshSizes(_store);
            foreach (var item in _cleanup.Items.ToArray())
                if (IsMissing(item.Path)) _cleanup.Remove(item.Path);
            int restored = SubtreeRefresh.Find(_store, viewed);
            _currentRoot = restored >= 0 ? restored : 0;
            var parents = new Stack<int>();
            for (int parent = _store.ParentIndex[_currentRoot]; parent >= 0; parent = _store.ParentIndex[parent]) parents.Push(parent);
            foreach (int parent in parents) _history.Push(parent);
            UpButton.IsVisible = _history.Count > 0;
            SetViewStore(_store, _currentRoot);
            UpdateHeader();
            ShowFileTypes();
            ShowQuickWins();
            OfferDuplicates();
            ShowCleanup();
            ShowNode(_currentRoot);
            StatusText.Text = Loc.T("Refresh_Done", result.ScannedPath);
            if (result.Errors > 0) StatusText.Text += " " + Loc.T("Stat_Unreadable", result.Errors);
            NodeStore finished = _store;
            _snapshotWork = _snapshots.Enqueue(() => SaveAndCompare(finished));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Folder refresh failed", ex);
            if (ReferenceEquals(_refresh, refresh)) StatusText.Text = Loc.T("Refresh_Failed", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_refresh, refresh))
            {
                _refresh = null;
                _refreshRunning = false;
                FilesTable.IsEnabled = true;
                CleanupRun.IsEnabled = true;
                RefreshFolderButton.IsEnabled = true;
                Badge.Busy = false;
                if (_pendingRefresh.Count > 0) Dispatcher.UIThread.Post(DrainChanges);
            }
        }
    }
}
