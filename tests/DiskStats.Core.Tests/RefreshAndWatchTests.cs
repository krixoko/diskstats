using DiskStats.Core.Scanning;
using DiskStats.Core.Storage;

namespace DiskStats.Core.Tests;

public sealed class RefreshAndWatchTests
{
    private static NodeStore Scan(TestTree tree)
    {
        var buffer = new NodeBuffer();
        DirectoryWalker.Walk(tree.Root, buffer, new WalkOptions());
        NodeStore store = NodeStoreBuilder.Build(buffer, tree.Root);
        SizeAggregator.Aggregate(store);
        return store;
    }

    [Fact]
    public void Aktualisiert_nur_den_gewaehlten_teilbaum_und_erhaelt_ausschluesse()
    {
        using var tree = new TestTree();
        tree.AddFile("target/old.bin", 100).AddFile("sibling/keep.bin", 200);
        NodeStore store = Scan(tree);
        File.Delete(tree.PathOf("target/old.bin"));
        tree.AddFile("target/new.bin", 300).AddFile("target/excluded/private.bin", 999)
            .AddFile("sibling/keep.bin", 5000);
        var result = SubtreeRefresh.Refresh(store, SubtreeRefresh.Find(store, tree.PathOf("target")),
            new WalkOptions { ExcludedPaths = ["target/excluded"] });
        Assert.Equal(tree.PathOf("target"), result.ScannedPath);
        Assert.Equal(500, result.Store.Size[0]); // the sibling is deliberately not rescanned
        Assert.Empty(NodeSearch.Find(result.Store, "old.bin"));
        Assert.Empty(NodeSearch.Find(result.Store, "private.bin"));
        Assert.Single(NodeSearch.Find(result.Store, "new.bin"));
        Assert.Equal(300, store.Size[0]); // original remains safe for concurrent readers
        Assert.Equal(0, result.Errors);
        using var snapshot = new MemoryStream();
        SnapshotFormat.Write(result.Store, snapshot);
        snapshot.Position = 0;
        Assert.Equal(result.Store.PhysicalSize, SnapshotFormat.Read(snapshot).PhysicalSize);
    }

    [Fact]
    public void Verschwundener_ordner_wird_entfernt_und_dateiauswahl_aktualisiert_den_elternordner()
    {
        using var tree = new TestTree();
        tree.AddFile("target/old.bin", 100).AddFile("keep.bin", 200);
        NodeStore store = Scan(tree);
        int file = SubtreeRefresh.Find(store, tree.PathOf("target/old.bin"));
        Directory.Delete(tree.PathOf("target"), recursive: true);
        var result = SubtreeRefresh.Refresh(store, file, new WalkOptions());
        Assert.Equal(200, result.Store.Size[0]);
        Assert.Empty(NodeSearch.Find(result.Store, "target"));
        Assert.Equal(-1, SubtreeRefresh.Find(store, Path.GetDirectoryName(tree.Root)!));
    }

    /// <summary>
    /// Ein Ordner, der gerade nicht lesbar ist, ist nicht leer. Wuerde der Refresh ihn durch
    /// das ersetzen, was er lesen konnte, verschwaenden Gigabyte aus der Anzeige — und der
    /// Nutzer suchte den Fehler auf der Platte statt in den Rechten.
    /// </summary>
    [Fact]
    public void Unlesbarer_unterordner_behaelt_beim_refresh_seine_alte_groesse()
    {
        using var tree = new TestTree();
        tree.AddFile("target/locked/big.bin", 4000).AddFile("target/open/small.bin", 100);
        NodeStore store = Scan(tree);
        tree.AddFile("target/open/more.bin", 50);
        Assert.SkipUnless(tree.DenyListing("target/locked"), "Rechteentzug nur unter Windows");

        var result = SubtreeRefresh.Refresh(store, SubtreeRefresh.Find(store, tree.PathOf("target")), new WalkOptions());

        Assert.Equal(1, result.Errors);
        int locked = SubtreeRefresh.Find(result.Store, tree.PathOf("target/locked"));
        Assert.NotEqual(-1, locked);
        Assert.Equal(4000, result.Store.Size[locked]);
        Assert.True((result.Store.Flags[locked] & EntryFlags.Unreadable) != 0, "der Ordner soll als unlesbar markiert sein");
        Assert.Equal(4150, result.Store.Size[0]);
        Assert.Single(NodeSearch.Find(result.Store, "more.bin"));
    }

