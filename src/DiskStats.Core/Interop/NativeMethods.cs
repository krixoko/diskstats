using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskStats.Core.Interop;

/// <summary>
/// Die Win32-Aufrufe, die mehr als eine Stelle braucht — an einem Ort statt mehrfach mit
/// leicht abweichenden Signaturen. Mehrere Kopien derselben Deklaration driften auseinander;
/// ein falsch deklarierter Aufruf zeigt sich erst zur Laufzeit.
///
/// Was nur eine Klasse benutzt (und dort mit eigenen Strukturen), bleibt bei ihr.
/// </summary>
internal static class NativeMethods
{
    internal const uint GenericRead = 0x80000000;

    /// <summary>FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE</summary>
    internal const uint ShareAll = 7;

    internal const uint OpenExisting = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    /// <summary>Mit Eingabepuffer als Bytefeld (DeviceIoControl).</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputLength,
        byte[] output, int outputLength, out int returned, IntPtr overlapped);

    /// <summary>Mit Eingabepuffer als Zeiger, fuer Strukturen im unverwalteten Speicher.</summary>
    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControlPtr(SafeFileHandle device, uint code, IntPtr input, int inputLength,
        byte[] output, int outputLength, out int returned, IntPtr overlapped);
}
