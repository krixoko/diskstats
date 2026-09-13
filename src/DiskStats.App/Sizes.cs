namespace DiskStats.App;

/// <summary>Groessenangaben in einer Form, die man im Vorbeigehen liest.</summary>
public static class Sizes
{
    public static string Format(long bytes)
    {
        double step = AppSettings.Current.Sizes == SizeBase.Decimal ? 1000 : 1024;
        double k = step, m = k * step, g = m * step, t = g * step;

        return bytes switch
        {
            _ when bytes >= t => $"{bytes / t:F2} TB",
            _ when bytes >= g => $"{bytes / g:F1} GB",
            _ when bytes >= m => $"{bytes / m:F1} MB",
            _ when bytes >= k => $"{bytes / k:F0} KB",
            _ => $"{bytes} B",
        };
    }
}
