using System.ComponentModel;
using DiskStats.Core.Benchmarking;

namespace DiskStats.Core.Tests;

public sealed class DiskBenchmarkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "diskstats-benchmark-tests-" + Guid.NewGuid().ToString("N"));
    private static BenchmarkOptions Small => new()
    {
        FileBytes = 2 * 1024 * 1024,
        MaxPhaseBytes = 2 * 1024 * 1024,
        PhaseDuration = TimeSpan.FromMilliseconds(30)
    };

    public DiskBenchmarkTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Units_are_decimal_MB_per_second_and_mean_latency()
    {
        var value = new BenchmarkMeasurement(8_000_000, 2000, TimeSpan.FromSeconds(2));
        Assert.Equal(4, value.MegabytesPerSecond);
        Assert.Equal(1000, value.Iops);
        Assert.Equal(1, value.LatencyMilliseconds);
    }

    [Fact]
    public async Task Seven_measurements_are_bounded_and_only_scratch_data_is_removed()
    {
        string existing = Path.Combine(_root, "keep.txt");
        await File.WriteAllTextAsync(existing, "personal data");
        var results = new List<BenchmarkProgress>();
        var transfers = new List<(long Offset, int Bytes, bool Write)>();
        await DiskBenchmark.RunAsync(_root, Small, new ProgressSink(results.Add), CancellationToken.None,
            (path, _) => new FakeFile(path, (offset, count, write) => transfers.Add((offset, count, write))));

        Assert.Equal(7, results.Count(p => p.Result is not null));
        Assert.Equal(Enum.GetValues<BenchmarkPhase>(), results.Select(p => p.Phase).Distinct());
        Assert.All(transfers, t =>
        {
            Assert.InRange(t.Offset, 0, Small.FileBytes - t.Bytes);
            Assert.Equal(0, t.Offset % 4096);
            Assert.Contains(t.Bytes, new[] { 4096, 64 * 1024, 1024 * 1024 });
        });
        Assert.All(results.Where(p => p.Result is not null), p => Assert.InRange(p.Result!.Bytes, 1, Small.MaxPhaseBytes));
        Assert.True(transfers.Take(2).All(t => t.Write)); // Fully initialized before the first read.
        Assert.Equal("personal data", await File.ReadAllTextAsync(existing));
        Assert.Equal(new[] { existing }, Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public async Task Default_preparation_initializes_exactly_one_GiB_before_measurements()
    {
        using var cancel = new CancellationTokenSource();
        long initialized = 0;
        long end = 0;
        var progress = new ProgressSink(p =>
        {
            if (p.Phase == BenchmarkPhase.SequentialRead) cancel.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DiskBenchmark.RunAsync(_root,
            new BenchmarkOptions(), progress, cancel.Token, (path, _) => new FakeFile(path, (offset, count, write) =>
            {
                Assert.True(write);
                Assert.Equal(end, offset);
                initialized += count;
                end = offset + count;
            })));
        Assert.Equal(1_073_741_824, initialized);
        Assert.Equal(1_073_741_824, end);
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public async Task Additional_phases_use_64_KiB_and_an_exact_70_30_mix()
    {
        BenchmarkPhase phase = BenchmarkPhase.Preparing;
        var transfers = new Dictionary<BenchmarkPhase, List<(int Count, bool Write)>>();
        var results = new Dictionary<BenchmarkPhase, BenchmarkMeasurement>();
        await DiskBenchmark.RunAsync(_root, Small, new ProgressSink(p =>
        {
            phase = p.Phase;
            if (p.Result is { } value) results[phase] = value;
        }), CancellationToken.None, (path, _) => new FakeFile(path, (_, count, write) =>
        {
            if (!transfers.TryGetValue(phase, out var items)) transfers[phase] = items = [];
            items.Add((count, write));
        }));
        Assert.All(transfers[BenchmarkPhase.Random64Read], t => { Assert.Equal(65536, t.Count); Assert.False(t.Write); });
        Assert.All(transfers[BenchmarkPhase.Random64Write], t => { Assert.Equal(65536, t.Count); Assert.True(t.Write); });
        var mixed = transfers[BenchmarkPhase.MixedRandom];
        Assert.All(mixed, t => Assert.Equal(4096, t.Count));
        Assert.Equal(0, mixed.Count % 10);
        Assert.Equal(mixed.Count * 7 / 10, mixed.Count(t => !t.Write));
        Assert.Equal(mixed.Count * 3 / 10, mixed.Count(t => t.Write));
        Assert.Equal(mixed.Count(t => !t.Write), results[BenchmarkPhase.MixedRandom].ReadOperations);
        Assert.Equal(mixed.Count(t => t.Write), results[BenchmarkPhase.MixedRandom].WriteOperations);
        Assert.Equal(mixed.Count * 4096L, results[BenchmarkPhase.MixedRandom].Bytes);
    }

    [Theory]
    [InlineData(BenchmarkPhase.Random64Read)]
    [InlineData(BenchmarkPhase.Random64Write)]
    [InlineData(BenchmarkPhase.MixedRandom)]
    public async Task Cancellation_inside_each_added_phase_cleans_up(BenchmarkPhase target)
    {
        using var cancel = new CancellationTokenSource();
        BenchmarkPhase phase = BenchmarkPhase.Preparing;
        var results = new List<BenchmarkPhase>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DiskBenchmark.RunAsync(_root, Small,
            new ProgressSink(p => { phase = p.Phase; if (p.Result is not null) results.Add(phase); }), cancel.Token,
            (path, _) => new FakeFile(path, (_, _, _) => { if (phase == target) cancel.Cancel(); })));
        Assert.DoesNotContain(target, results);
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public async Task Cancellation_during_preparation_removes_the_file_and_directory()
    {
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DiskBenchmark.RunAsync(_root, Small, null,
            cancel.Token, (path, _) => new FakeFile(path, (_, _, _) => cancel.Cancel())));
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public async Task Io_failure_removes_scratch_and_allows_another_run()
    {
        await Assert.ThrowsAsync<IOException>(() => DiskBenchmark.RunAsync(_root, Small, null, CancellationToken.None,
            (path, _) => new FakeFile(path, (_, _, _) => throw new IOException("Drive disconnected"))));
        Assert.Empty(Directory.GetFileSystemEntries(_root));
        await DiskBenchmark.RunAsync(_root, Small, null, CancellationToken.None, (path, _) => new FakeFile(path));
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public async Task Cancelled_or_invalid_requests_never_create_files()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DiskBenchmark.RunAsync(_root, Small, null,
            new CancellationToken(true)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => DiskBenchmark.RunAsync(_root,
            Small with { FileBytes = 123 }, null, CancellationToken.None));
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public async Task A_second_run_is_rejected_while_the_first_is_active()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        Task first = DiskBenchmark.RunAsync(_root, Small, null, CancellationToken.None,
            (path, _) => new FakeFile(path, (_, _, _) => { entered.TrySetResult(); Assert.True(release.Wait(5000)); }));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<InvalidOperationException>(() => DiskBenchmark.RunAsync(_root, Small, null, CancellationToken.None));
        }
        finally { release.Set(); await first; }
    }

    [Fact]
    public void Native_file_creation_cannot_overwrite_an_existing_file()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows direct I/O test");
        string path = Path.Combine(_root, "existing.bin");
        File.WriteAllText(path, "keep this data");
        Assert.Throws<Win32Exception>(() => new WindowsBenchmarkFile(path, new byte[1024 * 1024]));
        Assert.Equal("keep this data", File.ReadAllText(path));
    }

    [Fact]
    public async Task Native_direct_io_completes_all_phases_and_leaves_no_test_data()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows direct I/O test");
        var results = new List<BenchmarkProgress>();
        await DiskBenchmark.RunAsync(_root, Small, new ProgressSink(results.Add), CancellationToken.None);
        Assert.Equal(7, results.Count(p => p.Result is not null));
        Assert.All(results.Where(p => p.Result is not null), p => Assert.True(p.Result!.MegabytesPerSecond > 0));
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    private sealed class ProgressSink(Action<BenchmarkProgress> callback) : IProgress<BenchmarkProgress>
    {
        public void Report(BenchmarkProgress value) => callback(value);
    }

    private sealed class FakeFile : IBenchmarkFile
    {
        private readonly FileStream _file;
        private readonly Action<long, int, bool>? _onTransfer;
        public FakeFile(string path, Action<long, int, bool>? onTransfer = null)
        {
            _file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
            _onTransfer = onTransfer;
        }
        public void Transfer(long offset, int count, bool write) => _onTransfer?.Invoke(offset, count, write);
        public void Dispose() => _file.Dispose();
    }
}
