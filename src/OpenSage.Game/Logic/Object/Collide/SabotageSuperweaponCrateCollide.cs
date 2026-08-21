// SabotageSuperweaponCrateCollide - R12 port. GPL ref: GeneralsMD/Code/GameEngine/{Include,Source}/
// GameLogic/{Module,Object/Collide/CrateCollide}/SabotageSuperweaponCrateCollide.{h,cpp}. The
// runtime is a mobile saboteur "crate" whose OnCollide resets every SpecialPowerModule on an
// enemy superweapon/strategy-center structure it touches, then retires itself - GPL's
// executeCrateBehavior `sp->startPowerRecharge()` loop is the landed SpecialPowerModule's
// ResetCountdown(), and GPL's onCollide "successful execute destroys the crate" is
// GameLogic.DestroyObject(GameObject) here. The base CrateCollide::onCollide/isValidToExecute
// dispatch (RequiredKindOf/ForbiddenKindOf/ForbidOwnerPlayer/HumanOnly, ExecuteFX, the world
// icon) is not itself ported yet in OpenSage (Collide/CrateCollide.cs is Load-only) and is a
// shared file this task does not touch (reservedNames is empty), so the validity + effect
// logic below lives entirely in this leaf: the SabotageSuperweapon-specific isValidToExecute
// extension plus a reasonable, self-contained base check (other != null).
//
// TODO-spec (unverified retail behavior; not modeled - the primitives do not exist in OpenSage
// yet, so translating them here would mean inventing engine plumbing, not porting it):
//   - the AIUpdateInterface goal-object gate ("is `other` still the saboteur's current AI goal
//     object") - OpenSage's AIUpdate carries no GoalObject concept to read;
//   - the EVA_BuildingSabotaged local-player announcement - OpenSage.Game/Eva holds only
//     asset-parse types (EvaEvent, ScoredKillEvaAnnouncer), no live "play this event now" system;
//   - doSabotageFeedbackFX's audio cue (TheAudio->getMiscAudio()->m_sabotageResetTimerBuilding)
//     and Drawable::flashAsSelected() - no misc-audio resource or drawable flash API is ported;
//   - base CrateCollideModuleData's ExecuteFX / ExecuteAnimation (fired by the unported base
//     dispatch, see above) - this leaf does not fire them.
// Landed here (real runtime, not parked): the FS_SUPERWEAPON / FS_STRATEGY_CENTER KindOf gate,
// the IsEffectivelyDead gate, the ENEMIES relationship gate, the radar infiltration event
// (Radar.AddRadarEvent with real tile coordinates via HeightMap.GetTilePosition, matching the
// call shape GameObject itself already uses), the SpecialPowerModule reset loop, and
// self-destruction on success.

using System.Numerics;
using OpenSage.Data.Ini;

namespace OpenSage.Logic.Object;

public sealed class SabotageSuperweaponCrateCollide : CrateCollide
{
    public SabotageSuperweaponCrateCollide(GameObject gameObject, IGameEngine gameEngine) : base(gameObject, gameEngine)
    {
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

    /// <summary>GPL SabotageSuperweaponCrateCollide::isValidToExecute (the override only; the
    /// CrateCollide base checks are not modeled here, see file header).</summary>
    internal bool IsValidToExecute(GameObject other)
    {
        return other != null && IsValidToExecute(other, GameObject.GetRelationship(other));
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
        if (other == null)
        {
            return false;
        }

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
    /// GPL SabotageSuperweaponCrateCollide::executeCrateBehavior: radar infiltration ping, then
    /// reset every SpecialPowerModule on the victim. Always returns true once past
    /// IsValidToExecute - GPL's only additional failure mode there is the AI-goal-object
    /// mismatch, which is not modeled (see file header).
    /// </summary>
    internal bool ExecuteCrateBehavior(GameObject other)
    {
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
        return new SabotageSuperweaponCrateCollide(gameObject, gameEngine);
    }
}
