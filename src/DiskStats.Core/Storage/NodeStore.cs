using DiskStats.Core.Scanning;

namespace DiskStats.Core.Storage;

/// <summary>
/// Der fertige Baum als parallele Arrays. Index 0 ist immer die Wurzel.
/// Kinder eines Knotens liegen zusammenhaengend ab ChildStart — dadurch ist das Durchlaufen
/// eines Ordners ein sequentieller Speicherzugriff statt einer Zeigerjagd.
/// </summary>
public sealed class NodeStore
{
    public required string RootPath { get; init; }
    public required int Count { get; init; }
    public required int[] ParentIndex { get; init; }
    public required int[] ChildStart { get; init; }
    public required int[] ChildCount { get; init; }
    public required int[] NameOffset { get; init; }
    public required ushort[] NameLength { get; init; }
    public required long[] Size { get; init; }
    public required long[] MTime { get; init; }
    public required EntryFlags[] Flags { get; init; }
    public required NamePool Names { get; init; }

    // Allocation -1 means unavailable, not zero bytes. Old snapshots have no allocation data.
    public long[] Allocation { get; init; } = [];
    public FileIdentity[] FileIds { get; init; } = [];
    public uint[] LinkCounts { get; init; } = [];
    public long[] PhysicalSize { get; internal set; } = [];
    public int[] UnknownAllocation { get; internal set; } = [];

    /// <summary>
    /// Lebende Dateien bzw. Ordner unterhalb eines Knotens (ohne ihn selbst). Vorberechnet,
    /// weil die Kopfzeile sie bei jeder Navigation braucht und ein Zaehllauf ueber zwei
    /// Millionen Knoten auf dem UI-Thread spuerbar waere. Gepflegt von <see cref="SizeAggregator"/>.
    /// </summary>
    public int[] FileCount { get; internal set; } = [];
    public int[] FolderCount { get; internal set; } = [];
    public bool HasStorageMetadata => Allocation.Length == Count;
    public long AllocationOf(int node) => HasStorageMetadata ? Allocation[node] : -1;
    public FileIdentity IdentityOf(int node) => FileIds.Length == Count ? FileIds[node] : default;
    public uint LinksOf(int node) => LinkCounts.Length == Count ? LinkCounts[node] : 0;

    public string GetName(int index) => Names.GetString(NameOffset[index], NameLength[index]);

    /// <summary>
    /// Der Name als UTF-8, ohne String. <see cref="GetName"/> legt bei jedem Aufruf ein neues Objekt
    /// an; in Schleifen ueber Zehntausende Knoten (Sortieren, Suchen, Zusammensetzen) ist das der
    /// Unterschied zwischen einem Durchlauf und einem Durchlauf plus hunderten Megabyte Abfall.
    /// </summary>
    public ReadOnlySpan<byte> NameBytes(int index) => Names.GetBytes(NameOffset[index], NameLength[index]);

