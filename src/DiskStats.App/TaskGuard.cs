using DiskStats.Core.Diagnostics;

namespace DiskStats.App;

/// <summary>
/// Der eine Ort, an dem Hintergrundarbeit ohne Aufrufer endet.
///
/// Ein <c>async void</c>-Handler, in dem etwas schiefgeht, beendet unter Avalonia den Prozess —
/// oder verschwindet im UnobservedTaskException-Handler, je nach Laune des Schedulers. Beides
/// ist falsch. Alle Handler, die nichts zurueckgeben koennen, reichen ihre Arbeit hier durch:
/// Fehler landen im Protokoll und in der Statuszeile, ein Abbruch ist kein Fehler.
/// </summary>
public static class TaskGuard
{
    public static async Task Fire(Task work, string context, Action<string>? report = null)
    {
        try
        {
            await work;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            DiagnosticLog.Write(context, ex);
            report?.Invoke(ex.Message);
        }
    }
}

/// <summary>
/// Reiht Snapshot-Arbeit auf. Zwei Speicherungen zugleich auf dieselbe Ablage — eine nach dem
/// Scan, eine nach einem Refresh — liefen frueher parallel, samt Aufraeumen desselben Ordners.
/// Hier laeuft immer nur ein Auftrag; ein gescheiterter blockiert die folgenden nicht.
/// </summary>
public sealed class SnapshotQueue
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Stellt einen Auftrag ein. Die zurueckgegebene Task endet erfolgreich, auch wenn er scheitert.</summary>
    public async Task Enqueue(Func<Task> work)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(work).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Snapshot", ex);
        }
        finally
        {
            _gate.Release();
        }
    }
}
