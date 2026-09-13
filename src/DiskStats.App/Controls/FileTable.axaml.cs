using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DiskStats.App.Localization;
using DiskStats.Core.Storage;

namespace DiskStats.App.Controls;

public enum FileSort { Name, Logical, Physical, Modified, Files, Links, Path }

public sealed record FileTableRow(NodeStore Store, int Node, int Files)
{
    public string Name => (Store.IsDirectory(Node) ? "▸ " : "") + Store.GetName(Node);
    public string Path => Store.PathOf(Node);
    public long Logical => Store.Size[Node];
    public long Physical => Store.IsDirectory(Node) ? Store.PhysicalSize[Node] : Store.AllocationOf(Node);
    public string LogicalText => Sizes.Format(Logical);
    public string PhysicalText => Physical < 0 ? Loc.T("Storage_Unknown") : Sizes.Format(Physical)
        + (Store.IsDirectory(Node) && Store.UnknownAllocation[Node] > 0 ? " + ?" : "");
    public long Modified => Store.MTime[Node];
    public string ModifiedText => Modified <= 0 ? Loc.T("Common_Empty") : new DateTime(Modified, DateTimeKind.Utc).ToLocalTime().ToString("g");
    public uint Links => Store.LinksOf(Node);
    public string LinksText => Store.IsDirectory(Node) ? Loc.T("Common_Empty") : Links == 0 ? "?" : Links.ToString();
}

public partial class FileTable : UserControl
{
    private NodeStore? _store;
    private int _root;
    private FileQuery _query = new();
    private CancellationTokenSource? _work;
    private FileSort _sort = FileSort.Logical;
    private bool _descending = true;
    public Task Pending { get; private set; } = Task.CompletedTask;
    public event Action<int>? NodeSelected;
    public event Action<IReadOnlyList<int>>? SelectionChanged;
    public event Action<int>? NodeHovered;
    public event Action<int>? NodeOpened;
    public event Action<IReadOnlyList<int>>? NodesStaged;

