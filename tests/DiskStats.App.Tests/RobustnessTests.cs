using DiskStats.Core.Diagnostics;

namespace DiskStats.App.Tests;

/// <summary>
/// Die Stellen, an denen die Anwendung frueher still scheitern oder sich selbst ueberholen
/// konnte: zwei Snapshot-Speicherungen zugleich, ein async void ohne Fang, ein Neustart mit
/// verfaelschten Argumenten.
/// </summary>
public sealed class SnapshotQueueTests
{
    [Fact]
    public async Task Fuehrt_auftraege_nacheinander_aus_auch_wenn_sie_gleichzeitig_kommen()
    {
        var queue = new SnapshotQueue();
        int running = 0, overlaps = 0;

        async Task Job()
        {
            if (Interlocked.Increment(ref running) > 1) Interlocked.Increment(ref overlaps);
            await Task.Delay(40);
            Interlocked.Decrement(ref running);
        }

        Task a = queue.Enqueue(Job);
        Task b = queue.Enqueue(Job);
        Task c = queue.Enqueue(Job);
        await Task.WhenAll(a, b, c);

        Assert.Equal(0, overlaps);
    }

    [Fact]
    public async Task Ein_fehlgeschlagener_auftrag_blockiert_die_folgenden_nicht()
    {
        var queue = new SnapshotQueue();
        bool second = false;

        Task failed = queue.Enqueue(() => throw new IOException("Platte voll"));
        Task next = queue.Enqueue(() => { second = true; return Task.CompletedTask; });

        await Task.WhenAll(failed, next);

        Assert.True(second);
        Assert.True(failed.IsCompletedSuccessfully, "der Fehler wird protokolliert, nicht weitergereicht");
    }
}

public sealed class FireAndForgetTests : IDisposable
{
    private readonly string _logs = DiagnosticLog.Folder;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "diskstats-fire-" + Guid.NewGuid().ToString("N"));

    public FireAndForgetTests()
    {
        Directory.CreateDirectory(_folder);
        DiagnosticLog.Folder = _folder;
    }

    public void Dispose()
    {
        DiagnosticLog.Folder = _logs;
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Eine_ausnahme_im_hintergrund_wird_protokolliert_statt_den_prozess_zu_beenden()
    {
        string? reported = null;

        await TaskGuard.Fire(Task.FromException(new InvalidOperationException("kaputt")),
            "Probe", message => reported = message);

        Assert.NotNull(reported);
        Assert.Contains("kaputt", reported);
        string log = File.ReadAllText(DiagnosticLog.FileFor(DateTime.Now));
        Assert.Contains("Probe", log);
        Assert.Contains("kaputt", log);
    }

    [Fact]
    public async Task Ein_abbruch_ist_kein_fehler()
    {
        string? reported = null;

        await TaskGuard.Fire(Task.FromCanceled(new CancellationToken(canceled: true)), "Probe", m => reported = m);

        Assert.Null(reported);
    }
}

public sealed class ElevationArgumentTests
{
    [Fact]
    public void Argumente_kommen_unveraendert_beim_neustart_an()
    {
        string[] arguments = ["DiskStats.exe", @"D:\Mein Ordner\", "--view", "Treemap", "er sagte \"hallo\""];

        IReadOnlyList<string> forwarded = Elevation.ArgumentsFor(arguments);

        // Das erste ist der Programmpfad und faellt weg; alles andere bleibt Zeichen fuer Zeichen.
        Assert.Equal(arguments.Skip(1), forwarded);
    }
}
