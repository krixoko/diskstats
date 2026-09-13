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

/// <summary>Suche nach Namen.</summary>
public partial class MainWindow
{
    // ---------- Suche ----------

    private DispatcherTimer? _searchDelay;

    /// <summary>
    /// Suche mit kurzer Verzoegerung. Ein Durchlauf ueber zwei Millionen Namen dauert einige
    /// Zehntelsekunden — bei jedem Anschlag zu suchen wuerde die Eingabe zaeh machen.
    /// </summary>
    private void BuildSearch()
    {
        _searchDelay = new DispatcherTimer(TimeSpan.FromMilliseconds(180), DispatcherPriority.Background,
            (_, _) => { _searchDelay!.Stop(); RunSearch(); });

        SearchBox.TextChanged += (_, _) => { _searchDelay.Stop(); _searchDelay.Start(); };
        SearchClose.Click += (_, _) => CloseSearch();

        SearchBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            CloseSearch();
            e.Handled = true;
        };
    }

    private void RunSearch() => Guard(RunSearchAsync(), "Suche");

    private CancellationTokenSource? _search;

    private async Task RunSearchAsync()
    {
        string term = SearchBox.Text?.Trim() ?? string.Empty;
        _search?.Cancel();

        if (_store is null || term.Length < 2)
        {
            SearchPanel.IsVisible = false;
            return;
        }

        NodeStore store = _store;
        using var search = new CancellationTokenSource();
        _search = search;
        CancellationToken token = search.Token;

        // Der Baumlauf gehoert nicht auf den UI-Thread: Bei zwei Millionen Knoten dauert er
        // Zehntelsekunden, und der Nutzer tippt weiter.
        List<SearchRow> rows;
        int count;
        try
        {
            (rows, count) = await Task.Run(() =>
            {
                IReadOnlyList<NodeSearch.Hit> hits = NodeSearch.Find(store, term);
                token.ThrowIfCancellationRequested();
                bool dark = Dark;
                var list = hits.Select(hit => new SearchRow(
                    hit.Node,
                    store.GetName(hit.Node),
                    Ort(store, hit.Node),
                    Sizes.Format(hit.Size),
                    new SolidColorBrush(Palette.ForCategory(NodeColoring.CategoryOf(store, hit.Node), dark))))
                    .ToList();
                return (list, hits.Count);
            }, token);
        }
        catch (OperationCanceledException) { return; }
        finally
        {
            if (ReferenceEquals(_search, search)) _search = null;
        }

        // Waehrend der Suche kann ein neuer Scan gelaufen oder weitergetippt worden sein.
        if (token.IsCancellationRequested || !ReferenceEquals(store, _store)) return;

        SearchResults.ItemsSource = rows;
        SearchSummary.Text = count switch
        {
            0 => Loc.T("Find_None", term),
            1 => Loc.T("Find_One", term),
            _ => Loc.T("Find_Many", count, term),
        };

        SearchPanel.IsVisible = true;
    }

    /// <summary>Der Ordner, in dem ein Treffer liegt — ohne ihn sagt ein Dateiname wenig.</summary>
    private static string Ort(NodeStore store, int node)
    {
        int parent = store.ParentIndex[node];
        return parent <= 0 ? store.RootPath : store.PathOf(parent);
    }

    private void CloseSearch()
    {
        SearchBox.Text = string.Empty;
        SearchPanel.IsVisible = false;
        Chart.Focus();
    }

    /// <summary>Ein Treffer fuehrt dorthin, wo er liegt, und wird dort ausgewaehlt.</summary>
    private void OnSearchHitClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int node } || _scanRunning || _cleanupRunning || !IsLive(node)) return;
        NodeStore store = _store;

        int parent = store.ParentIndex[node];
        int target = parent >= 0 ? parent : 0;

        if (target != _currentRoot)
        {
            _history.Clear();
            for (int walk = store.ParentIndex[target]; walk >= 0; walk = store.ParentIndex[walk])
                _history.Push(walk);

            _currentRoot = target;
            UpButton.IsVisible = _history.Count > 0;
            SetViewStore(store, _currentRoot);
            UpdateHeader();
        }

        SearchPanel.IsVisible = false;
        OnNodeSelected(node);
    }

    /// <summary>
    /// Setzt eine Ablehnung in Worte. Der Kern nennt nur den Grund und den Namen, der ihn
    /// ausgeloest hat; welcher Satz daraus wird, entscheidet die eingestellte Sprache.
    /// </summary>
    /// <summary>
    /// Zeigt eine Knotennummer aus der Oberflaeche noch auf diesen Baum?
    ///
    /// Zeilen und Kacheln tragen ihre Nummer als Tag. Wird der Baum ersetzt, waehrend so eine
    /// Zeile noch steht, meint dieselbe Nummer etwas anderes oder gar nichts — und ein Zugriff
    /// daneben beendet die Anwendung, statt nur das Falsche zu zeigen.
    /// </summary>
    [MemberNotNullWhen(true, nameof(_store))]
    private bool IsLive(int node) => _store is not null && node >= 0 && node < _store.Count && !_store.IsRemoved(node);

    /// <summary>
    /// Wieviel auf der Liste steht. Bei genau einem Eintrag eine eigene Fassung: "1 entries"
    /// in einer Rueckfrage, nach der Dateien verschwinden, liest sich wie ein Fehler — und wer
    /// dem Werkzeug an dieser Stelle nicht glaubt, klickt zu Recht auf Abbrechen.
    /// </summary>
    private static string CleanupSummary(IReadOnlyList<CleanupList.Item> items)
        => Loc.T(items.Count == 1 ? "Clean_SummaryOne" : "Clean_Summary",
            items.Count, Sizes.Format(items.Sum(i => i.Size)));

    private static string Say(Refusal refusal)
        => refusal.Name is null
            ? Loc.T("Deny_" + refusal.Kind)
            : Loc.T("Deny_" + refusal.Kind, refusal.Name);
}
