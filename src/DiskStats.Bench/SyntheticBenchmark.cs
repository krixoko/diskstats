using System.Diagnostics;
using DiskStats.Core.Scanning;
using DiskStats.Core.Storage;

namespace DiskStats.Bench;

/// <summary>
/// Misst die Stellen, an denen ein grosser Baum im Speicher bearbeitet wird - ohne Platte.
///
/// Ein echter Scan misst vor allem das Dateisystem. Hier geht es um das, was danach auf dem
/// UI-Faden und im Speichersammler passiert: Sortieren der Dateitabelle nach Namen, Pfadsuche,
/// das Zusammenkopieren des Baums bei einem Refresh, die Hardlink-Aufloesung. Aufruf:
/// <c>DiskStats.Bench --synthetic 2000000</c>
/// </summary>
internal static class SyntheticBenchmark
{
    public static int Run(int entries)
    {
        Console.WriteLine($"Synthetischer Baum : {entries:N0} Eintraege (Release-Build, ohne Platte)");
        Console.WriteLine();

        NodeStore store = Build(entries, out int bigFolder, out int[] sampleNames);
        Console.WriteLine($"  Knoten             : {store.Count:N0}, grosser Ordner mit {store.ChildCount[bigFolder]:N0} Kindern");
        Console.WriteLine();

        Measure("Sortieren nach Name (alt: string je Vergleich)", () =>
        {
            int[] nodes = Enumerable.Range(1, store.Count - 1).ToArray();
            Array.Sort(nodes, (a, b) => string.Compare(store.GetName(a), store.GetName(b), StringComparison.OrdinalIgnoreCase));
            return nodes[0];
        });

        Measure("Sortieren nach Name (NodeStore.CompareNames)", () =>
        {
            int[] nodes = Enumerable.Range(1, store.Count - 1).ToArray();
            Array.Sort(nodes, store.CompareNames);
            return nodes[0];
        });

        // 1000 Pfade im grossen Ordner suchen, gleichmaessig ueber ihn verteilt.
        string[] paths = [.. sampleNames.Select(n => store.PathOf(n))];
        Measure($"Find: {paths.Length:N0} Pfade im Ordner mit {store.ChildCount[bigFolder]:N0} Kindern", () =>
        {
            int found = 0;
            foreach (string path in paths) if (SubtreeRefresh.Find(store, path) > 0) found++;
            return found;
        });

        Measure("Replace: ganzen Baum neu zusammensetzen (Refresh)", () =>
            SubtreeRefresh.Replace(store, bigFolder, replacement: null).Count);

        Measure("AggregatePhysical (Hardlink-Aufloesung)", () =>
        {
            SizeAggregator.AggregatePhysical(store);
            return (int)(store.PhysicalSize[0] & 0xff);
        });

        return 0;
    }

    private static void Measure(string label, Func<int> work)
    {
        work(); // aufwaermen: JIT und erste Allokationen nicht mitmessen
        GC.Collect();
        GC.WaitForPendingFinalizers();

        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        int gen0 = GC.CollectionCount(0), gen2 = GC.CollectionCount(2);
        var clock = Stopwatch.StartNew();
        int result = work();
        clock.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

        Console.WriteLine($"  {label}");
        Console.WriteLine($"      {clock.Elapsed.TotalMilliseconds,9:N0} ms   {allocated / 1024.0 / 1024,9:N1} MB allokiert   "
            + $"GC gen0 {GC.CollectionCount(0) - gen0}, gen2 {GC.CollectionCount(2) - gen2}   (Ergebnis {result})");
    }

    /// <summary>
    /// Ein Baum mit gemischten Namen (Gross-/Kleinschreibung, einige mit Umlauten), einem sehr grossen
    /// Ordner und etwa jeder zehnten Datei als Hardlink-Gruppe.
    /// </summary>
    private static NodeStore Build(int entries, out int bigFolder, out int[] sampleNames)
    {
        var buffer = new NodeBuffer();
        var random = new Random(42);
        string[] words = ["Report", "backup", "Archive", "image", "Data", "temp", "Log", "cache", "Müller", "Größe", "video", "Doc"];

        // Ordner 1 ist der grosse (die Haelfte aller Dateien), der Rest verteilt sich auf viele kleine.
        int folders = Math.Max(10, entries / 200);
        for (int d = 1; d <= folders; d++)
            buffer.AddDirectory(0, d, d == 1 ? "big" : $"dir{d:D6}", 0, EntryFlags.Directory);

        uint identity = 1;
        for (int i = 0; i < entries; i++)
        {
            int parent = i % 2 == 0 ? 1 : 2 + random.Next(folders - 1);
            string name = $"{words[random.Next(words.Length)]}-{i:D7}-{words[random.Next(words.Length)].ToUpperInvariant()}.dat";
            long size = random.Next(1, 5_000_000);

            // Jede zehnte Datei hat einen Zwilling (gleiche Kennung) - wie WinSxS.
            bool linked = i % 10 == 0;
            ulong id = linked ? identity++ : 0;
            var storage = new FileStorageInfo(size, linked ? new FileIdentity(1, id, 0) : default, linked ? 2u : 1u);
            buffer.AddFile(parent, name, size, 0, EntryFlags.None, storage);
            if (linked)
                buffer.AddFile(2 + random.Next(folders - 1), "link-" + name, size, 0, EntryFlags.None,
                    new FileStorageInfo(size, new FileIdentity(1, id, 0), 2u));
        }

        NodeStore store = NodeStoreBuilder.Build(buffer, @"C:\synthetic");
        SizeAggregator.Aggregate(store);

        bigFolder = Enumerable.Range(1, store.Count - 1).First(i => store.IsDirectory(i) && store.GetName(i) == "big");
        int start = store.ChildStart[bigFolder], count = store.ChildCount[bigFolder];
        sampleNames = [.. Enumerable.Range(0, 1000).Select(k => start + (int)((long)k * count / 1000))];
        return store;
    }
}
