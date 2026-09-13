using DiskStats.Core.Scanning;
using DiskStats.Core.Storage;

namespace DiskStats.Core.Tests;

public class SizeAggregatorTests
{
    private static NodeStore ScanAndAggregate(TestTree tree)
    {
        var buffer = new NodeBuffer();
        DirectoryWalker.Walk(tree.Root, buffer, new WalkOptions { WorkerCount = 4 });
        var store = NodeStoreBuilder.Build(buffer, tree.Root);
        SizeAggregator.Aggregate(store);
        return store;
    }

    [Fact]
    public void Wurzel_traegt_die_summe_aller_dateien()
    {
        using var tree = new TestTree();
        tree.AddFile("a.bin", 100).AddFile("sub/b.bin", 200).AddFile("sub/tief/c.bin", 300);

        var store = ScanAndAggregate(tree);

        Assert.Equal(600, store.Size[0]);
    }

    /// <summary>
    /// Die Kopfzeile zeigt Datei- und Ordnerzahl unter dem betrachteten Knoten. Sie bei jeder
    /// Navigation neu zu zaehlen kostete bei zwei Millionen Knoten Zehntelsekunden auf dem
    /// UI-Thread — deshalb liegen die Zahlen fertig im Baum.
    /// </summary>
    [Fact]
    public void Fuehrt_datei_und_ordnerzahl_je_knoten_mit()
    {
        using var tree = new TestTree();
        tree.AddFile("a.bin", 1).AddFile("sub/b.bin", 1).AddFile("sub/tief/c.bin", 1).AddDirectory("leer");

        var store = ScanAndAggregate(tree);

        int sub = Enumerable.Range(0, store.Count).Single(i => store.GetName(i) == "sub");
        Assert.Equal(3, store.FileCount[0]);
        Assert.Equal(3, store.FolderCount[0]);   // sub, tief, leer
        Assert.Equal(2, store.FileCount[sub]);
        Assert.Equal(1, store.FolderCount[sub]);
    }

    [Fact]
    public void Entfernte_eintraege_zaehlen_nicht_mehr()
    {
        using var tree = new TestTree();
        tree.AddFile("a.bin", 1).AddFile("sub/b.bin", 1).AddFile("sub/tief/c.bin", 1);

        var store = ScanAndAggregate(tree);
        int sub = Enumerable.Range(0, store.Count).Single(i => store.GetName(i) == "sub");

        DiskStats.Core.Cleanup.CleanupList.ApplyRemoval(store, sub);

        Assert.Equal(1, store.FileCount[0]);
        Assert.Equal(0, store.FolderCount[0]);
    }

    [Fact]
    public void Ordner_traegt_nur_die_summe_seines_teilbaums()
    {
        using var tree = new TestTree();
        tree.AddFile("aussen.bin", 1000).AddFile("sub/b.bin", 200).AddFile("sub/tief/c.bin", 300);

        var store = ScanAndAggregate(tree);

        int sub = Enumerable.Range(0, store.Count).Single(i => store.GetName(i) == "sub");
        Assert.Equal(500, store.Size[sub]);
    }

    [Fact]
    public void Stimmt_mit_naiv_rekursiver_berechnung_ueberein()
    {
        using var tree = new TestTree();
        for (int i = 0; i < 40; i++)
            tree.AddFile($"a{i % 3}/b{i % 5}/f{i}.bin", i + 1);

        var store = ScanAndAggregate(tree);

        for (int i = 0; i < store.Count; i++)
            Assert.Equal(NaiveSum(store, i), store.Size[i]);
    }

    /// <summary>Unabhaengige Referenzimplementierung — absichtlich langsam und offensichtlich korrekt.</summary>
    private static long NaiveSum(NodeStore store, int index)
    {
        if (!store.IsDirectory(index)) return store.Size[index];

        long sum = 0;
        int start = store.ChildStart[index];
        for (int c = start; c < start + store.ChildCount[index]; c++)
            sum += NaiveSum(store, c);
        return sum;
    }

    [Fact]
    public void Tiefer_baum_verursacht_keinen_stack_overflow()
    {
        using var tree = new TestTree();
        string deep = string.Join('/', Enumerable.Range(0, 200).Select(i => "d" + i));
        tree.AddFile(deep + "/tief.bin", 42);

        var store = ScanAndAggregate(tree);

        Assert.Equal(42, store.Size[0]);
    }
}
