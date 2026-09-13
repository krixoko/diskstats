using DiskStats.Core.Scanning;
using DiskStats.Core.Storage;

namespace DiskStats.Core.Tests;

public class ExtensionStatsTests
{
    private static NodeStore Scan(TestTree tree)
    {
        var buffer = new NodeBuffer();
        DirectoryWalker.Walk(tree.Root, buffer, new WalkOptions { WorkerCount = 2 });
        NodeStore store = NodeStoreBuilder.Build(buffer, tree.Root);
        SizeAggregator.Aggregate(store);
        return store;
    }

    [Theory]
    [InlineData("film.mkv", ".mkv")]
    [InlineData("FILM.MKV", ".mkv")]
    [InlineData("archiv.tar.gz", ".gz")]
    [InlineData("ohne_endung", "")]
    [InlineData(".gitignore", "")]
    [InlineData("punkt.", "")]
    public void Liest_die_endung(string name, string expected)
        => Assert.Equal(expected, ExtensionStats.ExtensionOf(name));

    [Fact]
    public void Summiert_nach_endung_und_sortiert_nach_platz()
    {
        using var tree = new TestTree();
        tree.AddFile("a.mkv", 900).AddFile("b.mkv", 100).AddFile("c.txt", 400);

        IReadOnlyList<ExtensionStats.Entry> stats = ExtensionStats.Of(Scan(tree), 0);

        Assert.Equal(".mkv", stats[0].Extension);
        Assert.Equal(1000, stats[0].Bytes);
        Assert.Equal(2, stats[0].Files);

        Assert.Equal(".txt", stats[1].Extension);
        Assert.Equal(400, stats[1].Bytes);
    }

    [Fact]
    public void Zaehlt_ueber_unterordner_hinweg()
    {
        using var tree = new TestTree();
        tree.AddFile("a.iso", 100).AddFile("tief/tiefer/b.iso", 200);

        IReadOnlyList<ExtensionStats.Entry> stats = ExtensionStats.Of(Scan(tree), 0);

        Assert.Equal(300, stats.Single(e => e.Extension == ".iso").Bytes);
    }

    [Fact]
    public void Erkennt_zugehoerigkeit_fuer_das_hervorheben()
    {
        Assert.True(ExtensionStats.Matches("Urlaub.MKV", ".mkv"));
        Assert.False(ExtensionStats.Matches("Urlaub.mp4", ".mkv"));
    }
}

public class CsvExportTests
{
    private static NodeStore Scan(TestTree tree)
    {
        var buffer = new NodeBuffer();
        DirectoryWalker.Walk(tree.Root, buffer, new WalkOptions { WorkerCount = 2 });
        NodeStore store = NodeStoreBuilder.Build(buffer, tree.Root);
        SizeAggregator.Aggregate(store);
        return store;
    }

    private static string Export(TestTree tree, bool foldersOnly = false)
    {
        var writer = new StringWriter();
        CsvExport.Write(Scan(tree), 0, writer, foldersOnly);
        return writer.ToString();
    }

    [Fact]
    public void Schreibt_kopfzeile_und_eintraege()
    {
        using var tree = new TestTree();
        tree.AddFile("bericht.pdf", 1234);

        string csv = Export(tree);

        Assert.Contains("Path;Name;Type;Size in bytes;Modified", csv);
        Assert.Contains("bericht.pdf;File;1234;", csv);
    }

    [Fact]
    public void Beginnt_mit_der_byte_reihenfolge_marke()
    {
        using var tree = new TestTree();
        tree.AddFile("a.bin", 1);

        // Ohne die Marke liest Excel Umlaute je nach Systemsprache falsch.
        Assert.StartsWith("﻿", Export(tree));
    }

    [Fact]
    public void Setzt_namen_mit_semikolon_in_anfuehrungszeichen()
    {
        using var tree = new TestTree();
        tree.AddFile("teil1; teil2.txt", 10);

        string csv = Export(tree);

        Assert.Contains("\"teil1; teil2.txt\"", csv);
    }

