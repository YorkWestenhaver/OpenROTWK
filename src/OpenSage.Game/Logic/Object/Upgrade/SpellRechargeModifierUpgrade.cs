// SpellRechargeModifierUpgrade - R13 port (modules-r13, spec:
// research/modules-r13/specs/SpellRechargeModifierUpgradeModuleData.md). No GPL file of its
// own exists (SpellRecharge* has zero hits under generals-gpl/generals-community); ported by
// analogy to the audited structural sibling CostModifierUpgrade (identical field shape:
// { LabelForPalantirString, List<Percentage> }, same UpgradeModuleData base, same mux-only
// Xfer shape), per the spec's Grounding Check.
//
// Behavior facts reproduced from the sibling pattern (see spec §1):
//   - state is exactly the upgrade mux triggered flag; the module holds NO economy state of
//     its own beyond that.
//   - OnUpgrade(): register each Percentage on the controlling Player's special-power
//     recharge-discount registry.
//   - RemoveFromPlayer(): undo the registration (no landed OnDestroy hook calls this yet -
//     filed finding, same as the sibling).
//   - OnCapture(oldOwner, newOwner): move the registration between players (no landed capture
//     hook calls this yet - filed finding, same as the sibling).
//   - ReapplyAfterLoad(): if the loaded mux flag says triggered and the transient registry
//     entry is missing, re-register (no landed post-load module pass calls this yet - filed
//     finding, same as the sibling).
//
// Unlike CostModifierUpgrade, this module has no EffectKindOf (or any other scoping) field -
// the registered discount is global to the player, gated at the *consuming* end by each
// special power's own SpecialPowerFlag.RespectRechargeTimeDiscount opt-in (spec F-SRM-2).
// Consuming the registered factor into an actual recharge-timer computation is out of scope:
// no recharge timer exists in this engine snapshot yet (spec F-SRM-1), exactly the same shape
// as the sibling's own un-wired build-cost consumption.
//
// Determinism correction from the pre-port stub: the stub parsed Percentage via
// parser.ParsePercentage() into the legacy float-backed OpenSage.Mathematics.Percentage type.
// Per the Fix64 rule and to match the sibling's own idiom exactly, this port parses
// Percentage via parser.ParseFix64Percentage() into List<Fix64>.
//
// The single Xfer walk carries every mutable sim field (the mux flag) exactly once.

using System.Collections.Generic;
using OpenSage.Data.Ini;
using OpenSage.Logic.Economy;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class SpellRechargeModifierUpgrade : BehaviorModule, IUpgradeableModule
{
    private readonly SpellRechargeModifierUpgradeModuleData _data;
    private readonly UpgradeLogic _upgradeLogic;

    // ---- derived runtime state (NOT xfered: rebuilt from the mux flag on load) ----
    // True while this module currently owns an entry in the player's recharge-discount
    // registry, so apply/remove stay balanced across upgrade, capture, delete and post-load
    // reconstruction.
    private bool _appliedToPlayer;

    public SpellRechargeModifierUpgrade(GameObject gameObject, ISimContext context, SpellRechargeModifierUpgradeModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        // The mux fires OnUpgrade from its ctor when StartsActive (same as the sibling), which
        // registers the modifier immediately.
        _upgradeLogic = new UpgradeLogic(data.UpgradeData, OnUpgrade);
    }

    public bool CanUpgrade(UpgradeSet existingUpgrades) => _upgradeLogic.CanUpgrade(existingUpgrades);

    public void TryUpgrade(UpgradeSet completedUpgrades) => _upgradeLogic.TryUpgrade(completedUpgrades);

    /// <summary>Whether this module has been triggered (test/inspection surface).</summary>
    public bool IsUpgraded => _upgradeLogic.Triggered;

    private void OnUpgrade()
    {
        ApplyToPlayer(GameObject.Owner);
    }

    /// <summary>
    /// When this module goes away, undo its registration. No landed contract lifecycle hook
    /// calls this yet (BehaviorModule has no OnDestroy) - the engine wiring is a recorded
    /// finding; exposed here so the owner/tests can drive it.
    /// </summary>
    public void RemoveFromPlayer()
    {
        RemoveFromPlayer(GameObject.Owner);
    }

    /// <summary>
    /// Move the registration between players on capture. No landed capture hook calls this
    /// yet (recorded finding); exposed for the owner/tests.
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
    /// applied, re-register. No landed post-load module pass calls this yet (recorded
    /// finding); tests call it directly.
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
        if (_appliedToPlayer || player is null)
        {
            return;
        }

        foreach (var percent in _data.Percentages)
        {
            player.SpecialPowerRechargeDiscount.Add(percent);
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
            player.SpecialPowerRechargeDiscount.Remove(percent);
        }
        _appliedToPlayer = false;
    }

    // ---- the single walk (S4/§3): save/load + CRC + deep-dump + conformance ----
    // Field order = declaration order = OUR choice (F9). The mux flag IS the base's state, so
    // the walk matches the sibling's shape exactly.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        _upgradeLogic.Xfer(xfer);   // UpgradeTriggered, Exact (XferBool)
    }
}

// SpellRechargeModifierUpgradeModuleData: parses Percentage(s) and LabelForPalantirString.
// Audited to the S5 quantized vocabulary: Percentage -> Fix64 via ParseFix64Percentage.
// LabelForPalantirString is a BFME UI label, stored but not consumed (spec F-SRM-3), same
// treatment as the sibling's identically-named field.
[SimDataAudited]
public sealed class SpellRechargeModifierUpgradeModuleData : UpgradeModuleData
{
    internal static SpellRechargeModifierUpgradeModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<SpellRechargeModifierUpgradeModuleData> FieldParseTable = UpgradeModuleData.FieldParseTable
        .Concat(new IniParseTable<SpellRechargeModifierUpgradeModuleData>
        {
            { "LabelForPalantirString", (parser, x) => x.LabelForPalantirString = parser.ParseLocalizedStringKey() },
            { "Percentage", (parser, x) => x.Percentages.Add(parser.ParseFix64Percentage()) }
        });

    /// <summary>BFME UI label. Parsed (audited); not consumed by this port.</summary>
    public string LabelForPalantirString { get; private set; }

    /// <summary>
    /// Special-power recharge-time discount percentages, quantized (Q31.32): "-20%" -> -0.20.
    /// Registered on the controlling Player's <see cref="Economy.SpecialPowerRechargeDiscountRegistry"/>
    /// when this module is triggered.
    /// </summary>
    public List<Fix64> Percentages { get; } = new();

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new SpellRechargeModifierUpgrade(gameObject, gameEngine.SimContext, this);
    }
}
