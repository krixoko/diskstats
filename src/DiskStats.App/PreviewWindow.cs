using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DiskStats.App.Localization;

namespace DiskStats.App;

public enum PreviewKind { Image, Text, Video, Unsupported }

public sealed class PreviewWindow : Window
{
    private readonly CancellationTokenSource _cancel = new();
    private readonly ContentControl _body = new();
    private Bitmap? _bitmap;
    private readonly string _path;
    internal const int TextLimit = 128 * 1024;

    public static PreviewKind KindOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch {
        ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp" => PreviewKind.Image,
        ".mp4" or ".m4v" or ".avi" or ".wmv" or ".mov" or ".mkv" => PreviewKind.Video,
        ".txt" or ".md" or ".log" or ".json" or ".jsonl" or ".xml" or ".csv" or ".yaml" or ".yml"
            or ".cs" or ".js" or ".ts" or ".py" or ".css" or ".html" or ".ini" or ".ps1" or ".sql" => PreviewKind.Text,
        _ => PreviewKind.Unsupported
    };

    public PreviewWindow(string path)
    {
        _path = path;
        Title = Path.GetFileName(path);
        Width = 840; Height = 600; MinWidth = 420; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.Bind(BackgroundProperty, this.GetResourceObservable("Page"));
        var layout = new DockPanel { Margin = new Thickness(18), LastChildFill = true };
        var close = new Button { Content = Loc.T("Common_Close"), IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        close.Classes.Add("quiet");
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Bottom); layout.Children.Add(close);
        var header = new TextBlock { Text = path, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 12) };
        ToolTip.SetTip(header, path);
        DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
        layout.Children.Add(_body); Content = layout;
        KeyDown += (_, e) => {
            if (e.Key is Key.Escape or Key.Space) { Close(); e.Handled = true; }
        };
        Opened += async (_, _) => await LoadAsync();
        Closed += (_, _) => { _cancel.Cancel(); _bitmap?.Dispose(); };
    }

    internal static async Task<(string Text, bool Truncated)> ReadTextAsync(string path, CancellationToken cancel)
    {
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous));
        var buffer = new char[TextLimit + 1];
        int count = await reader.ReadBlockAsync(buffer.AsMemory(), cancel);
        return (new string(buffer, 0, Math.Min(count, TextLimit)), count > TextLimit);
    }

    private async Task LoadAsync()
    {
        _body.Content = new TextBlock { Text = Loc.T("Preview_Loading") };
        try
        {
            PreviewKind kind = KindOf(_path);
            if (kind == PreviewKind.Text)
            {
                var result = await ReadTextAsync(_path, _cancel.Token);
                if (_cancel.IsCancellationRequested) return;
                var panel = new DockPanel();
                if (result.Truncated)
                {
                    var note = new TextBlock { Text = Loc.T("Preview_Truncated"), Margin = new Thickness(0, 0, 0, 8) };
                    DockPanel.SetDock(note, Dock.Top); panel.Children.Add(note);
                }
                panel.Children.Add(new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    Content = new SelectableTextBlock { Text = result.Text, FontFamily = new FontFamily("Consolas"), FontSize = 13 } });
                _body.Content = panel;
            }
            else if (kind == PreviewKind.Image)
            {
                Bitmap bitmap = await Task.Run(() => {
                    using var file = File.OpenRead(_path);
                    return Bitmap.DecodeToWidth(file, 1600);
                });
                if (_cancel.IsCancellationRequested) { bitmap.Dispose(); return; }
                _bitmap = bitmap;
                _body.Content = new Image { Source = bitmap, Stretch = Stretch.Uniform };
            }
            else if (kind == PreviewKind.Video)
            {
                var panel = new DockPanel();
                var status = new TextBlock { Text = Loc.T("Preview_Codecs"), TextWrapping = TextWrapping.Wrap };
                DockPanel.SetDock(status, Dock.Bottom); panel.Children.Add(status);
                var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 8) };
                var video = new Controls.WindowsVideoHost(_path);
                var play = new Button { Content = Loc.T("Preview_Play") };
                var pause = new Button { Content = Loc.T("Preview_Pause") };
                var restart = new Button { Content = Loc.T("Preview_Restart") };
                play.Click += (_, _) => video.Play(); pause.Click += (_, _) => video.Pause();
                restart.Click += (_, _) => video.Restart();
                controls.Children.Add(play); controls.Children.Add(pause); controls.Children.Add(restart);
                DockPanel.SetDock(controls, Dock.Bottom); panel.Children.Add(controls);
                video.Failed += error => { status.Text = Loc.T("Preview_Error", error); play.IsEnabled = pause.IsEnabled = restart.IsEnabled = false; };
                panel.Children.Add(video); _body.Content = panel;
            }
            else _body.Content = new TextBlock { Text = Loc.T("Preview_Unsupported"), TextWrapping = TextWrapping.Wrap };
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_cancel.IsCancellationRequested)
                _body.Content = new TextBlock { Text = Loc.T("Preview_Error", ex.Message), TextWrapping = TextWrapping.Wrap };
        }
    }
}
