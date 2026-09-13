using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using DiskStats.Core.Scanning;

namespace DiskStats.Core.Storage;

/// <summary>
/// Serialisiert den NodeStore. Weil das Modell bereits flach ist, ist die Datei fast eine
/// 1:1-Abbildung der Arrays — es braucht keinen eigenen Serialisierer.
///
/// Die Arrays werden als zusammenhaengende Byte-Bloecke geschrieben, nicht Element fuer Element.
/// Das ist nicht nur schneller (ein memcpy statt Millionen Einzelaufrufe), sondern auch der
/// Unterschied zwischen Kompression und Aufblaehung: Brotli braucht grosse Bloecke, um die
/// Wiederholungsmuster in gleichartigen Zahlenkolonnen ueberhaupt zu sehen.
///
/// Folge dieser Bauweise: die Bytereihenfolge ist die der Plattform. Der Snapshot ist ein
/// lokaler Cache, kein Austauschformat — das ist der bewusste Kompromiss.
/// </summary>
public static class SnapshotFormat
{
    private static ReadOnlySpan<byte> Magic => "DSKS"u8;
    private const int CurrentVersion = 2;

    public static void Write(NodeStore store, Stream target)
    {
        using var brotli = new BrotliStream(target, CompressionLevel.Fastest, leaveOpen: true);

        using (var w = new BinaryWriter(brotli, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Magic);
            w.Write(CurrentVersion);
            w.Write(DateTime.UtcNow.Ticks);
            w.Write(store.RootPath);
            w.Write(store.Count);
            w.Write(store.Names.ByteLength);
            w.Flush();
        }

        int n = store.Count;
        WriteBlock<int>(brotli, store.ParentIndex.AsSpan(0, n));
        WriteBlock<int>(brotli, store.ChildStart.AsSpan(0, n));
        WriteBlock<int>(brotli, store.ChildCount.AsSpan(0, n));
        WriteBlock<int>(brotli, store.NameOffset.AsSpan(0, n));
        WriteBlock<ushort>(brotli, store.NameLength.AsSpan(0, n));
        WriteBlock<long>(brotli, store.Size.AsSpan(0, n));
        WriteBlock<long>(brotli, store.MTime.AsSpan(0, n));
        WriteBlock<EntryFlags>(brotli, store.Flags.AsSpan(0, n));

        brotli.Write(store.Names.GetBytes(0, store.Names.ByteLength));
        WriteBlock<long>(brotli, store.HasStorageMetadata ? store.Allocation
            : Enumerable.Repeat(-1L, n).ToArray());
        WriteBlock<FileIdentity>(brotli, store.FileIds.Length == n ? store.FileIds : new FileIdentity[n]);
        WriteBlock<uint>(brotli, store.LinkCounts.Length == n ? store.LinkCounts : new uint[n]);
    }

    /// <summary>
    /// Liest einen Snapshot. Alles, was an der Datei nicht stimmt — abgeschnitten, verfaelscht,
    /// fremd — kommt als <see cref="InvalidDataException"/> zurueck, damit der Aufrufer einen
    /// einzigen Fall behandeln muss: Datei verwerfen, neu scannen.
    /// </summary>
    public static NodeStore Read(Stream source)
    {
        try
        {
            return ReadCore(source);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or OverflowException
            or ArgumentException or IndexOutOfRangeException or FormatException)
        {
            throw new InvalidDataException("Snapshot ist beschaedigt oder abgeschnitten.", ex);
        }
    }

