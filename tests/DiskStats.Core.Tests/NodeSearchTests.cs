using DiskStats.Core.Scanning;
using DiskStats.Core.Storage;

namespace DiskStats.Core.Tests;

public class NodeSearchTests
{
    private static NodeStore Scan(TestTree tree)
    {
        var buffer = new NodeBuffer();
        DirectoryWalker.Walk(tree.Root, buffer, new WalkOptions { WorkerCount = 2 });
        NodeStore store = NodeStoreBuilder.Build(buffer, tree.Root);
        SizeAggregator.Aggregate(store);
        return store;
    }

    private static string[] Names(NodeStore store, IReadOnlyList<NodeSearch.Hit> hits)
        => hits.Select(h => store.GetName(h.Node)).ToArray();

    [Fact]
    public void Findet_teiltreffer_im_namen()
    {
        using var tree = new TestTree();
        tree.AddFile("bericht-2026.pdf", 100).AddFile("notiz.txt", 50);

        NodeStore store = Scan(tree);

        Assert.Equal(["bericht-2026.pdf"], Names(store, NodeSearch.Find(store, "richt")));
    }

    [Fact]
    public void Unterscheidet_nicht_zwischen_gross_und_klein()
    {
        using var tree = new TestTree();
        tree.AddFile("Bericht.PDF", 100);

        NodeStore store = Scan(tree);

        Assert.Single(NodeSearch.Find(store, "bericht"));
        Assert.Single(NodeSearch.Find(store, "PDF"));
        Assert.Single(NodeSearch.Find(store, "pdf"));
    }

    [Fact]
    public void Findet_auch_ordner_nicht_nur_dateien()
    {
        using var tree = new TestTree();
        tree.AddFile("node_modules/paket.js", 10);

        NodeStore store = Scan(tree);

        Assert.Contains("node_modules", Names(store, NodeSearch.Find(store, "node_mod")));
    }

    [Fact]
    public void Liefert_die_groessten_treffer_zuerst()
    {
        using var tree = new TestTree();
        tree.AddFile("daten-klein.bin", 10)
            .AddFile("daten-gross.bin", 9000)
            .AddFile("daten-mittel.bin", 500);

        NodeStore store = Scan(tree);
        string[] found = Names(store, NodeSearch.Find(store, "daten"));

        Assert.Equal(["daten-gross.bin", "daten-mittel.bin", "daten-klein.bin"], found);
    }

    [Fact]
    public void Haelt_die_obergrenze_ein_und_behaelt_die_groessten()
    {
        using var tree = new TestTree();
        for (int i = 1; i <= 40; i++) tree.AddFile($"treffer-{i}.bin", i * 100);

        NodeStore store = Scan(tree);
        IReadOnlyList<NodeSearch.Hit> hits = NodeSearch.Find(store, "treffer", max: 5);

        Assert.Equal(5, hits.Count);
        Assert.Equal(4000, hits[0].Size);
        Assert.Equal(3600, hits[^1].Size);
    }

    [Fact]
    public void Findet_umlaute()
    {
        using var tree = new TestTree();
        tree.AddFile("Größenübersicht.xlsx", 100).AddFile("anderes.txt", 50);

        NodeStore store = Scan(tree);

        Assert.Single(NodeSearch.Find(store, "übersicht"));
    }

    [Fact]
    public void Leerer_begriff_liefert_nichts()
    {
        using var tree = new TestTree();
        tree.AddFile("a.bin", 10);

        NodeStore store = Scan(tree);

        Assert.Empty(NodeSearch.Find(store, ""));
        Assert.Empty(NodeSearch.Find(store, "   "));
    }
}
