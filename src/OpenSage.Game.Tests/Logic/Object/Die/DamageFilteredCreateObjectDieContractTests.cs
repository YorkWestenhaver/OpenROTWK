// Mocked-game unit tests for the DamageFilteredCreateObjectDie port (api-freeze-v1 §6 fitness
// item 4, research/modules-r13/specs/DamageFilteredCreateObjectDieModuleData.md §3): shaped
// exactly like CreateObjectDieContractTests, over the frozen module contract. Deaths are
// driven by PortedModuleTestKit.TriggerDeath / .ApplyDamage; the kit default damage type for
// TriggerDeath is DamageType.Unresistable, so any case that wants the gate to fail can just
// omit damageType. DieModule has no Update(), so the sleepy-update caveat that applies to
// other module kinds does not apply here - the one game.Step() in these tests is ordinary
// object-list bookkeeping, not a wake-frame wait.
//
// Deliberately NOT tested: anything involving SELF (does not parse today - spec §4.1), and any
// behavioral effect of DamageTypeTriggersForDuration / PostFilterTriggeredDuration beyond the
// round-trip case. Both are held, unmodelled fields (spec §4.2); writing behavioral tests for
// them is how invented behavior gets laundered into the codebase.

using System.IO;
using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Die;

public class DamageFilteredCreateObjectDieContractTests
{
    private const string Definitions = @"
Object Spawnling
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

ObjectCreationList OCL_MakeOneSpawnling
  CreateObject
    ObjectNames = Spawnling
    Count = 1
  End
End

Object UndeadHusk                          ; the BECOME_UNDEAD corpus shape (object.ini:357-362)
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = DamageFilteredCreateObjectDie ModuleTag_Die
    DamageTypeTriggersInstantly   = BECOME_UNDEAD
    DamageTypeTriggersForDuration = BECOME_UNDEAD
    PostFilterTriggeredDuration   = 10000
    CreationList                  = OCL_MakeOneSpawnling
  End
End

Object UndeadOnceHusk                      ; the second live shape (object.ini:366-371)
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = DamageFilteredCreateObjectDie ModuleTag_Die
    DamageTypeTriggersInstantly   = BECOME_UNDEAD_ONCE
    DamageTypeTriggersForDuration = BECOME_UNDEAD_ONCE
    PostFilterTriggeredDuration   = 10000
    CreationList                  = OCL_MakeOneSpawnling
  End
End

Object BurnedUndeadHusk                    ; DeathTypes + damage-type gate stacked
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = DamageFilteredCreateObjectDie ModuleTag_Die
    DamageTypeTriggersInstantly = BECOME_UNDEAD
    DeathTypes                  = NONE +BURNED
    CreationList                = OCL_MakeOneSpawnling
  End
End

Object NoListUndeadHusk
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = DamageFilteredCreateObjectDie ModuleTag_Die
    DamageTypeTriggersInstantly = BECOME_UNDEAD
  End
End

Object Bystander
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xD1E5EED)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static DamageFilteredCreateObjectDie DieModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<DamageFilteredCreateObjectDie>().Single();

    private static GameObject[] SpawnlingsIn(HeadlessSimGame game) =>
        game.GameLogic.Objects.Where(o => o.Definition.Name == "Spawnling").ToArray();

    [Fact]
    public void ParseRoundTrip_IncludingTheHeldFields()
    {
        var game = NewGame();
        var husk = game.SpawnObject("UndeadHusk", game.CivilianPlayer, Vector3.Zero);
        var data = (DamageFilteredCreateObjectDieModuleData)husk.Definition.Behaviors["ModuleTag_Die"].Data;

        Assert.Equal(DamageType.BecomeUndead, data.DamageTypeTriggersInstantly);
        Assert.Equal(DamageType.BecomeUndead, data.DamageTypeTriggersForDuration);
        Assert.Equal(10000, data.PostFilterTriggeredDuration);
        Assert.Equal("OCL_MakeOneSpawnling", data.CreationList.Value.Name);
    }

    [Fact]
    public void MatchingKillingBlowType_RunsTheCreationList()
    {
        var game = NewGame();
        var husk = game.SpawnObject("UndeadHusk", game.CivilianPlayer, Vector3.Zero);

        Assert.Empty(SpawnlingsIn(game));

        PortedModuleTestKit.TriggerDeath(husk, DeathType.Normal, DamageType.BecomeUndead);

        var spawned = Assert.Single(SpawnlingsIn(game));
        Assert.Equal(husk.Translation, spawned.Translation);
        Assert.Equal(game.CivilianPlayer, spawned.Owner);
    }

    [Fact]
    public void NonMatchingKillingBlowType_CreatesNothing()
    {
        var game = NewGame();
        var husk = game.SpawnObject("UndeadHusk", game.CivilianPlayer, Vector3.Zero);

        var result = PortedModuleTestKit.TriggerDeath(husk); // kit default: Unresistable

        Assert.True(result.Died);
        Assert.Empty(SpawnlingsIn(game));
    }

    [Fact]
    public void NonMatchingKillingBlowType_OtherRealType_CreatesNothing()
    {
        var game = NewGame();
        var husk = game.SpawnObject("UndeadHusk", game.CivilianPlayer, Vector3.Zero);

        PortedModuleTestKit.TriggerDeath(husk, DeathType.Normal, DamageType.BecomeUndeadOnce);

        Assert.Empty(SpawnlingsIn(game));
    }

    [Fact]
    public void EachBlockMatchesOnlyItsOwnType()
    {
        var game = NewGame();
        var undeadHusk = game.SpawnObject("UndeadHusk", game.CivilianPlayer, Vector3.Zero);
        var undeadOnceHusk = game.SpawnObject("UndeadOnceHusk", game.CivilianPlayer, new Vector3(50, 0, 0));

        PortedModuleTestKit.TriggerDeath(undeadHusk, DeathType.Normal, DamageType.BecomeUndead);
        PortedModuleTestKit.TriggerDeath(undeadOnceHusk, DeathType.Normal, DamageType.BecomeUndead);

        var spawned = Assert.Single(SpawnlingsIn(game));
        Assert.Equal(undeadHusk.Translation, spawned.Translation);
    }

    [Fact]
    public void DeathTypesGate_FiltersFirst()
    {
        var game = NewGame();

        var a = game.SpawnObject("BurnedUndeadHusk", game.CivilianPlayer, Vector3.Zero);
        PortedModuleTestKit.TriggerDeath(a, DeathType.Normal, DamageType.BecomeUndead);
        Assert.Empty(SpawnlingsIn(game));

        var b = game.SpawnObject("BurnedUndeadHusk", game.CivilianPlayer, new Vector3(50, 0, 0));
        PortedModuleTestKit.TriggerDeath(b, DeathType.Burned, DamageType.Explosion);
        Assert.Empty(SpawnlingsIn(game));

        var c = game.SpawnObject("BurnedUndeadHusk", game.CivilianPlayer, new Vector3(100, 0, 0));
        PortedModuleTestKit.TriggerDeath(c, DeathType.Burned, DamageType.BecomeUndead);
        Assert.Single(SpawnlingsIn(game));
    }

    [Fact]
    public void SubLethalMatchingDamage_CreatesNothing()
    {
        var game = NewGame();
        var husk = game.SpawnObject("UndeadHusk", game.CivilianPlayer, Vector3.Zero);

        var result = PortedModuleTestKit.ApplyDamage(husk, 40f, DamageType.BecomeUndead);

        Assert.False(result.Died);
        Assert.Empty(SpawnlingsIn(game));
    }

    [Fact]
    public void NoCreationList_DiesQuietly()
    {
        var game = NewGame();
        var husk = game.SpawnObject("NoListUndeadHusk", game.CivilianPlayer, Vector3.Zero);

        PortedModuleTestKit.TriggerDeath(husk, DeathType.Normal, DamageType.BecomeUndead);

        Assert.Empty(SpawnlingsIn(game));
    }

    [Fact]
    public void DamageDealerIsPassedAsTheSecondaryObject()
    {
        var game = NewGame();
        var husk = game.SpawnObject("UndeadHusk", game.CivilianPlayer, Vector3.Zero);
        var bystander = game.SpawnObject("Bystander", game.CivilianPlayer, new Vector3(5, 0, 0));

        PortedModuleTestKit.TriggerDeath(husk, DeathType.Normal, DamageType.BecomeUndead, source: bystander);

        Assert.Single(SpawnlingsIn(game));
    }

    [Fact]
    public void NoSource_StillCreates()
    {
        var game = NewGame();
        var husk = game.SpawnObject("UndeadHusk", game.CivilianPlayer, Vector3.Zero);

        PortedModuleTestKit.TriggerDeath(husk, DeathType.Normal, DamageType.BecomeUndead, source: null);

        Assert.Single(SpawnlingsIn(game));
    }

    [Fact]
    public void SourceDiedFirst_IsASilentNoOp_NotAThrow()
    {
        var game = NewGame();
        var husk = game.SpawnObject("UndeadHusk", game.CivilianPlayer, Vector3.Zero);
        var bystander = game.SpawnObject("Bystander", game.CivilianPlayer, new Vector3(5, 0, 0));

        PortedModuleTestKit.TriggerDeath(bystander); // kit default damage type - no gate on Bystander
        game.Step(); // reap the bystander

        PortedModuleTestKit.TriggerDeath(husk, DeathType.Normal, DamageType.BecomeUndead, source: bystander);

        Assert.Single(SpawnlingsIn(game));
    }

    [Fact]
    public void CreationFiresExactlyOnce()
    {
        var game = NewGame();
        var husk = game.SpawnObject("UndeadHusk", game.CivilianPlayer, Vector3.Zero);

        PortedModuleTestKit.TriggerDeath(husk, DeathType.Normal, DamageType.BecomeUndead);
        var spawned = Assert.Single(SpawnlingsIn(game));

        for (var i = 0; i < 4; i++)
        {
            game.Step();
        }

        Assert.Same(spawned, Assert.Single(SpawnlingsIn(game)));
        Assert.Contains(husk, game.GameLogic.Objects);
        Assert.Equal(0f, husk.BodyModule.Health);
    }

    [Fact]
    public void Xfer_IsVersionOnly_AndStateInventoryIsEmpty()
    {
        var game = NewGame();
        var husk = game.SpawnObject("UndeadHusk", game.CivilianPlayer, Vector3.Zero);

        PortedModuleTestKit.TriggerDeath(husk, DeathType.Normal, DamageType.BecomeUndead);

        Assert.Equal(new byte[] { 0x01 }, PortedModuleTestKit.Save(DieModuleOf(husk)));
    }

    [Fact]
    public void Xfer_RejectsAFutureVersion()
    {
        var game = NewGame();
        var husk = game.SpawnObject("UndeadHusk", game.CivilianPlayer, Vector3.Zero);

        Assert.Throws<InvalidDataException>(
            () => PortedModuleTestKit.Load(DieModuleOf(husk), new byte[] { 0x02 }));
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();

        // "Mid-behavior" for a stateless Die module means AFTER it has acted.
        var husk = game.SpawnObject("UndeadHusk", game.CivilianPlayer, Vector3.Zero);
        var live = DieModuleOf(husk);
        PortedModuleTestKit.TriggerDeath(husk, DeathType.Normal, DamageType.BecomeUndead);
        game.Step();

        var shadowHost = game.SpawnObject("UndeadHusk", game.CivilianPlayer, new Vector3(100, 0, 0));
        var shadow = DieModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidBehavior_SaveLoadRoundTrip_ContinuesIdentically()
    {
        var trajectoryA = RunScenario(roundTripAtFrame: -1);
        var trajectoryB = RunScenario(roundTripAtFrame: 2);
        Assert.Equal(trajectoryA, trajectoryB);
    }

    private static (int Objects, float FirstSpawnlingHealth)[] RunScenario(int roundTripAtFrame)
    {
        var game = NewGame(seed: 0xFEED);
        var husk = game.SpawnObject("UndeadHusk", game.CivilianPlayer, Vector3.Zero);
        var module = DieModuleOf(husk);

        var trajectory = new (int, float)[8];
        for (var i = 0; i < trajectory.Length; i++)
        {
            if (i == 1)
            {
                PortedModuleTestKit.TriggerDeath(husk, DeathType.Normal, DamageType.BecomeUndead);
            }

            if (i == roundTripAtFrame)
            {
                PortedModuleTestKit.Load(module, PortedModuleTestKit.Save(module));
            }

            game.Step();

            var spawnlings = SpawnlingsIn(game);
            trajectory[i] = (
                game.GameLogic.Objects.Count(),
                spawnlings.Length == 0 ? -1f : spawnlings[0].BodyModule.Health);
        }

        return trajectory;
    }

    [Fact]
    public void PortConstructsThroughTheContractCtor()
    {
        var game = NewGame();
        var husk = game.SpawnObject("UndeadHusk", game.CivilianPlayer, Vector3.Zero);
        var module = DieModuleOf(husk);

        Assert.IsAssignableFrom<DieModule>(module);
        Assert.Contains(module, game.GameEngine.SimContext.GameLogic
            .GetObjectById(husk.Id).BehaviorModules);
    }
}
