// Mocked-game unit tests for the RebuildHoleExposeDie port (experiment-round-4 §4.1 DoD
// item 4): one test per INI-configurable branch, minimum [create -> trigger death ->
// observable effect] via the batch's PortedModuleTestKit death trigger, plus the shadow-copy
// base test mid-behavior and a mid-behavior save/load continuation.
//
// The observable effect of this class is a NEW OBJECT: a rebuild hole standing where the
// structure stood, carrying the module data's max health and knowing which structure it is
// rebuilding. So every assertion here is over the world (object count, the hole's owner,
// position, max health and OriginalStructureId), never over module internals.
//
// Object definitions are parsed from INI text through the real parser, so the quantizing S5
// parse functions (ParseFix64 on HoleMaxHealth) are on the tested path.

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Die;

public class RebuildHoleExposeDieContractTests
{
    // WorkerRespawnDelay is deliberately enormous: the hole's own update must never reach
    // its worker-spawning branch during these tests, which are about the DIE module.
    private const string Definitions = @"
Object RebuildWorker
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 50
  End
End

Object RebuildHoleObject
  KindOf = STRUCTURE REBUILD_HOLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 10
  End
  Behavior = RebuildHoleBehavior ModuleTag_Hole
    WorkerObjectName = RebuildWorker
    WorkerRespawnDelay = 600000
  End
End

Object InertHoleObject
  KindOf = STRUCTURE REBUILD_HOLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 10
  End
End

Object RebuildableKeep
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 400
  End
  Behavior = RebuildHoleExposeDie ModuleTag_Expose
    HoleName = RebuildHoleObject
    HoleMaxHealth = 250
  End
End

Object BurnOnlyKeep
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 400
  End
  Behavior = RebuildHoleExposeDie ModuleTag_Expose
    HoleName = RebuildHoleObject
    HoleMaxHealth = 250
    DeathTypes = NONE +BURNED
  End
End

Object InertHoleKeep
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 400
  End
  Behavior = RebuildHoleExposeDie ModuleTag_Expose
    HoleName = InertHoleObject
    HoleMaxHealth = 250
  End
End

Object HolelessKeep
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 400
  End
  Behavior = RebuildHoleExposeDie ModuleTag_Expose
    HoleMaxHealth = 250
  End
End

Object Bfme2Keep
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 400
  End
  Behavior = RebuildHoleExposeDie ModuleTag_Expose
    HoleName = RebuildHoleObject
    HoleMaxHealth = 250
    FadeInTimeSeconds = 2.5
    TransferAttackers = No
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xB011Du)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static GameObject[] HolesIn(HeadlessSimGame game) =>
        game.GameLogic.Objects
            .Where(o => o.Definition.KindOf?.Get(ObjectKinds.RebuildHole) == true)
            .ToArray();

    private static RebuildHoleExposeDieModuleData DataOf(HeadlessSimGame game, string definitionName) =>
        game.AssetStore.ObjectDefinitions.GetByName(definitionName)
            .Behaviors.Values.Select(c => c.Data)
            .OfType<RebuildHoleExposeDieModuleData>().Single();

    // ---- branch: the baseline path ------------------------------------------------

    [Fact]
    public void Death_ExposesAHoleAtTheStructuresPlace()
    {
        var game = NewGame();
        var position = new Vector3(30, -12, 0);
        var (keep, result) = PortedModuleTestKit.SpawnAndKill(
            game, "RebuildableKeep", game.CivilianPlayer, position);

        Assert.True(result.Died);

        var hole = Assert.Single(HolesIn(game));
        Assert.Equal("RebuildHoleObject", hole.Definition.Name);
        Assert.Equal(game.CivilianPlayer, hole.Owner);
        Assert.Equal(position.X, hole.Translation.X, 3);
        Assert.Equal(position.Y, hole.Translation.Y, 3);
        Assert.NotEqual(keep.Id, hole.Id);
    }

    [Fact]
    public void Death_GivesTheHoleTheModuleDatasMaxHealth_NotTheTemplates()
    {
        var game = NewGame();
        // The hole template says MaxHealth = 10; the Die module says HoleMaxHealth = 250.
        PortedModuleTestKit.SpawnAndKill(
            game, "RebuildableKeep", game.CivilianPlayer, Vector3.Zero);

        var hole = Assert.Single(HolesIn(game));
        Assert.Equal(250f, hole.BodyModule.MaxHealth);
    }

    [Fact]
    public void Death_TellsTheHoleWhichStructureItIsRebuilding()
    {
        var game = NewGame();
        var (keep, _) = PortedModuleTestKit.SpawnAndKill(
            game, "RebuildableKeep", game.CivilianPlayer, Vector3.Zero);

        var hole = Assert.Single(HolesIn(game));
        var rebuild = hole.FindBehavior<RebuildHoleUpdate>();
        Assert.NotNull(rebuild);
        Assert.Equal(keep.Id, rebuild.OriginalStructureId);
    }

    // ---- branch: the three guards --------------------------------------------------

    [Fact]
    public void DeathWhileUnderConstruction_ExposesNoHole()
    {
        // GPL guard (c): a structure that dies mid-build - which includes the scaffold a hole
        // is already rebuilding - must not leave a second hole behind.
        var game = NewGame();
        var keep = game.SpawnObject("RebuildableKeep", game.CivilianPlayer, Vector3.Zero);
        keep.SetObjectStatus(ObjectStatus.UnderConstruction, true);

        PortedModuleTestKit.TriggerDeath(keep);

        Assert.Empty(HolesIn(game));
    }

    [Fact]
    public void DeathOfANeutrallyOwnedStructure_ExposesNoHole()
    {
        // GPL guard (a): nobody owns it, so nobody is rebuilding it.
        var game = NewGame();
        PortedModuleTestKit.SpawnAndKill(
            game, "RebuildableKeep", game.PlayerManager.NeutralPlayer, Vector3.Zero);

        Assert.Empty(HolesIn(game));
    }

    [Fact]
    public void DeathTypeFilteredOut_ExposesNoHole()
    {
        // The shared DieLogicData filter (DeathTypes) reaches this module unchanged.
        var game = NewGame();
        PortedModuleTestKit.SpawnAndKill(
            game, "BurnOnlyKeep", game.CivilianPlayer, Vector3.Zero, DeathType.Normal);

        Assert.Empty(HolesIn(game));
    }

    [Fact]
    public void DeathTypeAllowed_ExposesAHole()
    {
        var game = NewGame();
        PortedModuleTestKit.SpawnAndKill(
            game, "BurnOnlyKeep", game.CivilianPlayer, Vector3.Zero, DeathType.Burned);

        Assert.Single(HolesIn(game));
    }

    // ---- branch: degenerate data ---------------------------------------------------

    [Fact]
    public void HoleTemplateWithoutARebuildBehavior_StillExposesTheHole()
    {
        // GPL asserts on the missing interface and skips only the handoff.
        var game = NewGame();
        PortedModuleTestKit.SpawnAndKill(
            game, "InertHoleKeep", game.CivilianPlayer, Vector3.Zero);

        var hole = Assert.Single(HolesIn(game));
        Assert.Null(hole.FindBehavior<IRebuildHoleBehavior>());
    }

    [Fact]
    public void NoHoleName_ExposesNothingAndDoesNotThrow()
    {
        var game = NewGame();
        var (_, result) = PortedModuleTestKit.SpawnAndKill(
            game, "HolelessKeep", game.CivilianPlayer, Vector3.Zero);

        Assert.True(result.Died);
        Assert.Empty(HolesIn(game));
    }

    // ---- branch: the parse side (S5 vocabulary + GPL defaults) ----------------------

    [Fact]
    public void ParsedData_UsesTheAuditedVocabularyAndTheGplDefaults()
    {
        var game = NewGame();

        var plain = DataOf(game, "RebuildableKeep");
        Assert.Equal((SimCore.Numerics.Fix64)250L, plain.HoleMaxHealth);
        Assert.True(plain.TransferAttackers);                       // GPL ctor default is TRUE
        Assert.Equal(SimCore.Numerics.Fix64.Zero, plain.FadeInTimeSeconds);

        var bfme2 = DataOf(game, "Bfme2Keep");
        Assert.False(bfme2.TransferAttackers);
        // 2.5 s quantizes exactly in Q31.32: half is a power of two.
        Assert.Equal((SimCore.Numerics.Fix64)2L + SimCore.Numerics.Fix64.Half,
            bfme2.FadeInTimeSeconds);
    }

    // ---- the walk (§3): shadow copy mid-behavior, and save/load continuation --------

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        // "Mid-behavior" for a one-shot Die module means AFTER its death has fired: the
        // structure is dead, the hole is out in the world, and the module instance is still
        // alive on the corpse. Its mutable state is empty by GPL fact, so what this test
        // proves is that the walk is version-stamped, symmetric and byte-stable - and it is
        // the test that would start failing the day someone adds a field without adding it
        // to Xfer AND the module stops being stateless.
        var game = NewGame();
        var (keep, _) = PortedModuleTestKit.SpawnAndKill(
            game, "RebuildableKeep", game.CivilianPlayer, Vector3.Zero);
        for (var i = 0; i < 3; i++)
        {
            game.Step();
        }

        var live = keep.FindBehavior<RebuildHoleExposeDie>();
        Assert.NotNull(live);

        var shadowHost = game.SpawnObject("RebuildableKeep", game.CivilianPlayer, new Vector3(200, 0, 0));
        var shadow = shadowHost.FindBehavior<RebuildHoleExposeDie>();

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidBehavior_SaveLoadRoundTrip_ContinuesIdentically()
    {
        // Two identical games, identical script; game B round-trips the module through
        // Save->Load at frame 2, between the death and the hole's later frames. If the load
        // path lost or misread anything, B's world trajectory diverges from A's.
        var trajectoryA = RunScenario(roundTripAtFrame: -1);
        var trajectoryB = RunScenario(roundTripAtFrame: 2);
        Assert.Equal(trajectoryA, trajectoryB);
    }

    private static (int Objects, int Holes, float HoleHealth)[] RunScenario(int roundTripAtFrame)
    {
        var game = NewGame(seed: 0xFEED);
        var keep = game.SpawnObject("RebuildableKeep", game.CivilianPlayer, Vector3.Zero);
        PortedModuleTestKit.ApplyDamage(keep, amount: 100f);
        game.Step();
        PortedModuleTestKit.TriggerDeath(keep);

        var module = keep.FindBehavior<RebuildHoleExposeDie>();

        var trajectory = new (int, int, float)[8];
        for (var i = 0; i < trajectory.Length; i++)
        {
            if (i == roundTripAtFrame)
            {
                PortedModuleTestKit.Load(module, PortedModuleTestKit.Save(module));
            }

            game.Step();

            var holes = HolesIn(game);
            trajectory[i] = (game.GameLogic.Objects.Count(), holes.Length,
                holes.Length > 0 ? holes[0].BodyModule.Health : 0f);
        }

        return trajectory;
    }
}
