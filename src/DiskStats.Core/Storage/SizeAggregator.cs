namespace DiskStats.Core.Storage;

public static class SizeAggregator
{
    /// <summary>
    /// Summiert die Groessen bottom-up in jeden Ordnerknoten.
    /// Bewusst iterativ: ein rekursiver Abstieg wuerde bei tiefen Baeumen den Stack sprengen.
    /// Da Kinder immer einen groesseren Index haben als ihr Elternknoten, genuegt ein
    /// Rueckwaertslauf ueber das Array — jedes Kind ist fertig, bevor sein Elternknoten drankommt.
    /// </summary>
    public static void Aggregate(NodeStore store)
    {
        long[] size = store.Size;
        int[] parent = store.ParentIndex;

        // Ordnergroessen zuruecksetzen: sie ergeben sich ausschliesslich aus den Kindern.
        for (int i = 0; i < store.Count; i++)
            if (store.IsDirectory(i)) size[i] = 0;

        for (int i = store.Count - 1; i >= 1; i--)
        {
            int p = parent[i];
            if (p >= 0) size[p] += size[i];
        }
        AggregatePhysical(store);
    }

    /// <summary>Charge each file identity once in the scan; unknown allocations remain explicit.</summary>
    public static void AggregatePhysical(NodeStore store)
    {
        var bytes = new long[store.Count];
        var unknown = new int[store.Count];
        var owners = new Dictionary<DiskStats.Core.Scanning.FileIdentity, int>();
        for (int i = 1; i < store.Count; i++)
        {
            if (store.IsRemoved(i) || store.IsDirectory(i)) continue;
            var id = store.IdentityOf(i);
            long allocation = store.AllocationOf(i);
            if (id.IsKnown)
            {
                if (owners.TryGetValue(id, out int previous))
                {
                    long previousAllocation = store.AllocationOf(previous);
                    // Prefer measured allocation, then a stable path regardless of worker order.
                    if (allocation < 0 && previousAllocation >= 0) continue;
                    if ((allocation >= 0) == (previousAllocation >= 0)
                        && StringComparer.OrdinalIgnoreCase.Compare(store.PathOf(previous), store.PathOf(i)) <= 0) continue;
                    bytes[previous] = 0;
                    unknown[previous] = 0;
                }
                owners[id] = i;
            }
            if (allocation >= 0) bytes[i] = allocation;
            else unknown[i] = 1;
        }
        for (int i = store.Count - 1; i > 0; i--)
        {
            int p = store.ParentIndex[i];
            if (p < 0) continue;
            bytes[p] += bytes[i];
            unknown[p] += unknown[i];
        }
        store.PhysicalSize = bytes;
        store.UnknownAllocation = unknown;
        AggregateCounts(store);
    }

    /// <summary>Zaehlt lebende Dateien und Ordner unterhalb jedes Knotens — derselbe Rueckwaertslauf.</summary>
    public static void AggregateCounts(NodeStore store)
    {
        var files = new int[store.Count];
        var folders = new int[store.Count];
        for (int i = store.Count - 1; i > 0; i--)
        {
            int p = store.ParentIndex[i];
            if (p < 0) continue;
            files[p] += files[i];
            folders[p] += folders[i];
            if (store.IsRemoved(i)) continue;
            if (store.IsDirectory(i)) folders[p]++;
            else files[p]++;
        }
        store.FileCount = files;
        store.FolderCount = folders;
    }
}
