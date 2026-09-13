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

/// <summary>Tastatur, Pfadleiste und Wurzelwechsel.</summary>
public partial class MainWindow
{
    // ---------- Tastatur ----------

    /// <summary>
    /// Ein Werkzeug, das man wiederholt benutzt, braucht die Tastatur. Die Pfeile bilden den
    /// Baum ab: links und rechts unter Geschwistern, hoch zum Elternteil, runter in den
    /// groessten Inhalt. Eingabe geht hinein, Ruecktaste wieder heraus.
    /// </summary>
    private void OnWindowKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        // Waehrend des Tippens im Suchfeld gehoeren die Tasten dorthin.
        if (SearchBox.IsFocused && e.Key != Key.Escape) return;

        switch (e.Key)
        {
            case Key.Escape:
                if (SearchPanel.IsVisible) CloseSearch();
                else if (_selected >= 0) OnNodeSelected(-1);
                else return;
                break;

            case Key.Back:
                if (_history.Count == 0) return;
                OnUpClicked(this, new RoutedEventArgs());
                break;

            case Key.Enter:
                if (_selected < 0) return;
                OnNodeOpened(_selected);
                break;

            case Key.Delete:
                if (_selected < 0) return;
                Stage(_selected);
                break;

            case Key.F5:
                if (_scanRoot.Length == 0 || _scanRunning) return;
                StartScan(_scanRoot);
                break;

            case Key.Home:
                if (_history.Count == 0) return;
                _history.Clear();
                _currentRoot = 0;
                UpButton.IsVisible = false;
                ClearSelection();
                SetViewStore(_store!, 0);
                UpdateHeader();
                ShowNode(0);
                break;

            case Key.Left: MoveSibling(-1); break;
            case Key.Right: MoveSibling(1); break;
            case Key.Up: MoveToParent(); break;
            case Key.Down: MoveToLargestChild(); break;

            default: return;
        }