    /// <summary>
    /// Ob der Name gleich <paramref name="text"/> ist, ohne Beachtung der Gross-/Kleinschreibung -
    /// dasselbe Ergebnis wie <c>GetName(index).Equals(text, OrdinalIgnoreCase)</c>, aber ohne
    /// Zeichenkette. Reines ASCII wird direkt verglichen.
    ///
    /// Ein ASCII-Name gleicht unter OrdinalIgnoreCase keinem Text mit anderen Zeichen und umgekehrt:
    /// .NET bildet weder das Kelvin-Zeichen noch das punktlose i noch das lange s auf ASCII-Buchstaben
    /// ab (die Tests pruefen das). Nur wenn beide Seiten nicht-ASCII enthalten, entscheidet der genaue Weg.
    /// </summary>
    public bool NameEquals(int index, ReadOnlySpan<char> text)
    {
        ReadOnlySpan<byte> name = NameBytes(index);
        bool nameIsAscii = System.Text.Ascii.IsValid(name);
        bool textIsAscii = System.Text.Ascii.IsValid(text);

        if (nameIsAscii && textIsAscii) return System.Text.Ascii.EqualsIgnoreCase(name, text);
        if (nameIsAscii != textIsAscii) return false;

        // Beide mit Umlauten o. Ae.: genauer Vergleich, aber im Stapel statt als neuer String.
        if (name.Length <= ScratchChars)
        {
            Span<char> scratch = stackalloc char[ScratchChars];
            int length = System.Text.Encoding.UTF8.GetChars(name, scratch);
            return scratch[..length].Equals(text, StringComparison.OrdinalIgnoreCase);
        }

        return GetName(index).AsSpan().Equals(text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Platz fuer einen dekodierten Namen im Stapel. Dateinamen sind auf 255 Zeichen begrenzt (UTF-8
    /// hoechstens 765 Byte); 512 deckt jeden ueblichen Namen, der Rest nimmt den Weg ueber einen String.
    /// </summary>
    private const int ScratchChars = 512;

    /// <summary>
    /// Ordnet zwei Namen wie <c>string.Compare(.., OrdinalIgnoreCase)</c>, ohne Zeichenketten anzulegen.
    /// Eine Namens-Sortierung ruft das n log n mal auf - bei zwei Millionen Knoten ueber vierzig Millionen
    /// Mal, jedes Mal mit zwei neuen Strings, wenn man <see cref="GetName"/> benutzt.
    ///
    /// Solange beide Namen in ASCII uebereinstimmen oder sich dort unterscheiden, entscheiden die Bytes.
    /// Sobald an der entscheidenden Stelle etwas anderes steht, gilt der genaue Vergleich - die Ordnung
    /// ist damit fuer jedes Paar dieselbe wie bei Zeichenketten.
    /// </summary>
    public int CompareNames(int a, int b)
    {
        ReadOnlySpan<byte> x = NameBytes(a), y = NameBytes(b);
        int shared = Math.Min(x.Length, y.Length);

        for (int i = 0; i < shared; i++)
        {
            byte p = x[i], q = y[i];
            if (p == q) continue;
            if (p >= 0x80 || q >= 0x80) return CompareExactly(x, y);

            int up = Upper(p), uq = Upper(q);
            if (up != uq) return up < uq ? -1 : 1;
        }

        return x.Length.CompareTo(y.Length);

        static int Upper(byte value) => value is >= (byte)'a' and <= (byte)'z' ? value - 32 : value;
    }

    /// <summary>Der genaue Vergleich zweier UTF-8-Namen, ohne Strings auf dem Heap.</summary>
    private int CompareExactly(ReadOnlySpan<byte> x, ReadOnlySpan<byte> y)
    {
        if (x.Length > ScratchChars || y.Length > ScratchChars)
            return string.Compare(System.Text.Encoding.UTF8.GetString(x), System.Text.Encoding.UTF8.GetString(y),
                StringComparison.OrdinalIgnoreCase);

        Span<char> left = stackalloc char[ScratchChars];
        Span<char> right = stackalloc char[ScratchChars];
        int leftLength = System.Text.Encoding.UTF8.GetChars(x, left);
        int rightLength = System.Text.Encoding.UTF8.GetChars(y, right);
        return left[..leftLength].CompareTo(right[..rightLength], StringComparison.OrdinalIgnoreCase);
    }

    public bool IsRemoved(int index) => (Flags[index] & EntryFlags.Removed) != 0;

    public bool IsDirectory(int index) => (Flags[index] & EntryFlags.Directory) != 0;

    /// <summary>
    /// Der vollstaendige Pfad eines Knotens.
    ///
    /// Der Baum haelt nur Namen und Elternbeziehungen; der Pfad entsteht beim Aufsteigen. Das
    /// ist Absicht — ihn je Knoten zu speichern kostete bei Millionen Eintraegen mehr
    /// Speicher als der ganze uebrige Baum.
    /// </summary>
    public string PathOf(int index)
    {
        if (index <= 0) return RootPath;

        // Erst die Kette der Vorfahren einsammeln, dann in einem Zug schreiben — ohne Liste,
        // ohne Insert(0), ohne Zwischenstrings. Diese Funktion laeuft in Suche, Export und
        // Hardlink-Abgleich millionenfach.
        int depth = 0;
        for (int i = index; i > 0; i = ParentIndex[i]) depth++;

        int[]? rented = depth > 64 ? System.Buffers.ArrayPool<int>.Shared.Rent(depth) : null;
        Span<int> chain = rented ?? stackalloc int[64];
        chain = chain[..depth];
        for (int i = index, k = depth - 1; i > 0; i = ParentIndex[i]) chain[k--] = i;

        string root = RootPath;
        bool rootHasSeparator = root.Length > 0 && root[^1] == Path.DirectorySeparatorChar;
        int length = root.Length + (rootHasSeparator ? 0 : 1) + depth - 1;
        foreach (int node in chain)
            length += System.Text.Encoding.UTF8.GetCharCount(Names.GetBytes(NameOffset[node], NameLength[node]));

        string result = string.Create(length, (this, root, rootHasSeparator, chain.ToArray()), static (span, state) =>
        {
            (NodeStore store, string root, bool hasSeparator, int[] chain) = state;
            root.AsSpan().CopyTo(span);
            int pos = root.Length;
            if (!hasSeparator) span[pos++] = Path.DirectorySeparatorChar;
            for (int k = 0; k < chain.Length; k++)
            {
                if (k > 0) span[pos++] = Path.DirectorySeparatorChar;
                int node = chain[k];
                pos += System.Text.Encoding.UTF8.GetChars(
                    store.Names.GetBytes(store.NameOffset[node], store.NameLength[node]), span[pos..]);
            }
        });

        if (rented is not null) System.Buffers.ArrayPool<int>.Shared.Return(rented);
        return result;
    }

    /// <summary>
    /// Der Name, wie er angezeigt gehoert.
    ///
    /// Die Wurzel traegt keinen: Sie ist durch <see cref="RootPath"/> benannt, nicht durch
    /// einen Eintrag im Namensvorrat. Wer sie beschriften will, braucht daher den letzten
    /// Teil des Pfads — und bei einer Laufwerkswurzel wie "C:\", die keinen letzten Teil
    /// hat, den Pfad selbst.
    /// </summary>
    public string DisplayName(int index)
    {
        string name = GetName(index);
        if (name.Length > 0) return name;

        string leaf = Path.GetFileName(RootPath.TrimEnd(Path.DirectorySeparatorChar));
        return leaf.Length > 0 ? leaf : RootPath;
    }
}
