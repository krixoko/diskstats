using System.Runtime.InteropServices;

namespace DiskStats.Core.Cleanup;

/// <summary>Preflight plus the Windows move UI, including progress, cancellation and collision prompts.</summary>
public static class FileMove
{
    public sealed record Plan(string[] Sources, string Destination, long Bytes, long FreeBytes);

    public static Plan Prepare(IEnumerable<string> sources, string destination, CancellationToken cancel = default)
    {
        destination = Path.GetFullPath(destination);
        if (!Directory.Exists(destination)) throw new DirectoryNotFoundException(destination);
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
                throw new IOException($"{path}: {reason.Kind}");
            RejectLinks(path);
            if (Inside(destination, path) || string.Equals(Path.GetDirectoryName(path),
                    destination.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Invalid move destination: {destination}");
            var pending = new Stack<string>();
            pending.Push(path);
            while (pending.TryPop(out string? current))
            {
                cancel.ThrowIfCancellationRequested();
                FileAttributes attributes = File.GetAttributes(current);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException($"Linked or cloud-only entry: {current}");
                if (attributes.HasFlag(FileAttributes.Directory))
                    foreach (string child in Directory.EnumerateFileSystemEntries(current)) pending.Push(child);
                else bytes = checked(bytes + new FileInfo(current).Length);
            }
            kept.Add(path);
        }
        if (kept.Count == 0) throw new ArgumentException("No files selected.");
        // UNC shares are supported by DriveInfo on Windows; inability to query is surfaced before moving.
        long free = new DriveInfo(Path.GetPathRoot(destination)!).AvailableFreeSpace;
        if (free < bytes) throw new IOException($"Insufficient free space: {free:N0} < {bytes:N0} bytes.");
        return new Plan(kept.ToArray(), destination, bytes, free);
    }

    private static bool Inside(string path, string parent) =>
        path.TrimEnd(Path.DirectorySeparatorChar).Equals(parent.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void RejectLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException($"Linked or cloud-only entry: {current}");
    }

    public static RecycleBin.Result Send(Plan plan, nint owner)
    {
        if (!OperatingSystem.IsWindows()) return new(false, false, -1);
        RecycleBin.Result result = default;
        var thread = new Thread(() =>
        {
            var operation = new Operation {
                Window = owner, Function = 1,
                From = string.Join('\0', plan.Sources) + "\0\0",
                To = plan.Destination + "\0\0",
                Flags = 0x0040, // Allow undo. Never suppress collision or error dialogs.
            };
            int code = SHFileOperation(ref operation);
            result = new(code == 0 && !operation.Aborted, operation.Aborted, code);
        }) { IsBackground = true, Name = "DiskStats.Move" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 8)]
    private struct Operation
    {
        public nint Window;
        public uint Function;
        [MarshalAs(UnmanagedType.LPWStr)] public string From;
        [MarshalAs(UnmanagedType.LPWStr)] public string To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool Aborted;
        public nint Mappings;
        public nint ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref Operation operation);
}
