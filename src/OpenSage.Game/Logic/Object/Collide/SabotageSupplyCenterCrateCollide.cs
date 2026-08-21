// SabotageSupplyCenterCrateCollide - R12 port, R13-revised. GPL ref:
// GeneralsMD/Code/GameEngine/Source/GameLogic/Object/Collide/CrateCollide/SabotageSupplyCenterCrateCollide.cpp,
// plus the shared CrateCollide::onCollide / CrateCollide::isValidToExecute base (CrateCollide.cpp)
// - this category's SimCore host does not exist yet (no [SimState] CollideModule lineage), so
// this stays a legacy (GameObject, IGameEngine) module. Because no sibling CrateCollide grows a
// shared base pipeline (see sibling SabotagePowerPlantCrateCollide.cs, which established this
// same pattern in the same R12/R13 wave), both the generic CrateCollide::isValidToExecute gate
// and this class's own SabotageSupplyCenterCrateCollide::isValidToExecute extension are
// translated here, reading straight off this module's own (inherited) CrateCollideModuleData
// fields, faithfully to the GPL - this file stays self-contained and CrateCollide.cs is
// untouched.
//
// R13 fix: OnCollide is the live PartitionCellManager.Update() dispatch target, called
// unconditionally on every simulation frame the pair is detected as still colliding (level-
// triggered, not edge-triggered - see PartitionCellManager.cs's collision-pair loop). GPL's
// CrateCollide::onCollide destroys the saboteur (TheGameLogic->destroyObject) immediately after
// a successful executeCrateBehavior so the theft can only ever happen once; the R12 port never
// did this, so a saboteur parked against a supply center for N frames could drain
// StealCashAmount N times. OnCollide now destroys the saboteur's GameObject on a successful
// steal, exactly like SabotagePowerPlantCrateCollide.cs's OnCollide.
//
// Ported here: the full base CrateCollide::isValidToExecute chain (neutral-controlled owner,
// AI-less non-building targets gated by BuildingPickup, above-terrain gated by the same flag,
// ForbiddenKindOf mask, ForbidOwnerPlayer, HumanOnly, Parachute exclusion) followed by this
// class's own dead/kind-of/relationship checks, the goal-object guard at the top of
// executeCrateBehavior, and the cash transfer itself (BankAccount.Withdraw/Deposit is the
// direct analogue of Money::withdraw/Money::deposit, both already-landed runtime API - no new
// economic surface invented here).
//
// DELIBERATE DEVIATIONS (translate, don't invent - recorded rather than guessed):
//   - RequiredKindOf is a MASK in GPL (isKindOfMulti); the existing ported
//     CrateCollideModuleData.RequiredKindOf field is a single ObjectKinds value (a
//     pre-existing simplification predating this port, out of scope to change here), so the
//     generic base gate enforces ForbiddenKindOf only and leaves RequiredKindOf unenforced -
//     same documented gap as SabotagePowerPlantCrateCollide.cs.
//   - md->m_pickupScience (PickupScience): CrateCollideModuleData has no such field ported
//     anywhere in this codebase (pre-existing gap, not introduced here) - not consulted.
//   - TheRadar->tryInfiltrationEvent: Radar.AddRadarEvent exists, but nothing in the engine
//     yet converts a world position to the map-tile coordinates it requires. Inventing that
//     math would be a guess, not a port.
//   - EVA_CashStolen / EVA_BuildingSabotaged: no live EVA playback queue exists anywhere in
//     this codebase (EvaEvent is parse-only data everywhere). Omitted rather than guessed -
//     same gap SabotagePowerPlantCrateCollide.cs documents and works around via
//     Player.PendingEvaEvents; not replicated here since this crate's own GPL source never
//     calls doSabotageFeedbackFX/setShouldPlay in the first place (that call lives in
//     SabotageBuildingCrateCollide's siblings, not this file's executeCrateBehavior).
//   - The floating "+cash"/"-cash" text: OpenSage.Game/Gui/InGame/InGameUI.cs only carries the
//     FloatingText* INI timing knobs; there is no AddFloatingText (or equivalent) runtime API.
//   - Player::getScoreKeeper()->addMoneyEarned: PlayerScoreManager tracks no "money earned"
//     field and exposes no public mutator.

using OpenSage.Data.Ini;

namespace OpenSage.Logic.Object;

