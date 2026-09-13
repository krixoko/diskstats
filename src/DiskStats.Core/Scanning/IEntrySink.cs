namespace DiskStats.Core.Scanning;

/// <summary>
/// Empfaengt jeden gefundenen Eintrag. Der Name kommt als Span, der nur waehrend des Aufrufs
/// gueltig ist — Implementierungen muessen ihn kopieren, wenn sie ihn behalten wollen.
/// Genau das vermeidet eine String-Allokation pro Datei.
/// </summary>
public interface IEntrySink
{
    void AddFile(int parentId, ReadOnlySpan<char> name, long size, long mtimeUtcTicks, EntryFlags flags);

    void AddFile(int parentId, ReadOnlySpan<char> name, long size, long mtimeUtcTicks,
        EntryFlags flags, FileStorageInfo storage)
        => AddFile(parentId, name, size, mtimeUtcTicks, flags);

    void AddDirectory(int parentId, int ownId, ReadOnlySpan<char> name, long mtimeUtcTicks, EntryFlags flags);
}
