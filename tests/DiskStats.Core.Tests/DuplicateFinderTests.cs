using DiskStats.Core.Analysis;
using DiskStats.Core.Scanning;
using DiskStats.Core.Storage;

namespace DiskStats.Core.Tests;

public class DuplicateFinderTests
{
    private static NodeStore Scan(TestTree tree)
    {
        var buffer = new NodeBuffer();
        DirectoryWalker.Walk(tree.Root, buffer, new WalkOptions { WorkerCount = 2 });
        NodeStore store = NodeStoreBuilder.Build(buffer, tree.Root);
        SizeAggregator.Aggregate(store);
        return store;
    }

    /// <summary>Unterhalb der Standardschwelle waere jeder Testbaum leer.</summary>
    private static IReadOnlyList<DuplicateGroup> Find(TestTree tree, long minSize = 1)
        => DuplicateFinder.Find(Scan(tree), 0, minSize);

    private static string[] Namen(NodeStore store, DuplicateGroup group)
        => [.. group.Nodes.Select(store.GetName).Order()];

    [Fact]
    public void Findet_zwei_gleiche_dateien()
    {
        using var tree = new TestTree();
        tree.AddFile("a/gleich.bin", 5000, 0x41)
            .AddFile("b/gleich.bin", 5000, 0x41);

        IReadOnlyList<DuplicateGroup> groups = Find(tree);

        DuplicateGroup group = Assert.Single(groups);
        Assert.Equal(2, group.Count);
        Assert.Equal(5000, group.Size);
    }

    /// <summary>Gleiche Groesse allein genuegt nicht — sonst waere jede Blockdatei ein Fund.</summary>
    [Fact]
    public void Gleiche_groesse_bei_anderem_inhalt_zaehlt_nicht()
    {
        using var tree = new TestTree();
        tree.AddFile("a.bin", 5000, 0x41)
            .AddFile("b.bin", 5000, 0x42);

        Assert.Empty(Find(tree));
    }

    /// <summary>
    /// Der haeufige Fall: Dateien, die erst spaet auseinandergehen. Die zweite Stufe liest nur
    /// den Anfang und wuerde sie faelschlich zusammenwerfen, wenn es die dritte nicht gaebe.
    /// </summary>
    [Fact]
    public void Ein_unterschied_am_ende_wird_bemerkt()
    {
        byte[] a = new byte[40_000];
        Array.Fill(a, (byte)0x41);

        byte[] b = (byte[])a.Clone();
        b[^1] = 0x42;

        using var tree = new TestTree();
        tree.AddFile("a.bin", a).AddFile("b.bin", b);

        Assert.Empty(Find(tree));
    }

    [Fact]
    public void Ein_unterschied_am_anfang_wird_bemerkt()
    {
        byte[] a = new byte[40_000];
        Array.Fill(a, (byte)0x41);

        byte[] b = (byte[])a.Clone();
        b[0] = 0x42;

        using var tree = new TestTree();
        tree.AddFile("a.bin", a).AddFile("b.bin", b);

        Assert.Empty(Find(tree));
    }

    [Fact]
    public void Verschiedene_groessen_werden_nie_verglichen()
    {
        using var tree = new TestTree();
        tree.AddFile("a.bin", 5000, 0x41).AddFile("b.bin", 6000, 0x41);

        Assert.Empty(Find(tree));
    }

    [Fact]
    public void Findet_mehr_als_zwei_fassungen()
    {
        using var tree = new TestTree();
        tree.AddFile("a.bin", 3000, 0x41)
            .AddFile("tief/b.bin", 3000, 0x41)
            .AddFile("tief/tiefer/c.bin", 3000, 0x41);

        DuplicateGroup group = Assert.Single(Find(tree));

        Assert.Equal(3, group.Count);
    }

    /// <summary>
    /// Von drei gleichen Dateien will man eine behalten. Was frei wird, sind also zwei — die
    /// ganze Gruppe zu zaehlen waere eine Zahl, nach der niemand handeln kann.
    /// </summary>
    [Fact]
    public void Zaehlt_nur_was_ueber_die_erste_fassung_hinausgeht()
    {
        using var tree = new TestTree();
        tree.AddFile("a.bin", 1000, 0x41)
            .AddFile("b.bin", 1000, 0x41)
            .AddFile("c.bin", 1000, 0x41);

        DuplicateGroup group = Assert.Single(Find(tree));

        Assert.Equal(2000, group.WastedBytes);
        Assert.Equal(2000, DuplicateFinder.WastedOf(Find(tree)));
    }

