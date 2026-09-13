using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DiskStats.Core.Storage;

/// <summary>
/// Legt Scans ab und holt sie zurueck.
///
/// Zwei Zwecke. Der erste ist die kalte Wartezeit: Denselben Ordner ein zweites Mal zu
/// durchlaufen kostet Minuten, ihn aus einem Snapshot zu lesen unter einer Sekunde. Der
/// zweite kam spaeter dazu und aendert die Ablage: Um zu zeigen, was seit dem letzten Mal
/// gewachsen ist, muss es ein letztes Mal geben. Je Pfad wird deshalb eine Reihe gefuehrt,
/// nicht eine Datei.
///
/// Da jeder Snapshot eines grossen Laufwerks rund dreissig Megabyte wiegt, braucht die Ablage
/// selbst eine Grenze — ein Werkzeug gegen Plattenmuell darf keinen anhaeufen.
/// </summary>
public static class SnapshotStore
{
    /// <summary>So viele Staende je Pfad, die mittleren fallen zuerst. Siehe <see cref="Thin"/>.</summary>
    public const int MaxPerPath = 6;

    public const int RetentionMonths = 12;

    /// <summary>
    /// Harte Obergrenze ueber alles. Wer viele grosse Laufwerke scannt, koennte sonst
    /// mehrere Gigabyte ansammeln, ohne es je zu bemerken.
    /// </summary>
    public const long MaxTotalBytes = 400L * 1024 * 1024;

    private const string Extension = ".dss";
    private const string Stamp = "yyyyMMddHHmmss";

    public static string Folder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiskStats", "snapshots");

    public readonly record struct Entry(string Path, string Key, DateTime Written, long Bytes);

    /// <summary>
    /// Der Teil des Dateinamens, der den Pfad benennt: ein Hash, weil ein Pfad Zeichen
    /// enthaelt, die in Dateinamen nicht vorkommen duerfen, und beliebig lang sein kann.
    /// </summary>
    public static string KeyOf(string scanRoot)
    {
        byte[] hash = SHA256.HashData(
            Encoding.UTF8.GetBytes(scanRoot.TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant()));

        return Convert.ToHexString(hash)[..24].ToLowerInvariant();
    }

    /// <summary>
    /// Dateiname aus Pfad und Zeitpunkt. Der Zeitstempel steht hinten und ist sortierbar —
    /// dadurch ergibt die alphabetische Ordnung der Dateinamen die zeitliche Reihenfolge.
    /// </summary>
    public static string FileFor(string scanRoot, DateTime moment)
        => Path.Combine(Folder,
            $"{KeyOf(scanRoot)}-{moment.ToString(Stamp, CultureInfo.InvariantCulture)}{Extension}");

    public static bool Save(NodeStore store) => Save(store, DateTime.Now);

