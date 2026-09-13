using DiskStats.Core.Scanning;

namespace DiskStats.Core.Storage;

public static class NodeStoreBuilder
{
    /// <summary>
    /// Verdichtet die unsortierten Rohdaten zu einem NodeStore.
    ///
    /// Phase 1 gruppiert die Eintraege per Counting Sort nach ihrer Eltern-Verzeichnis-Id.
    /// Phase 2 laeuft den Baum in Breitensuche ab der Wurzel ab und vergibt dabei die
    /// endgueltigen Knotenindizes. Die Breitensuche garantiert zwei Eigenschaften, auf die
    /// spaeter gebaut wird:
    ///   1. Die Kinder eines Knotens liegen zusammenhaengend.
    ///   2. Jeder Kindindex ist groesser als der Index seines Elternknotens — Voraussetzung
    ///      fuer den Rueckwaertslauf im SizeAggregator.
    /// </summary>
    public static NodeStore Build(NodeBuffer buffer, string rootPath)
        => Build(buffer.Entries, buffer.Names, buffer.MaxDirectoryId, rootPath);

    /// <summary>Baut aus einem eingefrorenen Zwischenstand — fuer das Zeichnen waehrend des Scans.</summary>
    public static NodeStore Build(NodeBuffer.Frozen frozen, string rootPath)
        => Build(frozen.Entries, frozen.Names, frozen.MaxDirectoryId, rootPath);

    private static NodeStore Build(
        IReadOnlyList<NodeBuffer.RawEntry> raw, NamePool names, int maxDirectoryId, string rootPath)
    {
        int nodeCount = raw.Count + 1;                 // +1 fuer die Wurzel
        int directoryCount = maxDirectoryId + 1;

        // Phase 1: Counting Sort. bucketBounds[d]..bucketBounds[d+1] begrenzt die
        // Eintragsindizes, deren Elternteil die Verzeichnis-Id d ist.
        var bucketBounds = new int[directoryCount + 1];
        for (int i = 0; i < raw.Count; i++)
        {
            int parent = raw[i].ParentId;
            // Ein Zulieferer, der auf eine nie vergebene Id verweist, hat einen Fehler — der
            // soll hier mit Namen genannt werden, nicht als IndexOutOfRange im Sortierlauf.
            if (parent < 0 || parent >= directoryCount)
                throw new InvalidOperationException(
                    $"Eintrag '{names.GetString(raw[i].NameOffset, raw[i].NameLength)}' verweist auf Verzeichnis-Id {parent}, " +
                    $"vergeben sind 0 bis {directoryCount - 1}.");
            bucketBounds[parent + 1]++;
        }
        for (int d = 0; d < directoryCount; d++) bucketBounds[d + 1] += bucketBounds[d];

        var ordered = new int[raw.Count];
        var cursor = new int[directoryCount];
        Array.Copy(bucketBounds, cursor, directoryCount);
        for (int i = 0; i < raw.Count; i++) ordered[cursor[raw[i].ParentId]++] = i;

        var parentIndex = new int[nodeCount];
        var childStart = new int[nodeCount];
        var childCount = new int[nodeCount];
        var nameOffset = new int[nodeCount];
        var nameLength = new ushort[nodeCount];
        var size = new long[nodeCount];
        var allocation = new long[nodeCount];
        var fileIds = new FileIdentity[nodeCount];
        var linkCounts = new uint[nodeCount];
        var mtime = new long[nodeCount];
        var flags = new EntryFlags[nodeCount];

        parentIndex[0] = -1;                            // die Wurzel hat keinen Elternknoten
        flags[0] = EntryFlags.Directory;

        // Phase 2: Breitensuche. Die Schlange traegt Paare aus Verzeichnis-Id und Knotenindex.
        var queue = new Queue<(int DirectoryId, int NodeIndex)>();
        queue.Enqueue((0, 0));
        int next = 1;

        while (queue.Count > 0)
        {
            (int directoryId, int nodeIndex) = queue.Dequeue();
            int from = bucketBounds[directoryId];
            int to = bucketBounds[directoryId + 1];

            if (from == to) continue;                   // leerer oder ungelesener Ordner

            childStart[nodeIndex] = next;
            childCount[nodeIndex] = to - from;

            for (int k = from; k < to; k++)
            {
                NodeBuffer.RawEntry e = raw[ordered[k]];
                int slot = next++;

                parentIndex[slot] = nodeIndex;
                nameOffset[slot] = e.NameOffset;
                nameLength[slot] = e.NameLength;
                size[slot] = e.Size;
                allocation[slot] = e.Storage.AllocatedBytes;
                fileIds[slot] = e.Storage.Identity;
                linkCounts[slot] = e.Storage.LinkCount;
                mtime[slot] = e.MTime;
                flags[slot] = e.Flags;

                // Reparse Points bekommen zwar eine Id, wurden aber nie gelesen —
                // ihr Bucket ist leer und die Schleife oben bricht sofort ab.
                if (e.OwnId >= 0) queue.Enqueue((e.OwnId, slot));
            }
        }

        // Nach dem Scan waechst der Namens-Pool nicht mehr — die reservierte Luft kann weg.
        names.Trim();

        if (next != nodeCount)
            throw new InvalidOperationException(
                $"Baum unvollstaendig: {next} von {nodeCount} Knoten erreicht. " +
                "Das deutet auf einen Eintrag hin, dessen Elternverzeichnis nie gescannt wurde.");

        return new NodeStore
        {
            RootPath = rootPath,
            Count = nodeCount,
            ParentIndex = parentIndex,
            ChildStart = childStart,
            ChildCount = childCount,
            NameOffset = nameOffset,
            NameLength = nameLength,
            Size = size,
            Allocation = allocation,
            FileIds = fileIds,
            LinkCounts = linkCounts,
            MTime = mtime,
            Flags = flags,
            Names = names,
        };
    }
}
