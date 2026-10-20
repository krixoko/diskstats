using DiskStats.Core.Scanning;

namespace DiskStats.Core.Tests;

/// <summary>
/// Ein Ueberlauf des Systempuffers hiesse "alles neu lesen". Auf einem Laufwerk waere das ein
/// kompletter Scan - und auf einem aktiven System kaeme der naechste Ueberlauf sofort.
/// </summary>
public sealed class WatchPolicyTests
{
    private static readonly DateTime Now = new(2026, 10, 20, 12, 0, 0);

    [Fact]
    public void Kleiner_baum_ohne_vorherigen_lauf_wird_neu_gelesen()
        => Assert.True(WatchPolicy.ShouldRescanAfterOverflow(5_000, lastRescan: null, Now));

    [Fact]
    public void Grosser_baum_wird_nie_von_selbst_neu_gelesen()
    {
        Assert.False(WatchPolicy.ShouldRescanAfterOverflow(WatchPolicy.MaxEntriesForAutomaticRescan + 1, null, Now));
        Assert.False(WatchPolicy.ShouldRescanAfterOverflow(2_000_000, null, Now));
    }

    [Fact]
    public void Die_grenze_selbst_gehoert_noch_dazu()
        => Assert.True(WatchPolicy.ShouldRescanAfterOverflow(WatchPolicy.MaxEntriesForAutomaticRescan, null, Now));

    [Fact]
    public void Nach_einem_lauf_gilt_eine_abkuehlzeit()
    {
        DateTime last = Now.AddMinutes(-1);

        Assert.False(WatchPolicy.ShouldRescanAfterOverflow(5_000, last, Now));
        Assert.False(WatchPolicy.ShouldRescanAfterOverflow(5_000, last, last + WatchPolicy.OverflowCooldown - TimeSpan.FromSeconds(1)));
        Assert.True(WatchPolicy.ShouldRescanAfterOverflow(5_000, last, last + WatchPolicy.OverflowCooldown));
    }
}
