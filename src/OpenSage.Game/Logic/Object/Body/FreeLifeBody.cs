// FreeLifeBody - Round-8 Body port to the frozen module contract (api-freeze-v1 as amended
// v1.1, rulings A1-A6; template v1.1 = pilot-autoheal.md sections 3/6). Builds ON S1
// (weapon/damage/armor, experiment-round-6): it consumes the landed ActiveBody /
// BodyDamageCore Fix64 health-application surface and does NOT reimplement damage math.
//
// Behavioral reference: NONE. FreeLifeBody is BFME2/RotWK-only (AddedIn Bfme2Rotwk) and is
// ABSENT from generals-gpl, so there is no GPL semantic reference; the behavior below is
// implemented to the task-packet specification ("one free auto-revive at FreeLifeHealthPercent
// when killed, then timed invincibility FreeLifeTime/FreeLifeInvincible") cross-checked against
// the AotR INI field comments (clean-room; binary-derived behavioral reference only, fresh code):
//   FreeLifeHealthPercent  = 50    ; percentage of MaxHealth health to recover
//   FreeLifeTime           = 10000 ; ms of post-resurrection invincibility (see finding F-FLB-1)
//   FreeLifeInvincible     = Yes
//   FreeLifePrerequisiteUpgrade = Upgrade_GimliFreeLife
//   FreeLifeAnimAndDuration     = AnimState:RESURRECTED AnimTime:3000
// The INI header calls it "a variation of RespawnBody". Every corpus usage is COMMENTED OUT
// (gapmap G1 is therefore trivially clean - see FreeLifeBody.md item 1).
//
// MUTABLE SIM-STATE INVENTORY (the whole Xfer walk contribution over ActiveBody's ledger):
//   _freeLifeUsed          - bool, the one free life has been spent (packet: exactly one).
//   _invincibleActive      - bool, currently inside the post-resurrection invincibility window.
//   _invincibleUntilFrame  - LogicFrame, the frame at which that window ends.
// (_freeLifePending is transient within a single AttemptDamage call - never live across a frame
//  boundary - so it is deliberately NOT xfered.)
//
// THE DEATH-INTERCEPTION SEAM (the crux; why this needs no shared-file edit): S1's core
// extraction commits combat health mutation inside BodyDamageCore.ApplyDamage, which does NOT
// route through the virtual float InternalChangeHealth GPL bodies overrode. The R7 ImmortalBody
// port added the sanctioned post-armor / post-scalar / post-Kill hook ActiveBody
// .ClampCombatHealthLoss(Fix64) for exactly this. FreeLifeBody reuses that EXISTING hook: while
// the free life is bankable it converts the killing blow into a survivable one (leaving the base
// death path unreached), flags a pending resurrection, and TriggerFreeLife runs after the base
// finishes applying damage FX/callbacks. So the whole port is additive over the S1 base - no
// change to any shared file (merge-hygiene clean).

#nullable enable

using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

/// <summary>
/// A Body that grants one free auto-revive: the first blow that would kill the object instead
/// restores it to <c>FreeLifeHealthPercent</c> of max health and (when <c>FreeLifeInvincible</c>)
/// makes it invincible for <c>FreeLifeTime</c>. After the free life is spent, death is normal.
/// BFME2/RotWK-only; no GPL reference (see file header).
/// </summary>
[SimState]
public sealed class FreeLifeBody : ActiveBody
{
    private readonly FreeLifeBodyModuleData _moduleData;

    /// <summary>The one free life has been spent (packet: exactly one auto-revive).</summary>
    private bool _freeLifeUsed;

    /// <summary>Currently inside the post-resurrection invincibility window.</summary>
    private bool _invincibleActive;

    /// <summary>Frame at which the invincibility window ends (valid only while active).</summary>
    private LogicFrame _invincibleUntilFrame;

    /// <summary>
    /// Set by <see cref="ClampCombatHealthLoss"/> during a base <see cref="AttemptDamage"/> call
    /// when it converts a lethal blow into a survivable one; consumed at the end of that same
    /// call to run the resurrection. Purely within-call scratch - never crosses a frame, so it is
    /// not sim state and not xfered.
    /// </summary>
    private bool _freeLifePending;

    internal FreeLifeBody(GameObject gameObject, IGameEngine gameEngine, FreeLifeBodyModuleData moduleData)
        : base(gameObject, gameEngine, moduleData)
    {
        _moduleData = moduleData;
    }

