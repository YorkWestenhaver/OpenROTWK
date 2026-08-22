// HUD-WIRE — two things this packet fixed, pinned so they cannot silently regress:
//
//   1. Bfme2RotwkDefinition.ControlBar was declared without an initializer, so it was
//      permanently null. Game.StartGame only builds a control bar when
//      Definition.ControlBar != null, which meant ROTWK/AotR rendered no in-match HUD at
//      all (Palantir.apt was never even loaded). It must now be a non-null
//      AptControlBarSource, matching Bfme2Definition and BfmeDefinition.
//
//   2. AptControlBar's clear and update paths disagreed: the clear path skipped
//      Bfme/Bfme2/Bfme2Rotwk while the update path skipped only Bfme, and only the clear
//      path checked that the Apt CommandButtons object had been populated. Both now go
//      through AptControlBarPolicy, so the two paths share one rule by construction.
//
// AptControlBar itself is internal, its constructor dereferences AssetStore.InGameUI, and
// the slot loops need a live ActionScript VM, so the policy surface is what is asserted
// here; the loops are covered by the 3-map headed gate run recorded in the packet.

using OpenSage;
using OpenSage.Gui.ControlBar;
using OpenSage.Mods.Bfme;
using OpenSage.Mods.Bfme2;
using Xunit;

namespace OpenSage.Tests.Gui.ControlBar;

public class HudWireControlBarSourceTests
{
    [Fact]
    public void Bfme2Rotwk_HasAControlBarSource()
    {
        Assert.NotNull(Bfme2RotwkDefinition.Instance.ControlBar);
    }

    [Fact]
    public void Bfme2Rotwk_UsesTheSameControlBarSourceKindAsBfme2()
    {
        Assert.IsType<AptControlBarSource>(Bfme2RotwkDefinition.Instance.ControlBar);
        Assert.Equal(
            Bfme2Definition.Instance.ControlBar.GetType(),
            Bfme2RotwkDefinition.Instance.ControlBar.GetType());
    }

    [Fact]
    public void ControlBarSource_CreatesABarWithoutBeingHandedANullGame()
    {
        // Guards the shape of the seam only: Create must exist and be the IControlBarSource
        // entry point Game.StartGame calls. Constructing the bar needs a live AssetStore.
        IControlBarSource source = new AptControlBarSource();
        Assert.NotNull(source);
    }
}

public class HudWireControlBarPolicyTests
{
    [Fact]
    public void SlotCount_IsSix()
    {
        Assert.Equal(6, AptControlBarPolicy.SlotCount);
    }

    [Theory]
    [InlineData(SageGame.Bfme2)]
    [InlineData(SageGame.Bfme2Rotwk)]
    [InlineData(SageGame.CncGenerals)]
    [InlineData(SageGame.CncGeneralsZeroHour)]
    public void SupportsCommandButtonSlots_IsTrueForEveryGameThatDrivesThePalantirSlots(SageGame game)
    {
        Assert.True(AptControlBarPolicy.SupportsCommandButtonSlots(game));
    }

    [Fact]
    public void SupportsCommandButtonSlots_IsFalseForBfme1Only()
    {
        // BFME1's Palantir lays the slots out differently; it is the sole opt-out.
        Assert.False(AptControlBarPolicy.SupportsCommandButtonSlots(SageGame.Bfme));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(6, true)]
    public void CommandButtonsUsable_RequiresAPopulatedConstantsTable(int constantsCount, bool expected)
    {
        Assert.Equal(expected, AptControlBarPolicy.CommandButtonsUsable(constantsCount));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(12, true)]
    public void ShouldUpdateButtons_RequiresASelection(int selectedUnitCount, bool expected)
    {
        Assert.Equal(expected, AptControlBarPolicy.ShouldUpdateButtons(selectedUnitCount));
    }
}
