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

/// <summary>Veraenderungen, Duplikate und Quick Wins.</summary>
public partial class MainWindow
{
    // ---------- Veraenderungen ----------

    /// <summary>
    /// Legt den Stand ab und vergleicht ihn mit dem vorigen.
    ///
    /// Beides gehoert zusammen und beides in den Hintergrund: Dreissig Megabyte zu schreiben
    /// und ebensoviel zurueckzulesen darf die Oberflaeche nicht anhalten, und ob es klappt,
    /// aendert am gezeigten Ergebnis nichts.
    /// </summary>
    private async Task SaveAndCompare(NodeStore finished)
    {
        try
        {
            if (!SnapshotStore.Save(finished))
            {
                DiagnosticLog.Write($"Snapshot fuer {finished.RootPath} nicht gespeichert");
                return;
            }

            // Der eben abgelegte Stand steht vorn; verglichen wird mit dem davor.
            IReadOnlyList<SnapshotStore.Entry> history = SnapshotStore.History(finished.RootPath);
            if (history.Count < 2) return;

            SnapshotStore.Entry earlier = history[1];
            NodeStore? before = SnapshotStore.Load(earlier.Path);
            if (before is null) return;

            IReadOnlyList<Change> changes = SnapshotDiff.Compare(before, finished);
            long total = SnapshotDiff.TotalDelta(before, finished);

            await Dispatcher.UIThread.InvokeAsync(
                () => ShowChanges(finished, earlier.Written, total, changes));
        }
        catch (Exception ex) { DiagnosticLog.Write("Vergleich fehlgeschlagen", ex); }
    }

    private void ShowChanges(
        NodeStore compared, DateTime since, long total, IReadOnlyList<Change> changes)
    {
        // Waehrend des Vergleichs kann ein neuer Scan gelaufen sein.
        if (!ReferenceEquals(compared, _store)) return;

        ChangesSince.Text = Loc.T("Chg_Since", since.ToString(
            Loc.Current == Language.German ? "dd.MM.yyyy HH:mm" : "yyyy-MM-dd HH:mm"));

        ChangesTotal.Text = Signed(total);
        ChangesBlock.IsVisible = true;

        if (changes.Count == 0)
        {
            ChangesList.ItemsSource = null;
            ChangesSince.Text += "  ·  " + Loc.T("Chg_Nothing");
            return;
        }

        ChangesList.ItemsSource = changes.Take(6).Select(change => new ChangeRow(
            change.Path,
            change.Name,
            Detail(change),
            Signed(change.Delta),
            new SolidColorBrush(change.Delta > 0
                ? Palette.ForCategory(FileCategory.Video, Dark)
                : Palette.ForCategory(FileCategory.Document, Dark)))).ToList();
    }

    /// <summary>Wo es liegt — oder dass es neu ist beziehungsweise fehlt.</summary>
    private static string Detail(Change change) => change.Kind switch
    {
        ChangeKind.Added => Loc.T("Chg_Added"),
        ChangeKind.Removed => Loc.T("Chg_Removed"),
        _ => Path.GetDirectoryName(change.Path) is { Length: > 0 } parent ? parent : ".",
    };

    /// <summary>Mit Vorzeichen: Ob etwas dazugekommen oder verschwunden ist, ist die Auskunft.</summary>
    private static string Signed(long delta)
        => (delta >= 0 ? "+" : "−") + Sizes.Format(Math.Abs(delta));

