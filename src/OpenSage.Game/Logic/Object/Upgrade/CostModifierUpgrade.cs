// CostModifierUpgrade - R9 port (experiment-round-4 4.1, template v1.1). Pure economy.
//
// Behavioral reference: generals-gpl GeneralsMD CostModifierUpgrade.cpp/.h +
// Common/RTS/Player.cpp add/remove/getKindOfProductionCostChange (GPL semantics only; this
// is fresh code against the frozen contract). Behavior facts used:
//   - state is exactly the upgrade mux triggered flag; the module holds NO economy state of
//     its own (GPL xfer is literally version + base UpgradeModule::xfer). The accumulated
//     modifier lives on the owning Player (KindOfProductionCostRegistry).
//   - upgradeImplementation(): on trigger, register (EffectKindOf, Percentage) on the
//     controlling Player so subsequent builds of matching KindOf cost (1 + percent) as much.
//   - onDelete(): if triggered, remove the registration (ReafCount down) and mark not-upgraded.
//   - onCapture(oldOwner,newOwner): move the registration from old to new player.
//
// Contract shape (why this subclasses BehaviorModule, not UpgradeModule): UpgradeModule's
// only ctor is the legacy (GameObject, IGameEngine) path and its UpgradeLogic mux is private,
// so a subclass cannot produce the contract Xfer walk. The pilot (AutoHealBehavior, the
// canonical template) composes its own UpgradeLogic instead; this port follows that exact
// pattern. Recorded as a delta in research/modules-r9/CostModifierUpgradeModuleData.md.
//
// GPL empty-mask note: Player::getProductionCostChangeBasedOnKindOf uses testSetAndClear,
// under which an EMPTY EffectKindOf mask matches EVERY object (a global discount). BFME2/AotR
// data almost never sets EffectKindOf - it uses ObjectFilter (+ UpgradeDiscount /
// ApplyToTheseUpgrades) instead, which has no GPL or written behavioral spec. To avoid
// silently applying a global production discount for those blocks, registration is gated on a
// non-empty EffectKindOf (the only GPL-evidenced path). The ObjectFilter / UpgradeDiscount /
// ApplyToTheseUpgrades / Slaughter / LabelForPalantirString fields are parsed (audited
// vocabulary) but deliberately not acted on - see the behavior-fact gaps in the research doc.
//
// The single Xfer walk carries every mutable sim field (the mux flag) exactly once.

using System.Collections.Generic;
using OpenSage.Data.Ini;
using OpenSage.Logic.Economy;
using OpenSage.Mathematics;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class CostModifierUpgrade : BehaviorModule, IUpgradeableModule
{
    private readonly CostModifierUpgradeModuleData _data;
    private readonly UpgradeLogic _upgradeLogic;

    // ---- derived runtime state (NOT xfered: rebuilt from the mux flag on load) ----
    // True while this module currently owns an entry in the player's cost registry, so
    // apply/remove stay balanced across upgrade, capture, delete and post-load reconstruction.
    private bool _appliedToPlayer;

    public CostModifierUpgrade(GameObject gameObject, ISimContext context, CostModifierUpgradeModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        // The mux fires OnUpgrade from its ctor when StartsActive (GPL: the module upgrades
        // itself on construction), which registers the modifier immediately.
        _upgradeLogic = new UpgradeLogic(data.UpgradeData, OnUpgrade);
    }

    public bool CanUpgrade(UpgradeSet existingUpgrades) => _upgradeLogic.CanUpgrade(existingUpgrades);

    public void TryUpgrade(UpgradeSet completedUpgrades) => _upgradeLogic.TryUpgrade(completedUpgrades);

    /// <summary>Whether this module has been triggered (test/inspection surface).</summary>
    public bool IsUpgraded => _upgradeLogic.Triggered;

    private void OnUpgrade()
    {
        // GPL upgradeImplementation(): register the production-cost change on the player.
        ApplyToPlayer(GameObject.Owner);
    }

    /// <summary>
    /// GPL <c>onDelete</c>: when this module goes away, undo its registration. No landed
    /// contract lifecycle hook calls this yet (BehaviorModule has no OnDestroy) - the engine
    /// wiring is a recorded finding; exposed here so the owner/tests can drive it.
    /// </summary>
    public void RemoveFromPlayer()
    {
        RemoveFromPlayer(GameObject.Owner);
    }

    /// <summary>
    /// GPL <c>onCapture(oldOwner, newOwner)</c>: move the registration between players. No
    /// landed capture hook calls this yet (recorded finding); exposed for the owner/tests.
    /// </summary>
    public void OnCapture(Player oldOwner, Player newOwner)
    {
        if (!_upgradeLogic.Triggered)
        {
            return;
        }

        RemoveFromPlayer(oldOwner);
        ApplyToPlayer(newOwner);
    }

    /// <summary>
    /// Post-load reconstruction of the transient player registry (the registry is derived
    /// state, not serialized): if the loaded mux flag says triggered and we are not currently
    /// applied, re-register. Mirrors the pilot's engine-owned wake-frame restore - the engine
    /// needs a post-load module pass to call this (recorded finding); tests call it directly.
    /// </summary>
    public void ReapplyAfterLoad()
    {
        if (_upgradeLogic.Triggered && !_appliedToPlayer)
        {
            ApplyToPlayer(GameObject.Owner);
        }
    }

    private void ApplyToPlayer(Player player)
    {
        if (_appliedToPlayer || player is null || !_data.EffectKindOf.AnyBitSet)
        {
            return;
        }

        foreach (var percent in _data.Percentages)
        {
            player.ProductionCostModifiers.Add(_data.EffectKindOf, percent);
        }
        _appliedToPlayer = true;
    }

    private void RemoveFromPlayer(Player player)
    {
        if (!_appliedToPlayer || player is null)
        {
            return;
        }

        foreach (var percent in _data.Percentages)
        {
            player.ProductionCostModifiers.Remove(_data.EffectKindOf, percent);
        }
        _appliedToPlayer = false;
    }

    // ---- the single walk (S4/§3): save/load + CRC + deep-dump + conformance ----
    // Field order = declaration order = OUR choice (F9). GPL's xfer is version + base only;
    // the mux flag IS the base's state, so the walk matches GPL's shape exactly.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        _upgradeLogic.Xfer(xfer);   // UpgradeTriggered, Exact (XferBool)
    }
}

