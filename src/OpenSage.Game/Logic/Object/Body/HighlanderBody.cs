#nullable enable

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;

namespace OpenSage.Logic.Object;

/// <summary>
/// Takes damage according to armor, but can't die from normal damage.
/// Can die from <see cref="DamageType.Unresistable"/> though.
/// </summary>
/// <remarks>
/// Behavioral reference (clean-room, semantics only): GPL GeneralsMD
/// <c>GameLogic/Object/Body/HighlanderBody.cpp</c> — <c>attemptDamage</c> binds the incoming
/// amount to <c>min(amount, getHealth() - 1)</c> for every non-Unresistable hit, then defers to
/// <c>ActiveBody::attemptDamage</c>. The clamp is on the PRE-armor amount, exactly as the
/// original (ActiveBody then applies armor / damage-scalar / health, all in Fix64 per S1).
///
/// Prior partial-runtime BUG (shared with UndeadBody): the override recursed into itself
/// (<c>AttemptDamage(modified)</c>) instead of calling <c>base.AttemptDamage</c>, which never
/// reached the real health application. Fixed here (and in UndeadBody) by delegating to
/// <c>base.AttemptDamage</c>.
///
/// Mutable sim state inventory: EMPTY. All health state lives in the base ActiveBody's Fix64
/// <c>BodyDamageCore</c>, which supplies the whole contract Xfer walk; this class adds no field,
/// so it needs no Xfer of its own (GPL's Highlander <c>xfer</c> only wraps the base in a version
/// byte, and F9 makes field/version layout ours — ActiveBody already emits version 1).
/// </remarks>
[SimState]
public sealed class HighlanderBody : ActiveBody
{
    internal HighlanderBody(GameObject gameObject, IGameEngine gameEngine, HighlanderBodyModuleData moduleData)
        : base(gameObject, gameEngine, moduleData)
    {
    }

    public override DamageInfoOutput AttemptDamage(in DamageInfoInput damageInput)
    {
        // Unresistable damage bypasses the immortality clamp entirely.
        if (damageInput.DamageType == DamageType.Unresistable)
        {
            return base.AttemptDamage(damageInput);
        }

        // Bind to one hitpoint remaining afterwards: cap the requested amount at
        // (currentHealth - 1). Comparison and clamp happen in Fix64 against the canonical
        // health ledger; only the final write-back crosses to the legacy float amount field
        // (the blessed display boundary, F4). When the hit is already survivable we pass the
        // request through untouched, avoiding a needless float round-trip on the common path.
        var requested = CombatLegacyBridge.QuantizeFloat(damageInput.Amount);
        var survivable = DamageCore.CurrentHealth - Fix64.One;

        if (requested <= survivable)
        {
            return base.AttemptDamage(damageInput);
        }

        var clampedInput = damageInput;
        clampedInput.Amount = survivable.ToFloatForDisplay();
        return base.AttemptDamage(clampedInput);
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
/// Allows the object to take damage but not die. The object will only die from irresistable damage.
/// </summary>
[SimDataAudited]
public sealed class HighlanderBodyModuleData : ActiveBodyModuleData
{
    internal static new HighlanderBodyModuleData Parse(IniParser parser)
    {
        var result = parser.ParseBlock(FieldParseTable);
        result.ApplyHealthDefaults(parser);   // F-HB-1: the shadowing Parse must keep the base defaulting.
        return result;
    }

    private static new readonly IniParseTable<HighlanderBodyModuleData> FieldParseTable = ActiveBodyModuleData.FieldParseTable
        .Concat(new IniParseTable<HighlanderBodyModuleData>());

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new HighlanderBody(gameObject, gameEngine, this);
    }
}
