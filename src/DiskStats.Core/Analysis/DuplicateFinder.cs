using System.Security.Cryptography;
using DiskStats.Core.Scanning;
using DiskStats.Core.Storage;

namespace DiskStats.Core.Analysis;

/// <summary>Gleiche Dateien, die mehrfach herumliegen.</summary>
public sealed record DuplicateGroup(long Size, IReadOnlyList<int> Nodes)
{
    public int Count => Nodes.Count;

    /// <summary>
    /// Was frei wuerde, wenn eine Fassung bleibt. Nicht die Summe der Gruppe: Eine davon
    /// will man behalten, sonst waere es kein Duplikat, sondern eine Loeschliste.
    /// </summary>
    public long WastedBytes => Size * (Count - 1);
}

public sealed record DuplicateProgress(int Done, int Total);

/// <summary>
/// Findet inhaltsgleiche Dateien.
///
/// Der Scan kennt nur Groessen; hier wird zum ersten Mal in Dateien hineingelesen, und das
/// kostet echte Zeit. Deshalb drei Stufen, die jede teurer ist als die vorige und nur das
/// weiterreicht, was die vorige nicht ausschliessen konnte:
///
///   1. Groesse — steht schon im Baum, kostet nichts. Wer allein auf seiner Groesse sitzt,
///      kann kein Duplikat sein.
///   2. Die ersten Kilobytes. Dateien gleicher Groesse unterscheiden sich meistens sofort;
///      dafuer die ganze Datei zu lesen waere Verschwendung.
///   3. Der vollstaendige Inhalt. Nur noch fuer die wenigen, die bis hierher gleich aussehen.
///
/// Verglichen wird ueber SHA-256 statt Byte fuer Byte: Bei drei gleichen Dateien braeuchte der
/// paarweise Vergleich drei Durchlaeufe, der Hash nur einen je Datei. Kryptografisch statt
/// schnell, weil an dieser Aussage haengt, was jemand loescht.
/// </summary>
public static class DuplicateFinder
{
    /// <summary>
    /// Kleiner lohnt die Suche nicht: Ein paar hundert gleiche Symbolschriften und
    /// Konfigurationsschnipsel sind kein Fund, sondern Laerm.
    /// </summary>
    public const long DefaultMinSize = 64 * 1024;

    private const int HeadBytes = 8 * 1024;

    public static IReadOnlyList<DuplicateGroup> Find(
        NodeStore store,
        int root,
        long minSize = DefaultMinSize,
        IProgress<DuplicateProgress>? progress = null,
        CancellationToken cancel = default)
    {
        List<int> candidates = Candidates(store, root, minSize, cancel);

        // Stufe 1: gleiche Groesse
        IEnumerable<List<int>> groups = ByKey(candidates, node => store.Size[node]);

        // Stufe 2 und 3: gleicher Anfang, dann gleicher Inhalt.
        // Jede Datei zaehlt zweimal — einmal je Lesephase. Wer nach dem Anfang herausfaellt,
        // bekommt den zweiten Schritt geschenkt, damit der Balken am Ende voll ist und die
        // teure dritte Stufe nicht als Stillstand erscheint.
        int total = 2 * groups.Sum(g => g.Count);
        int done = 0;

        var confirmed = new List<DuplicateGroup>();

        foreach (List<int> sameSize in groups)
        {
            cancel.ThrowIfCancellationRequested();

            long size = store.Size[sameSize[0]];

            // Passt die Datei ohnehin in den Anfang, ist der zweite Durchlauf derselbe.
            bool headIsAll = size <= HeadBytes;

            List<List<int>> byHead = Hashed(store, sameSize, size, HeadBytes, ref done, total, progress, cancel);
            int reachingFullHash = headIsAll ? 0 : byHead.Sum(g => g.Count);
            done += sameSize.Count - reachingFullHash;
            progress?.Report(new DuplicateProgress(done, total));

            foreach (List<int> sameHead in byHead)
            {
                if (headIsAll)
                {
                    confirmed.Add(new DuplicateGroup(size, sameHead));
                    continue;
                }

                foreach (List<int> same in Hashed(store, sameHead, size, int.MaxValue, ref done, total, progress, cancel))
                    confirmed.Add(new DuplicateGroup(size, same));
            }
        }

        progress?.Report(new DuplicateProgress(total, total));

        return [.. confirmed.OrderByDescending(g => g.WastedBytes)];
    }

