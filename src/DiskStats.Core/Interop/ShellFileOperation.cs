using System.Runtime.InteropServices;
using DiskStats.Core.Cleanup;

namespace DiskStats.Core.Interop;

/// <summary>
/// Der einzige Ort, an dem <c>SHFileOperation</c> aufgerufen wird. Papierkorb und Verschieben
/// hatten je eine eigene Kopie von Struktur und Deklaration; eine Korrektur an einer haette die
/// andere nicht erreicht.
///
/// Die Shell zeigt ihre Dialoge im Faden des Aufrufers und erwartet dafuer einen STA-Faden.
/// Der Aufrufer kommt aber meist aus dem Threadpool (MTA). Also ein eigener Faden nur fuer
/// diesen Aufruf — kurzlebig, und der Aufrufer wartet auf ihn.
/// </summary>
internal static class ShellFileOperation
{
    internal const uint Move = 1;
    internal const uint Delete = 3;

    internal const ushort NoConfirmation = 0x0010;   // die Rueckfrage stellt die Anwendung selbst
    internal const ushort AllowUndo = 0x0040;        // in den Papierkorb statt endgueltig
    internal const ushort WantNukeWarning = 0x4000;  // aber warnen, wenn es doch endgueltig waere

    internal static RecycleBin.Result Run(string threadName, uint function, IReadOnlyList<string> from,
        string? to, ushort flags, nint owner)
    {
        if (!OperatingSystem.IsWindows()) return new RecycleBin.Result(false, false, -1);

        RecycleBin.Result result = default;
        var thread = new Thread(() => result = RunCore(function, from, to, flags, owner))
        {
            IsBackground = true,
            Name = threadName,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    private static RecycleBin.Result RunCore(uint function, IReadOnlyList<string> from, string? to,
        ushort flags, nint owner)
    {
        // SHFileOperation erwartet die Pfade mit Null getrennt und doppelt abgeschlossen.
        var operation = new ShFileOpStruct
        {
            Window = owner,
            Function = function,
            From = string.Join('\0', from) + "\0\0",
            To = to is null ? null : to + "\0\0",
            Flags = flags,
        };

        int code = SHFileOperation(ref operation);
        return new RecycleBin.Result(code == 0 && !operation.AnyOperationsAborted,
            operation.AnyOperationsAborted, code);
    }

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
