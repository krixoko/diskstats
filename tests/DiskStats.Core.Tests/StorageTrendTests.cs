using DiskStats.Core.Analysis;
using DiskStats.Core.Scanning;

namespace DiskStats.Core.Tests;

public sealed class StorageTrendTests
{
    private static Core.Storage.NodeStore Scan(string root)
    {
        var buffer = new Core.Storage.NodeBuffer();
        DirectoryWalker.Walk(root, buffer, new WalkOptions());
        var store = Core.Storage.NodeStoreBuilder.Build(buffer, root);
        Core.Storage.SizeAggregator.Aggregate(store);
        return store;
    }

    [Fact]
    public void Reports_history_and_growth_for_selected_folder_with_actual_dates()
    {
        using var tree = new TestTree().AddFile("downloads/a.txt", 100).AddFile("other/b.txt", 500);
        var before = Scan(tree.Root);
        tree.AddFile("downloads/a.txt", 300).AddFile("downloads/new.txt", 70);
        var after = Scan(tree.Root);
        DateTime day = new(2026, 9, 1);
        var result = StorageTrend.Build(tree.PathOf("downloads"), [(day, before), (day.AddDays(12), after)]);
        Assert.Equal(new long?[] { 100, 370 }, result.Points.Select(p => p.Bytes));
        Assert.Equal(day.AddDays(12), result.Points[1].Written);
        Assert.Equal(new long[] { 200, 70 }, result.Growth.Select(g => g.Delta));
        Assert.DoesNotContain(result.Growth, g => g.Path.Contains("other"));
    }

    [Fact]
    public void Missing_folder_is_a_gap_and_one_scan_has_no_growth()
    {
        using var tree = new TestTree().AddFile("other.txt", 50);
        var before = Scan(tree.Root);
        tree.AddFile("new/a.txt", 80);
        var after = Scan(tree.Root);
        var report = StorageTrend.Build(tree.PathOf("new"), [(DateTime.Today, before), (DateTime.Today.AddDays(1), after)]);
        Assert.Null(report.Points[0].Bytes);
        Assert.Equal(80, report.Points[1].Bytes);
        Assert.Empty(StorageTrend.Build(tree.Root, [(DateTime.Today, after)]).Growth);
    }
}
