using System.Threading.Channels;

namespace DiskStats.Core.Scanning;

public readonly record struct WalkResult(long DirectoriesVisited, ScanErrorLog Errors);

public static class DirectoryWalker
{
    /// <summary>
    /// Laeuft den Baum unterhalb von <paramref name="rootPath"/> parallel ab.
    /// Die Arbeitsschlange enthaelt Verzeichnisse, nicht Dateien — dadurch bleibt der
    /// Koordinationsaufwand gering und die Last verteilt sich von selbst.
    /// </summary>
    public static WalkResult Walk(
        string rootPath, IEntrySink sink, WalkOptions options, CancellationToken ct = default)
    {
        var errors = new ScanErrorLog();
        var exclusions = new ScanExclusions(options.ExclusionRoot ?? rootPath, options.ExcludedPaths);
        var ids = new DirectoryIdAllocator();
        var queue = Channel.CreateUnbounded<PendingDirectory>(
            new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

        // Zaehlt eingestellte, aber noch nicht fertig bearbeitete Verzeichnisse.
        // Faellt er auf 0, ist der Baum vollstaendig abgearbeitet und die Schlange wird geschlossen.
        int pending = 1;
        long visited = 0;

        queue.Writer.TryWrite(new PendingDirectory(rootPath, Id: 0));

        int workerCount = Math.Max(1, options.WorkerCount);
        var workers = new Task[workerCount];

        // Stirbt ein Worker an etwas Unerwartetem — nicht an einem Lesefehler, den er selbst
        // verbucht —, schliesst er die Schlange fuer alle. Sonst blieben die uebrigen Worker
        // ewig auf ein Verzeichnis warten, das nie als fertig gemeldet wird.
        Exception? fatal = null;

        for (int i = 0; i < workerCount; i++)
        {
            workers[i] = Task.Run(async () =>
            {
                var subdirectories = new List<PendingDirectory>(32);

                try
                {
                    while (await queue.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                    {
                        while (queue.Reader.TryRead(out PendingDirectory current))
                        {
                            ct.ThrowIfCancellationRequested();
                            subdirectories.Clear();
                            try
                            {
                                DirectoryScanner.ScanOne(current.Path, current.Id, sink, subdirectories, errors, ids, exclusions);
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                            {
                                errors.Record(current.Path, ex.HResult);
                            }

                            Interlocked.Increment(ref visited);

                            if (subdirectories.Count > 0)
                            {
                                // Erst hochzaehlen, dann einstellen — sonst koennte der Zaehler
                                // zwischenzeitlich 0 erreichen und die Schlange zu frueh schliessen.
                                Interlocked.Add(ref pending, subdirectories.Count);
                                foreach (PendingDirectory sub in subdirectories)
                                    queue.Writer.TryWrite(sub);
                            }

                            if (Interlocked.Decrement(ref pending) == 0)
                                queue.Writer.TryComplete();
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Interlocked.CompareExchange(ref fatal, ex, null);
                    queue.Writer.TryComplete();
                    throw;
                }
            }, ct);
        }

        try
        {
            Task.WaitAll(workers, ct);
        }
        catch (AggregateException) when (fatal is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(fatal);
        }
        return new WalkResult(visited, errors);
    }
}
