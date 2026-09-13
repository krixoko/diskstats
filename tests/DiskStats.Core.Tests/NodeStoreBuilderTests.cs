using DiskStats.Core.Scanning;
using DiskStats.Core.Storage;

namespace DiskStats.Core.Tests;

public class NodeStoreBuilderTests
{
    private static NodeStore Scan(TestTree tree)
    {
        var buffer = new NodeBuffer();
        DirectoryWalker.Walk(tree.Root, buffer, new WalkOptions { WorkerCount = 4 });
        return NodeStoreBuilder.Build(buffer, tree.Root);
    }

    [Fact]
    public void Wurzel_hat_index_null_und_keinen_elternknoten()
    {
        using var tree = new TestTree();
        tree.AddFile("a.bin", 10);

        var store = Scan(tree);

        Assert.Equal(-1, store.ParentIndex[0]);
        Assert.Equal(tree.Root, store.RootPath);
    }

    /// <summary>
    /// Ein Eintrag mit einer Eltern-Id, die es nicht gibt, ist ein Programmfehler im Zulieferer.
    /// Der soll benannt werden — nicht als IndexOutOfRange irgendwo im Counting Sort auftauchen.
    /// </summary>
    [Fact]
    public void Eltern_id_ausserhalb_des_baums_wird_klar_benannt()
    {
        var buffer = new NodeBuffer();
        buffer.AddFile(0, "ok.txt", 1, 0, EntryFlags.None);
        buffer.AddFile(7, "verwaist.txt", 1, 0, EntryFlags.None);   // Verzeichnis 7 wurde nie angelegt

        var ex = Assert.Throws<InvalidOperationException>(() => NodeStoreBuilder.Build(buffer, @"C:\fixture"));
        Assert.Contains("7", ex.Message);
        Assert.Contains("verwaist.txt", ex.Message);

        var negative = new NodeBuffer();
        negative.AddFile(-1, "x.txt", 1, 0, EntryFlags.None);
        Assert.Throws<InvalidOperationException>(() => NodeStoreBuilder.Build(negative, @"C:\fixture"));
    }

    [Fact]
    public void Enthaelt_jeden_eintrag_genau_einmal()
    {
        using var tree = new TestTree();
        tree.AddFile("a.bin", 10).AddFile("sub/b.bin", 20).AddFile("sub/c.bin", 30);

        var store = Scan(tree);

        // Wurzel + sub + drei Dateien
        Assert.Equal(5, store.Count);
    }

    [Fact]
    public void Kinder_liegen_zusammenhaengend_und_verweisen_zurueck_auf_den_elternknoten()
    {
        using var tree = new TestTree();
        tree.AddFile("a.bin", 1).AddFile("b.bin", 2).AddFile("c.bin", 3);

        var store = Scan(tree);

        int start = store.ChildStart[0];
        int count = store.ChildCount[0];
        Assert.Equal(3, count);

        for (int i = start; i < start + count; i++)
            Assert.Equal(0, store.ParentIndex[i]);
    }

    [Fact]
    public void Jeder_kindindex_ist_groesser_als_der_elternindex()
    {
        using var tree = new TestTree();
        for (int i = 0; i < 30; i++)
            tree.AddFile($"a{i % 3}/b{i % 4}/f{i}.bin", i + 1);

        var store = Scan(tree);

        // Diese Eigenschaft ist die Voraussetzung fuer den Rueckwaertslauf im SizeAggregator.
        for (int i = 1; i < store.Count; i++)
            Assert.True(store.ParentIndex[i] < i,
                $"Knoten {i} hat Elternindex {store.ParentIndex[i]} — nicht kleiner als er selbst.");
    }

    [Fact]
    public void Behaelt_namen_und_groessen_der_dateien()
    {
        using var tree = new TestTree();
        tree.AddFile("bericht.pdf", 4242);

        var store = Scan(tree);

        int index = Enumerable.Range(0, store.Count).Single(i => store.GetName(i) == "bericht.pdf");
        Assert.Equal(4242, store.Size[index]);
        Assert.Equal(EntryFlags.None, store.Flags[index] & EntryFlags.Directory);
    }

    [Fact]
    public void Markiert_verzeichnisse_als_solche()
    {
        using var tree = new TestTree();
        tree.AddDirectory("unterordner");

        var store = Scan(tree);

        int index = Enumerable.Range(0, store.Count).Single(i => store.GetName(i) == "unterordner");
        Assert.True(store.Flags[index].HasFlag(EntryFlags.Directory));
    }
}

public class DisplayNameTests
{
    private static NodeStore Scan(TestTree tree)
    {
        var buffer = new NodeBuffer();
        DirectoryWalker.Walk(tree.Root, buffer, new WalkOptions { WorkerCount = 2 });
        NodeStore store = NodeStoreBuilder.Build(buffer, tree.Root);
        SizeAggregator.Aggregate(store);
        return store;
    }

    [Fact]
    public void Nennt_eintraege_bei_ihrem_namen()
    {
        using var tree = new TestTree();
        tree.AddFile("ordner/datei.bin", 10);

        NodeStore store = Scan(tree);
        int node = Enumerable.Range(0, store.Count).First(i => store.GetName(i) == "datei.bin");

        Assert.Equal("datei.bin", store.DisplayName(node));
    }

    /// <summary>Die Wurzel hat keinen Eintrag im Namensvorrat — sie kommt aus dem Pfad.</summary>
    [Fact]
    public void Benennt_die_wurzel_nach_dem_letzten_pfadteil()
    {
        using var tree = new TestTree();
        tree.AddFile("a.bin", 1);

        NodeStore store = Scan(tree);

        Assert.Equal(string.Empty, store.GetName(0));
        Assert.Equal(Path.GetFileName(tree.Root), store.DisplayName(0));
    }

    /// <summary>Eine Laufwerkswurzel hat keinen letzten Teil — dann gilt der Pfad selbst.</summary>
    [Fact]
    public void Faellt_bei_einer_laufwerkswurzel_auf_den_pfad_zurueck()
    {
        var buffer = new NodeBuffer();
        buffer.AddFile(0, "a.bin", 1, 0, EntryFlags.None);

        NodeStore store = NodeStoreBuilder.Build(buffer, @"C:\");
        SizeAggregator.Aggregate(store);

        Assert.Equal(@"C:\", store.DisplayName(0));
    }
}
