using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using DiskStats.Core.Scanning;
using DiskStats.Core.Storage;

namespace DiskStats.Core.Tests;

/// <summary>
/// Der MFT-Scan liest das Dateisystem am Betriebssystem vorbei. Synthetische Records pruefen den
/// Parser, aber nicht, ob das Ergebnis mit dem uebereinstimmt, was Windows selbst meldet. Dieser
/// Test legt dafuer ein echtes virtuelles NTFS-Laufwerk an, fuellt es mit den Faellen, an denen
/// die beiden Wege auseinanderlaufen koennten (Hardlink, komprimiert, sparse, Junction,
/// Datenstrom, Langpfad, viele Dateien) und vergleicht beide Scans Eintrag fuer Eintrag.
///
/// Opt-in, weil er ein virtuelles Laufwerk einhaengt: <c>DISKSTATS_VHD_TESTS=1</c>, Windows und
/// Administratorrechte. Ohne die Variable wird er uebersprungen; mit ihr aber ist ein fehlendes
/// Recht ein Fehler, kein Ueberspringen — sonst bliebe die Luecke in der CI unbemerkt.
/// </summary>
public sealed class NtfsDifferentialTests
{
    private const string OptIn = "DISKSTATS_VHD_TESTS";

    [Fact]
    public void Mft_scan_und_verzeichnisscan_liefern_denselben_baum()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(OptIn) == "1",
            $"Opt-in: {OptIn}=1 haengt ein virtuelles Laufwerk ein (Windows, Administrator).");
        Assert.True(OperatingSystem.IsWindows() && IsAdministrator(),
            "Mit gesetztem Opt-in braucht der Test Windows und Administratorrechte.");

        using var disk = VirtualNtfsDisk.Create();
        string data = Path.Combine(disk.Root, "data");
        Populate(disk.Root, data);

        // 1) Unterordner als Wurzel: der Weg, auf dem die Wurzel ueber ihre Dateikennung gefunden wird.
        NodeStore viaDirectory = Scan(data, ScanMethod.Directory);
        NodeStore viaMft = Scan(data, ScanMethod.Ntfs);
        AssertSameTree(viaDirectory, viaMft, data + Path.DirectorySeparatorChar, data + Path.DirectorySeparatorChar);

        // 2) Ganzes Laufwerk als Wurzel: der Weg ueber den Wurzel-Record. Verglichen wird nur "data" -
        //    "System Volume Information" und der Papierkorb sehen beide Wege verschieden.
        NodeStore wholeDrive = Scan(disk.Root, ScanMethod.Ntfs);
        AssertSameTree(viaDirectory, wholeDrive, data + Path.DirectorySeparatorChar, data + Path.DirectorySeparatorChar);

        AssertFixtureCovered(viaMft, data);
    }

    // ---------- Scan ----------

    private static NodeStore Scan(string root, ScanMethod method)
    {
        var buffer = new NodeBuffer();
        ScanCoordinator.Result result = ScanCoordinator.Scan(root, buffer,
            new WalkOptions { Method = method, WorkerCount = 4 });

        // Ohne diese Pruefung verglichen wir bei einem stillen Rueckfall den Verzeichnisscan mit sich selbst.
        Assert.True(result.Method == method,
            $"{root}: erwartet {method}, geliefert {result.Method}. Grund des Rueckfalls: {result.Fallback}");

        NodeStore store = NodeStoreBuilder.Build(result.Buffer, root);
        SizeAggregator.Aggregate(store);
        return store;
    }

    private readonly record struct Facts(bool IsDirectory, long Size, long Allocation, uint Links, bool Reparse, long MTime);

    /// <summary>Alle Eintraege unterhalb von <paramref name="prefix"/>, nach Pfad relativ dazu.</summary>
    private static SortedDictionary<string, Facts> Flatten(NodeStore store, string prefix)
    {
        var all = new SortedDictionary<string, Facts>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < store.Count; i++)
        {
            if (store.IsRemoved(i)) continue;
            string path = store.PathOf(i);
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            all[path[prefix.Length..]] = new Facts(
                store.IsDirectory(i), store.Size[i], store.AllocationOf(i), store.LinksOf(i),
                (store.Flags[i] & EntryFlags.ReparsePoint) != 0, store.MTime[i]);
        }

        return all;
    }

    private static void AssertSameTree(NodeStore expected, NodeStore actual, string expectedPrefix, string actualPrefix)
    {
        SortedDictionary<string, Facts> a = Flatten(expected, expectedPrefix);
        SortedDictionary<string, Facts> b = Flatten(actual, actualPrefix);
        var problems = new List<string>();

        foreach (string path in a.Keys.Except(b.Keys, StringComparer.OrdinalIgnoreCase))
            problems.Add($"nur im Verzeichnisscan: {path}");
        foreach (string path in b.Keys.Except(a.Keys, StringComparer.OrdinalIgnoreCase))
            problems.Add($"nur im MFT-Scan: {path}");

        foreach ((string path, Facts want) in a)
        {
            if (!b.TryGetValue(path, out Facts got)) continue;

            if (want.IsDirectory != got.IsDirectory) problems.Add($"{path}: Ordner {want.IsDirectory} <> {got.IsDirectory}");
            if (want.Reparse != got.Reparse) problems.Add($"{path}: Reparse {want.Reparse} <> {got.Reparse}");
            if (want.IsDirectory) continue; // Ordner: Zeit und Belegung folgen aus dem Inhalt

            if (want.Size != got.Size) problems.Add($"{path}: Groesse {want.Size} <> {got.Size}");
            if (want.Allocation != got.Allocation) problems.Add($"{path}: Belegung {want.Allocation} <> {got.Allocation}");
            if (want.Links != got.Links) problems.Add($"{path}: Links {want.Links} <> {got.Links}");
            if (Math.Abs(want.MTime - got.MTime) > TimeSpan.FromSeconds(2).Ticks)
                problems.Add($"{path}: Zeit {new DateTime(want.MTime, DateTimeKind.Utc):O} <> {new DateTime(got.MTime, DateTimeKind.Utc):O}");
        }

        Assert.True(problems.Count == 0,
            $"{problems.Count} Abweichung(en) zwischen Verzeichnisscan und MFT-Scan:{Environment.NewLine}"
            + string.Join(Environment.NewLine, problems.Take(40)));
        Assert.True(a.Count > 3000, $"zu wenige Eintraege verglichen: {a.Count}");
    }

    /// <summary>Stellt sicher, dass die schwierigen Faelle wirklich im Baum waren und nicht stillschweigend ausfielen.</summary>
    private static void AssertFixtureCovered(NodeStore store, string data)
    {
        SortedDictionary<string, Facts> all = Flatten(store, data + Path.DirectorySeparatorChar);

        Assert.True(all["junction"].Reparse, "Junction fehlt oder ist nicht als Verknuepfung erkannt");
        Assert.Equal(2u, all["hard-a.bin"].Links);
        Assert.Equal(2u, all["hard-b.bin"].Links);
        Assert.True(all["compressed.bin"].Allocation < all["compressed.bin"].Size / 2, "komprimierte Datei belegt nicht weniger");
        Assert.True(all["sparse.bin"].Allocation < all["sparse.bin"].Size / 2, "Sparse-Datei belegt nicht weniger");
        Assert.Equal(0, all["empty.bin"].Size);
        Assert.Contains(all.Keys, k => k.Contains("ünïcödé"));
        Assert.Contains(all.Keys, k => k.Length > 260);
        Assert.Equal(3000, all.Keys.Count(k => k.StartsWith("bulk" + Path.DirectorySeparatorChar)));

        // Beide Namen derselben Datei zusammen belegen den Platz nur einmal.
        Assert.True(store.PhysicalSize[0] < store.Size[0] + 4096L * all.Count);
    }

    // ---------- Fixture ----------

    private static void Populate(string drive, string data)
    {
        Directory.CreateDirectory(data);
        string outside = Path.Combine(drive, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "unreachable.txt"), "darf im Scan nicht auftauchen");

        File.WriteAllBytes(Path.Combine(data, "resident.txt"), new byte[200]);     // passt in den MFT-Record
        File.WriteAllBytes(Path.Combine(data, "medium.bin"), Pattern(5_000));
        File.WriteAllBytes(Path.Combine(data, "large.bin"), Pattern(3 * 1024 * 1024));
        File.WriteAllBytes(Path.Combine(data, "empty.bin"), []);
        File.WriteAllText(Path.Combine(data, "ünïcödé-Datei ✓.txt"), "Umlaute und Sonderzeichen");
        File.WriteAllText(Path.Combine(data, "mit-datenstrom.txt"), "hauptstrom");
        File.WriteAllText(Path.Combine(data, "mit-datenstrom.txt") + ":zusatz", "versteckter zweiter Strom, der nicht zaehlt");
        File.SetAttributes(Path.Combine(data, "resident.txt"), FileAttributes.Hidden | FileAttributes.ReadOnly);

        Directory.CreateDirectory(Path.Combine(data, "leerer-ordner"));

        // Viele Dateien, damit die MFT mehrere Bloecke und Extents bekommt.
        string bulk = Path.Combine(data, "bulk");
        Directory.CreateDirectory(bulk);
        for (int i = 0; i < 3000; i++) File.WriteAllBytes(Path.Combine(bulk, $"f{i:D4}.dat"), new byte[100 + i % 50]);

        // Langpfad: laenger als die klassischen 260 Zeichen.
        string deep = data;
        for (int level = 0; level < 8; level++) deep = Path.Combine(deep, new string((char)('a' + level), 40));
        Directory.CreateDirectory(deep);
        File.WriteAllBytes(Path.Combine(deep, "tief.bin"), Pattern(1_000));

        File.WriteAllBytes(Path.Combine(data, "hard-a.bin"), Pattern(8_000));
        Run("cmd.exe", $"/c mklink /H \"{Path.Combine(data, "hard-b.bin")}\" \"{Path.Combine(data, "hard-a.bin")}\"");

        File.WriteAllBytes(Path.Combine(data, "compressed.bin"), new byte[1024 * 1024]);
        Run("compact.exe", $"/c /f \"{Path.Combine(data, "compressed.bin")}\"");

        string sparse = Path.Combine(data, "sparse.bin");
        Run("fsutil.exe", $"file createnew \"{sparse}\" 10485760");
        Run("fsutil.exe", $"sparse setflag \"{sparse}\"");
        Run("fsutil.exe", $"sparse setrange \"{sparse}\" 0 10485760");
        using (var stream = new FileStream(sparse, FileMode.Open, FileAccess.Write, FileShare.None))
            stream.Write(Pattern(4096));

        // Die Junction fuehrt aus "data" hinaus; folgen duerfte ihr keiner der Scans.
        Run("cmd.exe", $"/c mklink /J \"{Path.Combine(data, "junction")}\" \"{outside}\"");
    }

    private static byte[] Pattern(int length)
    {
        byte[] bytes = new byte[length];
        new Random(length).NextBytes(bytes);
        return bytes;
    }

    // ---------- Prozesse ----------

    private static string Run(string file, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{file} {arguments} antwortet nicht.");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{file} {arguments} endete mit {process.ExitCode}:{Environment.NewLine}{output}");
        return output;
    }

    [SupportedOSPlatform("windows")]
    private static bool IsAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Ein NTFS-Laufwerk in einer VHD-Datei, solange das Objekt lebt.</summary>
    private sealed class VirtualNtfsDisk : IDisposable
    {
        private readonly string _file;

        private VirtualNtfsDisk(string file, string root)
        {
            _file = file;
            Root = root;
        }

        /// <summary>Laufwerkswurzel wie "Z:\".</summary>
        public string Root { get; }

        public static VirtualNtfsDisk Create()
        {
            string file = Path.Combine(Path.GetTempPath(), "diskstats-test-" + Guid.NewGuid().ToString("N") + ".vhd");
            char letter = FreeLetter();

            try
            {
                Diskpart($"""
                    create vdisk file="{file}" maximum=256 type=expandable
                    select vdisk file="{file}"
                    attach vdisk
                    create partition primary
                    format fs=ntfs quick label=DSTEST
                    assign letter={letter}
                    """);

                string root = letter + @":\";
                WaitUntilReady(root);
                return new VirtualNtfsDisk(file, root);
            }
            catch
            {
                Detach(file);
                throw;
            }
        }

        public void Dispose() => Detach(_file);

        private static void Detach(string file)
        {
            try { Diskpart($"select vdisk file=\"{file}\"\ndetach vdisk"); }
            catch (InvalidOperationException) { /* nie eingehaengt */ }
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try { File.Delete(file); return; }
                catch (IOException) { Thread.Sleep(500); }
            }
        }

        private static void Diskpart(string script)
        {
            string path = Path.Combine(Path.GetTempPath(), "diskstats-diskpart-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(path, script);
            try { Run("diskpart.exe", $"/s \"{path}\""); }
            finally { File.Delete(path); }
        }

        private static char FreeLetter()
        {
            var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
            for (char c = 'Z'; c >= 'G'; c--)
                if (!used.Contains(c)) return c;
            throw new InvalidOperationException("Kein freier Laufwerksbuchstabe.");
        }

        private static void WaitUntilReady(string root)
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(30))
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady && drive.DriveFormat == "NTFS") return;
                Thread.Sleep(250);
            }
            throw new TimeoutException($"{root} wurde nicht als NTFS-Laufwerk bereit.");
        }
    }
}
