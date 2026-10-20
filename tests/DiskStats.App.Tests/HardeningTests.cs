using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using DiskStats.App.Controls;
using DiskStats.App.Localization;
using DiskStats.Core.Cleanup;
using DiskStats.Core.Diagnostics;
using DiskStats.Core.Scanning;
using DiskStats.Core.Storage;

namespace DiskStats.App.Tests;

/// <summary>
/// Die Verträglichkeit der Operationen als Tabelle: Wer eine fuenfte Operation hinzufuegt, sieht
/// hier sofort, was er verschiebt. Vorher stand sie verstreut in ueber zwanzig Bedingungen.
/// </summary>
public sealed class OperationGateTests
{
    [Fact]
    public void Im_ruhezustand_geht_alles()
    {
        var gate = new OperationGate();

        Assert.True(gate.CanStartScan);
        Assert.True(gate.CanBrowse);
        Assert.True(gate.CanStage);
        Assert.True(gate.CanAnalyze);
        Assert.True(gate.CanChangeFiles);
        Assert.False(gate.AnyRunning);
    }

    [Fact]
    public void Waehrend_eines_scans_wird_nicht_navigiert_aufgeraeumt_oder_vorgemerkt()
    {
        var gate = new OperationGate { Scanning = true };

        Assert.False(gate.CanBrowse);
        Assert.False(gate.CanStage);
        Assert.False(gate.CanAnalyze);
        Assert.False(gate.CanChangeFiles);
        // Ein neuer Scan ersetzt den laufenden.
        Assert.True(gate.CanStartScan);
    }

    [Fact]
    public void Waehrend_dateien_geaendert_werden_ruht_alles_ausser_dem_vormerken()
    {
        var gate = new OperationGate { ChangingFiles = true };

        Assert.False(gate.CanStartScan);
        Assert.False(gate.CanBrowse);
        Assert.False(gate.CanChangeFiles);
        Assert.False(gate.CanAnalyze);
        // Die Liste selbst aendert keine Dateien.
        Assert.True(gate.CanStage);
    }

    [Fact]
    public void Ein_refresh_sperrt_weitere_aenderungen_aber_nicht_die_ansicht()
    {
        var gate = new OperationGate { Refreshing = true };

        Assert.False(gate.CanChangeFiles);
        Assert.True(gate.CanBrowse);
        Assert.True(gate.CanStartScan);
    }

    [Fact]
    public void Der_benchmark_sperrt_scan_und_aenderungen_aber_nicht_die_navigation()
    {
        var gate = new OperationGate { BenchmarkOpen = true };

        Assert.False(gate.CanStartScan);
        Assert.False(gate.CanChangeFiles);
        Assert.False(gate.CanAnalyze);
        Assert.True(gate.CanBrowse);
        Assert.True(gate.AnyRunning == false);
    }
}

public sealed class CommandLineTests
{
    [Theory]
    [InlineData(new[] { "diskstats" }, "")]
    [InlineData(new[] { "diskstats", @"D:\daten" }, @"D:\daten")]
    [InlineData(new[] { "diskstats", "--dark", @"D:\daten" }, @"D:\daten")]
    [InlineData(new[] { "diskstats", "--view", "Files", @"D:\daten" }, @"D:\daten")]
    [InlineData(new[] { "diskstats", @"D:\daten", "--view", "Files", "--dark" }, @"D:\daten")]
    [InlineData(new[] { "diskstats", "--view", "Files" }, "")]
    [InlineData(new[] { "diskstats", "--elevated-restart" }, "")]
    [InlineData(new[] { "diskstats", @"D:\daten", "--elevated-restart" }, @"D:\daten")]
    public void Findet_den_pfad_zwischen_den_schaltern(string[] args, string expected)
        => Assert.Equal(expected, CommandLine.PathFrom(args));

    [Fact]
    public void Der_wert_von_view_wird_nie_zum_pfad()
        => Assert.Equal("", CommandLine.PathFrom(["diskstats", "--VIEW", "Treemap"]));

    [Theory]
    [InlineData(new[] { "diskstats" }, false)]
    [InlineData(new[] { "diskstats", @"D:\daten" }, false)]
    [InlineData(new[] { "diskstats", "--elevated-restart" }, true)]
    [InlineData(new[] { "diskstats", @"D:\daten", "--ELEVATED-RESTART" }, true)]
    public void Erkennt_den_neustart_mit_erhoehten_rechten(string[] args, bool expected)
        => Assert.Equal(expected, CommandLine.IsElevatedRestart(args));

    [Fact]
    public void Der_programmname_an_erster_stelle_zaehlt_nicht()
        => Assert.False(CommandLine.IsElevatedRestart(["--elevated-restart"]));
}

/// <summary>
/// Der Neustart mit erhoehten Rechten: Der alte Prozess haelt die Sperre noch, wenn der neue
/// startet. Der neue muss warten koennen, statt sich fuer einen zweiten Start zu halten.
/// </summary>
public sealed class SingleInstanceTests : IDisposable
{
    private readonly string _name = @"Global\DiskStats.Test." + Guid.NewGuid().ToString("N");

    public void Dispose() => SingleInstance.Release();

