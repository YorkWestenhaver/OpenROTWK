// R15 packet 3 ("one clock") gate tests for AttributeModifier
// (workbench research/design-sim-presentation-bridge.md §2 packet 3).
//
// Attribute modifiers were the last sim state expiring against a wall-clock TimeInterval: a
// buff's lifetime was "map-timer milliseconds", so how many logic frames it covered depended
// on the host's frame pacing. These tests pin the replacement: both deadlines (Duration and
// the ModifierUpgrade Delay) are LogicFrames, quantized once at Apply with the same
// ceil(ms / MsPerLogicFrame) rule every other duration field in the engine uses.
//
// Two levels, deliberately:
//   * the quantization/predicate facts, driven straight against AttributeModifier at explicit
//     frame numbers, so an off-by-one in the boundary is visible without a frame loop;
//   * the whole per-frame path, driven as a real frame drives it - GameLogic.Update() to
//     advance the logic clock (the ModuleUpdate phase), then GameObject.LogicTick(), which is
//     exactly what Scene3D.SimObjectTick calls per object at the head of PartitionUpdate.
//
// Render-free: the host is HeadlessSimGame, the definitions are parsed from INI text through
// the real parser, and no test here touches a GraphicsDevice.

using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Ticking;
using Xunit;

namespace OpenSage.Tests.Logic;

