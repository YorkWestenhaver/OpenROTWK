// A0-prime (closes F-EMP-6/F-RING-5/F-LDB-3): GameObject.Update() (the healer timeout and the
// DisabledType auto-expiry sweep, CheckDisabledStates) had zero callers anywhere in the tree
// until GameLogic.Update() wired it in, once per object per frame. These tests exercise that
// wiring directly against GameObject.Disable/IsDisabledByType rather than through any one
// module's own port, so they cover the framework-level fix independent of any single module's
// own coverage of it (see EmpUpdateContractTests/OneRingPenaltyUpdateContractTests/
// LeafletDropBehaviorContractTests for the module-level ports that consume this fix).
//
// Frame accounting: GameLogic.Update() runs this sweep AFTER incrementing its frame counter
// (unlike the sleepy-module/pathfind/player/partition passes earlier in the same method, which
// all read the pre-increment "now"), so a disable window of T frames (expiry frame =
// disable-time CurrentFrame + T) still reads as disabled through CurrentFrame == expiry-frame
// and only reads as cleared once CurrentFrame first exceeds it, i.e. at expiry-frame + 1 - "a
// T-frame window clears at T+1".

using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Ticking;
using Xunit;

namespace OpenSage.Tests.Logic.Object;

public class GameObjectDisabledExpiryContractTests
{
    private const string Definitions = @"
Object Paralyzable
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xD15AB1E) // "disable"
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    [Fact]
    public void TimedDisable_StaysActiveThroughExpiryFrame_ThenAutoClearsAtTPlus1()
    {
        var game = NewGame();
        var unit = game.SpawnObject("Paralyzable", game.CivilianPlayer, Vector3.Zero);

        // Disable for a 3-frame window from the current (frame 0) CurrentFrame.
        const uint windowLength = 3;
        var expiryFrame = game.GameLogic.CurrentFrame + new LogicFrameSpan(windowLength);
        unit.Disable(DisabledType.Paralyzed, expiryFrame);

        Assert.True(unit.IsDisabledByType(DisabledType.Paralyzed));

        // CurrentFrame 1..3 (== the expiry frame itself): still disabled.
        for (var i = 0; i < windowLength; i++)
        {
            game.Step();
            Assert.True(unit.IsDisabledByType(DisabledType.Paralyzed));
        }

        // CurrentFrame 4 == T+1: the auto-expiry sweep clears it.
        game.Step();
        Assert.False(unit.IsDisabledByType(DisabledType.Paralyzed));
    }

    [Fact]
    public void TimedDisable_MidGame_ClearsRelativeToItsOwnStartingFrame_NotFrameZero()
    {
        var game = NewGame();
        var unit = game.SpawnObject("Paralyzable", game.CivilianPlayer, Vector3.Zero);

        // Advance a few frames before disabling, so the expiry frame is not the naive
        // T-frames-from-zero a placement bug could accidentally satisfy.
        for (var i = 0; i < 5; i++)
        {
            game.Step();
        }

        const uint windowLength = 2;
        var expiryFrame = game.GameLogic.CurrentFrame + new LogicFrameSpan(windowLength);
        unit.Disable(DisabledType.Paralyzed, expiryFrame);
        Assert.True(unit.IsDisabledByType(DisabledType.Paralyzed));

        // T frames measured relative to the disable call, regardless of the absolute frame it
        // happened on: windowLength Step()s still disabled, the (windowLength+1)-th clears.
        for (var i = 0; i < windowLength; i++)
        {
            game.Step();
            Assert.True(unit.IsDisabledByType(DisabledType.Paralyzed));
        }

        game.Step(); // T+1 steps after the disable call: clears
        Assert.False(unit.IsDisabledByType(DisabledType.Paralyzed));
    }

    [Fact]
    public void MultipleDisabledTypes_ExpireIndependently()
    {
        var game = NewGame();
        var unit = game.SpawnObject("Paralyzable", game.CivilianPlayer, Vector3.Zero);

        var shortExpiry = game.GameLogic.CurrentFrame + new LogicFrameSpan(1);
        var longExpiry = game.GameLogic.CurrentFrame + new LogicFrameSpan(4);
        unit.Disable(DisabledType.Paralyzed, shortExpiry);
        unit.Disable(DisabledType.Emp, longExpiry);

        game.Step(); // CurrentFrame 1 == shortExpiry: both still active
        Assert.True(unit.IsDisabledByType(DisabledType.Paralyzed));
        Assert.True(unit.IsDisabledByType(DisabledType.Emp));

        game.Step(); // CurrentFrame 2 == shortExpiry + 1: Paralyzed clears, Emp does not
        Assert.False(unit.IsDisabledByType(DisabledType.Paralyzed));
        Assert.True(unit.IsDisabledByType(DisabledType.Emp));

        for (var i = 0; i < 3; i++)
        {
            game.Step();
        }
        Assert.False(unit.IsDisabledByType(DisabledType.Emp)); // CurrentFrame 5 == longExpiry + 1
    }

    [Fact]
    public void UndisabledObject_NeverReportsDisabled_AcrossManyFrames()
    {
        // Sanity: wiring GameObject.Update() into every frame must not spuriously disable an
        // object that was never disabled in the first place.
        var game = NewGame();
        var unit = game.SpawnObject("Paralyzable", game.CivilianPlayer, Vector3.Zero);

        for (var i = 0; i < 10; i++)
        {
            game.Step();
            Assert.False(unit.IsDisabledByType(DisabledType.Paralyzed));
            Assert.False(unit.IsDisabledByType(DisabledType.Emp));
        }
    }
}
