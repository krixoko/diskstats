using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using DiskStats.App.Localization;
using DiskStats.Core.Analysis;

namespace DiskStats.App;

public sealed class TrendsWindow : Window
{
    private readonly string _root, _folder;
    private readonly StackPanel _results = new() { Spacing = 12 };
    private CancellationTokenSource? _load;
    private readonly ComboBox _period = new() { ItemsSource = new[] {
        Loc.T("Trend_Week"), Loc.T("Trend_Month"), Loc.T("Trend_Quarter"), Loc.T("Trend_All") }, SelectedIndex = 1 };

    public TrendsWindow(string root, string folder)
    {
        _root = root; _folder = folder;
        Title = Loc.T("Trend_Title"); Width = 730; Height = 610; MinWidth = 500; MinHeight = 350;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.Bind(BackgroundProperty, this.GetResourceObservable("Page"));
        var layout = new DockPanel { Margin = new Thickness(20) };
        var header = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 14) };
        header.Children.Add(new TextBlock { Text = folder, TextTrimming = TextTrimming.CharacterEllipsis });
        header.Children.Add(_period);
        header.Children.Add(new TextBlock { Text = Loc.T("Trend_Retention"), FontSize = 11, TextWrapping = TextWrapping.Wrap });
        DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
        var close = new Button { Content = Loc.T("Common_Close"), IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close(null);
        DockPanel.SetDock(close, Dock.Bottom); layout.Children.Add(close);
        layout.Children.Add(new ScrollViewer { Content = _results }); Content = layout;
        Opened += async (_, _) => await RefreshAsync();
        _period.SelectionChanged += async (_, _) => await RefreshAsync();
        Closed += (_, _) => _load?.Cancel();
    }

    private async Task RefreshAsync()
    {
        _load?.Cancel();
        using var cancel = new CancellationTokenSource();
        _load = cancel;
        _results.Children.Clear();
        _results.Children.Add(new TextBlock { Text = Loc.T("Trend_Loading") });
        try
        {
            int days = new[] { 7, 30, 90, 0 }[Math.Max(0, _period.SelectedIndex)];
            StorageTrend.Report report = await Task.Run(() => StorageTrend.Load(_root, _folder, days, cancel.Token), cancel.Token);
            if (cancel.IsCancellationRequested) return;
            _results.Children.Clear();
            if (report.Points.Count < 2)
            {
                _results.Children.Add(new TextBlock { Text = Loc.T("Trend_Empty"), TextWrapping = TextWrapping.Wrap });
                return;
            }
            var first = report.Points[0]; var last = report.Points[^1];
            _results.Children.Add(new TextBlock { Text = $"{first.Written:g} — {last.Written:g}", FontSize = 12 });
            if (first.Bytes is { } before && last.Bytes is { } after)
                _results.Children.Add(new TextBlock { Text = Signed(after - before), FontSize = 26, FontWeight = FontWeight.Bold });
            var chart = new Canvas { Height = 150, ClipToBounds = true };
            void Draw()
            {
                chart.Children.Clear();
                double width = Math.Max(20, chart.Bounds.Width - 16);
                long max = Math.Max(1, report.Points.Max(p => p.Bytes ?? 0));
                double elapsed = Math.Max(1, (last.Written - first.Written).TotalSeconds);
                Point? previous = null;
                foreach (var sample in report.Points)
                {
                    if (sample.Bytes is not { } bytes) { previous = null; continue; }
                    var at = new Point(8 + (sample.Written - first.Written).TotalSeconds / elapsed * width,
                        138 - bytes / (double)max * 126);
                    if (previous is { } p) chart.Children.Add(new Line { StartPoint = p, EndPoint = at, Stroke = Brushes.SteelBlue, StrokeThickness = 3 });
                    var dot = new Ellipse { Width = 8, Height = 8, Fill = Brushes.SteelBlue };
                    Canvas.SetLeft(dot, at.X - 4); Canvas.SetTop(dot, at.Y - 4);
                    ToolTip.SetTip(dot, $"{sample.Written:g} · {Sizes.Format(bytes)}"); chart.Children.Add(dot);
                    previous = at;
                }
            }
            chart.SizeChanged += (_, _) => Draw();
            _results.Children.Add(chart);
            foreach (var sample in report.Points)
                _results.Children.Add(new TextBlock { Text = $"{sample.Written:g}   {(sample.Bytes is { } bytes ? Sizes.Format(bytes) : Loc.T("Trend_Missing"))}", FontSize = 12 });
            _results.Children.Add(new TextBlock { Text = Loc.T("Trend_Changes"), FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 10, 0, 0) });
            foreach (var growth in report.Growth.Take(15))
            {
                var button = new Button { Content = $"{Signed(growth.Delta)}   {System.IO.Path.GetFileName(growth.Path)}", HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left };
                button.Classes.Add("quiet"); ToolTip.SetTip(button, growth.Path);
                button.Click += (_, _) => Close(growth.Path);
                _results.Children.Add(button);
            }
            if (report.Growth.Count == 0) _results.Children.Add(new TextBlock { Text = Loc.T("Chg_Nothing") });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cancel.IsCancellationRequested) { _results.Children.Clear(); _results.Children.Add(new TextBlock { Text = ex.Message, TextWrapping = TextWrapping.Wrap }); }
        }
        finally { if (ReferenceEquals(_load, cancel)) _load = null; }
    }

    private static string Signed(long bytes) => (bytes >= 0 ? "+" : "−") + Sizes.Format(Math.Abs(bytes));
}
