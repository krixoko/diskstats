using DiskStats.App.Rendering.Engines;

namespace DiskStats.App.Tests;

/// <summary>
/// Die Rangliste sammelt die k groessten Eintraege aus einem Lauf ueber Millionen Dateien.
/// Ihr Ergebnis muss genau dem entsprechen, was ein vollstaendiges Sortieren ergaebe —
/// absteigend nach Groesse, bei gleicher Groesse in der Reihenfolge des Angebots, also nach
/// Knotenindex, wenn in dieser Reihenfolge angeboten wird.
/// </summary>
public class TopListTests
{
    private static (int Node, long Size)[] Reference(IReadOnlyList<long> sizes, int k)
        => [.. sizes.Select((size, node) => (Node: node, Size: size))
            .OrderByDescending(e => e.Size)     // stabil: Gleichstand bleibt in Angebotsreihenfolge
            .Take(k)];

    [Theory]
    [InlineData(1, 1000, 1)]
    [InlineData(2, 1000, 10)]
    [InlineData(3, 5000, 40)]
    [InlineData(4, 100, 500)]     // weniger Eintraege als Plaetze
    [InlineData(5, 0, 8)]         // gar keine Eintraege
    public void Liefert_die_k_groessten_wie_eine_volle_sortierung(int seed, int count, int k)
    {
        var random = new Random(seed);
        long[] sizes = [.. Enumerable.Range(0, count).Select(_ => random.NextInt64(0, 1L << 40))];

        var list = new TopList(k);
        for (int node = 0; node < sizes.Length; node++) list.Offer(node, sizes[node]);

        Assert.Equal(Reference(sizes, k), list.Entries);
    }

    /// <summary>
    /// Viele gleiche Groessen — der Fall, in dem eine Halde ohne Zweitschluessel die Reihenfolge
    /// unter Gleichen beliebig wuerfelt.
    /// </summary>
    [Theory]
    [InlineData(11, 2000, 25)]
    [InlineData(12, 3000, 7)]
    public void Bei_gleichstand_bleibt_die_angebotsreihenfolge(int seed, int count, int k)
    {
        var random = new Random(seed);
        long[] sizes = [.. Enumerable.Range(0, count).Select(_ => (long)random.Next(0, 12) * 1024)];

        var list = new TopList(k);
        for (int node = 0; node < sizes.Length; node++) list.Offer(node, sizes[node]);

        Assert.Equal(Reference(sizes, k), list.Entries);
    }

    [Fact]
    public void Nach_weiteren_angeboten_ist_das_ergebnis_wieder_aktuell()
    {
        var list = new TopList(3);
        list.Offer(0, 10);
        list.Offer(1, 30);
        _ = list.Entries;

        list.Offer(2, 20);
        list.Offer(3, 40);

        Assert.Equal([(3, 40L), (1, 30L), (2, 20L)], list.Entries);
    }
}
