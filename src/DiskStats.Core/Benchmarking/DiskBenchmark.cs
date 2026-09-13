using System.Diagnostics;
using System.Security.Cryptography;

namespace DiskStats.Core.Benchmarking;

public enum BenchmarkPhase
{
    Preparing, SequentialRead, SequentialWrite, RandomRead, RandomWrite,
    Random64Read, Random64Write, MixedRandom
}

public sealed record BenchmarkMeasurement(long Bytes, long Operations, TimeSpan Elapsed)
{
    public long ReadOperations { get; init; }
    public long WriteOperations { get; init; }
    public double MegabytesPerSecond => Elapsed.TotalSeconds > 0 ? Bytes / 1_000_000d / Elapsed.TotalSeconds : 0;
    public double Iops => Elapsed.TotalSeconds > 0 ? Operations / Elapsed.TotalSeconds : 0;
    public double LatencyMilliseconds => Operations > 0 ? Elapsed.TotalMilliseconds / Operations : 0;
}

public sealed record BenchmarkProgress(BenchmarkPhase Phase, double Fraction, BenchmarkMeasurement? Result = null);

public sealed record BenchmarkOptions
{
    public const long DefaultFileBytes = 1024L * 1024 * 1024;
    public long FileBytes { get; init; } = DefaultFileBytes;
    public TimeSpan PhaseDuration { get; init; } = TimeSpan.FromSeconds(3);
    public long MaxPhaseBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    internal void Validate()
    {
        if (FileBytes < 1024 * 1024 || FileBytes > 1024L * 1024 * 1024 || FileBytes % (1024 * 1024) != 0)
            throw new ArgumentOutOfRangeException(nameof(FileBytes));
        if (PhaseDuration <= TimeSpan.Zero || PhaseDuration > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(PhaseDuration));
        if (MaxPhaseBytes < FileBytes || MaxPhaseBytes > 4L * 1024 * 1024 * 1024 || MaxPhaseBytes % (1024 * 1024) != 0)
            throw new ArgumentOutOfRangeException(nameof(MaxPhaseBytes));
    }
}

/// <summary>A bounded Q1T1 file benchmark. Never opens a raw volume or an existing data file.</summary>
public static class DiskBenchmark
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private const int SequentialBlock = 1024 * 1024;
    private const int RandomBlock = 4096;

    public static Task RunAsync(string directory, IProgress<BenchmarkProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => RunAsync(directory, new BenchmarkOptions(), progress, cancellationToken);

    internal static async Task RunAsync(string directory, BenchmarkOptions options,
        IProgress<BenchmarkProgress>? progress, CancellationToken cancellationToken,
        Func<string, byte[], IBenchmarkFile>? factory = null)
    {
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (!await Gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("A disk benchmark is already running.");
        try
        {
            await Task.Run(() => Run(directory, options, progress, cancellationToken,
                factory ?? ((path, data) => new WindowsBenchmarkFile(path, data))), cancellationToken).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    private static void Run(string directory, BenchmarkOptions options, IProgress<BenchmarkProgress>? progress,
        CancellationToken cancel, Func<string, byte[], IBenchmarkFile> factory)
    {
        string parent = Path.GetFullPath(directory);
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException(parent);
        var drive = new DriveInfo(Path.GetPathRoot(parent)!);
        if (drive.AvailableFreeSpace < options.FileBytes + 256L * 1024 * 1024)
            throw new IOException("At least the test file size plus 256 MiB of free space is required.");

        // A fresh directory on the selected volume; no recursive deletion, even on failure.
        string scratch = Path.Combine(parent, "DiskStats-benchmark-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            cancel.ThrowIfCancellationRequested();
            byte[] data = new byte[SequentialBlock];
            RandomNumberGenerator.Fill(data); // Incompressible data, not sparse/zero-filled blocks.
            using IBenchmarkFile file = factory(Path.Combine(scratch, "test.bin"), data);
            var notify = Stopwatch.StartNew();
            progress?.Report(new(BenchmarkPhase.Preparing, 0));
            for (long offset = 0; offset < options.FileBytes; offset += SequentialBlock)
            {
                cancel.ThrowIfCancellationRequested();
                file.Transfer(offset, SequentialBlock, write: true);
                if (notify.ElapsedMilliseconds >= 100)
                {
                    progress?.Report(new(BenchmarkPhase.Preparing, (offset + SequentialBlock) / (double)options.FileBytes));
                    notify.Restart();
                }
            }
            progress?.Report(new(BenchmarkPhase.Preparing, 1));
            foreach (BenchmarkPhase phase in Enum.GetValues<BenchmarkPhase>().Skip(1))
            {
                cancel.ThrowIfCancellationRequested();
                bool random = phase is not (BenchmarkPhase.SequentialRead or BenchmarkPhase.SequentialWrite);
                bool mixed = phase == BenchmarkPhase.MixedRandom;
                bool writeOnly = phase is BenchmarkPhase.SequentialWrite or BenchmarkPhase.RandomWrite or BenchmarkPhase.Random64Write;
                int block = phase is BenchmarkPhase.Random64Read or BenchmarkPhase.Random64Write ? 64 * 1024
                    : random ? RandomBlock : SequentialBlock;
                // Each random phase transfers at most 512 MiB (including reads in the mixed phase).
                long limit = random ? Math.Min(options.MaxPhaseBytes, 512L * 1024 * 1024) : options.MaxPhaseBytes;
                // Finish complete groups of seven reads + three writes so 70/30 stays exact.
                if (mixed) limit -= limit % (10 * block);
                var rng = new Random(42);
                long bytes = 0, operations = 0, readOperations = 0, writeOperations = 0;
                progress?.Report(new(phase, 0));
                notify.Restart();
                var watch = Stopwatch.StartNew();
                do
                {
                    cancel.ThrowIfCancellationRequested();
                    long offset = random ? rng.NextInt64(options.FileBytes / block) * block : bytes % options.FileBytes;
                    bool write = mixed ? operations % 10 is 2 or 5 or 9 : writeOnly;
                    file.Transfer(offset, block, write);
                    bytes += block;
                    operations++;
                    if (write) writeOperations++; else readOperations++;
                    if (notify.ElapsedMilliseconds >= 100)
                    {
                        progress?.Report(new(phase, Math.Min(1, Math.Max(bytes / (double)limit,
                            watch.Elapsed.TotalSeconds / options.PhaseDuration.TotalSeconds))));
                        notify.Restart();
                    }
                } while (bytes < limit && (watch.Elapsed < options.PhaseDuration || (mixed && operations % 10 != 0)));
                watch.Stop();
                cancel.ThrowIfCancellationRequested();
                progress?.Report(new(phase, 1, new(bytes, operations, watch.Elapsed)
                { ReadOperations = readOperations, WriteOperations = writeOperations }));
            }
        }
        finally { Directory.Delete(scratch, recursive: false); }
    }
}

internal interface IBenchmarkFile : IDisposable
{
    void Transfer(long offset, int count, bool write);
}
