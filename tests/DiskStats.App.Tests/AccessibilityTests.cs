using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using DiskStats.App.Localization;
using DiskStats.App.Rendering;

namespace DiskStats.App.Tests;

/// <summary>
/// Ein Knopf ohne Namen heisst fuer ein Vorlesewerkzeug "Schaltflaeche" — bei uns waeren das
/// neun Ansichtsknoepfe, ein Zahnrad, ein Pfeil und ein Kreuz, alle gleich. Das faellt beim
/// Ansehen nie auf, weil man die Bildzeichen sieht.
/// </summary>
public class AccessibilityTests : IDisposable
{
    private readonly string _sandbox;
    private readonly string _settings;
    private readonly string _snapshots;
    private readonly string _logs;

    public AccessibilityTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "diskstats-a11y-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_sandbox);

        _settings = AppSettings.Folder;
        _snapshots = Core.Storage.SnapshotStore.Folder;
        _logs = Core.Diagnostics.DiagnosticLog.Folder;

        AppSettings.Folder = _sandbox;
        Core.Storage.SnapshotStore.Folder = Path.Combine(_sandbox, "snapshots");
        Core.Diagnostics.DiagnosticLog.Folder = Path.Combine(_sandbox, "logs");
        Loc.Current = Language.English;
    }

    public void Dispose()
    {
        AppSettings.Folder = _settings;
        Core.Storage.SnapshotStore.Folder = _snapshots;
        Core.Diagnostics.DiagnosticLog.Folder = _logs;

        try { Directory.Delete(_sandbox, recursive: true); } catch (IOException) { }
    }

    private static MainWindow Open()
    {
        var window = new MainWindow([]);
        window.Show();
        return window;
    }

    /// <summary>Ein Bedienelement ohne sichtbaren Text braucht einen Namen.</summary>
    private static void AssertNamed(Control control, string what)
    {
        string? name = AutomationProperties.GetName(control);

        Assert.False(string.IsNullOrWhiteSpace(name), $"{what} hat keinen Namen.");
    }

    [AvaloniaFact]
    public void Die_knoepfe_ohne_beschriftung_haben_namen()
    {
        MainWindow window = Open();

        AssertNamed(window.ThemeButton, "Der Themenknopf");
        AssertNamed(window.UpButton, "Der Zurueck-Knopf");
        AssertNamed(window.SettingsButton, "Das Zahnrad");
        AssertNamed(window.BenchmarkButton, "Der Tempotest");
        AssertNamed(window.HealthButton, "Der Laufwerkszustand");
        AssertNamed(window.SearchBox, "Das Suchfeld");
        AssertNamed(window.Chart, "Die Karte");
    }

    /// <summary>
    /// Neun Knoepfe, die sich nur durch ihr Bildzeichen unterscheiden. Ohne Namen waeren sie
    /// beim Vorlesen nicht auseinanderzuhalten.
    /// </summary>
    [AvaloniaFact]
    public void Jeder_ansichtsknopf_hat_seinen_eigenen_namen()
    {
        MainWindow window = Open();

        string[] names =
        [
            .. window.ViewSwitcher.Children.OfType<Button>()
                .Select(b => AutomationProperties.GetName(b) ?? string.Empty),
        ];

        Assert.Equal(Enum.GetValues<ViewKind>().Length, names.Length);
        Assert.DoesNotContain(string.Empty, names);
        Assert.Equal(names.Length, names.Distinct().Count());
    }

    [AvaloniaFact]
    public void Die_namen_folgen_der_sprache()
    {
        Loc.Current = Language.German;
        MainWindow window = Open();

        Assert.Equal(Loc.T("Set_Title"), AutomationProperties.GetName(window.SettingsButton));

        Loc.Current = Language.English;
    }

    /// <summary>
    /// Die Karte zeichnet alles selbst; es gibt kein Element je Kachel. Ihr Name ist deshalb
    /// das Einzige, woran ein Vorlesewerkzeug erkennt, worauf die Auswahl steht.
    /// </summary>
    [AvaloniaFact]
    public void Die_karte_sagt_ohne_scan_ihren_zustand_an()
    {
        MainWindow window = Open();

        Assert.Equal(Loc.T("A11y_MapEmpty"), AutomationProperties.GetName(window.Chart));
    }

    [AvaloniaFact]
    public void Die_karte_nimmt_den_fokus_an()
    {
        MainWindow window = Open();

        Assert.True(window.Chart.Focusable);
    }

    // ---------- Tastatur ----------

    private static void Press(MainWindow window, Key key, KeyModifiers modifiers = KeyModifiers.None)
        => window.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = modifiers,
        });

    [AvaloniaFact]
    public void Strg_f_springt_ins_suchfeld()
    {
        MainWindow window = Open();

        Press(window, Key.F, KeyModifiers.Control);

        Assert.True(window.SearchBox.IsFocused);
    }

    /// <summary>
    /// Ohne Baum darf keine Taste etwas ausloesen. Dieselbe Fehlerklasse wie beim
    /// Kontextmenue: ein Weg, der auf einen Knoten wirkt, den es nicht gibt.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(Key.Enter)]
    [InlineData(Key.Delete)]
    [InlineData(Key.Back)]
    [InlineData(Key.Escape)]
    [InlineData(Key.Left)]
    [InlineData(Key.Right)]
    [InlineData(Key.Up)]
    [InlineData(Key.Down)]
    [InlineData(Key.Home)]
    [InlineData(Key.F5)]
    public void Ohne_scan_beendet_keine_taste_etwas(Key key)
    {
        MainWindow window = Open();

        Press(window, key);

        Assert.True(window.IsVisible);
    }
}

