using System.Diagnostics;
using Avalonia.Threading;
using DiskStats.Core.Scanning;
using DiskStats.Core.Storage;

namespace DiskStats.App;

/// <summary>
/// Fuehrt einen Scan aus und liefert waehrenddessen Zwischenstaende an die Oberflaeche.
///
/// Der Takt passt sich an: Ein Zwischenstand wird nur gebaut, wenn der Baum seit dem letzten
/// deutlich gewachsen ist. Am Anfang aendert sich das Bild stark und wird oft neu gezeichnet;
/// spaeter, wenn ein zusaetzlicher Prozent Dateien nichts mehr sichtbar veraendert, wird der
/// Aufbau seltener — was auch noetig ist, weil jeder Zwischenstand ueber alle bisherigen
/// Eintraege laeuft.
/// </summary>
public static class ScanSession
{
    public sealed record Update(
        NodeStore Store,
        long FileCount,
        long TotalBytes,
        TimeSpan Elapsed,
        bool IsFinal,
        int UnreadableFolders, ScanMethod Method = ScanMethod.Directory, string? Fallback = null);

    private const int PollMilliseconds = 400;
    private const double GrowthFactorForRedraw = 1.35;
    private const int MinimumEntriesForFirstDraw = 200;

    public static async Task RunAsync(
        string rootPath, int workers, Action<Update> onUpdate, CancellationToken ct)
        => await RunAsync(rootPath, new WalkOptions { WorkerCount = workers }, onUpdate, ct);

    public static async Task RunAsync(string rootPath, WalkOptions options, Action<Update> onUpdate, CancellationToken ct)
    {
        var buffer = new NodeBuffer();
        var clock = Stopwatch.StartNew();

        Task<ScanCoordinator.Result> walk = Task.Run(
            () => ScanCoordinator.Scan(rootPath, buffer, options, ct), ct);

        int lastDrawnEntries = 0;

        while (!walk.IsCompleted)
        {
            await Task.WhenAny(walk, Task.Delay(PollMilliseconds, ct)).ConfigureAwait(false);
            if (walk.IsCompleted || ct.IsCancellationRequested) break;

            int current = buffer.Entries.Count;
            if (current < MinimumEntriesForFirstDraw) continue;
            if (lastDrawnEntries > 0 && current < lastDrawnEntries * GrowthFactorForRedraw) continue;

            NodeStore? partial = TryBuildPartial(buffer, rootPath);
            if (partial is null) continue;

            lastDrawnEntries = current;
            await Publish(onUpdate, partial, clock.Elapsed, isFinal: false, unreadable: 0);
        }

        ScanCoordinator.Result result = await walk.ConfigureAwait(false);

        // Wer kurz vor Schluss abbricht, will nicht noch sekundenlang auf den Aufbau eines
        // Baums warten, den niemand mehr sieht.
        ct.ThrowIfCancellationRequested();
        NodeStore store = NodeStoreBuilder.Build(result.Buffer, rootPath);
        ct.ThrowIfCancellationRequested();
        SizeAggregator.Aggregate(store);
        ct.ThrowIfCancellationRequested();
        await Publish(onUpdate, store, clock.Elapsed, isFinal: true, result.Walk.Errors.Count, result.Method, result.Fallback);
    }

    /// <summary>
    /// Baut aus einem eingefrorenen Zwischenstand. Schlaegt der Aufbau fehl, weil der Scan
    /// mitten in einem Verzeichnis stand, wird der Zwischenstand stillschweigend uebersprungen —
    /// der naechste ist in wenigen hundert Millisekunden da.
    /// </summary>
    private static NodeStore? TryBuildPartial(NodeBuffer buffer, string rootPath)
    {
        try
        {
            NodeStore store = NodeStoreBuilder.Build(buffer.Freeze(), rootPath);
            SizeAggregator.Aggregate(store);
            return store;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task Publish(
        Action<Update> onUpdate, NodeStore store, TimeSpan elapsed, bool isFinal, int unreadable,
        ScanMethod method = ScanMethod.Directory, string? fallback = null)
    {
        var update = new Update(
            store,
            FileCount: store.Count - 1,
            TotalBytes: store.Size.Length > 0 ? store.Size[0] : 0,
            elapsed,
            isFinal,
            unreadable, method, fallback);

        await Dispatcher.UIThread.InvokeAsync(() => onUpdate(update));
    }
}
