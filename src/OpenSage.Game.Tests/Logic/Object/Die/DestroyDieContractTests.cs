// Mocked-game unit tests for the DestroyDie port (experiment-round-4 §4.1, DoD item 4):
// one test per INI branch, each of the shape [create -> trigger death -> observable effect]
// using the batch's death-trigger helper, plus the shadow-copy base test taken mid-behavior
// and a mid-behavior save/load continuation.
//
// DestroyDie has no INI fields of its own, so "one per INI branch" means one per branch of
// the shared Die filter it inherits (DieLogicData): unfiltered, DeathTypes, RequiredStatus,
// ExemptStatus - plus the two behavioral edges the GPL reference implies (sub-lethal damage
// must not destroy; a second lethal blow must not re-destroy).
//
// Object definitions are parsed from INI text through the real parser, so the audited parse
// table is on the tested path.

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Sync;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Die;

public class DestroyDieContractTests
{
    private const string Definitions = @"
Object PlainVictim
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = DestroyDie ModuleTag_Die
  End
End

Object BurnOnlyVictim
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = DestroyDie ModuleTag_Die
    DeathTypes = NONE +BURNED
  End
End

Object UnderConstructionVictim
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = DestroyDie ModuleTag_Die
    RequiredStatus = UNDER_CONSTRUCTION
  End
End

Object NotWhileSoldVictim
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = DestroyDie ModuleTag_Die
    ExemptStatus = SOLD
  End
End

Object DoubleDestroyVictim
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = DestroyDie ModuleTag_DieA
  End
  Behavior = DestroyDie ModuleTag_DieB
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xD35u)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static DestroyDie DieModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<DestroyDie>().First();

    private static int ObjectCount(HeadlessSimGame game) => game.GameLogic.Objects.Count();

    // ---- INI branch: no filter at all (the 508-of-695 AotR shape) ----

    [Fact]
    public void UnfilteredDeath_DestroysTheObject()
    {
        var game = NewGame();
        var before = ObjectCount(game);

        var (victim, result) = PortedModuleTestKit.SpawnAndKill(
            game, "PlainVictim", game.CivilianPlayer, Vector3.Zero);

        Assert.True(result.Died);
        Assert.True(victim.IsDestroyed);
        Assert.True(victim.TestStatus(ObjectStatus.Destroyed));

        // ...and the object leaves the world at the end of the frame, not before: a module
        // walking ObjectsAscendingId later in the SAME frame still sees the corpse.
        Assert.Equal(before + 1, ObjectCount(game));
        game.Step();
        Assert.Equal(before, ObjectCount(game));
    }

    [Fact]
    public void EveryDeathTypeDestroys_WhenUnfiltered()
    {
        var game = NewGame();
        var deathTypes = new[]
        {
            DeathType.Normal, DeathType.None, DeathType.Crushed, DeathType.Burned,
            DeathType.Exploded, DeathType.Poisoned, DeathType.Toppled, DeathType.Suicided,
        };

        var x = 0f;
        foreach (var deathType in deathTypes)
        {
            var (victim, _) = PortedModuleTestKit.SpawnAndKill(
                game, "PlainVictim", game.CivilianPlayer, new Vector3(x += 20f, 0, 0), deathType);
            Assert.True(victim.IsDestroyed, $"{deathType} did not destroy the object");
        }
    }

    // ---- INI branch: DeathTypes ----

    [Fact]
    public void DeathTypesFilter_DestroysOnlyOnAListedDeath()
    {
        var game = NewGame();

        var survivor = game.SpawnObject("BurnOnlyVictim", game.CivilianPlayer, Vector3.Zero);
        var normal = PortedModuleTestKit.TriggerDeath(survivor, DeathType.Normal);
        Assert.True(normal.Died);                  // it died...
        Assert.False(survivor.IsDestroyed);        // ...but DestroyDie was filtered out

        var burned = game.SpawnObject("BurnOnlyVictim", game.CivilianPlayer, new Vector3(20, 0, 0));
        PortedModuleTestKit.TriggerDeath(burned, DeathType.Burned);
        Assert.True(burned.IsDestroyed);
    }

    // ---- INI branch: RequiredStatus ----

    [Fact]
    public void RequiredStatus_GatesTheDestroy()
    {
        var game = NewGame();

        var complete = game.SpawnObject("UnderConstructionVictim", game.CivilianPlayer, Vector3.Zero);
        complete.SetObjectStatus(ObjectStatus.UnderConstruction, false);
        PortedModuleTestKit.TriggerDeath(complete);
        Assert.False(complete.IsDestroyed);

        var building = game.SpawnObject("UnderConstructionVictim", game.CivilianPlayer, new Vector3(20, 0, 0));
        building.SetObjectStatus(ObjectStatus.UnderConstruction, true);
        PortedModuleTestKit.TriggerDeath(building);
        Assert.True(building.IsDestroyed);
    }

    // ---- INI branch: ExemptStatus ----

    [Fact]
    public void ExemptStatus_SuppressesTheDestroy()
    {
        var game = NewGame();

        var sold = game.SpawnObject("NotWhileSoldVictim", game.CivilianPlayer, Vector3.Zero);
        sold.SetObjectStatus(ObjectStatus.Sold, true);
        PortedModuleTestKit.TriggerDeath(sold);
        Assert.False(sold.IsDestroyed);

        var notSold = game.SpawnObject("NotWhileSoldVictim", game.CivilianPlayer, new Vector3(20, 0, 0));
        PortedModuleTestKit.TriggerDeath(notSold);
        Assert.True(notSold.IsDestroyed);
    }

    // ---- behavioral edges ----

    [Fact]
    public void SubLethalDamage_DoesNotDestroy()
    {
        var game = NewGame();
        var victim = game.SpawnObject("PlainVictim", game.CivilianPlayer, Vector3.Zero);

        var result = PortedModuleTestKit.ApplyDamage(victim, amount: 99f);

        Assert.False(result.Died);
        Assert.False(victim.IsDestroyed);
        Assert.Equal(1f, victim.BodyModule.Health);
    }

    [Fact]
    public void TwoDestroyDieModules_DestroyOnce()
    {
        // GameLogic.DestroyObject is idempotent, and the destroy list must not grow a
        // duplicate entry: an object carrying two DestroyDie modules is reaped exactly once.
        var game = NewGame();
        var before = ObjectCount(game);

        var (victim, _) = PortedModuleTestKit.SpawnAndKill(
            game, "DoubleDestroyVictim", game.CivilianPlayer, Vector3.Zero);
        Assert.True(victim.IsDestroyed);

        game.Step();
        Assert.Equal(before, ObjectCount(game));
    }

    // ---- the walk (item 3) ----

    [Fact]
    public void Xfer_IsVersionOnly_AndStateInventoryIsEmpty()
    {
        // The state inventory is empty, so the walk is the version byte alone. Pinning the
        // saved length here is what turns "a future field was added without an Xfer line"
        // into a failing test rather than a silent desync.
        var game = NewGame();
        var victim = game.SpawnObject("PlainVictim", game.CivilianPlayer, Vector3.Zero);
        var module = DieModuleOf(victim);

        Assert.True(module.HasSimXfer);
        Assert.Equal(new byte[] { 1 }, PortedModuleTestKit.Save(module));

        // Two instances of the class are indistinguishable in the walk, because there is
        // nothing to distinguish - and the CRC is still a real, non-zero fold of the version.
        var other = DieModuleOf(game.SpawnObject("BurnOnlyVictim", game.CivilianPlayer, new Vector3(20, 0, 0)));
        Assert.Equal(PortedModuleTestKit.LiveCrc(module), PortedModuleTestKit.LiveCrc(other));
    }

    [Fact]
    public void Xfer_RejectsAFutureVersion()
    {
        var game = NewGame();
        var module = DieModuleOf(game.SpawnObject("PlainVictim", game.CivilianPlayer, Vector3.Zero));

        Assert.ThrowsAny<System.Exception>(() => PortedModuleTestKit.Load(module, new byte[] { 2 }));
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        // "Mid-behavior" for a Die module is between damage and death: the object has been
        // hurt, frames have really ticked, and the death has not landed yet.
        var game = NewGame();
        var victim = game.SpawnObject("PlainVictim", game.CivilianPlayer, Vector3.Zero);
        var live = DieModuleOf(victim);

        PortedModuleTestKit.ApplyDamage(victim, amount: 60f);
        for (var i = 0; i < 5; i++)
        {
            game.Step();
        }
        Assert.False(victim.IsDestroyed);

        var shadowHost = game.SpawnObject("PlainVictim", game.CivilianPlayer, new Vector3(100, 0, 0));
        var shadow = DieModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidBehavior_SaveLoadRoundTrip_ContinuesIdentically()
    {
        // Two identical games, identical script; game B round-trips the module's state
        // through Save->Load mid-behavior. The observable continuation - when the victim
        // dies and whether it is destroyed - must be identical.
        var trajectoryA = RunScenario(roundTripAtFrame: -1);
        var trajectoryB = RunScenario(roundTripAtFrame: 3);
        Assert.Equal(trajectoryA, trajectoryB);
    }

    private static (float Health, bool Destroyed, int Objects)[] RunScenario(int roundTripAtFrame)
    {
        var game = NewGame(seed: 0xFEED);
        var victim = game.SpawnObject("PlainVictim", game.CivilianPlayer, Vector3.Zero);
        var module = DieModuleOf(victim);

        var trajectory = new (float, bool, int)[10];
        for (var i = 0; i < trajectory.Length; i++)
        {
            if (i == roundTripAtFrame)
            {
                PortedModuleTestKit.Load(module, PortedModuleTestKit.Save(module));
            }

            // damage on 1 and 4, the killing blow on 6 - so the round-trip at 3 sits
            // squarely between a wound and the death it must not disturb.
            if (i == 1 || i == 4)
            {
                PortedModuleTestKit.ApplyDamage(victim, amount: 30f);
            }
            else if (i == 6)
            {
                PortedModuleTestKit.TriggerDeath(victim, DeathType.Exploded);
            }

            game.Step();
            trajectory[i] = (victim.BodyModule.Health, victim.IsDestroyed, ObjectCount(game));
        }

        return trajectory;
    }
}
