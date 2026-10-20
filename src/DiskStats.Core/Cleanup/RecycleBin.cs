using DiskStats.Core.Interop;

namespace DiskStats.Core.Cleanup;

/// <summary>
/// Verschiebt in den Papierkorb statt endgueltig zu loeschen.
///
/// Windows bietet dafuer SHFileOperation an. Der Umweg ueber die Shell ist der Punkt:
/// Nur sie legt Dateien wiederherstellbar ab, und nur sie warnt, wenn etwas zu gross fuer
/// den Papierkorb ist und darum endgueltig verschwinden wuerde. Direktes Loeschen ueber
/// File.Delete kaeme ohne beides.
/// </summary>
public static class RecycleBin
{
    public readonly record struct Result(bool Ok, bool Aborted, int Code);

    /// <param name="owner">
    /// Fensterhandle, an das die Shell ihre Warnung haengt ("zu gross fuer den Papierkorb").
    /// Ohne Besitzer erscheint sie irgendwo auf dem Bildschirm, hinter der Anwendung.
    /// </param>
    public static Result Send(IReadOnlyList<string> paths, nint owner = 0)
    {
        if (paths.Count == 0) return new Result(true, false, 0);

        // Die Shell kennt das \\?\-Praefix nicht, das der Baum fuer lange Pfade fuehrt.
        return ShellFileOperation.Run("DiskStats.RecycleBin", ShellFileOperation.Delete,
            [.. paths.Select(StripExtendedPrefix)], to: null,
            ShellFileOperation.AllowUndo | ShellFileOperation.NoConfirmation | ShellFileOperation.WantNukeWarning,
            owner);
    }

    /// <summary>
    /// Ueberschreitet der Pfad die klassische Grenze? SHFileOperation ist eine Schnittstelle aus
    /// der Zeit davor; ein Fehlschlag bei solchen Pfaden ist erwartbar und sollte als solcher
    /// erklaert werden, nicht als nackter Fehlercode.
    /// </summary>
    public static bool ExceedsClassicPathLimit(string path)
        => StripExtendedPrefix(path).Length >= ClassicPathLimit;

    private const int ClassicPathLimit = 260;

    private static string StripExtendedPrefix(string path)
        => path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + path[8..]
         : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..]
         : path;
}