    [Fact]
    public async Task Beobachter_meldet_beide_eltern_einer_umbenennung_und_ignoriert_eigene_ablagen()
    {
        using var tree = new TestTree();
        tree.AddFile("from/file.bin", 100).AddDirectory("to").AddDirectory("ignored");
        using var monitor = new FileChangeMonitor(tree.Root, [tree.PathOf("ignored")]);
        var completion = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        monitor.Changed += (folders, overflow) =>
        {
            lock (seen)
            {
                Assert.False(overflow);
                seen.UnionWith(folders);
                if (seen.Contains(tree.PathOf("from")) && seen.Contains(tree.PathOf("to"))) completion.TrySetResult(seen.ToArray());
            }
        };
        File.WriteAllBytes(tree.PathOf("ignored/log.txt"), new byte[10]);
        File.Move(tree.PathOf("from/file.bin"), tree.PathOf("to/renamed.bin"));
        string[] folders = await completion.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.DoesNotContain(tree.PathOf("ignored"), folders);
    }

    /// <summary>
    /// Das inkrementelle Entfernen verspricht dasselbe Ergebnis wie ein neuer Scan — nur
    /// ohne die Platte. Das Versprechen gilt fuer jede Zahl im Baum, nicht nur fuer die Wurzel.
    /// </summary>
    [Fact]
    public void Entfernen_im_baum_liefert_dieselben_zahlen_wie_ein_neuer_scan()
    {
        using var tree = new TestTree();
        tree.AddFile("a/x.bin", 100).AddFile("a/y.bin", 200).AddFile("a/tief/z.bin", 400)
            .AddFile("b/w.bin", 800).AddFile("b/v.bin", 1600);
        NodeStore live = Scan(tree);

        var cleanup = new DiskStats.Core.Cleanup.CleanupList();
        int folder = SubtreeRefresh.Find(live, tree.PathOf("a/tief"));
        int file = SubtreeRefresh.Find(live, tree.PathOf("b/w.bin"));
        Assert.Null(cleanup.Add(live, folder));
        Assert.Null(cleanup.Add(live, file));
        DiskStats.Core.Cleanup.CleanupList.ApplyRemovals(live, [folder, file]);

        Directory.Delete(tree.PathOf("a/tief"), recursive: true);
        File.Delete(tree.PathOf("b/w.bin"));
        NodeStore fresh = Scan(tree);

        Assert.Equal(fresh.Size[0], live.Size[0]);
        Assert.Equal(fresh.PhysicalSize[0], live.PhysicalSize[0]);
        Assert.Equal(fresh.FileCount[0], live.FileCount[0]);
        Assert.Equal(fresh.FolderCount[0], live.FolderCount[0]);
        foreach (string relative in new[] { "a", "b" })
        {
            int inFresh = SubtreeRefresh.Find(fresh, tree.PathOf(relative));
            int inLive = SubtreeRefresh.Find(live, tree.PathOf(relative));
            Assert.Equal(fresh.Size[inFresh], live.Size[inLive]);
            Assert.Equal(fresh.PhysicalSize[inFresh], live.PhysicalSize[inLive]);
            Assert.Equal(fresh.FileCount[inFresh], live.FileCount[inLive]);
            Assert.Equal(fresh.FolderCount[inFresh], live.FolderCount[inLive]);
        }
        Assert.Equal(-1, SubtreeRefresh.Find(live, tree.PathOf("a/tief")));
        Assert.Equal(-1, SubtreeRefresh.Find(live, tree.PathOf("b/w.bin")));
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, System.Text.StringBuilder shortPath, uint length);