    /// <summary>
    /// True while the free life is still bankable: not yet spent and (if a prerequisite upgrade is
    /// configured) that upgrade is present. <see cref="GameObject.HasUpgrade"/> returns true for a
    /// null template, so a body with no prerequisite is always eligible.
    /// </summary>
    private bool FreeLifeAvailable =>
        !_freeLifeUsed && GameObject.HasUpgrade(_moduleData.FreeLifePrerequisiteUpgrade?.Value);

    public override DamageInfoOutput AttemptDamage(in DamageInfoInput damageInput)
    {
        // Lazy expiry of the invincibility window. A Body module gets no per-frame tick, so the
        // window is evaluated whenever damage is next attempted - deterministic and sufficient,
        // since the window only matters when the object would otherwise take damage.
        RefreshInvincibility();

        // While invincible, ignore all damage. Unresistable still bypasses (finding F-FLB-2).
        // Healing also bypasses (F-INT-R8-2): the base redirects Healing-typed AttemptDamage
        // calls to AttemptHealing (ActiveBody's GPL "shouldn't happen" compatibility path), so
        // blocking it here silently swallowed heals mis-routed through AttemptDamage for the
        // whole invincibility window even though direct AttemptHealing calls worked fine.
        if (_invincibleActive
            && damageInput.DamageType != DamageType.Unresistable
            && damageInput.DamageType != DamageType.Healing)
        {
            return new DamageInfoOutput();
        }

        // Run the normal S1 damage resolution. If the free life is bankable and the blow is
        // lethal, ClampCombatHealthLoss leaves us alive and flags a pending resurrection.
        _freeLifePending = false;
        var damageOutput = base.AttemptDamage(damageInput);

        if (_freeLifePending)
        {
            _freeLifePending = false;
            TriggerFreeLife();
        }

        return damageOutput;
    }

    /// <summary>
    /// The S1 combat health-floor seam (added by the R7 ImmortalBody port; runs post-armor,
    /// post-scalar, post-Kill-resolution, Fix64 on the canonical core health). While the free life
    /// is bankable, a would-be-lethal loss is bound to leave 1 HP so the base never runs its death
    /// path, and a resurrection is flagged for <see cref="AttemptDamage"/> to apply. A survivable
    /// loss passes through untouched (normal ActiveBody behavior below the free-life threshold).
    /// </summary>
    protected override Fix64 ClampCombatHealthLoss(Fix64 loss)
    {
        if (!FreeLifeAvailable)
        {
            return loss;
        }

        var current = DamageCore.CurrentHealth;
        if (loss < current)
        {
            // Survivable (resulting health stays > 0): the free life is not needed for this hit.
            return loss;
        }

        // Lethal (resulting health would be <= 0). Convert the killing blow into a resurrection:
        // leave 1 HP so the base's death check never fires, and flag the pending free life.
        _freeLifePending = true;
        var maxLoss = current - Fix64.One;
        return maxLoss < Fix64.Zero ? Fix64.Zero : maxLoss;
    }

    /// <summary>
    /// Spend the free life: restore health to <c>FreeLifeHealthPercent</c> of max (Fix64 on the
    /// core, never the float display view), start the invincibility window if configured, and
    /// fire the client resurrection animation.
    /// </summary>
    private void TriggerFreeLife()
    {
        _freeLifeUsed = true;

        // Restore to FreeLifeHealthPercent% of MaxHealth. FreeLifeHealthPercent is a Fix64
        // fraction quantized at parse (ParseFix64Percentage, S5); the arithmetic is Fix64 on the
        // canonical BodyDamageCore. InternalChangeHealth(Fix64) chains the core change + the
        // ActiveBody health-change side effects (damage-state recompute, effectively-dead flag).
        var target = DamageCore.MaxHealth * _moduleData.FreeLifeHealthPercent;
        InternalChangeHealth(target - DamageCore.CurrentHealth);

        if (_moduleData.FreeLifeInvincible)
        {
            _invincibleActive = true;
            _invincibleUntilFrame = GameEngine.GameLogic.CurrentFrame + _moduleData.FreeLifeTime;
        }

        // Client-bound resurrection animation (FreeLifeAnimAndDuration). A client output, never a
        // sim input, so it carries no determinism obligation and is null-safe on headless hosts.
        // TODO(Port): drive the FreeLifeAnimAndDuration ModelConditionFlag on the drawable.
    }

    /// <summary>Expire the invincibility window once its end frame is reached (lazy, tick-free).</summary>
    private void RefreshInvincibility()
    {
        if (_invincibleActive && GameEngine.GameLogic.CurrentFrame >= _invincibleUntilFrame)
        {
            _invincibleActive = false;
        }
    }

