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

/// <summary>Aufraeumliste und Loeschlauf.</summary>
public partial class MainWindow
{
    // ---------- Aufraeumen ----------

    /// <summary>
    /// Nimmt einen Eintrag auf die Liste. Abgelehnt wird mit Begruendung in der Statusleiste —
    /// ein Knopf, der wortlos nichts tut, waere schlimmer als gar keiner.
    /// </summary>
    internal void Stage(int node)
    {
        if (_scanRunning || !IsLive(node)) return;

        Refusal? refused = _cleanup.Add(_store, node);
        StatusText.Text = refused is null
            ? Loc.T("Clean_Staged", _store.GetName(node))
            : Loc.T("Clean_NotPossible", Say(refused));

        ShowCleanup();
    }

    private void OnUnstageClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path }) return;

        _cleanup.Remove(path);
        ShowCleanup();
    }

    private void ShowCleanup()
    {
        CleanupCard.IsVisible = _cleanup.Count > 0;
        CleanupTotal.Text = Sizes.Format(_cleanup.TotalBytes);

        CleanupItems.ItemsSource = _cleanup.Items
            .Select(i => new CleanupRow(i.Path, i.Name, Sizes.Format(i.Size)))
            .ToList();
    }

    /// <summary>
    /// Liest nach einem teilweise gescheiterten Loeschlauf nur die Ordner neu, in denen etwas
    /// haengen blieb. Zwei Eintraege im selben Ordner kosten einen Durchlauf; ein Eintrag in
    /// einem bereits neu gelesenen Ast keinen weiteren.
    /// </summary>
    private SubtreeRefresh.Result RefreshAfterPartialCleanup(NodeStore store, IReadOnlyList<string> stuck)
    {
        var options = new WalkOptions { WorkerCount = _workers, ExcludedPaths = AppSettings.Current.ExcludedPaths };
        NodeStore current = store;
        int errors = 0;
        var done = new List<string>();

        foreach (string path in stuck.OrderBy(p => p.Length))
        {
            string folder = Path.GetDirectoryName(path) ?? path;
            if (done.Any(d => folder.Equals(d, StringComparison.OrdinalIgnoreCase)
                    || folder.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                continue;

            int node = SubtreeRefresh.Find(current, folder);
            if (node < 0) continue;

            SubtreeRefresh.Result step = SubtreeRefresh.Refresh(current, node, options);
            current = step.Store;
            errors += step.Errors;
            done.Add(folder);
        }

        return new SubtreeRefresh.Result(current, errors, store.RootPath);
    }

    /// <summary>
    /// Loescht das Vorgemerkte — in den Papierkorb, damit ein Irrtum umkehrbar bleibt.
    /// Die Rueckfrage nennt Anzahl und Menge, weil beides zusammen die Tragweite ausmacht.
    /// Die Delegaten lassen Tests Rueckfrage und Shell ersetzen, ohne echte Dateien zu loeschen.
    /// </summary>
    internal async Task RunCleanup(
        Func<string, Task<bool>>? confirm = null,
        Func<IReadOnlyList<string>, Task<RecycleBin.Result>>? recycle = null)
    {
        if (_store is null || _benchmarkOpen || _cleanup.Count == 0 || _scanRunning || _cleanupRunning || _refreshRunning) return;

        NodeStore store = _store;
        CleanupList.Item[] items = _cleanup.Items.ToArray();
        string question = Loc.T("Clean_Confirm", CleanupSummary(items).TrimEnd());
        _cleanupRunning = true;
        CleanupRun.IsEnabled = false;
        BrowseButton.IsEnabled = false;
        DuplicatesButton.IsEnabled = false;
        try
        {
            bool yes = confirm is not null ? await confirm(question)
                : (await Confirm.Ask(this, Loc.T("Clean_Delete"), question, Loc.T("Clean_Move"),
                    null, focusConfirm: false, paths: items.Select(i => i.Path).ToArray())).Yes;
            if (!yes) return;

            // Snapshot serialization must finish before this same store is mutated.
            await _snapshotWork;
            _dupSearch?.Cancel();
            string[] paths = items.Select(i => i.Path).ToArray();
            foreach (string path in paths)
            {
                // Zweite Pruefung unmittelbar vor dem Loeschen: Die Liste kann aus einem
                // aelteren Baum stammen, und was ausserhalb der Scanwurzel liegt, wurde nie gezeigt.
                Refusal? refusal = CleanupList.IsUnderRoot(store.RootPath, path)
                    ? ProtectedPaths.Reason(path) : new Refusal(RefusalKind.InvalidPath);
                if (refusal is null) continue;
                StatusText.Text = Loc.T("Clean_NotPossible", Say(refusal));
                return;
            }

            RecycleBin.Result result;
            try
            {
                // Mit Besitzerfenster: Die Warnung der Shell soll vor diesem Fenster stehen, nicht dahinter.
                nint owner = TryGetPlatformHandle()?.Handle ?? 0;
                result = await (recycle?.Invoke(paths) ?? Task.Run(() => RecycleBin.Send(paths, owner)));
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("Recycle Bin failed", ex);
                result = new RecycleBin.Result(false, false, ex.HResult);
            }

            int gone = 0;
            long moved = 0;
            var removed = new List<int>(items.Length);
            foreach (CleanupList.Item item in items)
            {
                // Exists() also returns false on access errors. Only definite absence counts.
                if (!IsMissing(item.Path)) continue;
                int node = FindByPath(store, item.Path);
                if (node >= 0) removed.Add(node);
                _cleanup.Remove(item.Path);
                gone++;
                moved += item.Size;
            }
            // Einmal markieren, einmal ueber den Baum rechnen — und nicht auf dem UI-Thread.
            if (removed.Count > 0) await Task.Run(() => CleanupList.ApplyRemovals(store, removed));

            ResetNavigation();
            bool partial = !result.Ok || gone != items.Length;
            if (partial)
            {
                // A remaining folder may already have lost some children. Re-read what is left
                // instead of pretending that a failed batch changed nothing — but only the
                // affected branches: a full rescan of a whole drive for three stuck files
                // would freeze the window for minutes.
                StatusText.Text = Loc.T("Clean_Checking");
                Badge.Busy = true;
                string[] stuck = items.Where(i => !IsMissing(i.Path)).Select(i => i.Path).ToArray();
                var refreshed = await Task.Run(() => RefreshAfterPartialCleanup(store, stuck));
                _store = refreshed.Store;
                string reason = result.Aborted ? Loc.T("Common_Cancel")
                    : result.Ok ? Loc.T("Clean_Remaining") : Loc.T("Clean_ErrorCode", result.Code);
                DiagnosticLog.Write(Loc.T("Clean_BinError", reason, paths.Length));
                StatusText.Text = Loc.T("Clean_Partial", reason);
                if (refreshed.Errors > 0)
                    StatusText.Text += " " + Loc.T("Stat_Unreadable", refreshed.Errors);
            }
            else StatusText.Text = Loc.T("Clean_Moved", gone, Sizes.Format(moved));

            foreach (CleanupList.Item pending in _cleanup.Items.ToArray())
                if (IsMissing(pending.Path)) _cleanup.Remove(pending.Path);
            _cleanup.RefreshSizes(_store);
            ResetNavigation();
            SetViewStore(_store, 0);
            UpdateHeader();
            ShowFileTypes();
            ShowQuickWins();
            OfferDuplicates();
            ShowNode(0);
            ChangesBlock.IsVisible = false;
            ChangesList.ItemsSource = null;
            LoadDrives();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Clean-up refresh failed", ex);
            StatusText.Text = Loc.T("Clean_RefreshFailed", ex.Message);
        }
        finally
        {
            _cleanupRunning = false;
            Badge.Busy = false;
            CleanupRun.IsEnabled = !_scanRunning;
            BrowseButton.IsEnabled = true;
            DuplicatesButton.IsEnabled = !_scanRunning;
            ShowCleanup();
            DrainChanges();
        }
    }

    private static bool IsMissing(string path)
    {
        try { _ = File.GetAttributes(path); return false; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private void ResetNavigation()
    {
        _currentRoot = 0;
        _history.Clear();
        UpButton.IsVisible = false;
        _menuNode = -1;
        Chart.ContextMenu?.Close();
        CloseSearch();
        SearchResults.ItemsSource = null;
        ClearSelection();
        ClearInspector();
    }

    /// <summary>Sucht den Knoten zu einem Pfad — nach dem Loeschen muss der Baum nachziehen.</summary>
    private static int FindByPath(NodeStore store, string path)
    {
        int node = SubtreeRefresh.Find(store, path);
        return node > 0 && !store.IsRemoved(node) ? node : -1;
    }
}