    /// <summary>
    /// Was ueberhaupt in Frage kommt.
    ///
    /// Ausgelassen werden Verknuepfungen und Wolkendateien: Eine Wolkendatei liegt nicht auf
    /// der Platte, und sie zu lesen hiesse, sie herunterzuladen — der Nutzer wollte eine
    /// Auswertung, keinen Download von hundert Gigabyte.
    /// </summary>
    private static List<int> Candidates(NodeStore store, int root, long minSize, CancellationToken cancel)
    {
        var found = new List<int>();
        var identities = new HashSet<FileIdentity>();
        var stack = new Stack<int>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            cancel.ThrowIfCancellationRequested();

            int node = stack.Pop();
            int start = store.ChildStart[node];

            for (int i = start; i < start + store.ChildCount[node]; i++)
            {
                if (store.IsRemoved(i)) continue;
                if (store.IsDirectory(i)) { stack.Push(i); continue; }

                EntryFlags flags = store.Flags[i];
                if ((flags & (EntryFlags.ReparsePoint | EntryFlags.CloudPlaceholder | EntryFlags.Unreadable)) != 0)
                    continue;

                FileIdentity identity = store.IdentityOf(i);
                if (store.Size[i] >= minSize && (!identity.IsKnown || identities.Add(identity))) found.Add(i);
            }
        }

        return found;
    }

    /// <summary>Gruppiert und wirft weg, was allein bleibt — Einzelstuecke sind keine Duplikate.</summary>
    private static List<List<int>> ByKey<TKey>(IEnumerable<int> nodes, Func<int, TKey> key)
        where TKey : notnull
    {
        var buckets = new Dictionary<TKey, List<int>>();

        foreach (int node in nodes)
        {
            if (!buckets.TryGetValue(key(node), out List<int>? bucket))
                buckets[key(node)] = bucket = [];

            bucket.Add(node);
        }

        return [.. buckets.Values.Where(b => b.Count > 1)];
    }

    /// <summary>
    /// Liest die Dateien einer Gruppe und teilt sie nach ihrem Hash weiter auf.
    /// Was sich nicht lesen laesst, faellt heraus statt den Lauf zu beenden.
    /// </summary>
    private static List<List<int>> Hashed(
        NodeStore store, List<int> nodes, long expectedSize, int limit,
        ref int done, int total, IProgress<DuplicateProgress>? progress, CancellationToken cancel)
    {
        var byHash = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        foreach (int node in nodes)
        {
            cancel.ThrowIfCancellationRequested();

            string? hash = HashOf(store.PathOf(node), expectedSize, limit);

            done++;
            progress?.Report(new DuplicateProgress(done, total));

            if (hash is null) continue;

            if (!byHash.TryGetValue(hash, out List<int>? bucket)) byHash[hash] = bucket = [];
            bucket.Add(node);
        }

        return [.. byHash.Values.Where(b => b.Count > 1)];
    }

    /// <summary>
    /// Liest und hasht — aber nur, wenn die Datei noch so gross ist wie beim Scan. Gruppiert
    /// wurde nach der Groesse von damals; eine seither gewachsene Datei gehoert in keine
    /// dieser Gruppen, und ihre Ersparnis waere erfunden.
    /// </summary>
    private static string? HashOf(string path, long expectedSize, int limit)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                bufferSize: 64 * 1024, FileOptions.SequentialScan);

            if (stream.Length != expectedSize) return null;

            using var sha = SHA256.Create();

            if (limit == int.MaxValue) return Convert.ToHexString(sha.ComputeHash(stream));

            byte[] head = new byte[limit];
            int read = stream.ReadAtLeast(head, limit, throwOnEndOfStream: false);

            return Convert.ToHexString(sha.ComputeHash(head, 0, read));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>Was insgesamt frei wuerde, wenn von jeder Gruppe eine Fassung bliebe.</summary>
    public static long WastedOf(IReadOnlyList<DuplicateGroup> groups) => groups.Sum(g => g.WastedBytes);
}
