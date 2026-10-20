namespace DiskStats.App;

/// <summary>
/// Liest die Kommandozeile. Bewusst klein: ein Pfad, ein paar Schalter, kein Parser-Paket.
///
/// Die Argumente tragen den Programmnamen an erster Stelle (so liefert ihn
/// <see cref="Environment.GetCommandLineArgs"/>); alles danach sind echte Argumente.
/// </summary>
internal static class CommandLine
{
    /// <summary>
    /// Haengt Elevation.Relaunch an den Neustart an. Der neue Prozess weiss damit, dass der alte
    /// noch einen Moment laeuft und die Einzelinstanz-Sperre haelt — er wartet darauf, statt sich
    /// als "zweiter Start" zu beenden.
    /// </summary>
    internal const string ElevatedRestart = "--elevated-restart";

    internal static bool IsElevatedRestart(IReadOnlyList<string> args)
        => args.Skip(1).Any(a => a.Equals(ElevatedRestart, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Der zu scannende Pfad: das erste Argument, das kein Schalter ist. Schalter beginnen mit
    /// "--"; <c>--view</c> hat einen Wert, der nicht fuer einen Pfad gehalten werden darf. Frueher
    /// galt blind das erste Argument, und ein vorangestellter Schalter wurde zum "Pfad".
    /// </summary>
    internal static string PathFrom(IReadOnlyList<string> args)
    {
        for (int i = 1; i < args.Count; i++)
        {
            if (args[i].Equals("--view", StringComparison.OrdinalIgnoreCase)) { i++; continue; }
            if (args[i].StartsWith("--", StringComparison.Ordinal)) continue;

            return args[i];
        }

        return string.Empty;
    }
}