    // ---- contract Xfer walk: own version, the ActiveBody base walk (DamageScalar + BodyDamageCore
    // ledger + crush/indestructible + armor-set flags), then FreeLifeBody's three sim-state fields.
    // Declaration order = ours (F9). HasSimXfer is inherited (true) from ActiveBody. Tolerances per
    // A3: bools are Exact (no quantum gap); the frame is Exact on both targets (frame counts are
    // integers - A3 Quantum-collapses-to-Exact for self-diff, and integers on both sides for the
    // oracle). ----

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        base.Xfer(xfer);
        xfer.XferBool("FreeLifeUsed", ref _freeLifeUsed);
        xfer.XferBool("FreeLifeInvincibleActive", ref _invincibleActive);
        xfer.XferFrame("FreeLifeInvincibleUntilFrame", ref _invincibleUntilFrame, Tolerance.Exact);
    }

    internal override void Load(StatePersister reader)
    {
        // Retail .sav layout (F9-exempt legacy reader): own version, then ActiveBody's persisted
        // body, then the free-life state. No retail-original field order is claimed (F9); this
        // mirrors the contract walk's own state so a legacy save round-trips onto the new fields.
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();

        reader.PersistBoolean(ref _freeLifeUsed);
        reader.PersistBoolean(ref _invincibleActive);
        reader.PersistLogicFrame(ref _invincibleUntilFrame);
    }
}

/// <summary>
/// A variation of RespawnBody: grants one free auto-revive to a percentage of max health, with
/// optional timed invincibility, gated by an optional prerequisite upgrade. BFME2/RotWK-only.
/// </summary>
[AddedIn(SageGame.Bfme2Rotwk)]
[SimDataAudited]
public sealed class FreeLifeBodyModuleData : ActiveBodyModuleData
{
    internal static new FreeLifeBodyModuleData Parse(IniParser parser)
    {
        var result = parser.ParseBlock(FieldParseTable);
        result.ApplyHealthDefaults(parser);   // F-R7-2 / F-HB-1: the shadowing Parse must re-apply
                                               // the BFME InitialHealth = MaxHealth default, else a
                                               // block with only MaxHealth spawns at 0 HP.
        return result;
    }

    private static new readonly IniParseTable<FreeLifeBodyModuleData> FieldParseTable = ActiveBodyModuleData.FieldParseTable
        .Concat(new IniParseTable<FreeLifeBodyModuleData>
        {
            // Percentage of MaxHealth to recover on resurrection -> Fix64 fraction at the S5
            // blessed integer text boundary (text / 100 exactly; "50" -> 0.5). Consumed in Fix64.
            { "FreeLifeHealthPercent", (parser, x) => x.FreeLifeHealthPercent = parser.ParseFix64Percentage() },
            // Post-resurrection invincibility duration -> whole logic frames (S5 ceil ms->frames,
            // the title's 5 Hz rate, F6).
            { "FreeLifeTime", (parser, x) => x.FreeLifeTime = parser.ParseDurationLogicFrames() },
            { "FreeLifeInvincible", (parser, x) => x.FreeLifeInvincible = parser.ParseBoolean() },
            { "FreeLifePrerequisiteUpgrade", (parser, x) => x.FreeLifePrerequisiteUpgrade = parser.ParseUpgradeReference() },
            { "FreeLifeAnimAndDuration", (parser, x) => x.FreeLifeAnimAndDuration = AnimAndDuration.Parse(parser) }
        });

    /// <summary>Fraction of <see cref="ActiveBodyModuleData.MaxHealth"/> restored on the free life (Fix64, quantized at parse).</summary>
    public Fix64 FreeLifeHealthPercent { get; private set; }

    /// <summary>Post-resurrection invincibility duration in whole logic frames.</summary>
    public LogicFrameSpan FreeLifeTime { get; private set; }

    /// <summary>Whether the resurrected object is invincible for <see cref="FreeLifeTime"/>.</summary>
    public bool FreeLifeInvincible { get; private set; }

    /// <summary>Optional upgrade that must be present for the free life to be available.</summary>
    public LazyAssetReference<UpgradeTemplate>? FreeLifePrerequisiteUpgrade { get; private set; }

    /// <summary>Client-side resurrection animation (no sim effect).</summary>
    public AnimAndDuration? FreeLifeAnimAndDuration { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new FreeLifeBody(gameObject, gameEngine, this);
    }
}
