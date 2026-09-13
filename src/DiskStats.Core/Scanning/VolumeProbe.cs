using System.Runtime.InteropServices;

namespace DiskStats.Core.Scanning;

public enum StorageKind { Unknown, Nvme, Ssd, Hdd, Removable, Network, Optical, Ram, Cloud }

/// <summary>
/// Ermittelt, auf welcher Art Datentraeger ein Pfad liegt.
///
/// Nuetzlich an zwei Stellen: Die Oberflaeche zeigt ein passendes Symbol, und der Walker kann
/// die Worker-Zahl danach richten — auf einer drehenden Platte kostet hohe Parallelitaet durch
/// Kopfbewegungen mehr, als sie einbringt.
///
/// Die Abfrage laeuft ueber IOCTL_STORAGE_QUERY_PROPERTY. Das Volume wird dabei mit
/// Zugriffsrecht 0 geoeffnet — reine Eigenschaftsabfragen brauchen keine Adminrechte,
/// anders als das Lesen der Rohdaten.
/// </summary>
public static class VolumeProbe
{
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const uint FileShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;

    private const int StorageDeviceProperty = 0;
    private const int StorageDeviceSeekPenaltyProperty = 7;
    private const int PropertyStandardQuery = 0;

    private const int BusTypeUsb = 0x07;
    private const int BusTypeSd = 0x0C;
    private const int BusTypeMmc = 0x0D;
    private const int BusTypeNvme = 0x11;

    /// <summary>
    /// Lage des BusType in STORAGE_DEVICE_DESCRIPTOR: zwei DWORD, vier Bytes, vier DWORD.
    /// Das Feld ist ein Enum und damit vier Byte breit.
    /// </summary>
    private const int BusTypeOffset = 28;

    // Feldlage in DEVICE_SEEK_PENALTY_DESCRIPTOR: Version, Size, dann das Flag.
    private const int SeekPenaltyOffset = 8;

    public static StorageKind KindOf(string path)
    {
        if (!OperatingSystem.IsWindows()) return StorageKind.Unknown;

        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return StorageKind.Unknown;

            var drive = new DriveInfo(root);
            switch (drive.DriveType)
            {
                case DriveType.Network: return StorageKind.Network;
                case DriveType.CDRom: return StorageKind.Optical;
                case DriveType.Ram: return StorageKind.Ram;
                case DriveType.Removable: return StorageKind.Removable;
                case DriveType.Fixed: break;
                default: return StorageKind.Unknown;
            }

            if (LooksLikeCloud(drive)) return StorageKind.Cloud;

            string letter = root.TrimEnd('\\', '/');
            if (letter.Length != 2 || letter[1] != ':') return StorageKind.Unknown;

            return Probe($@"\\.\{letter}");
        }
        catch (IOException) { return StorageKind.Unknown; }
        catch (UnauthorizedAccessException) { return StorageKind.Unknown; }
        catch (ArgumentException) { return StorageKind.Unknown; }
    }

    /// <summary>
    /// Dateisysteme von Cloud-Diensten geben sich als gewoehnliches lokales Laufwerk aus.
    ///
    /// Es gibt dafuer keine saubere Abfrage: Google Drive meldet DriveType.Fixed, das
    /// Dateisystem FAT32 und als Groesse die der Systemplatte — auf diesem Rechner Byte fuer
    /// Byte dieselbe Zahl wie C:. Deshalb wird am Namen erkannt, und zwar bewusst nur bei
    /// Laufwerken, hinter denen keine echte Platte steckt.
    ///
    /// Faellt die Erkennung aus, ist das Ergebnis ein allgemeines Laufwerkssymbol statt eines
    /// falschen — der Preis fuer einen Irrtum ist hier klein.
    /// </summary>
    private static readonly string[] CloudNames =
    [
        "Google Drive", "OneDrive", "Dropbox", "iCloud", "pCloud", "Nextcloud", "ownCloud",
        "MEGA", "Box", "Tresorit", "Proton Drive", "Sync", "Mediafire", "rclone", "Cryptomator",
    ];

    private static bool LooksLikeCloud(DriveInfo drive)
    {
        string label;
        try { label = drive.VolumeLabel; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }

        if (label.Length == 0) return false;

        foreach (string name in CloudNames)
            if (label.Contains(name, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    private static StorageKind Probe(string volume)
    {
        // Zugriffsrecht 0: reicht fuer Eigenschaftsabfragen und vermeidet die Rechtepruefung,
        // die das Oeffnen zum Lesen ausloesen wuerde.
        using SafeFileHandle handle = CreateFile(
            volume, 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

        if (handle.IsInvalid) return StorageKind.Unknown;

        // Bewusst die Geraete- und nicht die Adapter-Eigenschaft: Auf Plattformen mit Intel VMD
        // meldet der Adapter "RAID", obwohl dahinter eine NVMe steckt. Das Geraet sagt die
        // Wahrheit ueber sich selbst.
        if (TryQuery(handle, StorageDeviceProperty, 512, out byte[] device)
            && device.Length >= BusTypeOffset + 4)
        {
            int bus = BitConverter.ToInt32(device, BusTypeOffset);
            switch (bus)
            {
                case BusTypeNvme: return StorageKind.Nvme;
                case BusTypeUsb:
                case BusTypeSd:
                case BusTypeMmc: return StorageKind.Removable;
            }
        }

        if (TryQuery(handle, StorageDeviceSeekPenaltyProperty, 16, out byte[] penalty)
            && penalty.Length > SeekPenaltyOffset)
        {
            return penalty[SeekPenaltyOffset] != 0 ? StorageKind.Hdd : StorageKind.Ssd;
        }

        return StorageKind.Unknown;
    }

    private static bool TryQuery(SafeFileHandle handle, int propertyId, int size, out byte[] buffer)
    {
        buffer = new byte[size];

        var query = new StoragePropertyQuery
        {
            PropertyId = propertyId,
            QueryType = PropertyStandardQuery,
        };

        int querySize = Marshal.SizeOf<StoragePropertyQuery>();
        IntPtr input = Marshal.AllocHGlobal(querySize);

        try
        {
            Marshal.StructureToPtr(query, input, fDeleteOld: false);
            return DeviceIoControl(
                handle, IoctlStorageQueryProperty, input, querySize,
                buffer, size, out _, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(input);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePropertyQuery
    {
        public int PropertyId;
        public int QueryType;
        public byte AdditionalParameters;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device, uint code, IntPtr input, int inputSize,
        byte[] output, int outputSize, out int returned, IntPtr overlapped);
}

/// <summary>Schlanker Handle-Wrapper, damit das Volume sicher geschlossen wird.</summary>
internal sealed class SafeFileHandle() : SafeHandle(new IntPtr(-1), ownsHandle: true)
{
    public override bool IsInvalid => handle == new IntPtr(-1) || handle == IntPtr.Zero;

    protected override bool ReleaseHandle() => CloseHandle(handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
