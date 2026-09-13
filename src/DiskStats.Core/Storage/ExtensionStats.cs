namespace DiskStats.Core.Storage;

/// <summary>
/// Was welche Dateiendung belegt.
///
/// Der Grund, warum WinDirStat seit zwanzig Jahren benutzt wird: Acht grobe Kategorien sagen
/// "Video frisst 60 GB", aber nicht, ob es .mkv oder .iso ist. Die Endung ist die Ebene, auf
/// der man tatsaechlich entscheidet — sie trennt Wegwerfbares von Unersetzlichem innerhalb
/// derselben Kategorie.
/// </summary>
public static class ExtensionStats
{
    public readonly record struct Entry(string Extension, long Bytes, int Files);

    /// <summary>Laenger als das ist keine Endung mehr, sondern ein Teil des Namens.</summary>
    private const int MaxLength = 12;

    public static IReadOnlyList<Entry> Of(NodeStore store, int root, int max = 40)
    {
        var byExtension = new Dictionary<string, (long Bytes, int Files)>(StringComparer.Ordinal);
        var stack = new Stack<int>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            int node = stack.Pop();
            int start = store.ChildStart[node];
            int count = store.ChildCount[node];

            for (int i = start; i < start + count; i++)
            {
                if (store.IsRemoved(i)) continue;
                if (store.IsDirectory(i)) { stack.Push(i); continue; }

                long size = store.Size[i];
                if (size <= 0) continue;

                string extension = ExtensionOf(store.GetName(i));
                byExtension.TryGetValue(extension, out (long Bytes, int Files) sum);
                byExtension[extension] = (sum.Bytes + size, sum.Files + 1);
            }
        }

        return byExtension
            .Select(e => new Entry(e.Key, e.Value.Bytes, e.Value.Files))
            .OrderByDescending(e => e.Bytes)
            .Take(max)
            .ToList();
    }

    /// <summary>
    /// Die Endung in Kleinschreibung, mit Punkt. Dateien ohne Endung bekommen einen eigenen
    /// Eintrag statt zu verschwinden — bei Datenbanken und Abbildern ist das oft die
    /// entscheidende Gruppe.
    /// </summary>
    /// <summary>
    /// Dateien ohne brauchbare Endung. Die leere Zeichenkette statt eines Wortes: Der Wert
    /// wird gruppiert und verglichen, und ein Wort waere in einer Sprache geschrieben.
    /// Beschriftet wird er erst in der Oberflaeche.
    /// </summary>
    public const string NoExtension = "";

    public static string ExtensionOf(string name)
    {
        int dot = name.LastIndexOf('.');

        // Ein Punkt am Anfang macht eine versteckte Datei, keine Endung: ".gitignore".
        if (dot <= 0 || dot == name.Length - 1) return NoExtension;

        string extension = name[dot..];
        return extension.Length > MaxLength ? NoExtension : extension.ToLowerInvariant();
    }

    /// <summary>Passt eine Datei zu dieser Endung? Grundlage fuer das Hervorheben in der Karte.</summary>
    public static bool Matches(string name, string extension)
        => string.Equals(ExtensionOf(name), extension, StringComparison.Ordinal);
}
