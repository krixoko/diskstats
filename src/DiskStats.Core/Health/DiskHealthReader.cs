using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DiskStats.Core.Health;

public enum DiskHealthState { Unknown, Healthy, Warning, Unhealthy }

public sealed record DiskHealthInfo(
    string Id, string Number, string Model, ulong? CapacityBytes, int? BusType,
    DiskHealthState State, bool ReliabilityAvailable, string? ReliabilityError,
    ulong? Temperature, ulong? WearUsed, ulong? PowerOnHours,
    ulong? ReadErrors, ulong? WriteErrors, ulong? UncorrectedReadErrors, ulong? UncorrectedWriteErrors)
{
    public bool NvmeSmartAvailable { get; init; }
    public byte? NvmeCriticalWarnings { get; init; }
    public ulong? MediaErrors { get; init; }
    public ulong? UnsafeShutdowns { get; init; }
}

/// <summary>Read-only Windows Storage and NVMe SMART log queries. No self-tests or disk writes.</summary>
public static class DiskHealthReader
{
    // Fixed script: no user input or device names are interpolated into PowerShell code.
    internal const string QueryScript = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
        try {
            $disks = @(Get-CimInstance -Namespace 'root/Microsoft/Windows/Storage' -ClassName MSFT_PhysicalDisk)
            $nativeReady = $false
            if (@($disks | Where-Object { $_.CimInstanceProperties['BusType'].Value -eq 17 }).Count -gt 0) {
                try {
                    Add-Type -TypeDefinition @'
        using System;
        using System.Text;
        using System.Runtime.InteropServices;
        using Microsoft.Win32.SafeHandles;
        public static class DiskStatsNvmeProbe {
            [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
            static extern SafeFileHandle CreateFileW(string p, uint a, uint s, IntPtr sec, uint mode, uint flags, IntPtr template);
            [DllImport("kernel32.dll", SetLastError=true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] input, uint inputSize,
                byte[] output, uint outputSize, out uint returned, IntPtr overlapped);
            static uint U32(byte[] b, int offset) { return BitConverter.ToUInt32(b, offset); }
            static void Put(byte[] b, int offset, uint value) { Array.Copy(BitConverter.GetBytes(value), 0, b, offset, 4); }
            public static byte[] Read(int number, string expectedSerial) {
                if (number < 0 || String.IsNullOrWhiteSpace(expectedSerial)) return null;
                using (var h = CreateFileW(@"\\.\PhysicalDrive" + number, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero)) {
                    if (h.IsInvalid) return null;
                    // Verify bus and serial before associating SMART data with a CIM device.
                    byte[] query = new byte[12], descriptor = new byte[4096];
                    uint returned;
                    if (!DeviceIoControl(h, 0x002D1400, query, 12, descriptor, 4096, out returned, IntPtr.Zero)
                        || returned < 36 || returned > descriptor.Length || U32(descriptor, 28) != 17) return null;
                    uint serialOffset = U32(descriptor, 24);
                    if (serialOffset < 36 || serialOffset >= returned) return null;
                    int end = (int)serialOffset;
                    while (end < returned && descriptor[end] != 0) end++;
                    string serial = Encoding.ASCII.GetString(descriptor, (int)serialOffset, end - (int)serialOffset).Trim();
                    if (!String.Equals(serial, expectedSerial.Trim(), StringComparison.OrdinalIgnoreCase)) return null;
                    // STORAGE_PROPERTY_QUERY + STORAGE_PROTOCOL_SPECIFIC_DATA, SMART log page 02h.
                    byte[] b = new byte[560];
                    Put(b, 0, 50); Put(b, 8, 3); Put(b, 12, 2); Put(b, 16, 2);
                    Put(b, 24, 40); Put(b, 28, 512);
                    if (!DeviceIoControl(h, 0x002D1400, b, 560, b, 560, out returned, IntPtr.Zero)
                        || returned < 48 || returned > b.Length || U32(b, 8) != 3 || U32(b, 12) != 2) return null;
                    uint offset = U32(b, 24), length = U32(b, 28);
                    if (offset < 40 || offset > 512 || length < 512 || (ulong)offset + 8 + 512 > returned) return null;
                    byte[] log = new byte[512]; Array.Copy(b, (int)offset + 8, log, 0, 512);
                    return log;
                }
            }
        }
        '@
                    $nativeReady = $true
                } catch { $nativeReady = $false }
            }
            $result = @(
                foreach ($disk in $disks) {
                    $counter = $null
                    $counterError = $null
                    try { $counter = Get-StorageReliabilityCounter -PhysicalDisk $disk -ErrorAction Stop }
                    catch { $counterError = $_.Exception.Message }
                    $smart = $null
                    if ($nativeReady -and $disk.CimInstanceProperties['BusType'].Value -eq 17) {
                        try { $smart = [DiskStatsNvmeProbe]::Read([int]$disk.DeviceId, [string]$disk.SerialNumber) }
                        catch { $smart = $null }
                    }
                    [pscustomobject]@{
                        Id = [string]$disk.ObjectId
                        Number = [string]$disk.DeviceId
                        Model = [string]$disk.FriendlyName
                        CapacityBytes = $disk.Size
                        # Storage module type adapters expose display strings after autoload.
                        BusType = $disk.CimInstanceProperties['BusType'].Value
                        HealthStatus = $disk.CimInstanceProperties['HealthStatus'].Value
                        ReliabilityAvailable = ($null -ne $counter)
                        ReliabilityError = $counterError
                        Temperature = $counter.Temperature
                        WearUsed = $counter.Wear
                        PowerOnHours = $counter.PowerOnHours
                        ReadErrors = $counter.ReadErrorsTotal
                        WriteErrors = $counter.WriteErrorsTotal
                        UncorrectedReadErrors = $counter.ReadErrorsUncorrected
                        UncorrectedWriteErrors = $counter.WriteErrorsUncorrected
                        NvmeSmartLog = $(if ($null -ne $smart) { [Convert]::ToBase64String($smart) } else { $null })
                    }
                }
            )
            ConvertTo-Json -InputObject $result -Depth 4 -Compress
        }
        catch {
            [Console]::Error.WriteLine($_.Exception.Message)
            exit 1
        }
        """;

    public static async Task<IReadOnlyList<DiskHealthInfo>> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows is required.");
        string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
                     Convert.ToBase64String(Encoding.Unicode.GetBytes(QueryScript)) }) start.ArgumentList.Add(arg);
        return Parse(await RunQueryAsync(start, TimeSpan.FromSeconds(25), cancellationToken).ConfigureAwait(false));
    }

    internal static async Task<string> RunQueryAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(timeout);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("The Windows storage query could not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            string json = await output.ConfigureAwait(false);
            string message = await error.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new IOException(string.IsNullOrWhiteSpace(message) ? "The Windows storage query failed." : message.Trim());
            return json;
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            // Observe both pipe readers, including cancellation during a blocked provider call.
            try { await Task.WhenAll(output, error).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            if (cancel.IsCancellationRequested) throw;
            throw new TimeoutException("The Windows storage provider did not respond within the time limit.");
        }
    }

    internal static IReadOnlyList<DiskHealthInfo> Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("Expected a list of physical disks.");
        var result = new List<DiskHealthInfo>();
        foreach (JsonElement disk in document.RootElement.EnumerateArray())
        {
            if (disk.ValueKind != JsonValueKind.Object) throw new JsonException("Invalid physical disk entry.");
            string number = Text(disk, "Number") ?? "?";
            bool reliability = disk.TryGetProperty("ReliabilityAvailable", out var flag) && flag.ValueKind == JsonValueKind.True;
            ulong? Counter(string name) => reliability ? Number(disk, name) : null;
            ulong? temperature = Counter("Temperature");
            ulong? wear = Counter("WearUsed");
            var info = new DiskHealthInfo(Text(disk, "Id") ?? number, number, Text(disk, "Model") ?? "—",
                Number(disk, "CapacityBytes"), Number(disk, "BusType") is { } bus && bus <= int.MaxValue ? (int)bus : null,
                Number(disk, "HealthStatus") switch
                {
                    0 => DiskHealthState.Healthy,
                    1 => DiskHealthState.Warning,
                    2 => DiskHealthState.Unhealthy,
                    _ => DiskHealthState.Unknown
                }, reliability, Text(disk, "ReliabilityError"),
                temperature is > 0 and <= 200 ? temperature : null,
                wear is <= 100 ? wear : null, Counter("PowerOnHours"),
                Counter("ReadErrors"), Counter("WriteErrors"),
                Counter("UncorrectedReadErrors"), Counter("UncorrectedWriteErrors"));
            if (Text(disk, "NvmeSmartLog") is { } encoded)
            {
                try { info = WithNvmeLog(info, Convert.FromBase64String(encoded)); }
                catch (FormatException) { /* Malformed optional SMART data must not discard the Windows status. */ }
            }
            result.Add(info);
        }
        return result;
    }

    internal static DiskHealthInfo WithNvmeLog(DiskHealthInfo disk, ReadOnlySpan<byte> log)
    {
        if (log.Length != 512 || log.IndexOfAnyExcept((byte)0) < 0 || log.IndexOfAnyExcept((byte)255) < 0) return disk;
        int kelvin = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(log[1..]);
        ulong? hours = NvmeCounter(log, 128);
        return disk with
        {
            NvmeSmartAvailable = true, NvmeCriticalWarnings = log[0],
            Temperature = kelvin is >= 273 and <= 473 ? (ulong)(kelvin - 273) : disk.Temperature,
            WearUsed = log[5], PowerOnHours = hours ?? disk.PowerOnHours,
            MediaErrors = NvmeCounter(log, 160), UnsafeShutdowns = NvmeCounter(log, 144)
        };
    }

    private static ulong? NvmeCounter(ReadOnlySpan<byte> log, int offset)
        => log.Slice(offset + 8, 8).IndexOfAnyExcept((byte)0) >= 0 ? null
            : System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(log[offset..]);

    private static string? Text(JsonElement value, string name)
        => value.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString())
            ? p.GetString() : null;

    private static ulong? Number(JsonElement value, string name)
        => value.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetUInt64(out ulong number)
            && number != ulong.MaxValue && number != uint.MaxValue ? number : null;
}
