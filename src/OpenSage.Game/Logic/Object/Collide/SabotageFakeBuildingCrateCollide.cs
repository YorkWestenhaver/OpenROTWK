using System.Numerics;
using OpenSage.Data.Ini;

namespace OpenSage.Logic.Object;

/// <summary>
/// A saboteur unit collision handler that destroys enemy fake buildings on contact
/// (GPL <c>SabotageFakeBuildingCrateCollide</c>). "Crate" is a misnomer here: the collider is
/// carried by the saboteur unit itself, not a pickup-crate object.
/// </summary>
public sealed class SabotageFakeBuildingCrateCollide : CrateCollide
{
    public SabotageFakeBuildingCrateCollide(GameObject gameObject, IGameEngine gameEngine) : base(gameObject, gameEngine)
    {
    }

    /// <summary>
    /// GPL <c>CrateCollide::onCollide</c>: validate, execute, and only then consume the
    /// saboteur ("crate" is a misnomer for this family - the collider lives on the mobile
    /// saboteur, not a pickup-crate object) via
    /// <c>TheGameLogic-&gt;destroyObject(getObject())</c>, matching the sibling
    /// <see cref="SabotagePowerPlantCrateCollide.OnCollide"/> port.
    /// </summary>
    public override void OnCollide(GameObject other, in Vector3 location, in Vector3 normal)
    {
        if (!TryExecuteSabotage(other))
        {
            return;
        }

        GameEngine.GameLogic.DestroyObject(GameObject);
    }

    /// <summary>
    /// GPL <c>isValidToExecute</c> + <c>executeCrateBehavior</c> folded into one testable call:
    /// validates the target, then (if valid) destroys it. Returns whether the sabotage was
    /// actually carried out.
    /// </summary>
    internal bool TryExecuteSabotage(GameObject other)
    {
        if (!IsValidToExecute(other))
        {
            return false;
        }

        // Check to make sure that the other object is also the goal object in the
        // AIUpdateInterface, in order to prevent an unintentional sabotage simply by having
        // the saboteur walk too close to it. GPL guards this with `ai && ai->getGoalObject()
        // != other`, so a saboteur with NO AIUpdate at all passes the gate - matching the
        // sibling SabotageSupplyCenterCrateCollide port. (Writing it as `AIUpdate?.GoalObject
        // != other` inverted that: a null AIUpdate yielded null != other and rejected.)
        var ai = GameObject.AIUpdate;
        if (ai != null && ai.GoalObject != other)
        {
            return false;
        }

        GameEngine.Radar?.AddRadarEvent(
            RadarEventType.EnemyInfiltrationDetected,
            other.Translation,
            GameEngine.GameLogic.CurrentFrame,
            mapTileXCoordinate: 0,
            mapTileYCoordinate: 0);

        // TODO(Port): Play EVA_BuildingSabotaged when other.IsLocallyControlled() - no Eva
        // announcer runtime exists yet to hang this off (only the INI-side EvaEvent asset).

        other.AttemptDamage(new DamageInfoInput(GameObject)
        {
            DamageType = DamageType.Unresistable,
            DeathType = DeathType.Detonated,
            Amount = other.BodyModule.MaxHealth,
        });

        return true;
    }

    private bool IsValidToExecute(GameObject other)
    {
        if (other is null)
        {
            return false;
        }

        // Can't sabotage dead structures.
        if (other.IsEffectivelyDead)
        {
            return false;
        }

        // We can only sabotage fake structures.
        if (!other.IsKindOf(ObjectKinds.FSFake))
        {
            return false;
        }

        // GPL getObject()->getRelationship(other) != ENEMIES. GameObject.GetRelationship
        // resolves through Team.GetRelationship -> Player.GetRelationship, which reads the
        // Player._playerToPlayerRelationships dictionary populated only by explicit
        // Player.SetRelationship calls - PlayerManager.OnNewGame (the entry point for every
        // real skirmish/multiplayer game start) never calls SetRelationship and leaves that
        // dictionary empty, so this check would always read Neutral and never fire in a real
        // game. Follow the sibling SabotagePowerPlantCrateCollide's established, live
        // convention of reading Player.Enemies directly instead (populated straight from map
        // side-list data by PlayerManager.OnNewGame).
        return other.Owner is not null
            && GameObject.Owner is not null
            && GameObject.Owner.Enemies.Contains(other.Owner);
    }

    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        base.Load(reader);
    }
}

/// <summary>
/// Hardcoded to play the SabotageBuilding sound definition when triggered.
/// </summary>
[AddedIn(SageGame.CncGeneralsZeroHour)]
public sealed class SabotageFakeBuildingCrateCollideModuleData : CrateCollideModuleData
{
    internal static SabotageFakeBuildingCrateCollideModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<SabotageFakeBuildingCrateCollideModuleData> FieldParseTable = CrateCollideModuleData.FieldParseTable
        .Concat(new IniParseTable<SabotageFakeBuildingCrateCollideModuleData>());

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new SabotageFakeBuildingCrateCollide(gameObject, gameEngine);
    }
}
