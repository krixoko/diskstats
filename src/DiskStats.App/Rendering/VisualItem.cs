using Avalonia;

namespace DiskStats.App.Rendering;

public enum ShapeKind { Rect, Arc, Circle }

/// <summary>
/// Ein gezeichnetes Element einer Ansicht. Alle acht Ansichten sind im Kern dieselbe Sache —
/// eine Abbildung vom Baum auf Formen — und unterscheiden sich nur darin, welche Form sie
/// waehlen und wie sie den Platz aufteilen. Deshalb ein gemeinsamer Typ statt acht eigener.
/// Die Dateitabelle ist keine Layout-Engine: sie blendet die Zeichenflaeche aus.
/// </summary>
public readonly record struct VisualItem
{
    public int NodeIndex { get; init; }
    public int Depth { get; init; }
    public bool IsDirectory { get; init; }
    public ShapeKind Kind { get; init; }

    /// <summary>Umschliessendes Rechteck — fuer Rect die Form selbst, sonst zum Platzieren von Text.</summary>
    public Rect Bounds { get; init; }

    public Point Center { get; init; }
    public double InnerRadius { get; init; }
    public double OuterRadius { get; init; }

    /// <summary>Startwinkel im Bogenmass, 0 = rechts, im Uhrzeigersinn wachsend.</summary>
    public double StartAngle { get; init; }
    public double SweepAngle { get; init; }

    public static VisualItem OfRect(int node, int depth, bool isDirectory, Rect bounds) => new()
    {
        NodeIndex = node, Depth = depth, IsDirectory = isDirectory,
        Kind = ShapeKind.Rect, Bounds = bounds,
    };

    public static VisualItem OfCircle(int node, int depth, bool isDirectory, Point center, double radius) => new()
    {
        NodeIndex = node, Depth = depth, IsDirectory = isDirectory,
        Kind = ShapeKind.Circle, Center = center, OuterRadius = radius,
        Bounds = new Rect(center.X - radius, center.Y - radius, radius * 2, radius * 2),
    };

    public static VisualItem OfArc(
        int node, int depth, bool isDirectory,
        Point center, double innerRadius, double outerRadius, double startAngle, double sweepAngle) => new()
    {
        NodeIndex = node, Depth = depth, IsDirectory = isDirectory,
        Kind = ShapeKind.Arc, Center = center,
        InnerRadius = innerRadius, OuterRadius = outerRadius,
        StartAngle = startAngle, SweepAngle = sweepAngle,
        Bounds = new Rect(
            center.X - outerRadius, center.Y - outerRadius, outerRadius * 2, outerRadius * 2),
    };

    public bool Contains(Point point)
    {
        switch (Kind)
        {
            case ShapeKind.Rect:
                return Bounds.Contains(point);

            case ShapeKind.Circle:
            {
                double dx = point.X - Center.X, dy = point.Y - Center.Y;
                return dx * dx + dy * dy <= OuterRadius * OuterRadius;
            }

            case ShapeKind.Arc:
            {
                double dx = point.X - Center.X, dy = point.Y - Center.Y;
                double distance = Math.Sqrt(dx * dx + dy * dy);
                if (distance < InnerRadius || distance > OuterRadius) return false;

                double angle = Math.Atan2(dy, dx);
                if (angle < 0) angle += Math.Tau;

                double start = StartAngle;
                if (start < 0) start += Math.Tau;
                double relative = angle - start;
                if (relative < 0) relative += Math.Tau;

                return relative <= SweepAngle;
            }

            default:
                return false;
        }
    }

    /// <summary>Ungefaehre Flaeche — dient dem Detailbudget und der Frage, ob Text hineinpasst.</summary>
    public double Area => Kind switch
    {
        ShapeKind.Rect => Bounds.Width * Bounds.Height,
        ShapeKind.Circle => Math.PI * OuterRadius * OuterRadius,
        ShapeKind.Arc => 0.5 * SweepAngle * (OuterRadius * OuterRadius - InnerRadius * InnerRadius),
        _ => 0,
    };
}
