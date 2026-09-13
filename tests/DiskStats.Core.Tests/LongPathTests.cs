using DiskStats.Core.Scanning;

namespace DiskStats.Core.Tests;

public class LongPathTests
{
    [Fact]
    public void Findet_dateien_jenseits_von_260_zeichen()
    {
        using var tree = new TestTree();

        // 40 Ebenen à 10 Zeichen ergeben rund 400 Zeichen plus Wurzel — sicher über MAX_PATH.
        string deep = string.Join('/', Enumerable.Range(0, 40).Select(i => $"ebene{i:D4}"));
        tree.AddFile(deep + "/ziel.bin", 777);

        Assert.True(tree.Root.Length + deep.Length > 260, "Testpfad war nicht lang genug");

        var sink = new CountingSink();
        var result = DirectoryWalker.Walk(tree.Root, sink, new WalkOptions());

        Assert.Equal(1, sink.FileCount);
        Assert.Equal(777, sink.TotalBytes);
        Assert.Equal(0, result.Errors.Count);
    }

    /// <summary>
    /// Der Walker geht ueber die .NET-Enumeration, die lange Pfade selbst mit \\?\ versieht.
    /// FileStorageInfo ruft CreateFileW dagegen direkt auf — ob ein Pfad jenseits von
    /// 300 Zeichen dort ankommt, haengt am Manifest (longPathAware) und der Systemrichtlinie.
    /// Genau das prueft dieser Test: gefunden UND vermessen, nicht nur gefunden.
    /// </summary>
    [Fact]
    public void Vermisst_dateien_jenseits_von_300_zeichen()
    {
        using var tree = new TestTree();

        // 30 Ebenen à 16 Zeichen ergeben rund 480 Zeichen plus Wurzel — sicher über 300.
        string deep = string.Join('/', Enumerable.Range(0, 30).Select(i => $"verzeichnis{i:D4}"));
        string relative = deep + "/ziel.bin";
        tree.AddFile(relative, 4321);
        string full = Path.GetFullPath(tree.PathOf(relative));

        Assert.True(full.Length > 300, $"Testpfad war nicht lang genug: {full.Length} Zeichen");

        var sink = new CountingSink();
        var result = DirectoryWalker.Walk(tree.Root, sink, new WalkOptions());

        Assert.Equal(1, sink.FileCount);
        Assert.Equal(4321, sink.TotalBytes);
        Assert.Equal(0, result.Errors.Count);

        // Darf in keinem Fall werfen. Ob ein Messwert herauskommt, entscheidet die
        // Systemrichtlinie: Ohne LongPathsEnabled lehnt CreateFileW den Pfad ab und
        // Read liefert korrekt Unknown — das ist dann kein Fehler dieses Codes.
        FileStorageInfo info = FileStorageInfo.Read(full, File.GetAttributes(full));

        Assert.True(info.AllocatedBytes >= 0,
            "FileStorageInfo.Read konnte den langen Pfad nicht oeffnen");
        Assert.True(info.LinkCount >= 1, "Eine gewoehnliche Datei hat mindestens einen Hardlink");
    }

    [Theory]
    [InlineData(@"C:\kurz.txt", @"\\?\C:\kurz.txt")]
    [InlineData(@"\\server\share\datei.bin", @"\\?\UNC\server\share\datei.bin")]
    [InlineData(@"\\?\C:\schon.txt", @"\\?\C:\schon.txt")]
    public void Versieht_win32_pfade_mit_erweitertem_praefix(string path, string expected)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Win32-Praefix nur unter Windows");
        Assert.Equal(expected, FileStorageInfo.ForNative(path), ignoreCase: true);
    }
}
