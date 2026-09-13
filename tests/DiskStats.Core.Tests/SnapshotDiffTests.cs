using DiskStats.Core.Analysis;
using DiskStats.Core.Scanning;
using DiskStats.Core.Storage;

namespace DiskStats.Core.Tests;

public class SnapshotDiffTests
{
    /// <summary>
    /// Baut einen Baum aus Pfadangaben, ohne das Dateisystem zu bemuehen. Ordner entstehen
    /// aus den Pfaden mit; ein Pfad ohne Groesse ist ein leerer Ordner.
    /// </summary>
    private static NodeStore Tree(params (string Path, long Size)[] files) => Tree(@"C:\test", files);

    private static NodeStore Tree(string root, params (string Path, long Size)[] files)
    {
        var buffer = new NodeBuffer();
        var folders = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [""] = 0 };
        int next = 1;

        foreach ((string path, long size) in files)
        {
            string[] parts = path.Split('/');
            string sofar = "";
            int parent = 0;

            for (int i = 0; i < parts.Length - 1; i++)
            {
                string deeper = sofar.Length == 0 ? parts[i] : sofar + "/" + parts[i];

                if (!folders.TryGetValue(deeper, out int id))
                {
                    id = next++;
                    buffer.AddDirectory(parent, id, parts[i], 0, EntryFlags.Directory);
                    folders[deeper] = id;
                }

                parent = id;
                sofar = deeper;
            }

            buffer.AddFile(parent, parts[^1], size, 0, EntryFlags.None);
        }

