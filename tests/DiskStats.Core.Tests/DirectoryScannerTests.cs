using DiskStats.Core.Scanning;
using DiskStats.Core.Storage;

namespace DiskStats.Core.Tests;

public class DirectoryScannerTests
{
    /// <summary>Sammelt alles, was der Scanner meldet — Namen als String, damit Tests lesbar bleiben.</summary>
    private sealed class RecordingSink : IEntrySink
    {
        public List<(int ParentId, string Name, long Size)> Files { get; } = [];
        public List<(int ParentId, int OwnId, string Name)> Directories { get; } = [];

        public void AddFile(int parentId, ReadOnlySpan<char> name, long size, long mtimeUtcTicks, EntryFlags flags)
            => Files.Add((parentId, name.ToString(), size));

        public void AddDirectory(int parentId, int ownId, ReadOnlySpan<char> name, long mtimeUtcTicks, EntryFlags flags)
            => Directories.Add((parentId, ownId, name.ToString()));
    }

    [Fact]
    public void Meldet_dateien_des_verzeichnisses_mit_groesse()
    {
        using var tree = new TestTree();
        tree.AddFile("a.bin", 100).AddFile("b.bin", 250);

        var sink = new RecordingSink();
        var subdirs = new List<PendingDirectory>();
        var errors = new ScanErrorLog();

        DirectoryScanner.ScanOne(tree.Root, directoryId: 0, sink, subdirs, errors, new DirectoryIdAllocator());

        Assert.Equal(2, sink.Files.Count);
        Assert.Equal(350, sink.Files.Sum(f => f.Size));
        Assert.All(sink.Files, f => Assert.Equal(0, f.ParentId));
    }

    [Fact]
    public void Steigt_nicht_selbst_in_unterverzeichnisse_ab_sondern_meldet_sie()
    {
        using var tree = new TestTree();
        tree.AddFile("top.bin", 10).AddFile("sub/deep.bin", 999);

        var sink = new RecordingSink();
        var subdirs = new List<PendingDirectory>();

        DirectoryScanner.ScanOne(tree.Root, directoryId: 0, sink, subdirs, new ScanErrorLog(), new DirectoryIdAllocator());

        Assert.Single(sink.Files);                       // deep.bin gehoert nicht hierher
        Assert.Equal("top.bin", sink.Files[0].Name);
        Assert.Single(subdirs);
        Assert.Equal("sub", Path.GetFileName(subdirs[0].Path));
    }

    [Fact]
    public void Vergibt_fortlaufende_ids_fuer_unterverzeichnisse()
    {
        using var tree = new TestTree();
        tree.AddDirectory("x").AddDirectory("y");

        var sink = new RecordingSink();
        var subdirs = new List<PendingDirectory>();

        DirectoryScanner.ScanOne(tree.Root, directoryId: 7, sink, subdirs, new ScanErrorLog(), new DirectoryIdAllocator());

        Assert.Equal(2, sink.Directories.Count);
        Assert.All(sink.Directories, d => Assert.Equal(7, d.ParentId));
        Assert.Equal(subdirs.Select(s => s.Id).OrderBy(i => i),
                     sink.Directories.Select(d => d.OwnId).OrderBy(i => i));
    }

    /// <summary>
    /// Der eigentliche Fall hinter dem Fehlerprotokoll: kein fehlender Ordner, sondern einer,
    /// den das Konto nicht lesen darf. Er muss als Eintrag im Baum stehen, sein Inhalt nicht —
    /// und die Summe darf nicht so tun, als sei sie vollstaendig.
    /// </summary>
    [Fact]
    public void Entzogenes_leserecht_wird_gezaehlt_und_der_ordner_bleibt_sichtbar()
    {
        using var tree = new TestTree();
        tree.AddFile("offen/a.bin", 100).AddFile("gesperrt/b.bin", 200);
        Assert.SkipUnless(tree.DenyListing("gesperrt"), "Rechteentzug nur unter Windows");

        var buffer = new NodeBuffer();
        WalkResult walk = DirectoryWalker.Walk(tree.Root, buffer, new WalkOptions { WorkerCount = 2 });
        NodeStore store = NodeStoreBuilder.Build(buffer, tree.Root);
        SizeAggregator.Aggregate(store);

        ScanError error = Assert.Single(walk.Errors.Snapshot());
        Assert.Equal(tree.PathOf("gesperrt"), error.Path);
        Assert.NotEqual(0, error.ErrorCode);
        Assert.Equal(100, store.Size[0]);
        int locked = SubtreeRefresh.Find(store, tree.PathOf("gesperrt"));
        Assert.NotEqual(-1, locked);
        Assert.Equal(0, store.ChildCount[locked]);
    }

    [Fact]
    public void Verzeichnis_das_nicht_existiert_landet_im_fehlerprotokoll()
    {
        var errors = new ScanErrorLog();

        DirectoryScanner.ScanOne(
            Path.Combine(Path.GetTempPath(), "gibt-es-nicht-" + Guid.NewGuid().ToString("N")),
            directoryId: 0, new RecordingSink(), [], errors, new DirectoryIdAllocator());

        Assert.Equal(1, errors.Count);
    }
}
