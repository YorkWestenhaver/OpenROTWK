// InactiveBody - Body-batch port to the frozen module contract (api-freeze-v1 §3/§5,
// template v1.1 = pilot-autoheal §3/§6). This class deliberately does NOT touch the S1
// BodyDamageCore: an inactive body has no health ledger to apply damage to.
//
// Behavioral reference: generals-gpl GeneralsMD InactiveBody.cpp/.h (GPL semantics only;
// this is fresh code). Behavior facts used:
//   - ctor: setEffectivelyDead(true). The object is dead-on-arrival by construction.
//   - getHealth() == 0, getDamageState() == BODY_PRISTINE, setDamageState() is a no-op,
//     internalChangeHealth() is a no-op: there is no health storage at all.
//   - estimateDamage(): 0, except UNRESISTABLE returns the raw requested amount.
//   - attemptDamage(): HEALING redirects to attemptHealing; otherwise the output is
//     (dealt 0, clipped 0, noEffect true). The one exception is UNRESISTABLE, which always
//     "wipes us out": noEffect is cleared and, exactly once (guarded by m_dieCalled), the
//     object's DieModules run via onDie(). Crucially, InactiveBody does NOT call
//     DamageModules and does NOT run DamageFX (it has no health, so there is nothing to
//     react to) - only DieModules fire.
//   - attemptHealing(): non-HEALING redirects to attemptDamage; otherwise a pure
//     (0, 0, noEffect) no-op. Inactive bodies cannot be healed.
//
// MUTABLE SIM STATE INVENTORY: exactly one field, `_dieCalled` (bool) - the latch that
// keeps a repeated UNRESISTABLE hit from firing DieModules twice. The base BodyModule also
// owns the Fix64 `_damageScalar` (already contract state, walked via XferBodyBase).
//
// PERSISTENCE DECISION (recorded as a deviation in modules-r7/InactiveBody.md, D-1): the GPL
// InactiveBody::xfer writes only a version and chains to BodyModule::xfer - it does NOT
// persist m_dieCalled. Under our contract every mutable sim field appears in the walk exactly
// once (§3 item 1); omitting the latch would let a save taken between the first UNRESISTABLE
// hit and the object's reaping re-fire DieModules on load. We therefore walk `_dieCalled`.
// This is our layout, not the retail one (F9), and cannot affect Target-B parity (the oracle
// never dumps a field we added; extra fields are simply not compared).
//
// Every mutable sim field appears in Xfer exactly once (§3); tolerances are the field's
// conformance class at its declaration site (§4).

#nullable enable

using OpenSage.Data.Ini;

namespace OpenSage.Logic.Object;

/// <summary>
/// An inactive body module. They are indestructible and largely cannot be
/// affected by things in the world. Does not have data storage for health and
/// damage etc. It's an "inactive" object that isn't affected by matters of the
/// body... it's all in the mind!
/// </summary>
public sealed class InactiveBody : BodyModule
{
    // The sole mutable field: latch so a repeated UNRESISTABLE hit fires DieModules once.
    private bool _dieCalled;

    public override float Health => 0.0f; // Inactive bodies have no health to get.

    public override BodyDamageType DamageState
    {
        get => BodyDamageType.Pristine;
        set { }
    }

    internal InactiveBody(GameObject gameObject, IGameEngine gameEngine)
        : base(gameObject, gameEngine)
    {
        gameObject.IsEffectivelyDead = true;
    }

