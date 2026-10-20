using DiskStats.App.Localization;

namespace DiskStats.App;

public partial class MainWindow
{
    private int _duplicateRuns;

    internal async Task ShowBenchmarkAsync(Func<Task>? show = null)
    {
        if (!_ops.CanChangeFiles || _duplicateRuns > 0 || !_snapshotWork.IsCompleted)
        {
            StatusText.Text = Loc.T("Bench_Busy");
            return;
        }
        _ops.BenchmarkOpen = true;
        try { await (show?.Invoke() ?? new BenchmarkWindow(_scanRoot).ShowDialog(this)); }
        finally
        {
            _ops.BenchmarkOpen = false;
            DrainChanges();
        }
    }
}
