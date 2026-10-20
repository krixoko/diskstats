using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace DiskStats.Core.Diagnostics;

/// <summary>
/// Ein knappes Fehlerprotokoll mit monatlichen Dateien.
///
/// Bewusst nur Fehler und Warnungen, kein Mitschreiben des Betriebs: Ein Werkzeug, das
/// jahrelang auf einem Rechner liegt, darf nicht unbemerkt Platz fressen — ausgerechnet
/// dieses hier waere ein schlechter Scherz. Bei reinen Fehlermeldungen bleibt das Protokoll
/// auch nach Jahren im Kilobyte-Bereich.
///
/// Aufbewahrung: zwoelf Monate, zusaetzlich eine harte Obergrenze fuer den Fall, dass ein
/// Fehler in Dauerschleife auftritt.
/// </summary>
public static class DiagnosticLog
{
    public const int RetentionMonths = 12;

    /// <summary>Reissleine gegen einen Fehler, der sich endlos wiederholt.</summary>
    public const long MaxTotalBytes = 4L * 1024 * 1024;

    private const string Prefix = "diskstats-";
    private const string Suffix = ".log";

    /// <summary>
    /// Laenger wird ein einzelner Eintrag nicht. Ein Stacktrace mit tiefer Ursachenkette kann
    /// Kilobytes fuellen, und ein Fehler in Dauerschleife soll die Obergrenze nicht in
    /// wenigen Aufrufen erreichen.
    /// </summary>
    public const int MaxEntryChars = 8 * 1024;

    private static readonly Lock Gate = new();

    /// <summary>Die Datei, in die in dieser Sitzung zuletzt die Kopfzeile geschrieben wurde.</summary>
    private static string? _headedFile;

    public static string Folder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiskStats", "logs");

    public static void Write(string message, Exception? error = null)
    {
        var line = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
            .Append("  ")
            .Append(message);

        // Die Kurzfassung bleibt in der ersten Zeile (greppbar), der Rest steht darunter:
        // Ohne Stacktrace und Ursachenkette laesst sich aus einem Fehlerbericht nichts ableiten.
        if (error is not null)
        {
            line.Append("  |  ").Append(error.GetType().Name).Append(": ").Append(Summary(error.Message));
            line.AppendLine().Append(Indent(Truncate(error.ToString())));
        }

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                string file = FileFor(DateTime.Now);

                // Je Sitzung und Datei einmal: Welche Fassung, welches System. Ein Bericht ohne
                // Versionsnummer ist Raten.
                var text = new StringBuilder();
                if (!string.Equals(_headedFile, file, StringComparison.OrdinalIgnoreCase))
                {
                    text.AppendLine(SessionHeader());
                    _headedFile = file;
                }

                File.AppendAllText(file, text.Append(line).AppendLine().ToString(), Encoding.UTF8);
            }
        }
        catch (IOException) { /* ein Protokoll darf niemals der Grund eines Absturzes sein */ }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Kopfzeile einer Sitzung: Anwendung, System, Laufzeit.</summary>
    internal static string SessionHeader()
    {
        string version = typeof(DiagnosticLog).Assembly.GetName().Version?.ToString(3) ?? "?";

        return $"=== DiskStats {version} | {RuntimeInformation.OSDescription} | "
            + $"{RuntimeInformation.ProcessArchitecture} | {RuntimeInformation.FrameworkDescription} ===";
    }

    /// <summary>Die Kurzzeile bleibt kurz, auch wenn eine Meldung eine ganze Datei enthaelt.</summary>
    private static string Summary(string message)
    {
        string oneLine = message.ReplaceLineEndings(" ");
        return oneLine.Length <= 400 ? oneLine : oneLine[..400] + "...";
    }

    private static string Truncate(string text)
        => text.Length <= MaxEntryChars ? text : text[..MaxEntryChars] + Environment.NewLine + "[... gekuerzt]";

    private static string Indent(string text)
        => "    " + text.ReplaceLineEndings(Environment.NewLine + "    ");

    public static string FileFor(DateTime moment)
        => Path.Combine(Folder, $"{Prefix}{moment:yyyy-MM}{Suffix}");

    /// <summary>
    /// Raeumt auf: Was aelter ist als die Aufbewahrungsfrist, faellt weg; bleibt es dennoch zu
    /// gross, faellt zusaetzlich das jeweils Aelteste. Gibt die Zahl der geloeschten Dateien
    /// zurueck, damit sich das Verhalten pruefen laesst.
    /// </summary>
    public static int Prune(DateTime now)
    {
        try
        {
            if (!Directory.Exists(Folder)) return 0;

            var files = new List<(string Path, DateTime Month, long Size)>();
            foreach (string path in Directory.EnumerateFiles(Folder, $"{Prefix}*{Suffix}"))
            {
                if (!TryParseMonth(path, out DateTime month)) continue;
                files.Add((path, month, new FileInfo(path).Length));
            }

            DateTime oldestKept = new DateTime(now.Year, now.Month, 1).AddMonths(-(RetentionMonths - 1));
            int removed = 0;

            for (int i = files.Count - 1; i >= 0; i--)
            {
                if (files[i].Month >= oldestKept) continue;
                if (!TryDelete(files[i].Path)) continue;

                files.RemoveAt(i);
                removed++;
            }

            // Obergrenze: das Aelteste zuerst, das laufende Monatsprotokoll zuletzt. Die Alters-
            // schleife hat "files" bereits verkleinert — gezaehlt wird hier deshalb neu, sonst
            // bricht die Schleife zu frueh ab und das Budget bleibt ueberschritten.
            files.Sort((a, b) => a.Month.CompareTo(b.Month));
            long total = files.Sum(f => f.Size);
            int remaining = files.Count;

            foreach ((string path, _, long size) in files)
            {
                if (total <= MaxTotalBytes || remaining <= 1) break;
                if (!TryDelete(path)) continue;

                total -= size;
                remaining--;
                removed++;
            }

            return removed;
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    private static bool TryDelete(string path)
    {
        try { File.Delete(path); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Liest den Monat aus dem Dateinamen — er ist die Ordnung dieses Protokolls.</summary>
    private static bool TryParseMonth(string path, out DateTime month)
    {
        month = default;
        string name = Path.GetFileNameWithoutExtension(path);
        if (!name.StartsWith(Prefix, StringComparison.Ordinal)) return false;

        return DateTime.TryParseExact(
            name[Prefix.Length..], "yyyy-MM",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out month);
    }
}
