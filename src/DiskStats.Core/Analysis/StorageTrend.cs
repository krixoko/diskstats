using DiskStats.Core.Storage;

namespace DiskStats.Core.Analysis;

public static class StorageTrend
{
    public sealed record Point(DateTime Written, long? Bytes);
    public sealed record Growth(string Path, long Before, long After) { public long Delta => After - Before; }
    public sealed record Report(IReadOnlyList<Point> Points, IReadOnlyList<Growth> Growth);

    public static Report Load(string root, string folder, int days, CancellationToken cancel)
    {
        DateTime cutoff = days == 0 ? DateTime.MinValue : DateTime.Now.AddDays(-days);
        var history = SnapshotStore.History(root).OrderBy(e => e.Written).ToArray();
        DateTime baseline = history.Where(e => e.Written <= cutoff).Select(e => e.Written).LastOrDefault();
        IEnumerable<(DateTime, NodeStore)> Read()
        {
            foreach (var entry in history.Where(e => e.Written >= cutoff || e.Written == baseline))
            {
                cancel.ThrowIfCancellationRequested();
                if (SnapshotStore.Load(entry.Path) is { } store) yield return (entry.Written, store);
            }
        }
        return Build(folder, Read(), cancel);
    }

    public static Report Build(string folder, IEnumerable<(DateTime Written, NodeStore Store)> snapshots,
        CancellationToken cancel = default)
    {
        var points = new List<Point>();
        Dictionary<string, long>? first = null, last = null;
        foreach (var (written, store) in snapshots)
        {
            cancel.ThrowIfCancellationRequested();
            int node = SubtreeRefresh.Find(store, folder);
            points.Add(new Point(written, node < 0 ? null : store.Size[node]));
            var children = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (node >= 0)
                for (int i = store.ChildStart[node]; i < store.ChildStart[node] + store.ChildCount[node]; i++)
                {
                    cancel.ThrowIfCancellationRequested();
                    if (!store.IsRemoved(i)) children[store.PathOf(i)] = store.Size[i];
                }
            first ??= children;
            last = children;
        }
        if (points.Count < 2 || first is null || last is null) return new(points, []);
        var growth = first.Keys.Union(last.Keys, StringComparer.OrdinalIgnoreCase)
            .Select(path => new Growth(path, first.GetValueOrDefault(path), last.GetValueOrDefault(path)))
            .Where(g => g.Delta != 0).OrderByDescending(g => g.Delta).ToArray();
        return new(points, growth);
    }
}
