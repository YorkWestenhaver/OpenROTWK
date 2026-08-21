// Mocked-game contract tests for the SabotageSuperweaponCrateCollide port (R12, R13-fixed): the
// real 'SabotageSuperweaponCrateCollide' INI name must produce a live runtime that gates on the
// full CrateCollide::isValidToExecute base chain (neutral-owner rejection, AIUpdate-or-
// BuildingPickup requirement, ForbiddenKindOf, IsEffectivelyDead, IsAboveTerrain,
// ForbidOwnerPlayer, HumanOnly, parachute rejection), then its own KindOf
// (FS_SUPERWEAPON / FS_STRATEGY_CENTER) and ENEMIES relationship checks, then the
// executeCrateBehavior AI goal-object gate, before resetting every SpecialPowerModule on the
// victim through the landed ResetCountdown() (the GPL startPowerRecharge() equivalent) and
// retiring itself.
//
// TestSuperweapon/TestStrategyCenter/TestOrdinaryStructure are buildings (no AIUpdateInterface),
// so the saboteur's module sets BuildingPickup = Yes - matching the sibling
// SabotagePowerPlantCrateCollideContractTests fixture and the real GPL requirement that a
// building-kinded victim needs BuildingPickup = Yes to pass the base gate at all.
//
// The ENEMIES gate is exercised through the IsValidToExecute(other, relationship) overload
// (see the file header on the production module): OpenSage's Team/Player relationship
// dictionaries are currently populated only by save-game load, so no live path exists yet to
// stand up a real ENEMIES relationship between two freshly-spawned HeadlessSimGame objects.

using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Collide;

public class SabotageSuperweaponCrateCollideContractTests
{
    private const string Definitions = @"
SpecialPower TestSuperweaponPower
  Enum = SPECIAL_SCUD_STORM
  ReloadTime = 1000
End

Object TestSuperweapon
  KindOf = STRUCTURE FS_SUPERWEAPON
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SpecialPowerModule ModuleTag_SpecialPower
    SpecialPowerTemplate = TestSuperweaponPower
  End
End

Object TestStrategyCenter
  KindOf = STRUCTURE FS_STRATEGY_CENTER
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SpecialPowerModule ModuleTag_SpecialPower
    SpecialPowerTemplate = TestSuperweaponPower
  End
End

Object TestOrdinaryStructure
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SpecialPowerModule ModuleTag_SpecialPower
    SpecialPowerTemplate = TestSuperweaponPower
  End
End

Object TestSaboteur
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 50
  End
  Behavior = AIUpdateInterface ModuleTag_AI
  End
  Behavior = SabotageSuperweaponCrateCollide ModuleTag_Sabotage
    BuildingPickup = Yes
  End
End

Object TestSaboteurForbidOwner
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 50
  End
  Behavior = AIUpdateInterface ModuleTag_AI
  End
  Behavior = SabotageSuperweaponCrateCollide ModuleTag_Sabotage
    BuildingPickup = Yes
    ForbidOwnerPlayer = Yes
  End
End

Object TestSaboteurHumanOnly
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 50
  End
  Behavior = AIUpdateInterface ModuleTag_AI
  End
  Behavior = SabotageSuperweaponCrateCollide ModuleTag_Sabotage
    BuildingPickup = Yes
    HumanOnly = Yes
  End
End

Object TestSaboteurNoBuildingPickup
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 50
  End
  Behavior = AIUpdateInterface ModuleTag_AI
  End
  Behavior = SabotageSuperweaponCrateCollide ModuleTag_Sabotage
  End
End

Science SCIENCE_TestSabotage
  IsGrantable = Yes
End

Object TestSaboteurRequiresSuperweaponAndStructure
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 50
  End
  Behavior = SabotageSuperweaponCrateCollide ModuleTag_Sabotage
    BuildingPickup = Yes
    RequiredKindOf = STRUCTURE FS_SUPERWEAPON
  End
End

Object TestSaboteurRequiresUnsatisfiableMask
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 50
  End
  Behavior = SabotageSuperweaponCrateCollide ModuleTag_Sabotage
    BuildingPickup = Yes
    RequiredKindOf = FS_SUPERWEAPON VEHICLE
  End
End

Object TestSaboteurRequiresScience
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 50
  End
  Behavior = SabotageSuperweaponCrateCollide ModuleTag_Sabotage
    BuildingPickup = Yes
    PickupScience = SCIENCE_TestSabotage
  End
End
";

