using DiskStats.Core.Storage;

namespace DiskStats.Core.Tests;

public class NamePoolTests
{
    [Fact]
    public void Gibt_gespeicherten_namen_unveraendert_zurueck()
    {
        var pool = new NamePool();
        int offset = pool.Add("bericht.pdf");

        Assert.Equal("bericht.pdf", pool.GetString(offset, NamePool.Utf8Length("bericht.pdf")));
    }

    [Fact]
    public void Haelt_mehrere_namen_getrennt()
    {
        var pool = new NamePool();
        int a = pool.Add("erste");
        int b = pool.Add("zweite");

        Assert.Equal("erste", pool.GetString(a, NamePool.Utf8Length("erste")));
        Assert.Equal("zweite", pool.GetString(b, NamePool.Utf8Length("zweite")));
    }

    [Fact]
    public void Behandelt_umlaute_und_emoji_korrekt()
    {
        var pool = new NamePool();
        const string name = "Größenübersicht 📊.xlsx";
        int offset = pool.Add(name);

        Assert.Equal(name, pool.GetString(offset, NamePool.Utf8Length(name)));
    }

    [Fact]
    public void Waechst_ueber_die_anfangskapazitaet_hinaus()
    {
        var pool = new NamePool(initialCapacity: 8);
        var offsets = new List<(int Offset, int Length, string Name)>();

        for (int i = 0; i < 500; i++)
        {
            string name = $"datei-{i}.bin";
            offsets.Add((pool.Add(name), NamePool.Utf8Length(name), name));
        }

        foreach ((int offset, int length, string name) in offsets)
            Assert.Equal(name, pool.GetString(offset, length));
    }

    [Fact]
    public void Ueberlebt_serialisierung()
    {
        var pool = new NamePool();
        int offset = pool.Add("wichtig.txt");

        var restored = NamePool.FromBytes(pool.ToArray());

        Assert.Equal("wichtig.txt", restored.GetString(offset, NamePool.Utf8Length("wichtig.txt")));
    }
}
