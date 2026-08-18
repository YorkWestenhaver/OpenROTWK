// Mocked-game unit tests for the CreateObjectDie port (api-freeze-v1 §6 fitness item 4):
// one test per INI-configurable branch, each [create -> trigger death -> observable
// effect] via the batch's death-trigger helper, plus the shadow-copy base test and the
// mid-behavior save/load continuation. Object definitions and the ObjectCreationLists are
// parsed from INI text through the real parser.

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Die;

public class CreateObjectDieContractTests
{
    private const string Definitions = @"
Object Spawnling
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
    SubdualDamageCap = 100
    SubdualDamageHealRate = 100
    SubdualDamageHealAmount = 1
  End
End

ObjectCreationList OCL_MakeOneSpawnling
  CreateObject
    ObjectNames = Spawnling
    Count = 1
  End
End

ObjectCreationList OCL_MakeTwoSpawnlings
  CreateObject
    ObjectNames = Spawnling
    Count = 2
  End
End

Object Husk
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
    SubdualDamageCap = 100
    SubdualDamageHealRate = 100
    SubdualDamageHealAmount = 1
  End
  Behavior = CreateObjectDie ModuleTag_Die
    CreationList = OCL_MakeOneSpawnling
  End
End

Object HuskWithTransfer
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
    SubdualDamageCap = 100
    SubdualDamageHealRate = 100
    SubdualDamageHealAmount = 1
  End
  Behavior = CreateObjectDie ModuleTag_Die
    CreationList = OCL_MakeOneSpawnling
    TransferPreviousHealth = Yes
  End
End

Object HuskWithTransferOfTwo
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
    SubdualDamageCap = 100
    SubdualDamageHealRate = 100
    SubdualDamageHealAmount = 1
  End
  Behavior = CreateObjectDie ModuleTag_Die
    CreationList = OCL_MakeTwoSpawnlings
    TransferPreviousHealth = Yes
  End
End

Object BurnHusk
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = CreateObjectDie ModuleTag_Die
    CreationList = OCL_MakeOneSpawnling
    DeathTypes = NONE +BURNED
  End
End

Object EmptyHusk
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = CreateObjectDie ModuleTag_Die
  End
End

Object Bystander
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xD1E)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static CreateObjectDie DieModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<CreateObjectDie>().Single();

    private static GameObject[] SpawnlingsIn(HeadlessSimGame game) =>
        game.GameLogic.Objects.Where(o => o.Definition.Name == "Spawnling").ToArray();

    [Fact]
    public void Death_RunsTheCreationList()
    {
        var game = NewGame();
        var husk = game.SpawnObject("Husk", game.CivilianPlayer, Vector3.Zero);

        Assert.Empty(SpawnlingsIn(game));

        PortedModuleTestKit.TriggerDeath(husk);

        var spawned = Assert.Single(SpawnlingsIn(game));
        // The original creates at the dying object's position (the nugget's zero Offset).
        Assert.Equal(husk.Translation, spawned.Translation);
        // ...and owned by the dying object's player.
        Assert.Equal(game.CivilianPlayer, spawned.Owner);
    }

    [Fact]
    public void SubLethalDamage_CreatesNothing()
    {
        var game = NewGame();
        var husk = game.SpawnObject("Husk", game.CivilianPlayer, Vector3.Zero);

        var result = PortedModuleTestKit.ApplyDamage(husk, 40f);

        Assert.False(result.Died);
        Assert.Empty(SpawnlingsIn(game));
    }

    [Fact]
    public void NoCreationList_DiesQuietly()
    {
        var game = NewGame();
        var husk = game.SpawnObject("EmptyHusk", game.CivilianPlayer, Vector3.Zero);

        // The original guards a null OCL; so does the seam. The observable effect is that
        // the death completes and nothing is created.
        PortedModuleTestKit.TriggerDeath(husk);

        Assert.Empty(SpawnlingsIn(game));
    }

    [Fact]
    public void DeathTypeFilter_OnlyTheMatchingDeathCreates()
    {
        var game = NewGame();

        var normalDeath = game.SpawnObject("BurnHusk", game.CivilianPlayer, Vector3.Zero);
        PortedModuleTestKit.TriggerDeath(normalDeath, DeathType.Normal);
        Assert.Empty(SpawnlingsIn(game));

        var burnedDeath = game.SpawnObject("BurnHusk", game.CivilianPlayer, new Vector3(50, 0, 0));
        PortedModuleTestKit.TriggerDeath(burnedDeath, DeathType.Burned);
        Assert.Single(SpawnlingsIn(game));
    }

    [Fact]
    public void TransferPreviousHealth_Off_LeavesTheNewObjectUnhurt()
    {
        var game = NewGame();
        var husk = game.SpawnObject("Husk", game.CivilianPlayer, Vector3.Zero);

        PortedModuleTestKit.ApplyDamage(husk, 30f);
        Assert.Equal(70f, husk.BodyModule.Health);

        PortedModuleTestKit.TriggerDeath(husk);

        Assert.Equal(100f, Assert.Single(SpawnlingsIn(game)).BodyModule.Health);
    }

    [Fact]
    public void TransferPreviousHealth_On_CopiesThePreDeathDeficit()
    {
        var game = NewGame();
        var husk = game.SpawnObject("HuskWithTransfer", game.CivilianPlayer, Vector3.Zero);

        PortedModuleTestKit.ApplyDamage(husk, 30f);
        Assert.Equal(70f, husk.BodyModule.Health);

        // PREVIOUS health, not current: the killing blow takes the husk to 0, and a
        // current-health reading would hand the replacement a 100-point deficit.
        PortedModuleTestKit.TriggerDeath(husk);

        Assert.Equal(70f, Assert.Single(SpawnlingsIn(game)).BodyModule.Health);
    }

    [Fact]
    public void TransferPreviousHealth_On_UndamagedDonorTransfersNothing()
    {
        var game = NewGame();
        var husk = game.SpawnObject("HuskWithTransfer", game.CivilianPlayer, Vector3.Zero);

        PortedModuleTestKit.TriggerDeath(husk);

        // Deficit is zero, so the second leg's amount is not positive and never fires.
        Assert.Equal(100f, Assert.Single(SpawnlingsIn(game)).BodyModule.Health);
    }

    [Fact]
    public void TransferPreviousHealth_On_CopiesSubdualDamage()
    {
        var game = NewGame();
        var husk = game.SpawnObject("HuskWithTransfer", game.CivilianPlayer, Vector3.Zero);

        PortedModuleTestKit.ApplyDamage(husk, 25f, DamageType.SubdualUnresistable);
        Assert.Equal(25f, husk.BodyModule.CurrentSubdualDamageAmount);
        Assert.Equal(100f, husk.BodyModule.Health);      // subdual is not health damage

        PortedModuleTestKit.TriggerDeath(husk);

        var spawned = Assert.Single(SpawnlingsIn(game));
        Assert.Equal(25f, spawned.BodyModule.CurrentSubdualDamageAmount);
    }

    [Fact]
    public void TransferPreviousHealth_On_TargetsOnlyTheFirstCreatedObject()
    {
        var game = NewGame();
        var husk = game.SpawnObject("HuskWithTransferOfTwo", game.CivilianPlayer, Vector3.Zero);

        PortedModuleTestKit.ApplyDamage(husk, 40f);
        PortedModuleTestKit.TriggerDeath(husk);

        // The original's create() returns the FIRST object made; the rest are untouched.
        var spawned = SpawnlingsIn(game);
        Assert.Equal(2, spawned.Length);
        Assert.Equal(60f, spawned[0].BodyModule.Health);
        Assert.Equal(100f, spawned[1].BodyModule.Health);
    }

    [Fact]
    public void KillerIsCreditedWithTheTransferredDamage()
    {
        var game = NewGame();
        var husk = game.SpawnObject("HuskWithTransfer", game.CivilianPlayer, Vector3.Zero);
        var killer = game.SpawnObject("Bystander", game.CivilianPlayer, new Vector3(5, 0, 0));

        PortedModuleTestKit.ApplyDamage(husk, 30f, source: killer);
        PortedModuleTestKit.TriggerDeath(husk, source: killer);

        // The transferred deficit is credited to whoever last damaged the donor.
        var spawned = Assert.Single(SpawnlingsIn(game));
        Assert.Equal(killer.Id, spawned.BodyModule.LastDamageInfo!.Value.Request.SourceID);
    }

    [Fact]
    public void CreationFiresExactlyOnce_AndTheSpawnedObjectOutlivesTheFrame()
    {
        var game = NewGame();
        var husk = game.SpawnObject("Husk", game.CivilianPlayer, Vector3.Zero);

        PortedModuleTestKit.TriggerDeath(husk);
        var spawned = Assert.Single(SpawnlingsIn(game));

        // Ticking on does not re-run the list (the module is a pure OnDie reaction, with no
        // update hook at all) and does not reap the creation.
        for (var i = 0; i < 4; i++)
        {
            game.Step();
        }
        Assert.Same(spawned, Assert.Single(SpawnlingsIn(game)));

        // Recorded fact, not an assumption: dying is not leaving. Removal from the world is
        // DestroyDie's job, so a husk carrying only CreateObjectDie stays in the object list
        // as a corpse at zero health - which is why the batch scenario pairs the two.
        Assert.Contains(husk, game.GameLogic.Objects);
        Assert.Equal(0f, husk.BodyModule.Health);
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();

        // "Mid-behavior" for a stateless Die module means AFTER it has acted: the live
        // instance below has already run its creation list and its health transfer, so
        // any state it had illicitly accumulated would be in it by now.
        var husk = game.SpawnObject("HuskWithTransfer", game.CivilianPlayer, Vector3.Zero);
        var live = DieModuleOf(husk);
        PortedModuleTestKit.ApplyDamage(husk, 30f);
        PortedModuleTestKit.TriggerDeath(husk);
        game.Step();

        // The shadow is the same class over the same data on a second, untouched object.
        var shadowHost = game.SpawnObject("HuskWithTransfer", game.CivilianPlayer, new Vector3(100, 0, 0));
        var shadow = DieModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidBehavior_SaveLoadRoundTrip_ContinuesIdentically()
    {
        // Two identical games, identical script. Game B round-trips the module state
        // through Save->Load in the middle of the death sequence's aftermath; if the walk
        // lost or misread anything, B's continuation diverges from A's.
        var trajectoryA = RunScenario(roundTripAtFrame: -1);
        var trajectoryB = RunScenario(roundTripAtFrame: 2);
        Assert.Equal(trajectoryA, trajectoryB);
    }

    private static (int Objects, float FirstSpawnlingHealth)[] RunScenario(int roundTripAtFrame)
    {
        var game = NewGame(seed: 0xFEED);
        var husk = game.SpawnObject("HuskWithTransfer", game.CivilianPlayer, Vector3.Zero);
        var module = DieModuleOf(husk);

        PortedModuleTestKit.ApplyDamage(husk, 30f);

        var trajectory = new (int, float)[8];
        for (var i = 0; i < trajectory.Length; i++)
        {
            if (i == 1)
            {
                PortedModuleTestKit.TriggerDeath(husk);
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
}
