namespace DiskStats.Core.Cleanup;

/// <summary>Wie sich ein Eintrag seit dem Scan veraendert hat.</summary>
public enum DriftKind
{
    /// <summary>
    /// Der Eintrag oder ein Ordner auf dem Weg dorthin ist jetzt eine Verknuepfung, die beim Scan
    /// keine war. Der Scan folgt Verknuepfungen nie — was darunter liegt, wurde dem Benutzer nie
    /// gezeigt. Harter Stopp.
    /// </summary>
    Linked,

    /// <summary>Aus der Datei wurde ein Ordner oder umgekehrt. Harter Stopp.</summary>
    KindChanged,

    /// <summary>Die Datei hat eine andere Groesse oder Aenderungszeit als beim Scan. Rueckfrage.</summary>
    Changed,
}

/// <summary>Eine Abweichung als Tatsache. <paramref name="Path"/> ist der Eintrag oder der verdaechtige Ordner davor.</summary>
public sealed record Drift(DriftKind Kind, string Path);

/// <summary>
/// Prueft unmittelbar vor dem Loeschen, ob die Platte noch das zeigt, was der Benutzer gesehen hat.
///
/// Die Aufraeumliste entsteht aus einem Scan, und zwischen Scan und Papierkorb liegen Minuten:
/// Der Benutzer liest die Liste, ein Programm schreibt weiter, ein Ordner wird durch eine
/// Junction ersetzt. <see cref="ProtectedPaths"/> prueft nur den Pfadtext — und der bleibt gleich,
/// auch wenn er inzwischen anderswohin fuehrt. Wer als Administrator laeuft, loescht dann dort.
///
/// Zwei Stufen, weil nicht jede Abweichung gleich schwer wiegt: Eine Verknuepfung ist ein harter
/// Stopp, eine veraenderte Datei eine Rueckfrage (Ordner nicht — sie aendern sich dauernd, ein
/// Temp-Ordner wuerde jedes Mal warnen, und eine Warnung, die immer kommt, liest niemand).
/// </summary>
public static class Revalidation
{
    public static IReadOnlyList<Drift> Check(
        string scanRoot, IEnumerable<CleanupList.Item> items, CancellationToken cancel = default)
    {
        var found = new List<Drift>();
        foreach (CleanupList.Item item in items)
        {
            cancel.ThrowIfCancellationRequested();
            if (Inspect(scanRoot, item) is { } drift) found.Add(drift);
        }

        return found;
    }

    internal static Drift? Inspect(string scanRoot, CleanupList.Item item)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(item.Path); }
        // Schon weg: Es gibt nichts zu schuetzen, der Papierkorb meldet es selbst.
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        // Nicht lesbar: Hier laesst sich nichts beurteilen; die Shell scheitert dann ohnehin.
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }

        if (LinkAbove(scanRoot, item.Path) is { } link) return new Drift(DriftKind.Linked, link);

        bool isLink = (attributes & FileAttributes.ReparsePoint) != 0;
        if (isLink && !item.WasLink) return new Drift(DriftKind.Linked, item.Path);

        bool isDirectory = (attributes & FileAttributes.Directory) != 0;
        if (isDirectory != item.IsDirectory) return new Drift(DriftKind.KindChanged, item.Path);

        // Ordner: siehe oben. Verknuepfung, die es schon beim Scan war (etwa ein Cloud-Platzhalter):
        // Ihre Groesse sagt nichts ueber die Datei dahinter.
        if (isDirectory || isLink) return null;

        try
        {
            var info = new FileInfo(item.Path);
            bool sizeChanged = info.Length != item.Size;
            bool timeChanged = item.MTimeTicks != 0 && info.LastWriteTimeUtc.Ticks != item.MTimeTicks;
            return sizeChanged || timeChanged ? new Drift(DriftKind.Changed, item.Path) : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Ein Ordner zwischen Scanwurzel und Eintrag, der jetzt eine Verknuepfung ist. Die Wurzel
    /// selbst und alles darueber hat der Benutzer gewaehlt und bleibt unbeanstandet; unterhalb
    /// ist der Scan durch lauter echte Ordner gelaufen.
    /// </summary>
    private static string? LinkAbove(string scanRoot, string path)
    {
        string root = Normalize(scanRoot);

        for (string? dir = Path.GetDirectoryName(path); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (Normalize(dir).Equals(root, StringComparison.OrdinalIgnoreCase)) return null;

            try
            {
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) return dir;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return null;
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar); }
        catch (ArgumentException) { return path.TrimEnd(Path.DirectorySeparatorChar); }
        catch (NotSupportedException) { return path.TrimEnd(Path.DirectorySeparatorChar); }
    }
}
