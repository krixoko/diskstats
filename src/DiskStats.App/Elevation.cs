using System.Runtime.InteropServices;
using System.Text;

namespace DiskStats.App;

/// <summary>
/// Ob die Anwendung mit erhoehten Rechten laeuft, ob sie es koennte, und wie sie sich selbst
/// neu startet.
///
/// Das Vorbild ist WinDirStat: Es fragt beim Start, laesst die Antwort merken und startet sich
/// bei Ja ueber ShellExecute mit dem Verb "runas" neu. Uebernommen ist auch die Vorpruefung —
/// gefragt wird nur, wenn der Benutzer ueberhaupt erhoehen *kann*. Ein reines Standardkonto
/// bekaeme sonst eine Anmeldemaske als Antwort auf eine Frage, die es nicht gestellt hat.
/// </summary>
public static class Elevation
{
    private const int TokenQuery = 0x0008;
    private const int TokenElevation = 20;
    private const int TokenElevationType = 18;

    /// <summary>Der Benutzer ist Administrator, der Prozess laeuft aber eingeschraenkt.</summary>
    private const int ElevationTypeLimited = 3;

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(nint process, int access, out nint token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        nint token, int kind, out int value, int size, out int written);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int length, StringBuilder? name);

    private const int AppmodelErrorNoPackage = 15700;

    /// <summary>Laeuft der Prozess bereits erhoeht?</summary>
    public static bool IsActive { get; } = Query(TokenElevation) != 0;

    /// <summary>
    /// Koennte er erhoeht werden? Nur dann lohnt die Frage.
    ///
    /// Nicht mit <see cref="IsActive"/> zu verwechseln: Auch ein Administrator laeuft
    /// normalerweise mit eingeschraenktem Token, und genau der ist gemeint.
    /// </summary>
    public static bool IsAvailable { get; } = !IsActive && Query(TokenElevationType) == ElevationTypeLimited;

    /// <summary>
    /// Laeuft die Anwendung aus einem MSIX-Paket?
    ///
    /// Dann wird nicht gefragt. Ein paketiertes Programm laesst sich nicht als dasselbe
    /// paketierte Programm erhoeht neu starten, und der Store laesst Anwendungen, die Rechte
    /// verlangen, praktisch nicht durch. Dieselbe Anwendung verhaelt sich in beiden
    /// Auslieferungen also richtig, ohne zwei Bauten zu brauchen.
    /// </summary>
    public static bool IsPackaged { get; } = DetectPackage();

    /// <summary>Ob das Angebot ueberhaupt in Frage kommt.</summary>
    public static bool CanOffer => OperatingSystem.IsWindows() && IsAvailable && !IsPackaged;

    private static int Query(int kind)
    {
        if (!OperatingSystem.IsWindows()) return 0;

        nint token = 0;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out token)) return 0;
            return GetTokenInformation(token, kind, out int value, sizeof(int), out _) ? value : 0;
        }
        catch (DllNotFoundException) { return 0; }
        catch (EntryPointNotFoundException) { return 0; }
        finally { if (token != 0) CloseHandle(token); }
    }

    private static bool DetectPackage()
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            int length = 0;
            return GetCurrentPackageFullName(ref length, null) != AppmodelErrorNoPackage;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    /// <summary>
    /// Startet dieselbe Anwendung erhoeht neu und gibt zurueck, ob das gelungen ist.
    ///
    /// Der Aufrufer beendet sich danach selbst. Bricht der Benutzer die Rueckfrage von Windows
    /// ab, laeuft die alte Fassung einfach weiter — das ist die richtige Folge, nicht ein
    /// Fehler, den man melden muesste.
    /// </summary>
    public static bool Relaunch(IReadOnlyList<string> arguments)
    {
        string? exe = Environment.ProcessPath;
        if (exe is null) return false;

        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty,
            };
            // ArgumentList statt selbst zusammengebauter Zeile: Das Laufzeitsystem kennt die
            // Windows-Regeln fuer Anfuehrungszeichen und Rueckstriche — eine Handfassung hatte
            // einem Pfad mit Leerzeichen den abschliessenden Rueckstrich abgeschnitten.
            foreach (string argument in ArgumentsFor(arguments))
                process.StartInfo.ArgumentList.Add(argument);

            return process.Start();
        }
        // Abbruch der Windows-Rueckfrage kommt als Win32Exception zurueck.
        catch (System.ComponentModel.Win32Exception) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>Die Argumente, die weitergereicht werden: alle ausser dem Programmpfad an erster Stelle.</summary>
    public static IReadOnlyList<string> ArgumentsFor(IReadOnlyList<string> arguments)
        => [.. arguments.Skip(1)];
}
