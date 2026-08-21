// SabotageSupplyCenterCrateCollide - R12 port. GPL ref:
// GeneralsMD/Code/GameEngine/Source/GameLogic/Object/Collide/CrateCollide/SabotageSupplyCenterCrateCollide.cpp
//
// A crate (in practice, a saboteur unit) that steals cash from an enemy supply center on
// collision. Ported here: the dead/kind-of/relationship/AI-goal validation chain
// (isValidToExecute + the goal-object guard at the top of executeCrateBehavior) and the cash
// transfer itself (BankAccount.Withdraw/Deposit is the direct analogue of Money::withdraw/
// Money::deposit, both already-landed runtime API - no new economic surface invented here).
//
// Deliberately NOT ported (no runtime host exists for these yet, so nothing is fabricated in
// their place - same "presentation absent from the sim seam" shape as LargeGroupAudioUpdate's
// audio mix):
//   - TheRadar->tryInfiltrationEvent: Radar.AddRadarEvent exists, but nothing in the engine
//     yet converts a world position to the map-tile coordinates it requires, and IGame's
//     current-frame source for a non-SimState legacy module is likewise unestablished by any
//     landed caller. Inventing either would be a guess, not a port.
//   - EVA_CashStolen / EVA_BuildingSabotaged: no EVA system exists anywhere in the codebase.
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

    public override void OnCollide(GameObject other, in System.Numerics.Vector3 location, in System.Numerics.Vector3 normal)
    {
        if (other == null)
        {
            return;
        }

        // Can't sabotage dead structures.
        if (other.IsEffectivelyDead)
        {
            return;
        }

        // We can only sabotage supply dropzones.
        if (!other.IsKindOf(ObjectKinds.FSSupplyCenter))
        {
            return;
        }

        // Can only sabotage enemy buildings.
        if (GameObject.GetRelationship(other) != RelationshipType.Enemies)
        {
            return;
        }

        // Guard against triggering this simply by having the saboteur walk too close to the
        // target: the target must still be the saboteur's active AI goal object (GPL
        // executeCrateBehavior's ai->getGoalObject() != other check).
        var ai = GameObject.AIUpdate;
        if (ai != null && ai.GoalObject != other)
        {
            return;
        }

        var targetAccount = other.Owner?.BankAccount;
        var sourceAccount = GameObject.Owner?.BankAccount;
        if (targetAccount == null || sourceAccount == null)
        {
            return;
        }

        var desiredAmount = _moduleData.StealCashAmount > 0 ? (uint)_moduleData.StealCashAmount : 0u;
        if (desiredAmount == 0)
        {
            return;
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