/// <summary>
/// Farbe kodiert die Dateityp-Kategorie in allen Ansichten. Etwa acht Prozent der Maenner
/// sehen Rot und Gruen kaum auseinander; die Design-Spec verlangt deshalb eine Pruefung auf
/// Rot-Gruen-Schwaeche. Geprueft wird die haeufigste Form, die Deuteranopie: Die Farben werden
/// so umgerechnet, wie ein Betroffener sie sieht (Machado, Oliveira und Fernandes 2009, volle
/// Auspraegung), und dann paarweise im CIE-Lab-Raum verglichen (Delta E 1976).
///
/// Zur Schwelle: Delta E 20 ist fuer eine Pastellpalette nicht erreichbar. Unter Deuteranopie
/// bleiben nur Helligkeit und die Blau-Gelb-Achse; acht Toene zwischen L* 80 und 90 lassen
/// sich darauf hoechstens etwa 12 Einheiten auseinanderlegen, selbst wenn man jeden Ton frei
/// verschiebt. Die Palette vor dieser Pruefung kam auf 0,8 (Flieder gegen Puderblau im dunklen
/// Thema) — praktisch dieselbe Farbe. Nach dem Auseinanderziehen liegt jedes Paar bei
/// mindestens 8,9; geprueft wird gegen 8, was ungefaehr "auf einen Blick verschieden" heisst.
///
/// Neue Werte (Farbton, Saettigung, Helligkeit), vorher in Klammern:
///   Sonstiges (26, 0.12, 0.810)  vorher (36, 0.16, 0.855)
///   Video     (8, 0.44, 0.780)   vorher (8, 0.38, 0.825)
///   Bilder    (35, 0.50, 0.785)  vorher (40, 0.44, 0.825)
///   Audio     (278, 0.33, 0.875) vorher (280, 0.27, 0.855)
///   Dokumente (159, 0.31, 0.890) vorher (150, 0.25, 0.825)
///   Code      (222, 0.37, 0.780) vorher (212, 0.31, 0.835)
///   Archive   (323, 0.35, 0.785) vorher (330, 0.29, 0.865)
///   Programme (89, 0.32, 0.850)  vorher (96, 0.26, 0.815)
/// </summary>
public class PaletteAccessibilityTests
{
    /// <summary>Unter diesem Abstand gelten zwei Toene als verwechselbar.</summary>
    private const double MinDistance = 8;

    public static TheoryData<FileCategory, FileCategory, bool> CategoryPairs()
    {
        var data = new TheoryData<FileCategory, FileCategory, bool>();
        FileCategory[] all = Enum.GetValues<FileCategory>();

        for (int i = 0; i < all.Length; i++)
            for (int j = i + 1; j < all.Length; j++)
                foreach (bool dark in new[] { false, true })
                    data.Add(all[i], all[j], dark);

        return data;
    }

