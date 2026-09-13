using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DiskStats.Core.Diagnostics;

namespace DiskStats.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Ein Absturz soll eine Spur hinterlassen; ohne das steht hinterher nur "war weg".
        //
        // Zusaetzlich zum Haken an der AppDomain, und nicht statt seiner: Ein Fehler waehrend
        // eines Eingabeereignisses beendet den Prozess nicht immer — Avalonia faengt ihn ab,
        // das Fenster laeuft weiter, und ohne diesen Haken bliebe davon keine Spur. Genau so
        // gemessen: ein IndexOutOfRangeException aus dem Kontextmenue, Fenster danach am Leben.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
            DiagnosticLog.Write("Unbehandelter Fehler in der Oberfläche", e.Exception);

        RequestedThemeVariant = AppSettings.Current.Theme switch
        {
            ThemeChoice.Light => Avalonia.Styling.ThemeVariant.Light,
            ThemeChoice.Dark => Avalonia.Styling.ThemeVariant.Dark,
            _ => Avalonia.Styling.ThemeVariant.Default,
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}