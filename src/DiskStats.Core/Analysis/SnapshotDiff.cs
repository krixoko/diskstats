using DiskStats.Core.Storage;

namespace DiskStats.Core.Analysis;

public enum ChangeKind { Grown, Shrunk, Added, Removed }

/// <summary>Was sich an einer Stelle getan hat.</summary>
public sealed record Change(
    string Path, string Name, bool IsDirectory, long Before, long After, ChangeKind Kind)
{
    public long Delta => After - Before;
}

/// <summary>
/// Vergleicht zwei Staende desselben Ordners.
///
/// Die Frage dahinter ist "wo ist der Platz hin", und die beantwortet eine Karte nicht: Sie
/// zeigt, was gross ist, nicht was gross geworden ist. Ein Ordner, der seit vier Wochen um
/// zwanzig Gigabyte zugelegt hat, faellt zwischen lauter anderen grossen Ordnern nicht auf.
///
/// Verglichen wird ueber Namen, nicht ueber Knotennummern: Zwischen zwei Scans verschiebt
/// sich die Nummerierung bei jeder neu angelegten Datei.
/// </summary>
public static class SnapshotDiff
{
    /// <summary>
    /// Kleiner meldet der Vergleich nicht. Ohne Schwelle bestuende das Ergebnis aus
    /// Protokolldateien, die um ein paar Kilobyte gewachsen sind.
    /// </summary>
    public const long DefaultMinDelta = 10L * 1024 * 1024;

    public static IReadOnlyList<Change> Compare(
        NodeStore before, NodeStore after, long minDelta = DefaultMinDelta)
    {
        // Zwei Staende verschiedener Ordner teilen keinen Namen: Alles waere "neu" und alles
        // "verschwunden". Das ist kein Vergleich, das ist ein Missgriff — und der soll auffallen.
        if (!SameRoot(before.RootPath, after.RootPath))
            throw new ArgumentException(
                $"Die Staende gehoeren zu verschiedenen Ordnern: '{before.RootPath}' und '{after.RootPath}'.");

        var changes = new List<Change>();
        Walk(before, 0, after, 0, string.Empty, minDelta, changes);

        return [.. changes.OrderByDescending(c => Math.Abs(c.Delta))];
    }

    /// <summary>Was ein Teilbaum an Meldungen beigesteuert hat.</summary>
    private readonly record struct Yield(int Count, long Delta)
    {
        public static Yield operator +(Yield a, Yield b) => new(a.Count + b.Count, a.Delta + b.Delta);
    }

    /// <summary>
    /// Steigt beide Baeume gleichzeitig ab und paart die Kinder ueber ihre Namen. Gibt zurueck,
    /// wieviele Meldungen unterhalb von <paramref name="b"/> entstanden sind und wieviel sie
    /// zusammen ausmachen — daran entscheidet der Aufrufer, ob er sich selbst noch melden muss.
    /// </summary>
    private static Yield Walk(
        NodeStore before, int a, NodeStore after, int b,
        string path, long minDelta, List<Change> changes)
    {
        Dictionary<string, int> mine = ChildrenOf(before, a);
        Dictionary<string, int> theirs = ChildrenOf(after, b);

        var sum = new Yield(0, 0);

        foreach ((string name, int child) in theirs)
        {
            string childPath = path.Length == 0 ? name : path + Path.DirectorySeparatorChar + name;
            bool isDirectory = after.IsDirectory(child);

            if (!mine.TryGetValue(name, out int old))
            {
                if (after.Size[child] < minDelta) continue;

                changes.Add(new Change(childPath, name, isDirectory,
                    0, after.Size[child], ChangeKind.Added));

                sum += new Yield(1, after.Size[child]);
                continue;
            }

            long delta = after.Size[child] - before.Size[old];
            var inner = new Yield(0, 0);

            if (isDirectory && before.IsDirectory(old))
                inner = Walk(before, old, after, child, childPath, minDelta, changes);

            sum += inner;

            if (Math.Abs(delta) < minDelta) continue;

            // Erklaert genau eine Meldung von weiter unten diesen Ordner vollstaendig, waere
            // er nur ein Wegweiser auf sie. Sonst staende dieselbe Zahl auf jeder Ebene von
            // "Users" bis hinunter zu der Datei, um die es wirklich geht.
            if (inner.Count == 1 && inner.Delta == delta) continue;

            changes.Add(new Change(childPath, name, isDirectory,
                before.Size[old], after.Size[child],
                delta > 0 ? ChangeKind.Grown : ChangeKind.Shrunk));

            sum += new Yield(1, delta);
        }

        foreach ((string name, int old) in mine)
        {
            if (theirs.ContainsKey(name) || before.Size[old] < minDelta) continue;

            string childPath = path.Length == 0 ? name : path + Path.DirectorySeparatorChar + name;

            changes.Add(new Change(childPath, name, before.IsDirectory(old),
                before.Size[old], 0, ChangeKind.Removed));

            sum += new Yield(1, -before.Size[old]);
        }

        return sum;
    }

    private static Dictionary<string, int> ChildrenOf(NodeStore store, int node)
    {
        int start = store.ChildStart[node];
        var map = new Dictionary<string, int>(
            store.ChildCount[node], StringComparer.OrdinalIgnoreCase);

        for (int i = start; i < start + store.ChildCount[node]; i++)
            if (!store.IsRemoved(i)) map[store.GetName(i)] = i;

        return map;
    }

    private static bool SameRoot(string a, string b)
    {
        static string Normal(string path)
        {
            try { path = Path.GetFullPath(path); }
            catch (ArgumentException) { }
            catch (IOException) { }
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        return string.Equals(Normal(a), Normal(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Wieviel der Ordner insgesamt zugelegt oder verloren hat.</summary>
    public static long TotalDelta(NodeStore before, NodeStore after)
        => after.Size[0] - before.Size[0];
}
