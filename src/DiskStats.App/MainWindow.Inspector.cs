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

/// <summary>Inspektor und Vorlesewerkzeuge.</summary>
public partial class MainWindow
{
    // ---------- Inspector ----------

    /// <summary>
    /// Der Hover zeigt nur eine Vorschau, solange nichts ausgewaehlt ist. Sonst wuerde jede
    /// Mausbewegung die Auswahl im Inspector ueberschreiben — man koennte eine Datei anklicken,
    /// sie aber nie in Ruhe ansehen. Was unter dem Zeiger liegt, sagt das Schild auf der Karte.
    /// </summary>
    private void OnNodeHovered(int node)
    {
        HoverPath.Text = IsLive(node) ? _store.PathOf(node) : string.Empty;
        ToolTip.SetTip(HoverPath, HoverPath.Text);
        if (_selected >= 0) return;
        if (node >= 0) ShowNode(node);
    }

    private void OnNodeSelected(int node)
    {
        if (node >= 0 && !IsLive(node)) return;
        SelectNodes(node >= 0 ? [node] : []);
    }

    private readonly HashSet<int> _selection = [];

    internal void ToggleNode(int node)
    {
        if (!IsLive(node)) return;
        if (!_selection.Remove(node)) _selection.Add(node);
        SelectNodes(_selection.ToArray());
    }

