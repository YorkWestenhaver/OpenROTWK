// ReflectDamage - R13 port (modules-r13/specs/ReflectDamageModuleData.md). BFME/BFME2-only
// "thorn armor" mechanic; no GPL sibling exists (grep -rn "reflect" generals-gpl
// generals-community turns up only unrelated W3DShaderManager/UI-material hits), so this is a
// data-derivation port: the three INI fields fully specify a closed-form rule resolved against
// already-landed engine primitives. See the spec's §0/§1 for the full source trail; the two load-
// bearing precedents reused verbatim are TransitionDamageFX (the BitArray<DamageType> "null mask
// = match every type" convention, PassesTypeGate) and PorcupineFormationBody (the transient
// _reflecting reentrancy guard, the self/invalid-source predicate, and the target-destroyed
// guard before delivery - the one landed precedent for this exact "reflect damage back at
// whoever dealt it" mechanic shape).
//
// MUTABLE SIM STATE INVENTORY: none of its own besides the transient _reflecting guard, which is
// explicitly NOT sim state (always false between frames - PorcupineFormationBody F-PFB-3 shape)
// and is therefore not xfered. The Xfer walk is version-only, matching TransitionDamageFX /
// BoneFXDamage for a module with no persisted field.
//
// Not [SimState]: OnDamage reads damageInfo.Result.ActualDamageDealt off the legacy float
// DamageInfo callback surface and crosses it through CombatLegacyBridge.QuantizeFloat - the same
// straddle TransitionDamageFX's header records ("cannot be marked [SimState] until the Body-batch
// flag-day migrates the DamageModule callback surface to Fix64"). Everything downstream of that
// one crossing (percentage multiply, threshold compare, delivery) is Fix64 end to end through
// DamagePipeline.DealDirectDamage, same as PorcupineFormationBody's reflect path.

using OpenSage.Data.Ini;
using OpenSage.Mathematics;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

public sealed class ReflectDamage : DamageModule
{
    private readonly ReflectDamageModuleData _data;

    // Transient within-call reentrancy guard (PorcupineFormationBody F-PFB-3 shape): a
    // reflected hit must never itself trigger a reflection (a reflector damaging itself, or an
    // A->B->A ping-pong between two reflectors within one synchronous call stack). Always false
    // between frames -> not sim state, not xfered.
    private bool _reflecting;

    internal ReflectDamage(GameObject gameObject, ISimContext context, ReflectDamageModuleData data)
        : base(gameObject, context)
    {
        _data = data;
    }

    /// <summary>
    /// If this hit's dealt amount qualifies (matching type, at/above the minimum), reflect
    /// <see cref="ReflectDamageModuleData.ReflectDamagePercentage"/> of it back at the attacker
    /// through the landed S1 delivery entry point. See the spec's §1 for the full gate order and
    /// its rationale.
    /// </summary>
    public override void OnDamage(in DamageInfo damageInfo)
    {
        if (_reflecting)
        {
            return;
        }

        // Type gate: the ORIGINAL hit's type (Request.DamageType, not the visual-only
        // DamageFXOverride). A null mask matches every type (TransitionDamageFX.PassesTypeGate
        // convention, same BitArray<DamageType> field type).
        var damageType = damageInfo.Request.DamageType;
        if (_data.DamageTypesToReflect != null && !_data.DamageTypesToReflect.Get(damageType))
        {
            return;
        }

        // Amount gate: the DEALT amount (post-armor, pre-clip), quantized at the one sanctioned
        // float -> Fix64 crossing point.
        var dealt = CombatLegacyBridge.QuantizeFloat(damageInfo.Result.ActualDamageDealt);
        if (dealt < _data.MinimumDamageToReflect)
        {
            return;
        }

        var reflected = dealt * _data.ReflectDamagePercentage;
        if (reflected <= Fix64.Zero)
        {
            return;
        }

        // Self/invalid source (PorcupineFormationBody.ShouldReflect's exact predicate): no
        // attacker to hit back, or the object damaged itself.
        var sourceId = damageInfo.Request.SourceID;
        if (!sourceId.IsValid || sourceId == GameObject.Id)
        {
            return;
        }

        var source = Context.GameLogic.GetObjectById(sourceId);
        if (source == null || source.IsDestroyed)
        {
            return;
        }

        _reflecting = true;
        try
        {
            DamagePipeline.DealDirectDamage(source, new CombatDamageInput
            {
                SourceId = GameObject.Id,
                DamageType = DamageType.Reflected,
                Amount = reflected,
                // DeathType left at its struct default (Normal): nothing in the three INI
                // fields implies a special death type for the reflected hit.
            });
        }
        finally
        {
            _reflecting = false;
        }
    }

    // OnHealing / OnBodyDamageStateChange are left as the DamageModule no-op defaults: the three
    // INI fields say nothing about healing or damage-state transitions.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
    }

    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();
    }
}

[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public sealed class ReflectDamageModuleData : DamageModuleData
{
    internal static ReflectDamageModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<ReflectDamageModuleData> FieldParseTable = new IniParseTable<ReflectDamageModuleData>
    {
        { "DamageTypesToReflect", (parser, x) => x.DamageTypesToReflect = parser.ParseEnumBitArray<DamageType>() },
        { "ReflectDamagePercentage", (parser, x) => x.ReflectDamagePercentage = parser.ParseFix64Percentage() },
        { "MinimumDamageToReflect", (parser, x) => x.MinimumDamageToReflect = parser.ParseFix64() }
    };

    /// <summary>Damage types that trigger a reflect; null = every type (unset INI key).</summary>
    public BitArray<DamageType> DamageTypesToReflect { get; private set; }

    /// <summary>Fraction of the dealt (post-armor) amount reflected back at the attacker.</summary>
    public Fix64 ReflectDamagePercentage { get; private set; }

    /// <summary>Minimum dealt (post-armor) amount required to trigger a reflect (inclusive).</summary>
    public Fix64 MinimumDamageToReflect { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
        => new ReflectDamage(gameObject, gameEngine.SimContext, this);
}
