using DiskStats.Core.Scanning;
using DiskStats.Core.Storage;

namespace DiskStats.Core.Tests;

/// <summary>
/// Die Namensvergleiche arbeiten auf den UTF-8-Bytes des Pools statt auf Zeichenketten. Das ist
/// nur dann eine Optimierung und kein Fehler, wenn jedes Paar genauso entschieden wird wie bei
/// <c>string.Compare/Equals(.., OrdinalIgnoreCase)</c> - gerade bei Zeichen, die Gross-/Kleinschreibung
/// und Kodierung auf ungewoehnliche Weise mischen.
/// </summary>
public sealed class NameComparisonTests
{
    private static readonly string[] Corpus =
    [
        "a", "A", "ab", "AB", "aB", "abc", "Abd", "b", "B", "_", "[", "a_b", "a[b", "Z", "z", "{", "~",
        "report.txt", "REPORT.TXT", "Report.Txt", "report.txt.bak", "report", "reports",
        "Müller", "MÜLLER", "müller", "Muller", "Mü", "Größe", "GRÖSSE", "größe", "grosse", "straße", "STRASSE",
        "ä", "Ä", "ö", "ü", "é", "É", "e", "E",
        "k", "K", "\u212A", "\u212Aelvin", "kelvin",     // Kelvin-Zeichen
        "i", "I", "\u0131", "\u0130", "\u0131stanbul",    // punktloses ı, İ
        "s", "S", "\u017F", "\u017Fs",                    // langes ſ
        "\u00B5", "\u039C", "\u03BC", "µg", "Μg",         // Mikro-Zeichen und griechisches My
        "Ωmega", "ωmega", "Σίσυφος", "σίσυφος", "ς",
        "日本語", "日本語.txt", "中文", "한국어",
        "😀", "😀.txt", "a😀", "A😀", "😁", "𠀀", "\uFF5E",  // Surrogatpaare und Zeichen ueber dem BMP
        "mit Leerzeichen", "mit  zwei", "tab\there", "-", "--", "0", "00", "9", "10", "2",
        "ünïcödé-Datei ✓.txt", "Ünïcödé-Datei ✓.TXT", "résumé", "RÉSUMÉ", "resume",
    ];

    private static NodeStore Store(IEnumerable<string> names)
    {
        var buffer = new NodeBuffer();
        foreach (string name in names) buffer.AddFile(0, name, 1, 0, EntryFlags.None);
        NodeStore store = NodeStoreBuilder.Build(buffer, @"C:\corpus");
        SizeAggregator.Aggregate(store);
        return store;
    }

    [Fact]
    public void Compare_names_entscheidet_jedes_paar_wie_string_compare()
    {
        NodeStore store = Store(Corpus);
        var problems = new List<string>();

        for (int a = 1; a < store.Count; a++)
        for (int b = 1; b < store.Count; b++)
        {
            string x = store.GetName(a), y = store.GetName(b);
            int expected = Math.Sign(string.Compare(x, y, StringComparison.OrdinalIgnoreCase));
            int actual = Math.Sign(store.CompareNames(a, b));
            if (expected != actual) problems.Add($"'{x}' <> '{y}': erwartet {expected}, geliefert {actual}");
        }

        Assert.True(problems.Count == 0, $"{problems.Count} abweichende Paare, z. B.{Environment.NewLine}"
            + string.Join(Environment.NewLine, problems.Take(10)));
    }

    [Fact]
    public void Compare_names_ist_antisymmetrisch_und_ordnet_transitiv()
    {
        NodeStore store = Store(Corpus);
        int[] nodes = Enumerable.Range(1, store.Count - 1).ToArray();

        // Eine Sortierung mit inkonsistentem Vergleich wirft oder liefert Unsinn - beides soll auffallen.
        int[] sorted = [.. nodes];
        Array.Sort(sorted, store.CompareNames);
        string[] viaStrings = [.. nodes.Select(store.GetName).Order(StringComparer.OrdinalIgnoreCase)];

        for (int i = 1; i < sorted.Length; i++)
            Assert.True(store.CompareNames(sorted[i - 1], sorted[i]) <= 0);
        Assert.Equal(viaStrings.Select(s => s.ToUpperInvariant()), sorted.Select(n => store.GetName(n).ToUpperInvariant()));

        foreach (int a in nodes)
        foreach (int b in nodes)
            Assert.Equal(-Math.Sign(store.CompareNames(a, b)), Math.Sign(store.CompareNames(b, a)));
    }

    [Fact]
    public void Name_equals_entscheidet_jedes_paar_wie_string_equals()
    {
        NodeStore store = Store(Corpus);
        var problems = new List<string>();

        for (int node = 1; node < store.Count; node++)
        foreach (string text in Corpus)
        {
            bool expected = store.GetName(node).Equals(text, StringComparison.OrdinalIgnoreCase);
            if (store.NameEquals(node, text) != expected)
                problems.Add($"'{store.GetName(node)}' == '{text}': erwartet {expected}");
        }

        Assert.True(problems.Count == 0, $"{problems.Count} abweichende Paare, z. B.{Environment.NewLine}"
            + string.Join(Environment.NewLine, problems.Take(10)));
    }

