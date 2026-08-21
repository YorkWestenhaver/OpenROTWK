// EvacuateDamage - R13 Damage-batch port (spec packet:
// bfme2-workbench/research/modules-r13/specs/EvacuateDamageModuleData.md).
//
// Behavioral reference: no `EvacuateDamage`/`EvacuateDamageModuleData` class exists anywhere in
// generals-gpl (BFME-only addition; verified by the spec's grep of both Generals and GeneralsMD
// trees). The action it triggers is not invented, though - it is the documented GPL primitive
// `orderAllPassengersToExit`:
//   - generals-gpl/Generals/Code/GameEngine/Include/GameLogic/Module/ContainModule.h:130 and
//     OpenContain.h:147 - "All of the smarts of exiting are in the passenger's AIExit. ...
//     this is the game Evacuate."
//   - generals-gpl/GeneralsMD/Code/GameEngine/Include/GameLogic/Module/ContainModule.h:146 and
//     OpenContain.h:150 - same primitive, GeneralsMD-only `Bool instantly` parameter added,
//     semantics unchanged (same comment, verbatim).
// Retail AotR data confirms the pairing: `WeaponThatCausesEvacuation` is used exclusively on
// GarrisonContain towers/keeps, always set to `MordorCatapultHumanHeads` (a siege-terror
// projectile) - e.g. data/AgeoftheRing/aotr/data/ini/object/evilfaction/structures/mordor/
// battletower.ini:247-249. This module is a thin data-derived gate wrapping that already-landed
// primitive, not new game mechanics.
//
// MUTABLE SIM STATE INVENTORY (before any code, runbook step 1):
//   *** EMPTY ***. The module carries no fields of its own - the gate (WeaponThatCausesEvacuation)
//   lives on the immutable ModuleData, and the evacuated/not-evacuated distinction lives entirely
//   in the sibling OpenContainModule's own already-landed Xfer (its _evacQueue/ContainedObjectIds).
//   The Xfer walk below is therefore version-only, matching BoneFXDamage's shape.
//
// Engine plumbing note (spec §2): OnDamage needs the *weapon* that dealt the damage, not just the
// attacking object's own template (DamageInfoInput.SourceTemplate is e.g. MordorCatapult, not the
// fired ammo MordorCatapultHumanHeads). This port adds DamageInfoInput.SourceWeaponTemplate /
// CombatDamageInput.SourceWeaponTemplate and threads it from the one live call site
// (WeaponTarget.DoDamage, fed by DamageNugget.Execute's context.Weapon.Template) through
// CombatLegacyBridge.ToLegacyInput into the legacy DamageInfo this module's OnDamage receives.
// See DamageInfo.cs / CombatDamage.cs / CombatLegacyBridge.cs / WeaponTarget.cs / DamageNugget.cs.

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class EvacuateDamage : DamageModule
{
    private readonly EvacuateDamageModuleData _data;

    // ---- mutable sim state: NONE (see the inventory above) ----

    public EvacuateDamage(GameObject gameObject, ISimContext context, EvacuateDamageModuleData data)
        : base(gameObject, context)
    {
        _data = data;
    }

    /// <summary>
    /// GPL orderAllPassengersToExit, gated by the weapon that dealt the damage ("this is the
    /// game Evacuate" - see the header comment above). Ordinal string compare, matching every
    /// other weapon/object-template-name gate in this codebase. No cooldown, no partial match:
    /// GPL's comment describes a single unconditional call to the primitive once the trigger
    /// fires, and OpenContainModule.Evacuate() degrades gracefully on an empty/already-evacuated
    /// container, so repeated matching hits are safe as repeated no-ops.
    /// </summary>
    public override void OnDamage(in DamageInfo damageInfo)
    {
        var weaponName = damageInfo.Request.SourceWeaponTemplate?.Name;
        if (weaponName != null && weaponName == _data.WeaponThatCausesEvacuation)
        {
            GameObject.FindBehavior<OpenContainModule>()?.Evacuate();
        }
    }

    // GPL's two citations describe onDamage only; there is no companion "on heal" or "on body
    // damage state change" hook implied by either. OnHealing/OnBodyDamageStateChange are left as
    // the DamageModule no-op defaults on purpose (same "leave the inherited no-ops alone"
    // posture as BoneFXDamage's OnDamage/OnHealing).

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----
    // No own sim state: version tag extending the base, declaration order ours (F9).

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        // No fields: the state inventory is empty. WeaponThatCausesEvacuation lives on the
        // immutable ModuleData; the evacuated/not-evacuated distinction lives entirely in the
        // sibling OpenContainModule's own xfer walk, not here.
    }

    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();
    }
}

/// <summary>
/// On damage from a specific weapon (retail: a catapult's "human heads" siege ammo), forces every
/// passenger of this object's contain module to evacuate (GPL "this is the game Evacuate" -
/// orderAllPassengersToExit). Retail use is exclusively on GarrisonContain-bearing towers/keeps.
/// </summary>
[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public sealed class EvacuateDamageModuleData : DamageModuleData
{
    internal static EvacuateDamageModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<EvacuateDamageModuleData> FieldParseTable = new IniParseTable<EvacuateDamageModuleData>
    {
        { "WeaponThatCausesEvacuation", (parser, x) => x.WeaponThatCausesEvacuation = parser.ParseString() }
    };

    /// <summary>
    /// Weapon-template name that triggers the evacuation gate. A plain template-name string,
    /// not a quantized numeric, so it needs no ParseFix64-family change for [SimDataAudited]
    /// conformance (matches the RequiredUpgrade/ObjectFilter template-name-gate idiom elsewhere
    /// in this codebase).
    /// </summary>
    public string WeaponThatCausesEvacuation { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
        => new EvacuateDamage(gameObject, gameEngine.SimContext, this);
}
