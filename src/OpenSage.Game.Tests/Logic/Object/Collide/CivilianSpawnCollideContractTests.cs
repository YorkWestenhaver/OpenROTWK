// Contract tests for the CivilianSpawnCollide port (R13; task packet civilian-spawn-collide):
// a generic filtered-delete-on-collide module - on OnCollide, destroys `other` iff
// DeleteObjectFilter matches it, guarded against a null/already-destroyed `other`.
//
// No GPL ancestor exists for this module (see the R13 spec's §0); the behavior pinned here is
// the data-derivation read of the module's single field against the frozen ICollideModule
// contract, not a GPL translation.
//
// Test-idiom note: HeadlessSimGame.Step() (Logic/Sim/HeadlessSimGame.cs:156-160) does not call
// PartitionCellManager.Update() (that only runs in the real Game.cs:871 loop), so - exactly like
// the landed UnitCrateCollideContractTests and SabotageSupplyCenterCrateCollideContractTests -
// these tests invoke GameObject.OnCollide(other) directly rather than relying on Step()-driven
// spatial overlap detection.

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.Tests.Logic.Object;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Collide;

public class CivilianSpawnCollideContractTests
{
    private const string Definitions = @"
Object Deleter
  KindOf = NO_COLLIDE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = CivilianSpawnCollide ModuleTag_Collide
    DeleteObjectFilter = NONE +INFANTRY
  End
End

Object DeleterUnsetFilter
  KindOf = NO_COLLIDE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = CivilianSpawnCollide ModuleTag_Collide
  End
End

Object DeleterTwoMatchingModules
  KindOf = NO_COLLIDE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = CivilianSpawnCollide ModuleTag_CollideFirst
    DeleteObjectFilter = NONE +INFANTRY
  End
  Behavior = CivilianSpawnCollide ModuleTag_CollideSecond
    DeleteObjectFilter = NONE +INFANTRY
  End
End

Object TargetInfantry
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1
  End
End

Object NonTargetInfantry
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xC5C0) // "csc0"
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static readonly Vector3 SomePosition = new(100, 100, 0);

    private static CivilianSpawnCollide ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<CivilianSpawnCollide>().Single();

    // §1 step 3: a matching candidate is destroyed on collide.
    [Fact]
    public void MatchingCandidate_DeletedOnCollide()
    {
        var game = NewGame();
        var deleter = game.SpawnObject("Deleter", game.CivilianPlayer, SomePosition);
        var civilian = game.SpawnObject("TargetInfantry", game.CivilianPlayer, SomePosition);

        deleter.OnCollide(civilian);

        Assert.True(civilian.IsDestroyed);
    }

    // §1 step 2: a non-matching candidate (fails the filter) is left alone.
    [Fact]
    public void NonMatchingCandidate_NotDeleted()
    {
        var game = NewGame();
        var deleter = game.SpawnObject("Deleter", game.CivilianPlayer, SomePosition);
        var nonCivilian = game.SpawnObject("NonTargetInfantry", game.CivilianPlayer, SomePosition);

        deleter.OnCollide(nonCivilian);

        Assert.False(nonCivilian.IsDestroyed);
    }

    // No collision fired at all -> the module is inert (no polling/scanning of its own, unlike
    // an Update module - it only ever acts inside an actual OnCollide call).
    [Fact]
    public void NoCollideCall_NoEffect()
    {
        var game = NewGame();
        var deleter = game.SpawnObject("Deleter", game.CivilianPlayer, SomePosition);
        var civilian = game.SpawnObject("TargetInfantry", game.CivilianPlayer, new Vector3(9999, 9999, 0));

        for (var i = 0; i < 3; i++)
        {
            game.Step();
        }

        Assert.False(civilian.IsDestroyed);
        Assert.False(deleter.IsDestroyed);
    }

    // §1 step 2's "absent filter never matches" reading: DeleteObjectFilter omitted on the
    // template -> no-op, not a universal match.
    [Fact]
    public void UnsetFilter_NoOpDelete()
    {
        var game = NewGame();
        var deleter = game.SpawnObject("DeleterUnsetFilter", game.CivilianPlayer, SomePosition);
        var civilian = game.SpawnObject("TargetInfantry", game.CivilianPlayer, SomePosition);

        var ex = Record.Exception(() => deleter.OnCollide(civilian));

        Assert.Null(ex);
        Assert.False(civilian.IsDestroyed);
    }

    // §1 step 1: GameObject.OnCollide (GameObject.cs:1069-1077) runs every collide module on the
    // object in sequence for the SAME event. Two CivilianSpawnCollide modules on one object,
    // both matching the same candidate, must not double-fault: the first module's OnCollide
    // destroys the candidate, and the second module's OnCollide call - for the identical
    // `other` - must see other.IsDestroyed and no-op rather than throwing or attempting a
    // second DestroyObject.
    [Fact]
    public void ReciprocalOnCollide_BothSidesFireSafely()
    {
        var game = NewGame();
        var deleter = game.SpawnObject("DeleterTwoMatchingModules", game.CivilianPlayer, SomePosition);
        var civilian = game.SpawnObject("TargetInfantry", game.CivilianPlayer, SomePosition);

        var ex = Record.Exception(() => deleter.OnCollide(civilian));

        Assert.Null(ex);
        Assert.True(civilian.IsDestroyed);
    }

    // Direct unit-level pin of the other.IsDestroyed guard, independent of the reciprocal-pair
    // setup above: `other` was already destroyed by an unrelated cause before this module's
    // OnCollide runs for it.
    [Fact]
    public void AlreadyDestroyedOther_NoOp()
    {
        var game = NewGame();
        var deleter = game.SpawnObject("Deleter", game.CivilianPlayer, SomePosition);
        var civilian = game.SpawnObject("TargetInfantry", game.CivilianPlayer, SomePosition);

        game.GameLogic.DestroyObject(civilian);
        game.Step(); // reap, matching HeadlessSimGame.Step()'s own DeleteDestroyed() half

        var ex = Record.Exception(() => deleter.OnCollide(civilian));

        Assert.Null(ex);
    }

    // api-freeze-v1 §6 fitness item 3: shadow-copy base test. The module carries no own state
    // (only a readonly ModuleData reference), so this pins that the inherited Load/base walk
    // alone stays consistent - including across a real destroy-triggering collide.
    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_AroundCollide()
    {
        var game = NewGame();
        var deleter = game.SpawnObject("Deleter", game.CivilianPlayer, SomePosition);
        var civilian = game.SpawnObject("TargetInfantry", game.CivilianPlayer, SomePosition);
        var live = ModuleOf(deleter);

        deleter.OnCollide(civilian);
        Assert.True(civilian.IsDestroyed);

        var shadowHost = game.SpawnObject("Deleter", game.CivilianPlayer, new Vector3(300, 0, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    // Save/load round-trip: the module's own Xfer state (none) must not desync the pending
    // outcome of a collide sequenced around the round-trip.
    [Fact]
    public void SaveLoadRoundTrip_AcrossCollideFrame()
    {
        var trajectoryA = RunScenario(roundTrip: false);
        var trajectoryB = RunScenario(roundTrip: true);
        Assert.Equal(trajectoryA, trajectoryB);
    }

    private static bool RunScenario(bool roundTrip)
    {
        var game = NewGame(seed: 0xF00D);
        var deleter = game.SpawnObject("Deleter", game.CivilianPlayer, SomePosition);
        var civilian = game.SpawnObject("TargetInfantry", game.CivilianPlayer, SomePosition);
        var module = ModuleOf(deleter);

        if (roundTrip)
        {
            var state = PortedModuleTestKit.Save(module);
            PortedModuleTestKit.Load(module, state);
        }

        deleter.OnCollide(civilian);
        return civilian.IsDestroyed;
    }
}
