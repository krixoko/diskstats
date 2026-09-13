using DiskStats.Core.Cleanup;
using DiskStats.Core.Scanning;
using DiskStats.Core.Storage;

namespace DiskStats.Core.Tests;

public class QuickWinsTests
{
    private static NodeStore Scan(TestTree tree)
    {
        var buffer = new NodeBuffer();
        DirectoryWalker.Walk(tree.Root, buffer, new WalkOptions { WorkerCount = 2 });
        NodeStore store = NodeStoreBuilder.Build(buffer, tree.Root);
        SizeAggregator.Aggregate(store);
        return store;
    }

    private static QuickWins.Result? Of(IReadOnlyList<QuickWins.Result> results, string key)
        => results.FirstOrDefault(r => r.Rule.Key == key);

    /// <summary>Die Testbaeume sind klein; die Mindestgroesse der Anwendung wuerde alles verschlucken.</summary>
    private static IReadOnlyList<QuickWins.Result> Find(TestTree tree) => QuickWins.Find(Scan(tree), 0, minBytes: 1);

    [Fact]
    public void Findet_paket_verzeichnisse_und_summiert_sie()
    {
        using var tree = new TestTree();
        tree.AddFile("projekt-a/node_modules/paket.js", 600).AddFile("projekt-a/package.json", 10)
            .AddFile("projekt-b/node_modules/paket.js", 400).AddFile("projekt-b/package.json", 10)
            .AddFile("projekt-a/quelltext.js", 50);

        QuickWins.Result? win = Of(Find(tree), "PackageDirs");

        Assert.NotNull(win);
        Assert.Equal(1000, win.Bytes);
        Assert.Equal(2, win.Count);
    }

    [Fact]
    public void Zaehlt_verschachtelte_treffer_nicht_doppelt()
    {
        using var tree = new TestTree();
        tree.AddFile("node_modules/paket/node_modules/tief.js", 500).AddFile("package.json", 10);

        QuickWins.Result? win = Of(Find(tree), "PackageDirs");

        // Das aeussere node_modules enthaelt das innere bereits.
        Assert.NotNull(win);
        Assert.Equal(1, win.Count);
        Assert.Equal(500, win.Bytes);
    }

    [Fact]
    public void Erkennt_gross_und_kleinschreibung()
    {
        using var tree = new TestTree();
        tree.AddFile("Projekt/BIN/ausgabe.dll", 300).AddFile("Projekt/Projekt.csproj", 10);

        Assert.NotNull(Of(Find(tree), "BuildOutput"));
    }

    [Fact]
    public void Sortiert_nach_platz()
    {
        using var tree = new TestTree();
        tree.AddFile("a/bin/gross.dll", 5000).AddFile("a/a.csproj", 10)
            .AddFile("b/.venv/klein.py", 100).AddFile("b/pyproject.toml", 10);

        IReadOnlyList<QuickWins.Result> results = Find(tree);

        Assert.Equal("BuildOutput", results[0].Rule.Key);
    }

    [Fact]
    public void Bietet_namenstreffer_nur_zur_durchsicht_an()
    {
        using var tree = new TestTree();
        tree.AddFile("node_modules/x.js", 100).AddFile("package.json", 10)
            .AddFile("Downloads/film.mkv", 900).AddFile("ntuser.dat", 1);

        IReadOnlyList<QuickWins.Result> results = Find(tree);

        Assert.Equal(WinRisk.LookFirst, Of(results, "PackageDirs")!.Rule.Risk);

        // Downloads enthaelt eigene Dateien und darf nie gesammelt vorgemerkt werden.
        Assert.Equal(WinRisk.LookFirst, Of(results, "Downloads")!.Rule.Risk);
    }

    [Fact]
    public void Meldet_nichts_wenn_es_nichts_gibt()
    {
        using var tree = new TestTree();
        tree.AddFile("urlaub/bild.jpg", 500);

        Assert.Empty(Find(tree));
    }

    [Fact]
    public void Ignoriert_gleichnamige_dateien()
    {
        using var tree = new TestTree();

        // Eine Datei namens "Cache" ist kein Zwischenspeicher-Ordner.
        tree.AddFile("Cache", 900);

        Assert.Empty(Find(tree));
    }

    /// <summary>
    /// Die Zahl in der Ueberschrift ist ein Versprechen. Sie darf nur enthalten, was sich
    /// ohne Nachdenken zurueckholen laesst — sonst wirbt sie mit fremden Dateien.
    /// </summary>
    [Fact]
    public void Zaehlt_unsichere_pakete_nicht_als_sicher_freigebbar()
    {
        using var tree = new TestTree();
        tree.AddFile("node_modules/x.js", 300).AddFile("package.json", 10)
            .AddFile("Downloads/film.mkv", 90_000).AddFile("ntuser.dat", 1);

        Assert.Equal(0, QuickWins.ReclaimableOf(Find(tree)));
    }

    /// <summary>Gleich riskante Treffer werden nach Groesse sortiert.</summary>
    [Fact]
    public void Sortiert_anzusehende_treffer_nach_groesse()
    {
        using var tree = new TestTree();
        tree.AddFile("node_modules/x.js", 100).AddFile("package.json", 10)
            .AddFile("Downloads/film.mkv", 90_000).AddFile("ntuser.dat", 1);

        IReadOnlyList<QuickWins.Result> results = Find(tree);

        Assert.Equal("Downloads", results[0].Rule.Key);
        Assert.Equal("PackageDirs", results[1].Rule.Key);
    }

