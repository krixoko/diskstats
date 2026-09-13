using System.Diagnostics;
using System.Runtime;
using DiskStats.Core.Scanning;
using DiskStats.Core.Storage;

if (args.Length < 1)
{
    Console.Error.WriteLine("Aufruf: DiskStats.Bench <pfad> [workerzahl] [--store]");
    return 1;
}

string root = args[0];
bool withStore = args.Contains("--store");
int workers = args.Length > 1 && int.TryParse(args[1], out int w) ? w : Environment.ProcessorCount;

if (!Directory.Exists(root))
{
    Console.Error.WriteLine($"Verzeichnis nicht gefunden: {root}");
    return 1;
}

Console.WriteLine($"Pfad     : {root}");
Console.WriteLine($"Worker   : {workers}");
Console.WriteLine($"Modus    : {(withStore ? "vollstaendiger NodeStore" : "nur zaehlen")}");
Console.WriteLine();

GC.Collect();
GC.WaitForPendingFinalizers();
long memoryBefore = GC.GetTotalMemory(forceFullCollection: true);

var stopwatch = Stopwatch.StartNew();
long fileCount, directoryCount, totalBytes;
int errorCount;

if (withStore)
{
    NodeStore store;
    WalkResult walk;
    TimeSpan afterWalk;

    // Eigene Methode statt eines Blocks: Ein Block beendet zwar die Sichtbarkeit, raeumt den
    // Stapelplatz aber nicht zwingend frei — der Puffer blieb dadurch erreichbar und wog in
    // der Messung 80 MB mit. Die Anwendung baut ihn ohnehin in einer Methode auf.
    (store, walk, afterWalk) = Aufbauen(root, workers, stopwatch);

    TimeSpan afterBuild = stopwatch.Elapsed;

    SizeAggregator.Aggregate(store);
    TimeSpan afterAggregate = stopwatch.Elapsed;

    // Dauerzustand hier messen, nicht spaeter: jetzt lebt genau ein NodeStore und der
    // Aufbau-Puffer ist eingesammelt. Nach dem Snapshot-Roundtrip waere ein zweiter im Speicher.
    stopwatch.Stop();
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    long steadyBytes = GC.GetTotalMemory(forceFullCollection: true) - memoryBefore;

    // Hier und nirgends sonst: Der Snapshot-Durchlauf danach legt einen zweiten Baum an, und
    // jede Zahl von dort waere um dessen Groesse verfaelscht.
    Aufschluesselung(store, steadyBytes, memoryBefore);

    stopwatch.Start();

    // Eigener Name je Lauf und Loeschen im finally: sonst bleibt bei einem Abbruch eine
    // dreissig Megabyte grosse Datei im Temp-Ordner liegen.
    string snapshotPath = Path.Combine(
        Path.GetTempPath(), $"diskstats-bench-{Environment.ProcessId}.dss");
    try
    {
    using (var file = File.Create(snapshotPath))
        SnapshotFormat.Write(store, file);
    TimeSpan afterSnapshot = stopwatch.Elapsed;
    long snapshotBytes = new FileInfo(snapshotPath).Length;

    using (var file = File.OpenRead(snapshotPath))
        _ = SnapshotFormat.Read(file);
    TimeSpan afterReload = stopwatch.Elapsed;
    stopwatch.Stop();

    fileCount = store.Count - 1;
    directoryCount = Enumerable.Range(0, store.Count).Count(i => store.IsDirectory(i));
    totalBytes = store.Size[0];
    errorCount = walk.Errors.Count;

    Console.WriteLine($"  Walk           : {afterWalk.TotalSeconds:F2} s");
    Console.WriteLine($"  NodeStore-Bau  : {(afterBuild - afterWalk).TotalSeconds:F2} s");
    Console.WriteLine($"  Aggregation    : {(afterAggregate - afterBuild).TotalSeconds:F2} s");
    Console.WriteLine($"  Snapshot-Write : {(afterSnapshot - afterAggregate).TotalSeconds:F2} s");
    Console.WriteLine($"  Snapshot-Read  : {(afterReload - afterSnapshot).TotalSeconds:F2} s");
    Console.WriteLine($"  Snapshot-Datei : {snapshotBytes / 1024.0 / 1024:F1} MB");
    Console.WriteLine($"  Namens-Pool    : {store.Names.ByteLength / 1024.0 / 1024:F1} MB");
    }
    finally
    {
        try { File.Delete(snapshotPath); } catch (IOException) { }
    }

    Console.WriteLine($"  Speicher Dauer : {steadyBytes / 1024.0 / 1024:F1} MB (ein geladener NodeStore)");
    GC.KeepAlive(store);
}
else
{
    var sink = new CountingSink();
    WalkResult walk = DirectoryWalker.Walk(root, sink, new WalkOptions { WorkerCount = workers });
    stopwatch.Stop();

    fileCount = sink.FileCount;
    directoryCount = sink.DirectoryCount;
    totalBytes = sink.TotalBytes;
    errorCount = walk.Errors.Count;
}

double seconds = stopwatch.Elapsed.TotalSeconds;
long managed = GC.GetTotalMemory(forceFullCollection: false) - memoryBefore;

