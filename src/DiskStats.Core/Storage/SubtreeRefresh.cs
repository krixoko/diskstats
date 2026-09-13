using DiskStats.Core.Scanning;

namespace DiskStats.Core.Storage;

public static class SubtreeRefresh
{
    public sealed record Result(NodeStore Store, int Errors, string ScannedPath);

    /// <summary>Only the selected directory is read from disk; the other branches are copied in memory.</summary>
    public static Result Refresh(NodeStore store, int node, WalkOptions options, CancellationToken cancel = default)
    {
        if (node < 0 || node >= store.Count || store.IsRemoved(node)) throw new ArgumentOutOfRangeException(nameof(node));
        if (!store.IsDirectory(node)) node = store.ParentIndex[node];
        if (node > 0 && (store.Flags[node] & EntryFlags.ReparsePoint) != 0)
            throw new InvalidOperationException("Reparse points are not followed during refresh.");
        string path = store.PathOf(node);
        var fresh = new NodeBuffer();
        WalkResult walk = DirectoryWalker.Walk(path, fresh,
            options with { ExclusionRoot = options.ExclusionRoot ?? store.RootPath }, cancel);
        cancel.ThrowIfCancellationRequested();
        NodeStore replacement = NodeStoreBuilder.Build(fresh, path);
        SizeAggregator.Aggregate(replacement);

        bool missing;
        try { _ = File.GetAttributes(path); missing = false; }
        catch (DirectoryNotFoundException) { missing = true; }
        catch (FileNotFoundException) { missing = true; }
        // Access errors must not turn an existing subtree into an empty or deleted one.
        catch (UnauthorizedAccessException) { return new Result(store, Math.Max(1, walk.Errors.Count), path); }
        if (walk.Errors.Count > 0 && replacement.Count == 1 && !missing)
            return new Result(store, walk.Errors.Count, path);
        if (!missing) replacement.MTime[0] = Directory.GetLastWriteTimeUtc(path).Ticks;

        // Unlesbare Unterordner behalten ihren alten Inhalt: Was der Walker dort nicht sehen
        // durfte, ist deshalb nicht weg. Sie bekommen die Markierung, die auch der Scan setzt.
        Dictionary<int, int>? keep = missing ? null : Unreadable(store, replacement, walk.Errors);
        if (node == 0 && keep is null) return new Result(replacement, walk.Errors.Count, path);
        return new Result(Replace(store, node, missing ? null : replacement, cancel, keep), missing ? 0 : walk.Errors.Count, path);
    }

    /// <summary>
    /// Ordnet jedem unlesbaren Ordner im frischen Teilbaum seinen Vorgaenger im alten zu
    /// (oder -1, wenn er neu ist). Leer, wenn es nichts zu bewahren gibt.
    /// </summary>
    private static Dictionary<int, int>? Unreadable(NodeStore old, NodeStore fresh, ScanErrorLog errors)
    {
        if (errors.Count == 0) return null;
        Dictionary<int, int>? keep = null;
        foreach (ScanError error in errors.Snapshot())
        {
            int inFresh = Find(fresh, error.Path);
            if (inFresh <= 0 || !fresh.IsDirectory(inFresh)) continue;
            (keep ??= [])[inFresh] = Find(old, error.Path);
        }
        return keep;
    }

    public static int Find(NodeStore store, string fullPath)
    {
        string relative = Path.GetRelativePath(store.RootPath, fullPath);
        if (relative == ".") return 0;
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar)) return -1;
        int node = 0;
        foreach (string part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            int found = -1;
            for (int i = store.ChildStart[node]; i < store.ChildStart[node] + store.ChildCount[node]; i++)
                if (!store.IsRemoved(i) && store.GetName(i).Equals(part, StringComparison.OrdinalIgnoreCase)) { found = i; break; }
            if (found < 0) return -1;
            node = found;
        }
        return node;
    }

    /// <param name="keepFromOriginal">
    /// Ordner im Ersatz (Schluessel), deren Inhalt aus dem Original (Wert, oder -1) uebernommen
    /// wird, weil der Ersatz sie nicht lesen konnte. Sie werden als unlesbar markiert.
    /// </param>
    public static NodeStore Replace(NodeStore original, int target, NodeStore? replacement, CancellationToken cancel = default,
        IReadOnlyDictionary<int, int>? keepFromOriginal = null)
    {
        var buffer = new NodeBuffer();
        int nextId = 0;
        var queue = new Queue<(NodeStore Store, int Node, int Id)>();
        // Wird die Wurzel selbst erneuert, ist der Ersatz der Ausgangspunkt.
        queue.Enqueue(target == 0 && replacement is not null ? (replacement, 0, 0) : (original, 0, 0));
        while (queue.TryDequeue(out var folder))
        {
            cancel.ThrowIfCancellationRequested();
            NodeStore source = folder.Store;
            for (int i = source.ChildStart[folder.Node]; i < source.ChildStart[folder.Node] + source.ChildCount[folder.Node]; i++)
            {
                if (source.IsRemoved(i)) continue;
                bool replace = ReferenceEquals(source, original) && i == target;
                if (replace && replacement is null) continue;
                if (source.IsDirectory(i))
                {
                    int id = ++nextId;
                    if (keepFromOriginal is not null && ReferenceEquals(source, replacement) && keepFromOriginal.TryGetValue(i, out int old))
                    {
                        bool known = old > 0 && original.IsDirectory(old);
                        buffer.AddDirectory(folder.Id, id, source.GetName(i), known ? original.MTime[old] : source.MTime[i],
                            source.Flags[i] | EntryFlags.Unreadable);
                        queue.Enqueue(known ? (original, old, id) : (source, i, id));
                        continue;
                    }
                    buffer.AddDirectory(folder.Id, id, source.GetName(i), replace ? replacement!.MTime[0] : source.MTime[i], source.Flags[i]);
                    queue.Enqueue(replace ? (replacement!, 0, id) : (source, i, id));
                }
                else buffer.AddFile(folder.Id, source.GetName(i), source.Size[i], source.MTime[i], source.Flags[i],
                    new FileStorageInfo(source.AllocationOf(i), source.IdentityOf(i), source.LinksOf(i)));
            }
        }
        NodeStore result = NodeStoreBuilder.Build(buffer, original.RootPath);
        SizeAggregator.Aggregate(result);
        return result;
    }
}
