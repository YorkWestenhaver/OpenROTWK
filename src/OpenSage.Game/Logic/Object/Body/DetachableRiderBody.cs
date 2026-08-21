// DetachableRiderBody - R8 Body-batch port to the frozen module contract (api-freeze-v1 §3/§5
// as amended v1.1; template v1.1 = pilot-autoheal §3/§6). Builds ON S1 (weapon/damage/armor):
// consumes the landed ActiveBody / BodyDamageCore Fix64 health surface and does NOT reimplement
// damage math.
//
// BEHAVIORAL REFERENCE: BFME/BFME2-only module - ABSENT from generals-gpl (there is no ZH
// HighlanderBody-style GPL file for it). Per the packet, the only reference is the binary-derived behavioral spec,
// used as behavioral facts (never transplanted code); this is fresh code. The one behavior the
// task authorizes as portable-now is the health-clamp half:
//
//     "on rider death, the (now riderless) mount's health drops to
//      HealthPercentageWhenRiderDies of its max."
//
// That is a pure S1 percent-health SET on the canonical Fix64 core - it needs no float and no
// rider/mount plumbing to be correct, so it ports and tests standalone here. The other half -
// WHO calls it and WHEN (detecting the rider's death, the Contain/rider-slot coupling) - reaches
// beyond the Body category (api-freeze §7 lists "Contain rider slots" as deliberately unfrozen),
// so this port exposes the drop as a public seam (OnRiderDied) that the future rider/mount
// coupling will call, and files the coupling + the two arming fields as spec-gated findings
// (modules-r8/DetachableRiderBody.md), exactly as the AutoHeal pilot did with its seven
// unacted BFME-only fields (pilot §4).
//
// MUTABLE SIM-STATE INVENTORY: none of its own. The mount's health lives entirely in the base
// ActiveBody's Fix64 BodyDamageCore (which supplies the whole contract Xfer walk). The two
// remaining INI fields (StartsActive / TriggeredBy) are an arming gate whose runtime semantics
// are unproven without a behavioral spec, so they are audited-but-unacted this round - NOT modeled as
// mutable state (inventing a gate flag would be invention, not a port). DetachableRiderBody
// therefore adds NO field to the Xfer walk; like ImmortalBody/HighlanderBody it only re-versions
// and chains the base (GPL-shape version wrapper; F9 makes layout ours).
//
// THE DROP IS Fix64 (item 2 acceptance content): desired = MaxHealth * HealthPercentageWhenRiderDies
// and the delta are computed on DamageCore (Fix64), never the float Health display view, so the
// penalty lands on the deterministic ledger. "Drops to" is read as a one-way lowering: a mount
// already below the threshold is left untouched (a rider death should never HEAL the mount).

#nullable enable

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

/// <summary>
/// A mount body whose rider can be detached (killed independently). When the rider dies the
/// mount's health is dropped to <see cref="DetachableRiderBodyModuleData.HealthPercentageWhenRiderDies"/>
/// of its maximum. BFME/BFME2-only; no GPL reference (binary-derived behavioral facts only, fresh code).
/// </summary>
[SimState]
public sealed class DetachableRiderBody : ActiveBody
{
    private readonly DetachableRiderBodyModuleData _moduleData;

    internal DetachableRiderBody(GameObject gameObject, IGameEngine gameEngine, DetachableRiderBodyModuleData moduleData)
        : base(gameObject, gameEngine, moduleData)
    {
        _moduleData = moduleData;
    }

    /// <summary>
    /// The portable core mechanic (the S1 health-clamp half): the rider has died, so drop the
    /// riderless mount's health to <c>HealthPercentageWhenRiderDies</c> of its max. Computed
    /// entirely in Fix64 on the canonical <see cref="ActiveBody.DamageCore"/> (never the float
    /// Health view), then applied through the base Fix64 health change so the damage-state /
    /// effectively-dead side effects fire exactly as any other health mutation.
    ///
    /// One-way lowering ("drops to"): if the mount is already at or below the target it is left
    /// untouched. This is the seam the (not-yet-built) rider/mount coupling calls when it detects
    /// the rider's death - see the module header and modules-r8/DetachableRiderBody.md for why
    /// the detection half is out of the Body category this round.
    /// </summary>
    public void OnRiderDied()
    {
        var target = DamageCore.MaxHealth * _moduleData.HealthPercentageWhenRiderDies;
        var current = DamageCore.CurrentHealth;

        if (target < current)
        {
            // Negative delta; the base clamps to [0, max] and runs the state side effects.
            InternalChangeHealth(target - current);
        }
    }

    // ---- the contract Xfer walk (F9: declaration order, ours). DetachableRiderBody owns no
    // mutable sim state of its own, so there is no field to add - only the version wrapper over
    // the base ActiveBody walk (which carries the health ledger). HasSimXfer is inherited (true)
    // from ActiveBody. ----

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        base.Xfer(xfer);
    }

    internal override void Load(StatePersister reader)
    {
        // Best-effort legacy .sav reader (F9-exempt): version + base. No BFME2 DetachableRiderBody
        // save layout is pinned by an oracle capture (OPEN-8); this class adds no own state, so
        // the walk is the base ActiveBody layout under a version byte.
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();
    }
}

/// <summary>
/// Body for a mount that carries a detachable rider; on the rider's death the mount's health
/// drops to <see cref="HealthPercentageWhenRiderDies"/> of its max.
/// </summary>
[SimDataAudited]
public sealed class DetachableRiderBodyModuleData : ActiveBodyModuleData
{
    internal static new DetachableRiderBodyModuleData Parse(IniParser parser)
    {
        var result = parser.ParseBlock(FieldParseTable);
        result.ApplyHealthDefaults(parser);   // F-R7-2: the shadowing Parse must keep the base InitialHealth=MaxHealth default.
        return result;
    }

    private static new readonly IniParseTable<DetachableRiderBodyModuleData> FieldParseTable = ActiveBodyModuleData.FieldParseTable
        .Concat(new IniParseTable<DetachableRiderBodyModuleData>
        {
            // S5 audit: percent text -> Fix64 at the blessed parse boundary (ParseFix64Percentage,
            // F4), consumed by the Fix64 percent-health SET in DetachableRiderBody.OnRiderDied.
            { "HealthPercentageWhenRiderDies", (parser, x) => x.HealthPercentageWhenRiderDies = parser.ParseFix64Percentage() },
            // Audited-but-unacted arming fields (runtime semantics need a behavioral spec - see the class
            // header + modules-r8/DetachableRiderBody.md). Parsed exactly as before; no field
            // grammar change, so gapmap parsing is byte-identical (G1).
            { "StartsActive", (parser, x) => x.StartsActive = parser.ParseBoolean() },
            { "TriggeredBy", (parser, x) => x.TriggeredBy = parser.ParseString() },
        });

    /// <summary>Fraction of max health the mount is dropped to when its rider dies (Fix64, S5).</summary>
    public Fix64 HealthPercentageWhenRiderDies { get; private set; }

    /// <summary>Arming gate; parsed and stored, not yet acted on (spec-gated - finding).</summary>
    public bool StartsActive { get; private set; }

    /// <summary>Upgrade name that arms the detach behavior; parsed and stored, not yet acted on (spec-gated - finding).</summary>
    public string? TriggeredBy { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new DetachableRiderBody(gameObject, gameEngine, this);
    }
}
