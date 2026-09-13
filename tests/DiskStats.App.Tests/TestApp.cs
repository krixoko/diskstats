using Avalonia;
using Avalonia.Headless;
using DiskStats.App;
using DiskStats.App.Tests;

[assembly: AvaloniaTestApplication(typeof(TestApp))]
// Tests share process-wide application settings and the Avalonia application.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace DiskStats.App.Tests;

/// <summary>
/// Die Anwendung fuer den kopflosen Testlauf.
///
/// Kopflos heisst: Avalonia baut den vollstaendigen Steuerelementbaum auf, wendet Stile an,
/// misst und ordnet an — nur gezeichnet wird auf keinen Bildschirm. Genau die Schicht also,
/// in der die Fehler dieses Projekts sassen: ein Klickpfad, der ins Leere greift, ein
/// Schildchen, das ueberschrieben wird.
/// </summary>
public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<global::DiskStats.App.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .WithInterFont();
}
