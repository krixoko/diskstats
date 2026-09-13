using System.Text.Json;
using System.Text.Json.Serialization;

using DiskStats.App.Localization;

namespace DiskStats.App;

public enum ThemeChoice { System, Light, Dark }

/// <summary>Wie fein die Karte aufgeloest wird — betrifft Zeichenaufwand und Lesbarkeit.</summary>
public enum DetailLevel { Coarse, Normal, Fine }

/// <summary>Basis der Groessenangaben: 1 GB als 1024 MB oder als 1000 MB.</summary>
public enum SizeBase { Binary, Decimal }

/// <summary>
/// Einstellungen, die etwas bewirken. Bewusst wenige: Jede hier verstellt echtes Verhalten,
/// keine ist Zierde. Was sich aus dem Zusammenhang ergibt — etwa die Worker-Zahl auf einer
/// SSD — bleibt automatisch und muss niemanden beschaeftigen.
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// Die Sprache der Oberflaeche. Wirkt beim naechsten Start, weil ein Teil der Texte
    /// beim Laden der Fenster aufgeloest wird.
    /// </summary>
    public Language Language { get; set; } = Language.English;

    /// <summary>
    /// Ob beim Start nach erhoehten Rechten gefragt wird. Der Haken im Dialog schaltet das ab.
    /// </summary>
    public bool ElevationPrompt { get; set; } = true;

    /// <summary>
    /// Was gilt, wenn nicht mehr gefragt wird. Nur zusammen mit <see cref="ElevationPrompt"/>
    /// auf false aussagekraeftig — dann startet sich die Anwendung ungefragt erhoeht neu.
    /// </summary>
    public bool AutoElevate { get; set; }

    public ThemeChoice Theme { get; set; } = ThemeChoice.Light;
    public bool Animations { get; set; } = true;
    public DetailLevel Detail { get; set; } = DetailLevel.Normal;

    /// <summary>0 bedeutet automatisch: Kernzahl auf Flash, gedrosselt auf drehenden Platten.</summary>
    public int WorkerCount { get; set; }
    public DiskStats.Core.Scanning.ScanMethod ScanMethod { get; set; }
    public string[] ExcludedPaths { get; set; } = [];
    public SavedFilter[] SavedFilters { get; set; } = [];

    public SizeBase Sizes { get; set; } = SizeBase.Binary;

    /// <summary>Zuletzt gescannter Pfad — als Vorschlag beim naechsten Start.</summary>
    public string LastPath { get; set; } = string.Empty;

    /// <summary>Fenstergeometrie. Breite 0 bedeutet: noch nie gemerkt.</summary>
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public double WindowX { get; set; }
    public double WindowY { get; set; }
    public bool WindowMaximized { get; set; }

    /// <summary>Die geltenden Einstellungen. Ein Prozess zeigt ein Fenster, deshalb genuegt eine Instanz.</summary>
    public static AppSettings Current { get; internal set; } = new();

    /// <summary>Wird nach jeder Aenderung ausgeloest, damit die Oberflaeche nachziehen kann.</summary>
    public static event Action? Changed;

    /// <summary>Kachelflaechen unterhalb dieses Faktors der Grundgroesse entfallen.</summary>
    public double DetailFactor => Detail switch
    {
        DetailLevel.Coarse => 2.6,
        DetailLevel.Fine => 0.45,
        _ => 1.0,
    };

    /// <summary>
    /// Wo die Einstellungen liegen. Umlenkbar wie bei <c>DiagnosticLog</c> und
    /// <c>SnapshotStore</c> — ein Test darf die Einstellungen des Benutzers nicht anfassen.
    /// </summary>
    public static string Folder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiskStats");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;

            AppSettings? loaded = JsonSerializer.Deserialize(
                File.ReadAllText(FilePath), SettingsContext.Default.AppSettings);

            Current = loaded ?? new AppSettings();
        }
        catch (IOException) { /* gesperrte Datei: Standardwerte genuegen fuer diese Sitzung */ }
        catch (UnauthorizedAccessException) { }
        catch (JsonException)
        {
            // Beschaedigt — etwa ein Absturz mitten im Schreiben. Beiseitelegen statt beim
            // naechsten Speichern still zu ueberschreiben: So bleibt nachvollziehbar, was war.
            Current = new AppSettings();
            try { File.Move(FilePath, FilePath + ".broken", overwrite: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Uebernimmt eine Aenderung, schreibt sie fort und benachrichtigt die Oberflaeche.</summary>
    public static void Apply(Action<AppSettings> change)
    {
        change(Current);
        Save();
        Changed?.Invoke();
    }

    private static void Save()
    {
        try
        {
            string? directory = Path.GetDirectoryName(FilePath);
            if (directory is not null) Directory.CreateDirectory(directory);

            // Erst daneben schreiben, dann umbenennen — wie beim Snapshot. Ein Abbruch mitten
            // im Schreiben hinterlaesst sonst eine halbe Datei, die beim Start nicht mehr liest.
            string temporary = FilePath + ".tmp";
            File.WriteAllText(temporary,
                JsonSerializer.Serialize(Current, SettingsContext.Default.AppSettings));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (IOException) { /* Einstellungen gehen verloren, die Sitzung laeuft weiter */ }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Quellcode-erzeugte Serialisierung — vermeidet Reflexion beim Start.</summary>
// Namen statt Zahlen: Die Datei liegt im Benutzerprofil und soll lesbar bleiben.
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsContext : JsonSerializerContext;
