using DiskStats.Core.Scanning;

namespace DiskStats.Core.Storage;

/// <summary>
/// Nimmt waehrend des Scans alle Eintraege in flachen Listen entgegen — unsortiert und
/// in der Reihenfolge, in der die Worker sie finden. Das Ordnen uebernimmt der NodeStoreBuilder.
/// </summary>
public sealed class NodeBuffer : IEntrySink
{
    public readonly record struct RawEntry(
        int ParentId, int OwnId, int NameOffset, ushort NameLength,
        long Size, long MTime, EntryFlags Flags, FileStorageInfo Storage = default);

    private readonly List<RawEntry> _entries = [];
    private readonly NamePool _names = new();
    private readonly Lock _gate = new();

    public NamePool Names => _names;
    public IReadOnlyList<RawEntry> Entries => _entries;

    /// <summary>Groesste vergebene Verzeichnis-Id. Bestimmt, wie viele Ordnerknoten es gibt.</summary>
    public int MaxDirectoryId { get; private set; }

    /// <summary>Eingefrorener Zwischenstand: eigene Kopien, unabhaengig vom weiterlaufenden Scan.</summary>
    public sealed record Frozen(RawEntry[] Entries, NamePool Names, int MaxDirectoryId);

    /// <summary>
    /// Zieht einen konsistenten Zwischenstand, waehrend der Scan noch laeuft. Grundlage fuer das
    /// inkrementelle Zeichnen: die Treemap kann sich fuellen, bevor der Walk fertig ist.
    ///
    /// Der Zwischenstand ist immer ein gueltiger Baum, weil ein Verzeichnis stets gemeldet wird,
    /// bevor sein Inhalt gemeldet werden kann — jeder enthaltene Eintrag hat also einen Elternteil.
    /// </summary>
    public Frozen Freeze()
    {
        lock (_gate)
            return new Frozen(_entries.ToArray(), NamePool.FromBytes(_names.ToArray()), MaxDirectoryId);
    }

    public void AddFile(int parentId, ReadOnlySpan<char> name, long size, long mtimeUtcTicks, EntryFlags flags)
        => AddFile(parentId, name, size, mtimeUtcTicks, flags, FileStorageInfo.Unknown);

    public void AddFile(int parentId, ReadOnlySpan<char> name, long size, long mtimeUtcTicks,
        EntryFlags flags, FileStorageInfo storage)
    {
        lock (_gate)
        {
            int offset = _names.Add(name);
            _entries.Add(new RawEntry(parentId, OwnId: -1, offset,
                (ushort)NamePool.Utf8Length(name), size, mtimeUtcTicks, flags, storage));
        }
    }

    public void AddDirectory(int parentId, int ownId, ReadOnlySpan<char> name, long mtimeUtcTicks, EntryFlags flags)
    {
        lock (_gate)
        {
            int offset = _names.Add(name);
            _entries.Add(new RawEntry(parentId, ownId, offset,
                (ushort)NamePool.Utf8Length(name), Size: 0, mtimeUtcTicks, flags));
            if (ownId > MaxDirectoryId) MaxDirectoryId = ownId;
        }
    }
}
