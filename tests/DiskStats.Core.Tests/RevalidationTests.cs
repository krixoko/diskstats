using DiskStats.Core.Cleanup;
using DiskStats.Core.Scanning;
using DiskStats.Core.Storage;

namespace DiskStats.Core.Tests;

/// <summary>
/// Zwischen Scan und Papierkorb vergeht Zeit. Der Pfadtext bleibt dabei gleich, das Ziel kann
/// sich aendern — eine Junction, eine neu geschriebene Datei. Diese Tests pruefen die Abweichungen
/// gegen eine echte Platte.
/// </summary>
public sealed class RevalidationTests
{
    private static CleanupList.Item FileItem(TestTree tree, string relative)
    {
        string path = tree.PathOf(relative);
        var info = new FileInfo(path);
        return new CleanupList.Item(path, info.Name, info.Length, false, info.LastWriteTimeUtc.Ticks);
    }

    private static CleanupList.Item FolderItem(TestTree tree, string relative)
    {
        string path = tree.PathOf(relative);
        return new CleanupList.Item(path, Path.GetFileName(path), 0, true,
            Directory.GetLastWriteTimeUtc(path).Ticks);
    }

    private static bool TryLink(string link, string target, bool directory)
    {
        try
        {
            if (directory) Directory.CreateSymbolicLink(link, target);
            else File.CreateSymbolicLink(link, target);
            return true;
        }
        // Unter Windows braucht das Anlegen Rechte (Entwicklermodus oder Administrator).
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    [Fact]
    public void Unveraenderte_eintraege_haben_keine_abweichung()
    {
        using var tree = new TestTree().AddFile("a/file.bin", 100).AddDirectory("a/sub");

        var drift = Revalidation.Check(tree.Root,
            [FileItem(tree, "a/file.bin"), FolderItem(tree, "a/sub")]);

        Assert.Empty(drift);
    }

    [Fact]
    public void Fehlende_eintraege_sind_keine_abweichung()
    {
        using var tree = new TestTree().AddFile("a/file.bin", 100);
        CleanupList.Item item = FileItem(tree, "a/file.bin");
        File.Delete(item.Path);

        Assert.Empty(Revalidation.Check(tree.Root, [item]));
    }

    [Fact]
    public void Geaenderte_groesse_wird_gemeldet()
    {
        using var tree = new TestTree().AddFile("a/file.bin", 100);
        CleanupList.Item item = FileItem(tree, "a/file.bin");
        File.WriteAllBytes(item.Path, new byte[250]);

        Drift drift = Assert.Single(Revalidation.Check(tree.Root, [item]));

        Assert.Equal(DriftKind.Changed, drift.Kind);
        Assert.Equal(item.Path, drift.Path);
    }

    [Fact]
    public void Gleiche_groesse_aber_neue_aenderungszeit_wird_gemeldet()
    {
        using var tree = new TestTree().AddFile("a/file.bin", 100);
        CleanupList.Item item = FileItem(tree, "a/file.bin");
        File.SetLastWriteTimeUtc(item.Path, new DateTime(item.MTimeTicks, DateTimeKind.Utc).AddHours(-3));

        Assert.Equal(DriftKind.Changed, Assert.Single(Revalidation.Check(tree.Root, [item])).Kind);
    }

    [Fact]
    public void Ohne_bekannte_zeit_zaehlt_nur_die_groesse()
    {
        using var tree = new TestTree().AddFile("a/file.bin", 100);
        CleanupList.Item item = FileItem(tree, "a/file.bin") with { MTimeTicks = 0 };
        File.SetLastWriteTimeUtc(item.Path, DateTime.UtcNow.AddDays(-9));

        Assert.Empty(Revalidation.Check(tree.Root, [item]));
    }

    [Fact]
    public void Ordner_werden_nicht_an_ihrer_aenderungszeit_gemessen()
    {
        // Ein Temp-Ordner aendert sich dauernd. Eine Warnung, die immer kommt, liest niemand.
        using var tree = new TestTree().AddDirectory("temp");
        CleanupList.Item item = FolderItem(tree, "temp");
        File.WriteAllBytes(tree.PathOf("temp/new.tmp"), new byte[10]);
        Directory.SetLastWriteTimeUtc(item.Path, DateTime.UtcNow.AddDays(-30));

        Assert.Empty(Revalidation.Check(tree.Root, [item]));
    }

    [Fact]
    public void Aus_einer_datei_wurde_ein_ordner_und_umgekehrt()
    {
        using var tree = new TestTree().AddFile("x.bin", 10).AddDirectory("y");
        CleanupList.Item file = FileItem(tree, "x.bin");
        CleanupList.Item folder = FolderItem(tree, "y");
        File.Delete(file.Path);
        Directory.CreateDirectory(file.Path);
        Directory.Delete(folder.Path);
        File.WriteAllBytes(folder.Path, new byte[3]);

        var drift = Revalidation.Check(tree.Root, [file, folder]);

        Assert.Equal(2, drift.Count);
        Assert.All(drift, d => Assert.Equal(DriftKind.KindChanged, d.Kind));
    }

    [Fact]
    public void Ein_ordner_auf_dem_weg_wurde_zur_verknuepfung()
    {
        using var tree = new TestTree().AddFile("real/target/inner.bin", 10).AddDirectory("elsewhere");
        CleanupList.Item item = FileItem(tree, "real/target/inner.bin");
        // Der Scan lief durch einen echten Ordner "real"; jetzt steht dort eine Verknuepfung.
        string moved = tree.PathOf("elsewhere/real");
        Directory.Move(tree.PathOf("real"), moved);
        Assert.SkipUnless(TryLink(tree.PathOf("real"), moved, directory: true),
            "Verknuepfungen anlegen braucht Rechte");

        Drift drift = Assert.Single(Revalidation.Check(tree.Root, [item]));

        Assert.Equal(DriftKind.Linked, drift.Kind);
        Assert.Equal(tree.PathOf("real"), drift.Path);
    }

    [Fact]
    public void Der_eintrag_selbst_wurde_zur_verknuepfung()
    {
        using var tree = new TestTree().AddFile("a/file.bin", 10).AddFile("other/data.bin", 10);
        CleanupList.Item item = FileItem(tree, "a/file.bin");
        File.Delete(item.Path);
        Assert.SkipUnless(TryLink(item.Path, tree.PathOf("other/data.bin"), directory: false),
            "Verknuepfungen anlegen braucht Rechte");

        Assert.Equal(DriftKind.Linked, Assert.Single(Revalidation.Check(tree.Root, [item])).Kind);
    }

    [Fact]
    public void Eine_verknuepfung_die_es_schon_beim_scan_war_ist_in_ordnung()
    {
        // Etwa ein Cloud-Platzhalter: Der Benutzer hat die Verknuepfung selbst gesehen und gewaehlt.
        using var tree = new TestTree().AddFile("a/file.bin", 10).AddFile("other/data.bin", 99);
        string link = tree.PathOf("a/link.bin");
        Assert.SkipUnless(TryLink(link, tree.PathOf("other/data.bin"), directory: false),
            "Verknuepfungen anlegen braucht Rechte");
        var item = new CleanupList.Item(link, "link.bin", 10, false, 0, WasLink: true);

        Assert.Empty(Revalidation.Check(tree.Root, [item]));
    }

    [Fact]
    public void Verknuepfungen_oberhalb_der_scanwurzel_sind_die_wahl_des_benutzers()
    {
        using var tree = new TestTree().AddFile("real/scan/file.bin", 10);
        string linkedParent = tree.PathOf("via-link");
        Assert.SkipUnless(TryLink(linkedParent, tree.PathOf("real"), directory: true),
            "Verknuepfungen anlegen braucht Rechte");
        string root = Path.Combine(linkedParent, "scan");
        string path = Path.Combine(root, "file.bin");
        var info = new FileInfo(path);
        var item = new CleanupList.Item(path, "file.bin", info.Length, false, info.LastWriteTimeUtc.Ticks);

        Assert.Empty(Revalidation.Check(root, [item]));
    }

    [Fact]
    public void Abbruch_wird_durchgereicht()
    {
        using var tree = new TestTree().AddFile("a.bin", 10);

        Assert.Throws<OperationCanceledException>(() =>
            Revalidation.Check(tree.Root, [FileItem(tree, "a.bin")], new CancellationToken(true)));
    }

    [Fact]
    public void Die_liste_merkt_sich_zeit_und_verknuepfung_vom_scan_und_erneuert_sie_mit_dem_neuen_stand()
    {
        using var tree = new TestTree().AddFile("a/file.bin", 100);
        NodeStore store = Scan(tree);
        int node = Enumerable.Range(1, store.Count - 1).Single(i => store.GetName(i) == "file.bin");
        var list = new CleanupList();

        Assert.Null(list.Add(store, node));

        CleanupList.Item item = Assert.Single(list.Items);
        Assert.Equal(store.MTime[node], item.MTimeTicks);
        Assert.NotEqual(0, item.MTimeTicks);
        Assert.False(item.WasLink);

        // Nach einem frischen Scan gilt der neue Stand als Massstab.
        File.WriteAllBytes(item.Path, new byte[300]);
        NodeStore fresh = Scan(tree);
        list.RefreshSizes(fresh);

        CleanupList.Item renewed = Assert.Single(list.Items);
        Assert.Equal(300, renewed.Size);
        Assert.Empty(Revalidation.Check(tree.Root, list.Items));
    }

    private static NodeStore Scan(TestTree tree)
    {
        var buffer = new NodeBuffer();
        DirectoryWalker.Walk(tree.Root, buffer, new WalkOptions { WorkerCount = 2 });
        NodeStore store = NodeStoreBuilder.Build(buffer, tree.Root);
        SizeAggregator.Aggregate(store);
        return store;
    }
}
