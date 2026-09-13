using DiskStats.Core.Scanning;

namespace DiskStats.Core.Storage;

/// <summary>
/// Der fertige Baum als parallele Arrays. Index 0 ist immer die Wurzel.
/// Kinder eines Knotens liegen zusammenhaengend ab ChildStart — dadurch ist das Durchlaufen
/// eines Ordners ein sequentieller Speicherzugriff statt einer Zeigerjagd.
/// </summary>
public sealed class NodeStore
{
    public required string RootPath { get; init; }
    public required int Count { get; init; }
    public required int[] ParentIndex { get; init; }
    public required int[] ChildStart { get; init; }
    public required int[] ChildCount { get; init; }
    public required int[] NameOffset { get; init; }
    public required ushort[] NameLength { get; init; }
    public required long[] Size { get; init; }
    public required long[] MTime { get; init; }
    public required EntryFlags[] Flags { get; init; }
    public required NamePool Names { get; init; }

    // Allocation -1 means unavailable, not zero bytes. Old snapshots have no allocation data.
    public long[] Allocation { get; init; } = [];
    public FileIdentity[] FileIds { get; init; } = [];
    public uint[] LinkCounts { get; init; } = [];
    public long[] PhysicalSize { get; internal set; } = [];
    public int[] UnknownAllocation { get; internal set; } = [];

    /// <summary>
    /// Lebende Dateien bzw. Ordner unterhalb eines Knotens (ohne ihn selbst). Vorberechnet,
    /// weil die Kopfzeile sie bei jeder Navigation braucht und ein Zaehllauf ueber zwei
    /// Millionen Knoten auf dem UI-Thread spuerbar waere. Gepflegt von <see cref="SizeAggregator"/>.
    /// </summary>
    public int[] FileCount { get; internal set; } = [];
    public int[] FolderCount { get; internal set; } = [];
    public bool HasStorageMetadata => Allocation.Length == Count;
    public long AllocationOf(int node) => HasStorageMetadata ? Allocation[node] : -1;
    public FileIdentity IdentityOf(int node) => FileIds.Length == Count ? FileIds[node] : default;
    public uint LinksOf(int node) => LinkCounts.Length == Count ? LinkCounts[node] : 0;

    public string GetName(int index) => Names.GetString(NameOffset[index], NameLength[index]);

    public bool IsRemoved(int index) => (Flags[index] & EntryFlags.Removed) != 0;

    public bool IsDirectory(int index) => (Flags[index] & EntryFlags.Directory) != 0;

    /// <summary>
    /// Der vollstaendige Pfad eines Knotens.
    ///
    /// Der Baum haelt nur Namen und Elternbeziehungen; der Pfad entsteht beim Aufsteigen. Das
    /// ist Absicht — ihn je Knoten zu speichern kostete bei Millionen Eintraegen mehr
    /// Speicher als der ganze uebrige Baum.
    /// </summary>
    public string PathOf(int index)
    {
        if (index <= 0) return RootPath;

        // Erst die Kette der Vorfahren einsammeln, dann in einem Zug schreiben — ohne Liste,
        // ohne Insert(0), ohne Zwischenstrings. Diese Funktion laeuft in Suche, Export und
        // Hardlink-Abgleich millionenfach.
        int depth = 0;
        for (int i = index; i > 0; i = ParentIndex[i]) depth++;

        int[]? rented = depth > 64 ? System.Buffers.ArrayPool<int>.Shared.Rent(depth) : null;
        Span<int> chain = rented ?? stackalloc int[64];
        chain = chain[..depth];
        for (int i = index, k = depth - 1; i > 0; i = ParentIndex[i]) chain[k--] = i;

        string root = RootPath;
        bool rootHasSeparator = root.Length > 0 && root[^1] == Path.DirectorySeparatorChar;
        int length = root.Length + (rootHasSeparator ? 0 : 1) + depth - 1;
        foreach (int node in chain)
            length += System.Text.Encoding.UTF8.GetCharCount(Names.GetBytes(NameOffset[node], NameLength[node]));

        string result = string.Create(length, (this, root, rootHasSeparator, chain.ToArray()), static (span, state) =>
        {
            (NodeStore store, string root, bool hasSeparator, int[] chain) = state;
            root.AsSpan().CopyTo(span);
            int pos = root.Length;
            if (!hasSeparator) span[pos++] = Path.DirectorySeparatorChar;
            for (int k = 0; k < chain.Length; k++)
            {
                if (k > 0) span[pos++] = Path.DirectorySeparatorChar;
                int node = chain[k];
                pos += System.Text.Encoding.UTF8.GetChars(
                    store.Names.GetBytes(store.NameOffset[node], store.NameLength[node]), span[pos..]);
            }
        });

        if (rented is not null) System.Buffers.ArrayPool<int>.Shared.Return(rented);
        return result;
    }

    /// <summary>
    /// Der Name, wie er angezeigt gehoert.
    ///
    /// Die Wurzel traegt keinen: Sie ist durch <see cref="RootPath"/> benannt, nicht durch
    /// einen Eintrag im Namensvorrat. Wer sie beschriften will, braucht daher den letzten
    /// Teil des Pfads — und bei einer Laufwerkswurzel wie "C:\", die keinen letzten Teil
    /// hat, den Pfad selbst.
    /// </summary>
    public string DisplayName(int index)
    {
        string name = GetName(index);
        if (name.Length > 0) return name;

        string leaf = Path.GetFileName(RootPath.TrimEnd(Path.DirectorySeparatorChar));
        return leaf.Length > 0 ? leaf : RootPath;
    }
}
