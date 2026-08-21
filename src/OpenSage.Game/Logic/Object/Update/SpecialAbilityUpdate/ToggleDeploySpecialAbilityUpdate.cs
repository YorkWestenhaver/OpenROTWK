// ToggleDeploySpecialAbilityUpdate - R13 port (api-freeze-v1 §6 / template v1.1).
//
// Data-derivation only: generals-gpl carries no ToggleDeploySpecialAbilityUpdate at all (grep
// confirms - it is a BFME2-only class, same posture as ToggleHiddenSpecialAbilityUpdate and
// FloodUpdate - see this class's own spec, research/modules-r13/specs/
// ToggleDeploySpecialAbilityUpdateModuleData.md, §0). This port is derived entirely from (a)
// this class's own 4-field INI vocabulary, (b) the landed sibling family's established idiom in
// this directory (ToggleHiddenSpecialAbilityUpdate, ToggleMountedSpecialAbilityUpdate), and (c)
// the frozen module API. No Ghidra/game.dat material is read or cited anywhere in this file.
//
// STATE MACHINE: unlike every sibling in this directory, this class's own INI vocabulary has no
// timer fields at all (no UnpackTime/PreparationTime/PersistentPrepTime/PackTime/
// EffectDuration), so the simplest composition consistent with the fields actually present is a
// two-state instant toggle (Undeployed <-> Deployed), not a phased Packing/Prepared/Active
// machine - inventing timer-driven phases here would add behavior the field list does not
// support. Toggle(specialPowerTemplateName, triggeringObject, deploy) is the single driven seam
// (no landed special-power/command system calls it yet, same posture as every other trigger seam
// in this batch's siblings): it is a no-op if the template name doesn't match this module's own
// SpecialPowerTemplate, and a no-op if the requested deploy state already matches the current
// state (idempotent against repeats, same "no interrupting/re-triggering" posture as
// ToggleHiddenSpecialAbilityUpdate.InitiateIntentToDoSpecialPower's _phase != Packed guard).
// Otherwise it flips ModelConditionFlag.Deployed via GameObject.SetModelConditionState /
// ClearModelConditionState, fires SoundDeploy/SoundUndeploy through
// ISimEvents.FireAudioEventAtObject when the corresponding field is non-empty (the same literal-
// AudioEvent-name shape as HordeSiegeEngineContain's EnterSound/ExitSound), and updates
// _deployed. The transition is instantaneous - no phase-end frame, no LogicFrameSpan field
// exists to time one.
//
// Update() does no per-frame work (no timer field exists to advance against - see above) and
// always returns UpdateSleepTime.Forever, the same posture as AttributeModifierAuraUpdate's own
// no-per-frame-work branches; the constructor sets SetWakeFrame(UpdateSleepTime.Forever) so the
// module never wakes on its own. All behavior lives in the Toggle seam.
//
// PARSED, NOT MODELED (audited gap, not invented):
//   - IgnoreFacingCheck: no landed module in this family implements a facing check anywhere,
//     and no ISimContext member exposes a facing/LOS predicate (IPartitionQuery has range
//     queries only). Modeling a facing gate here would require inventing an engine capability
//     neither this class's own fields nor the frozen ISimContext contract provide. Parsed, held,
//     and exposed read-only (IgnoresFacingCheck) for whatever caller-side targeting/facing gate
//     eventually consumes it - the same "parsed and held" posture as
//     ToggleHiddenSpecialAbilityUpdateModuleData.ShowPalantirTimer.
//
// Every mutable sim field appears in Xfer exactly once (§3); tolerances are the field's
// conformance class at its declaration site (§4).

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class ToggleDeploySpecialAbilityUpdate : UpdateModule
{
    private readonly ToggleDeploySpecialAbilityUpdateModuleData _data;

    // ---- mutable sim state (the whole inventory; the one field is in Xfer) ----

    private bool _deployed;

    public ToggleDeploySpecialAbilityUpdate(GameObject gameObject, ISimContext context, ToggleDeploySpecialAbilityUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        // No timer field exists on this module (see file header): it never wakes on its own,
        // and all behavior lives in the driven Toggle() seam.
        SetWakeFrame(UpdateSleepTime.Forever);
    }

    /// <summary>Parsed and held; not currently modeled - see the file-header gap note.</summary>
    public bool IgnoresFacingCheck => _data.IgnoreFacingCheck;

    /// <summary>Whether this object is currently in the Deployed state.</summary>
    public bool IsDeployed => _deployed;

    /// <summary>
    /// Drives the Undeployed &lt;-&gt; Deployed toggle. Only this module's own special power
    /// (matched by template name) may fire it; a call that repeats the current state is a no-op
    /// (idempotent against repeat calls - same posture as
    /// ToggleHiddenSpecialAbilityUpdate.InitiateIntentToDoSpecialPower's phase guard). On a real
    /// transition, flips <see cref="ModelConditionFlag.Deployed"/> and fires the corresponding
    /// sound cue (<see cref="ToggleDeploySpecialAbilityUpdateModuleData.SoundDeploy"/> or
    /// <see cref="ToggleDeploySpecialAbilityUpdateModuleData.SoundUndeploy"/>) when configured.
    /// </summary>
    /// <param name="triggeringObject">
    /// Accepted for API-shape symmetry with the sibling family's own trigger seams, but unused:
    /// this class has no field to consume it against (see file header).
    /// </param>
    public bool Toggle(string specialPowerTemplateName, GameObject triggeringObject, bool deploy)
    {
        if (_data.SpecialPowerTemplate != specialPowerTemplateName)
        {
            return false;
        }

        if (_deployed == deploy)
        {
            return false;
        }

        if (deploy)
        {
            GameObject.SetModelConditionState(ModelConditionFlag.Deployed);

            if (!string.IsNullOrEmpty(_data.SoundDeploy))
            {
                Context.Events.FireAudioEventAtObject(_data.SoundDeploy, GameObject.Id);
            }
        }
        else
        {
            GameObject.ClearModelConditionState(ModelConditionFlag.Deployed);

            if (!string.IsNullOrEmpty(_data.SoundUndeploy))
            {
                Context.Events.FireAudioEventAtObject(_data.SoundUndeploy, GameObject.Id);
            }
        }

        _deployed = deploy;
        return true;
    }

    public override UpdateSleepTime Update()
    {
        // No per-frame phase to advance (no timer field exists - see file header). All
        // behavior lives in the driven Toggle() seam.
        return UpdateSleepTime.Forever;
    }

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----
    //
    // Tolerance (ruling A3): _deployed is a lifecycle fact (no timer field exists on this
    // module at all), so Exact - the entire Xfer walk is one Exact-class bool.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferBool("Deployed", ref _deployed);
    }
}

[AddedIn(SageGame.Bfme2)]
[SimDataAudited]
public sealed class ToggleDeploySpecialAbilityUpdateModuleData : UpdateModuleData
{
    internal static ToggleDeploySpecialAbilityUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<ToggleDeploySpecialAbilityUpdateModuleData> FieldParseTable = new IniParseTable<ToggleDeploySpecialAbilityUpdateModuleData>
    {
        { "SpecialPowerTemplate", (parser, x) => x.SpecialPowerTemplate = parser.ParseAssetReference() },
        { "IgnoreFacingCheck", (parser, x) => x.IgnoreFacingCheck = parser.ParseBoolean() },
        { "SoundDeploy", (parser, x) => x.SoundDeploy = parser.ParseAssetReference() },
        { "SoundUndeploy", (parser, x) => x.SoundUndeploy = parser.ParseAssetReference() },
    };

    public string SpecialPowerTemplate { get; private set; }

    /// <summary>Parsed and held; not currently modeled - see the file-header gap note.</summary>
    public bool IgnoreFacingCheck { get; private set; }

    public string SoundDeploy { get; private set; }
    public string SoundUndeploy { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new ToggleDeploySpecialAbilityUpdate(gameObject, gameEngine.SimContext, this);
    }
}