    private static (HeadlessSimGame Game, GameObject Saboteur) NewGame(string saboteurDefinition = "TestSaboteur")
    {
        var game = new HeadlessSimGame(SageGame.CncGeneralsZeroHour, 0xCA7E);
        game.LoadIniText(Definitions);
        var saboteur = game.SpawnObject(saboteurDefinition, game.CivilianPlayer, new Vector3(0, 0, 0));
        return (game, saboteur);
    }

    private static SabotageSuperweaponCrateCollide ModuleOf(GameObject obj) =>
        obj.FindBehavior<SabotageSuperweaponCrateCollide>();

    [Fact]
    public void NonSuperweaponNonStrategyCenterStructure_IsRejected()
    {
        var (game, saboteur) = NewGame();
        var victim = game.SpawnObject("TestOrdinaryStructure", game.CivilianPlayer, new Vector3(10, 0, 0));
        var module = ModuleOf(saboteur);

        Assert.False(module.IsValidToExecute(victim, RelationshipType.Enemies));
    }

    [Theory]
    [InlineData(RelationshipType.Allies)]
    [InlineData(RelationshipType.Neutral)]
    public void NonEnemyRelationship_IsRejected(RelationshipType relationship)
    {
        var (game, saboteur) = NewGame();
        var victim = game.SpawnObject("TestSuperweapon", game.CivilianPlayer, new Vector3(10, 0, 0));
        var module = ModuleOf(saboteur);

        Assert.False(module.IsValidToExecute(victim, relationship));
    }

    [Fact]
    public void EffectivelyDeadSuperweapon_IsRejected()
    {
        var (game, saboteur) = NewGame();
        var victim = game.SpawnObject("TestSuperweapon", game.CivilianPlayer, new Vector3(10, 0, 0));
        victim.IsEffectivelyDead = true;
        var module = ModuleOf(saboteur);

        Assert.False(module.IsValidToExecute(victim, RelationshipType.Enemies));
    }

    [Fact]
    public void ValidEnemySuperweapon_IsAccepted()
    {
        var (game, saboteur) = NewGame();
        var victim = game.SpawnObject("TestSuperweapon", game.CivilianPlayer, new Vector3(10, 0, 0));
        var module = ModuleOf(saboteur);

        Assert.True(module.IsValidToExecute(victim, RelationshipType.Enemies));
    }

    /// <summary>
    /// GPL CrateCollide::isValidToExecute: nothing neutral-controlled can pick up any crate.
    /// The base gate must reject a neutral-owned FS_SUPERWEAPON structure regardless of the
    /// (separately-testable) ENEMIES relationship parameter, which is why this exercises the
    /// real overload rather than IsValidToExecute(other, relationship).
    /// </summary>
    [Fact]
    public void NeutralOwnedSuperweapon_IsRejected()
    {
        var (game, saboteur) = NewGame();
        var victim = game.SpawnObject("TestSuperweapon", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));
        var module = ModuleOf(saboteur);

