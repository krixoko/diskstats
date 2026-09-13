using Avalonia.Controls;
using Avalonia.Platform.Storage;
using DiskStats.App.Localization;
using DiskStats.Core.Cleanup;

namespace DiskStats.App;

public partial class MainWindow
{
    private bool _previewOpen;

    internal async Task PreviewAsync()
    {
        int node = _selected >= 0 ? _selected : _inspected;
        if (_previewOpen || !IsLive(node) || _store.IsDirectory(node) || _scanRunning || _cleanupRunning) return;
        _previewOpen = true;
        try { await new PreviewWindow(_store.PathOf(node)).ShowDialog(this); }
        finally { _previewOpen = false; }
    }

    private async Task PickMoveDestination()
    {
        if (_scanRunning || _cleanupRunning || _refreshRunning || _benchmarkOpen || _selection.Count == 0) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions {
            Title = Loc.T("Move_Title"), AllowMultiple = false
        });
        string? destination = folders.FirstOrDefault()?.TryGetLocalPath();
        if (destination is not null) await MoveSelectionAsync(destination);
    }

    internal async Task MoveSelectionAsync(string destination,
        Func<FileMove.Plan, Task<bool>>? confirm = null,
        Func<FileMove.Plan, Task<RecycleBin.Result>>? move = null)
    {
        if (_store is null || _scanRunning || _cleanupRunning || _refreshRunning || _benchmarkOpen) return;
        int[] nodes = _selection.Where(n => n > 0 && IsLive(n) && !HasSelectedAncestor(n)).ToArray();
        if (nodes.Length == 0) return;
        string[] paths = nodes.Select(_store.PathOf).ToArray();
        string root = _scanRoot;
        _cleanupRunning = true;
        MoveButton.IsEnabled = CleanupRun.IsEnabled = BrowseButton.IsEnabled = false;
        bool attempted = false;
        string resultText = string.Empty;
        try
        {
            StatusText.Text = Loc.T("Move_Checking");
            FileMove.Plan plan = await Task.Run(() => FileMove.Prepare(paths, destination));
            bool yes = confirm is not null ? await confirm(plan)
                : (await Confirm.Ask(this, Loc.T("Move_Title"),
                    Loc.T("Move_Confirm", plan.Sources.Length, Sizes.Format(plan.Bytes), destination, Sizes.Format(plan.FreeBytes)),
                    Loc.T("Move_Action"), null, false, plan.Sources)).Yes;
            if (!yes) { resultText = Loc.T("Move_Cancelled"); return; }
            await _snapshotWork;
            // Recheck paths and capacity after the user has reviewed the operation.
            plan = await Task.Run(() => FileMove.Prepare(paths, destination));
            _dupSearch?.Cancel();
            StatusText.Text = Loc.T("Move_Running");
            nint owner = TryGetPlatformHandle()?.Handle ?? 0;
            attempted = true;
            RecycleBin.Result result = await (move?.Invoke(plan) ?? Task.Run(() => FileMove.Send(plan, owner)));
            resultText = Loc.T(result.Ok ? "Move_Done" : result.Aborted ? "Move_Partial" : "Move_Failed", result.Code);
        }
        catch (Exception ex) { resultText = Loc.T("Move_Error", ex.Message); }
        finally
        {
            _cleanupRunning = false;
            MoveButton.IsEnabled = _selection.Count > 0;
            CleanupRun.IsEnabled = BrowseButton.IsEnabled = true;
            if (attempted && !_closed) await RunScanAsync(root);
            if (!_closed) { StatusText.Text = resultText; LoadDrives(); DrainChanges(); }
        }
    }
}
