namespace DiskStats.Core.Scanning;

public sealed record WalkOptions
{
    /// <summary>
    /// Anzahl paralleler Worker. Die automatische Drosselung auf HDDs
    /// (IOCTL_STORAGE_QUERY_PROPERTY) folgt in M2 — bis dahin explizit setzen und messen.
    /// </summary>
    public ScanMethod Method { get; init; } = ScanMethod.Auto;
    public string? ExclusionRoot { get; init; }
    public string[] ExcludedPaths { get; init; } = [];

    public int WorkerCount { get; init; } = Environment.ProcessorCount;

    /// <summary>
    /// Fortschritt des NTFS-Scans von 0 bis 1. Der Verzeichnisscan fuellt seinen Puffer
    /// sichtbar waehrend der Arbeit; das Lesen der MFT liefert erst am Ende einen Baum und
    /// bliebe sonst minutenlang stumm. Wird auf einem Hintergrundfaden aufgerufen, hoechstens
    /// einmal je ganzem Prozent.
    /// </summary>
    public Action<double>? MftProgress { get; init; }
}
