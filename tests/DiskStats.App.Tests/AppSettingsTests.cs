using Avalonia.Headless.XUnit;

namespace DiskStats.App.Tests;

/// <summary>
/// Die Einstellungsdatei liegt im Profil und wird bei jedem Schieberegler-Schritt geschrieben.
/// Ein Absturz mitten im Schreiben darf weder die Datei zerstoeren noch beim naechsten Start
/// stillschweigend alles auf Standard zuruecksetzen und die Truemmer liegen lassen.
/// </summary>
public sealed class AppSettingsTests : IDisposable
{
    private readonly string _previous = AppSettings.Folder;
    private readonly AppSettings _previousCurrent = AppSettings.Current;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "diskstats-settings-" + Guid.NewGuid().ToString("N"));

    public AppSettingsTests()
    {
        Directory.CreateDirectory(_folder);
        AppSettings.Folder = _folder;
    }

    public void Dispose()
    {
        AppSettings.Folder = _previous;
        AppSettings.Current = _previousCurrent;
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    [AvaloniaFact]
    public void Schreibt_erst_daneben_und_laesst_keine_halbe_datei_zurueck()
    {
        AppSettings.Apply(s => s.LastPath = @"D:\Filme");

        string[] files = Directory.GetFiles(_folder);
        Assert.Single(files);
        Assert.Equal("settings.json", Path.GetFileName(files[0]));
        Assert.Contains(@"D:\\Filme", File.ReadAllText(files[0]));
    }

    [Fact]
    public void Beschaedigte_datei_wird_beiseite_gelegt_statt_still_ueberschrieben()
    {
        string file = Path.Combine(_folder, "settings.json");
        File.WriteAllText(file, "{ \"LastPath\": \"D:\\\\Filme\", \"Detai");   // abgerissen

        AppSettings.Load();

        Assert.Equal(string.Empty, AppSettings.Current.LastPath);
        Assert.True(File.Exists(file + ".broken"), "die Truemmer sollen zum Nachsehen bleiben");
        Assert.False(File.Exists(file));
    }
}
