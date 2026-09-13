using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DiskStats.Core.Health;

namespace DiskStats.Core.Tests;

public class DiskHealthTests
{
    [Fact]
    public void Nvme_smart_fills_counters_when_windows_denies_access_without_changing_windows_status()
    {
        byte[] log = new byte[512];
        log[0] = 10;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(log.AsSpan(1), 315);
        log[5] = 120;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(log.AsSpan(128), 12345);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(log.AsSpan(144), 7);
        var disk = Assert.Single(DiskHealthReader.Parse(JsonSerializer.Serialize(new[] { new
        {
            HealthStatus = 0, ReliabilityAvailable = false, ReliabilityError = "Access denied",
            NvmeSmartLog = Convert.ToBase64String(log)
        } })));
        Assert.True(disk.NvmeSmartAvailable);
        Assert.False(disk.ReliabilityAvailable);
        Assert.Equal(DiskHealthState.Healthy, disk.State);
        Assert.Equal((byte)10, disk.NvmeCriticalWarnings);
        Assert.Equal(42UL, disk.Temperature);
        Assert.Equal(120UL, disk.WearUsed);
        Assert.Equal(12345UL, disk.PowerOnHours);
        Assert.Equal(7UL, disk.UnsafeShutdowns);
        Assert.Equal(0UL, disk.MediaErrors);
        Assert.Null(disk.ReadErrors);
    }

    [Fact]
    public void Nvme_overflow_and_invalid_temperature_do_not_fabricate_values()
    {
        var original = Assert.Single(DiskHealthReader.Parse("[{}]"));
        byte[] log = new byte[512];
        log[5] = 255;
        log[136] = log[152] = log[168] = 1;
        var disk = DiskHealthReader.WithNvmeLog(original, log);
        Assert.True(disk.NvmeSmartAvailable);
        Assert.Null(disk.PowerOnHours);
        Assert.Null(disk.UnsafeShutdowns);
        Assert.Null(disk.MediaErrors);
        Assert.Null(disk.Temperature);
        Assert.Equal(255UL, disk.WearUsed);
    }

    [Fact]
    public void Invalid_optional_smart_logs_preserve_available_windows_data()
    {
        var original = Assert.Single(DiskHealthReader.Parse("""[{"ReliabilityAvailable":true,"Temperature":43}]"""));
        Assert.Equal(original, DiskHealthReader.WithNvmeLog(original, new byte[511]));
        Assert.Equal(original, DiskHealthReader.WithNvmeLog(original, new byte[512]));
        Assert.Equal(original, DiskHealthReader.WithNvmeLog(original, Enumerable.Repeat((byte)255, 512).ToArray()));
        var malformed = Assert.Single(DiskHealthReader.Parse("""[{"ReliabilityAvailable":true,"Temperature":43,"NvmeSmartLog":"invalid!"}]"""));
        Assert.Equal(original, malformed);
    }

    [Theory]
    [InlineData(0, DiskHealthState.Healthy)]
    [InlineData(1, DiskHealthState.Warning)]
    [InlineData(2, DiskHealthState.Unhealthy)]
    [InlineData(5, DiskHealthState.Unknown)]
    [InlineData(99, DiskHealthState.Unknown)]
    public void Windows_health_status_is_not_inferred_from_missing_counters(int code, DiskHealthState state)
    {
        var disk = Assert.Single(DiskHealthReader.Parse($$"""[{"HealthStatus":{{code}},"ReliabilityAvailable":false}]"""));
        Assert.Equal(state, disk.State);
        Assert.Null(disk.Temperature);
        Assert.Null(disk.WearUsed);
        Assert.Null(disk.PowerOnHours);
        Assert.Null(disk.ReadErrors);
    }

