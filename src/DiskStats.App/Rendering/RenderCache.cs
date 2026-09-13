using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace DiskStats.App.Rendering;

/// <summary>
/// Haelt Schriften, Pinsel, Stifte und gesetzte Texte vor, die beim Zeichnen immer wieder
/// gleich gebraucht werden.
///
/// Die Karte zeichnet waehrend einer Bewegung sechzig Bilder je Sekunde mit tausenden Formen.
/// Jeder <c>new Pen</c> und jeder <c>new FormattedText</c> in diesem Pfad ist Abfall fuer
/// den Speichersammler, und der meldet sich als Ruckler mitten in der Bewegung. Was sich
/// zwischen zwei Bildern nicht aendert, wird deshalb einmal erzeugt und wiederverwendet.
///
/// Nur vom Oberflaechenfaden benutzt, deshalb ohne Sperren.
/// </summary>
internal static class RenderCache
{
    public static readonly Typeface Ui = new("Segoe UI Variable Text, Segoe UI");
    public static readonly Typeface UiBold = new("Segoe UI Variable Text, Segoe UI", FontStyle.Normal, FontWeight.SemiBold);
    public static readonly Typeface Mono = new("Consolas, Cascadia Mono");

    private static readonly Dictionary<uint, IBrush> Brushes = [];
    private static readonly Dictionary<(IBrush Brush, double Thickness, bool Round), IPen> Pens = [];
    private static readonly Dictionary<(string, Typeface, double, IBrush, double), FormattedText> Texts = [];

    /// <summary>
    /// Ab hier wird der Textspeicher geleert. Ranglisten und Karten brauchen ein paar hundert
    /// Eintraege; wer weit darueber kommt, hat wechselnde Breiten und keinen Nutzen vom Merken.
    /// </summary>
    private const int MaxTexts = 4096;

    /// <summary>Ein Pinsel je Farbe. Die Farbe selbst ist der Schluessel.</summary>
    public static IBrush Brush(Color color)
    {
        uint key = color.ToUInt32();
        if (Brushes.TryGetValue(key, out IBrush? brush)) return brush;

        brush = new ImmutableSolidColorBrush(color);
        Brushes[key] = brush;
        return brush;
    }

    /// <summary>Ein Stift je Pinsel und Staerke. Der Pinsel zaehlt als Referenz.</summary>
    public static IPen Pen(IBrush brush, double thickness) => PenFor(brush, thickness, round: false);

    /// <summary>Stift mit runden Enden und Ecken — fuer die Konturen der Bildzeichen.</summary>
    public static IPen RoundPen(IBrush brush, double thickness) => PenFor(brush, thickness, round: true);

    private static IPen PenFor(IBrush brush, double thickness, bool round)
    {
        var key = (brush, thickness, round);
        if (Pens.TryGetValue(key, out IPen? pen)) return pen;

        pen = round
            ? new Pen(brush, thickness) { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }
            : new Pen(brush, thickness);
        Pens[key] = pen;
        return pen;
    }

    /// <summary>
    /// Ein gesetzter Text je Inhalt, Schrift, Groesse, Pinsel und Breite. Das Setzen ist der
    /// teure Teil — Schrift laden, Glyphen suchen, Zeile brechen —, das Zeichnen danach billig.
    /// </summary>
    public static FormattedText Text(
        string value, Typeface face, double size, IBrush brush, double maxWidth = double.PositiveInfinity)
    {
        double width = double.IsInfinity(maxWidth) ? double.PositiveInfinity : Math.Max(8, maxWidth);
        var key = (value, face, size, brush, width);
        if (Texts.TryGetValue(key, out FormattedText? text)) return text;

        if (Texts.Count >= MaxTexts) Texts.Clear();

        text = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, brush)
        {
            MaxTextWidth = width,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        Texts[key] = text;
        return text;
    }
}
