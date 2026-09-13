namespace DiskStats.Core.Scanning;

/// <summary>
/// Zaehlt nur. Fuer M0, um die Rohgeschwindigkeit der Enumeration ohne Speicherkosten zu messen,
/// und als leichtgewichtiges Test-Double.
/// </summary>
public sealed class CountingSink : IEntrySink
{
    private long _fileCount;
    private long _directoryCount;
    private long _totalBytes;

    public long FileCount => Interlocked.Read(ref _fileCount);
    public long DirectoryCount => Interlocked.Read(ref _directoryCount);
    public long TotalBytes => Interlocked.Read(ref _totalBytes);

    public void AddFile(int parentId, ReadOnlySpan<char> name, long size, long mtimeUtcTicks, EntryFlags flags)
    {
        Interlocked.Increment(ref _fileCount);
        Interlocked.Add(ref _totalBytes, size);
    }

    public void AddDirectory(int parentId, int ownId, ReadOnlySpan<char> name, long mtimeUtcTicks, EntryFlags flags)
        => Interlocked.Increment(ref _directoryCount);
}