Console.WriteLine();
Console.WriteLine($"Dauer gesamt     : {seconds:F2} s");
Console.WriteLine($"Dateien          : {fileCount:N0}");
Console.WriteLine($"Verzeichnisse    : {directoryCount:N0}");
Console.WriteLine($"Gesamtgroesse    : {totalBytes / 1024.0 / 1024 / 1024:F2} GB");
Console.WriteLine($"Dateien/Sekunde  : {(seconds > 0 ? fileCount / seconds : 0):N0}");
Console.WriteLine($"Managed-Speicher : {managed / 1024.0 / 1024:F1} MB");
Console.WriteLine($"Peak Working Set : {Process.GetCurrentProcess().PeakWorkingSet64 / 1024.0 / 1024:F1} MB");
Console.WriteLine($"Unlesbare Ordner : {errorCount:N0}");

return 0;

/// <summary>
/// Laeuft den Baum ab und baut den NodeStore. Der Puffer lebt nur hier: Beim Verlassen der
/// Methode ist er unerreichbar und wird eingesammelt.
/// </summary>
static (NodeStore Store, WalkResult Walk, TimeSpan AfterWalk) Aufbauen(
    string root, int workers, Stopwatch stopwatch)
{
    var buffer = new NodeBuffer();
    WalkResult walk = DirectoryWalker.Walk(root, buffer, new WalkOptions { WorkerCount = workers });
    TimeSpan afterWalk = stopwatch.Elapsed;

    return (NodeStoreBuilder.Build(buffer, root), walk, afterWalk);
}

/// <summary>
/// Wiegt jedes Feld einzeln und stellt die Summe der gemessenen Haldengroesse gegenueber.
///
/// Der Anlass: Nach der M0-Messung klaffte eine Luecke von rund 69 MB zwischen Rechnung und
/// Messung, und solange sie unerklaert ist, weiss niemand, ob sie bei fuenf Millionen Dateien
/// linear waechst oder schlimmer. Geraten wurde genug; hier wird gezaehlt.
/// </summary>
static void Aufschluesselung(NodeStore store, long gemessen, long grundlage)
{
    long n = store.Count;

    (string Name, long Bytes)[] felder =
    [
        ("ParentIndex  int[]",    n * sizeof(int)),
        ("ChildStart   int[]",    n * sizeof(int)),
        ("ChildCount   int[]",    n * sizeof(int)),
        ("NameOffset   int[]",    n * sizeof(int)),
        ("NameLength   ushort[]", n * sizeof(ushort)),
        ("Size         long[]",   n * sizeof(long)),
        ("MTime        long[]",   n * sizeof(long)),
        ("Flags        byte[]",   n * sizeof(byte)),
        ("Namensvorrat belegt",   store.Names.ByteLength),
        ("Namensvorrat Luft",     store.Names.Capacity - store.Names.ByteLength),
    ];

    Console.WriteLine();
    Console.WriteLine("Aufschluesselung des Dauerzustands");
    Console.WriteLine($"  Knoten         : {n:N0}");

    long summe = 0;
    foreach ((string name, long bytes) in felder)
    {
        summe += bytes;
        Console.WriteLine($"  {name,-22}: {bytes / 1024.0 / 1024,8:F1} MB");
    }

    Console.WriteLine($"  {"Summe der Felder",-22}: {summe / 1024.0 / 1024,8:F1} MB");
    Console.WriteLine($"  {"Gemessen",-22}: {gemessen / 1024.0 / 1024,8:F1} MB");
    Console.WriteLine($"  {"Differenz",-22}: {(gemessen - summe) / 1024.0 / 1024,8:F1} MB");

    // Die Halde selbst befragen: Wieviel davon ist Freiraum zwischen den Objekten?
    GCMemoryInfo info = GC.GetGCMemoryInfo();
    Console.WriteLine();
    Console.WriteLine("Was die Halde dazu sagt");
    Console.WriteLine($"  {"Haldengroesse",-22}: {info.HeapSizeBytes / 1024.0 / 1024,8:F1} MB");
    Console.WriteLine($"  {"davon Freiraum",-22}: {info.FragmentedBytes / 1024.0 / 1024,8:F1} MB");

    // Der grosse Objektbereich wird normalerweise nicht verdichtet. Beide Puffer wachsen in
    // Zweierpotenzen, ihre abgelegten Vorgaenger liegen also dort herum.
    GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    long danachGemessen = GC.GetTotalMemory(forceFullCollection: true) - grundlage;
    GCMemoryInfo danach = GC.GetGCMemoryInfo();

    Console.WriteLine();
    Console.WriteLine("Nach dem Verdichten des grossen Objektbereichs");
    Console.WriteLine($"  {"Haldengroesse",-22}: {danach.HeapSizeBytes / 1024.0 / 1024,8:F1} MB");
    Console.WriteLine($"  {"davon Freiraum",-22}: {danach.FragmentedBytes / 1024.0 / 1024,8:F1} MB");
    Console.WriteLine($"  {"Gemessen",-22}: {danachGemessen / 1024.0 / 1024,8:F1} MB");
    Console.WriteLine($"  {"Differenz zu Feldern",-22}: {(danachGemessen - summe) / 1024.0 / 1024,8:F1} MB");
}
