using Cavdeck.Infrastructure.Updates;
using Xunit;

namespace Cavdeck.Infrastructure.Tests;

public sealed class ApplicationActivityGateTests
{
    [Fact]
    public void CliLeasesCanCoexistButPreventAnUpdateLease()
    {
        var root = Directory.CreateTempSubdirectory("cavdeck-activity-gate-").FullName;
        try
        {
            var gate = new FileApplicationActivityGate(Path.Combine(root, "application.lock"));
            using var firstCli = gate.AcquireCliLease();
            using var secondCli = gate.AcquireCliLease();

            Assert.Null(gate.TryAcquireUpdateLease());

            firstCli.Dispose();
            Assert.Null(gate.TryAcquireUpdateLease());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UpdateLeasePreventsNewCliProcessesUntilReleased()
    {
        var root = Directory.CreateTempSubdirectory("cavdeck-activity-gate-").FullName;
        try
        {
            var gate = new FileApplicationActivityGate(Path.Combine(root, "application.lock"));
            using (var update = gate.TryAcquireUpdateLease())
            {
                Assert.NotNull(update);
                Assert.Throws<ApplicationUpdateInProgressException>(() => gate.AcquireCliLease());
            }

            using var cli = gate.AcquireCliLease();
            Assert.Null(gate.TryAcquireUpdateLease());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PendingUpdateMarkerBlocksCliAfterDesktopProcessReleasesItsLease()
    {
        var root = Directory.CreateTempSubdirectory("cavdeck-activity-gate-").FullName;
        try
        {
            var gate = new FileApplicationActivityGate(Path.Combine(root, "application.lock"));
            using (var update = gate.TryAcquireUpdateLease())
            {
                Assert.NotNull(update);
                gate.MarkUpdatePending("1.2.3");
            }

            Assert.Throws<ApplicationUpdateInProgressException>(() => gate.AcquireCliLease());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PendingUpdateMarkerIsReconciledAgainstTheInstalledVersion()
    {
        var root = Directory.CreateTempSubdirectory("cavdeck-activity-gate-").FullName;
        try
        {
            var gate = new FileApplicationActivityGate(Path.Combine(root, "application.lock"));
            using (var update = gate.TryAcquireUpdateLease())
            {
                Assert.NotNull(update);
                gate.MarkUpdatePending("1.2.3");
            }

            gate.ReconcileUpdatePending("1.2.2", hasPendingUpdate: true);
            Assert.Throws<ApplicationUpdateInProgressException>(() => gate.AcquireCliLease());

            gate.ReconcileUpdatePending("1.2.3.0+commit", hasPendingUpdate: true);
            using var cli = gate.AcquireCliLease();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StalePendingUpdateMarkerIsClearedWhenVelopackHasNoPendingUpdate()
    {
        var root = Directory.CreateTempSubdirectory("cavdeck-activity-gate-").FullName;
        try
        {
            var gate = new FileApplicationActivityGate(Path.Combine(root, "application.lock"));
            using (var update = gate.TryAcquireUpdateLease())
            {
                Assert.NotNull(update);
                gate.MarkUpdatePending("1.2.3");
            }

            gate.ReconcileUpdatePending("1.2.2", hasPendingUpdate: false);
            using var cli = gate.AcquireCliLease();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