    /// <summary>
    /// Windows verbietet Anfuehrungszeichen in Dateinamen, eine solche Datei laesst sich also
    /// gar nicht anlegen. Der Baum wird deshalb direkt aufgebaut — geprueft wird die
    /// Ausgabeschicht, nicht das Dateisystem. Ueber ein Netzlaufwerk oder einen fremd
    /// erzeugten Snapshot kann ein solcher Name durchaus ankommen.
    /// </summary>
    /// <summary>
    /// Tabellenkalkulationen fuehren Zellen aus, die mit =, +, - oder @ beginnen. Ein Dateiname
    /// "=HYPERLINK(...)" wuerde beim Oeffnen des Exports zur Formel. Solche Zellen bekommen ein
    /// vorangestelltes Hochkomma und Anfuehrungszeichen — Excel zeigt dann den Text.
    /// </summary>
    [Theory]
    [InlineData("=HYPERLINK(\"http://x\")")]
    [InlineData("+1+cmd")]
    [InlineData("-2+3")]
    [InlineData("@SUM(A1)")]
    [InlineData("\tversteckt")]
    public void Entschaerft_zellen_die_eine_tabellenkalkulation_als_formel_laese(string name)
    {
        var buffer = new NodeBuffer();
        buffer.AddFile(0, name, 10, 0, EntryFlags.None);
        NodeStore store = NodeStoreBuilder.Build(buffer, @"C:\test");
        SizeAggregator.Aggregate(store);

        var writer = new StringWriter();
        CsvExport.Write(store, 0, writer);
        string[] lines = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string row = lines[1].TrimEnd('\r');

        // Spalte 2 ist der Name; keine Zelle der Zeile darf mit dem gefaehrlichen Zeichen beginnen.
        foreach (string cell in SplitCsv(row))
        {
            string unquoted = cell.Trim('"');
            Assert.False(unquoted.Length > 0 && "=+-@\t\r".Contains(unquoted[0]),
                $"Zelle beginnt mit Formelzeichen: {cell}");
        }
        Assert.Contains("'" + name.Replace("\"", "\"\""), row);
    }

    private static IEnumerable<string> SplitCsv(string row)
    {
        var current = new System.Text.StringBuilder();
        bool quoted = false;
        foreach (char c in row)
        {
            if (c == '"') { quoted = !quoted; current.Append(c); }
            else if (c == ';' && !quoted) { yield return current.ToString(); current.Clear(); }
            else current.Append(c);
        }
        yield return current.ToString();
    }

    [Fact]
    public void Verdoppelt_anfuehrungszeichen_im_namen()
    {
        var buffer = new NodeBuffer();
        buffer.AddFile(0, "er sagte \"hallo\".txt", 10, 0, EntryFlags.None);

        NodeStore store = NodeStoreBuilder.Build(buffer, @"C:\test");
        SizeAggregator.Aggregate(store);

        var writer = new StringWriter();
        CsvExport.Write(store, 0, writer);

        Assert.Contains("\"er sagte \"\"hallo\"\".txt\"", writer.ToString());
    }

    [Fact]
    public void Kann_sich_auf_ordner_beschraenken()
    {
        using var tree = new TestTree();
        tree.AddFile("ordner/datei.bin", 100);

        string csv = Export(tree, foldersOnly: true);

        Assert.Contains("ordner;Folder;", csv);
        Assert.DoesNotContain("datei.bin", csv);
    }

    [Fact]
    public void Schreibt_jede_datei_genau_einmal()
    {
        using var tree = new TestTree();
        for (int i = 0; i < 12; i++) tree.AddFile($"a{i % 3}/f{i}.bin", i + 1);

        string csv = Export(tree);
        int lines = csv.Split('\n').Count(l => l.Contains(";File;"));

        Assert.Equal(12, lines);
    }
}