    [Theory]
    [MemberData(nameof(CategoryPairs))]
    public void Kategoriefarben_bleiben_bei_deuteranopie_unterscheidbar(FileCategory a, FileCategory b, bool dark)
    {
        double distance = DeltaE(
            Lab(Deuteranopia(Linear(Palette.ForCategory(a, dark)))),
            Lab(Deuteranopia(Linear(Palette.ForCategory(b, dark)))));

        Assert.True(distance >= MinDistance,
            $"{a} und {b} ({(dark ? "dunkel" : "hell")}) liegen unter Deuteranopie nur {distance:F1} auseinander.");
    }

    /// <summary>
    /// Was fuer Betroffene gilt, muss erst recht fuer alle gelten — sonst haette die Anpassung
    /// die Palette fuer die Mehrheit verschlechtert.
    /// </summary>
    [Theory]
    [MemberData(nameof(CategoryPairs))]
    public void Kategoriefarben_sind_auch_normal_unterscheidbar(FileCategory a, FileCategory b, bool dark)
    {
        double distance = DeltaE(
            Lab(Linear(Palette.ForCategory(a, dark))),
            Lab(Linear(Palette.ForCategory(b, dark))));

        Assert.True(distance >= MinDistance,
            $"{a} und {b} ({(dark ? "dunkel" : "hell")}) liegen nur {distance:F1} auseinander.");
    }

    // ---------- Farbrechnung ----------

    private static double[] Linear(Color color) =>
    [
        Channel(color.R), Channel(color.G), Channel(color.B),
    ];

    /// <summary>sRGB-Gammakurve rueckwaerts: von Bildpunktwerten zu linearem Licht.</summary>
    private static double Channel(byte value)
    {
        double c = value / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    /// <summary>Matrix nach Machado et al. 2009 fuer Deuteranopie mit Schweregrad 1,0 auf linearem RGB.</summary>
    private static double[] Deuteranopia(double[] rgb) =>
    [
        Math.Clamp(0.367322 * rgb[0] + 0.860646 * rgb[1] - 0.227968 * rgb[2], 0, 1),
        Math.Clamp(0.280085 * rgb[0] + 0.672501 * rgb[1] + 0.047413 * rgb[2], 0, 1),
        Math.Clamp(-0.011820 * rgb[0] + 0.042940 * rgb[1] + 0.968881 * rgb[2], 0, 1),
    ];

    /// <summary>Lineares sRGB nach CIE Lab, Weisspunkt D65.</summary>
    private static double[] Lab(double[] rgb)
    {
        double x = 0.4124 * rgb[0] + 0.3576 * rgb[1] + 0.1805 * rgb[2];
        double y = 0.2126 * rgb[0] + 0.7152 * rgb[1] + 0.0722 * rgb[2];
        double z = 0.0193 * rgb[0] + 0.1192 * rgb[1] + 0.9505 * rgb[2];

        double fx = F(x / 0.95047), fy = F(y / 1.0), fz = F(z / 1.08883);
        return [116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz)];

        static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : 7.787 * t + 16.0 / 116;
    }

    private static double DeltaE(double[] a, double[] b)
        => Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]));
}

/// <summary>
/// Das Angebot, sich mit Administratorrechten neu zu starten. Der Vorgang selbst laesst sich
/// nicht pruefen — er verlangt eine Rueckfrage von Windows —, wohl aber die Bedingungen, unter
/// denen ueberhaupt gefragt wird. Und die sind der heikle Teil: Wer nie erhoehen kann, soll
/// die Frage nie sehen.
/// </summary>
public class ElevationTests
{
    [Fact]
    public void Erhoeht_und_erhoehbar_schliessen_sich_aus()
    {
        // Wer schon erhoeht laeuft, bekommt kein Angebot mehr.
        Assert.False(Elevation.IsActive && Elevation.IsAvailable);
    }

    [Fact]
    public void Im_paket_wird_nie_gefragt()
    {
        if (Elevation.IsPackaged) Assert.False(Elevation.CanOffer);
        else Assert.Equal(Elevation.IsAvailable, Elevation.CanOffer);
    }

    /// <summary>
    /// Vorgabe: fragen, aber nichts voraussetzen. Ein Werkzeug, das sich beim ersten Start
    /// ungefragt Rechte holt, hat die Entscheidung an sich gerissen.
    /// </summary>
    [Fact]
    public void Wird_gefragt_und_nichts_vorausgesetzt()
    {
        var frisch = new AppSettings();

        Assert.True(frisch.ElevationPrompt);
        Assert.False(frisch.AutoElevate);
    }
}
