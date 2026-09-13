namespace DiskStats.App.Rendering;

/// <summary>
/// Wieviel eine Ansicht hoechstens zeichnet: keine Form unter <see cref="MinArea"/>
/// Bildpunkten im Quadrat, nicht mehr als <see cref="MaxItems"/> Formen insgesamt.
///
/// Es werden nie alle Knoten gezeichnet — bei zwei Millionen waere jedes Bild eine
/// Sekunde. Sichtbar sind selten mehr als ein paar tausend Formen; alles darunter ist
/// weder lesbar noch anklickbar und kostet nur Zeit. Das Budget gilt fuer alle Ansichten
/// gleich, damit der Regler "Detailgrad" ueberall dasselbe bewirkt.
/// </summary>
public readonly record struct LayoutBudget(double MinArea, int MaxItems)
{
    /// <summary>Mindestflaeche bei normalem Detailgrad. Darunter ist eine Kachel ein Strich.</summary>
    public const double BaseMinArea = 46;

    /// <summary>Aus den Einstellungen: grob zeichnet weniger, fein mehr.</summary>
    public static LayoutBudget FromSettings(AppSettings settings) => new(
        MinArea: BaseMinArea * settings.DetailFactor,
        MaxItems: settings.Detail switch
        {
            DetailLevel.Coarse => 6_000,
            DetailLevel.Fine => 20_000,
            _ => 12_000,
        });

    public static LayoutBudget Current => FromSettings(AppSettings.Current);

    /// <summary>Ob eine weitere Form noch ins Budget passt.</summary>
    public bool Allows(int count) => count < MaxItems;

    /// <summary>Ob eine Flaeche gross genug ist, um gezeichnet zu werden.</summary>
    public bool Fits(double area) => area >= MinArea;
}