    public FileTable()
    {
        InitializeComponent();
        Rows.TemplateApplied += (_, _) =>
        {
            ScrollViewer? scroll = Rows.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (scroll is not null) scroll.ScrollChanged += (_, _) => HeaderScroll.Offset = new Avalonia.Vector(scroll.Offset.X, 0);
        };
        Recursive.IsCheckedChanged += (_, _) => Refresh();
        Rows.SelectionChanged += (_, _) =>
        {
            if (Rows.SelectedItem is FileTableRow row) NodeSelected?.Invoke(row.Node);
            SelectionChanged?.Invoke(Rows.SelectedItems?.Cast<FileTableRow>().Select(r => r.Node).ToArray() ?? []);
        };
        Rows.PointerMoved += (_, e) => {
            var control = e.Source as Avalonia.Visual;
            var row = control?.GetSelfAndVisualAncestors().OfType<Control>()
                .Select(c => c.DataContext).OfType<FileTableRow>().FirstOrDefault();
            NodeHovered?.Invoke(row?.Node ?? -1);
        };
        Rows.PointerExited += (_, _) => NodeHovered?.Invoke(-1);
        StageSelected.Click += (_, _) => NodesStaged?.Invoke(
            Rows.SelectedItems?.Cast<FileTableRow>().Select(r => r.Node).ToArray() ?? []);
        Rows.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || Rows.SelectedItem is not FileTableRow row) return;
            NodeOpened?.Invoke(row.Node);
            e.Handled = true;
        };
        DetachedFromVisualTree += (_, _) => _work?.Cancel();
    }

    public void SetStore(NodeStore? store, int root, FileQuery? query = null)
    {
        _store = store;
        _root = root;
        if (query is not null) _query = query;
        Refresh();
    }

    public void SetRecursive(bool recursive) => Recursive.IsChecked = recursive;
    public void SetSort(FileSort sort, bool descending)
    {
        _sort = sort;
        _descending = descending;
        Refresh();
    }

    private void Refresh() => Pending = RefreshAsync();
    private async Task RefreshAsync()
    {
        _work?.Cancel();
        using var work = new CancellationTokenSource();
        _work = work;
        NodeStore? store = _store;
        int root = _root;
        FileQuery query = _query;
        bool recursive = Recursive.IsChecked == true;
        FileSort sort = _sort;
        bool descending = _descending;
        Rows.ItemsSource = null;
        Error.Text = string.Empty;
        if (store is null) { _work = null; Summary.Text = string.Empty; return; }
        Summary.Text = Loc.T("Table_Loading");
        try
        {
            Page page = await Task.Run(() => BuildPage(store, root, query, recursive,
                sort, descending, MaxRows, work.Token), work.Token);
            if (work.IsCancellationRequested || !ReferenceEquals(work, _work)) return;
            Rows.ItemsSource = page.Rows;
            Summary.Text = (page.Total > page.Rows.Count
                ? Loc.T("Table_Capped", page.Rows.Count, page.Total)
                : Loc.T("Table_Count", page.Total)) + (descending ? " ↓" : " ↑");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(work, _work)) { Error.Text = ex.Message; Summary.Text = string.Empty; }
        }
        finally { if (ReferenceEquals(work, _work)) _work = null; }
    }

    /// <summary>
    /// So viele Zeilen hoechstens. Eine Laufwerkswurzel mit "Unterordner einbeziehen" hat
    /// Millionen Treffer; niemand liest sie, aber jede kostete ein Objekt und einen Ruckler
    /// beim Binden. Die Zusammenfassung nennt die Gesamtzahl, der Filter grenzt ein.
    /// </summary>
    internal const int MaxRows = 50_000;

    /// <summary>Die sichtbaren Zeilen und wie viele Treffer es insgesamt gab.</summary>
    internal sealed record Page(IReadOnlyList<FileTableRow> Rows, int Total);

    internal static IReadOnlyList<FileTableRow> BuildRows(NodeStore store, int root, FileQuery query,
        bool recursive, FileSort sort, bool descending, CancellationToken cancel = default)
        => BuildPage(store, root, query, recursive, sort, descending, int.MaxValue, cancel).Rows;

    internal static Page BuildPage(NodeStore store, int root, FileQuery query,
        bool recursive, FileSort sort, bool descending, int limit, CancellationToken cancel = default)
    {
        // Sortiert wird auf Knotenindizes mit einem Schluessel je Knoten — nicht auf Zeilen-
        // objekten mit Pfadstrings. Zeilen entstehen erst fuer das, was gezeigt wird.
        int[] nodes = [.. query.Find(store, root, recursive, cancel: cancel)];
        // Ordner zeigen die Dateien darunter, eine Datei zaehlt sich selbst.
        int[] below = store.FileCount.Length == store.Count ? store.FileCount : new int[store.Count];
        var files = new int[store.Count];
        foreach (int n in nodes) files[n] = store.IsDirectory(n) ? below[n] : store.IsRemoved(n) ? 0 : 1;

        Comparison<int> compare = sort switch
        {
            FileSort.Name => (a, b) => string.Compare(store.GetName(a), store.GetName(b), StringComparison.OrdinalIgnoreCase),
            FileSort.Physical => (a, b) => Physical(store, a).CompareTo(Physical(store, b)),
            FileSort.Modified => (a, b) => store.MTime[a].CompareTo(store.MTime[b]),
            FileSort.Files => (a, b) => files[a].CompareTo(files[b]),
            FileSort.Links => (a, b) => store.LinksOf(a).CompareTo(store.LinksOf(b)),
            FileSort.Path => (a, b) => string.Compare(store.PathOf(a), store.PathOf(b), StringComparison.OrdinalIgnoreCase),
            _ => (a, b) => store.Size[a].CompareTo(store.Size[b]),
        };
        Comparison<int> ordered = descending
            ? (a, b) => { int c = compare(b, a); return c != 0 ? c : a.CompareTo(b); }
            : (a, b) => { int c = compare(a, b); return c != 0 ? c : a.CompareTo(b); };

        // Ein Pfadvergleich je Paar waere bei Millionen Zeilen zu teuer: vorab berechnen.
        if (sort == FileSort.Path)
        {
            string[] paths = new string[nodes.Length];
            var index = new Dictionary<int, int>(nodes.Length);
            for (int k = 0; k < nodes.Length; k++) { paths[k] = store.PathOf(nodes[k]); index[nodes[k]] = k; }
            compare = (a, b) => string.Compare(paths[index[a]], paths[index[b]], StringComparison.OrdinalIgnoreCase);
            ordered = descending
                ? (a, b) => { int c = compare(b, a); return c != 0 ? c : a.CompareTo(b); }
                : (a, b) => { int c = compare(a, b); return c != 0 ? c : a.CompareTo(b); };
        }

        cancel.ThrowIfCancellationRequested();
        Array.Sort(nodes, ordered);
        cancel.ThrowIfCancellationRequested();

        int shown = Math.Min(limit, nodes.Length);
        var rows = new FileTableRow[shown];
        for (int k = 0; k < shown; k++) rows[k] = new FileTableRow(store, nodes[k], files[nodes[k]]);
        return new Page(rows, nodes.Length);
    }

    private static long Physical(NodeStore store, int node)
        => store.IsDirectory(node) ? store.PhysicalSize[node] : store.AllocationOf(node);

    private void OnSort(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !Enum.TryParse(tag, out FileSort sort)) return;
        SetSort(sort, sort == _sort ? !_descending : sort is not (FileSort.Name or FileSort.Path));
    }

    private void OnOpen(object? sender, TappedEventArgs e)
    {
        if (Rows.SelectedItem is FileTableRow row) NodeOpened?.Invoke(row.Node);
    }
}
