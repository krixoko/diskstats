using DiskStats.Core.Scanning;

namespace DiskStats.Core.Tests;

public class ReparsePointTests
{
    /// <summary>Legt eine Junction an. Gibt false zurueck, wenn das System das nicht zulaesst.</summary>
    private static bool TryCreateJunction(string link, string target)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe",
            $"/c mklink /J \"{link}\" \"{target}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = System.Diagnostics.Process.Start(psi)!;
        process.WaitForExit();
        return Directory.Exists(link);
    }

    [Fact]
    public void Junction_wird_nicht_verfolgt_und_zaehlt_nicht_doppelt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "nur unter Windows");

        using var tree = new TestTree();
        tree.AddFile("echt/gross.bin", 1000);

        Assert.True(TryCreateJunction(tree.PathOf("verweis"), tree.PathOf("echt")),
            "Junction konnte nicht angelegt werden — der Test haette sonst nur scheinbar bestanden.");

        var sink = new CountingSink();
        DirectoryWalker.Walk(tree.Root, sink, new WalkOptions());

        // gross.bin darf genau einmal gezaehlt werden, nicht zweimal ueber den Verweis.
        Assert.Equal(1, sink.FileCount);
        Assert.Equal(1000, sink.TotalBytes);
    }

    [Fact]
    public async Task Schleife_ueber_eine_junction_terminiert()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "nur unter Windows");

        using var tree = new TestTree();
        tree.AddDirectory("a");

        Assert.True(TryCreateJunction(tree.PathOf("a/zurueck"), tree.Root),
            "Junction konnte nicht angelegt werden — der Test haette sonst nur scheinbar bestanden.");

        var sink = new CountingSink();

        // Der eigentliche Test: der Aufruf kehrt ueberhaupt zurueck.
        // Laeuft er in eine Endlosschleife, wirft WaitAsync eine TimeoutException.
        Task<WalkResult> walk = Task.Run(() => DirectoryWalker.Walk(tree.Root, sink, new WalkOptions()));
        await walk.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
