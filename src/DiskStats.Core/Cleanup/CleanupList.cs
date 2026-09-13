using DiskStats.Core.Storage;
using DiskStats.Core.Scanning;

namespace DiskStats.Core.Cleanup;

/// <summary>
/// Was gelöscht werden soll — gesammelt, bevor irgendetwas passiert.
///
/// Der Zwischenschritt ist Absicht: Beim Aufraeumen sammelt man mehrere Funde und will
/// vorher sehen, was zusammenkommt. Ein Loeschen direkt aus der Karte heraus waere ein
/// Klick von der Reue entfernt.
///
/// Gemerkt werden Pfade, nicht Knotenindizes: Nach einem neuen Scan zeigt derselbe Index
/// auf etwas anderes.
/// </summary>
public sealed class CleanupList
{
    public readonly record struct Item(string Path, string Name, long Size, bool IsDirectory);

    private readonly List<Item> _items = [];
    private static readonly System.Buffers.SearchValues<char> InvalidNameChars
        = System.Buffers.SearchValues.Create(['\\', '/', ':', '\0']);

    public IReadOnlyList<Item> Items => _items;
    public long TotalBytes => _items.Sum(i => i.Size);
    public int Count => _items.Count;

    /// <summary>Warum ein Eintrag abgelehnt wurde — oder null, wenn er aufgenommen wurde.</summary>
    public Refusal? Add(NodeStore store, int node)
    {
        if (node <= 0) return new Refusal(RefusalKind.ScanRoot);
        if (node >= store.Count) return new Refusal(RefusalKind.InvalidPath);
        if (store.IsRemoved(node)) return new Refusal(RefusalKind.Removed);

        string path = PathOf(store, node);

        // Der Pfad entsteht aus Namen im Baum. Stimmt dort etwas nicht — ein Name "..", ein
        // absoluter Pfad, ein Doppelpunkt —, zeigt er auf etwas, das nie gescannt wurde.
        // Das darf nicht in die Liste.
        if (store.GetName(node).AsSpan().IndexOfAny(InvalidNameChars) >= 0
            || !IsUnderRoot(store.RootPath, path))
            return new Refusal(RefusalKind.InvalidPath);

        Refusal? blocked = ProtectedPaths.Reason(path);
        if (blocked is not null) return blocked;

        if (_items.Any(i => i.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            return new Refusal(RefusalKind.AlreadyListed);

        // Liegt der Eintrag unter einem bereits gelisteten Ordner, waere er doppelt gezaehlt.
        foreach (Item existing in _items)
        {
            if (!existing.IsDirectory) continue;
            if (!path.StartsWith(existing.Path + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)) continue;

            return new Refusal(RefusalKind.InsideListed, existing.Name);
        }

        // Umgekehrt: ein neu aufgenommener Ordner ersetzt seine gelisteten Inhalte.
        if (store.IsDirectory(node))
        {
            _items.RemoveAll(i => i.Path.StartsWith(
                path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        }

        _items.Add(new Item(path, store.GetName(node), store.Size[node], store.IsDirectory(node)));
        return null;
    }

    /// <summary>
    /// Liegt <paramref name="path"/> echt unterhalb von <paramref name="root"/>? Beide werden
    /// zuerst aufgeloest, damit ".." und Kurznamen nicht taeuschen koennen. Die Wurzel selbst
    /// zaehlt nicht als "unterhalb" — sie ist nie ein Aufraeumziel.
    /// </summary>
    public static bool IsUnderRoot(string root, string path)
    {
        string fullRoot, fullPath;
        try
        {
            fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
        catch (PathTooLongException) { return false; }

        return fullPath.Length > fullRoot.Length + 1
            && fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public void Remove(string path)
        => _items.RemoveAll(i => i.Path.Equals(path, StringComparison.OrdinalIgnoreCase));

    public void Clear() => _items.Clear();

    /// <summary>
    /// Nach einem Teilerfolg behaltene Auftraege mit dem neuen Scan abgleichen. Der Pfad wird
    /// Segment fuer Segment im Baum gesucht — nicht jeder Knoten zum Pfad gemacht und verglichen.
    /// </summary>
    public void RefreshSizes(NodeStore store)
    {
        for (int item = 0; item < _items.Count; item++)
        {
            int node = SubtreeRefresh.Find(store, _items[item].Path);
            if (node <= 0 || store.IsRemoved(node)) continue;
            _items[item] = _items[item] with { Size = store.Size[node] };
        }
    }

    /// <summary>
    /// Markiert den gesamten entfernten Teilbaum und zieht seine Groesse einmalig von den
    /// Vorfahren ab. Die Markierung unterscheidet geloeschte von vorhandenen leeren Eintraegen.
    /// </summary>
    public static void ApplyRemoval(NodeStore store, int node)
    {
        if (Mark(store, node)) SizeAggregator.AggregatePhysical(store);
    }

    /// <summary>
    /// Mehrere Eintraege auf einmal: einmal markieren, einmal rechnen. Die physische
    /// Aggregation laeuft ueber den ganzen Baum — je Eintrag wiederholt waere sie bei fuenfzig
    /// Eintraegen und zwei Millionen Knoten eine Sekunde Stillstand.
    /// </summary>
    public static void ApplyRemovals(NodeStore store, IEnumerable<int> nodes)
    {
        bool any = false;
        foreach (int node in nodes) any |= Mark(store, node);
        if (any) SizeAggregator.AggregatePhysical(store);
    }

    private static bool Mark(NodeStore store, int node)
    {
        if (node <= 0 || node >= store.Count || store.IsRemoved(node)) return false;
        long gone = store.Size[node];
        var pending = new Stack<int>();
        pending.Push(node);
        while (pending.TryPop(out int removed))
        {
            int start = store.ChildStart[removed];
            for (int i = start; i < start + store.ChildCount[removed]; i++) pending.Push(i);
            store.Size[removed] = 0;
            store.Flags[removed] |= EntryFlags.Removed;
        }

        for (int parent = store.ParentIndex[node]; parent >= 0; parent = store.ParentIndex[parent])
            store.Size[parent] -= gone;
        return true;
    }

    public static string PathOf(NodeStore store, int node) => store.PathOf(node);
}
