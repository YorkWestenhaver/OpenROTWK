// UpgradeDie - Die-batch port to the frozen module contract (api-freeze-v1 §3/§5,
// template v1.1 = pilot-autoheal §3/§6).
//
// Behavioral reference: generals-gpl GeneralsMD UpgradeDie.cpp/.h (GPL semantics reference
// only; this is fresh code). Behavior facts used:
//   - onDie(): applicability filter first (DeathTypes / RequiredStatus / ExemptStatus, the
//     shared DieLogicData gate); then find the PRODUCER - the object that created this one -
//     and free the named upgrade on it. "Used in cases where the producer builds an upgrade
//     that can die... like ranger building scout drones."
//   - Every step is a silent no-op when it cannot proceed: no producer (it died first, or
//     this object was never produced), no such upgrade template, or a producer that does not
//     actually hold the upgrade. The last case is a debug assert in the original, i.e. a data
//     error that is deliberately NOT a runtime effect - so it must not remove anything.
//   - MUTABLE SIM STATE INVENTORY: empty. GPL's UpgradeDie has no fields; its xfer() is a
//     version byte plus the (also stateless) DieModule base. The removal is a one-shot effect
//     on ANOTHER object at death, not state carried by this module - which is why the walk
//     below is a version byte and nothing else, and why that is completeness, not an omission.
//
// BFME2-only INI addition: UpgradeToRemove carries an optional SECOND token (a module tag -
// AotR writes "Upgrade_TestBuilding BaseUpgradeTag_01"). ZH's field is a single AsciiString.
// The token is parsed and stored but deliberately not acted on: no GPL reference and no
// Ghidra behavioral spec says what the original does with it (see UpgradeDie.md, behavior-fact
// gaps). Making it OPTIONAL is the parse fix that this audit carries - eight AotR files use
// the one-token form and currently fail to parse at that line.
//
// Category note: this module composes IDieModule onto BehaviorModule rather than deriving
// from the DieModule category base, because that base has no ISimContext ctor yet. Composition
// is blessed by the contract (api-freeze-v1 §3 item 4, "multi-category composition via
// IDieModule ... dispatched in ModuleIndex order") and is what FireWeaponWhenDeadBehavior and
// GenerateMinefieldBehavior already do. Filed as a finding, not patched here (no task touches
// the framework).

using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class UpgradeDieModule : BehaviorModule, IDieModule
{
    private readonly UpgradeDieModuleData _data;

    public UpgradeDieModule(GameObject gameObject, ISimContext context, UpgradeDieModuleData data)
        : base(gameObject, context)
    {
        _data = data;
    }

    void IDieModule.OnDie(in DamageInfoInput damageInput)
    {
        // The shared Die gate (DeathTypes / RequiredStatus / ExemptStatus).
        if (!_data.DieData.IsDieApplicable(damageInput, GameObject))
        {
            return;
        }

        // Look for the object that created me. It may already be gone; that is normal.
        var producer = Context.GameLogic.GetObjectById(GameObject.CreatedByObjectID);
        if (producer is null)
        {
            return;
        }

        var upgrade = _data.UpgradeToRemove.UpgradeName?.Value;
        if (upgrade is null)
        {
            return;
        }

        // GPL asserts (and does nothing) when the producer does not hold the upgrade: a data
        // error must not silently mutate the producer's upgrade set.
        if (!producer.HasUpgrade(upgrade))
        {
            return;
        }

        producer.RemoveUpgrade(upgrade);
    }

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----
    // Field order = declaration order = OURS (F9). The inventory above is empty, so the walk
    // is exactly the version byte: no field means no tolerance class to declare, and the
    // shadow-copy test still proves the round trip is byte-stable and version-correct.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
    }

    // ---- legacy retail-save reader (outside the contract, F9; template rule D-9: a port
    // that replaces an existing module KEEPS its Load and remaps it). The original stream
    // nests UpgradeDie's version over DieModule's over BehaviorModule's; composing IDieModule
    // instead of deriving from DieModule removes a C# base class, not a byte, so the middle
    // level is written out explicitly here to keep the layout identical. ----
    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        {
            // DieModule's own level.
            reader.PersistVersion(1);

            reader.BeginObject("Base");
            base.Load(reader);
            reader.EndObject();
        }
        reader.EndObject();
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight (design-module-api §2.2). Audited: this class has no
// numeric, duration, angle, or vector fields at all, so the S5 quantizing vocabulary
// (ParseFix64 / ParseDurationLogicFrames / ParseAngleDegrees / ParseFixVector3) has nothing
// to convert here - the whole payload is an upgrade reference plus an identifier, and the
// inherited DieLogicData gate (DeathTypes / RequiredStatus / ExemptStatus). Recorded rather
// than assumed: an audit that touches nothing is still an audit.
// ============================================================================
[SimDataAudited]
public sealed class UpgradeDieModuleData : DieModuleData
{
    internal static UpgradeDieModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<UpgradeDieModuleData> FieldParseTable = DieModuleData.FieldParseTable
        .Concat(new IniParseTable<UpgradeDieModuleData>
        {
            { "UpgradeToRemove", (parser, x) => x.UpgradeToRemove = UpgradeToRemove.Parse(parser) }
        });

    /// <summary>The upgrade freed on the producer at death (plus its unconsumed BFME2 tag).</summary>
    public UpgradeToRemove UpgradeToRemove { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new UpgradeDieModule(gameObject, gameEngine.SimContext, this);
    }
}

/// <summary>
/// The <c>UpgradeToRemove</c> payload: an upgrade name, and - BFME2 only - an optional module
/// tag naming the module on the producer that granted it. ZH parses one token
/// (<c>INI::parseAsciiString</c>); AotR writes both forms, six object files with the bare
/// upgrade name and two with a trailing tag.
/// </summary>
public readonly struct UpgradeToRemove
{
    internal static UpgradeToRemove Parse(IniParser parser)
    {
        var upgradeName = parser.ParseUpgradeReference();

        // Optional (the ZH form ends the line here). A required token throws
        // "Expected a token" and takes the whole file down with it.
        var moduleTag = parser.GetNextTokenOptional();

        return new UpgradeToRemove(upgradeName, moduleTag?.Text);
    }

    internal UpgradeToRemove(LazyAssetReference<UpgradeTemplate> upgradeName, string moduleTag)
    {
        UpgradeName = upgradeName;
        ModuleTag = moduleTag;
    }

    public LazyAssetReference<UpgradeTemplate> UpgradeName { get; }

    /// <summary>
    /// BFME2-only second token. Parsed and stored; no GPL reference or Ghidra behavioral spec
    /// says what the original does with it, so nothing acts on it (behavior-fact gap).
    /// Null for the one-token ZH form.
    /// </summary>
    [AddedIn(SageGame.Bfme2)]
    public string ModuleTag { get; }
}
