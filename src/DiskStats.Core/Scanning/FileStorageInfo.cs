using System.Runtime.InteropServices;
using NativeFileHandle = Microsoft.Win32.SafeHandles.SafeFileHandle;

namespace DiskStats.Core.Scanning;

public readonly record struct FileIdentity(ulong Volume, ulong Low, ulong High)
{
    public bool IsKnown => Volume != 0 || Low != 0 || High != 0;
}

public readonly record struct FileStorageInfo(long AllocatedBytes, FileIdentity Identity, uint LinkCount)
{
    public static FileStorageInfo Unknown => new(-1, default, 0);

    /// <summary>Eine leere Datei: belegt nichts, ohne dass man nachsehen muesste.</summary>
    public static FileStorageInfo Empty => new(0, default, 1);

    /// <summary>Read metadata without reading file content or following a reparse point.</summary>
    public static FileStorageInfo Read(string path, FileAttributes attributes = FileAttributes.Normal)
    {
        if (!OperatingSystem.IsWindows() || (attributes & FileAttributes.ReparsePoint) != 0)
            return Unknown;
        using NativeFileHandle file = CreateFileW(ForNative(path), 0x80, 7, IntPtr.Zero, 3,
            0x02000000 | 0x00200000 | 0x00100000, IntPtr.Zero);
        if (file.IsInvalid || !GetStandardInfo(file, 1, out StandardInfo standard, 24)) return Unknown;

        long allocation = standard.AllocationSize;
        if ((attributes & (FileAttributes.Compressed | FileAttributes.SparseFile)) != 0)
        {
            allocation = GetCompressionInfo(file, 8, out CompressionInfo compressed, 16)
                ? compressed.CompressedFileSize : -1;
        }
        FileIdentity identity = GetIdInfo(file, 18, out IdInfo id, 24)
            ? new FileIdentity(id.Volume, id.Low, id.High) : default;
        return new FileStorageInfo(allocation, identity, standard.NumberOfLinks);
    }

    /// <summary>
    /// CreateFileW kennt ohne Manifest und ohne Systemrichtlinie nur 260 Zeichen. Das
    /// \\?\-Praefix hebt die Grenze auf — auch im Testhost, der unser Manifest nicht traegt.
    /// </summary>
    internal static string ForNative(string path)
    {
        if (!OperatingSystem.IsWindows()) return path;
        try { path = Path.GetFullPath(path); }
        catch (ArgumentException) { return path; }
        catch (NotSupportedException) { return path; }
        catch (IOException) { return path; }

        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            path.StartsWith(@"\\.\", StringComparison.Ordinal))
            return path;

        return path.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + path[2..]
            : @"\\?\" + path;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StandardInfo
    {
        public long AllocationSize, EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending, Directory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CompressionInfo
    {
        public long CompressedFileSize;
        public ushort CompressionFormat;
        public byte CompressionUnitShift, ChunkShift, ClusterShift;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IdInfo { public ulong Volume, Low, High; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern NativeFileHandle CreateFileW(string name, uint access, uint share,
        IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetStandardInfo(NativeFileHandle file, int kind, out StandardInfo info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCompressionInfo(NativeFileHandle file, int kind, out CompressionInfo info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIdInfo(NativeFileHandle file, int kind, out IdInfo info, uint size);
}
