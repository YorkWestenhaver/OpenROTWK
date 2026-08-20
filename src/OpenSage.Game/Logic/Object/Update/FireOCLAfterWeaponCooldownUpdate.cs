// FireOCLAfterWeaponCooldownUpdate - R12 port, permanently-parked-for-now runtime module
// (LargeGroupAudioUpdate pattern; see that module's header for the template this follows).
//
// Behavioral reference: generals-gpl Generals GameLogic/Include/Module/FireOCLAfterWeaponCooldownUpdate.h
// and .../Source/GameLogic/Object/Update/FireOCLAfterWeaponCooldownUpdate.cpp (semantics only).
// GPL update() tracks the object's CURRENT weapon each frame: it counts consecutive shots on
// the tracked WeaponSlot (weapon->getLastShotFrame() == now - 1), and when firing stops or the
// object's current weapon no longer matches the tracked slot (obj->getCurrentWeapon() /
// obj->getWeaponInWeaponSlot()), it checks MinShotsToCreateOCL and - if met, and the
// TriggeredBy/ConflictsWith upgrade mux is satisfied - fires the OCL with a lifetime computed
// from the firing duration (scaled by OCLLifetimePerSecond, capped at OCLLifetimeMaxCap).
//
// WHY THIS CANNOT BE A FUNCTIONAL PORT YET: every one of those observations - "the object's
// current weapon", "which slot it's in", "did it fire last frame", "when can it fire again" -
// comes from GPL's live Weapon/WeaponSet firing state on Object. On the frozen ISimContext
// surface (api-freeze-v1) that state does not exist for [SimState] module code:
//   - ISimContext has no weapon surface at all (no current-weapon, no weapon-slot query).
//   - GameObject's only "current weapon" is the LEGACY float/state-machine `Weapon` class
//     (GameObject.CurrentWeapon / ActiveWeaponSet - see Weapon.cs, WeaponSet.cs), which is
//     non-deterministic, not Fix64, and off-limits to [SimState] code (the float-quarantine
//     analyzer rejects it; same boundary CreateObjectDie's F-CODIE-2 finding describes for
//     AIUpdate::transferAttack).
//   - SimWeapon (Combat/SimWeapon.cs) is the deterministic replacement, but by its own header
//     it is "the surface future WeaponModule ports call" - nothing yet owns a live, per-slot
//     SimWeapon on a GameObject, so there is no "current weapon slot" to compare against.
// Firing the OCL (Context.CreateFromObjectCreationList) and scaling its lifetime by firing
// duration are themselves portable once that per-object weapon-firing state exists; today
// there is nothing sim-visible to drive them from, so inventing the trigger would mean
// guessing at a firing signal GPL never has to guess at. TODO-spec: re-open this module when a
// WeaponModule port lands a per-object "current weapon slot" + "last shot frame" surface on
// ISimContext/GameObject, then implement update()/fireOCL() for real against it.
//
// Every INI field is still parsed and audited (closing the [ParseOnly] hole for census/schema
// purposes) so the object carries a live module instead of a parse-only stand-in; none of it
// is consumed by Update(), which sleeps forever, matching the LargeGroupAudioUpdate template.

using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class FireOCLAfterWeaponCooldownUpdate : UpdateModule
{
    public FireOCLAfterWeaponCooldownUpdate(GameObject gameObject, ISimContext context, FireOCLAfterWeaponCooldownUpdateModuleData data)
        : base(gameObject, context)
    {
        // Parked (see header): nothing to schedule until a weapon-firing seam exists.
        SetWakeFrame(UpdateSleepTime.Forever);
    }

    public override UpdateSleepTime Update() => UpdateSleepTime.Forever;

    // ---- the single walk: no mutable sim state (there is nothing to track yet). ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// GPL FireOCLAfterWeaponCooldownUpdateModuleData: an UpdateModuleData carrying the tracked
// weapon slot, the OCL to fire, the shot/lifetime tuning, and an embedded UpgradeMuxData
// (TriggeredBy/ConflictsWith). The mux and tuning fields are parsed and audited but not yet
// consumed (see the module header) - kept as plain vocabulary, same posture as the original
// [ParseOnly] stub, so re-enabling the logic later is a pure addition, not a re-parse.
// ============================================================================
[SimDataAudited]
public sealed class FireOCLAfterWeaponCooldownUpdateModuleData : UpdateModuleData
{
    internal static FireOCLAfterWeaponCooldownUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<FireOCLAfterWeaponCooldownUpdateModuleData> FieldParseTable = new IniParseTable<FireOCLAfterWeaponCooldownUpdateModuleData>
    {
        { "WeaponSlot", (parser, x) => x.WeaponSlot = parser.ParseEnum<WeaponSlot>() },
        { "TriggeredBy", (parser, x) => x.TriggeredBy = parser.ParseAssetReferenceArray() },
        { "ConflictsWith", (parser, x) => x.ConflictsWith = parser.ParseAssetReferenceArray() },
        { "OCL", (parser, x) => x.OCL = parser.ParseObjectCreationListReference() },
        { "MinShotsToCreateOCL", (parser, x) => x.MinShotsToCreateOCL = parser.ParseInteger() },
        { "OCLLifetimePerSecond", (parser, x) => x.OCLLifetimePerSecond = parser.ParseInteger() },
        // GPL parseDurationUnsignedInt: ms -> logic frames, quantized once at parse (S5).
        { "OCLLifetimeMaxCap", (parser, x) => x.OCLLifetimeMaxCap = parser.ParseDurationLogicFrames() }
    };

    /// <summary>Which weapon slot's firing activity is tracked (GPL m_weaponSlot).</summary>
    public WeaponSlot WeaponSlot { get; private set; }

    /// <summary>Upgrades that arm the OCL trigger (GPL UpgradeMuxData). Parsed, not yet
    /// consumed - see the module header.</summary>
    public string[] TriggeredBy { get; private set; }

    /// <summary>Upgrades that veto the OCL trigger (GPL UpgradeMuxData). Parsed, not yet
    /// consumed - see the module header.</summary>
    public string[] ConflictsWith { get; private set; }

    /// <summary>The OCL to fire when firing stops with enough consecutive shots (GPL m_ocl).</summary>
    public LazyAssetReference<ObjectCreationList> OCL { get; private set; }

    /// <summary>Consecutive-shot threshold before the OCL may fire (GPL m_minShotsRequired).</summary>
    public int MinShotsToCreateOCL { get; private set; }

    /// <summary>Milli-fraction scale of OCL lifetime per second of firing duration (GPL
    /// m_oclLifetimePerSecond, used as value * 0.001 real seconds-per-second).</summary>
    public int OCLLifetimePerSecond { get; private set; }

    /// <summary>Upper bound on the computed OCL lifetime (GPL m_oclMaxFrames).</summary>
    public LogicFrameSpan OCLLifetimeMaxCap { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new FireOCLAfterWeaponCooldownUpdate(gameObject, gameEngine.SimContext, this);
    }
}
