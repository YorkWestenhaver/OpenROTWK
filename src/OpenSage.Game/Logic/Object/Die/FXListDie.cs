// FXListDie - Round-5 Die batch, class 1 of 11 (experiment-round-4 §4).
//
// Behavioral reference: generals-gpl GeneralsMD FXListDie.cpp/.h (GPL semantics reference
// only; this is fresh code against the frozen contract). Behavior facts used:
//   - the module is an upgrade mux AND a die module: state is exactly the mux's triggered
//     flag, and StartsActive defaults to TRUE (the original's own comment records this as a
//     1.02 patch decision - "should default to FALSE but only ONE case sets it false out of
//     847"), so an FXListDie with no upgrade fields is active from birth.
//   - onDie: return unless the mux is active; return unless the death is applicable
//     (DeathTypes / RequiredStatus / ExemptStatus); return if a CONFLICTING upgrade has been
//     completed on the object or by its controlling player - a re-check at death time, not
//     just at trigger time, so an upgrade acquired after triggering still suppresses the FX;
//     then, only if a DeathFX is named, fire it: oriented to the object (with the damage
//     dealer as the effect's source object) when OrientToObject, else unoriented at the
//     object's position.
//   - OrientToObject defaults to TRUE.
//   - there is no update, no timer, no RNG draw: the class's whole output is one FX event.
//
// The FX is an ISimEvents output (S8): it leaves the sim and never re-enters it, so it
// carries no determinism obligation. The mux flag is the only mutable sim state, and it is
// in the Xfer walk exactly once (§3).
//
// Ordering note vs the reference: the original checks the mux BEFORE the death-applicability
// filter; here the shared DieModule base runs applicability first and then calls Die. Both
// predicates are pure, so the observable result is identical.

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class FXListDie : DieModule, IUpgradeableModule
{
    private readonly FXListDieModuleData _data;
    private readonly UpgradeLogic _upgradeLogic;

    // ---- mutable sim state ----
    // The mux's triggered flag, owned by UpgradeLogic and walked through UpgradeLogic.Xfer.
    // Nothing else in this class is mutable: an FXListDie that has fired is
    // indistinguishable from one that has not.

    public FXListDie(GameObject gameObject, ISimContext context, FXListDieModuleData data)
        : base(gameObject, context, data)
    {
        _data = data;

        // The mux fires its callback from the ctor when StartsActive (the original's
        // giveSelfUpgrade); this module has nothing to do on trigger.
        _upgradeLogic = new UpgradeLogic(data.UpgradeData, OnUpgradeTriggered);
    }

    private static void OnUpgradeTriggered()
    {
        // The original's upgradeImplementation() is empty: being active is the whole effect.
    }

    public bool CanUpgrade(UpgradeSet existingUpgrades) => _upgradeLogic.CanUpgrade(existingUpgrades);

    public void TryUpgrade(UpgradeSet completedUpgrades) => _upgradeLogic.TryUpgrade(completedUpgrades);

    /// <summary>
    /// Reached only for deaths that passed <c>DieLogicData.IsDieApplicable</c> (the base).
    /// </summary>
    protected override void Die(in DamageInfoInput damageInput)
    {
        if (!_upgradeLogic.Triggered)
        {
            return;
        }

        if (HasConflictingUpgrade())
        {
            return;
        }

        if (string.IsNullOrEmpty(_data.DeathFX))
        {
            return;
        }

        if (_data.OrientToObject)
        {
            // doFXObj: the corpse orients the effect, the damage dealer is its source.
            Context.Events.FireFXAtObject(_data.DeathFX, GameObject.Id, damageInput.SourceID);
        }
        else
        {
            // doFXPos: position only, no orientation.
            Context.Events.FireFXAtObjectPosition(_data.DeathFX, GameObject.Id);
        }
    }

    /// <summary>
    /// The death-time conflict re-check. Set membership only - no set is ever enumerated, so
    /// no iteration order can leak into the decision.
    /// </summary>
    private bool HasConflictingUpgrade()
    {
        var conflicting = _data.UpgradeData.ConflictsWithHashSet;
        if (conflicting.Count == 0)
        {
            return false;
        }

        return GameObject.CompletedUpgradesIncludingPlayer.Overlaps(conflicting);
    }

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----
    // Field order = declaration order = OUR choice (F9). Every field here is an Exact-class
    // lifecycle flag; this class has no Quantum-class field at all, so ruling A3's Target-A
    // collapse is vacuously satisfied and its Target-B half never applies.
    //
    // Divergence from the reference, deliberately: the original's FXListDie::xfer writes only
    // a version and the (empty) DieModule base - it never xfers its own UpgradeMux state, so
    // a save taken while an FXListDie was untriggered reloads as triggered. Our contract says
    // every mutable sim field appears in the walk exactly once (§3), so the flag is written.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        _upgradeLogic.Xfer(xfer);   // "UpgradeTriggered", Exact
    }

    // ---- legacy retail-save reader (outside the contract, F9): kept and remapped per the
    // template's replace-an-existing-module rule (D-9). The retail layout is base only - the
    // original wrote no mux state here either - so this stays byte-compatible with the
    // stream it has always read. ----
    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight (design-module-api §2.2). Nothing here is a
// magnitude: no Fix64, no duration, no angle, so the S5 quantizing functions have
// nothing to quantize in this class. Recorded, not skipped.
// ============================================================================
[SimDataAudited]
public sealed class FXListDieModuleData : DieModuleData
{
    internal static FXListDieModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<FXListDieModuleData> FieldParseTable = DieModuleData.FieldParseTable
        .Concat(new IniParseTableChild<FXListDieModuleData, UpgradeLogicData>(x => x.UpgradeData, UpgradeLogicData.FieldParseTable))
        .Concat(new IniParseTable<FXListDieModuleData>
        {
            { "DeathFX", (parser, x) => x.DeathFX = parser.ParseAssetReference() },
            { "OrientToObject", (parser, x) => x.OrientToObject = parser.ParseBoolean() },
        });

    /// <summary>
    /// The full upgrade-mux vocabulary (TriggeredBy / ConflictsWith / RequiresAllTriggers /
    /// RequiresAllConflictingTriggers / StartsActive / ...), shared with every other mux.
    /// <c>StartsActive</c> is seeded TRUE because that is this class's documented default -
    /// an FXListDie block that names no upgrade is active. In the AotR 9.3.1 corpus that is
    /// EVERY block: of 173 FXListDie blocks, none sets StartsActive, TriggeredBy or
    /// ConflictsWith, so the mux path below is reachable only from BFME2/ZH data and from our
    /// own tests and driver scenario. Seeding it wrong would silence all 173.
    /// </summary>
    public UpgradeLogicData UpgradeData { get; } = new() { StartsActive = true };

    /// <summary>Name of the FX list played on death; empty means "no FX", which is legal.</summary>
    public string DeathFX { get; private set; }

    /// <summary>
    /// Whether the FX takes the dying object's orientation as well as its position.
    /// Defaults TRUE (the reference's m_orientToObject); the three AotR 9.3.1 blocks that
    /// mention it all set it to No, so both branches are corpus-reachable.
    /// </summary>
    public bool OrientToObject { get; private set; } = true;

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new FXListDie(gameObject, gameEngine.SimContext, this);
    }
}
