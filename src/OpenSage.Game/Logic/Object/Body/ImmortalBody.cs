// ImmortalBody - Body-batch port to the frozen module contract (api-freeze-v1 §3/§5,
// template v1.1 = pilot-autoheal §3/§6). Builds ON S1 (weapon/damage/armor): it consumes
// the landed ActiveBody / BodyDamageCore health-application surface and does NOT
// reimplement damage math.
//
// Behavioral reference: generals-gpl GeneralsMD ImmortalBody.cpp/.h (GPL semantics only;
// fresh code). "Just like Active Body, but won't let health drop below 1." Behavior facts:
//   - internalChangeHealth(delta): delta = max(delta, -getHealth() + 1) BEFORE chaining
//     ActiveBody::internalChangeHealth. Health can never fall below one hit point; if it is
//     already below one, the floor pulls it back up to one. "I go first because I can't let
//     you die and then fix it, I must prevent." A DEBUG_ASSERTCRASH afterwards insists the
//     object was never marked dead.
//   - xfer: writes its own version (1) then chains ActiveBody::xfer.
//   - loadPostProcess / ctor: pure chain, no own state.
//
// MUTABLE SIM STATE INVENTORY: none of its own. Immortality is a stateless rule over
// ActiveBody's Fix64 health ledger (the BodyDamageCore in the base). So ImmortalBody adds
// no field to the Xfer walk - it only re-versions and chains the base (GPL shape).
//
// THE FIX64 FLOOR (the crux of this port; task acceptance criterion "floor arithmetic runs
// in the Fix64 core, not the float view"): the retail floor was Real (float) arithmetic on
// getHealth(). The float Health property here is a D-7 display view over the canonical Fix64
// BodyDamageCore; doing max(delta, -Health + 1) in float would round the floor off the
// deterministic core. Both floor sites below therefore compute in Fix64 against
// DamageCore.CurrentHealth.
//
// THE COMBAT SEAM (recorded in modules-r7/ImmortalBody.md, F-IMB-1): GPL routes ALL health
// changes - including attemptDamage's health subtraction - through the virtual
// internalChangeHealth, so overriding it there is enough to make an object immortal. S1's
// core extraction commits the combat health mutation inside BodyDamageCore.ApplyDamage,
// which does NOT call the virtual internalChangeHealth. Overriding internalChangeHealth
// alone would therefore leave combat damage un-floored on the S1 base. The S1 design (D-4)
// makes AttemptDamage the sanctioned amount-modifying seam for Body subclasses; we take the
// post-armor / post-scalar / post-Kill floor there via the additive ActiveBody hook
// ClampCombatHealthLoss (default identity), so the floor lands AFTER armor amplification and
// even against DAMAGE_KILL - which a pre-armor Amount clamp (the Highlander/Undead stub
// shape) cannot guarantee. We ALSO override the virtual internalChangeHealth so external /
// scripted callers of that path stay floored, matching GPL for the path S1 still routes
// through it.

#nullable enable

using OpenSage.Data.Ini;

namespace OpenSage.Logic.Object;

/// <summary>
/// Just like Active Body, but won't let health drop below 1.
/// </summary>
public sealed class ImmortalBody : ActiveBody
{
    internal ImmortalBody(GameObject gameObject, IGameEngine gameEngine, ImmortalBodyModuleData moduleData)
        : base(gameObject, gameEngine, moduleData)
    {
    }

    /// <summary>
    /// The combat floor (S1 seam). Runs on the post-armor, post-scalar, post-Kill-resolution
    /// health loss a single AttemptDamage would inflict, so an ImmortalBody survives lethal
    /// damage and even DAMAGE_KILL. Fix64 on the canonical core health.
    /// </summary>
    protected override SimCore.Numerics.Fix64 ClampCombatHealthLoss(SimCore.Numerics.Fix64 loss)
    {
        // GPL: delta = max(delta, -getHealth() + 1), with delta = -loss.
        //   ⇔ loss = min(loss, getHealth() - 1), floored at zero so a body already at or
        //   below 1 HP takes no further loss.
        var maxLoss = DamageCore.CurrentHealth - SimCore.Numerics.Fix64.One;
        if (maxLoss < SimCore.Numerics.Fix64.Zero)
        {
            maxLoss = SimCore.Numerics.Fix64.Zero;
        }

        return loss < maxLoss ? loss : maxLoss;
    }

    /// <summary>
    /// GPL ImmortalBody::internalChangeHealth. Floors the delta in Fix64 (never the float
    /// Health view), then chains the base Fix64 health change. Covers the paths S1 still
    /// routes through the virtual float entry (external / scripted callers); combat damage
    /// is floored by <see cref="ClampCombatHealthLoss"/> above.
    /// </summary>
    public override void InternalChangeHealth(float delta)
    {
        // Don't let anything change us to below one hit point.
        //   delta = max(delta, -getHealth() + 1)   (in Fix64: -current + 1 = 1 - current)
        var fixDelta = CombatLegacyBridge.QuantizeFloat(delta);
        var minDelta = SimCore.Numerics.Fix64.One - DamageCore.CurrentHealth;
        if (fixDelta < minDelta)
        {
            fixDelta = minDelta;
        }

        // Extend functionality, but I go first because I can't let you die and then fix it,
        // I must prevent. Chain the base Fix64 health change.
        InternalChangeHealth(fixDelta);

        DebugUtility.AssertCrash(
            DamageCore.CurrentHealth > SimCore.Numerics.Fix64.Zero && !GameObject.IsEffectivelyDead,
            "Immortal objects should never get marked as dead!");
    }

    // ---- the contract Xfer walk: GPL ImmortalBody::xfer writes its own version then chains
    // ActiveBody::xfer. ImmortalBody owns no mutable sim state of its own, so there is no
    // field to add to the walk - only the version wrapper (F9: declaration order, ours) over
    // the base walk. HasSimXfer is inherited (true) from ActiveBody. ----

    public override void Xfer(SimCore.Sync.IXfer xfer)
    {
        xfer.XferVersion(1);
        base.Xfer(xfer);
    }

    internal override void Load(StatePersister reader)
    {
        // Retail .sav layout (F9-exempt legacy reader): GPL ImmortalBody::xfer is version +
        // ActiveBody::xfer, with no own state.
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();
    }
}

/// <summary>
/// Prevents the object from dying (health is floored at 1). Adds no INI fields of its own
/// over <see cref="ActiveBodyModuleData"/>.
/// </summary>
[SimDataAudited]
public sealed class ImmortalBodyModuleData : ActiveBodyModuleData
{
    internal static new ImmortalBodyModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    // ImmortalBody carries no INI fields beyond ActiveBody's, so the audit contribution of
    // THIS class is vacuous (empty concat). The inherited health vocabulary belongs to
    // ActiveBodyModuleData, whose Fix64 audit is the separate ActiveBody task; gapmap G1
    // confirms parsing is byte-identical to the baseline.
    private static new readonly IniParseTable<ImmortalBodyModuleData> FieldParseTable = ActiveBodyModuleData.FieldParseTable
        .Concat(new IniParseTable<ImmortalBodyModuleData>());

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new ImmortalBody(gameObject, gameEngine, this);
    }
}