        NodeStore store = NodeStoreBuilder.Build(buffer, root);
        SizeAggregator.Aggregate(store);
        return store;
    }

    private const long M = 1024 * 1024;

    private static IReadOnlyList<Change> Compare(NodeStore a, NodeStore b, long min = M)
        => SnapshotDiff.Compare(a, b, min);

    /// <summary>
    /// Zwei Staende verschiedener Ordner haben nichts gemeinsam — der Vergleich meldete
    /// jeden Eintrag als neu und jeden als verschwunden. Lieber klar ablehnen.
    /// </summary>
    [Fact]
    public void Weist_staende_verschiedener_wurzeln_ab()
    {
        NodeStore a = Tree(("a.bin", 10 * M));

        Assert.Throws<ArgumentException>(() => Compare(a, Tree(@"D:\anders", ("a.bin", 30 * M))));

        // Schreibweise und Schraegstrich am Ende sind derselbe Ordner.
        Assert.Single(Compare(a, Tree(@"c:\TEST\", ("a.bin", 30 * M))));
    }

    [Fact]
    public void Bemerkt_eine_gewachsene_datei()
    {
        Change change = Assert.Single(Compare(
            Tree(("a.bin", 10 * M)),
            Tree(("a.bin", 30 * M))));

        Assert.Equal("a.bin", change.Name);
        Assert.Equal(ChangeKind.Grown, change.Kind);
        Assert.Equal(20 * M, change.Delta);
    }

    [Fact]
    public void Bemerkt_eine_geschrumpfte_datei()
    {
        Change change = Assert.Single(Compare(
            Tree(("a.bin", 30 * M)),
            Tree(("a.bin", 10 * M))));

        Assert.Equal(ChangeKind.Shrunk, change.Kind);
        Assert.Equal(-20 * M, change.Delta);
    }

    [Fact]
    public void Bemerkt_was_dazugekommen_ist()
    {
        Change change = Assert.Single(Compare(
            Tree(("a.bin", 5 * M)),
            Tree(("a.bin", 5 * M), ("neu.bin", 20 * M))));

        Assert.Equal("neu.bin", change.Name);
        Assert.Equal(ChangeKind.Added, change.Kind);
        Assert.Equal(0, change.Before);
    }

    [Fact]
    public void Bemerkt_was_verschwunden_ist()
    {
        Change change = Assert.Single(Compare(
            Tree(("a.bin", 5 * M), ("weg.bin", 20 * M)),
            Tree(("a.bin", 5 * M))));

        Assert.Equal("weg.bin", change.Name);
        Assert.Equal(ChangeKind.Removed, change.Kind);
        Assert.Equal(0, change.After);
    }

    [Fact]
    public void Uebergeht_was_unter_der_schwelle_bleibt()
    {
        Assert.Empty(Compare(
            Tree(("log.txt", 1000)),
            Tree(("log.txt", 2000))));
    }

    [Fact]
    public void Meldet_nichts_wenn_sich_nichts_getan_hat()
    {
        Assert.Empty(Compare(
            Tree(("a.bin", 10 * M), ("tief/b.bin", 5 * M)),
            Tree(("a.bin", 10 * M), ("tief/b.bin", 5 * M))));
    }

    [Fact]
    public void Findet_aenderungen_in_der_tiefe()
    {
        Change change = Assert.Single(Compare(
            Tree(("a/b/c/tief.bin", 5 * M)),
            Tree(("a/b/c/tief.bin", 50 * M))));

        Assert.Equal("tief.bin", change.Name);
        Assert.Equal(Path.Combine("a", "b", "c", "tief.bin"), change.Path);
    }

    /// <summary>
    /// Der Kern der Sache: Ohne diese Regel staende dieselbe Zahl fuenfmal untereinander,
    /// einmal je Ebene von "a" bis hinunter zur Datei — und die Liste waere unbrauchbar.
    /// </summary>
    [Fact]
    public void Nennt_nur_die_stelle_die_es_erklaert()
    {
        IReadOnlyList<Change> changes = Compare(
            Tree(("a/b/c/tief.bin", 5 * M)),
            Tree(("a/b/c/tief.bin", 50 * M)));

        Assert.Single(changes);
        Assert.Equal("tief.bin", changes[0].Name);
    }

    /// <summary>
    /// Umgekehrt: Wachsen zwei Kinder, erklaert keines allein den Ordner. Dann ist der Ordner
    /// die nuetzlichere Auskunft und bleibt stehen.
    /// </summary>
    [Fact]
    public void Nennt_den_ordner_wenn_mehrere_kinder_wachsen()
    {
        IReadOnlyList<Change> changes = Compare(
            Tree(("cache/a.bin", 1), ("cache/b.bin", 1)),
            Tree(("cache/a.bin", 20 * M), ("cache/b.bin", 30 * M)));

        Assert.Contains(changes, c => c.Name == "cache" && c.IsDirectory);
        Assert.Equal(3, changes.Count);
    }

    [Fact]
    public void Sortiert_nach_groesse_der_aenderung()
    {
        IReadOnlyList<Change> changes = Compare(
            Tree(("klein.bin", 1), ("gross.bin", 1)),
            Tree(("klein.bin", 5 * M), ("gross.bin", 90 * M)));

        Assert.Equal("gross.bin", changes[0].Name);
    }

    /// <summary>Ein Schwund zaehlt so viel wie ein Zuwachs — beides will man sehen.</summary>
    [Fact]
    public void Ordnet_schwund_und_zuwachs_nach_betrag()
    {
        IReadOnlyList<Change> changes = Compare(
            Tree(("weg.bin", 90 * M), ("neu.bin", 1)),
            Tree(("weg.bin", 1), ("neu.bin", 20 * M)));

        Assert.Equal("weg.bin", changes[0].Name);
    }

    [Fact]
    public void Nennt_die_gesamtaenderung()
    {
        NodeStore before = Tree(("a.bin", 10 * M));
        NodeStore after = Tree(("a.bin", 10 * M), ("b.bin", 25 * M));

        Assert.Equal(25 * M, SnapshotDiff.TotalDelta(before, after));
    }

    /// <summary>Aus einer Datei wird ein Ordner gleichen Namens — selten, aber kein Absturz.</summary>
    [Fact]
    public void Vertraegt_einen_wechsel_von_datei_zu_ordner()
    {
        IReadOnlyList<Change> changes = Compare(
            Tree(("ding", 30 * M)),
            Tree(("ding/drin.bin", 30 * M)));

        Assert.NotNull(changes);
    }

    [Fact]
    public void Vertraegt_zwei_leere_baeume()
    {
        var buffer = new NodeBuffer();
        NodeStore leer = NodeStoreBuilder.Build(buffer, @"C:\leer");
        SizeAggregator.Aggregate(leer);

        Assert.Empty(SnapshotDiff.Compare(leer, leer));
    }
}