    public override DamageInfoOutput AttemptDamage(in DamageInfoInput damageInput)
    {
        if (damageInput.DamageType == DamageType.Healing)
        {
            // Healing and damage are separate, so this shouldn't happen.
            return AttemptHealing(damageInput);
        }

        // Inactive bodies have no health so no damage can really be done.
        var damageOutput = new DamageInfoOutput
        {
            ActualDamageDealt = 0.0f,
            ActualDamageClipped = 0.0f,
            NoEffect = true,
        };

        // ... except damage type UNRESISTABLE always wipes us out.
        if (damageInput.DamageType == DamageType.Unresistable)
        {
            // GPL guards this with DEBUG_ASSERTCRASH (a debug-build-only invariant check).
            // Our DebugUtility.AssertCrash throws in ALL builds, so malformed data (a
            // prerequisite object carrying an InactiveBody) becomes a hard fault here rather
            // than the original's silent continue. The invariant should hold for well-formed
            // data; the release-build divergence is recorded as a finding, not silently
            // changed (DebugUtility is framework - see modules-r7/InactiveBody.md, F-1).
            DebugUtility.AssertCrash(!GameObject.Definition.IsPrerequisite, "Prerequisites should not have InactiveBody");

            damageOutput.NoEffect = false;

            // Since we have no health, we do not call DamageModules, nor do
            // DamageFX. However, we DO process DieModules.
            if (!_dieCalled)
            {
                GameObject.OnDie(damageInput);
                _dieCalled = true;
            }
        }

        return damageOutput;
    }

    public override DamageInfoOutput AttemptHealing(in DamageInfoInput damageInput)
    {
        if (damageInput.DamageType != DamageType.Healing)
        {
            // Healing and damage are separate, so this shouldn't happen.
            return AttemptDamage(damageInput);
        }

        // Inactive bodies have no health so no healing can really be done.
        return new DamageInfoOutput
        {
            ActualDamageDealt = 0.0f,
            ActualDamageClipped = 0.0f,
            NoEffect = true,
        };
    }

    public override float EstimateDamage(in DamageInfoInput damageInfo)
    {
        // Inactive bodies have no health so no damage can really be done.
        var amount = 0.0f;

        // ... with this exception.
        if (damageInfo.DamageType == DamageType.Unresistable)
        {
            amount = damageInfo.Amount;
        }

        return amount;
    }

    public override void SetAflame(bool setting) { }

    public override void OnVeterancyLevelChanged(VeterancyLevel oldLevel, VeterancyLevel newLevel, bool provideFeedback) { }

    public override void SetArmorSetFlag(ArmorSetCondition armorSetCondition) { }

    public override void ClearArmorSetFlag(ArmorSetCondition armorSetType) { }

    public override bool TestArmorSetFlag(ArmorSetCondition armorSetType) => false;

    public override void InternalChangeHealth(float delta)
    {
        // Inactive bodies have no health to increase or decrease.
    }

    // ---- the contract Xfer walk (S1): the module's mutable sim state participates in
    // save/load + CRC + deep-dump. Field order = declaration order, ours (F9). ----

    internal override bool HasSimXfer => true;

    public override void Xfer(SimCore.Sync.IXfer xfer)
    {
        xfer.XferVersion(1);
        XferBodyBase(xfer);                       // base BodyModule::xfer: DamageScalar (Quantum)
        xfer.XferBool("DieCalled", ref _dieCalled); // our completeness addition (see header)
    }

    internal override void Load(StatePersister reader)
    {
        // Retail .sav layout (F9-exempt legacy reader): matches GPL InactiveBody::xfer,
        // which is version + BodyModule::xfer only and does NOT persist m_dieCalled. The
        // contract walk above is the forward-looking layout; this reader stays byte-faithful
        // to the original save format.
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();
    }
}

/// <summary>
/// Prevents normal interaction with other objects.
/// </summary>
[SimDataAudited]
public sealed class InactiveBodyModuleData : BodyModuleData
{
    internal static InactiveBodyModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    // InactiveBody carries no INI fields of its own (GPL InactiveBodyModuleData is empty),
    // so the audit to the S5 quantized vocabulary is vacuously clean: there is nothing to
    // convert away from float. The gapmap G1 non-regression check confirms parsing is
    // byte-identical to the baseline.
    private static readonly IniParseTable<InactiveBodyModuleData> FieldParseTable = [];

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new InactiveBody(gameObject, gameEngine);
    }
}