    [Fact]
    public void Alte_windows_version_gilt_als_anzusehen()
    {
        using var tree = new TestTree();
        tree.AddFile("Windows.old/kram.dll", 9000);

        QuickWins.Result? win = Of(Find(tree), "WindowsOld");

        Assert.NotNull(win);
        Assert.Equal(WinRisk.LookFirst, win.Rule.Risk);
    }

    [Theory]
    [InlineData(".cargo/credentials.toml")]
    [InlineData(".cargo/bin/tool.exe")]
    [InlineData(".m2/settings.xml")]
    [InlineData(".gradle/gradle.properties")]
    [InlineData("Service Worker/offline-data.bin")]
    [InlineData("Temp/unsaved-document.txt")]
    public void Eigene_daten_und_konfiguration_sind_keine_sicheren_quick_wins(string relative)
    {
        using var tree = new TestTree();
        tree.AddFile(relative, 123);
        var results = Find(tree);
        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.Equal(WinRisk.LookFirst, r.Rule.Risk));
        Assert.Equal(0, QuickWins.ReclaimableOf(results));
    }

    /// <summary>
    /// "vendor" neben einer package.json ist ein Paketordner — mit moeglichen eigenen
    /// Aenderungen darin. Er wird gezeigt, aber nicht als sicher versprochen.
    /// </summary>
    [Fact]
    public void Paketordner_im_projekt_bleiben_anzusehen()
    {
        using var tree = new TestTree();
        tree.AddFile("vendor/own-code.php", 123).AddFile("composer.json", 10);
        var results = Find(tree);
        Assert.Equal(WinRisk.LookFirst, Assert.Single(results).Rule.Risk);
    }

    /// <summary>
    /// "bin" heisst auch der Ordner mit den Buergschaftsunterlagen — ohne Projektdatei
    /// daneben ist der Name kein Beleg fuer irgendetwas. Dann lieber kein Fund.
    /// </summary>
    [Theory]
    [InlineData("Fotos/bin/urlaub.jpg")]
    [InlineData("Unterlagen/dist/vertrag.pdf")]
    [InlineData("Dokumente/vendor/rechnung.pdf")]
    [InlineData("Musik/node_modules/song.mp3")]
    [InlineData("Ablage/.venv/notizen.txt")]
    [InlineData("Backup/Downloads/film.mkv")]
    public void Projekt_und_profilnamen_ohne_kontext_sind_kein_fund(string relative)
    {
        using var tree = new TestTree();
        tree.AddFile(relative, 123);

        Assert.Empty(Find(tree));
    }

    /// <summary>
    /// Neben einer Projektdatei ist Build-Ausgabe genau das: Sie entsteht beim naechsten Bau
    /// neu. Das ist der eine Fall, in dem die Zahl oben ein echtes Versprechen sein darf.
    /// </summary>
    [Theory]
    [InlineData("app/bin/app.dll", "app/app.csproj")]
    [InlineData("app/obj/project.assets.json", "app/app.fsproj")]
    [InlineData("crate/target/debug/x.exe", "crate/Cargo.toml")]
    [InlineData("web/dist/bundle.js", "web/package.json")]
    [InlineData("web/.next/cache.bin", "web/package.json")]
    [InlineData("repo/bin/tool.exe", "repo/.git/HEAD")]
    public void Build_ausgabe_neben_einer_projektdatei_waechst_nach(string output, string marker)
    {
        using var tree = new TestTree();
        tree.AddFile(output, 4000).AddFile(marker, 10);

        var results = Find(tree);

        QuickWins.Result win = Assert.Single(results);
        Assert.Equal("BuildOutput", win.Rule.Key);
        Assert.Equal(WinRisk.Regrows, win.Rule.Risk);
        Assert.Equal(4000, QuickWins.ReclaimableOf(results));
    }

    [Fact]
    public void Python_zwischenspeicher_wachsen_ueberall_nach()
    {
        using var tree = new TestTree();
        tree.AddFile("irgendwo/__pycache__/mod.pyc", 300).AddFile("tief/.pytest_cache/v/x", 200);

        var results = Find(tree);

        QuickWins.Result win = Assert.Single(results);
        Assert.Equal("PyCaches", win.Rule.Key);
        Assert.Equal(WinRisk.Regrows, win.Rule.Risk);
        Assert.Equal(500, QuickWins.ReclaimableOf(results));
    }

    [Fact]
    public void Der_downloads_ordner_zaehlt_nur_im_profil()
    {
        using var tree = new TestTree();
        tree.AddFile("Downloads/film.mkv", 900).AddFile("ntuser.dat", 1)
            .AddFile("Projekte/Downloads/daten.csv", 900);

        QuickWins.Result? win = Of(Find(tree), "Downloads");

        Assert.NotNull(win);
        Assert.Equal(1, win.Count);
        Assert.Equal(900, win.Bytes);
    }

    /// <summary>Ein bin-Ordner mit 20 KB ist kein Quick Win — er ist Laerm in der Liste.</summary>
    [Fact]
    public void Kleine_treffer_bleiben_unter_der_mindestgroesse()
    {
        using var tree = new TestTree();
        tree.AddFile("app/bin/app.dll", 4000).AddFile("app/app.csproj", 10);

        Assert.Empty(QuickWins.Find(Scan(tree), 0));   // Standard: 1 MiB
        Assert.Single(QuickWins.Find(Scan(tree), 0, minBytes: 4000));
    }
}
