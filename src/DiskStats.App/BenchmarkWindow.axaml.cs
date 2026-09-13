using System.ComponentModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Threading;
using DiskStats.App.Localization;
using DiskStats.Core.Benchmarking;

namespace DiskStats.App;

internal sealed record BenchmarkDrive(string Root, string Directory, string Label)
{
    public override string ToString() => Label;
}

public partial class BenchmarkWindow : Window
{
    private readonly Func<string, IProgress<BenchmarkProgress>, CancellationToken, Task> _runner;
    private CancellationTokenSource? _run;
    private bool _closeWhenStopped;
    internal bool IsRunning => _run is not null;

    public BenchmarkWindow() : this(null) { }

    public BenchmarkWindow(string? selectedPath)
        : this(FindDrives(), DiskBenchmark.RunAsync, selectedPath) { }

    internal BenchmarkWindow(IReadOnlyList<BenchmarkDrive> drives,
        Func<string, IProgress<BenchmarkProgress>, CancellationToken, Task> runner, string? selectedPath = null)
    {
        InitializeComponent();
        _runner = runner;
        DrivePicker.ItemsSource = drives;
        DrivePicker.SelectedItem = drives.FirstOrDefault(d => selectedPath?.StartsWith(d.Root,
            StringComparison.OrdinalIgnoreCase) == true) ?? drives.FirstOrDefault();
        StartButton.IsEnabled = drives.Count > 0;
        if (drives.Count == 0) Status.Text = Loc.T("Bench_NoDrive");
        DrivePicker.SelectionChanged += (_, _) =>
        {
            if (IsRunning) return;
            ResetResults();
            Status.Text = Loc.T("Bench_Ready");
            StartButton.IsEnabled = DrivePicker.SelectedItem is BenchmarkDrive;
        };
        StartButton.Click += async (_, _) =>
        {
            if (IsRunning) CancelRun();
            else await StartRunAsync();
        };
        CloseButton.Click += (_, _) => Close();
        Closing += (_, e) =>
        {
            if (!IsRunning) return;
            e.Cancel = true;
            _closeWhenStopped = true;
            CancelRun();
        };
    }

    private void CancelRun()
    {
        _run?.Cancel();
        StartButton.IsEnabled = false;
        Status.Text = Loc.T("Bench_Stopping");
    }

    internal async Task StartRunAsync()
    {
        if (IsRunning || DrivePicker.SelectedItem is not BenchmarkDrive drive) return;
        using var run = new CancellationTokenSource();
        _run = run;
        ResetResults();
        DrivePicker.IsEnabled = false;
        StartButton.Content = Loc.T("Common_Cancel");
        Status.Text = Loc.T("Bench_Preparing");
        var progress = new RunProgress(value =>
        {
            if (!ReferenceEquals(_run, run) || run.IsCancellationRequested) return;
            ShowProgress(value);
        });
        try
        {
            await _runner(drive.Directory, progress, run.Token);
            run.Token.ThrowIfCancellationRequested();
            foreach (BenchmarkProgress result in progress.Results) ShowProgress(result);
            Progress.Value = 100;
            Status.Text = Loc.T("Bench_Complete");
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested)
        {
            foreach (BenchmarkProgress result in progress.Results) ShowProgress(result);
            Status.Text = Loc.T("Bench_Cancelled");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception
                                      or NotSupportedException or InvalidOperationException or ArgumentException)
        { Status.Text = Loc.T("Bench_Error", ex.Message); }
        finally
        {
            _run = null;
            DrivePicker.IsEnabled = true;
            StartButton.IsEnabled = DrivePicker.SelectedItem is BenchmarkDrive;
            StartButton.Content = Loc.T("Bench_Start");
            if (_closeWhenStopped) Close();
        }
    }

    private void ShowProgress(BenchmarkProgress value)
    {
        Progress.Value = ((int)value.Phase + Math.Clamp(value.Fraction, 0, 1)) * 100 / Enum.GetValues<BenchmarkPhase>().Length;
        Status.Text = Loc.T("Bench_" + value.Phase);
        if (value.Result is not { } result) return;
        TextBlock? cell = value.Phase switch
        {
            BenchmarkPhase.SequentialRead => SequentialRead,
            BenchmarkPhase.SequentialWrite => SequentialWrite,
            BenchmarkPhase.RandomRead => RandomRead,
            BenchmarkPhase.RandomWrite => RandomWrite,
            BenchmarkPhase.Random64Read => Random64Read,
            BenchmarkPhase.Random64Write => Random64Write,
            BenchmarkPhase.MixedRandom => MixedSpeed,
            _ => null
        };
        if (cell is not null) cell.Text = result.MegabytesPerSecond.ToString("N1", CultureInfo.CurrentCulture);
        (TextBlock Iops, TextBlock Latency)? details = value.Phase switch
        {
            BenchmarkPhase.RandomRead => (ReadDetails, ReadLatency),
            BenchmarkPhase.RandomWrite => (WriteDetails, WriteLatency),
            BenchmarkPhase.Random64Read => (Read64Details, Read64Latency),
            BenchmarkPhase.Random64Write => (Write64Details, Write64Latency),
            BenchmarkPhase.MixedRandom => (MixedDetails, MixedLatency),
            _ => null
        };
        if (details is { } fields)
        {
            fields.Iops.Text = result.Iops.ToString("N0", CultureInfo.CurrentCulture) + " IOPS";
            fields.Latency.Text = result.LatencyMilliseconds.ToString("N3", CultureInfo.CurrentCulture) + " ms";
        }
    }

    private void ResetResults()
    {
        foreach (TextBlock cell in new[] { SequentialRead, SequentialWrite, RandomRead, RandomWrite,
                     Random64Read, Random64Write, MixedSpeed }) cell.Text = "—";
        ReadDetails.Text = WriteDetails.Text = Read64Details.Text = Write64Details.Text = MixedDetails.Text = "— IOPS";
        ReadLatency.Text = WriteLatency.Text = Read64Latency.Text = Write64Latency.Text = MixedLatency.Text = "— ms";
        Progress.Value = 0;
    }

    private static IReadOnlyList<BenchmarkDrive> FindDrives()
    {
        var result = new List<BenchmarkDrive>();
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !drive.IsReady) continue;
                string root = drive.RootDirectory.FullName;
                string temp = Path.GetTempPath();
                string directory = string.Equals(Path.GetPathRoot(temp), root, StringComparison.OrdinalIgnoreCase) ? temp : root;
                result.Add(new(root, directory, $"{root}  {drive.VolumeLabel}   ·   {Loc.T("Bench_Free", Sizes.Format(drive.AvailableFreeSpace))}"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }

    private sealed class RunProgress(Action<BenchmarkProgress> onProgress) : IProgress<BenchmarkProgress>
    {
        private readonly List<BenchmarkProgress> _results = [];
        public BenchmarkProgress[] Results { get { lock (_results) return [.. _results]; } }
        public void Report(BenchmarkProgress value)
        {
            if (value.Result is not null) { lock (_results) _results.Add(value); }
            if (Dispatcher.UIThread.CheckAccess()) onProgress(value);
            else Dispatcher.UIThread.Post(() => onProgress(value));
        }
    }
}
