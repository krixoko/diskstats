using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Avalonia.Controls;
using DiskStats.App.Localization;
using DiskStats.Core.Health;

namespace DiskStats.App;

public partial class HealthWindow : Window
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<DiskHealthInfo>>> _reader;
    private CancellationTokenSource? _request;
    private bool _closed;

    public HealthWindow() : this(DiskHealthReader.ReadAsync) { }

    internal HealthWindow(Func<CancellationToken, Task<IReadOnlyList<DiskHealthInfo>>> reader, bool loadOnOpen = true)
    {
        InitializeComponent();
        _reader = reader;
        DiskPicker.SelectionChanged += (_, _) => ShowDisk();
        RefreshButton.Click += async (_, _) => await RefreshAsync();
        CloseButton.Click += (_, _) => Close();
        if (loadOnOpen) Opened += async (_, _) => await RefreshAsync();
        Closed += (_, _) => { _closed = true; _request?.Cancel(); };
        ShowDisk();
    }

    internal async Task RefreshAsync()
    {
        if (_closed || _request is not null) return;
        using var request = new CancellationTokenSource();
        _request = request;
        string? selectedId = (DiskPicker.SelectedItem as DiskChoice)?.Disk.Id;
        DiskPicker.ItemsSource = null;
        ShowDisk(); // Never leave old health values visible after a failed refresh.
        DiskPicker.IsEnabled = RefreshButton.IsEnabled = false;
        Loading.IsVisible = true;
        Status.Text = Loc.T("Health_Loading");
        try
        {
            IReadOnlyList<DiskHealthInfo> disks = await _reader(request.Token);
            if (_closed || request.IsCancellationRequested) return;
            DiskChoice[] choices = [.. disks.Select(d => new DiskChoice(d))];
            DiskPicker.ItemsSource = choices;
            DiskPicker.SelectedItem = choices.FirstOrDefault(d => d.Disk.Id == selectedId) ?? choices.FirstOrDefault();
            ShowDisk();
            Status.Text = choices.Length == 0 ? Loc.T("Health_NoDisks")
                : Loc.T("Health_Updated", DateTime.Now.ToString("T", CultureInfo.CurrentCulture));
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or TimeoutException
                                      or NotSupportedException or JsonException or InvalidOperationException)
        {
            if (!_closed) Status.Text = Loc.T("Health_Error", ex.Message);
        }
        finally
        {
            _request = null;
            if (!_closed)
            {
                Loading.IsVisible = false;
                RefreshButton.IsEnabled = true;
                DiskPicker.IsEnabled = DiskPicker.ItemCount > 0;
            }
        }
    }

    private void ShowDisk()
    {
        string missing = Loc.T("Health_Unavailable");
        DiskHealthInfo? disk = (DiskPicker.SelectedItem as DiskChoice)?.Disk;
        HealthStatus.Text = disk is null ? "—" : Loc.T("Health_" + disk.State);
        DeviceInfo.Text = disk is null ? "" : Loc.T("Health_Device", disk.Number,
            Bus(disk.BusType), disk.CapacityBytes is { } size && size <= long.MaxValue ? Sizes.Format((long)size) : missing);
        Temperature.Text = Value(disk?.Temperature, " °C");
        Wear.Text = Value(disk?.WearUsed, " %");
        if (disk is { NvmeSmartAvailable: true, WearUsed: 255 }) Wear.Text = "≥ 255 %";
        PowerOnHours.Text = Value(disk?.PowerOnHours, " h");
        ReadErrors.Text = Value(disk?.ReadErrors);
        WriteErrors.Text = Value(disk?.WriteErrors);
        UncorrectedRead.Text = Value(disk?.UncorrectedReadErrors);
        UncorrectedWrite.Text = Value(disk?.UncorrectedWriteErrors);
        NvmeCounters.IsVisible = NvmeStatus.IsVisible = disk?.NvmeSmartAvailable == true;
        NvmeStatus.Text = NvmeWarnings(disk?.NvmeCriticalWarnings);
        MediaErrors.Text = Value(disk?.MediaErrors);
        UnsafeShutdowns.Text = Value(disk?.UnsafeShutdowns);
        Availability.Text = disk is null ? "" : Loc.T(disk.NvmeSmartAvailable ? "Health_NvmeReported"
            : disk.ReliabilityAvailable ? "Health_Reported" : "Health_Limited");
        ToolTip.SetTip(Availability, disk?.ReliabilityError);
    }

    private static string NvmeWarnings(byte? warnings)
    {
        if (warnings is null) return Loc.T("Health_Unavailable");
        if (warnings == 0) return Loc.T("Health_NvmeClear");
        string[] keys = ["Health_SpareLow", "Health_TempWarning", "Health_ReliabilityWarning",
            "Health_ReadOnly", "Health_BackupWarning", "Health_PmrWarning"];
        var reasons = keys.Where((_, bit) => (warnings.Value & (1 << bit)) != 0).Select(Loc.T).ToList();
        if ((warnings.Value & 0xC0) != 0) reasons.Add(Loc.T("Health_Unknown"));
        return Loc.T("Health_NvmeWarning", string.Join(", ", reasons), warnings.Value.ToString("X2"));
    }

    private static string Value(ulong? value, string suffix = "")
        => value is { } number ? number.ToString("N0", CultureInfo.CurrentCulture) + suffix : Loc.T("Health_Unavailable");

    private static string Bus(int? bus) => bus switch
    {
        1 => "SCSI", 3 => "ATA", 6 => Loc.T("Health_FibreChannel"), 7 => "USB", 8 => "RAID", 9 => "iSCSI",
        10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC", 14 => Loc.T("Health_Virtual"), 15 => Loc.T("Health_FileVirtual"),
        16 => "Storage Spaces", 17 => "NVMe", 18 => "SCM", 19 => "UFS", _ => Loc.T("Health_Unknown")
    };

    private sealed record DiskChoice(DiskHealthInfo Disk)
    {
        public override string ToString() => $"{Disk.Model} ({Loc.T("Health_DiskNumber", Disk.Number)})";
    }
}