    /// <summary>
    /// Springt zu der Stelle. Der Pfad ist relativ zur Wurzel des Scans — aufgeloest wird er
    /// ueber die Namen, weil sich die Knotennummern zwischen zwei Scans verschieben.
    /// </summary>
    private void OnChangeClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path } || _store is null) return;

        int node = Resolve(_store, path);
        if (node < 0) return;

        SearchPanel.IsVisible = false;

        if (_store.IsDirectory(node)) OnNodeOpened(node);
        else OnNodeSelected(node);
    }

    /// <summary>Findet den Knoten zu einem Pfad unterhalb der Wurzel, oder -1.</summary>
    private static int Resolve(NodeStore store, string path)
    {
        int node = 0;

        foreach (string part in path.Split(Path.DirectorySeparatorChar))
        {
            int start = store.ChildStart[node];
            int found = -1;

            for (int i = start; i < start + store.ChildCount[node]; i++)
            {
                if (!string.Equals(store.GetName(i), part, StringComparison.OrdinalIgnoreCase)) continue;
                found = i;
                break;
            }

            if (found < 0) return -1;
            node = found;
        }

        return node;
    }

    // ---------- Duplikate ----------

    /// <summary>
    /// Bietet die Suche an, statt sie zu starten.
    ///
    /// Anders als alles andere in der Seitenleiste kostet sie Zeit: Der Scan kennt nur
    /// Groessen, hier wird zum ersten Mal in Dateien hineingelesen. Das ungefragt nach jedem
    /// Scan zu tun hiesse, jedem die Rechnung zu stellen, der nur die Karte sehen wollte.
    /// </summary>
    private void OfferDuplicates()
    {
        _duplicates = [];
        DuplicatesList.ItemsSource = null;
        DuplicatesTotal.Text = string.Empty;
        DuplicatesLabel.Text = Loc.T("Dup_Search");
        DuplicatesButton.IsEnabled = true;
        DuplicatesBlock.IsVisible = _store is not null;
    }

    private void OnFindDuplicatesClicked(object? sender, RoutedEventArgs e) => Guard(FindDuplicatesAsync(), "Duplikatsuche");

    private async Task FindDuplicatesAsync()
    {
        if (_store is null || _benchmarkOpen || _scanRunning || _cleanupRunning) return;
        NodeStore store = _store;

        _dupSearch?.Cancel();
        _dupSearch = new CancellationTokenSource();
        _duplicateRuns++;
        CancellationToken token = _dupSearch.Token;

        DuplicatesButton.IsEnabled = false;
        DuplicatesList.ItemsSource = null;
        DuplicatesTotal.Text = string.Empty;

        var progress = new Progress<DuplicateProgress>(step =>
        {
            if (!token.IsCancellationRequested && ReferenceEquals(store, _store))
                DuplicatesLabel.Text = Loc.T("Dup_Running", step.Done, step.Total);
        });

        try
        {
            IReadOnlyList<DuplicateGroup> found = await Task.Run(
                () => DuplicateFinder.Find(store, 0, DuplicateFinder.DefaultMinSize, progress, token),
                token);

            // Waehrend der Suche kann ein neuer Scan gelaufen sein; dann gehoeren die Nummern
            // zu einem anderen Baum und duerfen nicht angezeigt werden.
            if (token.IsCancellationRequested || !ReferenceEquals(store, _store)) return;

            ShowDuplicates(found);
        }
        catch (OperationCanceledException) { /* A scan or cleanup has superseded this search. */ }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Duplikatsuche fehlgeschlagen", ex);
            StatusText.Text = Loc.T("Dup_Failed", ex.Message);
        }
        finally
        {
            _duplicateRuns--;
            if (!token.IsCancellationRequested && !_cleanupRunning && !_scanRunning) DuplicatesButton.IsEnabled = true;
        }
    }

    /// <summary>Zeigt die Funde, die groesste Ersparnis zuerst.</summary>
    private void ShowDuplicates(IReadOnlyList<DuplicateGroup> found)
    {
        if (_store is null) return;
        NodeStore store = _store;

        _duplicates = found;

        if (found.Count == 0)
        {
            DuplicatesLabel.Text = Loc.T("Dup_None", Sizes.Format(DuplicateFinder.DefaultMinSize));
            return;
        }

        DuplicatesLabel.Text = Loc.T("Dup_Search");
        DuplicatesTotal.Text = Sizes.Format(DuplicateFinder.WastedOf(found));

        // Nur die groessten: Wer zweihundert Zeilen durchscrollt, raeumt nicht auf.
        DuplicatesList.ItemsSource = found.Take(8).Select((group, index) => new DuplicateRow(
            index,
            store.GetName(group.Nodes[0]),
            Loc.T("Dup_Copies", group.Count, Sizes.Format(group.Size)),
            Sizes.Format(group.WastedBytes),
            new SolidColorBrush(Palette.ForCategory(
                NodeColoring.CategoryOf(store, group.Nodes[0]), Dark)))).ToList();
    }

    /// <summary>
    /// Zeigt die Fundstellen einer Gruppe. Bewusst nur zeigen: Welche Fassung bleibt, haengt
    /// davon ab, wo sie liegt — das weiss der Nutzer, nicht das Werkzeug.
    /// </summary>
    private void OnDuplicateClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int index } || _store is null) return;
        if (index < 0 || index >= _duplicates.Count) return;

        DuplicateGroup group = _duplicates[index];
        foreach (int node in group.Nodes)
            if (!IsLive(node)) return;

        NodeStore store = _store;

        SearchResults.ItemsSource = group.Nodes
            .Select(node => new SearchRow(
                node,
                store.GetName(node),
                Ort(store, node),
                Sizes.Format(store.Size[node]),
                new SolidColorBrush(Palette.ForCategory(NodeColoring.CategoryOf(store, node), Dark))))
            .ToList();

        SearchSummary.Text = Loc.T("Dup_GroupHead", group.Count, Sizes.Format(group.Size));
        SearchPanel.IsVisible = true;
    }

    // ---------- Quick Wins ----------

    /// <summary>
    /// Die bekannten Platzfresser, von selbst gefunden. Eine Treemap zeigt, *wo* der Platz
    /// liegt, und ueberlaesst einem das Urteil; diese Liste kennt die Faelle, in denen die
    /// Antwort schon feststeht.
    /// </summary>
    private void ShowQuickWins()
    {
        if (_store is null) { QuickWinsBlock.IsVisible = false; return; }
        NodeStore store = _store;

        _wins = QuickWins.Find(store, 0);
        if (_wins.Count == 0) { QuickWinsBlock.IsVisible = false; return; }

        long reclaimable = QuickWins.ReclaimableOf(_wins);
        QuickWinsTotal.Text = reclaimable > 0 ? Sizes.Format(reclaimable) : string.Empty;

        QuickWinsList.ItemsSource = _wins.Select((win, index) => new QuickWinRow(
            index,
            Loc.T("Win_" + win.Rule.Key),
            Loc.T(win.Rule.Risk == WinRisk.Regrows ? "Win_Regrows" : "Win_LookFirst", win.Count),
            Loc.T("Win_" + win.Rule.Key + "Hint"),
            Sizes.Format(win.Bytes),
            new SolidColorBrush(win.Rule.Risk == WinRisk.Regrows
                ? Palette.ForCategory(FileCategory.Document, Dark)
                : Palette.ForCategory(FileCategory.Video, Dark)))).ToList();

        QuickWinsBlock.IsVisible = true;
    }

    /// <summary>
    /// Was nachwaechst, wird gesammelt vorgemerkt — die Aufraeumliste ist die Kontrolle davor,
    /// geloescht wird erst auf Knopfdruck. Was eigene Dateien enthaelt, wird nur gezeigt: Ein
    /// Klick, der den Downloads-Ordner vormerkt, waere ein Klick zu wenig vor dem Verlust.
    /// </summary>
    private void OnQuickWinClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int index } || _store is null) return;
        if (index < 0 || index >= _wins.Count) return;

        QuickWins.Result win = _wins[index];
        foreach (int node in win.Nodes)
            if (!IsLive(node)) return;

        if (win.Rule.Risk == WinRisk.LookFirst)
        {
            ShowGroup(win);
            return;
        }

        int added = 0;
        foreach (int node in win.Nodes)
            if (_cleanup.Add(_store, node) is null) added++;

        ShowCleanup();
        StatusText.Text = added == win.Count
            ? Loc.T("Win_Staged", added)
            : Loc.T("Win_StagedSome", added, win.Count);
    }

    /// <summary>Zeigt die Fundstellen einer Gruppe, ohne etwas vorzumerken.</summary>
    private void ShowGroup(QuickWins.Result win)
    {
        if (_store is null) return;
        NodeStore store = _store;

        SearchResults.ItemsSource = win.Nodes
            .OrderByDescending(node => store.Size[node])
            .Select(node => new SearchRow(
                node,
                store.GetName(node),
                Ort(store, node),
                Sizes.Format(store.Size[node]),
                new SolidColorBrush(Palette.ForCategory(NodeColoring.CategoryOf(store, node), Dark))))
            .ToList();

        SearchSummary.Text = Loc.T("Win_GroupHead",
            Loc.T("Win_" + win.Rule.Key).ToUpperInvariant(),
            Loc.T("Win_" + win.Rule.Key + "Hint").ToUpperInvariant());
        SearchPanel.IsVisible = true;
    }
}
