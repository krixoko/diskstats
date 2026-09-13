using Avalonia;
using DiskStats.App.Localization;
using DiskStats.Core.Diagnostics;
using DiskStats.Core.Storage;
using System;
using System.Globalization;
using System.Threading.Tasks;

namespace DiskStats.App;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Faeden ausserhalb der Oberflaeche — der Walker laeuft auf vielen davon.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            DiagnosticLog.Write("Unbehandelter Fehler", e.ExceptionObject as Exception);

        // Fehler in vergessenen Tasks beenden den Prozess nicht, verschlucken aber die Ursache.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            DiagnosticLog.Write("Fehler in einer Hintergrundaufgabe", e.Exception);
            e.SetObserved();
        };

        // Zwei Instanzen wuerden sich Einstellungen und Snapshots gegenseitig ueberschreiben.
        if (!SingleInstance.Claim()) return;

        // Wo die Anwendung schreibt, haengt davon ab, ob sie paketiert laeuft — und das
        // entscheidet, ob ihre Daten die Deinstallation ueberleben.
        AppData.Apply();

        // Vor dem Aufbau der Oberflaeche: das Thema muss beim ersten Bild schon stimmen.
        AppSettings.Load();

        // Vor dem ersten Fenster: AXAML loest seine Texte beim Laden einmal auf.
        Loc.Current = AppSettings.Current.Language;

        // Zahlen und Datumsangaben folgen der Sprache, nicht dem System: "147,3 GB" in einer
        // englischen Oberflaeche liest sich wie ein Fehler, und umgekehrt genauso. Muss vor
        // dem ersten Faden gesetzt werden, sonst erben ihn die Scan-Arbeiter nicht.
        var culture = new CultureInfo(Loc.Current == Language.German ? "de-DE" : "en-US");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        // Aufraeumen beim Start statt im Hintergrund: einmal je Sitzung genuegt, und
        // ein Werkzeug, das Platz zeigen soll, darf selbst keinen verschwenden.
        DiagnosticLog.Prune(DateTime.Now);
        SnapshotStore.Prune(DateTime.Now);

        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        finally { SingleInstance.Release(); }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
