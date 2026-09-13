using DiskStats.Core.Scanning;

namespace DiskStats.Core.Tests;

public class DirectoryWalkerTests
{
    [Fact]
    public void Erfasst_alle_dateien_im_gesamten_baum()
    {
        using var tree = new TestTree();
        tree.AddFile("a.bin", 100)
            .AddFile("sub/b.bin", 200)
            .AddFile("sub/tief/c.bin", 300)
            .AddFile("anders/d.bin", 400);

        var sink = new CountingSink();
        DirectoryWalker.Walk(tree.Root, sink, new WalkOptions());

        Assert.Equal(4, sink.FileCount);
        Assert.Equal(1000, sink.TotalBytes);
        Assert.Equal(3, sink.DirectoryCount);   // sub, sub/tief, anders
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(16)]
    public void Liefert_unabhaengig_von_der_workerzahl_dasselbe_ergebnis(int workers)
    {
        using var tree = new TestTree();
        for (int i = 0; i < 50; i++)
            tree.AddFile($"d{i % 7}/f{i}.bin", i + 1);

        var sink = new CountingSink();
        DirectoryWalker.Walk(tree.Root, sink, new WalkOptions { WorkerCount = workers });

        Assert.Equal(50, sink.FileCount);
        Assert.Equal(50 * 51 / 2, sink.TotalBytes);
    }

    [Fact]
    public void Leeres_verzeichnis_liefert_null_dateien_ohne_fehler()
    {
        using var tree = new TestTree();

        var sink = new CountingSink();
        var result = DirectoryWalker.Walk(tree.Root, sink, new WalkOptions());

        Assert.Equal(0, sink.FileCount);
        Assert.Equal(0, result.Errors.Count);
    }

    /// <summary>
    /// Wirft der Empfaenger etwas Unerwartetes, darf der Lauf nicht still haengen: Ein
    /// gestorbener Worker hinterliesse sonst einen Zaehler, der nie 0 erreicht, und die
    /// uebrigen Worker warteten ewig auf eine Schlange, die nie geschlossen wird.
    /// </summary>
    [Fact]
    public async Task Unerwartete_ausnahme_im_empfaenger_beendet_den_lauf_statt_zu_haengen()
    {
        using var tree = new TestTree();
        for (int i = 0; i < 6; i++) tree.AddFile($"o{i}/u{i}/f{i}.bin", 10);

        // Genau ein Fehler: Der eine Worker stirbt, die drei anderen arbeiten den Rest ab —
        // und wuerden dann ewig auf das Verzeichnis warten, das der tote nie abgeschlossen hat.
        var sink = new ExplodingSink(onFile: 2);
        Task<WalkResult> walk = Task.Run(() => DirectoryWalker.Walk(tree.Root, sink, new WalkOptions { WorkerCount = 4 }));

        Task finished = await Task.WhenAny(walk, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(ReferenceEquals(finished, walk), "der Lauf haengt");

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => walk);
        Assert.Contains("kaputt", (thrown as AggregateException)?.InnerException?.Message ?? thrown.Message);
    }

    private sealed class ExplodingSink(int onFile) : IEntrySink
    {
        private int _files;

        public void AddFile(int parentId, ReadOnlySpan<char> name, long size, long mtimeUtcTicks, EntryFlags flags)
        {
            if (Interlocked.Increment(ref _files) == onFile) throw new InvalidOperationException("kaputt");
        }

        public void AddDirectory(int parentId, int ownId, ReadOnlySpan<char> name, long mtimeUtcTicks, EntryFlags flags) { }
    }
}
