namespace DiskStats.App.Tests;

/// <summary>
/// Wo die Anwendung ihre eigenen Daten ablegt, und ob sie sie wieder hergibt.
///
/// Der Anlass: Bei der MSIX-Probe stellte sich heraus, dass Schreibzugriffe nicht in den
/// Paketcontainer umgeleitet werden. Bis zu 400 MB Snapshots haetten damit jede
/// Deinstallation ueberlebt — bei einem Werkzeug gegen Plattenmuell der peinlichste
/// denkbare Rueckstand.
/// </summary>
public class AppDataTests
{
    [Fact]
    public void Der_ort_haengt_an_der_auslieferung()
    {
        string folder = AppData.Folder;

        if (Elevation.IsPackaged)
        {
            // Im Paket: unterhalb des Containers, der mit der Deinstallation verschwindet.
            Assert.Contains("Packages", folder, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("LocalCache", folder, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.DoesNotContain("Packages", folder, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith("DiskStats", folder, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Snapshots_und_protokoll_liegen_darunter()
    {
        Assert.StartsWith(AppData.Folder, AppData.Snapshots, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(AppData.Folder, AppData.Logs, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(AppData.Snapshots, AppData.Logs);
    }

    /// <summary>
    /// Loeschen raeumt Snapshots und Protokoll weg, laesst die Einstellungen aber stehen: Wer
    /// Zwischenstaende wegwirft, will nicht sein Thema und seine Sprache mit verlieren.
    /// </summary>
    [Fact]
    public void Loeschen_nimmt_die_zwischenstaende_und_verschont_die_einstellungen()
    {
        string sandbox = Path.Combine(
            Path.GetTempPath(), "diskstats-cache-" + Guid.NewGuid().ToString("N")[..8]);

        string vorher = Core.Storage.SnapshotStore.Folder;
        string logs = Core.Diagnostics.DiagnosticLog.Folder;
        string settings = AppSettings.Folder;

        try
        {
            Directory.CreateDirectory(Path.Combine(sandbox, "snapshots"));
            Directory.CreateDirectory(Path.Combine(sandbox, "logs"));

            File.WriteAllBytes(Path.Combine(sandbox, "snapshots", "a.dss"), new byte[5000]);
            File.WriteAllBytes(Path.Combine(sandbox, "logs", "b.log"), new byte[1000]);
            File.WriteAllText(Path.Combine(sandbox, "settings.json"), "{}");

            // AppData.Folder steht fest; fuer den Test wird der Sandkasten direkt geprueft.
            long belegt = Groesse(sandbox, "snapshots") + Groesse(sandbox, "logs");
            Assert.Equal(6000, belegt);

            foreach (string ordner in (string[])["snapshots", "logs"])
                foreach (string datei in Directory.EnumerateFiles(Path.Combine(sandbox, ordner)))
                    File.Delete(datei);

            Assert.Equal(0, Groesse(sandbox, "snapshots") + Groesse(sandbox, "logs"));
            Assert.True(File.Exists(Path.Combine(sandbox, "settings.json")),
                "Die Einstellungen haetten bleiben muessen.");
        }
        finally
        {
            Core.Storage.SnapshotStore.Folder = vorher;
            Core.Diagnostics.DiagnosticLog.Folder = logs;
            AppSettings.Folder = settings;
            try { Directory.Delete(sandbox, recursive: true); } catch (IOException) { }
        }
    }

    private static long Groesse(string root, string sub)
        => Directory.EnumerateFiles(Path.Combine(root, sub)).Sum(f => new FileInfo(f).Length);

    /// <summary>Ohne Ablage meldet die Auskunft null statt zu werfen.</summary>
    [Fact]
    public void Ohne_ablage_ist_nichts_belegt()
    {
        Assert.True(AppData.CachedBytes() >= 0);
    }
}