        e.Handled = true;
    }

    /// <summary>Reihenfolge der Geschwister nach Groesse — dieselbe, die auch die Karte zeigt.</summary>
    private int[] SiblingsOf(int node)
    {
        if (_store is null) return [];
        NodeStore store = _store;

        int parent = node >= 0 ? store.ParentIndex[node] : _currentRoot;
        if (parent < 0) parent = _currentRoot;

        int start = store.ChildStart[parent];
        int count = store.ChildCount[parent];
        if (count == 0) return [];

        int[] order = Enumerable.Range(start, count).Where(i => !store.IsRemoved(i)).ToArray();
        Array.Sort(order, (a, b) => store.Size[b].CompareTo(store.Size[a]));
        return order;
    }

    private void MoveSibling(int step)
    {
        if (_store is null) return;

        int[] order = SiblingsOf(_selected);
        if (order.Length == 0) return;

        // Ohne Auswahl beginnt man beim groessten Eintrag — dort schaut man ohnehin zuerst hin.
        if (_selected < 0) { OnNodeSelected(order[0]); return; }

        int position = Array.IndexOf(order, _selected);
        if (position < 0) { OnNodeSelected(order[0]); return; }

        OnNodeSelected(order[Math.Clamp(position + step, 0, order.Length - 1)]);
    }

    private void MoveToParent()
    {
        if (_store is null || _selected < 0) return;

        int parent = _store.ParentIndex[_selected];
        if (parent == _currentRoot || parent < 0) OnNodeSelected(-1);
        else OnNodeSelected(parent);
    }

    private void MoveToLargestChild()
    {
        if (_store is null) return;
        NodeStore store = _store;

        int from = _selected >= 0 ? _selected : _currentRoot;
        if (!store.IsDirectory(from) || store.ChildCount[from] == 0) return;

        int start = store.ChildStart[from];
        int best = -1;
        for (int i = start; i < start + store.ChildCount[from]; i++)
            if (!store.IsRemoved(i) && (best < 0 || store.Size[i] > store.Size[best])) best = i;

        if (best >= 0) OnNodeSelected(best);
    }

    // ---------- Navigation ----------

    /// <summary>
    /// Wechselt die Wurzel der Karte. Fuer Tests erreichbar: Hier stuerzte die Anwendung ab,
    /// als das Kontextmenue mit -1 aufrief.
    /// </summary>
    internal void OnNodeOpened(int node)
    {
        if (!IsLive(node) || _scanRunning || _cleanupRunning || !_store.IsDirectory(node)) return;
        if (_store.ChildCount[node] == 0) return;

        _history.Push(_currentRoot);
        _currentRoot = node;
        ClearSelection();
        UpButton.IsVisible = true;
        SetViewStore(_store, _currentRoot);
        UpdateHeader();
        ShowNode(node);
        AnnounceSelection();
    }

    private void OnUpClicked(object? sender, RoutedEventArgs e)
    {
        if (_history.Count == 0) return;

        _currentRoot = _history.Pop();
        ClearSelection();
        UpButton.IsVisible = _history.Count > 0;
        SetViewStore(_store, _currentRoot);
        UpdateHeader();
        ShowNode(_currentRoot);
    }

    private void ClearSelection()
    {
        _selected = -1;
        Chart.SetSelected(-1);
    }

    /// <summary>
    /// Der Weg von der Scan-Wurzel bis hierher, jede Station anklickbar. Ohne ihn sieht man
    /// nur den aktuellen Ordnernamen und muss sich Ebene fuer Ebene zurueckklicken.
    /// </summary>
    private void BuildBreadcrumb()
    {
        Breadcrumb.Children.Clear();
        if (_store is null) return;
        NodeStore store = _store;

        var chain = new List<int>();
        for (int node = _currentRoot; node > 0; node = store.ParentIndex[node]) chain.Insert(0, node);

        string rootLabel = Path.GetFileName(store.RootPath.TrimEnd(Path.DirectorySeparatorChar));
        if (rootLabel.Length == 0) rootLabel = store.RootPath;

        AddCrumb(rootLabel, 0, chain.Count == 0);

        IBrush divider = new SolidColorBrush(Dark
            ? Color.FromRgb(0x6B, 0x64, 0x5A) : Color.FromRgb(0xA9, 0xA1, 0x96));

        for (int i = 0; i < chain.Count; i++)
        {
            Breadcrumb.Children.Add(new TextBlock
            {
                Text = "›",
                FontSize = 11.5,
                Foreground = divider,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });

            AddCrumb(store.GetName(chain[i]), chain[i], i == chain.Count - 1);
        }
    }

    private void AddCrumb(string label, int node, bool last)
    {
        var button = new Button
        {
            Content = label,
            Tag = node,
            FontSize = 11.5,
            Padding = new Avalonia.Thickness(6, 2),
        };

        button.Classes.Add("crumb");
        if (last) button.Classes.Add("here");
        button.Click += (_, _) => JumpTo((int)button.Tag!);

        Breadcrumb.Children.Add(button);
    }

    /// <summary>Springt direkt auf eine Station des Weges, statt mehrfach zurueckzugehen.</summary>
    private void JumpTo(int node)
    {
        if (_store is null || node == _currentRoot) return;

        // Alles unterhalb der Zielstation faellt aus dem Verlauf.
        while (_history.Count > 0 && _history.Peek() != node) _history.Pop();
        if (_history.Count > 0) _history.Pop();

        _currentRoot = node;
        ClearSelection();
        UpButton.IsVisible = _history.Count > 0;
        SetViewStore(_store, _currentRoot);
        UpdateHeader();
        ShowNode(node);
        AnnounceSelection();
    }

    /// <summary>Der Farbverlauf der Altersfaerbung, in Baendern nachgezeichnet.</summary>
    private void BuildAgeRamp()
    {
        AgeRampBands.Children.Clear();
        long now = DateTime.UtcNow.Ticks;
        const int bands = 26;

        for (int i = 0; i < bands; i++)
        {
            double years = i / (double)(bands - 1) * 5;
            long moment = now - (long)(years * 365.25 * TimeSpan.TicksPerDay);

            AgeRampBands.Children.Add(new Border
            {
                Width = 130.0 / bands,
                Background = new SolidColorBrush(Palette.ForAge(moment, now, Dark)),
            });
        }
    }

    private void UpdateHeader()
    {
        if (_store is null) return;
        NodeStore store = _store;

        BuildBreadcrumb();

        string leaf = Path.GetFileName(store.RootPath.TrimEnd('\\'));
        TitleText.Text = _currentRoot == 0
            ? leaf.Length > 0 ? leaf : store.RootPath
            : store.GetName(_currentRoot);

        // Vorberechnet im Aggregator: ein Zaehllauf ueber den Teilbaum bei jeder Navigation
        // und jedem Zwischenstand waere bei zwei Millionen Knoten spuerbar.
        int files = store.FileCount.Length == store.Count ? store.FileCount[_currentRoot] : 0;
        int folders = store.FolderCount.Length == store.Count ? store.FolderCount[_currentRoot] : 0;

        StorageStatsText.Text = StorageText(store, _currentRoot);
        StatsText.Text =
            Loc.T("Head_Counts", Sizes.Format(store.Size[_currentRoot]),
                files.ToString("N0"), folders.ToString("N0"));
    }
}
