// The deterministic damage descriptor - the Fix64 core of GPL's DamageInfo
// (generals-gpl GeneralsMD GameLogic/Damage.h: DamageInfoInput / DamageInfoOutput,
// semantics only; fresh code).
//
// This is the value that flows through the frozen firing -> damage -> armor -> health
// chain (build-roadmap S1). The legacy float DamageInfo (Damage/DamageInfo.cs) remains
// as the unmigrated-module callback + retail-save view; conversions between the two live
// in CombatLegacyBridge.cs (a non-[SimState] file), never here.
//
// Every field is Fix64 / int / enum / ObjectId - no float can be typed in this file
// (SIMCORE001 enforces it).

using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

/// <summary>
/// Damage we are trying to inflict, before armor / scalar resolution
/// (GPL <c>DamageInfoInput</c>, Fix64).
/// </summary>
[SimState]
public struct CombatDamageInput()
{
    /// <summary>Object dealing the damage; may be invalid (environmental damage).</summary>
    public ObjectId SourceId = ObjectId.Invalid;

    /// <summary>Type of damage; selects the armor coefficient.</summary>
    public DamageType DamageType = DamageType.Explosion;

    /// <summary>If <see cref="DamageType"/> is Status, which status to inflict.</summary>
    public ObjectStatus DamageStatusType = ObjectStatus.None;

    /// <summary>
    /// Visual-only damage-type override for DamageFX. Unresistable = "no override"
    /// (the GPL sentinel: they were out of bits). Never affects resolution.
    /// </summary>
    public DamageType DamageFXOverride = DamageType.Unresistable;

    /// <summary>Death type to use if this kills the victim.</summary>
    public DeathType DeathType = DeathType.Normal;

    /// <summary>Damage to attempt, before armor / scalar (Q31.32).</summary>
    public Fix64 Amount = Fix64.Zero;

    /// <summary>Always kills the victim regardless of amount (GPL m_kill).</summary>
    public bool Kill = false;

    /// <summary>
    /// The weapon (ammo) template dealing this damage, when known - e.g. a catapult's
    /// <c>MordorCatapultHumanHeads</c> ammo, distinct from the attacker object's own
    /// template. Set at the one live call site (<c>WeaponTarget.DoDamage</c>) and threaded
    /// through <see cref="CombatLegacyBridge.ToLegacyInput"/> into the legacy
    /// <see cref="DamageInfoInput.SourceWeaponTemplate"/>, which is the field
    /// <see cref="IDamageModule.OnDamage"/> consumers (e.g. EvacuateDamage) actually read.
    /// Deliberately NOT walked by <see cref="Xfer"/>: <see cref="CombatDamageInput"/> is a
    /// transient call parameter (never itself stored as persisted sim state - see the
    /// dead <see cref="CombatDamage"/> wrapper below), and <see cref="IXfer"/> carries no
    /// string/asset-reference primitive to route a <see cref="WeaponTemplate"/> by name
    /// through the four Save/Load/Crc/DeepDump visitors. The one place this value truly
    /// persists is the legacy float view, which already has that machinery
    /// (<see cref="StatePersister.PersistAsciiString"/> + asset-store resolution).
    /// </summary>
    public WeaponTemplate? SourceWeaponTemplate = null;

    public void Xfer(IXfer xfer)
    {
        xfer.XferObjectId("SourceId", ref SourceId);
        xfer.XferEnum("DamageType", ref DamageType);
        xfer.XferEnum("DamageStatusType", ref DamageStatusType);
        xfer.XferEnum("DamageFXOverride", ref DamageFXOverride);
        xfer.XferEnum("DeathType", ref DeathType);
        xfer.XferFix64("Amount", ref Amount, Tolerance.Quantum);
        xfer.XferBool("Kill", ref Kill);
        // SourceWeaponTemplate intentionally excluded - see field doc comment.
    }
}

/// <summary>
/// What actually happened after armor / scalar / health clipping
/// (GPL <c>DamageInfoOutput</c>, Fix64).
/// </summary>
[SimState]
public struct CombatDamageOutput()
{
    /// <summary>Damage applied after armor and scalar (may exceed remaining health).</summary>
    public Fix64 ActualDamageDealt = Fix64.Zero;

    /// <summary>
    /// <see cref="ActualDamageDealt"/> clipped to the health actually removed
    /// (GPL: prevHealth - currentHealth).
    /// </summary>
    public Fix64 ActualDamageClipped = Fix64.Zero;

    /// <summary>True when no damage was done at all (e.g. InactiveBody).</summary>
    public bool NoEffect = false;

    public void Xfer(IXfer xfer)
    {
        xfer.XferFix64("ActualDamageDealt", ref ActualDamageDealt, Tolerance.Quantum);
        xfer.XferFix64("ActualDamageClipped", ref ActualDamageClipped, Tolerance.Quantum);
        xfer.XferBool("NoEffect", ref NoEffect);
    }
}

/// <summary>Request + result pair (GPL <c>DamageInfo</c>).</summary>
[SimState]
public struct CombatDamage()
{
    public CombatDamageInput Request;
    public CombatDamageOutput Result;

    public void Xfer(IXfer xfer)
    {
        Request.Xfer(xfer);
        Result.Xfer(xfer);
    }
}
