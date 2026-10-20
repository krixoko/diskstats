using DiskStats.Core.Interop;

namespace DiskStats.Core.Cleanup;

/// <summary>Warum eine Verschiebung gar nicht erst begonnen wird.</summary>
public enum FileMoveProblem
{
    /// <summary>Der Zielordner existiert nicht (mehr).</summary>
    DestinationMissing,

    /// <summary>Das Ziel liegt in der Auswahl oder ist ihr Ordner — die Verschiebung waere sinnlos oder zirkulaer.</summary>
    InvalidDestination,

    /// <summary>Ein Eintrag steht auf der Schutzliste, siehe <see cref="FileMoveException.Refusal"/>.</summary>
    Protected,

    /// <summary>Verknuepfung oder reiner Cloud-Eintrag: Verschieben koennte das Ziel statt der Verknuepfung treffen.</summary>
    Linked,

    /// <summary>Am Ziel ist nicht genug Platz.</summary>
    InsufficientSpace,
}

/// <summary>
/// Eine Verschiebung wurde im Vorfeld abgelehnt. Der Grund steht als Tatsache da, nicht als Satz:
/// Der Text entsteht in der Oberflaeche, denn nur die kennt die eingestellte Sprache — wie bei
/// <see cref="Refusal"/>.
/// </summary>
public sealed class FileMoveException : IOException
{
    public FileMoveException(FileMoveProblem problem, string path, Refusal? refusal = null,
        long needed = 0, long available = 0)
        : base($"{problem}: {path}")
    {
        Problem = problem;
        Path = path;
        Refusal = refusal;
        Needed = needed;
        Available = available;
    }

    public FileMoveProblem Problem { get; }

    /// <summary>Der betroffene Eintrag oder Zielordner.</summary>
    public string Path { get; }

    /// <summary>Bei <see cref="FileMoveProblem.Protected"/>: Warum der Eintrag gesperrt ist.</summary>
    public Refusal? Refusal { get; }

    /// <summary>Bei <see cref="FileMoveProblem.InsufficientSpace"/>: benoetigte und freie Bytes.</summary>
    public long Needed { get; }

    public long Available { get; }
}

/// <summary>Preflight plus the Windows move UI, including progress, cancellation and collision prompts.</summary>
public static class FileMove
{
    public sealed record Plan(string[] Sources, string Destination, long Bytes, long FreeBytes);

    public static Plan Prepare(IEnumerable<string> sources, string destination, CancellationToken cancel = default)
    {
        destination = Path.GetFullPath(destination);
        if (!Directory.Exists(destination)) throw new FileMoveException(FileMoveProblem.DestinationMissing, destination);
        RejectLinks(destination);
        var paths = sources.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p.Length).ToList();
        var kept = new List<string>();
        long bytes = 0;
        foreach (string path in paths)
        {
            cancel.ThrowIfCancellationRequested();
            if (kept.Any(p => Inside(path, p))) continue;
            if (ProtectedPaths.Reason(path) is { } reason)
                throw new FileMoveException(FileMoveProblem.Protected, path, reason);
            RejectLinks(path);
            if (Inside(destination, path) || string.Equals(Path.GetDirectoryName(path),
                    destination.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new FileMoveException(FileMoveProblem.InvalidDestination, destination);
            var pending = new Stack<string>();
            pending.Push(path);
            while (pending.TryPop(out string? current))
            {
                cancel.ThrowIfCancellationRequested();
                FileAttributes attributes = File.GetAttributes(current);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new FileMoveException(FileMoveProblem.Linked, current);
                if (attributes.HasFlag(FileAttributes.Directory))
                    foreach (string child in Directory.EnumerateFileSystemEntries(current)) pending.Push(child);
                else bytes = checked(bytes + new FileInfo(current).Length);
            }
            kept.Add(path);
        }
        if (kept.Count == 0) throw new ArgumentException("No files selected.");
        // UNC shares are supported by DriveInfo on Windows; inability to query is surfaced before moving.
        long free = new DriveInfo(Path.GetPathRoot(destination)!).AvailableFreeSpace;
        if (free < bytes) throw new FileMoveException(FileMoveProblem.InsufficientSpace, destination, needed: bytes, available: free);
        return new Plan(kept.ToArray(), destination, bytes, free);
    }

    private static bool Inside(string path, string parent) =>
        path.TrimEnd(Path.DirectorySeparatorChar).Equals(parent.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void RejectLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new FileMoveException(FileMoveProblem.Linked, current);
    }

    public static RecycleBin.Result Send(Plan plan, nint owner)
        => ShellFileOperation.Run("DiskStats.Move", ShellFileOperation.Move, plan.Sources, plan.Destination,
            ShellFileOperation.AllowUndo, // Kollisions- und Fehlerdialoge der Shell bleiben bewusst an.
            owner);
}
