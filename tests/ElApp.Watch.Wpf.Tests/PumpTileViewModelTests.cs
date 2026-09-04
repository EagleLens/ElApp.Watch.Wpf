using ElApp.Watch.Domain;
using ElApp.Watch.Wpf.ViewModels;
using Xunit;

namespace ElApp.Watch.Wpf.Tests;

/// <summary>
/// Verifies PumpTileViewModel.ResolveSnapshotStatus - the decision behind LastSnapshotStatus, the
/// source for MainViewModel's single-line status bar. Tested as the pure static method directly rather
/// than via a constructed PumpTileViewModel: its constructor builds a real placeholder BitmapImage from
/// a pack://application:,,,/ URI, which needs a live WPF resource-loading context
/// (Application.ResourceAssembly pointed at the assembly that actually embeds Assets/*.jpg) that a plain
/// test host doesn't have and can't reliably fake - Application.ResourceAssembly gets defaulted to the
/// entry assembly (the test host) the instant System.Windows.Application is touched at all, before any
/// override can run.
/// </summary>
public class PumpTileViewModelTests
{
    [Fact]
    public void ResolveSnapshotStatus_returns_a_message_for_TakingPhoto()
    {
        Assert.Equal("Pump 3 - Taking photo", PumpTileViewModel.ResolveSnapshotStatus(pumpNumber: 3, PumpState.TakingPhoto));
    }

    [Theory]
    [InlineData(PumpState.Empty)]
    [InlineData(PumpState.VehicleComing)]
    [InlineData(PumpState.VehicleStopping)]
    [InlineData(PumpState.VehicleStopped)]
    public void ResolveSnapshotStatus_returns_null_for_every_other_state(PumpState state)
    {
        Assert.Null(PumpTileViewModel.ResolveSnapshotStatus(pumpNumber: 1, state));
    }
}
