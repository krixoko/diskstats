using Avalonia.Headless.XUnit;
using DiskStats.App.Localization;
using DiskStats.Core.Health;

namespace DiskStats.App.Tests;

public class HealthWindowTests
{
    [AvaloniaFact]
    public async Task Nvme_warning_is_visible_even_when_windows_reports_healthy()
    {
        var disk = Disk("0") with { ReliabilityAvailable = false, NvmeSmartAvailable = true,
            NvmeCriticalWarnings = 10, MediaErrors = 0, UnsafeShutdowns = 7, WearUsed = 255 };
        var window = new HealthWindow(_ => Task.FromResult<IReadOnlyList<DiskHealthInfo>>([disk]), false);
        window.Show();
        try
        {
            await window.RefreshAsync();
            Assert.Equal(Loc.T("Health_Healthy"), window.HealthStatus.Text);
            Assert.True(window.NvmeStatus.IsVisible);
            Assert.Contains(Loc.T("Health_TempWarning"), window.NvmeStatus.Text);
            Assert.Contains(Loc.T("Health_ReadOnly"), window.NvmeStatus.Text);
            Assert.Contains("0x0A", window.NvmeStatus.Text);
            Assert.Equal("0", window.MediaErrors.Text);
            Assert.Equal("7", window.UnsafeShutdowns.Text);
            Assert.Equal("≥ 255 %", window.Wear.Text);
            Assert.Equal(Loc.T("Health_NvmeReported"), window.Availability.Text);
        }
        finally { window.Close(); }
    }

    private static DiskHealthInfo Disk(string id, ulong? temperature = 40) => new(
        id, id, "Test disk " + id, 1_000_000_000_000, 17, DiskHealthState.Healthy,
        true, null, temperature, 0, 1234, 0, 0, 0, 0);

    [AvaloniaFact]
    public async Task Missing_counters_are_unavailable_while_reported_zero_is_visible()
    {
        var window = new HealthWindow(_ => Task.FromResult<IReadOnlyList<DiskHealthInfo>>([Disk("0", null)]), false);
        window.Show();
        try
        {
            await window.RefreshAsync();
            Assert.Equal(Loc.T("Health_Unavailable"), window.Temperature.Text);
            Assert.Equal("0 %", window.Wear.Text);
            Assert.Equal("0", window.ReadErrors.Text);
            Assert.Equal(Loc.T("Health_Healthy"), window.HealthStatus.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Selection_follows_disk_identity_when_refresh_reorders_devices()
    {
        int calls = 0;
        var window = new HealthWindow(_ => Task.FromResult<IReadOnlyList<DiskHealthInfo>>(
            ++calls == 1 ? [Disk("0", 40), Disk("1", 50)] : [Disk("1", 51), Disk("0", 41)]), false);
        window.Show();
        try
        {
            await window.RefreshAsync();
            window.DiskPicker.SelectedIndex = 1;
            Assert.Equal("50 °C", window.Temperature.Text);
            await window.RefreshAsync();
            Assert.Equal(0, window.DiskPicker.SelectedIndex);
            Assert.Equal("51 °C", window.Temperature.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Failed_refresh_clears_stale_health_values_and_enables_retry()
    {
        int calls = 0;
        var window = new HealthWindow(_ => ++calls == 1
            ? Task.FromResult<IReadOnlyList<DiskHealthInfo>>([Disk("0")])
            : Task.FromException<IReadOnlyList<DiskHealthInfo>>(new IOException("Drive removed")), false);
        window.Show();
        try
        {
            await window.RefreshAsync();
            await window.RefreshAsync();
            Assert.Equal("—", window.HealthStatus.Text);
            Assert.Equal(Loc.T("Health_Unavailable"), window.Temperature.Text);
            Assert.Contains("Drive removed", window.Status.Text);
            Assert.True(window.RefreshButton.IsEnabled);
            Assert.False(window.Loading.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Closing_cancels_query_and_ignores_late_results()
    {
        var complete = new TaskCompletionSource<IReadOnlyList<DiskHealthInfo>>();
        CancellationToken token = default;
        int calls = 0;
        var window = new HealthWindow(t => { token = t; calls++; return complete.Task; }, false);
        window.Show();
        Task refresh = window.RefreshAsync();
        await window.RefreshAsync();
        Assert.Equal(1, calls);
        window.Close();
        Assert.True(token.IsCancellationRequested);
        complete.SetResult([Disk("0")]);
        await refresh;
        Assert.Equal("—", window.HealthStatus.Text);
        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public async Task Empty_provider_response_shows_no_disks_instead_of_a_health_score()
    {
        var window = new HealthWindow(_ => Task.FromResult<IReadOnlyList<DiskHealthInfo>>([]), false);
        window.Show();
        try
        {
            await window.RefreshAsync();
            Assert.Equal(Loc.T("Health_NoDisks"), window.Status.Text);
            Assert.Equal("—", window.HealthStatus.Text);
            Assert.False(window.DiskPicker.IsEnabled);
        }
        finally { window.Close(); }
    }
}
