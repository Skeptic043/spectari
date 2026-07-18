using Spectari.Util;
using Xunit;

namespace Spectari.Tests;

public sealed class HostAccessServiceTests
{
    [Fact]
    public void InterpretsHelperExitCodes()
    {
        Assert.Equal(
            HostAccessSetupOutcome.Succeeded,
            HostAccessService.InterpretHelperExitCode(0));
        Assert.Equal(
            HostAccessSetupOutcome.ApprovalDeclined,
            HostAccessService.InterpretHelperExitCode(-2));
        Assert.Equal(
            HostAccessSetupOutcome.Failed,
            HostAccessService.InterpretHelperExitCode(-1));
        Assert.Equal(
            HostAccessSetupOutcome.Failed,
            HostAccessService.InterpretHelperExitCode(2));
        Assert.Equal(
            HostAccessSetupOutcome.Failed,
            HostAccessService.InterpretHelperExitCode(3));
    }

    [Fact]
    public void ReservationClassificationFailsClosedWhenOwnerIsUnknown()
    {
        HostReservationReview review = HostAccessService.ClassifyReservationOwner(
            HostAccessService.UnknownOwner,
            "DOMAIN\\current");

        Assert.Equal(HostReservationStatus.UnknownOwner, review.Status);
        Assert.Null(review.Owner);
    }

    [Fact]
    public void ReservationProbeFailsClosedWhenItDoesNotCompleteCleanly()
    {
        var timedOut = new ProcessResult(true, null, "", "");
        var failed = new ProcessResult(false, 1, "", "");
        var unparseable = new ProcessResult(
            false,
            0,
            "Reserved URL : http://+:8093/\nAccount: unavailable",
            "");

        Assert.Equal(
            HostAccessService.UnknownOwner,
            HostAccessService.InterpretReservationOwnerProbe(8093, timedOut));
        Assert.Equal(
            HostAccessService.UnknownOwner,
            HostAccessService.InterpretReservationOwnerProbe(8093, failed));
        Assert.Equal(
            HostAccessService.UnknownOwner,
            HostAccessService.InterpretReservationOwnerProbe(8093, unparseable));
    }

    [Fact]
    public void ReservationClassificationPreservesOwnerTrust()
    {
        HostReservationReview available = HostAccessService.ClassifyReservationOwner(
            null, "DOMAIN\\current");
        HostReservationReview owned = HostAccessService.ClassifyReservationOwner(
            "domain\\CURRENT", "DOMAIN\\current");
        HostReservationReview foreign = HostAccessService.ClassifyReservationOwner(
            "DOMAIN\\other", "DOMAIN\\current");

        Assert.Equal(HostReservationStatus.AvailableOrOwned, available.Status);
        Assert.Equal(HostReservationStatus.AvailableOrOwned, owned.Status);
        Assert.Equal(HostReservationStatus.ForeignOwner, foreign.Status);
        Assert.Equal("DOMAIN\\other", foreign.Owner);
    }

    [Fact]
    public void PersistedLanScopeOnlyAppliesToItsConfiguredPort()
    {
        var service = new HostAccessService(action => action(), _ => { });

        service.RestorePersistedState(allowLan: true, port: 8093);

        Assert.True(service.IsLanAppliedForPort(8093));
        Assert.False(service.IsLanAppliedForPort(8094));

        service.RestorePersistedState(allowLan: false, port: 8093);

        Assert.False(service.IsLanAppliedForPort(8093));
    }

    [Fact]
    public void FirewallScopeDefaultsToTailscaleAndAddsLanOnlyWhenRequested()
    {
        Assert.Equal(
            HostAccessService.TailscaleRange,
            HostAccessService.FirewallRemoteIp(allowLan: false));
        Assert.Equal(
            $"{HostAccessService.TailscaleRange},{HostAccessService.LanRanges}",
            HostAccessService.FirewallRemoteIp(allowLan: true));
        Assert.Equal(
            HostAccessService.TailscaleRange,
            HostAccessService.ResolveRollbackFirewallRemoteIp(null));
        Assert.Equal(
            "100.64.0.0/10,192.168.0.0/16",
            HostAccessService.ResolveRollbackFirewallRemoteIp(
                "100.64.0.0/10,192.168.0.0/16"));
    }
}