    [Fact]
    public void Reported_zero_is_preserved_but_missing_and_sentinel_values_are_not()
    {
        var disk = Assert.Single(DiskHealthReader.Parse("""
            [{"Id":"disk-A","Number":"0","Model":"Test NVMe","CapacityBytes":1024209543168,
              "BusType":17,"HealthStatus":0,"ReliabilityAvailable":true,
              "Temperature":0,"WearUsed":0,"PowerOnHours":0,"ReadErrors":0,
              "WriteErrors":null,"UncorrectedReadErrors":18446744073709551615,
              "UncorrectedWriteErrors":4294967295}]
            """));
        Assert.Equal(0UL, disk.WearUsed);
        Assert.Equal(0UL, disk.PowerOnHours);
        Assert.Equal(0UL, disk.ReadErrors);
        Assert.Null(disk.Temperature);
        Assert.Null(disk.WriteErrors);
        Assert.Null(disk.UncorrectedReadErrors);
        Assert.Null(disk.UncorrectedWriteErrors);
        Assert.Equal(1024209543168UL, disk.CapacityBytes);
    }

    [Fact]
    public void Different_devices_keep_their_own_counters_and_errors()
    {
        var disks = DiskHealthReader.Parse("""
            [{"Id":"one","Number":"0","Model":"SSD","HealthStatus":0,"ReliabilityAvailable":true,
              "Temperature":42,"WearUsed":7,"PowerOnHours":1234,"ReadErrors":5000000000},
             {"Id":"two","Number":"1","Model":"USB","HealthStatus":1,"ReliabilityAvailable":false,
              "ReliabilityError":"Access denied","Temperature":99,"WearUsed":0}]
            """);
        Assert.Equal(2, disks.Count);
        Assert.Equal(42UL, disks[0].Temperature);
        Assert.Equal(7UL, disks[0].WearUsed);
        Assert.Equal(5000000000UL, disks[0].ReadErrors);
        Assert.Equal("two", disks[1].Id);
        Assert.Null(disks[1].Temperature);
        Assert.Null(disks[1].WearUsed);
        Assert.Equal("Access denied", disks[1].ReliabilityError);
    }

    [Fact]
    public void Unknown_status_and_invalid_numbers_never_become_healthy_or_zero()
    {
        var disk = Assert.Single(DiskHealthReader.Parse("""
            [{"ReliabilityAvailable":true,"Temperature":65535,"WearUsed":255,
              "PowerOnHours":-1,"ReadErrors":"unavailable"}]
            """));
        Assert.Equal(DiskHealthState.Unknown, disk.State);
        Assert.Null(disk.Temperature);
        Assert.Null(disk.WearUsed);
        Assert.Null(disk.PowerOnHours);
        Assert.Null(disk.ReadErrors);
    }

    [Fact]
    public void Empty_device_list_is_valid_but_broken_provider_output_is_an_error()
    {
        Assert.Empty(DiskHealthReader.Parse("[]"));
        Assert.Throws<JsonException>(() => DiskHealthReader.Parse("{}"));
        Assert.Throws<JsonException>(() => DiskHealthReader.Parse("[null]"));
        Assert.ThrowsAny<JsonException>(() => DiskHealthReader.Parse("not json"));
    }

    [Fact]
    public async Task Provider_process_timeout_is_bounded()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows process test");
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => DiskHealthReader.RunQueryAsync(
            Start("Start-Sleep -Seconds 30"), TimeSpan.FromMilliseconds(500), CancellationToken.None));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task Provider_failure_is_reported_instead_of_an_empty_healthy_result()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows process test");
        var error = await Assert.ThrowsAsync<IOException>(() => DiskHealthReader.RunQueryAsync(
            new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                ArgumentList = { "/d", "/c", "echo Provider unavailable 1>&2 & exit /b 1" }
            }, TimeSpan.FromSeconds(15), CancellationToken.None));
        Assert.Contains("Provider unavailable", error.Message);
    }

    [Fact]
    public async Task Cancelled_query_never_starts_a_process()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DiskHealthReader.RunQueryAsync(
            new ProcessStartInfo("missing-executable"), TimeSpan.FromSeconds(1), new CancellationToken(true)));
    }

    private static ProcessStartInfo Start(string script)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand",
                     Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(arg);
        return start;
    }
}
