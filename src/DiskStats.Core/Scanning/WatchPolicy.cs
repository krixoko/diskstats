namespace DiskStats.Core.Scanning;

/// <summary>
/// Wann die Live-Ueberwachung nach einem Ueberlauf von sich aus neu liest.
///
/// Meldet der Systempuffer einen Ueberlauf, hat Windows Aenderungen verschluckt; man weiss nicht,
/// welche. Der sichere Ausweg ist, alles neu zu lesen. Bei einem kleinen Ordner kostet das
/// Sekunden, bei einem Laufwerk einen kompletten Scan — und weil auf einem aktiven System der
/// naechste Ueberlauf gleich folgt, wuerde die Ueberwachung ein Laufwerk im Kreis scannen,
/// samt Schreiben von Snapshots. Ein Werkzeug gegen Plattenmuell soll die Platte nicht abnutzen.
/// </summary>
public static class WatchPolicy
{
    /// <summary>
    /// Bis zu dieser Baumgroesse liest die Ueberwachung nach einem Ueberlauf selbst neu (ein
    /// Durchlauf dauert Sekunden). Darueber bleibt es beim Hinweis, und der Benutzer entscheidet.
    /// </summary>
    public const int MaxEntriesForAutomaticRescan = 200_000;

    /// <summary>Mindestabstand zweier automatischer Komplett-Lesevorgaenge.</summary>
    public static readonly TimeSpan OverflowCooldown = TimeSpan.FromMinutes(5);

    public static bool ShouldRescanAfterOverflow(int entryCount, DateTime? lastRescan, DateTime now)
        => entryCount <= MaxEntriesForAutomaticRescan
           && (lastRescan is null || now - lastRescan.Value >= OverflowCooldown);
}
