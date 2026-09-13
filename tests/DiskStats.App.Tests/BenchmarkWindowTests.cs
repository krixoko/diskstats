using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using DiskStats.App.Localization;
using DiskStats.Core.Benchmarking;

namespace DiskStats.App.Tests;

public class BenchmarkWindowTests
{
    private static readonly BenchmarkDrive[] Drives =
    [new("C:\\", "C:\\test", "C: Test drive"), new("D:\\", "D:\\test", "D: Second drive")];

    [AvaloniaFact]
    public async Task Selecting_a_drive_does_not_start_io_and_changing_it_clears_results()
    {
        int runs = 0;
        string? target = null;
        var window = new BenchmarkWindow(Drives, (directory, progress, _) =>
        {
            runs++;
            target = directory;
            foreach (BenchmarkPhase phase in Enum.GetValues<BenchmarkPhase>().Skip(1))
                progress.Report(new(phase, 1, new(4_000_000, 1000, TimeSpan.FromSeconds(1))));
            return Task.CompletedTask;
        }, "D:\\folder");
        window.Show();
        try
        {
            Assert.Equal(0, runs);
            await window.StartRunAsync();
            Assert.Equal("D:\\test", target);
            Assert.Equal(1, runs);
            Assert.NotEqual("—", window.SequentialRead.Text);
            Assert.NotEqual("— ms", window.ReadLatency.Text);
            Assert.NotEqual("—", window.Random64Read.Text);
            Assert.NotEqual("—", window.Random64Write.Text);
            Assert.NotEqual("—", window.MixedSpeed.Text);
            Assert.NotEqual("— ms", window.MixedLatency.Text);
            window.DrivePicker.SelectedIndex = 0;
            Assert.Equal("—", window.SequentialRead.Text);
            Assert.Equal("— IOPS", window.ReadDetails.Text);
            Assert.Equal("—", window.Random64Read.Text);
            Assert.Equal("—", window.Random64Write.Text);
            Assert.Equal("—", window.MixedSpeed.Text);
            Assert.Equal("— IOPS", window.MixedDetails.Text);
            Assert.Equal(1, runs);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Progress_covers_all_eight_stages_and_file_size_is_visible()
    {
        var finish = new TaskCompletionSource();
        IProgress<BenchmarkProgress>? sink = null;
        var window = new BenchmarkWindow(Drives, (_, progress, _) => { sink = progress; return finish.Task; });
        window.Show();
        try
        {
            Task running = window.StartRunAsync();
            Assert.Contains("1 GiB", window.FileSizeHint.Text);
            sink!.Report(new(BenchmarkPhase.RandomWrite, 1));
            Assert.Equal(62.5, window.Progress.Value);
            sink.Report(new(BenchmarkPhase.Random64Write, 1));
            Assert.Equal(87.5, window.Progress.Value);
            sink.Report(new(BenchmarkPhase.MixedRandom, 0.5));
            Assert.Equal(93.75, window.Progress.Value);
            finish.SetResult();
            await running;
            Assert.Equal(100, window.Progress.Value);
        }
        finally { finish.TrySetResult(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Closing_cancels_and_waits_for_cleanup_before_the_window_disappears()
    {
        var entered = new TaskCompletionSource();
        bool cleanup = false;
        var window = new BenchmarkWindow(Drives, async (_, _, token) =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { cleanup = true; }
        });
        window.Show();
        Task run = window.StartRunAsync();
        await entered.Task;
        Assert.True(window.IsRunning);
        Assert.False(window.DrivePicker.IsEnabled);
        window.Close();
        await run;
        Assert.True(cleanup);
        Assert.False(window.IsVisible);
        Assert.False(window.IsRunning);
    }

    [AvaloniaFact]
    public async Task Error_restores_controls_and_late_progress_cannot_change_a_finished_run()
    {
        IProgress<BenchmarkProgress>? delayed = null;
        var window = new BenchmarkWindow(Drives, (_, progress, _) =>
        {
            delayed = progress;
            return Task.FromException(new IOException("Drive removed"));
        });
        window.Show();
        try
        {
            await window.StartRunAsync();
            Assert.Contains("Drive removed", window.Status.Text);
            string? status = window.Status.Text;
            delayed!.Report(new(BenchmarkPhase.SequentialRead, 1, new(100, 1, TimeSpan.FromSeconds(1))));
            Assert.Equal(status, window.Status.Text);
            Assert.Equal("—", window.SequentialRead.Text);
            Assert.True(window.DrivePicker.IsEnabled);
            Assert.True(window.StartButton.IsEnabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void No_drive_disables_start()
    {
        var window = new BenchmarkWindow([], (_, _, _) => throw new InvalidOperationException());
        window.Show();
        try { Assert.False(window.StartButton.IsEnabled); }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Cancel_button_keeps_completed_results_and_blocks_duplicate_starts()
    {
        int runs = 0;
        var window = new BenchmarkWindow(Drives, async (_, progress, token) =>
        {
            runs++;
            progress.Report(new(BenchmarkPhase.SequentialRead, 1, new(1_000_000, 1, TimeSpan.FromSeconds(1))));
            await Task.Delay(Timeout.Infinite, token);
        });
        window.Show();
        try
        {
            Task running = window.StartRunAsync();
            await window.StartRunAsync();
            Assert.Equal(1, runs);
            window.StartButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await running;
            Assert.NotEqual("—", window.SequentialRead.Text);
            Assert.Equal(Loc.T("Bench_Cancelled"), window.Status.Text);
            Assert.True(window.StartButton.IsEnabled);
        }
        finally { window.Close(); }
    }
}
