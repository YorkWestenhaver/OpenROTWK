// RadiusDecalUpdate - R12 port. GPL reference:
// Generals/Code/GameEngine/Include/GameLogic/Module/RadiusDecalUpdate.h (+ .cpp),
// GeneralsMD carries the identical shape. Behavior facts used:
//   - ctor: no decal, m_killWhenNoLongerAttacking = false, wake forever (nothing to do
//     until a decal is created).
//   - createRadiusDecal(tmpl, radius, pos): (re)builds the decal at the given position and
//     radius, wakes UPDATE_SLEEP_NONE while it exists (empty template -> stays asleep; every
//     concrete template call produces a non-empty decal, so this port always wakes on
//     create).
//   - killRadiusDecal(): clears the decal, sleeps forever.
//   - update(): if killWhenNoLongerAttacking and the object is no longer
//     OBJECT_STATUS_IS_ATTACKING, clear the decal and sleep forever; otherwise refresh the
//     decal (client-side throb/position tracking) and keep ticking every frame.
//   - xfer: version 1, decal state then m_killWhenNoLongerAttacking.
//
// Scope note (clean-room, no game.dat content): the GPL RadiusDecal/RadiusDecalTemplate pair
// (GameClient/RadiusDecal.h) is a float, GPU-resident texture-throb animation - the same kind
// of client-only presentation LargeGroupAudioUpdate's audio mix is (S8: no client-decal host
// on ISimContext). What's deterministic and sim-visible is exactly what the GPL module reads
// back through its own accessors: whether a decal is active, and the radius/position it was
// created with (Coord3D/Real, ported as Fix64/FixVector3 end to end - no floats). The
// template identity is tracked as an opaque caller-supplied id (int) so a future client-decal
// host can resolve it; the pixel-level throb animation itself stays out of the sim per the S8
// precedent and is a recorded follow-up, not invented here.

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class RadiusDecalUpdate : UpdateModule
{
    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>Whether a radius decal is currently displayed.</summary>
    private bool _decalActive;

    /// <summary>Opaque caller-supplied template id for the active decal (client resolves it).</summary>
    private int _decalTemplateId;

    private Fix64 _decalRadius;
    private FixVector3 _decalPosition;

    /// <summary>GPL killWhenNoLongerAttacking(Bool): clear the decal once OBJECT_STATUS_IS_ATTACKING drops.</summary>
    private bool _killWhenNoLongerAttacking;

    public RadiusDecalUpdate(GameObject gameObject, ISimContext context, RadiusDecalUpdateModuleData data)
        : base(gameObject, context)
    {
        // GPL ctor: m_deliveryDecal.clear(); m_killWhenNoLongerAttacking = false; wake forever.
        SetWakeFrame(UpdateSleepTime.Forever);
    }

    public bool IsDecalActive => _decalActive;
    public int DecalTemplateId => _decalTemplateId;
    public Fix64 DecalRadius => _decalRadius;
    public FixVector3 DecalPosition => _decalPosition;
    public bool KillWhenNoLongerAttackingFlag => _killWhenNoLongerAttacking;

    /// <summary>GPL killWhenNoLongerAttacking(Bool v).</summary>
    public void KillWhenNoLongerAttacking(bool value) => _killWhenNoLongerAttacking = value;

    /// <summary>GPL createRadiusDecal(tmpl, radius, pos): (re)builds the decal and wakes the module.</summary>
    public void CreateRadiusDecal(int templateId, Fix64 radius, in FixVector3 position)
    {
        _decalActive = true;
        _decalTemplateId = templateId;
        _decalRadius = radius;
        _decalPosition = position;

        SetWakeFrame(UpdateSleepTime.None);
    }

    /// <summary>GPL killRadiusDecal(): clear the decal and sleep forever.</summary>
    public void KillRadiusDecal()
    {
        ClearDecalState();

        SetWakeFrame(UpdateSleepTime.Forever);
    }

    /// <summary>The state-clearing half of <see cref="KillRadiusDecal"/>, without the
    /// SetWakeFrame call: Update() itself already returns UpdateSleepTime.Forever to sleep
    /// itself, and SetWakeFrame() may not be called from inside a module's own Update() (it
    /// would be ignored anyway, in favor of the return code - GameLogic.AwakenUpdateModule).</summary>
    private void ClearDecalState()
    {
        _decalActive = false;
        _decalTemplateId = 0;
        _decalRadius = Fix64.Zero;
        _decalPosition = default;
    }

    public override UpdateSleepTime Update()
    {
        if (_killWhenNoLongerAttacking && !GameObject.TestStatus(ObjectStatus.IsAttacking))
        {
            ClearDecalState();
            return UpdateSleepTime.Forever;
        }

        if (!_decalActive)
        {
            // Should not normally be reached (the module sleeps forever while inactive), but
            // guards a stray wake from ever ticking without a decal.
            return UpdateSleepTime.Forever;
        }

        // GPL m_deliveryDecal.update(): client-side throb/position refresh, out of sim scope
        // (S8 precedent) - the decal just stays active and awake every frame until killed.
        return UpdateSleepTime.None;
    }

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferBool("DecalActive", ref _decalActive);
        xfer.XferInt("DecalTemplateId", ref _decalTemplateId);
        xfer.XferFix64("DecalRadius", ref _decalRadius, Tolerance.Band);
        xfer.XferFixVector3("DecalPosition", ref _decalPosition, Tolerance.Band);
        xfer.XferBool("KillWhenNoLongerAttacking", ref _killWhenNoLongerAttacking);
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================

/// <summary>
/// Allows use of a radius decal cursor from Mouse.INI on the object's weapon when not
/// explicitly fired.
/// </summary>
[SimDataAudited]
public sealed class RadiusDecalUpdateModuleData : UpdateModuleData
{
    internal static RadiusDecalUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    // GPL RadiusDecalUpdateModuleData::buildFieldParse: both fields (DeliveryDecal,
    // DeliveryDecalRadius) are commented out in the retail source - the module carries no
    // parsed data of its own (the decal is always driven at runtime via createRadiusDecal).
    private static readonly IniParseTable<RadiusDecalUpdateModuleData> FieldParseTable = new IniParseTable<RadiusDecalUpdateModuleData>();

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new RadiusDecalUpdate(gameObject, gameEngine.SimContext, this);
    }
}