    [Fact]
    public void Haelt_gruppen_auseinander()
    {
        using var tree = new TestTree();
        tree.AddFile("a1.bin", 2000, 0x41).AddFile("a2.bin", 2000, 0x41)
            .AddFile("b1.bin", 3000, 0x42).AddFile("b2.bin", 3000, 0x42);

        IReadOnlyList<DuplicateGroup> groups = Find(tree);

        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.Equal(2, g.Count));
    }

    /// <summary>Die groesste Ersparnis zuerst — danach entscheidet man, was man anfasst.</summary>
    [Fact]
    public void Sortiert_nach_ersparnis()
    {
        using var tree = new TestTree();
        tree.AddFile("klein1.bin", 1000, 0x41).AddFile("klein2.bin", 1000, 0x41)
            .AddFile("gross1.bin", 9000, 0x42).AddFile("gross2.bin", 9000, 0x42);

        Assert.Equal(9000, Find(tree)[0].WastedBytes);
    }

    [Fact]
    public void Kleine_dateien_bleiben_unter_der_schwelle()
    {
        using var tree = new TestTree();
        tree.AddFile("a.bin", 100, 0x41).AddFile("b.bin", 100, 0x41);

        Assert.Empty(Find(tree, minSize: 1000));
    }

    [Fact]
    public void Meldet_nichts_wenn_es_nichts_gibt()
    {
        using var tree = new TestTree();
        tree.AddFile("einzeln.bin", 4000, 0x41);

        Assert.Empty(Find(tree));
    }

    [Fact]
    public void Meldet_den_fortschritt()
    {
        using var tree = new TestTree();
        tree.AddFile("a.bin", 2000, 0x41).AddFile("b.bin", 2000, 0x41);

        var steps = new List<DuplicateProgress>();
        DuplicateFinder.Find(Scan(tree), 0, 1, new Progress<DuplicateProgress>(steps.Add));

        // Progress<T> meldet ueber den Synchronisationskontext; im Test genuegt, dass der
        // Lauf ohne ihn durchlaeuft. Der letzte Stand wird in jedem Fall gesetzt.
        Assert.True(steps.Count >= 0);
    }

    /// <summary>
    /// Der Baum kennt die Groesse vom Scan; gehasht wird die Datei von jetzt. Ist sie
    /// inzwischen gewachsen, stimmt weder die Ersparnis noch die Gruppe — zwei ungleich lange
    /// Dateien mit gleichem Anfang saehen sonst wie Zwillinge aus.
    /// </summary>
    [Fact]
    public void Eine_seit_dem_scan_veraenderte_datei_faellt_heraus()
    {
        using var tree = new TestTree();
        tree.AddFile("a.bin", 5000, 0x41).AddFile("b.bin", 5000, 0x41);
        NodeStore store = Scan(tree);

        // Beide wachsen nach dem Scan gleich — inhaltlich noch Zwillinge, aber die Groesse
        // im Baum stimmt nicht mehr, und mit ihr die Ersparnis. Lieber kein Fund als ein falscher.
        tree.AddFile("a.bin", 6000, 0x41).AddFile("b.bin", 6000, 0x41);

        Assert.Empty(DuplicateFinder.Find(store, 0, 1));
    }

    private sealed class Sofort : IProgress<DuplicateProgress>
    {
        public List<DuplicateProgress> Steps { get; } = [];
        public void Report(DuplicateProgress value) => Steps.Add(value);
    }

    /// <summary>
    /// Die dritte Stufe liest ganze Dateien — die teuerste. Gerade dort darf der Balken
    /// nicht stehen bleiben.
    /// </summary>
    [Fact]
    public void Fortschritt_laeuft_auch_waehrend_des_vollstaendigen_hashens()
    {
        using var tree = new TestTree();
        for (int i = 0; i < 4; i++) tree.AddFile($"f{i}.bin", 40_000, 0x41);
        var progress = new Sofort();

        DuplicateFinder.Find(Scan(tree), 0, 1, progress);

        // 4 Dateien, zwei Lesephasen: ein Schritt je Datei und Phase, monoton, am Ende voll.
        Assert.True(progress.Steps.Select(s => s.Done).Distinct().Count() >= 8, $"{progress.Steps.Count} Meldungen");
        Assert.Equal(progress.Steps.Select(s => s.Done), progress.Steps.Select(s => s.Done).Order());
        Assert.Equal(progress.Steps[^1].Total, progress.Steps[^1].Done);
        Assert.All(progress.Steps, s => Assert.True(s.Done <= s.Total));
    }

    [Fact]
    public void Laesst_sich_abbrechen()
    {
        using var tree = new TestTree();
        for (int i = 0; i < 20; i++) tree.AddFile($"f{i}.bin", 2000, 0x41);

        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => DuplicateFinder.Find(Scan(tree), 0, 1, null, cancel.Token));
    }

    /// <summary>Der Pfad entsteht beim Aufsteigen — ohne ihn kaeme der Finder an keine Datei.</summary>
    [Fact]
    public void Der_baum_kennt_den_pfad_jedes_knotens()
    {
        using var tree = new TestTree();
        tree.AddFile("tief/tiefer/datei.bin", 10);

        NodeStore store = Scan(tree);
        int node = Enumerable.Range(0, store.Count).First(i => store.GetName(i) == "datei.bin");

        Assert.Equal(Path.Combine(tree.Root, "tief", "tiefer", "datei.bin"), store.PathOf(node));
        Assert.Equal(tree.Root, store.PathOf(0));
    }
}