    private static NodeStore ReadCore(Stream source)
    {
        using var brotli = new BrotliStream(source, CompressionMode.Decompress, leaveOpen: true);

        int n;
        int version;
        int poolLength;
        string rootPath;

        using (var r = new BinaryReader(brotli, Encoding.UTF8, leaveOpen: true))
        {
            Span<byte> magic = stackalloc byte[4];
            try
            {
                if (r.Read(magic) != 4 || !magic.SequenceEqual(Magic))
                    throw new InvalidDataException("Keine DiskStats-Snapshot-Datei.");
            }
            catch (InvalidDataException) { throw; }
            catch (Exception ex)
            {
                throw new InvalidDataException("Snapshot konnte nicht gelesen werden.", ex);
            }

            version = r.ReadInt32();
            if (version != 1 && version != CurrentVersion)
                throw new InvalidDataException($"Snapshot-Version {version} wird nicht unterstuetzt.");

            _ = r.ReadInt64();                   // Erstellungszeitpunkt, aktuell ungenutzt
            rootPath = r.ReadString();
            n = r.ReadInt32();
            poolLength = r.ReadInt32();
        }

        // Der Kopf ist Eingabe, keine Wahrheit: Eine beschaedigte oder untergeschobene Datei
        // darf hier hoechstens eine klare Ablehnung ausloesen — kein OutOfMemory beim Start.
        if (n < 1 || n > MaxNodes)
            throw new InvalidDataException($"Snapshot nennt {n} Knoten; erlaubt sind 1 bis {MaxNodes}.");
        if (poolLength < 0 || poolLength > MaxPoolBytes)
            throw new InvalidDataException($"Snapshot nennt {poolLength} Byte Namen; erlaubt sind 0 bis {MaxPoolBytes}.");

        var parentIndex = ReadBlock<int>(brotli, n);
        var childStart = ReadBlock<int>(brotli, n);
        var childCount = ReadBlock<int>(brotli, n);
        var nameOffset = ReadBlock<int>(brotli, n);
        var nameLength = ReadBlock<ushort>(brotli, n);
        var size = ReadBlock<long>(brotli, n);
        var mtime = ReadBlock<long>(brotli, n);
        var flags = ReadBlock<EntryFlags>(brotli, n);

        var pool = new byte[poolLength];
        brotli.ReadExactly(pool);

        var allocation = Enumerable.Repeat(-1L, n).ToArray();
        var ids = new FileIdentity[n];
        var links = new uint[n];
        if (version >= 2)
        {
            allocation = ReadBlock<long>(brotli, n);
            ids = ReadBlock<FileIdentity>(brotli, n);
            links = ReadBlock<uint>(brotli, n);
        }
        var store = new NodeStore
        {
            RootPath = rootPath,
            Count = n,
            ParentIndex = parentIndex,
            ChildStart = childStart,
            ChildCount = childCount,
            NameOffset = nameOffset,
            NameLength = nameLength,
            Size = size,
            Allocation = allocation,
            FileIds = ids,
            LinkCounts = links,
            MTime = mtime,
            Flags = flags,
            Names = NamePool.FromBytes(pool),
        };
        Validate(store);
        SizeAggregator.AggregatePhysical(store);
        return store;
    }

    /// <summary>
    /// Obergrenzen fuer den Dateikopf. Grosszuegig gegenueber jedem echten Volume (2 Mio. Knoten
    /// brauchen rund 100 MB), aber weit unter dem, was einen Rechner in die Knie zwingt.
    /// </summary>
    internal const int MaxNodes = 200_000_000;
    internal const int MaxPoolBytes = int.MaxValue - 64;

    private static readonly System.Buffers.SearchValues<byte> InvalidNameBytes
        = System.Buffers.SearchValues.Create([(byte)'\\', (byte)'/', (byte)':', 0]);

    /// <summary>
    /// Prueft die Baum-Invarianten, auf die sich der ganze uebrige Code verlaesst: Eltern vor
    /// Kindern, Kinderbereiche im Baum, Namen im Vorrat und ohne Pfadtrenner. Ein Name ".."
    /// liesse <see cref="NodeStore.PathOf"/> aus der Scanwurzel hinaus — und damit die
    /// Aufraeumliste auf etwas zeigen, das nie gescannt wurde.
    /// </summary>
    private static void Validate(NodeStore store)
    {
        int n = store.Count;
        int poolLength = store.Names.ByteLength;

        if (store.ParentIndex[0] != -1)
            throw new InvalidDataException("Snapshot: die Wurzel hat ein Elternteil.");

        for (int i = 0; i < n; i++)
        {
            if (i > 0 && (store.ParentIndex[i] < 0 || store.ParentIndex[i] >= i))
                throw new InvalidDataException($"Snapshot: Knoten {i} liegt vor seinem Elternteil.");

            int start = store.ChildStart[i];
            int count = store.ChildCount[i];
            if (count < 0 || start < 0 || (count > 0 && (start <= i || (long)start + count > n)))
                throw new InvalidDataException($"Snapshot: Kinderbereich von Knoten {i} liegt ausserhalb des Baums.");

            int offset = store.NameOffset[i];
            int length = store.NameLength[i];
            if (offset < 0 || (long)offset + length > poolLength)
                throw new InvalidDataException($"Snapshot: Name von Knoten {i} liegt ausserhalb des Namensvorrats.");

            ReadOnlySpan<byte> name = store.Names.GetBytes(offset, length);
            if (name.IndexOfAny(InvalidNameBytes) >= 0
                || name.SequenceEqual("."u8) || name.SequenceEqual(".."u8))
                throw new InvalidDataException($"Snapshot: Name von Knoten {i} ist kein gueltiger Dateiname.");
        }
    }

    private static void WriteBlock<T>(Stream stream, ReadOnlySpan<T> data) where T : unmanaged
        => stream.Write(MemoryMarshal.AsBytes(data));

    private static T[] ReadBlock<T>(Stream stream, int count) where T : unmanaged
    {
        var array = new T[count];
        stream.ReadExactly(MemoryMarshal.AsBytes(array.AsSpan()));
        return array;
    }
}