// CostModifierUpgradeModuleData: parses EffectKindOf, Percentage(s), and the BFME2 additions
// (LabelForPalantirString, ObjectFilter, UpgradeDiscount, ApplyToTheseUpgrades, Slaughter).
// Audited to the S5 quantized vocabulary: Percentage -> Fix64 via ParseFix64Percentage;
// EffectKindOf -> KindOf bitmask (GPL KindOfMaskType::parseFromINI). The BFME2 fields have no
// GPL semantics and are stored but not consumed (recorded behavior-fact gaps).
[SimDataAudited]
public sealed class CostModifierUpgradeModuleData : UpgradeModuleData
{
    internal static CostModifierUpgradeModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<CostModifierUpgradeModuleData> FieldParseTable = UpgradeModuleData.FieldParseTable
        .Concat(new IniParseTable<CostModifierUpgradeModuleData>
        {
            { "EffectKindOf", (parser, x) => x.EffectKindOf = parser.ParseEnumBitArray<ObjectKinds>() },
            { "Percentage", (parser, x) => x.Percentages.Add(parser.ParseFix64Percentage()) },
            { "LabelForPalantirString", (parser, x) => x.LabelForPalantirString = parser.ParseLocalizedStringKey() },
            { "ObjectFilter", (parser, x) => x.ObjectFilter = ObjectFilter.Parse(parser) },
            { "UpgradeDiscount", (parser, x) => x.UpgradeDiscount = parser.ParseBoolean() },
            { "ApplyToTheseUpgrades", (parser, x) => x.ApplyToTheseUpgrades = parser.ParseAssetReferenceArray() },
            { "Slaughter", (parser, x) => x.Slaughter = parser.ParseBoolean() },
        });

    /// <summary>Kinds whose production cost this upgrade modifies (GPL m_kindOf mask).</summary>
    public BitArray<ObjectKinds> EffectKindOf { get; private set; } = new();

    /// <summary>
    /// Cost change percentages, quantized (Q31.32): "-10%" -> -0.10. GPL is a single
    /// m_percentage; BFME2 accepts a list, applied multiplicatively (one (1 + percent)
    /// registration each).
    /// </summary>
    public List<Fix64> Percentages { get; } = new();

    [AddedIn(SageGame.Bfme)]
    public string LabelForPalantirString { get; private set; }

    /// <summary>BFME2 object-scope filter. Parsed (audited); no GPL/spec reference, not acted on.</summary>
    [AddedIn(SageGame.Bfme)]
    public ObjectFilter ObjectFilter { get; private set; }

    /// <summary>BFME2: also discount upgrade purchases. Parsed (audited); not acted on.</summary>
    [AddedIn(SageGame.Bfme)]
    public bool UpgradeDiscount { get; private set; }

    /// <summary>BFME2: specific upgrades this discount applies to. Parsed (audited); not acted on.</summary>
    [AddedIn(SageGame.Bfme2)]
    public string[] ApplyToTheseUpgrades { get; private set; }

    /// <summary>BFME2 flag. Parsed (audited); no GPL/spec reference, not acted on.</summary>
    [AddedIn(SageGame.Bfme2)]
    public bool Slaughter { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new CostModifierUpgrade(gameObject, gameEngine.SimContext, this);
    }
}
