// UpgradeDie - Die-batch port to the frozen module contract (api-freeze-v1 §3/§5 as amended by
// api-freeze-amendments-v1.1, template v1.1 = pilot-autoheal §3/§6).
//
// Behavioral reference: generals-gpl GeneralsMD UpgradeDie.cpp/.h (GPL semantics reference
// only; this is fresh code). Behavior facts used:
//   - onDie(): the shared applicability gate first (DeathTypes / RequiredStatus / ExemptStatus,
//     i.e. DieLogicData) - which on this branch is the CATEGORY BASE's job, not this class's:
//     DieModule.IDieModule.OnDie runs it and only then calls Die() below. So this file starts
//     where GPL's isDieApplicable early-out ends.
//   - Then find the PRODUCER - the object that created this one - and free the named upgrade
//     on it. "Used in cases where the producer builds an upgrade that can die... like ranger
//     building scout drones."
//   - Every remaining step is a silent no-op when it cannot proceed: no producer (it died
//     first, or this object was never produced), no such upgrade template, or a producer that
//     does not actually hold the upgrade. The last case is a DEBUG_ASSERTCRASH in the original,
//     i.e. a data error that is deliberately NOT a runtime effect - so it must not remove
//     anything. (The pre-port fork code called RemoveUpgrade unconditionally; that is the one
//     behavioral correction this port makes, and it is GPL-sourced.)
//   - MUTABLE SIM STATE INVENTORY: empty. GPL's UpgradeDie has no members; its xfer() is a
//     version byte plus the (also stateless) DieModule base, and its crc() carries nothing.
//     The removal is a one-shot effect on ANOTHER object at death, not state carried by this
//     module - which is why the walk below is a version byte and nothing else, and why that is
//     completeness rather than an omission. No hasFired flag is invented: re-entry is prevented
//     by ActiveBody's >0 -> <=0 health crossing, not by module state (template lesson D-10).
//
// BFME2-only INI addition: UpgradeToRemove carries an optional SECOND token (a module tag -
// AotR writes "Upgrade_TestBuilding BaseUpgradeTag_01"). ZH's field is a single AsciiString
// (INI::parseAsciiString). The token is parsed and stored but deliberately not acted on: no GPL
// reference and no Ghidra behavioral spec says what the original does with it (see
// research/die/UpgradeDie.md, behavior-fact gaps). Making the token OPTIONAL is the parse fix
// this audit carries - the pre-port code called ParseIdentifier(), which is required, so every
// AotR object file using the bare ZH one-token form died at that line.

using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class UpgradeDieModule : DieModule
{
    private readonly UpgradeDieModuleData _moduleData;

    internal UpgradeDieModule(GameObject gameObject, ISimContext context, UpgradeDieModuleData moduleData)
        : base(gameObject, context, moduleData)
    {
        _moduleData = moduleData;
    }

    /// <summary>
    /// Frees the producer's upgrade. Reached only when the base's shared
    /// <c>DieLogicData</c> gate has already passed.
    /// </summary>
    protected override void Die(in DamageInfoInput damageInput)
    {
        // Look for the object that created me. It may already be gone; that is normal.
        var producer = Context.GameLogic.GetObjectById(GameObject.CreatedByObjectID);
        if (producer is null)
        {
            return;
        }

        var upgrade = _moduleData.UpgradeToRemove.UpgradeName?.Value;
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
    // Field order = declaration order = OURS (F9). The inventory above is empty, so the walk is
    // exactly the version byte. No field means no tolerance class to declare at a call site
    // (ruling A3 is therefore vacuous here, and recorded as vacuous rather than skipped); the
    // shadow-copy test still proves the round trip is byte-stable and version-correct.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
    }

    // ---- legacy retail-save reader (outside the contract, F9; template rule D-9: a port that
    // replaces an existing module KEEPS its Load and remaps it). Byte layout unchanged from the
    // pre-port class: this level's version, then the DieModule base level. There are no fields
    // to remap - the inventory is empty on both sides. ----
    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight (design-module-api §2.2). Audited: this class has no
// numeric, duration, angle, or vector fields at all, so the S5 quantizing vocabulary
// (ParseFix64 / ParseFix64Percentage / ParseDurationLogicFrames / ParseAngleDegrees /
// ParseFixVector3) has nothing to convert here - the whole payload is an upgrade reference plus
// an identifier, on top of the inherited DieLogicData gate (DeathTypes / RequiredStatus /
// ExemptStatus), which is enum and bit-array data. Recorded rather than assumed: an audit that
// converts nothing is still an audit, and its finding is "no quantization surface".
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
/// (<c>INI::parseAsciiString</c>); AotR writes both forms.
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
