namespace DiskStats.Core.Scanning;

/// <summary>
/// Reicht Fortschritt hoechstens einmal je ganzem Prozent und nie rueckwaerts weiter.
///
/// Der MFT-Scan liest in Schleifen mit zehntausenden Durchlaeufen; jeder davon duerfte die
/// Oberflaeche nicht mit einem Aufruf belasten. Und weil der Lesepfad zwischen zwei
/// Strategien wechseln kann, soll ein Balken nie zurueckspringen.
/// </summary>
internal sealed class ScanProgress(Action<double>? sink)
{
    private int _lastPercent = -1;

    /// <param name="fraction">Anteil von 0 bis 1; Werte ausserhalb werden begrenzt.</param>
    public void Report(double fraction)
    {
        if (sink is null) return;

        double clamped = double.IsNaN(fraction) ? 0 : Math.Clamp(fraction, 0, 1);
        int percent = (int)(clamped * 100);
        if (percent <= _lastPercent) return;

        _lastPercent = percent;
        sink(clamped);
    }
}
