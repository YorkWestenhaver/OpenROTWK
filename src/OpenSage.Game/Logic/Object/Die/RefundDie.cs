// RefundDie - Die-batch port to the frozen module contract (api-freeze-v1 §3/§5 as amended by
// api-freeze-amendments-v1.1, template v1.1 = pilot-autoheal §3/§6).
//
// Behavioral reference: NONE. `grep -rn "refunddie" generals-gpl generals-community`
// (case-insensitive) has no hits, and the only `RefundPercent` hits in the GPL corpus are
// GlobalData's `m_RefundPercent` global sell-on-cancel constant (`ThingTemplate::getRefundValue`,
// `BuildAssistant.cpp`) - a different mechanism (global cancel-refund keyed off a template field,
// not a per-object Die-module field). See research/modules-r13/specs/RefundDieModuleData.md §0 for
// the grounding check. This class has no GPL ancestor of any kind; everything below is
// idiom-reuse across already-landed sibling modules, not translation:
//   - RefundPercent -> BankAccount.Deposit, copying AutoDepositUpdate.Update()'s
//     "(uint)(Percentage * float)" arithmetic shape (Update/AutoDepositUpdate.cs).
//   - UpgradeRequired -> GameObject.HasUpgrade, the own-object gate idiom used by
//     BunkerBusterBehavior.BustTheBunker() and AutoDepositUpdate.Update(). HasUpgrade(null)
//     already returns true, so an absent field passes the gate for free.
//   - BuildingRequired -> ObjectFilter.Matches against the killer resolved from
//     DamageInfoInput.SourceID via Context.GameLogic.GetObjectById, the same lookup
//     RespawnBody.ResolvePermanence() uses for its own killer-matching filter.
//   - MUTABLE SIM STATE INVENTORY: empty. Like UpgradeDieModule, this class reads only its own
//     ModuleData plus other objects' live state (itself, the resolved killer) at the moment of
//     death; it carries no field of its own across frames. The walk below is a version byte and
//     nothing else.
//   - Which object each gate reads (self for UpgradeRequired, killer for BuildingRequired) is
//     idiom-by-analogy, not GPL-confirmed - filed as finding F-RFD-1 in the port spec. Likewise
//     GameObject.Owner is not null-checked before .BankAccount, matching every landed sibling's
//     posture (F-RFD-2), and BuildCost is read at time of death, the only value available
//     (F-RFD-3).

using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.Mathematics;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class RefundDieModule : DieModule
{
    private readonly RefundDieModuleData _moduleData;

    internal RefundDieModule(GameObject gameObject, ISimContext context, RefundDieModuleData moduleData)
        : base(gameObject, context, moduleData)
    {
        _moduleData = moduleData;
    }

    /// <summary>
    /// Refunds a percentage of this object's own build cost to its owner, gated on this
    /// object holding <see cref="RefundDieModuleData.UpgradeRequired"/> (if set) and on the
    /// killer matching <see cref="RefundDieModuleData.BuildingRequired"/> (if set). Reached
    /// only when the base's shared <c>DieLogicData</c> gate has already passed.
    /// </summary>
    protected override void Die(in DamageInfoInput damageInput)
    {
        var upgradeRequired = _moduleData.UpgradeRequired?.Value;
        if (!GameObject.HasUpgrade(upgradeRequired))
        {
            // GameObject.HasUpgrade already treats a null template as "always satisfied" -
            // same single-check idiom as BunkerBusterBehavior.BustTheBunker.
            return;
        }

        if (_moduleData.BuildingRequired != null)
        {
            var killer = Context.GameLogic.GetObjectById(damageInput.SourceID);
            if (killer == null || !_moduleData.BuildingRequired.Matches(killer))
            {
                return;
            }
        }

        var amount = (uint)(GameObject.Definition.BuildCost * _moduleData.RefundPercent);
        GameObject.Owner.BankAccount.Deposit(amount);
    }

    // ---- the single walk: save/load + CRC + deep-dump + conformance. State inventory is
    // empty (identical posture to UpgradeDieModule), so the walk is exactly the version byte.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
    }

    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight (design-module-api §2.2). Field-type fix bundled with this
// port (GPL-adjacent precedent: UpgradeDie's own token-optionality fix, Die/UpgradeDie.cs:30-32):
// UpgradeRequired previously parsed via ParseAssetReference() into a bare string. To reach the
// landed GameObject.HasUpgrade(UpgradeTemplate) idiom it must be a resolved upgrade reference -
// the same type BunkerBusterBehaviorModuleData.UpgradeRequired already uses. BuildingRequired
// (ObjectFilter) and RefundPercent (Percentage) keep their pre-existing parse/type exactly - both
// were already the correct landed type for their idiom.
// ============================================================================
[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public sealed class RefundDieModuleData : DieModuleData
{
    internal static RefundDieModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<RefundDieModuleData> FieldParseTable = DieModuleData.FieldParseTable
        .Concat(new IniParseTable<RefundDieModuleData>
        {
            { "UpgradeRequired", (parser, x) => x.UpgradeRequired = parser.ParseUpgradeReference() },
            { "BuildingRequired", (parser, x) => x.BuildingRequired = ObjectFilter.Parse(parser) },
            { "RefundPercent", (parser, x) => x.RefundPercent = parser.ParsePercentage() }
        });

    public LazyAssetReference<UpgradeTemplate> UpgradeRequired { get; private set; }
    public ObjectFilter BuildingRequired { get; private set; }
    public Percentage RefundPercent { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new RefundDieModule(gameObject, gameEngine.SimContext, this);
    }
}
