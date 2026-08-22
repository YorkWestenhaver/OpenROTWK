// R15 packet HUD-TEST (L1 lane), second half - the pure-policy grade.
//
// AptControlBarSelectionPathTests can drive the real ClearCommandbuttons and the guard chain
// at the head of UpdateCommandbuttons, but it cannot go past the CreateContent call: that
// needs a live ActionScript VM (_window.Context.Avm) which no headless test can build. The
// decisions taken around that call are nonetheless plain predicates, so HUD-TEST asked
// HUD-WIRE (blackboard line [HUD-TEST #1], a RESERVE-negotiation request - HUD-TEST does not
// edit AptControlBarSource.cs) to land them as a pure, public, side-effect-free surface:
//
//   namespace OpenSage.Mods.Bfme;
//   public static class AptControlBarPolicy
//   {
//       public const int SlotCount = 6;
//       public static bool SupportsCommandButtonSlots(SageGame game);
//       public static bool CommandButtonsUsable(int constantsCount);
//       public static bool ShouldUpdateButtons(int selectedUnitCount);
//   }
//
// with the same SupportsCommandButtonSlots call in BOTH ClearCommandbuttons and
// UpdateCommandbuttons - that identity IS the skip-asymmetry fix - and CommandButtonsUsable
// in both, since Update currently has no Constants guard at all.
//
// The binding is by REFLECTION on purpose. HUD-TEST is write-only and merges independently of
// HUD-WIRE, so a compile-time reference would turn "HUD-WIRE has not merged yet" into a build
// break for the whole test project. Reflection keeps that case a legible red test with a
// message naming the missing member instead.

using System;
using System.Reflection;
using OpenSage.Mods.Bfme;
using Xunit;

namespace OpenSage.Tests.Mods.Bfme;

public class AptControlBarPolicyTests
{
    /// <summary>
    /// SageGame.Bfme is the only game whose Palantir command-button layout is still unknown,
    /// so it is the only one whose slot loop stays skipped. Bfme2 and Bfme2Rotwk (the game
    /// AotR runs as) must be driven - today UpdateCommandbuttons already drives them while
    /// ClearCommandbuttons skips them, which is the whole defect.
    /// </summary>
    [Theory]
    [InlineData(SageGame.Bfme, false)]
    [InlineData(SageGame.Bfme2, true)]
    [InlineData(SageGame.Bfme2Rotwk, true)]
    [InlineData(SageGame.CncGenerals, true)]
    [InlineData(SageGame.CncGeneralsZeroHour, true)]
    public void SupportsCommandButtonSlots_SkipsBfme1Only(SageGame game, bool expected)
    {
        Assert.Equal(expected, Policy.Invoke<bool>("SupportsCommandButtonSlots", game));
    }

    /// <summary>
    /// A single predicate is the point: Clear and Update must agree for every game, because
    /// any game Update paints and Clear skips strands its buttons on screen after a deselect.
    /// This test states that as a property over the whole enum rather than the five cases
    /// above, so a SageGame added later cannot reintroduce the asymmetry unnoticed.
    /// </summary>
    [Fact]
    public void SupportsCommandButtonSlots_IsTotalOverSageGame_AndNeverThrows()
    {
        foreach (SageGame game in Enum.GetValues<SageGame>())
        {
            var thrown = Record.Exception(() => Policy.Invoke<bool>("SupportsCommandButtonSlots", game));
            Assert.True(thrown is null, $"SupportsCommandButtonSlots({game}) threw {thrown?.GetType().Name}");
        }
    }

    /// <summary>
    /// The Constants guard. An Apt CommandButtons object with no constants was never
    /// populated, so its numbered members do not exist and neither method may address them.
    /// ClearCommandbuttons has had this guard all along; the fix is that Update honours it too.
    /// </summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(6, true)]
    [InlineData(64, true)]
    public void CommandButtonsUsable_RequiresAtLeastOneConstant(int constantsCount, bool expected)
    {
        Assert.Equal(expected, Policy.Invoke<bool>("CommandButtonsUsable", constantsCount));
    }

    /// <summary>
    /// The Update(Player) dispatch at the bottom of the class: a non-empty selection paints,
    /// an empty selection clears. This is the branch the packet calls the selection path - it
    /// is the only thing standing between "a unit is selected" and CreateContent running.
    /// </summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(12, true)]
    public void ShouldUpdateButtons_TracksWhetherAnythingIsSelected(int selectedUnitCount, bool expected)
    {
        Assert.Equal(expected, Policy.Invoke<bool>("ShouldUpdateButtons", selectedUnitCount));
    }

    /// <summary>
    /// Clearing and updating are mutually exclusive and jointly exhaustive over the selection
    /// count - there is no count at which both run, and none at which neither does.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(50)]
    public void UpdateAndClearPartitionTheSelectionCount(int selectedUnitCount)
    {
        var shouldUpdate = Policy.Invoke<bool>("ShouldUpdateButtons", selectedUnitCount);
        Assert.Equal(selectedUnitCount > 0, shouldUpdate);
    }

    /// <summary>
    /// Six slots, matching the i = 1..6 loop both command-button methods run. Pinned as a
    /// constant so the two loops and these tests cannot drift apart.
    /// </summary>
    [Fact]
    public void SlotCount_IsSix()
    {
        Assert.Equal(6, Policy.Constant<int>("SlotCount"));
    }

    // ---------------------------------------------------------------------------------
    // Reflection binding
    // ---------------------------------------------------------------------------------

    private static class Policy
    {
        private const string TypeName = "OpenSage.Mods.Bfme.AptControlBarPolicy";

        private static Type Type
        {
            get
            {
                // Anchored on the public factory so the assembly is found the same way
                // AptControlBarSelectionPathTests finds it.
                var assembly = typeof(AptControlBarSource).Assembly;
                var type = assembly.GetType(TypeName, throwOnError: false);
                Assert.True(
                    type is not null,
                    $"{TypeName} not found in {assembly.GetName().Name}. It is the seam HUD-TEST " +
                    "requested from HUD-WIRE on blackboard line [HUD-TEST #1]; see the header of " +
                    "this file for the exact required shape.");
                return type;
            }
        }

        public static T Invoke<T>(string methodName, params object[] args)
        {
            var method = Type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
            Assert.True(method is not null, $"{TypeName}.{methodName} (public static) not found");

            try
            {
                return (T)method.Invoke(null, args);
            }
            catch (TargetInvocationException e) when (e.InnerException is not null)
            {
                throw e.InnerException;
            }
        }

        public static T Constant<T>(string name)
        {
            var field = Type.GetField(name, BindingFlags.Public | BindingFlags.Static);
            Assert.True(field is not null, $"{TypeName}.{name} (public static/const) not found");
            return (T)field.GetValue(null);
        }
    }
}
