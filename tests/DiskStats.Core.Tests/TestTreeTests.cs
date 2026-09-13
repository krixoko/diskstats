namespace DiskStats.Core.Tests;

public class TestTreeTests
{
    [Fact]
    public void Legt_dateien_mit_der_angegebenen_groesse_an()
    {
        using var tree = new TestTree();
        tree.AddFile("a/b.bin", 1234);

        Assert.Equal(1234, new FileInfo(tree.PathOf("a/b.bin")).Length);
    }

    [Fact]
    public void Raeumt_beim_dispose_auf()
    {
        string root;
        using (var tree = new TestTree())
        {
            root = tree.Root;
            tree.AddFile("x.bin", 1);
        }

        Assert.False(Directory.Exists(root));
    }
}