    internal void SelectNodes(IReadOnlyList<int> nodes)
    {
        _selection.Clear();
        _selection.UnionWith(nodes.Where(IsLive));
        _selected = _selection.LastOrDefault(-1);
        Chart.SetSelectedNodes(_selection);
        ShowNode(_selected >= 0 ? _selected : _currentRoot);
        LargestCard.IsVisible = _selection.Count <= 1;
        SelectionTree.Items.Clear();
        SelectionTree.IsVisible = _selection.Count > 0;
        if (_store is not null && _selection.Count > 0)
        {
            var branches = new Dictionary<int, TreeViewItem>();
            TreeViewItem AddBranch(int node)
            {
                if (branches.TryGetValue(node, out var existing)) return existing;
                var label = new TextBlock {
                    Text = _store.DisplayName(node), FontSize = 11.5,
                    FontWeight = _selection.Contains(node) ? FontWeight.Bold : FontWeight.Normal
                };
                ToolTip.SetTip(label, _store.PathOf(node));
                label.PointerEntered += (_, _) => OnNodeHovered(node);
                label.PointerExited += (_, _) => OnNodeHovered(-1);
                label.PointerPressed += (_, e) => {
                    if (!e.GetCurrentPoint(label).Properties.IsLeftButtonPressed) return;
                    if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) ToggleNode(node);
                    else OnNodeSelected(node);
                    e.Handled = true;
                };
                var item = new TreeViewItem { Header = label, IsExpanded = true };
                branches[node] = item;
                int parent = _store.ParentIndex[node];
                if (parent >= 0) AddBranch(parent).Items.Add(item);
                else SelectionTree.Items.Add(item);
                return item;
            }
            foreach (int node in _selection) AddBranch(node);
            if (_selection.Count > 1)
            {
                InspectorKind.Text = Loc.T("Insp_Selection");
                InspectorName.Text = Loc.T("Insp_Entries", _selection.Count);
                InspectorPath.Text = string.Empty;
                InspectorSize.Text = Sizes.Format(_selection.Where(n => !HasSelectedAncestor(n)).Sum(n => _store.Size[n]));
                InspectorStorage.Text = InspectorShare.Text = string.Empty;
                DetailRows.ItemsSource = null;
                TopChildren.ItemsSource = null;
                LargestCount.Text = string.Empty;
                NoChildrenHint.IsVisible = false;
                RevealButton.IsEnabled = false;
                StageButton.IsEnabled = !_scanRunning && _selection.Any(n => n > 0);
            }
        }
        MoveButton.IsEnabled = !_scanRunning && !_cleanupRunning && _selection.Any(n => n > 0);
        AnnounceSelection();
    }

    private bool HasSelectedAncestor(int node)
    {
        for (int p = _store!.ParentIndex[node]; p >= 0; p = _store.ParentIndex[p])
            if (_selection.Contains(p)) return true;
        return false;
    }

    private void StageSelection()
    {
        foreach (int node in (_selection.Count > 0 ? _selection.ToArray() : new[] { _inspected })) Stage(node);
    }

    private void ShowNode(int node)
    {
        if (!IsLive(node)) return;
        NodeStore store = _store;
        _inspected = node;

        bool isDirectory = store.IsDirectory(node);
        PreviewButton.IsEnabled = !isDirectory && !_scanRunning;
        TrendsButton.IsEnabled = !_scanRunning;
        InspectorKind.Text = Loc.T(isDirectory ? "Insp_Folder" : "Insp_File");

        string leaf = Path.GetFileName(store.RootPath.TrimEnd('\\'));
        InspectorName.Text = node == 0
            ? leaf.Length > 0 ? leaf : store.RootPath
            : store.GetName(node);

        InspectorPath.Text = store.PathOf(node);
        InspectorSize.Text = Sizes.Format(store.Size[node]);
        InspectorStorage.Text = StorageText(store, node);
        if (store.LinksOf(node) > 1) InspectorStorage.Text += " " + Loc.T("Storage_Links", store.LinksOf(node));

        long total = store.Size[0];
        InspectorShare.Text = total > 0
            ? Loc.T("Insp_ShareOfScan",
                (store.Size[node] / (double)total * 100).ToString("F1") + " %")
            : string.Empty;

        var details = new List<DetailRow>();
        int parent = store.ParentIndex[node];
        if (parent >= 0 && store.Size[parent] > 0)
            details.Add(new DetailRow(Loc.T("Insp_ShareOfFolder"),
                $"{store.Size[node] / (double)store.Size[parent] * 100:F1} %"));

        if (isDirectory)
            details.Add(new DetailRow(Loc.T("Insp_DirectEntries"), $"{Enumerable.Range(store.ChildStart[node], store.ChildCount[node]).Count(i => !store.IsRemoved(i)):N0}"));

        if (store.MTime[node] > 0)
            details.Add(new DetailRow(Loc.T("Insp_Modified"),
                new DateTime(store.MTime[node], DateTimeKind.Utc).ToLocalTime()
                    .ToString(Loc.Current == Language.German ? "dd.MM.yyyy" : "yyyy-MM-dd")));

        details.Add(new DetailRow(Loc.T("Insp_Category"),
            FileCategories.DisplayName(NodeColoring.CategoryOf(store, node))));

        // Der Anteil am Scan sagt wenig, wenn der Scan nur ein Unterordner war. Der Anteil
        // am Laufwerk sagt, worum es beim Aufraeumen wirklich geht.
        if (_driveTotal > 0)
            details.Add(new DetailRow(Loc.T("Insp_ShareOfDrive", _driveLabel),
                $"{store.Size[node] / (double)_driveTotal * 100:F1} %"));

        DetailRows.ItemsSource = details;

        RevealButton.IsEnabled = true;
        CopyPathButton.IsEnabled = true;
        StageButton.IsEnabled = node > 0 && !_scanRunning;

        ShowTopChildren(store, node);
    }

    private void ShowTopChildren(NodeStore store, int node)
    {
        int[] children = Enumerable.Range(store.ChildStart[node], store.ChildCount[node])
            .Where(i => !store.IsRemoved(i)).ToArray();
        int count = children.Length;
        LargestCount.Text = count > 0 ? Loc.T("Insp_Entries", count.ToString("N0")) : string.Empty;

        if (count == 0)
        {
            TopChildren.ItemsSource = null;
            NoChildrenHint.IsVisible = true;
            return;
        }

        NoChildrenHint.IsVisible = false;
        var top = children
            .OrderByDescending(i => store.Size[i])
            .Take(9)
            .ToList();

        long largest = Math.Max(1, store.Size[top[0]]);

        TopChildren.ItemsSource = top.Select(i => new ChildEntry(
            store.GetName(i),
            Sizes.Format(store.Size[i]),
            Math.Round(store.Size[i] / (double)largest * ChildBarWidth),
            new SolidColorBrush(Palette.ForCategory(NodeColoring.CategoryOf(store, i), Dark)))).ToList();
    }

    private void ClearInspector()
    {
        ClearSelection();
        PreviewButton.IsEnabled = MoveButton.IsEnabled = TrendsButton.IsEnabled = false;
        InspectorKind.Text = Loc.T("Insp_Selection");
        InspectorName.Text = Loc.T("Stat_ScanningShort");
        InspectorPath.Text = string.Empty;
        InspectorStorage.Text = string.Empty;
        InspectorSize.Text = Loc.T("Common_Empty");
        InspectorShare.Text = string.Empty;
        DetailRows.ItemsSource = null;
        TopChildren.ItemsSource = null;
        NoChildrenHint.IsVisible = false;
        LargestCount.Text = string.Empty;
        RevealButton.IsEnabled = false;
        CopyPathButton.IsEnabled = false;
        StageButton.IsEnabled = false;
    }

    // ---------- Vorlesewerkzeuge ----------

    /// <summary>
    /// Sagt der Karte an, worauf die Auswahl steht.
    ///
    /// Die Karte zeichnet alles selbst: Es gibt kein Element je Kachel, das ein
    /// Vorlesewerkzeug finden koennte, und ohne diesen Namen bliebe sie ein leeres Rechteck.
    /// Weil die Pfeiltasten die Auswahl bewegen, ist der Name der Karte zugleich das, was
    /// beim Bewegen vorgelesen wird — die Ansage folgt der Bedienung.
    /// </summary>
    private void AnnounceSelection()
    {
        if (_store is null)
        {
            AutomationProperties.SetName(Chart, Loc.T("A11y_MapEmpty"));
            return;
        }

        NodeStore store = _store;
        if (_selection.Count > 1)
        {
            AutomationProperties.SetName(Chart, Loc.T("Insp_Entries", _selection.Count) + ", " + InspectorSize.Text);
            return;
        }

        if (_selected < 0 || _selected >= store.Count)
        {
            AutomationProperties.SetName(Chart, Loc.T("A11y_Map"));
            return;
        }

        long total = Math.Max(1, store.Size[_currentRoot]);

        AutomationProperties.SetName(Chart, Loc.T("A11y_Selected",
            store.DisplayName(_selected),
            Loc.T(store.IsDirectory(_selected) ? "Insp_Folder" : "Insp_File"),
            Sizes.Format(store.Size[_selected]),
            (store.Size[_selected] / (double)total).ToString("P1")));
    }
}
