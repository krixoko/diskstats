namespace DiskStats.Core.Scanning;

public readonly record struct ScanError(string Path, int ErrorCode);

/// <summary>
/// Sammelt unlesbare Verzeichnisse, statt sie zu verschlucken. Die UI weist sie spaeter aus,
/// damit eine zu kleine Gesamtsumme nicht als korrekt erscheint.
/// </summary>
public sealed class ScanErrorLog
{
    private readonly List<ScanError> _errors = [];
    private readonly Lock _gate = new();

    public int Count { get { lock (_gate) return _errors.Count; } }

    public void Record(string path, int errorCode)
    {
        lock (_gate) _errors.Add(new ScanError(path, errorCode));
    }

    public IReadOnlyList<ScanError> Snapshot()
    {
        lock (_gate) return _errors.ToArray();
    }
}
