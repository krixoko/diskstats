using System.Text;

namespace DiskStats.Core.Storage;

/// <summary>
/// Sucht Namen im Baum.
///
/// Die Suche laeuft direkt ueber die UTF-8-Bytes des Namens-Pools. Bei zwei Millionen
/// Eintraegen und einer Suche bei jedem Tastendruck waere es sonst genau der Fall, den der
/// Pool ueberhaupt erst vermeiden soll: zwei Millionen kurzlebige Zeichenketten je Anschlag.
///
/// Gross- und Kleinschreibung wird fuer ASCII direkt in den Bytes verglichen. Enthaelt der
/// Suchbegriff etwas anderes — Umlaute etwa — greift der genaue Vergleich, weil sich die
/// Faltung dort nicht Byte fuer Byte machen laesst.
/// </summary>
public static class NodeSearch
{
    public readonly record struct Hit(int Node, long Size);

    public static IReadOnlyList<Hit> Find(NodeStore store, string term, int max = 200)
    {
        if (string.IsNullOrWhiteSpace(term) || max <= 0) return [];

        byte[] needle = Encoding.UTF8.GetBytes(term.Trim());
        if (needle.Length == 0) return [];

        bool foldCase = IsAscii(needle);
        if (foldCase) Lower(needle);

        var hits = new List<Hit>(Math.Min(max, 256));
        long threshold = long.MinValue;

        for (int node = 1; node < store.Count; node++)
        {
            if (store.IsRemoved(node)) continue;
            long size = store.Size[node];
            if (hits.Count == max && size <= threshold) continue;

            ReadOnlySpan<byte> name = store.Names.GetBytes(store.NameOffset[node], store.NameLength[node]);
            if (!Contains(name, needle, foldCase)) continue;

            Insert(hits, new Hit(node, size), max);
            if (hits.Count == max) threshold = hits[^1].Size;
        }

        return hits;
    }

    /// <summary>Haelt die Liste absteigend sortiert, ohne am Ende alles zu sortieren.</summary>
    private static void Insert(List<Hit> hits, Hit hit, int max)
    {
        int position = hits.FindIndex(h => h.Size < hit.Size);
        if (position < 0) position = hits.Count;

        hits.Insert(position, hit);
        if (hits.Count > max) hits.RemoveAt(hits.Count - 1);
    }

    private static bool Contains(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle, bool foldCase)
    {
        if (needle.Length > haystack.Length) return false;
        if (!foldCase) return haystack.IndexOf(needle) >= 0;

        for (int start = 0; start + needle.Length <= haystack.Length; start++)
        {
            int i = 0;
            while (i < needle.Length && ToLower(haystack[start + i]) == needle[i]) i++;
            if (i == needle.Length) return true;
        }

        return false;
    }

    private static bool IsAscii(ReadOnlySpan<byte> value)
    {
        foreach (byte b in value)
            if (b >= 0x80) return false;

        return true;
    }

    private static void Lower(Span<byte> value)
    {
        for (int i = 0; i < value.Length; i++) value[i] = ToLower(value[i]);
    }

    private static byte ToLower(byte b) => b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 32) : b;
}
