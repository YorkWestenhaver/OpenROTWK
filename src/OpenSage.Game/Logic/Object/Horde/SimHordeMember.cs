// SimHordeMember - the member-side horde tag (spec §2: HordeMemberCollide has NO INI
// fields; it marks member-side handling). Our runtime role: hold the back-reference to
// the owning horde and forward member damage to the horde's flank test (spec §6: "when a
// member is damaged, the horde runs the flank test").
//
// NOT [SimState] (the TransitionDamageFX precedent): the OnDamage callback rides the
// legacy float DamageInfo surface, and the attacker-position read falls back through the
// F4 wire boundary (SimTransformBridge) when the attacker has no S2 locomotor. Everything
// forwarded to the horde is Fix64/ObjectId. The one mutable sim field (_hordeId) is in
// the contract Xfer walk.

using OpenSage.Data.Ini;
using OpenSage.Logic.Object.Locomotion;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object.Horde;

public sealed class SimHordeMember : DamageModule
{
    // ---- mutable sim state (the whole inventory) ----
    private ObjectId _hordeId;

    public SimHordeMember(GameObject gameObject, ISimContext context, SimHordeMemberModuleData data)
        : base(gameObject, context)
    {
    }

    public ObjectId HordeId => _hordeId;

    /// <summary>Called by the horde when this member is seated (spec §5.2 registration).</summary>
    public void AttachToHorde(ObjectId hordeId) => _hordeId = hordeId;

    public SimHordeContain GetHorde()
    {
        if (!_hordeId.IsValid)
        {
            return null;
        }
        var horde = Context.GameLogic.GetObjectById(_hordeId);
        return horde == null || horde.IsDestroyed ? null : horde.FindBehavior<SimHordeContain>();
    }

    public override void OnDamage(in DamageInfo damageInfo)
    {
        var horde = GetHorde();
        if (horde == null)
        {
            return;
        }
        var attackerId = damageInfo.Request.SourceID;
        if (!attackerId.IsValid)
        {
            return;
        }
        var attacker = Context.GameLogic.GetObjectById(attackerId);
        if (attacker == null)
        {
            return;
        }

        // Attacker position, Fix64: the S2 sim transform when the attacker is
        // locomotor-driven, else one quantizing pull through the F4 wire boundary
        // (same-binary deterministic; finding HORDE-F4).
        FixVector2 attackerPosition;
        var attackerMover = attacker.FindBehavior<SimLocomotorUpdate>();
        if (attackerMover != null && attackerMover.TransformInitialized)
        {
            var p = attackerMover.Physics.Position;
            attackerPosition = new FixVector2(p.X, p.Y);
        }
        else
        {
            var p = SimTransformBridge.PullPosition(attacker);
            attackerPosition = new FixVector2(p.X, p.Y);
        }

        horde.NotifyMemberDamaged(GameObject.Id, attackerPosition);
    }

    // ---- the single walk (F8 Objects channel; declaration order, F9) ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferObjectId("HordeId", ref _hordeId);
    }
}

/// <summary>
/// Member-side horde marker. The binary's HordeMemberCollide parses an EMPTY block
/// (spec §2); registered under the interim name "SimHordeMember" alongside the legacy
/// "HordeMemberCollide" entry, which stays untouched.
/// </summary>
[SimDataAudited]
public sealed class SimHordeMemberModuleData : BehaviorModuleData
{
    internal static SimHordeMemberModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<SimHordeMemberModuleData> FieldParseTable =
        new IniParseTable<SimHordeMemberModuleData>();

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new SimHordeMember(gameObject, gameEngine.SimContext, this);
    }
}
