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

    public override void OnCollide(GameObject other, in Vector3 location, in Vector3 normal)
    {
        TryExecuteSabotage(other);
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

        // Can only sabotage enemy buildings.
        return GameObject.GetRelationship(other) == RelationshipType.Enemies;
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
