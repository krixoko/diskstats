using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DiskStats.App;

/// <summary>
/// Sorgt dafuer, dass nur eine Anwendung laeuft.
///
/// Nicht aus Prinzip, sondern weil zwei Instanzen sich dieselbe Einstellungsdatei und
/// dieselbe Snapshot-Ablage teilen: Wer zuletzt schreibt, gewinnt, und die Einstellungen der
/// anderen sind still verloren. Ein Scan ueber Minuten macht das besonders aergerlich.
///
/// Der zweite Start beendet sich nicht wortlos — er holt das vorhandene Fenster nach vorn.
/// Wortlos zu verschwinden saehe aus, als sei das Doppelklicken folgenlos geblieben.
/// </summary>
public static class SingleInstance
{
    /// <summary>
    /// Global, damit die Sperre auch ueber Sitzungsgrenzen hinweg greift: Zwei Anmeldungen
    /// desselben Kontos teilen sich dasselbe Profil und damit dieselben Dateien. Das Fenster
    /// der anderen Sitzung laesst sich von hier nicht nach vorn holen — aber lieber ein
    /// stiller zweiter Start als zwei Instanzen, die sich gegenseitig ueberschreiben.
    /// </summary>
    private const string Name = @"Global\DiskStats.SingleInstance";

    private static Mutex? _held;

    private delegate bool EnumWindowsProc(nint window, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);

    private const int Restore = 9;
    private const uint Owner = 4;

    /// <summary>
    /// Gibt zurueck, ob diese Anwendung weiterlaufen darf. Ist bereits eine da, wird sie
    /// nach vorn geholt und dieser Start soll sich beenden.
    /// </summary>
    public static bool Claim()
    {
        try
        {
            _held = new Mutex(initiallyOwned: true, Name, out bool mine);
            if (mine) return true;
        }
        // Gehoert die Sperre einem anderen Benutzerkonto, ist sie fuer uns unerreichbar.
        // Dann lieber starten als sich grundlos beenden.
        catch (UnauthorizedAccessException) { return true; }
        catch (IOException) { return true; }

        BringExistingToFront();
        return false;
    }

    /// <summary>
    /// Sucht das Hauptfenster der anderen Instanz ueber ihre Prozesskennung — nicht ueber den
    /// Fenstertitel. Der Titel gehoert auch einem Explorer-Fenster im Ordner "DiskStats", und
    /// waehrend die andere Instanz noch startet, gibt es ihn noch gar nicht.
    /// </summary>
    private static void BringExistingToFront()
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            nint window = FindSiblingWindow();
            if (window == 0) return;

            if (IsIconic(window)) ShowWindow(window, Restore);
            SetForegroundWindow(window);
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        catch (InvalidOperationException) { /* Prozess zwischenzeitlich beendet */ }
    }

    private static nint FindSiblingWindow()
    {
        using Process self = Process.GetCurrentProcess();
        var siblings = new HashSet<uint>();
        foreach (Process p in Process.GetProcessesByName(self.ProcessName))
        {
            using (p) if (p.Id != self.Id) siblings.Add((uint)p.Id);
        }
        if (siblings.Count == 0) return 0;

        nint found = 0;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint owner);
            // Nur sichtbare Hauptfenster: Avalonia haelt daneben unsichtbare Hilfsfenster.
            if (!siblings.Contains(owner) || !IsWindowVisible(window) || GetWindow(window, Owner) != 0)
                return true;
            found = window;
            return false;
        }, 0);
        return found;
    }

    /// <summary>Gibt die Sperre frei. Ohne das haelt sie bis zum Prozessende.</summary>
    public static void Release()
    {
        try { _held?.ReleaseMutex(); }
        catch (ApplicationException) { /* nie gehalten */ }

        _held?.Dispose();
        _held = null;
    }
}
