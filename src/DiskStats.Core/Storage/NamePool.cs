using System.Text;

namespace DiskStats.Core.Storage;

/// <summary>
/// Speichert alle Dateinamen hintereinander als UTF-8 in einem einzigen Array.
/// Bei zwei Millionen Dateien spart das zwei Millionen String-Objekte samt Objekt-Overhead
/// und GC-Druck; ein Knoten merkt sich nur Offset und Laenge.
/// Nicht threadsicher — der <see cref="NodeBuffer"/> schuetzt seinen Pool mit einer Sperre.
/// Ein Pool je Worker war geplant; die Messung (Bench, C:\Windows, 16 Worker) zeigte den
/// Walk mit Sperre genauso schnell wie mit einem zaehlenden Sink ohne — der Engpass ist das
/// Dateisystem, nicht die Sperre. Bis eine Messung etwas anderes sagt, bleibt es einfach.
/// </summary>
public sealed class NamePool
{
    private byte[] _buffer;
    private int _length;

    public NamePool(int initialCapacity = 1 << 16)
        => _buffer = new byte[Math.Max(16, initialCapacity)];

    private NamePool(byte[] buffer, int length)
    {
        _buffer = buffer;
        _length = length;
    }

    public int ByteLength => _length;

    /// <summary>Wieviel der Puffer wirklich belegt — nach <see cref="Trim"/> gleich ByteLength.</summary>
    public int Capacity => _buffer.Length;

    public static int Utf8Length(ReadOnlySpan<char> name) => Encoding.UTF8.GetByteCount(name);

    /// <summary>Haengt einen Namen an und liefert seinen Byte-Offset.</summary>
    public int Add(ReadOnlySpan<char> name)
    {
        int needed = Encoding.UTF8.GetByteCount(name);
        EnsureCapacity(_length + needed);

        int offset = _length;
        Encoding.UTF8.GetBytes(name, _buffer.AsSpan(offset));
        _length += needed;
        return offset;
    }

    public ReadOnlySpan<byte> GetBytes(int offset, int length) => _buffer.AsSpan(offset, length);

    public string GetString(int offset, int length) => Encoding.UTF8.GetString(_buffer, offset, length);

    public byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();

    public static NamePool FromBytes(byte[] bytes) => new(bytes, bytes.Length);

    /// <summary>
    /// Gibt die ungenutzte Kapazitaet frei. Der Puffer waechst in Zweierpotenzen und haelt nach
    /// dem Scan im Mittel ein Drittel Luft — bei 35 MB Namen sind das rund 30 MB, die dauerhaft
    /// belegt blieben. Nach dem Scan wird nichts mehr angehaengt, also lohnt sich das Umkopieren.
    /// </summary>
    public void Trim()
    {
        if (_buffer.Length != _length)
            Array.Resize(ref _buffer, _length);
    }

    /// <summary>Uebernimmt die Namen eines anderen Pools und liefert die Offset-Verschiebung.</summary>
    public int Append(NamePool other)
    {
        EnsureCapacity(_length + other._length);
        int shift = _length;
        other._buffer.AsSpan(0, other._length).CopyTo(_buffer.AsSpan(_length));
        _length += other._length;
        return shift;
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _buffer.Length) return;

        int capacity = _buffer.Length;
        while (capacity < required) capacity *= 2;
        Array.Resize(ref _buffer, capacity);
    }
}
