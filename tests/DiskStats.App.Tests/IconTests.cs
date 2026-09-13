using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia;
using Avalonia.Media;
using DiskStats.App.Rendering;

namespace DiskStats.App.Tests;

/// <summary>
/// Die Bildzeichen kommen aus einem fremden Satz und werden beim Uebernehmen umgerechnet.
/// Beim Umrechnen kann etwas verrutschen, ohne dass irgendetwas fehlschlaegt — das X kam
/// dabei einmal als Schraegstrich heraus, weil ein Teilpfad ausserhalb des Felds landete.
/// Genau das pruefen diese Tests.
/// </summary>
public class IconTests
{
    public static TheoryData<string, string> AllIcons()
    {
        var data = new TheoryData<string, string>();

        foreach (FieldInfo field in typeof(Icons).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (field.FieldType == typeof(string) && field.GetValue(null) is string path)
                data.Add(field.Name, path);

        return data;
    }

    [Fact]
    public void Es_gibt_ueberhaupt_zeichen()
        => Assert.True(AllIcons().Count > 20, "Der Zeichensatz ist verdaechtig klein.");

    [AvaloniaTheory]
    [MemberData(nameof(AllIcons))]
    public void Jedes_zeichen_laesst_sich_zerlegen(string name, string data)
    {
        Geometry geometry = Geometry.Parse(data);

        Assert.False(geometry.Bounds.Width == 0 && geometry.Bounds.Height == 0,
            $"{name} ergibt eine leere Form.");
    }

    /// <summary>
    /// Alles muss auf dem Feld von 24x24 liegen.
    ///
    /// Was dieser Test nicht leistet: Avalonia berechnet die Ausdehnung aus Stuetz- und
    /// Endpunkten, nicht aus dem gezeichneten Bogen. Ein Zeichen, das erst mitten in einem
    /// Bogen ueber den Rand tritt, faellt hier nicht auf — nachgemessen am Mond, den Avalonia
    /// mit 9,5 statt 19 angibt. Der Test faengt also Verschiebungen ganzer Teilpfade, und
    /// genau die entstehen beim Zusammenfassen mehrerer Vorlagen-Elemente.
    ///
    /// Der Fehler, den dieser Test faengt: Lucide legt manche Zeichen als mehrere Pfade ab,
    /// spaetere davon mit relativem "m". Aneinandergehaengt beginnt so ein Teil nicht im
    /// Ursprung, sondern am Endpunkt des vorigen — beim X lief der zweite Strich dadurch bis
    /// (24|36) und war im Bild nicht mehr zu sehen. Die Form blieb dabei gueltig; nur ihre
    /// Ausdehnung verriet den Fehler.
    /// </summary>
    [AvaloniaTheory]
    [MemberData(nameof(AllIcons))]
    public void Jedes_zeichen_bleibt_auf_dem_feld(string name, string data)
    {
        Rect bounds = Geometry.Parse(data).Bounds;

        // Ein halber Strich Toleranz: Lucide setzt Punkte bewusst auf den Rand.
        const double slack = Icons.Stroke / 2;

        Assert.True(bounds.X >= -slack, $"{name} beginnt links ausserhalb: {bounds}");
        Assert.True(bounds.Y >= -slack, $"{name} beginnt oben ausserhalb: {bounds}");
        Assert.True(bounds.Right <= Icons.Grid + slack, $"{name} ragt rechts hinaus: {bounds}");
        Assert.True(bounds.Bottom <= Icons.Grid + slack, $"{name} ragt unten hinaus: {bounds}");
    }

    /// <summary>
    /// Der Waechter oben, an der Fassung von damals gemessen.
    ///
    /// Ohne diesen Test waere nicht belegt, dass die Pruefung den Fehler wirklich faengt, den
    /// sie fangen soll — eine Pruefung, die nie etwas ablehnt, sieht genauso aus wie eine, die
    /// nichts pruefen kann.
    /// </summary>
    [AvaloniaFact]
    public void Der_waechter_haette_das_kaputte_X_erkannt()
    {
        // So sah es aus, als beide Teilpfade aneinandergehaengt wurden, ohne das fuehrende
        // relative "m" absolut zu schreiben: Der zweite Strich lief bis (24|36).
        Rect bounds = Geometry.Parse("M 18 6 L 6 18 m 6 6 l 12 12").Bounds;

        Assert.True(bounds.Bottom > Icons.Grid + Icons.Stroke / 2,
            "Der Waechter erkennt die kaputte Fassung nicht mehr.");
    }

    [Fact]
    public void Jede_ansicht_hat_ihr_eigenes_zeichen()
    {
        ViewKind[] kinds = Enum.GetValues<ViewKind>();
        string[] icons = [.. kinds.Select(ViewIcons.For)];

        Assert.Equal(kinds.Length, icons.Distinct().Count());
    }
}
