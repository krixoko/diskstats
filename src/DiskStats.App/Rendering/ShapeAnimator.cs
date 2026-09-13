using System.Diagnostics;
using Avalonia;
using DiskStats.Core.Storage;

namespace DiskStats.App.Rendering;

/// <summary>
/// Laesst die Flaechen einer Ansicht wachsen statt erscheinen.
///
/// Der Zweck ist nicht Zierde: Waehrend eines Scans wird die Karte alle paar hundert
/// Millisekunden neu gebaut, und ohne Uebergang blitzt bei jeder Aktualisierung das ganze
/// Bild neu auf. Mit Uebergang sieht man stattdessen, wie sich die Verhaeltnisse einpendeln —
/// die Karte fuellt sich, statt zu flackern.
///
/// Die Schwierigkeit dabei: Jeder Zwischenstand vergibt neue Knotenindizes, ein Knoten ist
/// also zwischen zwei Aktualisierungen nicht wiederzuerkennen. Deshalb der Pfad-Hash als
/// Identitaet — er ueberlebt die Neuindizierung.
/// </summary>
public sealed class ShapeAnimator
{
    private const double SpawnSeconds = 0.46;
    private const double MoveSeconds = 0.30;

    /// <summary>Zeitversatz zwischen erster und letzter Kachel. Erzeugt die Welle.</summary>
    private const double MaxStagger = 0.22;

    /// <summary>Ab dieser Menge wird nicht mehr versetzt — sonst dauert der Einlauf zu lang.</summary>
    private const int StaggerBudget = 900;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Dictionary<ulong, Rect> _previous = [];

    private ulong[] _keys = [];
    private Rect[] _from = [];
    private bool[] _hasFrom = [];
    private double[] _startAt = [];

    /// <summary>Laeuft gerade eine Bewegung? Solange das gilt, wird weitergezeichnet.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>Verwirft den Verlauf — beim Wechsel der Ansicht oder eines neuen Scans.</summary>
    public void Reset()
    {
        _previous = [];
        _keys = [];
        _from = [];
        _hasFrom = [];
        _startAt = [];
        IsRunning = false;
    }

    /// <summary>
    /// Vergleicht das neue Layout mit dem vorigen und legt fest, was wandert und was neu
    /// hereinkommt. Muss nach jedem Layout aufgerufen werden.
    /// </summary>
    public void Prepare(NodeStore store, IReadOnlyList<VisualItem> items, bool animate)
    {
        if (!AppSettings.Current.Animations) animate = false;

        int count = items.Count;
        _keys = new ulong[count];
        _from = new Rect[count];
        _hasFrom = new bool[count];
        _startAt = new double[count];

        var next = new Dictionary<ulong, Rect>(count);
        double now = _clock.Elapsed.TotalSeconds;
        double stagger = count > StaggerBudget ? 0 : MaxStagger / Math.Max(1, count);

        for (int i = 0; i < count; i++)
        {
            ulong key = KeyOf(store, items[i].NodeIndex);
            _keys[i] = key;
            next[key] = items[i].Bounds;

            if (!animate)
            {
                _startAt[i] = double.NegativeInfinity;   // sofort fertig
                continue;
            }

            if (_previous.TryGetValue(key, out Rect old))
            {
                // Bekannte Flaeche: sofort loslegen. Ein Zeitversatz wuerde sie bis zu ihrem
                // Einsatz unsichtbar machen — sie wuerde blinken, obwohl sie durchgehend da ist.
                _from[i] = old;
                _hasFrom[i] = true;
                _startAt[i] = now;
                continue;
            }

            // Neue Flaeche: gestaffelt herein. Die Reihenfolge des Layouts ist absteigend
            // nach Groesse, der Blick folgt also dem Gewicht.
            _startAt[i] = now + i * stagger;
        }

        _previous = next;
        IsRunning = animate && count > 0;
    }

    /// <summary>
    /// Zustand einer Form in diesem Bild: wohin sie gezeichnet wird, wie stark sie
    /// skaliert ist und wie deckend.
    /// </summary>
    public readonly record struct Frame(Rect Bounds, double Scale, double Opacity)
    {
        public bool IsIdle => Scale >= 0.999 && Opacity >= 0.999;
    }

    public Frame At(int index, VisualItem item)
    {
        if (index >= _startAt.Length) return new Frame(item.Bounds, 1, 1);

        double elapsed = _clock.Elapsed.TotalSeconds - _startAt[index];
        if (double.IsInfinity(_startAt[index])) return new Frame(item.Bounds, 1, 1);

        // Vor dem Einsatz: was es schon gab, bleibt sichtbar an alter Stelle; was neu ist,
        // wartet unsichtbar.
        if (elapsed <= 0)
            return _hasFrom[index] ? new Frame(_from[index], 1, 1) : new Frame(item.Bounds, 0, 0);

        if (_hasFrom[index])
        {
            // Bekannte Flaeche: sie wandert an ihren neuen Platz, ohne zu zappeln.
            double t = Math.Clamp(elapsed / MoveSeconds, 0, 1);
            double e = EaseOutCubic(t);
            return new Frame(Lerp(_from[index], item.Bounds, e), 1, 1);
        }

        // Neue Flaeche: sie faehrt mit einem Ueberschwinger heran. Der Ueberschwinger ist
        // das, was den Eindruck von Masse erzeugt — ohne ihn wirkt es geschoben statt bewegt.
        double s = Math.Clamp(elapsed / SpawnSeconds, 0, 1);
        return new Frame(item.Bounds, 0.55 + 0.45 * EaseOutBack(s), Math.Min(1, s * 2.4));
    }

    /// <summary>Prueft, ob noch etwas in Bewegung ist, und haelt die Uhr sonst an.</summary>
    public void Settle(IReadOnlyList<VisualItem> items)
    {
        if (!IsRunning) return;

        for (int i = 0; i < items.Count; i++)
        {
            Frame frame = At(i, items[i]);
            if (frame.IsIdle && frame.Bounds.Equals(items[i].Bounds)) continue;
            return;
        }

        IsRunning = false;
    }

    private static Rect Lerp(Rect a, Rect b, double t) => new(
        a.X + (b.X - a.X) * t,
        a.Y + (b.Y - a.Y) * t,
        a.Width + (b.Width - a.Width) * t,
        a.Height + (b.Height - a.Height) * t);

    private static double EaseOutCubic(double t)
    {
        double u = 1 - t;
        return 1 - u * u * u;
    }

    /// <summary>Faehrt ueber das Ziel hinaus und pendelt zurueck — der eigentliche Wabbel.</summary>
    private static double EaseOutBack(double t)
    {
        const double c1 = 2.2, c3 = c1 + 1;
        double u = t - 1;
        return 1 + c3 * u * u * u + c1 * u * u;
    }

    /// <summary>
    /// Identitaet einer Kachel ueber Neuindizierungen hinweg: FNV-1a ueber die Namen auf dem
    /// Weg zur Wurzel. Der Pfad ist das Einzige, was zwischen zwei Zwischenstaenden stabil ist.
    /// </summary>
    private static ulong KeyOf(NodeStore store, int node)
    {
        const ulong Prime = 1099511628211UL;
        ulong hash = 14695981039346656037UL;

        for (int i = node; i > 0; i = store.ParentIndex[i])
        {
            ReadOnlySpan<byte> name = store.Names.GetBytes(store.NameOffset[i], store.NameLength[i]);
            foreach (byte b in name)
            {
                hash ^= b;
                hash *= Prime;
            }

            hash ^= (byte)'\\';
            hash *= Prime;
        }

        return hash;
    }
}
