using DiskStats.Core.Analysis;
using DiskStats.Core.Cleanup;
using DiskStats.Core.Scanning;
using DiskStats.Core.Storage;

namespace DiskStats.Core.Tests;

public class CleanupListTests
{
    private static NodeStore Scan(TestTree tree)
    {
        var buffer = new NodeBuffer();
        DirectoryWalker.Walk(tree.Root, buffer, new WalkOptions { WorkerCount = 2 });
        NodeStore store = NodeStoreBuilder.Build(buffer, tree.Root);
        SizeAggregator.Aggregate(store);
        return store;
    }

    private static int Find(NodeStore store, string name)
        => Enumerable.Range(1, store.Count - 1).First(i => store.GetName(i) == name);

    [Fact]
    public void Nimmt_eine_datei_auf_und_summiert()
    {
        using var tree = new TestTree();
        tree.AddFile("gross.bin", 900).AddFile("klein.bin", 100);

        NodeStore store = Scan(tree);
        var list = new CleanupList();

        Assert.Null(list.Add(store, Find(store, "gross.bin")));
        Assert.Null(list.Add(store, Find(store, "klein.bin")));

        Assert.Equal(2, list.Count);
        Assert.Equal(1000, list.TotalBytes);
    }

    [Fact]
    public void Weist_die_wurzel_des_scans_ab()
    {
        using var tree = new TestTree();
        tree.AddFile("a.bin", 10);

        Assert.NotNull(new CleanupList().Add(Scan(tree), 0));
    }

    /// <summary>
    /// Ein Baum, dessen Namen aus der Scanwurzel hinausfuehren, darf nie in die Aufraeumliste.
    /// Der Snapshot-Leser weist solche Namen inzwischen ab; die Liste verlaesst sich nicht darauf.
    /// </summary>
    [Theory]
    [InlineData("..")]
    [InlineData(@"..\..\Windows")]
    [InlineData(@"C:\Windows")]
    public void Weist_ab_was_die_scanwurzel_verlaesst(string evilName)
    {
        using var tree = new TestTree();
        tree.AddFile("harmlos.bin", 10);
        NodeStore scanned = Scan(tree);

        var pool = new NamePool();
        int offset = pool.Add(evilName);
        var store = new NodeStore
        {
            RootPath = scanned.RootPath,
            Count = 2,
            ParentIndex = [-1, 0],
            ChildStart = [1, 0],
            ChildCount = [1, 0],
            NameOffset = [0, offset],
            NameLength = [0, (ushort)NamePool.Utf8Length(evilName)],
            Size = [10, 10],
            MTime = [0, 0],
            Flags = [EntryFlags.Directory, EntryFlags.None],
            Names = pool,
        };

        Refusal? refusal = new CleanupList().Add(store, 1);

        Assert.NotNull(refusal);
        Assert.Equal(RefusalKind.InvalidPath, refusal.Kind);
    }

    [Fact]
    public void Nimmt_nichts_doppelt_auf()
    {
        using var tree = new TestTree();
        tree.AddFile("a.bin", 10);

        NodeStore store = Scan(tree);
        var list = new CleanupList();
        int node = Find(store, "a.bin");

        Assert.Null(list.Add(store, node));
        Assert.NotNull(list.Add(store, node));
        Assert.Equal(1, list.Count);
    }

    [Fact]
    public void Weist_ab_was_bereits_in_einem_gelisteten_ordner_liegt()
    {
        using var tree = new TestTree();
        tree.AddFile("ordner/datei.bin", 500);

        NodeStore store = Scan(tree);
        var list = new CleanupList();

        Assert.Null(list.Add(store, Find(store, "ordner")));
        Assert.NotNull(list.Add(store, Find(store, "datei.bin")));

        // Sonst waere derselbe Platz zweimal gezaehlt.
        Assert.Equal(500, list.TotalBytes);
    }

    [Fact]
    public void Ein_ordner_ersetzt_seine_bereits_gelisteten_inhalte()
    {
        using var tree = new TestTree();
        tree.AddFile("ordner/datei.bin", 500);

        NodeStore store = Scan(tree);
        var list = new CleanupList();

        Assert.Null(list.Add(store, Find(store, "datei.bin")));
        Assert.Null(list.Add(store, Find(store, "ordner")));

        Assert.Equal(1, list.Count);
        Assert.Equal(500, list.TotalBytes);
    }

    [Fact]
    public void Traegt_geloeschte_groesse_aus_dem_baum_aus()
    {
        using var tree = new TestTree();
        tree.AddFile("bleibt.bin", 100).AddFile("ordner/weg.bin", 900);

        NodeStore store = Scan(tree);
        int gone = Find(store, "weg.bin");
        int folder = store.ParentIndex[gone];

        Assert.Equal(1000, store.Size[0]);

        CleanupList.ApplyRemoval(store, gone);

        Assert.Equal(0, store.Size[gone]);
        Assert.Equal(0, store.Size[folder]);
        Assert.Equal(100, store.Size[0]);
    }