    /// <summary>
    /// Der Beobachter meldet Pfade, wie das System sie liefert — mal in Kurzform, mal mit
    /// anderer Schreibweise. Der Filter fuer die eigene Ablage muss sie trotzdem erkennen,
    /// sonst loest jeder Snapshot einen Refresh des Snapshot-Ordners aus.
    /// </summary>
    [Fact]
    public void Beobachter_erkennt_die_eigene_ablage_auch_unter_anderem_namen()
    {
        using var tree = new TestTree();
        tree.AddDirectory("ignored-folder-with-long-name").AddDirectory("other");
        string ignored = tree.PathOf("ignored-folder-with-long-name");
        using var monitor = new FileChangeMonitor(tree.Root, [ignored]);

        Assert.False(monitor.Queue(Path.Combine(tree.Root, "other", "..", "ignored-folder-with-long-name", "log.txt")));
        Assert.False(monitor.Queue(Path.Combine(tree.Root, "IGNORED-FOLDER-WITH-LONG-NAME", "log.txt")));
        Assert.True(monitor.Queue(Path.Combine(tree.Root, "other", "file.txt")));

        Assert.SkipUnless(OperatingSystem.IsWindows(), "Kurznamen nur unter Windows");
        var buffer = new System.Text.StringBuilder(260);
        Assert.SkipWhen(GetShortPathNameW(ignored, buffer, (uint)buffer.Capacity) == 0, "kein Kurzname ermittelbar");
        string shortName = buffer.ToString();
        Assert.SkipWhen(shortName.Equals(ignored, StringComparison.OrdinalIgnoreCase), "Kurznamen auf diesem Datentraeger aus");
        Assert.False(monitor.Queue(Path.Combine(shortName, "log.txt")));
    }

    [Fact]
    public async Task Beobachter_erkennt_aenderungen_und_loeschungen()
    {
        using var tree = new TestTree();
        tree.AddFile("sub/file.bin", 100);
        using var monitor = new FileChangeMonitor(tree.Root);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.Changed += (folders, _) => { if (folders.Contains(tree.PathOf("sub"))) changed.TrySetResult(); };
        File.AppendAllText(tree.PathOf("sub/file.bin"), "changed");
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(8));
        var deleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.Changed += (folders, _) => { if (folders.Contains(tree.PathOf("sub"))) deleted.TrySetResult(); };
        File.Delete(tree.PathOf("sub/file.bin"));
        await deleted.Task.WaitAsync(TimeSpan.FromSeconds(8));
    }

    [Fact]
    public async Task Pufferueberlauf_fordert_einen_vollstaendigen_abgleich_an()
    {
        using var tree = new TestTree();
        using var monitor = new FileChangeMonitor(tree.Root);
        var completion = new TaskCompletionSource<(IReadOnlyList<string>, bool)>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.Changed += (paths, overflow) => completion.TrySetResult((paths, overflow));
        monitor.RescanAfterOverflow();
        var (folders, overflow) = await completion.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.True(overflow);
        Assert.Equal(tree.Root, Assert.Single(folders));
    }

    [Fact]
    public async Task Dauernde_schreibzugriffe_verhindern_keine_aktualisierung()
    {
        using var tree = new TestTree();
        tree.AddFile("file.bin", 100).AddFile("node_modules/deep/ignored.bin", 100);
        using var monitor = new FileChangeMonitor(tree.Root, excludedPatterns: ["node_modules"]);
        var completion = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.Changed += (paths, _) => completion.TrySetResult(paths);
        Task writer = Task.Run(async () =>
        {
            for (int i = 0; i < 20; i++)
            {
                File.AppendAllText(tree.PathOf("file.bin"), "x");
                File.AppendAllText(tree.PathOf("node_modules/deep/ignored.bin"), "x");
                await Task.Delay(100);
            }
        });
        try
        {
            var paths = await completion.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.False(writer.IsCompleted);
            Assert.Equal(tree.Root, Assert.Single(paths));
        }
        finally { await writer; }
    }
}
