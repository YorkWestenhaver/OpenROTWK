// SabotagePowerPlantCrateCollide - R12 port from [ParseOnly].
//
// Behavioral reference: generals-gpl GeneralsMD SabotagePowerPlantCrateCollide.cpp, plus the
// shared CrateCollide::onCollide / CrateCollide::isValidToExecute base (CrateCollide.cpp) -
// this category's SimCore host does not exist yet (no [SimState] CollideModule lineage), so
// this stays a legacy (GameObject, IGameEngine) module like its landed Collide siblings
// (MoneyCrateCollide, ConvertToHijackedVehicleCrateCollide, ...); it reaches the sim only
// through the pre-existing legacy OnCollide dispatch (PartitionCellManager -> GameObject ->
// ICollideModule.OnCollide), which none of those siblings has occupied yet.
//
// Because no sibling CrateCollide has ever overridden OnCollide, the base pipeline
// (CrateCollide::onCollide + CrateCollide::isValidToExecute) does not exist anywhere in
// OpenSAGE either. Rather than grow the shared CrateCollide base for one module, both layers
// are translated here, reading straight off this module's own (inherited) CrateCollideModuleData
// fields, faithfully to the GPL - this file stays self-contained and CrateCollide.cs is
// untouched.
//
// DELIBERATE DEVIATIONS (translate, don't invent - recorded rather than guessed):
//   - RequiredKindOf is a MASK in GPL (isKindOfMulti); the existing ported
//     CrateCollideModuleData.RequiredKindOf field is a single ObjectKinds value (a
//     pre-existing simplification predating this port, out of scope to change here), so the
//     generic base gate enforces ForbiddenKindOf only and leaves RequiredKindOf unenforced.
//   - TheRadar->tryInfiltrationEvent(other): Radar.AddRadarEvent exists but takes map-tile
//     coordinates that nothing in the engine currently computes from a world position; no
//     radar ping is fired rather than guessing that math.
//   - doSabotageFeedbackFX (a one-shot positional sound + Drawable::flashAsSelected): no
//     ported hook connects AudioSystem to a positional one-shot from game-logic code, and
//     Drawable has no flash-as-selected port; omitted rather than guessed.
//   - EVA_BuildingSabotaged: OpenSAGE has no live EVA playback queue (EvaEvent is parse-only
//     data everywhere in this codebase). Player.PendingEvaEvents is the minimal additive
//     surface this port introduces so the "should play" request is still observable and
//     queued, matching TheEva->setShouldPlay's fire-and-forget shape, for the eventual EVA
//     system to drain.
//   - AIUpdateInterface::getGoalObject(): the retail hijacker/order-targeting goal-object
//     concept isn't ported (AIUpdate is deliberately unfrozen). AIUpdate.CurrentVictimId is
//     the closest existing analogue (this unit's current AI target), added by this port.

using System.Numerics;
using OpenSage.Data.Ini;

namespace OpenSage.Logic.Object;

/// <summary>
/// A crate (in practice, a mobile saboteur unit) that disables an enemy power plant on
/// contact: the plant's owner suffers a power brownout for
/// <see cref="SabotagePowerPlantCrateCollideModuleData.SabotagePowerDuration"/> logic frames,
/// then <see cref="Player.LogicTick"/> lifts it again once that duration elapses.
/// </summary>
public sealed class SabotagePowerPlantCrateCollide : CrateCollide
{
    /// <summary>EvaEvent asset name for GPL's EVA_BuildingSabotaged (Eva.cpp's "BUILDINGSABOTAGED").</summary>
    private const string BuildingSabotagedEvaEventName = "BuildingSabotaged";

    private readonly SabotagePowerPlantCrateCollideModuleData _moduleData;

    public SabotagePowerPlantCrateCollide(GameObject gameObject, IGameEngine gameEngine, SabotagePowerPlantCrateCollideModuleData moduleData)
        : base(gameObject, gameEngine)
    {
        _moduleData = moduleData;
    }

    /// <summary>GPL CrateCollide::onCollide: validate, execute, and only then consume the crate.</summary>
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

        GameEngine.GameLogic.DestroyObject(GameObject);
    }

    /// <summary>
    /// CrateCollide::isValidToExecute (the generic pickup gate) followed by
    /// SabotagePowerPlantCrateCollide::isValidToExecute's own three checks.
    /// </summary>
    private bool IsValidToExecute(GameObject other)
    {
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

        // ---- SabotagePowerPlantCrateCollide's own extension ----

        if (other.IsEffectivelyDead)
        {
            // Can't sabotage dead structures.
            return false;
        }

        if (!other.IsKindOf(ObjectKinds.FSPower))
        {
            // We can only sabotage power plants.
            return false;
        }

        // GPL getObject()->getRelationship(other) != ENEMIES. GameObject.GetRelationship
        // resolves through the (largely unpopulated, script/map-data-only) Team relationship
        // tables; the ported sibling modules that already gate on enmity (EnemyNearUpdate,
        // AutoHealBehavior's ally check) instead read Player.Enemies directly, so this
        // follows that established, live convention rather than the dormant Team path.
        if (other.Owner is null || GameObject.Owner is null || !GameObject.Owner.Enemies.Contains(other.Owner))
        {
            // Can only sabotage enemy buildings.
            return false;
        }

        return true;
    }

    private bool ExecuteCrateBehavior(GameObject other)
    {
        // "Check to make sure that the other object is also the goal object in the
        // AIUpdateInterface in order to prevent an unintentional conversion simply by having
        // the terrorist walk too close to it." See the AIUpdate.CurrentVictimId deviation
        // note at the top of this file.
        var ai = GameObject.AIUpdate;
        if (ai is not null && ai.CurrentVictimId.IsValid && ai.CurrentVictimId != other.Id)
        {
            return false;
        }

        if (GameEngine.Scene3D?.LocalPlayer is { } localPlayer && other.Owner == localPlayer)
        {
            // "When the sabotage occurs, play the appropriate EVA event if the local player
            // is the victim!"
            localPlayer.PendingEvaEvents.Add(BuildingSabotagedEvaEventName);
        }

        var player = other.Owner;
        if (player is not null)
        {
            var sabotageEndFrame = GameEngine.GameLogic.CurrentFrame
                + new LogicFrameSpan((uint)_moduleData.SabotagePowerDuration);

            // Sets the duration and immediately triggers the brownout callback; Player's
            // own LogicTick turns it back off once the duration elapses.
            player.SetPowerSabotagedTillFrame(sabotageEndFrame);
        }

        return true;
    }
}

/// <summary>
/// Hardcoded to play the SabotageBuildingPower sound definition when triggered.
/// </summary>
[AddedIn(SageGame.CncGeneralsZeroHour)]
public sealed class SabotagePowerPlantCrateCollideModuleData : CrateCollideModuleData
{
    internal static SabotagePowerPlantCrateCollideModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<SabotagePowerPlantCrateCollideModuleData> FieldParseTable = CrateCollideModuleData.FieldParseTable
        .Concat(new IniParseTable<SabotagePowerPlantCrateCollideModuleData>
        {
            { "SabotagePowerDuration", (parser, x) => x.SabotagePowerDuration = parser.ParseInteger() },
        });

    public int SabotagePowerDuration { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new SabotagePowerPlantCrateCollide(gameObject, gameEngine, this);
    }
}