        Assert.False(module.IsValidToExecute(victim));
    }

    /// <summary>
    /// GPL CrateCollide::isValidToExecute: without BuildingPickup = Yes, a structure (which has
    /// no AIUpdateInterface) can never pass the "must be a Unit type thing" check.
    /// </summary>
    [Fact]
    public void BuildingVictim_WithoutBuildingPickup_IsRejected()
    {
        var (game, saboteur) = NewGame("TestSaboteurNoBuildingPickup");
        var victim = game.SpawnObject("TestSuperweapon", game.CivilianPlayer, new Vector3(10, 0, 0));
        var module = ModuleOf(saboteur);

        Assert.False(module.IsValidToExecute(victim, RelationshipType.Enemies));
    }

    /// <summary>
    /// GPL CrateCollide::isValidToExecute: ForbidOwnerPlayer = Yes rejects a victim owned by the
    /// same player as the saboteur ("Design has decreed this to not be picked up by the dead
    /// guy's team"). The relationship-overload is used since GetRelationship isn't populated in
    /// this harness, but the same-owner check reads GameObject.Owner directly regardless.
    /// </summary>
    [Fact]
    public void ForbidOwnerPlayer_SameOwnerAsSaboteur_IsRejected()
    {
        var (game, saboteur) = NewGame("TestSaboteurForbidOwner");
        var victim = game.SpawnObject("TestSuperweapon", game.CivilianPlayer, new Vector3(10, 0, 0));
        var module = ModuleOf(saboteur);

        Assert.False(module.IsValidToExecute(victim, RelationshipType.Enemies));
    }

    /// <summary>
    /// GPL CrateCollide::isValidToExecute: ForbidOwnerPlayer = Yes only rejects a
    /// same-controlling-player victim; a genuinely different owner still passes that check (the
    /// module's own ENEMIES gate is what separately governs enmity). Uses a distinct
    /// third/fourth registered player pair (see SabotageSupplyCenterCrateCollideContractTests'
    /// identical pattern) rather than the two-player NewGame() helper, since NewGame() always
    /// owns both saboteur and victim with the same CivilianPlayer.
    /// </summary>
    [Fact]
    public void ForbidOwnerPlayer_DifferentOwner_PassesBaseGate()
    {
        var game = new HeadlessSimGame(SageGame.CncGeneralsZeroHour, 0xCA7E);
        game.LoadIniText(Definitions);

        var mapPlayerOne = new OpenSage.Data.Map.Player { Name = "PlayerOne", Faction = "FactionOne", DisplayName = "PlayerOne" };
        var mapPlayerTwo = new OpenSage.Data.Map.Player { Name = "PlayerTwo", Faction = "FactionTwo", DisplayName = "PlayerTwo" };
        game.PlayerManager.OnNewGame(
            [
                OpenSage.Data.Map.Player.CreateNeutralPlayer(),
                OpenSage.Data.Map.Player.CreateCivilianPlayer(),
                mapPlayerOne,
                mapPlayerTwo,
            ],
            GameType.Skirmish);

        var saboteurOwner = game.PlayerManager.GetPlayerByIndex(2);
        var victimOwner = game.PlayerManager.GetPlayerByIndex(3);

        var saboteur = game.SpawnObject("TestSaboteurForbidOwner", saboteurOwner, new Vector3(0, 0, 0));
        var victim = game.SpawnObject("TestSuperweapon", victimOwner, new Vector3(10, 0, 0));
        var module = ModuleOf(saboteur);

        // The ForbidOwnerPlayer check alone must not reject this - owners genuinely differ.
        // Uses the relationship-parameter overload since OpenSage's Team/Player relationship
        // dictionaries are populated only by save-game load (see the file header), so a real
        // ENEMIES relationship can't be stood up between two freshly-spawned objects here.
        Assert.True(module.IsValidToExecute(victim, RelationshipType.Enemies));
    }

    /// <summary>
    /// GPL CrateCollide::isValidToExecute: HumanOnly = Yes rejects a victim whose owner is not
    /// PLAYER_HUMAN. CivilianPlayer is non-human (Data.Map.Player.IsHuman defaults to false), so
    /// it stands in directly for the "AI/non-human owner" case GPL's check targets.
    /// </summary>
    [Fact]
    public void HumanOnly_NonHumanOwnedVictim_IsRejected()
    {
        var (game, saboteur) = NewGame("TestSaboteurHumanOnly");
        var victim = game.SpawnObject("TestSuperweapon", game.CivilianPlayer, new Vector3(10, 0, 0));
        var module = ModuleOf(saboteur);

        Assert.False(module.IsValidToExecute(victim, RelationshipType.Enemies));
    }

    [Fact]
    public void ValidCollision_ResetsAllSpecialPowers()
    {
        var (game, saboteur) = NewGame();
        var victim = game.SpawnObject("TestSuperweapon", game.CivilianPlayer, new Vector3(10, 0, 0));
        var module = ModuleOf(saboteur);
        var specialPower = victim.FindBehavior<SpecialPowerModule>();

        // Let the power fully recharge so ResetCountdown() (the startPowerRecharge()
        // equivalent) has an observable effect: sabotage puts it back into recharge.
        for (var i = 0; i < 40 && !specialPower.Ready; i++)
        {
            game.Step();
        }
        Assert.True(specialPower.Ready);

        // A deliberate sabotage order: GPL's executeCrateBehavior requires the AI goal object
        // to be the victim, so the order must be in place for the reset to proceed.
        saboteur.AIUpdate.GoalObject = victim;

        Assert.True(module.IsValidToExecute(victim, RelationshipType.Enemies));

        var result = module.ExecuteCrateBehavior(victim);

        Assert.True(result);
        Assert.False(specialPower.Ready);
    }

    [Fact]
    public void StrategyCenterVictim_AlsoResetsAllSpecialPowers()
    {
        var (game, saboteur) = NewGame();
        var victim = game.SpawnObject("TestStrategyCenter", game.CivilianPlayer, new Vector3(10, 0, 0));
        var module = ModuleOf(saboteur);
        var specialPower = victim.FindBehavior<SpecialPowerModule>();

        for (var i = 0; i < 40 && !specialPower.Ready; i++)
        {
            game.Step();
        }
        Assert.True(specialPower.Ready);

        saboteur.AIUpdate.GoalObject = victim;

        Assert.True(module.IsValidToExecute(victim, RelationshipType.Enemies));
        Assert.True(module.ExecuteCrateBehavior(victim));
        Assert.False(specialPower.Ready);
    }

    /// <summary>
    /// GPL executeCrateBehavior: "Check to make sure that the other object is also the goal
    /// object in the AIUpdateInterface in order to prevent an unintentional [reset] simply by
    /// having the terrorist walk too close to it." A saboteur whose AI goal object is a
    /// different object than the one it collided with must veto the reset even though
    /// isValidToExecute passes in isolation.
    /// </summary>
    [Fact]
    public void AIGoalMismatch_Rejected()
    {
        var (game, saboteur) = NewGame();
        var victim = game.SpawnObject("TestSuperweapon", game.CivilianPlayer, new Vector3(10, 0, 0));
        var decoy = game.SpawnObject("TestOrdinaryStructure", game.CivilianPlayer, new Vector3(50, 0, 0));
        var module = ModuleOf(saboteur);
        var specialPower = victim.FindBehavior<SpecialPowerModule>();
        for (var i = 0; i < 40 && !specialPower.Ready; i++)
        {
            game.Step();
        }
        Assert.True(specialPower.Ready);

        saboteur.AIUpdate.GoalObject = decoy;

        Assert.True(module.IsValidToExecute(victim, RelationshipType.Enemies));
        var result = module.ExecuteCrateBehavior(victim);

        Assert.False(result);
        Assert.True(specialPower.Ready); // untouched: the goal-object gate refused.
    }

    /// <summary>The mirror of <see cref="AIGoalMismatch_Rejected"/>: when the AI goal object is
    /// the actual victim, the reset proceeds as normal.</summary>
    [Fact]
    public void AIGoalMatch_ResetsAllSpecialPowers()
    {
        var (game, saboteur) = NewGame();
        var victim = game.SpawnObject("TestSuperweapon", game.CivilianPlayer, new Vector3(10, 0, 0));
        var module = ModuleOf(saboteur);
        var specialPower = victim.FindBehavior<SpecialPowerModule>();
        for (var i = 0; i < 40 && !specialPower.Ready; i++)
        {
            game.Step();
        }
        Assert.True(specialPower.Ready);

        saboteur.AIUpdate.GoalObject = victim;

        Assert.True(module.IsValidToExecute(victim, RelationshipType.Enemies));
        var result = module.ExecuteCrateBehavior(victim);

        Assert.True(result);
        Assert.False(specialPower.Ready);
    }

    /// <summary>
    /// The real end-to-end OnCollide dispatch (used by live gameplay collisions): with two
    /// freshly-spawned objects, <see cref="GameObject.GetRelationship"/> is Neutral (OpenSage's
    /// Team/Player relationship dictionaries are populated only by save-game load today, see
    /// the file header), so the module's own real-relationship overload correctly refuses to
    /// execute and the saboteur survives - it does not self-destruct on a non-enemy touch.
    /// </summary>
    [Fact]
    public void OnCollide_RealDefaultRelationship_DoesNotExecuteOrDestroySaboteur()
    {
        var (game, saboteur) = NewGame();
        var victim = game.SpawnObject("TestSuperweapon", game.CivilianPlayer, new Vector3(10, 0, 0));
        var module = ModuleOf(saboteur);
        var specialPower = victim.FindBehavior<SpecialPowerModule>();
        for (var i = 0; i < 40 && !specialPower.Ready; i++)
        {
            game.Step();
        }
        Assert.True(specialPower.Ready);

        module.OnCollide(victim, victim.Translation, Vector3.UnitZ);

        Assert.False(saboteur.IsDestroyed);
        Assert.True(specialPower.Ready); // untouched: the real relationship gate refused.
    }

    [Fact]
    public void OnCollide_NullOther_DoesNotThrowOrDestroySaboteur()
    {
        var (_, saboteur) = NewGame();
        var module = ModuleOf(saboteur);

        module.OnCollide(null, Vector3.Zero, Vector3.Zero);

        Assert.False(saboteur.IsDestroyed);
    }

    // ---- R13.5: shared CrateCollide::isValidToExecute base gate (crate-gate hoist) ----
    //
    // These cases exercise the base gate's own fields (RequiredKindOf-as-mask, PickupScience),
    // which this leaf's construction only started parsing/enforcing as of the 525ddaa0 hoist -
    // a victim that would pass this leaf's own three checks (alive, FS_SUPERWEAPON/
    // FS_STRATEGY_CENTER, ENEMIES) so any rejection below can only come from the base gate.

    // RequiredKindOf is a MASK (GPL isKindOfMulti): EVERY bit must be present. The old
    // single-value parse would have kept only the last token of a multi-kind authored line.
    [Fact]
    public void RequiredKindOfMask_AcceptsVictimCarryingEveryBit()
    {
        var (game, saboteur) = NewGame("TestSaboteurRequiresSuperweaponAndStructure");
        var victim = game.SpawnObject("TestSuperweapon", game.CivilianPlayer, new Vector3(10, 0, 0));
        var module = ModuleOf(saboteur);

        // TestSuperweapon is KindOf = STRUCTURE FS_SUPERWEAPON - both required bits present.
        Assert.True(module.IsValidToExecute(victim, RelationshipType.Enemies));
    }

    [Fact]
    public void RequiredKindOfMask_RejectsVictimMissingOneBit()
    {
        // Requires FS_SUPERWEAPON *and* VEHICLE; the superweapon structure has only the
        // former, so a true mask rejects it. A single-value parse (last token wins = VEHICLE,
        // unenforced) would have accepted it.
        var (game, saboteur) = NewGame("TestSaboteurRequiresUnsatisfiableMask");
        var victim = game.SpawnObject("TestSuperweapon", game.CivilianPlayer, new Vector3(10, 0, 0));
        var module = ModuleOf(saboteur);

        Assert.False(module.IsValidToExecute(victim, RelationshipType.Enemies));
    }

    // PickupScience ("m_pickupScience"): only relevant when the collided-with object's owner
    // holds the named science. This module casts the sabotaged structure as the base gate's
    // "collector" role, so it is the VICTIM's owner that must hold it.
    [Fact]
    public void PickupScience_VictimOwnerLacksIt_IsRejectedByTheBaseGate()
    {
        var (game, saboteur) = NewGame("TestSaboteurRequiresScience");
        var victim = game.SpawnObject("TestSuperweapon", game.CivilianPlayer, new Vector3(10, 0, 0));
        var module = ModuleOf(saboteur);
        // Deliberately not granted: game.CivilianPlayer never receives SCIENCE_TestSabotage.

        Assert.False(module.IsValidToExecute(victim, RelationshipType.Enemies));
    }

    [Fact]
    public void PickupScience_VictimOwnerHasIt_PassesTheBaseGate()
    {
        var (game, saboteur) = NewGame("TestSaboteurRequiresScience");
        var victim = game.SpawnObject("TestSuperweapon", game.CivilianPlayer, new Vector3(10, 0, 0));
        var module = ModuleOf(saboteur);
        game.CivilianPlayer.DirectlyAssignScience(game.AssetStore.Sciences.GetByName("SCIENCE_TestSabotage"));

        Assert.True(module.IsValidToExecute(victim, RelationshipType.Enemies));
    }

    [Fact]
    public void ExecuteCrateBehavior_WithNoSpecialPowerModules_StillSucceeds()
    {
        var (game, saboteur) = NewGame();
        // A superweapon-kinded object with no SpecialPowerModule attached: the reset loop
        // must simply see zero modules, not throw.
        game.LoadIniText(@"
Object TestBareSuperweapon
  KindOf = STRUCTURE FS_SUPERWEAPON
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
");
        var victim = game.SpawnObject("TestBareSuperweapon", game.CivilianPlayer, new Vector3(10, 0, 0));
        var module = ModuleOf(saboteur);

        Assert.Empty(victim.FindBehaviors<SpecialPowerModule>());
        saboteur.AIUpdate.GoalObject = victim;
        Assert.True(module.ExecuteCrateBehavior(victim));
    }
}
