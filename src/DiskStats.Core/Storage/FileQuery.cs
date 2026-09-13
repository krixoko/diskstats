using System.Text.RegularExpressions;
using DiskStats.Core.Scanning;

namespace DiskStats.Core.Storage;

public sealed record FileQuery
{
    public string Name { get; init; } = string.Empty;
    public string PathPattern { get; init; } = string.Empty;
    public bool UseRegex { get; init; }
    public long? MinBytes { get; init; }
    public long? MaxBytes { get; init; }
    public int? OlderThanDays { get; init; }

    /// <summary>Etwa 10 000 Jahre — mehr Tage gaebe es bis zum Ende von DateTime nicht.</summary>
    public const int MaxDays = 3652058;

    public void Validate()
    {
        if (MinBytes < 0 || MaxBytes < 0 || OlderThanDays < 0 || OlderThanDays > MaxDays || MinBytes > MaxBytes)
            throw new ArgumentException("Invalid size or age range.");
        _ = Compile(Name);
        _ = Compile(PathPattern);
    }

    public IReadOnlyList<int> Find(NodeStore store, int root = 0, bool recursive = true,
        DateTime? nowUtc = null, CancellationToken cancel = default)
    {
        Validate();
        Regex? name = Compile(Name);
        Regex? path = Compile(PathPattern);
        // Validate begrenzt die Tage; checked haelt die Grenze auch dann, wenn jemand sie lockert.
        long? before = OlderThanDays is { } days
            ? Math.Max(0, (nowUtc ?? DateTime.UtcNow).Ticks - checked(Math.Min(days, MaxDays) * TimeSpan.TicksPerDay))
            : null;
        var results = new List<int>();
        var pending = new Stack<int>();
        pending.Push(root);
        while (pending.TryPop(out int parent))
        {
            cancel.ThrowIfCancellationRequested();
            int start = store.ChildStart[parent];
            for (int i = start; i < start + store.ChildCount[parent]; i++)
            {
                if (store.IsRemoved(i)) continue;
                if (recursive && store.IsDirectory(i)) pending.Push(i);
                if (MinBytes is { } min && store.Size[i] < min) continue;
                if (MaxBytes is { } max && store.Size[i] > max) continue;
                if (before is { } cutoff && (store.MTime[i] <= 0 || store.MTime[i] > cutoff)) continue;
                if (name is not null && !name.IsMatch(store.GetName(i))) continue;
                if (path is not null && !path.IsMatch(Path.GetRelativePath(store.RootPath, store.PathOf(i)).Replace('\\', '/')))
                    continue;
                results.Add(i);
            }
        }
        return results;
    }

    private Regex? Compile(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return null;
        if (!UseRegex) return ScanExclusions.Glob(pattern.Trim());

        // Nutzermuster laufen ueber Millionen Namen. Die lineare Auswertung kennt keine
        // katastrophale Rueckwaertssuche — "(a+)+$" bleibt ein Muster statt einer Sekunde
        // je Datei. Was sie nicht kann (Rueckbezuege, Lookarounds), bekommt die klassische
        // Auswertung mit Zeitgrenze.
        const RegexOptions common = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        try
        {
            return new Regex(pattern, common | RegexOptions.NonBacktracking);
        }
        catch (NotSupportedException)
        {
            return new Regex(pattern, common, TimeSpan.FromMilliseconds(100));
        }
    }
}
