// SabotageSuperweaponCrateCollide - R12 port (R13-fixed). GPL ref: GeneralsMD/Code/GameEngine/
// {Include,Source}/GameLogic/{Module,Object/Collide/CrateCollide}/SabotageSuperweaponCrateCollide.
// {h,cpp}, plus the shared CrateCollide::onCollide / CrateCollide::isValidToExecute base
// (CrateCollide.cpp). The runtime is a mobile saboteur "crate" whose OnCollide resets every
// SpecialPowerModule on an enemy superweapon/strategy-center structure it touches, then retires
// itself - GPL's executeCrateBehavior `sp->startPowerRecharge()` loop is the landed
// SpecialPowerModule's ResetCountdown(), and GPL's onCollide "successful execute destroys the
// crate" is GameLogic.DestroyObject(GameObject) here.
//
// R13: the base CrateCollide::isValidToExecute gate (neutral-owner rejection, AIUpdate-or-
// building-pickup requirement, ForbiddenKindOf, IsEffectivelyDead, IsAboveTerrain,
// ForbidOwnerPlayer, HumanOnly, parachute rejection) is now translated inline, mirroring the
// sibling SabotagePowerPlantCrateCollide (landed in the same R12 batch) rather than being
// reduced to `other != null` - see CrateCollideModuleData.cs for the field set this reads.
// The AIUpdateInterface goal-object gate ("is `other` still the saboteur's current AI goal
// object") is now translated via AIUpdate.GoalObject, the real 1:1 port of GPL's
// getGoalObject() that AIUpdate already exposes (see AIUpdate.cs) and that the
// SabotageSupplyCenterCrateCollide sibling already uses for the identical GPL check
// (`ai->getGoalObject() != other`) - a mismatch, including "no goal object set", is a reject.
// The file's original TODO-spec claim that "OpenSage's AIUpdate carries no GoalObject concept
// to read" was mistaken; the primitive already exists.
//
// Still TODO-spec (unverified retail behavior; not modeled - the primitives do not exist in
// OpenSage yet, so translating them here would mean inventing engine plumbing, not porting it):
//   - the EVA_BuildingSabotaged local-player announcement - OpenSage.Game/Eva holds only
//     asset-parse types (EvaEvent, ScoredKillEvaAnnouncer), no live "play this event now" system;
//   - doSabotageFeedbackFX's audio cue (TheAudio->getMiscAudio()->m_sabotageResetTimerBuilding)
//     and Drawable::flashAsSelected() - no misc-audio resource or drawable flash API is ported;
//   - base CrateCollideModuleData's ExecuteFX / ExecuteAnimation (fired by the base dispatch in
//     GPL) - this leaf does not fire them yet.
// Landed here (real runtime, not parked): the full CrateCollide base gate, the FS_SUPERWEAPON /
// FS_STRATEGY_CENTER KindOf gate, the IsEffectivelyDead gate, the ENEMIES relationship gate, the
// AI goal-object gate, the radar infiltration event (Radar.AddRadarEvent with real tile
// coordinates via HeightMap.GetTilePosition, matching the call shape GameObject itself already
// uses), the SpecialPowerModule reset loop, and self-destruction on success.

using System.Numerics;
using OpenSage.Data.Ini;

namespace OpenSage.Logic.Object;

public sealed class SabotageSuperweaponCrateCollide : CrateCollide
{
    private readonly SabotageSuperweaponCrateCollideModuleData _moduleData;

    public SabotageSuperweaponCrateCollide(GameObject gameObject, IGameEngine gameEngine, SabotageSuperweaponCrateCollideModuleData moduleData) : base(gameObject, gameEngine)
    {
        _moduleData = moduleData;
    }

    public override void OnCollide(GameObject other, in Vector3 location, in Vector3 normal)
    {
        if (!IsValidToExecute(other))
        {
            return;
        }

        if (!ExecuteCrateBehavior(other))
        {
            return;
        }

        // GPL CrateCollide::onCollide: a successful executeCrateBehavior destroys the crate.
        GameEngine.GameLogic.DestroyObject(GameObject);
    }

    /// <summary>
    /// GPL CrateCollide::isValidToExecute (the generic pickup gate) followed by
    /// SabotageSuperweaponCrateCollide::isValidToExecute's own three checks.
    /// </summary>
    internal bool IsValidToExecute(GameObject other)
    {
        return IsValidToExecute(other, other != null ? GameObject.GetRelationship(other) : RelationshipType.Neutral);
    }