    [Fact]
    public void Entfernt_den_ganzen_teilbaum_aus_suche_statistik_export_und_duplikaten()
    {
        using var tree = new TestTree();
        tree.AddFile("keep.txt", 100).AddFile("gone/deep/bin/a.bin", 900)
            .AddFile("gone/deep/bin/b.bin", 900).AddFile("gone/empty.txt", 0)
            .AddFile("empty-kept.txt", 0);
        NodeStore store = Scan(tree);
        NodeStore before = Scan(tree);
        int gone = Find(store, "gone");
        int descendant = Find(store, "a.bin");
        CleanupList.ApplyRemoval(store, gone);
        CleanupList.ApplyRemoval(store, descendant);
        CleanupList.ApplyRemoval(store, gone); // repeated reconciliation must be harmless

        Assert.Equal(100, store.Size[0]);
        Assert.Equal(100, ExtensionStats.Of(store, 0).Sum(e => e.Bytes));
        Assert.Empty(NodeSearch.Find(store, "a.bin"));
        Assert.Empty(NodeSearch.Find(store, "gone"));
        Assert.Empty(NodeSearch.Find(store, "empty.txt"));
        Assert.Single(NodeSearch.Find(store, "empty-kept.txt"));
        Assert.Empty(QuickWins.Find(store, 0));
        Assert.Empty(DuplicateFinder.Find(store, 0, minSize: 1));
        Assert.Equal(RefusalKind.Removed, new CleanupList().Add(store, descendant)!.Kind);
        using var csv = new StringWriter();
        CsvExport.Write(store, 0, csv);
        Assert.DoesNotContain("gone", csv.ToString());
        Assert.Contains("keep.txt", csv.ToString());
        Assert.Contains(SnapshotDiff.Compare(before, store, minDelta: 1),
            c => c.Name == "gone" && c.Kind == ChangeKind.Removed);
        SizeAggregator.Aggregate(store);
        Assert.Equal(100, store.Size[0]);

        using var snapshot = new MemoryStream();
        SnapshotFormat.Write(store, snapshot);
        snapshot.Position = 0;
        NodeStore restored = SnapshotFormat.Read(snapshot);
        Assert.True(restored.IsRemoved(descendant));
        Assert.Empty(NodeSearch.Find(restored, "a.bin"));
    }

    /// <summary>
    /// Fuenfzig Eintraege einzeln zu entfernen hiess frueher fuenfzigmal ueber den ganzen Baum
    /// zu aggregieren. Gebuendelt wird einmal markiert und einmal gerechnet — mit demselben Ergebnis.
    /// </summary>
    [Fact]
    public void Gebuendeltes_entfernen_liefert_dasselbe_wie_einzelnes()
    {
        using var tree = new TestTree();
        for (int i = 0; i < 12; i++) tree.AddFile($"o{i % 3}/f{i}.bin", 100 + i);

        NodeStore single = Scan(tree);
        NodeStore batched = Scan(tree);
        int[] victims = [Find(single, "f1.bin"), Find(single, "f5.bin"), Find(single, "o2")];

        foreach (int v in victims) CleanupList.ApplyRemoval(single, v);
        CleanupList.ApplyRemovals(batched, victims);

        Assert.Equal(single.Size, batched.Size);
        Assert.Equal(single.Flags, batched.Flags);
        Assert.Equal(single.PhysicalSize, batched.PhysicalSize);
        Assert.Equal(single.FileCount, batched.FileCount);
    }

    [Fact]
    public void Groessenabgleich_findet_eintraege_ueber_den_pfad()
    {
        using var tree = new TestTree();
        tree.AddFile("ordner/datei.bin", 500);
        NodeStore before = Scan(tree);
        var list = new CleanupList();
        Assert.Null(list.Add(before, Find(before, "ordner")));

        File.WriteAllBytes(tree.PathOf("ordner/datei.bin"), new byte[200]);
        NodeStore after = Scan(tree);
        list.RefreshSizes(after);

        Assert.Equal(200, list.TotalBytes);
    }

    [Fact]
    public void Entfernt_auch_leere_ordner_und_dateien()
    {
        using var tree = new TestTree();
        tree.AddFile("empty/file.txt", 0);
        NodeStore store = Scan(tree);
        CleanupList.ApplyRemoval(store, Find(store, "empty"));
        Assert.Empty(NodeSearch.Find(store, "file"));
        Assert.True(store.IsRemoved(Find(store, "empty")));
        Assert.Equal(0, store.Size[0]);
    }
}
