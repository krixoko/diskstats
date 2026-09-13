using System.Globalization;
using System.Text;

namespace DiskStats.Core.Storage;

/// <summary>
/// Schreibt einen Teilbaum als CSV.
///
/// Der meistgenannte Wunsch bei WinDirStat, den es dort bis heute nicht gibt. Wer aufraeumt,
/// will das Ergebnis oft weiterreichen oder ueber Zeitraeume vergleichen — dafuer braucht es
/// eine Datei, die eine Tabellenkalkulation liest.
///
/// Getrennt wird mit Semikolon und geschrieben mit BOM: Excel liest eine UTF-8-Datei sonst
/// je nach Systemsprache falsch, und Komma-getrennte Spalten zerfallen dort, wo das Komma
/// das Dezimalzeichen ist.
/// </summary>
/// <summary>
/// Die Spaltentexte. Sie werden hereingereicht statt hier festgelegt, damit die Datei in der
/// Sprache herauskommt, die der Nutzer eingestellt hat — die kennt nur die Oberflaeche.
/// </summary>
public sealed record CsvLabels(IReadOnlyList<string> Header, string Folder, string File)
{
    public static readonly CsvLabels English =
        new(["Path", "Name", "Type", "Size in bytes", "Modified"], "Folder", "File");
}

public static class CsvExport
{
    public static void Write(
        NodeStore store, int root, TextWriter writer,
        bool foldersOnly = false, CsvLabels? labels = null)
    {
        labels ??= CsvLabels.English;

        writer.Write('﻿');
        writer.WriteLine(string.Join(';', labels.Header));

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
                bool directory = store.IsDirectory(i);
                if (directory) stack.Push(i);
                else if (foldersOnly) continue;

                if (store.Size[i] <= 0 && !directory) continue;

                writer.Write(Quote(store.PathOf(i)));
                writer.Write(';');
                writer.Write(Quote(store.GetName(i)));
                writer.Write(';');
                writer.Write(directory ? labels.Folder : labels.File);
                writer.Write(';');
                writer.Write(store.Size[i].ToString(CultureInfo.InvariantCulture));
                writer.Write(';');
                writer.WriteLine(store.MTime[i] > 0
                    ? new DateTime(store.MTime[i], DateTimeKind.Utc).ToLocalTime()
                        .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                    : string.Empty);
            }
        }
    }

    public static void WriteFile(
        NodeStore store, int root, string path, bool foldersOnly = false, CsvLabels? labels = null)
    {
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(false));
        Write(store, root, writer, foldersOnly, labels);
    }

    /// <summary>
    /// Setzt in Anfuehrungszeichen, wo es sein muss. Dateinamen duerfen Semikolon,
    /// Anfuehrungszeichen und Zeilenumbrueche enthalten — ohne Behandlung verrutschen
    /// dadurch alle folgenden Spalten.
    /// </summary>
    private static string Quote(string value)
    {
        // Tabellenkalkulationen fuehren Zellen aus, die mit =, +, - oder @ beginnen — ein
        // Dateiname "=HYPERLINK(...)" wuerde beim Oeffnen zur Formel. Ein vorangestelltes
        // Hochkomma macht daraus Text; es ist die von Excel und LibreOffice verstandene Form.
        bool defused = value.Length > 0 && "=+-@\t\r".Contains(value[0]);
        if (defused) value = "'" + value;

        bool needs = defused || value.AsSpan().IndexOfAny(";\"\n\r".AsSpan()) >= 0;
        if (!needs) return value;

        return '"' + value.Replace("\"", "\"\"") + '"';
    }
}