public sealed class SabotageSupplyCenterCrateCollide : CrateCollide
{
    private readonly SabotageSupplyCenterCrateCollideModuleData _moduleData;

    public SabotageSupplyCenterCrateCollide(GameObject gameObject, IGameEngine gameEngine, SabotageSupplyCenterCrateCollideModuleData moduleData)
        : base(gameObject, gameEngine)
    {
        _moduleData = moduleData;
    }

    /// <summary>GPL CrateCollide::onCollide: validate, execute, and only then consume the crate.</summary>
    public override void OnCollide(GameObject other, in System.Numerics.Vector3 location, in System.Numerics.Vector3 normal)
    {
        // In live play this saboteur is removed from the partition grid the instant it is
        // destroyed below, so PartitionCellManager.Update() can never re-dispatch OnCollide for
        // it again. This guard makes that one-shot guarantee hold at the module level too (not
        // just via partition removal), matching GPL's "a destroyed object does nothing" model
        // and closing the exact repeated-drain gap this file used to have (see the R13 fix note
        // above) even for direct/off-partition callers.
        if (GameObject.IsDestroyed)
        {
            return;
        }

        if (!IsValidToExecute(other))
        {
            return;
        }

        if (!ExecuteCrateBehavior(other))
        {
            return;
        }

        // GPL destroys the saboteur/crate object immediately after a successful theft so the
        // pickup can only ever execute once (see the R13 fix note at the top of this file).
        // Without this, PartitionCellManager.Update()'s level-triggered collision dispatch
        // would re-run OnCollide - and re-steal StealCashAmount - on every subsequent frame
        // the pair remains overlapping.
        GameEngine.GameLogic.DestroyObject(GameObject);
    }

    /// <summary>
    /// CrateCollide::isValidToExecute (the generic pickup gate) followed by
    /// SabotageSupplyCenterCrateCollide::isValidToExecute's own three checks.
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

        // ---- SabotageSupplyCenterCrateCollide's own extension ----

        if (other.IsEffectivelyDead)
        {
            // Can't sabotage dead structures.
            return false;
        }

        if (!other.IsKindOf(ObjectKinds.FSSupplyCenter))
        {
            // We can only sabotage supply dropzones.
            return false;
        }

        // Can only sabotage enemy buildings.
        return GameObject.GetRelationship(other) == RelationshipType.Enemies;
    }

    private bool ExecuteCrateBehavior(GameObject other)
    {
        // Guard against triggering this simply by having the saboteur walk too close to the
        // target: the target must still be the saboteur's active AI goal object (GPL
        // executeCrateBehavior's ai->getGoalObject() != other check).
        var ai = GameObject.AIUpdate;
        if (ai != null && ai.GoalObject != other)
        {
            return false;
        }

        var targetAccount = other.Owner?.BankAccount;
        var sourceAccount = GameObject.Owner?.BankAccount;
        if (targetAccount == null || sourceAccount == null)
        {
            return false;
        }

        var desiredAmount = _moduleData.StealCashAmount > 0 ? (uint)_moduleData.StealCashAmount : 0u;
        if (desiredAmount == 0)
        {
            return false;
        }

        // Withdraw() already clamps to the target's available balance (GPL's
        // "cash = min(desiredAmount, cash)"), returning what was actually taken. GPL's
        // executeCrateBehavior calls Money::withdraw/deposit directly with no accompanying
        // SFX, so playSound is suppressed here to match (the standard withdraw/deposit
        // jingle is not part of this crate's feedback - only the un-ported EVA cue is).
        var stolen = targetAccount.Withdraw(desiredAmount, playSound: false);
        if (stolen > 0)
        {
            sourceAccount.Deposit(stolen, playSound: false);
        }

        return true;
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
public sealed class SabotageSupplyCenterCrateCollideModuleData : CrateCollideModuleData
{
    internal static SabotageSupplyCenterCrateCollideModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<SabotageSupplyCenterCrateCollideModuleData> FieldParseTable = CrateCollideModuleData.FieldParseTable
        .Concat(new IniParseTable<SabotageSupplyCenterCrateCollideModuleData>
        {
            { "StealCashAmount", (parser, x) => x.StealCashAmount = parser.ParseInteger() },
        });

    public int StealCashAmount { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new SabotageSupplyCenterCrateCollide(gameObject, gameEngine, this);
    }
}
