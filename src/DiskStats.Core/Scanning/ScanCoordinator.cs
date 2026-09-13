using DiskStats.Core.Storage;

namespace DiskStats.Core.Scanning;

public enum ScanMethod { Auto, Directory, Ntfs }

public static class ScanCoordinator
{
    public sealed record Result(NodeBuffer Buffer, WalkResult Walk, ScanMethod Method, string? Fallback);

    public static Result Scan(string root, NodeBuffer progressBuffer, WalkOptions options, CancellationToken cancel = default)
    {
        string full = Path.GetFullPath(root);
        bool wholeDrive = full.TrimEnd(Path.DirectorySeparatorChar).Equals(
            Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        string? fallback = null;
        if (options.Method == ScanMethod.Ntfs || (options.Method == ScanMethod.Auto && wholeDrive))
        {
            // Auch der MFT-Scan hat Fehler zu melden: uebersprungene Records, nicht
            // nachlesbare Dateien. Ein leeres Protokoll waere eine falsche Zusage.
            var errors = new ScanErrorLog();
            if (NtfsScanner.TryScan(full, options, cancel, errors, out NodeBuffer? fast, out fallback))
                return new Result(fast!, new WalkResult(fast!.MaxDirectoryId + 1, errors), ScanMethod.Ntfs, null);
        }
        cancel.ThrowIfCancellationRequested();
        WalkResult walk = DirectoryWalker.Walk(full, progressBuffer, options, cancel);
        return new Result(progressBuffer, walk, ScanMethod.Directory, fallback);
    }
}
