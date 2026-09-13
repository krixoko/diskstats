using System.Runtime.InteropServices;

namespace DiskStats.Core.Scanning;

public sealed class FileChangeMonitor : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _timer;
    private readonly Lock _gate = new();
    private readonly HashSet<string> _folders = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _root;
    private readonly string[] _ignored;
    private readonly ScanExclusions _exclusions;
    private bool _overflow;
    private bool _disposed;
    private bool _scheduled;
    public event Action<IReadOnlyList<string>, bool>? Changed;

    public FileChangeMonitor(string root, IEnumerable<string>? ignoredPaths = null, IEnumerable<string>? excludedPatterns = null)
    {
        _root = Path.GetFullPath(root);
        _ignored = (ignoredPaths ?? []).Select(Path.GetFullPath).ToArray();
        _exclusions = new ScanExclusions(_root, excludedPatterns ?? []);
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(_root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
            InternalBufferSize = 32 * 1024,
        };
        _watcher.Created += OnChange;
        _watcher.Changed += OnChange;
        _watcher.Deleted += OnChange;
        _watcher.Renamed += (_, e) => { Queue(e.OldFullPath); Queue(e.FullPath); };
        _watcher.Error += (_, _) => RescanAfterOverflow();
        _watcher.EnableRaisingEvents = true;
    }

    internal void RescanAfterOverflow()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _overflow = true;
            _folders.Clear();
            _folders.Add(_root);
            Schedule();
        }
    }

    private void OnChange(object sender, FileSystemEventArgs e) => Queue(e.FullPath);

    /// <summary>
    /// Nimmt einen gemeldeten Pfad auf. Gibt false zurueck, wenn er herausgefiltert wurde —
    /// weil er in der eigenen Ablage oder einem Ausschluss liegt.
    /// </summary>
    internal bool Queue(string path)
    {
        // Das System meldet, was es hat: Kurznamen, andere Schreibweise, gelegentlich "..".
        // Verglichen wird erst nach dem Aufloesen, sonst rutscht die eigene Ablage durch.
        path = Normalize(path);
        if (_ignored.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(p.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) return false;
        for (string? current = path; current is not null && !current.TrimEnd(Path.DirectorySeparatorChar)
            .Equals(_root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current))
            if (_exclusions.IsExcluded(current)) return false;
        lock (_gate)
        {
            if (_disposed) return false;
            if (!_overflow) _folders.Add(Path.GetDirectoryName(path) ?? _root);
            Schedule();
        }
        return true;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string shortPath, [Out] char[] longPath, uint length);

    private static string Normalize(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            // Kurznamen (PROGRA~1) loest nur das Dateisystem auf — und nur fuer den Teil, den
            // es noch gibt. Fuer eine soeben geloeschte Datei bleibt der Pfad, wie er kam.
            if (OperatingSystem.IsWindows() && full.Contains('~'))
            {
                var buffer = new char[full.Length + 260];
                uint length = GetLongPathNameW(full, buffer, (uint)buffer.Length);
                if (length > 0 && length < buffer.Length) full = new string(buffer, 0, (int)length);
            }
            return full;
        }
        catch (ArgumentException) { return path; }
        catch (IOException) { return path; }
        catch (UnauthorizedAccessException) { return path; }
    }

    private void Schedule()
    {
        // A busy writer must not postpone refresh forever by resetting a debounce timer.
        if (_scheduled) return;
        _scheduled = true;
        _timer.Change(500, Timeout.Infinite);
    }

    private void Flush()
    {
        string[] folders;
        bool overflow;
        lock (_gate)
        {
            if (_disposed || _folders.Count == 0) return;
            folders = _folders.ToArray();
            overflow = _overflow;
            _folders.Clear();
            _overflow = false;
            _scheduled = false;
        }
        Changed?.Invoke(folders, overflow);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Dispose();
        }
        _watcher.Dispose();
    }
}