    [Fact]
    public void Find_liefert_dieselben_treffer_wie_der_vergleich_mit_zeichenketten()
    {
        // Ein Ordner "d" mit dem ganzen Korpus; gesucht wird jeder Name in jeder Schreibweise.
        var buffer = new NodeBuffer();
        buffer.AddDirectory(0, 1, "d", 0, EntryFlags.Directory);
        foreach (string name in Corpus.Distinct()) buffer.AddFile(1, name, 1, 0, EntryFlags.None);
        NodeStore store = NodeStoreBuilder.Build(buffer, @"C:\corpus");
        SizeAggregator.Aggregate(store);
        int folder = Enumerable.Range(1, store.Count - 1).Single(i => store.GetName(i) == "d");

        var problems = new List<string>();
        foreach (string text in Corpus)
        foreach (string variant in new[] { text, text.ToUpperInvariant(), text.ToLowerInvariant() })
        {
            int expected = -1;
            for (int i = store.ChildStart[folder]; i < store.ChildStart[folder] + store.ChildCount[folder]; i++)
                if (store.GetName(i).Equals(variant, StringComparison.OrdinalIgnoreCase)) { expected = i; break; }

            int actual = SubtreeRefresh.Find(store, Path.Combine(@"C:\corpus", "d", variant));
            if (expected != actual) problems.Add($"'{variant}': erwartet {expected}, geliefert {actual}");
        }

        Assert.True(problems.Count == 0, $"{problems.Count} abweichende Suchen, z. B.{Environment.NewLine}"
            + string.Join(Environment.NewLine, problems.Take(10)));
    }

    [Fact]
    public void Utf8_overloads_legen_denselben_eintrag_an_wie_die_zeichen_overloads()
    {
        var viaChars = new NodeBuffer();
        var viaBytes = new NodeBuffer();
        foreach (string name in Corpus)
        {
            viaChars.AddFile(0, name, 7, 11, EntryFlags.None, new FileStorageInfo(8, default, 1));
            viaBytes.AddFileUtf8(0, System.Text.Encoding.UTF8.GetBytes(name), 7, 11, EntryFlags.None, new FileStorageInfo(8, default, 1));
        }
        viaChars.AddDirectory(0, 1, "Ordner ✓", 5, EntryFlags.Directory);
        viaBytes.AddDirectoryUtf8(0, 1, System.Text.Encoding.UTF8.GetBytes("Ordner ✓"), 5, EntryFlags.Directory);

        Assert.Equal(viaChars.Entries.Count, viaBytes.Entries.Count);
        Assert.Equal(viaChars.MaxDirectoryId, viaBytes.MaxDirectoryId);
        for (int i = 0; i < viaChars.Entries.Count; i++)
        {
            NodeBuffer.RawEntry a = viaChars.Entries[i], b = viaBytes.Entries[i];
            Assert.Equal(a.NameLength, b.NameLength);
            Assert.True(viaChars.Names.GetBytes(a.NameOffset, a.NameLength)
                .SequenceEqual(viaBytes.Names.GetBytes(b.NameOffset, b.NameLength)));
            Assert.Equal((a.ParentId, a.OwnId, a.Size, a.MTime, a.Flags), (b.ParentId, b.OwnId, b.Size, b.MTime, b.Flags));
        }
    }

    [Fact]
    public void Replace_bewahrt_jeden_namen_auch_mit_sonderzeichen()
    {
        var buffer = new NodeBuffer();
        buffer.AddDirectory(0, 1, "keep", 0, EntryFlags.Directory);
        buffer.AddDirectory(0, 2, "drop", 0, EntryFlags.Directory);
        foreach (string name in Corpus.Distinct())
        {
            buffer.AddFile(1, name, 5, 3, EntryFlags.None);
            buffer.AddFile(2, name, 5, 3, EntryFlags.None);
        }
        buffer.AddDirectory(1, 3, "Unterordner ✓ 😀", 9, EntryFlags.Directory);
        NodeStore original = NodeStoreBuilder.Build(buffer, @"C:\corpus");
        SizeAggregator.Aggregate(original);
        int drop = Enumerable.Range(1, original.Count - 1).Single(i => original.GetName(i) == "drop");

        NodeStore result = SubtreeRefresh.Replace(original, drop, replacement: null);

        string[] expected = [.. Enumerable.Range(1, original.Count - 1)
            .Where(i => original.PathOf(i).StartsWith(@"C:\corpus" + Path.DirectorySeparatorChar + "keep", StringComparison.Ordinal))
            .Select(original.PathOf).Order(StringComparer.Ordinal)];
        string[] actual = [.. Enumerable.Range(1, result.Count - 1).Select(result.PathOf).Order(StringComparer.Ordinal)];

        Assert.Equal(expected, actual);
        Assert.DoesNotContain(actual, p => p.Contains("drop"));
    }
}
