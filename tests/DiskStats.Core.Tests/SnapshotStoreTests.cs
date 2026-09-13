using DiskStats.Core.Scanning;
using DiskStats.Core.Storage;

namespace DiskStats.Core.Tests;

/// <summary>
/// Snapshots sind gross. Ohne Grenze waere die Ablage nach einem Jahr Gebrauch selbst der
/// groesste Posten auf der Platte — bei diesem Werkzeug besonders peinlich.
/// </summary>
public class SnapshotStoreTests : IDisposable
{
    private readonly string _folder;
    private readonly string _previous;

    public SnapshotStoreTests()
    {
        _previous = SnapshotStore.Folder;
        _folder = Path.Combine(Path.GetTempPath(), "diskstats-snap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
        SnapshotStore.Folder = _folder;
    }

    public void Dispose()
    {
        SnapshotStore.Folder = _previous;
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private static NodeStore Build(TestTree tree)
    {
        var buffer = new NodeBuffer();
        DirectoryWalker.Walk(tree.Root, buffer, new WalkOptions { WorkerCount = 2 });
        NodeStore store = NodeStoreBuilder.Build(buffer, tree.Root);
        SizeAggregator.Aggregate(store);
        return store;
    }

    /// <summary>Legt einen Stand mit gegebenem Zeitpunkt ab, ohne wirklich zu scannen.</summary>
    private static void Given(string scanRoot, DateTime written, int bytes = 16)
        => File.WriteAllBytes(SnapshotStore.FileFor(scanRoot, written), new byte[bytes]);

    [Fact]
    public void Beschaedigter_juengster_stand_wird_verworfen_und_der_naechste_genommen()
    {
        using var tree = new TestTree();
        tree.AddFile("a.bin", 500);
        NodeStore good = Build(tree);
        Assert.True(SnapshotStore.Save(good, new DateTime(2026, 9, 1, 12, 0, 0)));

        // Ein spaeterer, aber kaputter Stand: darf nicht bei jedem Start erneut scheitern.
        string broken = SnapshotStore.FileFor(tree.Root, new DateTime(2026, 9, 2, 12, 0, 0));
        File.WriteAllBytes(broken, "DSKS-nicht-wirklich"u8.ToArray());

        NodeStore? loaded = SnapshotStore.TryLoad(tree.Root, out DateTime written);

        Assert.NotNull(loaded);
        Assert.Equal(new DateTime(2026, 9, 1, 12, 0, 0), written);
        Assert.False(File.Exists(broken), "die beschaedigte Datei muss weg sein");
    }

    [Fact]
    public void Legt_einen_scan_ab_und_holt_ihn_zurueck()
    {
        using var tree = new TestTree();
        tree.AddFile("a.bin", 500).AddFile("sub/b.bin", 250);

        NodeStore original = Build(tree);
        Assert.True(SnapshotStore.Save(original));

        NodeStore? loaded = SnapshotStore.TryLoad(tree.Root, out DateTime written);

        Assert.NotNull(loaded);
        Assert.Equal(original.Count, loaded.Count);
        Assert.Equal(original.Size[0], loaded.Size[0]);
        Assert.Equal(original.RootPath, loaded.RootPath);
        Assert.True(written > DateTime.Now.AddMinutes(-1));
    }

    [Fact]
    public void Liefert_nichts_fuer_einen_unbekannten_pfad()
    {
        Assert.Null(SnapshotStore.TryLoad(@"C:\gibt-es-hier-nicht", out _));
    }

    [Fact]
    public void Derselbe_pfad_ergibt_denselben_schluessel_unabhaengig_von_schreibweise()
    {
        Assert.Equal(SnapshotStore.KeyOf(@"C:\Users\Test"), SnapshotStore.KeyOf(@"c:\users\test\"));
    }

    /// <summary>
    /// Der Zeitstempel steht im Dateinamen, nicht in den Dateizeiten: Wer den Ordner kopiert,
    /// aendert die Dateizeiten, nicht die Namen — und verlaere sonst seinen ganzen Verlauf.
    /// </summary>
    [Fact]
    public void Der_zeitpunkt_steht_im_namen()
    {
        var moment = new DateTime(2026, 3, 7, 14, 30, 5);
        Given(@"C:\test", moment);

        SnapshotStore.Entry entry = Assert.Single(SnapshotStore.History(@"C:\test"));

        Assert.Equal(moment, entry.Written);
    }

    // ---------- Verlauf je Pfad ----------

    [Fact]
    public void Fuehrt_je_pfad_eine_reihe()
    {
        var now = new DateTime(2026, 9, 15);
        Given(@"C:\a", now);
        Given(@"C:\a", now.AddDays(-1));
        Given(@"C:\b", now);

        Assert.Equal(2, SnapshotStore.History(@"C:\a").Count);
        Assert.Single(SnapshotStore.History(@"C:\b"));
    }

    [Fact]
    public void Der_juengste_stand_kommt_zuerst()
    {
        var now = new DateTime(2026, 9, 15);
        Given(@"C:\a", now.AddDays(-3));
        Given(@"C:\a", now);
        Given(@"C:\a", now.AddDays(-1));

        Assert.Equal(now, SnapshotStore.History(@"C:\a")[0].Written);
    }

    [Fact]
    public void Behaelt_je_pfad_hoechstens_die_vereinbarte_zahl()
    {
        var now = new DateTime(2026, 9, 15);
        for (int i = 0; i < SnapshotStore.MaxPerPath + 4; i++)
            Given(@"C:\viel", now.AddDays(-i));

        SnapshotStore.Prune(now);

        Assert.Equal(SnapshotStore.MaxPerPath, SnapshotStore.History(@"C:\viel").Count);
    }

    /// <summary>
    /// Der eigentliche Zweck des Ausduennens: Wer taeglich scannt, soll trotzdem sagen
    /// koennen, was sich in einem Monat getan hat. Die aeltesten einfach wegzuwerfen wuerde
    /// den Verlauf auf sechs Tage stutzen.
    /// </summary>
    [Fact]
    public void Behaelt_beim_ausduennen_die_spannweite()
    {
        var now = new DateTime(2026, 9, 15);
        for (int i = 0; i < 30; i++) Given(@"C:\taeglich", now.AddDays(-i));

        SnapshotStore.Prune(now);
        IReadOnlyList<SnapshotStore.Entry> left = SnapshotStore.History(@"C:\taeglich");

        Assert.Equal(SnapshotStore.MaxPerPath, left.Count);
        Assert.Equal(now, left[0].Written);
        Assert.Equal(now.AddDays(-29), left[^1].Written);
    }

    [Fact]
    public void Verschiedene_pfade_stehen_sich_nicht_im_weg()
    {
        var now = new DateTime(2026, 9, 15);
        for (int i = 0; i < 20; i++) Given($@"C:\ordner{i}", now.AddMinutes(-i));

        SnapshotStore.Prune(now);

        Assert.Equal(20, Directory.GetFiles(_folder, "*.dss").Length);
    }

    /// <summary>Sonst sammelte sich unbemerkt Platz an, den dieses Werkzeug anprangern soll.</summary>
    [Fact]
    public void Haelt_die_ablage_im_budget()
    {
        var now = new DateTime(2026, 9, 15);
        int stueck = (int)(SnapshotStore.MaxTotalBytes / 4);

        for (int i = 0; i < 6; i++) Given($@"C:\gross{i}", now.AddDays(-i), stueck);

        SnapshotStore.Prune(now);

        long total = SnapshotStore.List().Sum(e => e.Bytes);
        Assert.True(total <= SnapshotStore.MaxTotalBytes, $"{total} Bytes uebrig");
    }

    [Fact]
    public void Verwirft_was_aelter_ist_als_die_frist()
    {
        var now = new DateTime(2026, 9, 15);
        Given(@"C:\frisch", now.AddMonths(-2));
        Given(@"C:\alt", now.AddMonths(-14));

        SnapshotStore.Prune(now);

        Assert.Single(SnapshotStore.History(@"C:\frisch"));
        Assert.Empty(SnapshotStore.History(@"C:\alt"));
    }

    [Fact]
    public void Raeumt_haelften_eines_abgebrochenen_schreibvorgangs_weg()
    {
        var now = new DateTime(2026, 9, 15);
        string half = Path.Combine(_folder, "abcdef.dss.part");
        File.WriteAllBytes(half, new byte[8]);
        File.SetLastWriteTime(half, now.AddHours(-5));

        Assert.True(SnapshotStore.Prune(now) > 0);
        Assert.False(File.Exists(half));
    }

    [Fact]
    public void Laesst_einen_gerade_laufenden_schreibvorgang_in_ruhe()
    {
        var now = new DateTime(2026, 9, 15);
        string half = Path.Combine(_folder, "abcdef.dss.part");
        File.WriteAllBytes(half, new byte[8]);
        File.SetLastWriteTime(half, now.AddMinutes(-5));

        SnapshotStore.Prune(now);

        Assert.True(File.Exists(half));
    }
}
