// Mocked-game unit tests for the CreateCrateDie port (api-freeze-v1 §6 fitness item 4;
// experiment-round-4 §4.1 DoD item 4): one test per INI-configurable branch, each in the
// [create -> trigger death -> observable effect] shape the Die batch's death-trigger helper
// exists to make possible, plus the mid-behavior save/load continuation and the shadow-copy
// base test.
//
// TWO observables are asserted throughout, and the second one matters as much as the first:
//   - the crates that appear in the world (behaviour), and
//   - ISimRandom.DrawCount (conformance channel 5). This class IS a draw sequence: a
//     condition that rejects a crate BEFORE the creation-chance draw and one that rejects it
//     AFTER look identical in crate counts and are a desync apart. Every test that expects
//     "no crate" therefore also says how many draws that costs.
//
// Object definitions are parsed from INI text through the real parser, so CrateData's
// quantizing Fix64 parse path is on the tested path.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Die;

public class CreateCrateDieContractTests
{
    private const string Definitions = @"
CrateData AlwaysCrate
  CreationChance = 1.0
  CrateObject = TestCrate 1.0
End

CrateData NeverCrate
  CreationChance = 0.0
  CrateObject = TestCrate 1.0
End

CrateData RegularOnlyCrate
  CreationChance = 1.0
  VeterancyLevel = REGULAR
  CrateObject = TestCrate 1.0
End

CrateData EliteOnlyCrate
  CreationChance = 1.0
  VeterancyLevel = ELITE
  CrateObject = TestCrate 1.0
End

CrateData InfantryKillerCrate
  CreationChance = 1.0
  KilledByType = INFANTRY
  CrateObject = TestCrate 1.0
End

CrateData InfantryAndVehicleKillerCrate
  CreationChance = 1.0
  KilledByType = INFANTRY VEHICLE
  CrateObject = TestCrate 1.0
End

CrateData SalvageScienceCrate
  CreationChance = 1.0
  KillerScience = SCIENCE_TestSalvage
  CrateObject = TestCrate 1.0
End

CrateData OwnedCrate
  CreationChance = 1.0
  OwnedByMaker = Yes
  CrateObject = TestCrate 1.0
End

CrateData SecondEntryOnlyCrate
  CreationChance = 1.0
  CrateObject = TestCrate 0.0
  CrateObject = OtherCrate 1.0
End

CrateData NoObjectCrate
  CreationChance = 1.0
End

Object TestCrate
  KindOf = CRATE
End

Object OtherCrate
  KindOf = CRATE
End

Object CrateDropper
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = CreateCrateDie ModuleTag_Crate
    CrateData = AlwaysCrate
  End
End

Object NeverDropper
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = CreateCrateDie ModuleTag_Crate
    CrateData = NeverCrate
  End
End

Object DanglingDropper
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = CreateCrateDie ModuleTag_Crate
    CrateData = NoSuchCrateDataExists
  End
End

Object EmptyDropper
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = CreateCrateDie ModuleTag_Crate
  End
End

Object RegularDropper
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = CreateCrateDie ModuleTag_Crate
    CrateData = RegularOnlyCrate
  End
End

Object EliteDropper
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = CreateCrateDie ModuleTag_Crate
    CrateData = EliteOnlyCrate
  End
End

Object InfantryKilledDropper
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = CreateCrateDie ModuleTag_Crate
    CrateData = InfantryKillerCrate
  End
End

Object InfantryAndVehicleKilledDropper
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = CreateCrateDie ModuleTag_Crate
    CrateData = InfantryAndVehicleKillerCrate
  End
End

Object SalvageScienceDropper
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = CreateCrateDie ModuleTag_Crate
    CrateData = SalvageScienceCrate
  End
End

Object OwnedDropper
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = CreateCrateDie ModuleTag_Crate
    CrateData = OwnedCrate
  End
End

Object WeightedDropper
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = CreateCrateDie ModuleTag_Crate
    CrateData = SecondEntryOnlyCrate
  End
End

Object EmptyTableDropper
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = CreateCrateDie ModuleTag_Crate
    CrateData = NoObjectCrate
  End
End

Object TwoTemplateDropper
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = CreateCrateDie ModuleTag_Crate
    CrateData = NeverCrate
    CrateData = AlwaysCrate
  End
End

Object BurnOnlyDropper
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = CreateCrateDie ModuleTag_Crate
    DeathTypes = NONE +BURNED
    CrateData = AlwaysCrate
  End
End

Science SCIENCE_TestSalvage
  IsGrantable = Yes
End

Object InfantryKiller
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object VehicleKiller
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object InfantryVehicleKiller
  KindOf = INFANTRY VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xC4A7Eu)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static Player Enemy(HeadlessSimGame game) => game.PlayerManager.Players[0];

    private static int CrateCount(HeadlessSimGame game, string definitionName = "TestCrate") =>
        game.GameLogic.Objects.Count(o => o.Definition.Name == definitionName);

    private static ulong Draws(HeadlessSimGame game) =>
        game.GameEngine.SimContext.GameLogicRandom.DrawCount;

    private static CreateCrateDie CrateModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<CreateCrateDie>().Single();

    // ---- the happy path -----------------------------------------------------

    [Fact]
    public void CertainCrate_SpawnsOneCrateForTheVictimsOwner_AndCostsThreeDraws()
    {
        var game = NewGame();
        var killer = game.SpawnObject("InfantryKiller", Enemy(game), new Vector3(5, 0, 0));
        var before = Draws(game);

        var (victim, result) = PortedModuleTestKit.SpawnAndKill(
            game, "CrateDropper", game.CivilianPlayer, new Vector3(20, 30, 0), source: killer);

        Assert.True(result.Died);
        Assert.Equal(1, CrateCount(game));

        // chance + weighted pick + facing.
        Assert.Equal(3ul, Draws(game) - before);

        var crate = game.GameLogic.Objects.Single(o => o.Definition.Name == "TestCrate");
        Assert.Same(game.CivilianPlayer, crate.Owner);

        // No findPositionAround analogue yet (recorded deviation): the crate stands where the
        // victim stood.
        Assert.Equal(victim.Transform.Translation, crate.Transform.Translation);
    }

    // ---- the rejection branches, each with its draw cost --------------------

    [Fact]
    public void ZeroCreationChance_SpawnsNothing_ButStillCostsTheChanceDraw()
    {
        var game = NewGame();
        var killer = game.SpawnObject("InfantryKiller", Enemy(game), new Vector3(5, 0, 0));
        var before = Draws(game);

        PortedModuleTestKit.SpawnAndKill(
            game, "NeverDropper", game.CivilianPlayer, Vector3.Zero, source: killer);

        Assert.Equal(0, CrateCount(game));
        Assert.Equal(1ul, Draws(game) - before);
    }

    [Fact]
    public void DanglingCrateDataName_SpawnsNothing_AndCostsNoDraws()
    {
        // The shipping AotR case: eggs.ini names AntiquarianCrateData, which is commented out
        // of crate.ini. GPL findCrateTemplate returns NULL and the entry is skipped before the
        // chance draw - so a broken name must not shift the RNG stream.
        var game = NewGame();
        var killer = game.SpawnObject("InfantryKiller", Enemy(game), new Vector3(5, 0, 0));
        var before = Draws(game);

        PortedModuleTestKit.SpawnAndKill(
            game, "DanglingDropper", game.CivilianPlayer, Vector3.Zero, source: killer);

        Assert.Equal(0, CrateCount(game));
        Assert.Equal(0ul, Draws(game) - before);
    }

    [Fact]
    public void NoCrateDataAtAll_SpawnsNothing_AndCostsNoDraws()
    {
        var game = NewGame();
        var before = Draws(game);

        PortedModuleTestKit.SpawnAndKill(
            game, "EmptyDropper", game.CivilianPlayer, Vector3.Zero);

        Assert.Equal(0, CrateCount(game));
        Assert.Equal(0ul, Draws(game) - before);
    }

    [Fact]
    public void AlliedKiller_CancelsEverything_BeforeAnyDraw()
    {
        // "Nope, no crate for killing ally at all" - and the gate sits before the loop, so it
        // costs nothing on the RNG stream.
        var game = NewGame();
        var friend = game.SpawnObject("InfantryKiller", game.CivilianPlayer, new Vector3(5, 0, 0));
        var before = Draws(game);

        PortedModuleTestKit.SpawnAndKill(
            game, "CrateDropper", game.CivilianPlayer, Vector3.Zero, source: friend);

        Assert.Equal(0, CrateCount(game));
        Assert.Equal(0ul, Draws(game) - before);
    }

    [Fact]
    public void ExplicitlyAlliedPlayer_AlsoCancels()
    {
        var game = NewGame();
        var enemy = Enemy(game);
        enemy.Allies.Add(game.CivilianPlayer);
        var ally = game.SpawnObject("InfantryKiller", enemy, new Vector3(5, 0, 0));

        PortedModuleTestKit.SpawnAndKill(
            game, "CrateDropper", game.CivilianPlayer, Vector3.Zero, source: ally);

        Assert.Equal(0, CrateCount(game));
    }

    [Fact]
    public void NullKiller_IsNotAllied_AndTheCrateStillDrops()
    {
        // A script kill or terrain death has no source object; GPL's relationship test is
        // guarded by "if (killer)", so the crate drops.
        var game = NewGame();

        PortedModuleTestKit.SpawnAndKill(
            game, "CrateDropper", game.CivilianPlayer, Vector3.Zero);

        Assert.Equal(1, CrateCount(game));
    }

    // ---- VeterancyLevel: the VICTIM's level, tested for equality ------------

    [Fact]
    public void VeterancyLevel_MatchingVictimRank_Drops()
    {
        // The template asks for REGULAR, which is every fresh object's rank. (Promoting an
        // object mid-test is not available headless: GameObject.OnVeterancyLevelChanged goes
        // through the audio system, which the graphics-free host does not own - recorded as a
        // test-host note, not worked around.)
        var game = NewGame();
        var killer = game.SpawnObject("InfantryKiller", Enemy(game), new Vector3(5, 0, 0));
        var victim = game.SpawnObject("RegularDropper", game.CivilianPlayer, Vector3.Zero);
        Assert.Equal(VeterancyLevel.Regular, victim.ExperienceTracker.VeterancyLevel);

        PortedModuleTestKit.TriggerDeath(victim, source: killer);

        Assert.Equal(1, CrateCount(game));
    }

    [Fact]
    public void VeterancyLevel_MismatchedVictimRank_DropsNothing_AfterTheChanceDraw()
    {
        var game = NewGame();
        var killer = game.SpawnObject("InfantryKiller", Enemy(game), new Vector3(5, 0, 0));
        var before = Draws(game);

        // Default rank is REGULAR, the template wants ELITE.
        PortedModuleTestKit.SpawnAndKill(
            game, "EliteDropper", game.CivilianPlayer, Vector3.Zero, source: killer);

        Assert.Equal(0, CrateCount(game));
        Assert.Equal(1ul, Draws(game) - before);
    }

    // ---- KilledByType: a MASK the killer must satisfy in full --------------

    [Fact]
    public void KilledByType_MatchingKiller_Drops()
    {
        var game = NewGame();
        var killer = game.SpawnObject("InfantryKiller", Enemy(game), new Vector3(5, 0, 0));

        PortedModuleTestKit.SpawnAndKill(
            game, "InfantryKilledDropper", game.CivilianPlayer, Vector3.Zero, source: killer);

        Assert.Equal(1, CrateCount(game));
    }

    [Fact]
    public void KilledByType_WrongKindKiller_DropsNothing()
    {
        var game = NewGame();
        var killer = game.SpawnObject("VehicleKiller", Enemy(game), new Vector3(5, 0, 0));

        PortedModuleTestKit.SpawnAndKill(
            game, "InfantryKilledDropper", game.CivilianPlayer, Vector3.Zero, source: killer);

        Assert.Equal(0, CrateCount(game));
    }

    [Fact]
    public void KilledByType_NullKiller_FailsTheTest()
    {
        var game = NewGame();

        PortedModuleTestKit.SpawnAndKill(
            game, "InfantryKilledDropper", game.CivilianPlayer, Vector3.Zero);

        Assert.Equal(0, CrateCount(game));
    }

    [Fact]
    public void KilledByType_MultiBitMask_NeedsEveryBit()
    {
        // The mask fix: a killer carrying only one of the two required kinds must NOT qualify.
        var game = NewGame();
        var partial = game.SpawnObject("InfantryKiller", Enemy(game), new Vector3(5, 0, 0));
        PortedModuleTestKit.SpawnAndKill(
            game, "InfantryAndVehicleKilledDropper", game.CivilianPlayer, Vector3.Zero, source: partial);
        Assert.Equal(0, CrateCount(game));

        var full = game.SpawnObject("InfantryVehicleKiller", Enemy(game), new Vector3(6, 0, 0));
        PortedModuleTestKit.SpawnAndKill(
            game, "InfantryAndVehicleKilledDropper", game.CivilianPlayer, new Vector3(1, 0, 0), source: full);
        Assert.Equal(1, CrateCount(game));
    }

    // ---- KillerScience: held by the KILLER'S CONTROLLING PLAYER ------------

    private static Science TestSalvage(HeadlessSimGame game) =>
        game.AssetStore.Sciences.GetByName("SCIENCE_TestSalvage");

    [Fact]
    public void KillerScience_KillerPlayerHasIt_Drops()
    {
        var game = NewGame();
        var enemy = Enemy(game);
        enemy.DirectlyAssignScience(TestSalvage(game));
        var killer = game.SpawnObject("InfantryKiller", enemy, new Vector3(5, 0, 0));
        var before = Draws(game);

        PortedModuleTestKit.SpawnAndKill(
            game, "SalvageScienceDropper", game.CivilianPlayer, Vector3.Zero, source: killer);

        Assert.Equal(1, CrateCount(game));
        Assert.Equal(3ul, Draws(game) - before);
    }

    [Fact]
    public void KillerScience_KillerPlayerLacksIt_DropsNothing_AfterTheChanceDraw()
    {
        // The science is never granted, so the test fails - but only AFTER the creation-chance
        // draw has already been spent. That ordering is the channel-5 fact.
        var game = NewGame();
        var killer = game.SpawnObject("InfantryKiller", Enemy(game), new Vector3(5, 0, 0));
        var before = Draws(game);

        PortedModuleTestKit.SpawnAndKill(
            game, "SalvageScienceDropper", game.CivilianPlayer, Vector3.Zero, source: killer);

        Assert.Equal(0, CrateCount(game));
        Assert.Equal(1ul, Draws(game) - before);
    }

    [Fact]
    public void KillerScience_NullKiller_FailsTheTest()
    {
        // GPL testKillerScience returns FALSE for a null killer outright: an unattributed death
        // (script, terrain) never satisfies a science requirement, even if every player has it.
        var game = NewGame();
        Enemy(game).DirectlyAssignScience(TestSalvage(game));
        game.CivilianPlayer.DirectlyAssignScience(TestSalvage(game));
        var before = Draws(game);

        PortedModuleTestKit.SpawnAndKill(
            game, "SalvageScienceDropper", game.CivilianPlayer, Vector3.Zero);

        Assert.Equal(0, CrateCount(game));
        Assert.Equal(1ul, Draws(game) - before);
    }

    [Fact]
    public void KillerScience_VictimPlayerHoldingIt_IsIrrelevant()
    {
        // The science is tested on the KILLER'S player, never the victim's - granting it to the
        // dead unit's owner alone must not conjure a crate.
        var game = NewGame();
        game.CivilianPlayer.DirectlyAssignScience(TestSalvage(game));
        var killer = game.SpawnObject("InfantryKiller", Enemy(game), new Vector3(5, 0, 0));

        PortedModuleTestKit.SpawnAndKill(
            game, "SalvageScienceDropper", game.CivilianPlayer, Vector3.Zero, source: killer);

        Assert.Equal(0, CrateCount(game));
    }

    // ---- the weighted one-of-n pick, and its degenerate case ---------------

    [Fact]
    public void WeightedPick_ZeroWeightEntryIsNeverChosen()
    {
        // TestCrate 0.0 / OtherCrate 1.0: the running total only ever exceeds the pick at the
        // second entry, whatever the draw.
        var game = NewGame();

        PortedModuleTestKit.SpawnAndKill(
            game, "WeightedDropper", game.CivilianPlayer, Vector3.Zero);

        Assert.Equal(0, CrateCount(game, "TestCrate"));
        Assert.Equal(1, CrateCount(game, "OtherCrate"));
    }

    [Fact]
    public void EmptyCrateObjectTable_SpawnsNothing_ButTheChanceAndPickDrawsAreSpent()
    {
        // A template whose chances do not resolve to an object: GPL takes the pick draw first
        // and only then discovers there is no crate type, so the stream must show two draws.
        var game = NewGame();
        var before = Draws(game);

        PortedModuleTestKit.SpawnAndKill(
            game, "EmptyTableDropper", game.CivilianPlayer, Vector3.Zero);

        Assert.Equal(0, CrateCount(game));
        Assert.Equal(2ul, Draws(game) - before);
    }

    // ---- the list shape: "CrateData = X" twice means two templates ---------

    [Fact]
    public void TwoCrateDataEntries_AreBothTried_InDeclarationOrder()
    {
        // NeverCrate (chance 0) then AlwaysCrate: one crate, and 1 + 3 draws - which is also
        // how the test knows the first entry was not silently dropped.
        var game = NewGame();
        var before = Draws(game);

        PortedModuleTestKit.SpawnAndKill(
            game, "TwoTemplateDropper", game.CivilianPlayer, Vector3.Zero);

        Assert.Equal(1, CrateCount(game));
        Assert.Equal(4ul, Draws(game) - before);
    }

    // ---- the base-class death filter still governs -------------------------

    [Fact]
    public void DeathTypesFilter_GatesTheWholeModule()
    {
        var game = NewGame();

        var normal = game.SpawnObject("BurnOnlyDropper", game.CivilianPlayer, Vector3.Zero);
        var before = Draws(game);
        PortedModuleTestKit.TriggerDeath(normal, DeathType.Normal);
        Assert.Equal(0, CrateCount(game));
        Assert.Equal(0ul, Draws(game) - before);

        var burned = game.SpawnObject("BurnOnlyDropper", game.CivilianPlayer, new Vector3(9, 0, 0));
        PortedModuleTestKit.TriggerDeath(burned, DeathType.Burned);
        Assert.Equal(1, CrateCount(game));
    }

    // ---- OwnedByMaker ------------------------------------------------------

    [Fact]
    public void OwnedByMaker_HandsTheCrateToTheVictimsPlayersDefaultTeam()
    {
        // GPL: crate->setTeam(me->getControllingPlayer()->getDefaultTeam()). OpenSAGE only
        // populates Player.DefaultTeam from a save file today, so in a live game the assignment
        // is inert - recorded as a finding rather than replaced with an invented team. What is
        // asserted here is the part that is real: the branch runs, the crate still drops, and
        // the crate's team follows the victim's player's default team whatever that is.
        var game = NewGame();

        PortedModuleTestKit.SpawnAndKill(
            game, "OwnedDropper", game.CivilianPlayer, Vector3.Zero);

        var crate = game.GameLogic.Objects.Single(o => o.Definition.Name == "TestCrate");
        Assert.Equal(game.CivilianPlayer.DefaultTeam, crate.Team);
    }

    // ---- the contract base tests -------------------------------------------

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var live = game.SpawnObject("CrateDropper", game.CivilianPlayer, Vector3.Zero);
        var shadowHost = game.SpawnObject("CrateDropper", game.CivilianPlayer, new Vector3(100, 0, 0));

        // Mid-behavior: real damage taken, real frames ticked, and the module has already run
        // a death on another object of the same definition, so the RNG stream has moved.
        PortedModuleTestKit.ApplyDamage(live, 40f);
        for (var i = 0; i < 4; i++)
        {
            game.Step();
        }
        PortedModuleTestKit.SpawnAndKill(game, "CrateDropper", game.CivilianPlayer, new Vector3(50, 0, 0));

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(
            CrateModuleOf(live), CrateModuleOf(shadowHost));
    }

    [Fact]
    public void MidBehavior_SaveLoadRoundTrip_ContinuesIdentically()
    {
        // The continuation test: two runs of the same trajectory, one of them interrupted by a
        // save/load of the module mid-way. Because this class carries no mutable state, the
        // interesting invariant is the one that would break if it ever gained some: the crates
        // and the draw count after the interruption must match the unperturbed run exactly.
        static (int Crates, ulong Draws) Run(bool interrupt)
        {
            var game = NewGame();
            var killer = game.SpawnObject("InfantryKiller", Enemy(game), new Vector3(5, 0, 0));
            var before = Draws(game);

            PortedModuleTestKit.SpawnAndKill(
                game, "TwoTemplateDropper", game.CivilianPlayer, Vector3.Zero, source: killer);

            var survivor = game.SpawnObject("TwoTemplateDropper", game.CivilianPlayer, new Vector3(30, 0, 0));
            if (interrupt)
            {
                var module = CrateModuleOf(survivor);
                PortedModuleTestKit.Load(module, PortedModuleTestKit.Save(module));
            }

            game.Step();
            PortedModuleTestKit.TriggerDeath(survivor, source: killer);

            return (CrateCount(game), Draws(game) - before);
        }

        Assert.Equal(Run(interrupt: false), Run(interrupt: true));
    }

    [Fact]
    public void SavedWalk_IsTheVersionByteAlone()
    {
        // The state inventory is empty by design (the GPL class has no members). Pinning the
        // walk's shape here means a future field addition cannot land without this test and
        // the Xfer version moving together.
        var game = NewGame();
        var obj = game.SpawnObject("CrateDropper", game.CivilianPlayer, Vector3.Zero);

        Assert.Equal(new byte[] { 1 }, PortedModuleTestKit.Save(CrateModuleOf(obj)));
    }
}
