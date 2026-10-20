using DiskStats.Core.Diagnostics;

namespace DiskStats.Core.Tests;

/// <summary>
/// Die Aufbewahrung ist der eigentliche Zweck dieses Protokolls — sie zu behaupten genuegt
/// nicht. Jeder Test setzt den Ordner auf ein eigenes Verzeichnis, damit er unabhaengig laeuft.
/// </summary>
public class DiagnosticLogTests : IDisposable
{
    private readonly string _folder;
    private readonly string _previous;

    public DiagnosticLogTests()
    {
        _previous = DiagnosticLog.Folder;
        _folder = Path.Combine(Path.GetTempPath(), "diskstats-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
        DiagnosticLog.Folder = _folder;
    }

    public void Dispose()
    {
        DiagnosticLog.Folder = _previous;
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private void Given(DateTime month, int bytes = 64)
        => File.WriteAllBytes(DiagnosticLog.FileFor(month), new byte[bytes]);

    [Fact]
    public void Schreibt_in_die_datei_des_laufenden_monats()
    {
        DiagnosticLog.Write("etwas ging schief");

        string expected = DiagnosticLog.FileFor(DateTime.Now);
        Assert.True(File.Exists(expected));
        Assert.Contains("etwas ging schief", File.ReadAllText(expected));
    }

    [Fact]
    public void Haengt_die_ausnahme_an_die_meldung()
    {
        DiagnosticLog.Write("Scan fehlgeschlagen", new UnauthorizedAccessException("kein Zugriff"));

        string text = File.ReadAllText(DiagnosticLog.FileFor(DateTime.Now));
        Assert.Contains("UnauthorizedAccessException", text);
        Assert.Contains("kein Zugriff", text);
    }

    private static Exception Thrown()
    {
        try { throw new InvalidOperationException("aeussere Ursache", new IOException("innere Ursache")); }
        catch (InvalidOperationException ex) { return ex; }
    }

    [Fact]
    public void Schreibt_stacktrace_und_innere_ursache_mit()
    {
        DiagnosticLog.Write("Scan fehlgeschlagen", Thrown());

        string text = File.ReadAllText(DiagnosticLog.FileFor(DateTime.Now));

        // Die Kurzfassung bleibt in einer Zeile, der Rest steht darunter.
        Assert.Contains("Scan fehlgeschlagen  |  InvalidOperationException: aeussere Ursache", text);
        Assert.Contains("innere Ursache", text);
        Assert.Contains("   at ", text);
        Assert.Contains(nameof(Thrown), text);
    }

    [Fact]
    public void Schreibt_die_kopfzeile_einmal_je_datei_und_sitzung()
    {
        DiagnosticLog.Write("eins");
        DiagnosticLog.Write("zwei");

        string[] lines = File.ReadAllLines(DiagnosticLog.FileFor(DateTime.Now));

        Assert.StartsWith("=== DiskStats ", lines[0]);
        Assert.Single(lines, l => l.StartsWith("=== DiskStats ", StringComparison.Ordinal));
        Assert.Contains(".NET", lines[0]);
    }

    [Fact]
    public void Kuerzt_einen_uferlosen_eintrag()
    {
        DiagnosticLog.Write("riesig", new InvalidOperationException(new string('x', DiagnosticLog.MaxEntryChars * 4)));

        long size = new FileInfo(DiagnosticLog.FileFor(DateTime.Now)).Length;

        Assert.True(size < DiagnosticLog.MaxEntryChars * 3, $"{size} Bytes fuer einen Eintrag");
        Assert.Contains("[... gekuerzt]", File.ReadAllText(DiagnosticLog.FileFor(DateTime.Now)));
    }

    [Fact]
    public void Behaelt_zwoelf_monate_und_verwirft_was_aelter_ist()
    {
        var now = new DateTime(2026, 9, 15);

        for (int back = 0; back < 18; back++) Given(now.AddMonths(-back));
        Assert.Equal(18, Directory.GetFiles(_folder).Length);

        DiagnosticLog.Prune(now);

        string[] left = Directory.GetFiles(_folder);
        Assert.Equal(DiagnosticLog.RetentionMonths, left.Length);

        // Der laufende Monat und die elf davor bleiben; der dreizehnte faellt.
        Assert.Contains(DiagnosticLog.FileFor(now), left);
        Assert.Contains(DiagnosticLog.FileFor(now.AddMonths(-11)), left);
        Assert.DoesNotContain(DiagnosticLog.FileFor(now.AddMonths(-12)), left);
    }

    [Fact]
    public void Laesst_alles_stehen_was_innerhalb_der_frist_liegt()
    {
        var now = new DateTime(2026, 9, 15);
        for (int back = 0; back < 5; back++) Given(now.AddMonths(-back));

        Assert.Equal(0, DiagnosticLog.Prune(now));
        Assert.Equal(5, Directory.GetFiles(_folder).Length);
    }

    [Fact]
    public void Greift_zusaetzlich_bei_ueberschreiten_der_obergrenze()
    {
        var now = new DateTime(2026, 9, 15);

        // Drei Monate innerhalb der Frist, aber zusammen ueber der Grenze.
        for (int back = 0; back < 3; back++)
            Given(now.AddMonths(-back), (int)(DiagnosticLog.MaxTotalBytes / 2));

        Assert.True(DiagnosticLog.Prune(now) > 0);

        long total = Directory.GetFiles(_folder).Sum(f => new FileInfo(f).Length);
        Assert.True(total <= DiagnosticLog.MaxTotalBytes, $"{total} Bytes verblieben");
    }

    [Fact]
    public void Haelt_die_obergrenze_auch_nach_einer_altersloeschung_ein()
    {
        var now = new DateTime(2026, 9, 15);

        // Drei veraltete Monate — sie fallen wegen des Alters. Und vier aktuelle, von denen
        // jeder allein die Haelfte des Budgets belegt: Zwei muessen zusaetzlich gehen.
        for (int back = 15; back < 18; back++) Given(now.AddMonths(-back));
        for (int back = 0; back < 4; back++)
            Given(now.AddMonths(-back), (int)(DiagnosticLog.MaxTotalBytes / 2));

        DiagnosticLog.Prune(now);

        long total = Directory.GetFiles(_folder).Sum(f => new FileInfo(f).Length);
        Assert.True(total <= DiagnosticLog.MaxTotalBytes, $"{total} Bytes verblieben");
        Assert.True(File.Exists(DiagnosticLog.FileFor(now)), "das laufende Protokoll bleibt");
    }

    [Fact]
    public void Behaelt_immer_mindestens_das_laufende_protokoll()
    {
        var now = new DateTime(2026, 9, 15);
        Given(now, (int)(DiagnosticLog.MaxTotalBytes * 3));

        DiagnosticLog.Prune(now);

        // Auch wenn es allein die Grenze sprengt: ohne Protokoll gaebe es nichts zu lesen.
        Assert.Single(Directory.GetFiles(_folder));
    }

    [Fact]
    public void Ignoriert_fremde_dateien_im_ordner()
    {
        var now = new DateTime(2026, 9, 15);
        Given(now.AddMonths(-20));
        File.WriteAllText(Path.Combine(_folder, "notizen.txt"), "nicht anfassen");

        DiagnosticLog.Prune(now);

        Assert.True(File.Exists(Path.Combine(_folder, "notizen.txt")));
    }
}
