// BoneFXDamage - Round-7 Damage-batch port (full task packet, template v1.1).
//
// Behavioral reference: generals-gpl GeneralsMD BoneFXDamage.cpp/.h (GPL semantics reference
// only; this is fresh code against the frozen contract). Behavior facts used:
//   - onObjectCreated(): the module REQUIRES a paired BoneFXUpdate on the same object; GPL
//     asserts and then `throw INI_INVALID_DATA` when it is absent. It is the "damage half" of
//     the BoneFX pair - it carries no FX data of its own, it only relays state changes into the
//     BoneFXUpdate that owns the bones, particle systems and FX lists.
//   - onBodyDamageStateChange(damageInfo, oldState, newState): finds the BoneFXUpdate sibling
//     and calls `changeBodyDamageState(oldState, newState)` on it (which records the new state,
//     kills running particle systems and re-inits the spawn timers). The sibling is re-found on
//     each call (GPL uses a static NameKey lookup, not a cached pointer) and a null sibling is
//     tolerated silently at this point (the hard requirement was already enforced at creation).
//   - onDamage() / onHealing(): deliberately EMPTY in GPL - BoneFXDamage reacts only to the
//     discrete damage-STATE transitions, never to every hit. Not overridden here.
//   - crc/xfer/loadPostProcess: version(1) then extend the base; no members of its own.
//
// MUTABLE SIM STATE INVENTORY (written before any code, runbook step 1):
//   *** EMPTY ***. GPL BoneFXDamage declares no members; its xfer is a version tag extending the
//   base. The whole behavior is a stateless relay into the sibling BoneFXUpdate. The Xfer walk
//   below is therefore version-only - complete with respect to the inventory.
//
// The FX itself (particle systems / FX lists spawned from bones) is a client-bound rendering
// output owned by BoneFXUpdate, which is NOT yet ported (its Update is a stub). This port wires
// the relay end-to-end - creation validation + the state-change dispatch that ActiveBody now
// delivers to IDamageModule siblings (S1) - and lands the one sim-visible line of the sibling
// call (CurrentBodyState). The downstream particle spawn/kill + timer re-init are a documented
// TODO in the unported BoneFXUpdate. See modules-r7/BoneFXDamage.md finding F-BFXD-1.

using System;
using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class BoneFXDamage : DamageModule
{
    private readonly BoneFXDamageModuleData _data;

    // ---- mutable sim state: NONE (see the inventory above) ----

    public BoneFXDamage(GameObject gameObject, ISimContext context, BoneFXDamageModuleData data)
        : base(gameObject, context)
    {
        _data = data;
    }

    /// <summary>
    /// GPL onObjectCreated(): BoneFXDamage is inert without the BoneFXUpdate that owns the bones
    /// and FX. Enforce the pairing at creation (after every module exists — this is the
    /// inter-module resolution pass), matching GPL's `throw INI_INVALID_DATA`.
    /// </summary>
    protected internal override void OnObjectCreated()
    {
        if (GameObject.FindBehavior<BoneFXUpdate>() is null)
        {
            throw new InvalidOperationException(
                $"BoneFXDamage on '{GameObject.Definition.Name}' requires a BoneFXUpdate module " +
                "on the same object.");
        }
    }

    /// <summary>
    /// GPL onBodyDamageStateChange(): relay the discrete damage-state transition into the paired
    /// BoneFXUpdate so it can swap its pristine/rubble FX. ActiveBody drives this on every
    /// Pristine→Damaged→ReallyDamaged→Rubble crossing (and the healing direction) now that S1's
    /// body dispatches to IDamageModule siblings. The sibling is re-found per call (GPL shape);
    /// a missing sibling is tolerated here (the create-time check already guaranteed it, and a
    /// later removal must not crash a state transition).
    /// </summary>
    public override void OnBodyDamageStateChange(
        in DamageInfo damageInfo,
        BodyDamageType oldState,
        BodyDamageType newState)
    {
        GameObject.FindBehavior<BoneFXUpdate>()?.ChangeBodyDamageState(oldState, newState);
    }

    // GPL onDamage()/onHealing() are empty: BoneFXDamage reacts to state transitions only, not to
    // individual hits. Left as the DamageModule no-op defaults on purpose.

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----
    // No own sim state: version tag extending the base, declaration order ours (F9).

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        // No fields: the state inventory is empty. GPL BoneFXDamage::xfer is likewise version +
        // base only.
    }

    // ---- legacy retail-save reader (outside the contract, F9): kept until the save system
    // migrates onto the Xfer walk. Matches GPL loadPostProcess/xfer: version(1) + base. ----
    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();
    }
}

/// <summary>
/// Enables use of the BoneFXUpdate module on this object where additional dynamic FX logic can be
/// driven from body damage-state transitions. Carries no INI fields of its own (GPL
/// BoneFXDamage uses MAKE_STANDARD_MODULE_MACRO with no ModuleData).
/// </summary>
[SimDataAudited]
public sealed class BoneFXDamageModuleData : DamageModuleData
{
    internal static BoneFXDamageModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<BoneFXDamageModuleData> FieldParseTable = new IniParseTable<BoneFXDamageModuleData>();

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new BoneFXDamage(gameObject, gameEngine.SimContext, this);
    }
}