    /// <summary>
    /// The relationship is taken as a parameter (rather than read from
    /// <see cref="GameObject.GetRelationship"/> internally) so this gate is directly
    /// testable: OpenSage's Team/Player relationship dictionaries are currently populated only
    /// by save-game load, so no runtime path exists yet to stand up a live ENEMIES relationship
    /// between two freshly-spawned objects. Production code still always calls the real
    /// <see cref="GameObject.GetRelationship"/> through the overload above.
    /// </summary>
    internal bool IsValidToExecute(GameObject other, RelationshipType relationship)
    {
        // ---- CrateCollide::isValidToExecute (base gate) ----

        if (other is null)
        {
            // "The ground never picks up a crate."
            return false;
        }

        var neutralPlayer = GameEngine.Game.PlayerManager.NeutralPlayer;
        if (other.Owner == neutralPlayer)
        {
            return false;
        }

        var validBuildingAttempt = _moduleData.BuildingPickup && other.IsKindOf(ObjectKinds.Structure);

        if (other.AIUpdate is null && !validBuildingAttempt)
        {
            return false;
        }

        if (_moduleData.ForbiddenKindOf is { AnyBitSet: true } forbidden
            && other.Definition.KindOf.Intersects(forbidden))
        {
            return false;
        }

        if (other.IsEffectivelyDead)
        {
            return false;
        }

        if (GameObject.IsAboveTerrain && !validBuildingAttempt)
        {
            return false;
        }

        if (_moduleData.ForbidOwnerPlayer && GameObject.Owner == other.Owner)
        {
            return false;
        }

        if (_moduleData.HumanOnly && other.Owner is { IsHuman: false })
        {
            return false;
        }

        if (other.IsKindOf(ObjectKinds.Parachute))
        {
            return false;
        }

        // ---- SabotageSuperweaponCrateCollide's own extension ----

        if (other.IsEffectivelyDead)
        {
            // Can't sabotage dead structures.
            return false;
        }

        if (!other.IsKindOf(ObjectKinds.FSSuperweapon) && !other.IsKindOf(ObjectKinds.FSStrategyCenter))
        {
            // We can only sabotage superweapon (or strategy center) structures.
            return false;
        }

        if (relationship != RelationshipType.Enemies)
        {
            // Can only sabotage enemy buildings.
            return false;
        }

        return true;
    }

    /// <summary>
    /// GPL SabotageSuperweaponCrateCollide::executeCrateBehavior: an AI goal-object check,
    /// radar infiltration ping, then reset every SpecialPowerModule on the victim.
    /// </summary>
    internal bool ExecuteCrateBehavior(GameObject other)
    {
        // "Check to make sure that the other object is also the goal object in the
        // AIUpdateInterface in order to prevent an unintentional conversion simply by having
        // the terrorist walk too close to it." GPL: `if (ai && ai->getGoalObject() != other)
        // return false;` - a mismatch (including the default "no goal object set" case) rejects.
        var ai = GameObject.AIUpdate;
        if (ai is not null && ai.GoalObject != other)
        {
            return false;
        }

        TryFireInfiltrationEvent(other);

        foreach (var specialPower in other.FindBehaviors<SpecialPowerModule>())
        {
            specialPower.ResetCountdown();
        }

        return true;
    }

    private void TryFireInfiltrationEvent(GameObject other)
    {
        var heightMap = GameEngine.Terrain?.HeightMap;
        if (GameEngine.Radar == null || heightMap == null)
        {
            return;
        }

        var tile = heightMap.GetTilePosition(other.Translation);
        if (tile == null)
        {
            return;
        }

        GameEngine.Radar.AddRadarEvent(
            RadarEventType.EnemyInfiltrationDetected,
            other.Translation,
            GameEngine.GameLogic.CurrentFrame,
            (uint)tile.Value.X,
            (uint)tile.Value.Y);
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
public sealed class SabotageSuperweaponCrateCollideModuleData : CrateCollideModuleData
{
    internal static SabotageSuperweaponCrateCollideModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<SabotageSuperweaponCrateCollideModuleData> FieldParseTable = CrateCollideModuleData.FieldParseTable
        .Concat(new IniParseTable<SabotageSuperweaponCrateCollideModuleData>());

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new SabotageSuperweaponCrateCollide(gameObject, gameEngine, this);
    }
}
