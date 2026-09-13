using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;

namespace DiskStats.App.Tests;

public sealed class PreviewTests
{
    [Theory]
    [InlineData("photo.JPG", PreviewKind.Image)]
    [InlineData("clip.mp4", PreviewKind.Video)]
    [InlineData("notes.md", PreviewKind.Text)]
    [InlineData("script.html", PreviewKind.Text)]
    [InlineData("app.exe", PreviewKind.Unsupported)]
    public void Classifies_without_executing_files(string name, PreviewKind kind)
        => Assert.Equal(kind, PreviewWindow.KindOf(name));

    [Fact]
    public async Task Text_preview_is_bounded_and_preserves_unicode()
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "Grüße 👋\n" + new string('x', PreviewWindow.TextLimit + 100), TestContext.Current.CancellationToken);
            var result = await PreviewWindow.ReadTextAsync(path, TestContext.Current.CancellationToken);
            Assert.True(result.Truncated);
            Assert.Equal(PreviewWindow.TextLimit, result.Text.Length);
            Assert.StartsWith("Grüße 👋", result.Text);
        }
        finally { File.Delete(path); }
    }

    [AvaloniaFact]
    public void Unsupported_file_gets_an_explicit_message_and_close_action()
    {
        var window = new PreviewWindow("file.exe");
        window.Show();
        try
        {
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Localization.Loc.T("Preview_Unsupported"));
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => b.IsCancel);
        }
        finally { window.Close(); }
    }
}
