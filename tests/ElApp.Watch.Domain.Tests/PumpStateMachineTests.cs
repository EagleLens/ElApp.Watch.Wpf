using ElApp.Watch.Domain;
using Xunit;

namespace ElApp.Watch.Domain.Tests;

/// <summary>
/// Characterization tests locking in PumpStateMachine's existing stop-detection behavior
/// (proposal.md TC001-TC004) ahead of its relocation into ElApp.Watch.Domain and the MVVM
/// rewrite of the WPF UI that consumes it.
///
/// fps is fixed at 2.0 throughout so stopConfirmSeconds (2.5) and graceSeconds (1.5) convert
/// to exact frame counts (5 and 3 respectively) with no midpoint-rounding ambiguity.
/// </summary>
public class PumpStateMachineTests
{
    private const double Fps = 2.0; // requiredStillFrames = 5, requiredGraceFrames = 3

    private static string Frame() => "frame";

    [Fact]
    public void FullStopDetectClearCycle_FiresExactlyOnePhotoCaptured()
    {
        var machine = new PumpStateMachine<string>("pump-1", Fps);
        var statusHistory = new List<PumpState>();
        int photoCapturedCount = 0;
        machine.StatusChanged += (_, e) => statusHistory.Add(e.State);
        machine.PhotoCaptured += (_, _) => photoCapturedCount++;

        Assert.Equal(PumpState.Empty, machine.State);

        // Vehicle arrives, still moving -> Empty -> VehicleComing
        machine.Update(isPresent: true, isMoving: true, Frame);
        Assert.Equal(PumpState.VehicleComing, machine.State);

        // Vehicle stops moving -> VehicleComing -> VehicleStopping (stillFrameCount = 1)
        machine.Update(isPresent: true, isMoving: false, Frame);
        Assert.Equal(PumpState.VehicleStopping, machine.State);

        // Hold still for the remaining required frames (2, 3, 4, then 5 triggers the photo)
        machine.Update(isPresent: true, isMoving: false, Frame);
        machine.Update(isPresent: true, isMoving: false, Frame);
        machine.Update(isPresent: true, isMoving: false, Frame);
        Assert.Equal(PumpState.VehicleStopping, machine.State);
        Assert.Equal(0, photoCapturedCount);

        machine.Update(isPresent: true, isMoving: false, Frame); // 5th still frame
        Assert.Equal(PumpState.TakingPhoto, machine.State);
        Assert.Equal(1, photoCapturedCount);

        // One-tick pulse settles into VehicleStopped on the next frame
        machine.Update(isPresent: true, isMoving: false, Frame);
        Assert.Equal(PumpState.VehicleStopped, machine.State);

        Assert.Equal(1, photoCapturedCount);
        Assert.Equal(
            [PumpState.VehicleComing, PumpState.VehicleStopping, PumpState.TakingPhoto, PumpState.VehicleStopped],
            statusHistory);
    }

    [Fact]
    public void BriefOcclusionDuringVehicleComing_DoesNotResetToEmpty()
    {
        var machine = new PumpStateMachine<string>("pump-1", Fps);

        machine.Update(isPresent: true, isMoving: true, Frame);
        Assert.Equal(PumpState.VehicleComing, machine.State);

        // Absence for fewer frames than the grace period (3) must not reset to Empty.
        machine.Update(isPresent: false, isMoving: false, Frame);
        Assert.Equal(PumpState.VehicleComing, machine.State);
        machine.Update(isPresent: false, isMoving: false, Frame);
        Assert.Equal(PumpState.VehicleComing, machine.State);

        // Presence resumes before the grace period elapses.
        machine.Update(isPresent: true, isMoving: true, Frame);
        Assert.Equal(PumpState.VehicleComing, machine.State);
    }

    [Fact]
    public void SustainedAbsenceFromVehicleStopped_ClearsThePump()
    {
        var machine = new PumpStateMachine<string>("pump-1", Fps);
        DriveToVehicleStopped(machine);
        Assert.Equal(PumpState.VehicleStopped, machine.State);

        // Absence for at least the grace period (3 frames) clears the pump back to Empty.
        machine.Update(isPresent: false, isMoving: false, Frame);
        machine.Update(isPresent: false, isMoving: false, Frame);
        Assert.Equal(PumpState.VehicleStopped, machine.State); // still within grace
        machine.Update(isPresent: false, isMoving: false, Frame);
        Assert.Equal(PumpState.Empty, machine.State);

        // Counters must be reset - a fresh arrival starts a clean cycle, not a stale one.
        machine.Update(isPresent: true, isMoving: true, Frame);
        Assert.Equal(PumpState.VehicleComing, machine.State);
    }

    [Fact]
    public void NoSecondPhotoWithoutReturningThroughEmpty()
    {
        var machine = new PumpStateMachine<string>("pump-1", Fps);
        int photoCapturedCount = 0;
        machine.PhotoCaptured += (_, _) => photoCapturedCount++;

        DriveToVehicleStopped(machine);
        Assert.Equal(1, photoCapturedCount);

        // Keep the pump occupied (with some sub-threshold jitter in presence) without ever
        // letting it clear back to Empty - no second photo should fire.
        for (int i = 0; i < 10; i++)
        {
            machine.Update(isPresent: true, isMoving: true, Frame);
        }
        machine.Update(isPresent: false, isMoving: false, Frame);
        machine.Update(isPresent: true, isMoving: true, Frame); // presence resumes before grace elapses

        Assert.Equal(PumpState.VehicleStopped, machine.State);
        Assert.Equal(1, photoCapturedCount);
    }

    /// <summary>Drives a fresh machine through a full stop cycle up to and including VehicleStopped.</summary>
    private static void DriveToVehicleStopped(PumpStateMachine<string> machine)
    {
        machine.Update(isPresent: true, isMoving: true, Frame);   // -> VehicleComing
        machine.Update(isPresent: true, isMoving: false, Frame);  // -> VehicleStopping (count=1)
        machine.Update(isPresent: true, isMoving: false, Frame);  // count=2
        machine.Update(isPresent: true, isMoving: false, Frame);  // count=3
        machine.Update(isPresent: true, isMoving: false, Frame);  // count=4
        machine.Update(isPresent: true, isMoving: false, Frame);  // count=5 -> TakingPhoto (+photo)
        machine.Update(isPresent: true, isMoving: false, Frame);  // -> VehicleStopped
    }
}