    /// <summary>Haelt die Sperre auf einem eigenen Faden, wie es ein anderer Prozess taete.</summary>
    private (Thread Holder, ManualResetEventSlim Release) Hold()
    {
        var acquired = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using var mutex = new Mutex(initiallyOwned: true, _name, out _);
            acquired.Set();
            release.Wait();
            mutex.ReleaseMutex();
        }) { IsBackground = true };
        holder.Start();
        Assert.True(acquired.Wait(TimeSpan.FromSeconds(10)));
        return (holder, release);
    }

    [Fact]
    public void Erster_start_bekommt_die_sperre()
        => Assert.True(SingleInstance.Claim(_name, TimeSpan.Zero));

    [Fact]
    public void Zweiter_start_ohne_warten_beendet_sich()
    {
        (Thread holder, ManualResetEventSlim release) = Hold();
        try { Assert.False(SingleInstance.Claim(_name, TimeSpan.Zero)); }
        finally { release.Set(); holder.Join(); }
    }

    [Fact]
    public void Neustart_wartet_bis_der_vorgaenger_die_sperre_freigibt()
    {
        (Thread holder, ManualResetEventSlim release) = Hold();
        // Der Vorgaenger schliesst sich "einen Moment spaeter".
        var closing = new Thread(() => { Thread.Sleep(300); release.Set(); }) { IsBackground = true };
        closing.Start();

        bool claimed = SingleInstance.Claim(_name, TimeSpan.FromSeconds(10));

        Assert.True(claimed);
        closing.Join();
        holder.Join();
    }

    [Fact]
    public void Neustart_gibt_auf_wenn_der_vorgaenger_nie_loslaesst()
    {
        (Thread holder, ManualResetEventSlim release) = Hold();
        try
        {
            var started = System.Diagnostics.Stopwatch.StartNew();

            Assert.False(SingleInstance.Claim(_name, TimeSpan.FromMilliseconds(200)));
            Assert.InRange(started.ElapsedMilliseconds, 150, 5000);
        }
        finally { release.Set(); holder.Join(); }
    }
}

public sealed class HardeningWindowTests : IDisposable
{
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), "diskstats-hardening-" + Guid.NewGuid());
    private readonly string _settings = AppSettings.Folder;
    private readonly string _snapshots = SnapshotStore.Folder;
    private readonly string _logs = DiagnosticLog.Folder;
    private readonly Language _language = Loc.Current;
    private readonly List<MainWindow> _windows = [];

    public HardeningWindowTests()
    {
        Directory.CreateDirectory(Path.Combine(_sandbox, "tree"));
        File.WriteAllBytes(Path.Combine(_sandbox, "tree", "a.txt"), new byte[100]);
        AppSettings.Folder = Path.Combine(_sandbox, "settings");
        SnapshotStore.Folder = Path.Combine(_sandbox, "snapshots");
        DiagnosticLog.Folder = Path.Combine(_sandbox, "logs");
        AppSettings.Current.ElevationPrompt = false;
        AppSettings.Current.AutoElevate = false;
        AppSettings.Current.LastPath = string.Empty;
        Loc.Current = Language.English;
    }

    public void Dispose()
    {
        foreach (MainWindow window in _windows) window.Close();
        AppSettings.Folder = _settings;
        SnapshotStore.Folder = _snapshots;
        DiagnosticLog.Folder = _logs;
        Loc.Current = _language;
        Directory.Delete(_sandbox, recursive: true);
    }

    [Fact]
    public void Verschiebefehler_erscheinen_als_text_in_beiden_sprachen_nicht_als_enumname()
    {
        var protectedPath = new FileMoveException(FileMoveProblem.Protected, @"C:\Windows",
            new Refusal(RefusalKind.SystemFolder, "Windows"));
        var linked = new FileMoveException(FileMoveProblem.Linked, @"D:\link");
        var bad = new FileMoveException(FileMoveProblem.InvalidDestination, @"D:\ziel");
        var missing = new FileMoveException(FileMoveProblem.DestinationMissing, @"D:\weg");
        var space = new FileMoveException(FileMoveProblem.InsufficientSpace, @"D:\", needed: 2048, available: 1024);

        foreach (Language language in Enum.GetValues<Language>())
        {
            Loc.Current = language;

            foreach (FileMoveException ex in new[] { protectedPath, linked, bad, missing, space })
            {
                string text = MainWindow.Say(ex);

                Assert.DoesNotContain(ex.Problem.ToString(), text);
                Assert.Contains(ex.Path, text);
                Assert.DoesNotContain("{", text);
            }
        }

        Loc.Current = Language.German;
        Assert.Contains("Systemordner Windows", MainWindow.Say(protectedPath));
        Assert.Contains("Cloud", MainWindow.Say(linked));
    }

    [AvaloniaFact]
    public async Task Mft_fortschritt_erscheint_in_statuszeile_und_abzeichen_solange_der_scan_laeuft()
    {
        var window = new MainWindow([]);
        _windows.Add(window);
        var finish = new TaskCompletionSource();
        Task scan = window.RunScanAsync(Path.Combine(_sandbox, "tree"), (_, _, _, _) => finish.Task);

        window.ReportMftProgress(0.42);

        Assert.Contains("42", window.FindControl<TextBlock>("StatusText")!.Text);
        Assert.Equal(0.42, window.FindControl<DriveBadge>("Badge")!.Progress);

        finish.SetResult();
        await scan;
    }

    [AvaloniaFact]
    public async Task Verspaeteter_mft_fortschritt_nach_dem_scan_aendert_nichts_mehr()
    {
        var window = new MainWindow([]);
        _windows.Add(window);
        await window.RunScanAsync(Path.Combine(_sandbox, "tree"));
        string? status = window.FindControl<TextBlock>("StatusText")!.Text;

        window.ReportMftProgress(0.9);

        Assert.Equal(status, window.FindControl<TextBlock>("StatusText")!.Text);
    }
}
