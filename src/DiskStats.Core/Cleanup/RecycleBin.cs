using System.Runtime.InteropServices;

namespace DiskStats.Core.Cleanup;

/// <summary>
/// Verschiebt in den Papierkorb statt endgueltig zu loeschen.
///
/// Windows bietet dafuer SHFileOperation an. Der Umweg ueber die Shell ist der Punkt:
/// Nur sie legt Dateien wiederherstellbar ab, und nur sie warnt, wenn etwas zu gross fuer
/// den Papierkorb ist und darum endgueltig verschwinden wuerde. Direktes Loeschen ueber
/// File.Delete kaeme ohne beides.
/// </summary>
public static class RecycleBin
{
    private const uint FoDelete = 3;

    private const ushort AllowUndo = 0x0040;         // in den Papierkorb statt endgueltig
    private const ushort NoConfirmation = 0x0010;    // die Rueckfrage stellt die Anwendung selbst
    private const ushort WantNukeWarning = 0x4000;   // aber warnen, wenn es doch endgueltig waere

    public readonly record struct Result(bool Ok, bool Aborted, int Code);

    /// <param name="owner">
    /// Fensterhandle, an das die Shell ihre Warnung haengt ("zu gross fuer den Papierkorb").
    /// Ohne Besitzer erscheint sie irgendwo auf dem Bildschirm, hinter der Anwendung.
    /// </param>
    public static Result Send(IReadOnlyList<string> paths, nint owner = 0)
    {
        if (paths.Count == 0) return new Result(true, false, 0);
        if (!OperatingSystem.IsWindows()) return new Result(false, false, -1);

        // Die Shell zeigt ihre Dialoge im Faden des Aufrufers und erwartet dafuer einen
        // STA-Faden. Der Aufrufer kommt aber meist aus dem Threadpool (MTA). Also ein eigener
        // Faden nur fuer diesen Aufruf — kurzlebig, und der Aufrufer wartet auf ihn.
        Result result = default;
        var thread = new Thread(() => result = SendCore(paths, owner))
        {
            IsBackground = true,
            Name = "DiskStats.RecycleBin",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    private static Result SendCore(IReadOnlyList<string> paths, nint owner)
    {
        // SHFileOperation erwartet die Pfade mit Null getrennt und doppelt abgeschlossen —
        // und ohne das \\?\-Praefix, das der Baum fuer lange Pfade fuehrt.
        string list = string.Join('\0', paths.Select(StripExtendedPrefix)) + "\0\0";

        var operation = new ShFileOpStruct
        {
            Window = owner,
            Function = FoDelete,
            From = list,
            Flags = AllowUndo | NoConfirmation | WantNukeWarning,
        };

        int code = SHFileOperation(ref operation);
        return new Result(code == 0 && !operation.AnyOperationsAborted,
            operation.AnyOperationsAborted, code);
    }

    private static string StripExtendedPrefix(string path)
        => path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + path[8..]
         : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..]
         : path;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 8)]
    private struct ShFileOpStruct
    {
        public IntPtr Window;
        public uint Function;
        [MarshalAs(UnmanagedType.LPWStr)] public string From;
        [MarshalAs(UnmanagedType.LPWStr)] public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHFileOperation(ref ShFileOpStruct operation);
}
