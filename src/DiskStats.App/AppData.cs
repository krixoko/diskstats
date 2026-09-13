using System.Runtime.InteropServices;
using System.Text;
using DiskStats.Core.Diagnostics;
using DiskStats.Core.Storage;

namespace DiskStats.App;

/// <summary>
/// Wo die Anwendung ihre eigenen Daten ablegt — Einstellungen, Snapshots, Protokoll.
///
/// Der Ort haengt davon ab, wie die Anwendung ausgeliefert wurde, und der Grund ist die
/// Deinstallation. Ein aus dem Store installiertes Paket loescht beim Entfernen seinen
/// eigenen Container mit; alles ausserhalb bleibt liegen. Bei der MSIX-Probe habe ich
/// nachgemessen, dass Schreibzugriffe NICHT automatisch dorthin umgeleitet werden — die
/// Snapshots landeten im gewoehnlichen %LOCALAPPDATA% und haetten die Deinstallation
/// ueberlebt. Bis zu 400 MB, bei einem Werkzeug gegen Plattenmuell.
///
/// Also wird der Ort selbst gewaehlt: im Paket der Container, sonst der gewohnte Pfad.
/// </summary>
public static class AppData
{
    private const int AppmodelErrorNoPackage = 15700;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(ref int length, StringBuilder? name);

    /// <summary>Das Verzeichnis fuer alles, was die Anwendung selbst schreibt.</summary>
    public static string Folder { get; } = Choose();

    public static string Snapshots => Path.Combine(Folder, "snapshots");

    public static string Logs => Path.Combine(Folder, "logs");

    private static string Choose()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string? family = PackageFamily();

        return family is null
            ? Path.Combine(local, "DiskStats")
            : Path.Combine(local, "Packages", family, "LocalCache", "Local", "DiskStats");
    }

    /// <summary>Der Paketname, oder null, wenn die Anwendung nicht paketiert laeuft.</summary>
    private static string? PackageFamily()
    {
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            int length = 0;
            if (GetCurrentPackageFamilyName(ref length, null) == AppmodelErrorNoPackage) return null;

            var name = new StringBuilder(length);
            return GetCurrentPackageFamilyName(ref length, name) == 0 ? name.ToString() : null;
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    /// <summary>Legt die Ablageorte fest. Muss vor dem ersten Zugriff laufen.</summary>
    public static void Apply()
    {
        AppSettings.Folder = Folder;
        SnapshotStore.Folder = Snapshots;
        DiagnosticLog.Folder = Logs;
    }

    /// <summary>Was Snapshots und Protokoll gerade belegen.</summary>
    public static long CachedBytes()
    {
        long total = 0;

        foreach (string folder in (string[])[Snapshots, Logs])
        {
            if (!Directory.Exists(folder)) continue;

            try
            {
                foreach (string file in Directory.EnumerateFiles(folder))
                    total += new FileInfo(file).Length;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return total;
    }

    /// <summary>
    /// Loescht Snapshots und Protokoll und gibt zurueck, wieviel frei wurde.
    ///
    /// Die Einstellungen bleiben: Wer Zwischenstaende wegwirft, will nicht sein Thema und
    /// seine Sprache mit verlieren.
    /// </summary>
    public static long ClearCache()
    {
        long freed = CachedBytes();

        foreach (string folder in (string[])[Snapshots, Logs])
        {
            if (!Directory.Exists(folder)) continue;

            foreach (string file in Directory.EnumerateFiles(folder))
            {
                try { File.Delete(file); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        return freed - CachedBytes();
    }
}