    public static bool Save(NodeStore store, DateTime moment)
    {
        try
        {
            Directory.CreateDirectory(Folder);

            // Erst daneben schreiben, dann umbenennen: ein Abbruch mitten im Schreiben
            // hinterlaesst sonst einen halben Snapshot, der beim Lesen scheitert.
            string target = FileFor(store.RootPath, moment);
            string temporary = target + ".part";

            using (var file = File.Create(temporary)) SnapshotFormat.Write(store, file);
            File.Move(temporary, target, overwrite: true);

            Prune(moment);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Der juengste Stand eines Pfads.</summary>
    public static NodeStore? TryLoad(string scanRoot, out DateTime written)
    {
        written = default;

        // Ein beschaedigter Stand wird verworfen und der naechstaeltere genommen — sonst
        // scheiterte jeder Start an derselben Datei, und der Verlauf dahinter bliebe unsichtbar.
        foreach (Entry entry in History(scanRoot))
        {
            NodeStore? store = Load(entry.Path, out bool damaged);
            if (store is not null)
            {
                written = entry.Written;
                return store;
            }
            if (damaged) TryDelete(entry.Path);
        }
        return null;
    }

    /// <summary>Ein bestimmter Stand.</summary>
    public static NodeStore? Load(string file) => Load(file, out _);

    private static NodeStore? Load(string file, out bool damaged)
    {
        damaged = false;
        try
        {
            using var stream = File.OpenRead(file);
            return SnapshotFormat.Read(stream);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (InvalidDataException) { damaged = true; return null; }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Alle Staende eines Pfads, der juengste zuerst.</summary>
    public static IReadOnlyList<Entry> History(string scanRoot)
    {
        string key = KeyOf(scanRoot);
        return [.. List().Where(e => e.Key == key)];
    }

    public static IReadOnlyList<Entry> List()
    {
        try
        {
            if (!Directory.Exists(Folder)) return [];

            var entries = new List<Entry>();
            foreach (string path in Directory.EnumerateFiles(Folder, "*" + Extension))
            {
                var info = new FileInfo(path);
                string name = Path.GetFileNameWithoutExtension(path);

                // Der Zeitstempel steht im Namen, nicht in den Dateizeiten: Ein Kopieren des
                // Ordners wuerde die Dateizeiten aendern, den Namen nicht.
                int dash = name.LastIndexOf('-');
                if (dash < 0) continue;

                if (!DateTime.TryParseExact(name[(dash + 1)..], Stamp,
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime written))
                    continue;

                entries.Add(new Entry(path, name[..dash], written, info.Length));
            }

            entries.Sort((a, b) => b.Written.CompareTo(a.Written));
            return entries;
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    /// <summary>
    /// Raeumt auf. Gibt die Zahl der geloeschten Dateien zurueck, damit sich das Verhalten
    /// pruefen laesst.
    /// </summary>
    public static int Prune(DateTime now)
    {
        IReadOnlyList<Entry> entries = List();
        DateTime cutoff = now.AddMonths(-RetentionMonths);

        var doomed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Entry entry in entries)
            if (entry.Written < cutoff) doomed.Add(entry.Path);

        // Je Pfad ausduennen
        foreach (IGrouping<string, Entry> group in entries
                     .Where(e => !doomed.Contains(e.Path))
                     .GroupBy(e => e.Key))
        {
            foreach (string path in Thin([.. group], MaxPerPath)) doomed.Add(path);
        }

        // Ueber alles: das Aelteste faellt, bis die Ablage in ihr Budget passt
        long total = entries.Where(e => !doomed.Contains(e.Path)).Sum(e => e.Bytes);

        foreach (Entry entry in entries.Where(e => !doomed.Contains(e.Path)).Reverse())
        {
            if (total <= MaxTotalBytes) break;
            doomed.Add(entry.Path);
            total -= entry.Bytes;
        }

        int removed = 0;
        foreach (string path in doomed)
        {
            try { File.Delete(path); removed++; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // Halbe Schreibvorgaenge eines abgestuerzten Laufs mitnehmen.
        try
        {
            foreach (string leftover in Directory.EnumerateFiles(Folder, "*.part"))
            {
                if (File.GetLastWriteTime(leftover) > now.AddHours(-1)) continue;
                File.Delete(leftover);
                removed++;
            }
        }
        // DirectoryNotFoundException leitet von IOException ab und ist mit abgedeckt.
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return removed;
    }

    /// <summary>
    /// Duennt eine Reihe auf <paramref name="keep"/> Staende aus und gibt zurueck, was weg darf.
    ///
    /// Nicht einfach die aeltesten wegzuwerfen ist der ganze Witz: Wer taeglich scannt, haette
    /// sonst nach sechs Tagen eine Woche Verlauf und koennte nie beantworten, was sich in
    /// einem Monat getan hat. Stattdessen faellt jeweils der Stand, dessen Wegfall die
    /// kleinste Luecke reisst — der juengste und der aelteste bleiben immer.
    /// </summary>
    internal static List<string> Thin(List<Entry> series, int keep)
    {
        var dropped = new List<string>();
        if (series.Count <= keep) return dropped;

        // Aufsteigend nach Zeit, damit "Nachbar" etwas bedeutet.
        series.Sort((a, b) => a.Written.CompareTo(b.Written));

        while (series.Count > keep)
        {
            int worst = 1;
            TimeSpan smallest = TimeSpan.MaxValue;

            for (int i = 1; i < series.Count - 1; i++)
            {
                TimeSpan gap = series[i + 1].Written - series[i - 1].Written;
                if (gap >= smallest) continue;

                smallest = gap;
                worst = i;
            }

            dropped.Add(series[worst].Path);
            series.RemoveAt(worst);
        }

        return dropped;
    }
}
