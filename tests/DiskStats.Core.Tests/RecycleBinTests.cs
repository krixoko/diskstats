using DiskStats.Core.Cleanup;

namespace DiskStats.Core.Tests;

/// <summary>
/// Diese Tests loeschen wirklich — mit Absicht.
///
/// Der Aufruf von SHFileOperation haengt an der genauen Ausrichtung einer Struktur im
/// Speicher. Stimmt sie nicht, faellt das beim Uebersetzen nicht auf, sondern erst im
/// Betrieb, und dann moeglicherweise am falschen Pfad. Nur ein echter Durchlauf zeigt,
/// dass der Aufruf tut, was er soll.
///
/// Es werden ausschliesslich eigens angelegte Dateien im Temp-Verzeichnis behandelt.
/// </summary>
public class RecycleBinTests
{
    private static string NewFile(string content = "wegwerf")
    {
        string path = Path.Combine(
            Path.GetTempPath(), "diskstats-papierkorb-" + Guid.NewGuid().ToString("N") + ".txt");

        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Verschiebt_eine_datei_und_meldet_erfolg()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "nur unter Windows");

        string path = NewFile();
        Assert.True(File.Exists(path));

        RecycleBin.Result result = RecycleBin.Send([path]);

        Assert.True(result.Ok, $"Fehlercode {result.Code}");
        Assert.False(result.Aborted);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Verschiebt_mehrere_auf_einmal()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "nur unter Windows");

        string[] paths = [NewFile(), NewFile(), NewFile()];

        Assert.True(RecycleBin.Send(paths).Ok);
        Assert.All(paths, p => Assert.False(File.Exists(p)));
    }

    [Fact]
    public void Verschiebt_einen_ganzen_ordner_samt_inhalt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "nur unter Windows");

        string folder = Path.Combine(
            Path.GetTempPath(), "diskstats-papierkorb-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Path.Combine(folder, "tief"));
        File.WriteAllText(Path.Combine(folder, "tief", "datei.txt"), "inhalt");

        Assert.True(RecycleBin.Send([folder]).Ok);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void Eine_leere_liste_ist_kein_fehler()
        => Assert.True(RecycleBin.Send([]).Ok);

    /// <summary>
    /// Die Shell kennt das \\?\-Praefix nicht; der Baum liefert es aber, wenn der Scan mit
    /// langen Pfaden umgehen musste. Der Papierkorb soll es abstreifen statt zu scheitern.
    /// </summary>
    [Fact]
    public void Nimmt_pfade_mit_erweitertem_praefix_an()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "nur unter Windows");

        string path = NewFile();

        RecycleBin.Result result = RecycleBin.Send([@"\\?\" + path]);

        Assert.True(result.Ok, $"Fehlercode {result.Code}");
        Assert.False(File.Exists(path));
    }

    /// <summary>
    /// Die Shell zeigt ihre Warnungen im Faden des Aufrufers; ein MTA-Faden aus dem Pool ist
    /// dafuer der falsche Ort. Der Aufruf laeuft deshalb auf einem eigenen STA-Faden — egal,
    /// woher er kommt.
    /// </summary>
    [Fact]
    public async Task Laeuft_auch_aus_einem_pool_faden_heraus()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "nur unter Windows");

        string path = NewFile();

        RecycleBin.Result result = await Task.Run(() => RecycleBin.Send([path]), TestContext.Current.CancellationToken);

        Assert.True(result.Ok, $"Fehlercode {result.Code}");
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Meldet_einen_fehler_statt_zu_schweigen()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "nur unter Windows");

        string missing = Path.Combine(Path.GetTempPath(), "diskstats-gibt-es-nicht-" + Guid.NewGuid());

        RecycleBin.Result result = RecycleBin.Send([missing]);

        Assert.False(result.Ok);
        Assert.NotEqual(0, result.Code);
    }
}
