using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskStats.Core.Benchmarking;

/// <summary>Synchronous, sector-aligned I/O: queue depth 1, no Windows file cache.</summary>
internal sealed class WindowsBenchmarkFile : IBenchmarkFile
{
    private readonly SafeFileHandle _handle;
    private readonly nint _allocation;
    private readonly nint _buffer;
    private readonly int _capacity;
    private bool _disposed;

    public WindowsBenchmarkFile(string path, byte[] data)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows is required.");
        if (!GetDiskFreeSpaceW(Path.GetPathRoot(path)!, out _, out uint sector, out _, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (sector == 0 || 4096 % sector != 0)
            throw new NotSupportedException("This volume does not support aligned 4 KiB tests.");

        // CREATE_NEW + exclusive access + DELETE_ON_CLOSE: no existing file can be overwritten.
        _handle = CreateFileW(path, 0xC0000000, 0, 0, 1, 0x20000000 | 0x80000000 | 0x04000000, 0);
        if (_handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            _handle.Dispose();
            throw new Win32Exception(error);
        }
        try
        {
            _capacity = data.Length;
            _allocation = Marshal.AllocHGlobal(data.Length + 4095);
            _buffer = (_allocation + 4095) & ~(nint)4095;
            Marshal.Copy(data, 0, _buffer, data.Length);
        }
        catch { Marshal.FreeHGlobal(_allocation); _handle.Dispose(); throw; }
    }

    public void Transfer(long offset, int count, bool write)
    {
        if (offset < 0 || offset % 4096 != 0 || count <= 0 || count > _capacity || count % 4096 != 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (!SetFilePointerEx(_handle, offset, out _, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        uint transferred;
        bool ok = write ? WriteFile(_handle, _buffer, (uint)count, out transferred, 0)
                        : ReadFile(_handle, _buffer, (uint)count, out transferred, 0);
        if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (transferred != count) throw new IOException("The drive completed only part of an I/O request.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _handle.Dispose(); // Deletes only the handle's freshly created test file.
        Marshal.FreeHGlobal(_allocation);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security,
        uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceW(string root, out uint sectorsPerCluster, out uint bytesPerSector,
        out uint freeClusters, out uint totalClusters);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFilePointerEx(SafeFileHandle file, long distance, out long position, uint method);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(SafeFileHandle file, nint buffer, uint count, out uint read, nint overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(SafeFileHandle file, nint buffer, uint count, out uint written, nint overlapped);
}