public class ModifierListFrameTests
{
    // Bfme2 runs at 5 Hz, so MsPerLogicFrame is 200 (SageGameExtensions.MsPerLogicFrame,
    // SimLoop.MsPerLogicFrame). Every Duration below is chosen against that: 600 -> 3 frames,
    // 200 -> exactly 1, 201 -> 2 (ceil), 1 -> 1 (ceil, must not round away to nothing).
    private const string Definitions = @"
Upgrade Upgrade_DelayedA
  Type = PLAYER
End

Upgrade Upgrade_DelayedB
  Type = PLAYER
End

ModifierList ThreeFrameBuff
  Category = LEADERSHIP
  Modifier = ARMOR 25%
  Duration = 600
End

ModifierList OneFrameBuff
  Category = LEADERSHIP
  Modifier = ARMOR 25%
  Duration = 200
End

ModifierList JustOverOneFrameBuff
  Category = LEADERSHIP
  Modifier = ARMOR 25%
  Duration = 201
End

ModifierList SubFrameBuff
  Category = LEADERSHIP
  Modifier = ARMOR 25%
  Duration = 1
End

ModifierList PermanentBuff
  Category = LEADERSHIP
  Modifier = ARMOR 25%
End

ModifierList TwoDelayedUpgrades
  Category = LEADERSHIP
  Modifier = ARMOR 25%
  Upgrade = Upgrade_DelayedA Upgrade_DelayedB Delay:600
End

Object Grunt
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xC10C4)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static GameObject NewObject(HeadlessSimGame game) =>
        game.SpawnObject("Grunt", game.CivilianPlayer, new Vector3(0, 0, 0));

    private static AttributeModifier Grant(HeadlessSimGame game, GameObject obj, string modifierListName)
    {
        var modifier = new AttributeModifier(game.AssetStore.ModifierLists.GetByName(modifierListName));
        obj.AddAttributeModifier(modifierListName, modifier);
        return modifier;
    }

    /// <summary>
    /// One whole logic frame as the loop runs it, for one object: the ModuleUpdate phase
    /// advances the logic clock, then the head of PartitionUpdate ticks the object. Splitting
    /// it any other way would test a frame shape the engine does not have.
    /// </summary>
    private static void StepFrame(HeadlessSimGame game, GameObject obj)
    {
        game.Step();
        obj.LogicTick();
    }

    // ------------------------------------------------------------------- quantization at Apply

    [Theory]
    // Duration ms, expected lifetime in whole logic frames at 5 Hz (ceil(ms / 200)).
    [InlineData(600, 3)]
    [InlineData(200, 1)]
    [InlineData(201, 2)]
    [InlineData(1, 1)]
    public void DurationQuantizesUpToWholeLogicFrames(int durationMs, uint expectedFrames)
    {
        var name = durationMs switch
        {
            600 => "ThreeFrameBuff",
            200 => "OneFrameBuff",
            201 => "JustOverOneFrameBuff",
            _ => "SubFrameBuff",
        };

        var game = NewGame();
        var obj = NewObject(game);
        var modifier = Grant(game, obj, name);

        var applyFrame = new LogicFrame(40);
        modifier.Apply(obj, game.GameEngine, applyFrame);

        // Live for every frame up to and including applyFrame + expectedFrames...
        for (var i = 0u; i <= expectedFrames; i++)
        {
            Assert.False(
                modifier.Expired(new LogicFrame(applyFrame.Value + i)),
                $"{name} must still be live at +{i} frames");
        }

        // ...and expired on the next one. A sub-frame Duration therefore still buys a whole
        // frame rather than rounding away to nothing, which is the point of the ceil.
        Assert.True(modifier.Expired(new LogicFrame(applyFrame.Value + expectedFrames + 1)));
    }

    [Fact]
    public void ExpiryIsMeasuredFromTheApplyFrameNotFromZero()
    {
        // The deadline is relative: the same modifier applied 1000 frames in lasts exactly as
        // long as one applied at frame 0. Under the old wall-clock deadline this was the case
        // only if the map timer and the frame counter had not drifted apart.
        var game = NewGame();
        var early = NewObject(game);
        var late = NewObject(game);

        var earlyModifier = Grant(game, early, "ThreeFrameBuff");
        var lateModifier = Grant(game, late, "ThreeFrameBuff");

        earlyModifier.Apply(early, game.GameEngine, LogicFrame.Zero);
        lateModifier.Apply(late, game.GameEngine, new LogicFrame(1000));

        Assert.False(earlyModifier.Expired(new LogicFrame(3)));
        Assert.True(earlyModifier.Expired(new LogicFrame(4)));

        Assert.False(lateModifier.Expired(new LogicFrame(1003)));
        Assert.True(lateModifier.Expired(new LogicFrame(1004)));
    }

    [Fact]
    public void AModifierWithNoDurationNeverExpires()
    {
        var game = NewGame();
        var obj = NewObject(game);
        var modifier = Grant(game, obj, "PermanentBuff");

        modifier.Apply(obj, game.GameEngine, LogicFrame.Zero);

        Assert.False(modifier.Expired(LogicFrame.Zero));
        Assert.False(modifier.Expired(new LogicFrame(100_000)));
        Assert.False(modifier.Expired(LogicFrame.MaxValue));
    }

    // ------------------------------------------------------------ the whole per-frame path

    [Fact]
    public void ASelfExpiringModifierIsEvictedOnItsExpiryFrameAndNotBefore()
    {
        var game = NewGame();
        var obj = NewObject(game);
        Grant(game, obj, "ThreeFrameBuff");

        // Frame 1 is the apply frame (the grant above happened before any frame ran), so the
        // deadline is frame 4 and the sweep evicts on frame 5.
        StepFrame(game, obj);
        Assert.Equal(1u, game.GameLogic.CurrentFrame.Value);
        Assert.True(obj.HasAttributeModifier("ThreeFrameBuff"));

        for (var frame = 2u; frame <= 4u; frame++)
        {
            StepFrame(game, obj);
            Assert.Equal(frame, game.GameLogic.CurrentFrame.Value);
            Assert.True(
                obj.HasAttributeModifier("ThreeFrameBuff"),
                $"a 3-frame buff applied on frame 1 must still be live on frame {frame}");
        }

        StepFrame(game, obj);
        Assert.Equal(5u, game.GameLogic.CurrentFrame.Value);
        Assert.False(obj.HasAttributeModifier("ThreeFrameBuff"));
    }

    [Fact]
    public void APermanentModifierSurvivesEveryFrame()
    {
        var game = NewGame();
        var obj = NewObject(game);
        Grant(game, obj, "PermanentBuff");

        for (var i = 0; i < 20; i++)
        {
            StepFrame(game, obj);
            Assert.True(obj.HasAttributeModifier("PermanentBuff"));
        }
    }

    [Fact]
    public void ARevokedModifierIsEvictedOnTheNextFrameRegardlessOfDuration()
    {
        // Invalid is checked alongside Expired in the same sweep, so a revoke still evicts on
        // the following tick - unchanged by the clock swap, pinned here because the two
        // branches now read different inputs.
        var game = NewGame();
        var obj = NewObject(game);
        Grant(game, obj, "PermanentBuff");

        StepFrame(game, obj);
        Assert.True(obj.HasAttributeModifier("PermanentBuff"));

        obj.RemoveAttributeModifier("PermanentBuff");
        Assert.False(obj.HasAttributeModifier("PermanentBuff"));

        StepFrame(game, obj);

        // And the dictionary entry itself is gone, so the name can be granted again.
        Grant(game, obj, "PermanentBuff");
        StepFrame(game, obj);
        Assert.True(obj.HasAttributeModifier("PermanentBuff"));
    }

    // ------------------------------------------------------------------- delayed upgrades

    [Fact]
    public void DelayedUpgradesAllActivateOnTheSameLogicFrame()
    {
        // Delay:600 -> 3 frames, granted on the frame AFTER the deadline (the predicate is
        // strictly greater-than, as the wall-clock one was). Both upgrades share the deadline,
        // so both must land on the same frame: the pre-packet-3 forward loop with RemoveAt
        // skipped an entry per grant and would land the second one a frame late.
        var game = NewGame();
        var obj = NewObject(game);
        Grant(game, obj, "TwoDelayedUpgrades");

        var upgradeA = game.AssetStore.Upgrades.GetByName("Upgrade_DelayedA");
        var upgradeB = game.AssetStore.Upgrades.GetByName("Upgrade_DelayedB");

        // Frame 1: applied, deadline is frame 4.
        for (var frame = 1u; frame <= 4u; frame++)
        {
            StepFrame(game, obj);
            Assert.False(obj.HasUpgrade(upgradeA), $"frame {frame}");
            Assert.False(obj.HasUpgrade(upgradeB), $"frame {frame}");
        }

        StepFrame(game, obj);

        Assert.True(obj.HasUpgrade(upgradeA));
        Assert.True(obj.HasUpgrade(upgradeB));
    }
}
