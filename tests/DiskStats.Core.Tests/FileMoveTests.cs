using DiskStats.Core.Cleanup;

namespace DiskStats.Core.Tests;

public sealed class FileMoveTests
{
    [Fact]
    public void Preflight_deduplicates_parent_and_child_and_never_moves_them()
    {
        using var tree = new TestTree().AddFile("source/folder/a.txt", 45).AddFile("source/folder/b.txt", 80).AddDirectory("target");
        var plan = FileMove.Prepare([tree.PathOf("source/folder/a.txt"), tree.PathOf("source/folder"), tree.PathOf("source/folder")], tree.PathOf("target"));
        Assert.Single(plan.Sources);
        Assert.Equal(125, plan.Bytes);
        Assert.True(File.Exists(tree.PathOf("source/folder/a.txt")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(tree.PathOf("target")));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("source/folder")]
    [InlineData("source/folder/inside")]
    public void Rejects_noop_and_destination_inside_source(string destination)
    {
        using var tree = new TestTree().AddFile("source/folder/a.txt", 45).AddDirectory("source/folder/inside");
        Assert.Throws<IOException>(() => FileMove.Prepare([tree.PathOf("source/folder")], tree.PathOf(destination)));
    }

    [Fact]
    public void Existing_target_is_left_for_explicit_Windows_conflict_resolution()
    {
        using var tree = new TestTree().AddFile("source/a.txt", 45).AddFile("target/a.txt", 90);
        var plan = FileMove.Prepare([tree.PathOf("source/a.txt")], tree.PathOf("target"));
        Assert.Equal(45, plan.Bytes);
        Assert.Equal(90, new FileInfo(tree.PathOf("target/a.txt")).Length);
    }

    [Fact]
    public void Cancellation_stops_preflight_without_changing_files()
    {
        using var tree = new TestTree().AddFile("source/a.txt", 45).AddDirectory("target");
        Assert.Throws<OperationCanceledException>(() => FileMove.Prepare([tree.PathOf("source/a.txt")], tree.PathOf("target"), new CancellationToken(true)));
        Assert.True(File.Exists(tree.PathOf("source/a.txt")));
    }
}
