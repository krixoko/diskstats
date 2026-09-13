namespace DiskStats.Core.Cleanup;

/// <summary>
/// Entscheidet, was gar nicht erst in die Aufraeumliste darf.
///
/// Die Regel ist bewusst streng und arbeitet auf Pfaden, nicht auf Namen: Ein Ordner namens
/// "Windows" irgendwo unter Downloads ist harmlos, C:\Windows nicht. Im Zweifel wird
/// gesperrt — ein zu vorsichtiges Werkzeug kostet einen Handgriff, ein zu mutiges den Rechner.
/// </summary>
/// <summary>Warum ein Pfad gesperrt ist.</summary>
public enum RefusalKind
{
    NoPath, InvalidPath, NoDrive, WholeDrive, WholeProfile, SystemFolder, SystemFile,

    /// <summary>Die Wurzel des Scans — sie zu loeschen hiesse, alles zu loeschen.</summary>
    ScanRoot,

    /// <summary>Der Eintrag wurde bereits entfernt.</summary>
    Removed,

    /// <summary>Steht schon auf der Liste.</summary>
    AlreadyListed,

    /// <summary>Liegt unter einem Ordner, der schon gelistet ist — waere doppelt gezaehlt.</summary>
    InsideListed,
}

/// <summary>
/// Die Ablehnung als Tatsache, nicht als Satz: Welcher Grund, und welcher Ordner oder welche
/// Datei ihn ausgeloest hat. Der Text dazu entsteht in der Oberflaeche, denn nur die kennt
/// die eingestellte Sprache — ein Scan-Kern hat keine.
/// </summary>
public sealed record Refusal(RefusalKind Kind, string? Name = null);

public static class ProtectedPaths
{
    /// <summary>Systemordner direkt unterhalb einer Laufwerkswurzel.</summary>
    private static readonly string[] RootFolders =
    [
        "Windows", "Program Files", "Program Files (x86)", "ProgramData",
        "System Volume Information", "$Recycle.Bin", "Recovery", "PerfLogs",
        "EFI", "Boot", "$WinREAgent",
    ];

    /// <summary>Systemdateien direkt unterhalb einer Laufwerkswurzel.</summary>
    private static readonly string[] RootFiles =
    [
        "pagefile.sys", "hiberfil.sys", "swapfile.sys", "bootmgr", "DumpStack.log.tmp",
    ];

    public static bool IsProtected(string path) => Reason(path) is not null;

    /// <summary>Warum gesperrt — oder null, wenn nichts dagegen spricht.</summary>
    public static Refusal? Reason(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return new Refusal(RefusalKind.NoPath);

        // "C:" ohne Trenner meint unter Windows nicht die Wurzel, sondern das aktuelle
        // Verzeichnis auf diesem Laufwerk. Ein solcher Pfad ist zu mehrdeutig, um ihn
        // aufloesen zu lassen — was er bedeutet, haengt davon ab, wo der Prozess steht.
        string raw = path.Trim();
        if (raw.Length == 2 && raw[1] == ':' && char.IsLetter(raw[0]))
            return new Refusal(RefusalKind.WholeDrive);

        string full;
        try { full = Path.GetFullPath(path); }
        catch (ArgumentException) { return new Refusal(RefusalKind.InvalidPath); }
        catch (NotSupportedException) { return new Refusal(RefusalKind.InvalidPath); }

        string trimmed = full.TrimEnd(Path.DirectorySeparatorChar);
        string? root = Path.GetPathRoot(full);

        if (root is null) return new Refusal(RefusalKind.NoDrive);

        // Die Laufwerkswurzel selbst: das waere alles.
        if (trimmed.Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            return new Refusal(RefusalKind.WholeDrive);

        foreach (string name in RootFolders)
        {
            string guarded = Path.Combine(root, name);
            if (IsSameOrBelow(trimmed, guarded)) return new Refusal(RefusalKind.SystemFolder, name);
        }

        foreach (string name in RootFiles)
        {
            if (trimmed.Equals(Path.Combine(root, name), StringComparison.OrdinalIgnoreCase))
                return new Refusal(RefusalKind.SystemFile, name);
        }

        // Das Benutzerprofil einschliesslich seiner Vorfahren; einzelne Ordner darin sind erlaubt.
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 0
            && IsSameOrBelow(profile, trimmed))
            return new Refusal(RefusalKind.WholeProfile);

        // Auch fremde Profile und der gemeinsame Profilordner sind keine Aufraeumziele.
        string users = Path.Combine(root, "Users");
        if (trimmed.Equals(users, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetDirectoryName(trimmed), users, StringComparison.OrdinalIgnoreCase))
            return new Refusal(RefusalKind.WholeProfile);

        return null;
    }

    /// <summary>
    /// Ist der Pfad der gesperrte selbst oder liegt er darunter? Der Vergleich haengt den
    /// Trenner an, damit "C:\Programme neu" nicht als Teil von "C:\Programme" gilt.
    /// </summary>
    private static bool IsSameOrBelow(string candidate, string guarded)
    {
        string trimmed = guarded.TrimEnd(Path.DirectorySeparatorChar);

        if (candidate.Equals(trimmed, StringComparison.OrdinalIgnoreCase)) return true;

        return candidate.StartsWith(trimmed + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }
}
