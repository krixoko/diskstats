using DiskStats.App.Localization;

namespace DiskStats.App;

public partial class MainWindow
{
    private bool _benchmarkOpen;
    private int _duplicateRuns;

    internal async Task ShowBenchmarkAsync(Func<Task>? show = null)
    {
        if (_benchmarkOpen || _scanRunning || _cleanupRunning || _refreshRunning || _duplicateRuns > 0 || !_snapshotWork.IsCompleted)
        {
            StatusText.Text = Loc.T("Bench_Busy");
            return;
        }
        _benchmarkOpen = true;
        try { await (show?.Invoke() ?? new BenchmarkWindow(_scanRoot).ShowDialog(this)); }
        finally
        {
            _benchmarkOpen = false;
            DrainChanges();
        }
    }
}
