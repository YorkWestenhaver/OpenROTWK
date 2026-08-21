// ProductionQueueHordeContain (damage half) - the ONE float-substrate crossing this module
// needs (D-7, the SimTransformBridge/SimHordeMember precedent): DamageInfo/DamageInfoInput are
// legacy float substrate (Amount is a plain float on both sides), the same seam SimHordeMember's
// OnDamage already rides. This partial-class half carries NO [SimState] attribute anywhere in
// THIS file, so the per-file SIMCORE quarantine scope (SimCoreScope.DeclaresSimStateType, which
// scans one syntax tree at a time) never turns on for it, exactly as SimTransformBridge.cs stays
// out of scope by declaring no [SimState] type. The Fix64 percentage from ProductionQueueHordeContain.cs
// is converted to float exactly once, here, and every field this half touches (_data, _slots,
// GameObject, Context) is the SAME instance state the [SimState] half owns - partial classes
// share one field set, so nothing is duplicated or re-xfered.
//
// Spec: "applies DamagePercentToUnits to members when the container takes damage" - propagated
// per hit, on the ACTUAL damage dealt (post-armor), to every seated member.

using OpenSage.SimCore.Numerics;

namespace OpenSage.Logic.Object;

public partial class ProductionQueueHordeContain : IDamageModule
{
    public void OnDamage(in DamageInfo damageInfo)
    {
        if (damageInfo.Result.NoEffect || _data.DamagePercentToUnits == Fix64.Zero)
        {
            return;
        }

        var memberDamage = damageInfo.Result.ActualDamageDealt * _data.DamagePercentToUnits.ToFloatForDisplay();
        if (memberDamage <= 0f)
        {
            return;
        }

        foreach (var occupant in _slots)
        {
            if (!occupant.IsValid)
            {
                continue;
            }
            var member = Context.GameLogic.GetObjectById(occupant);
            member?.AttemptDamage(new DamageInfoInput(GameObject)
            {
                DamageType = DamageType.Unresistable,
                DeathType = DeathType.Normal,
                Amount = memberDamage,
            });
        }
    }
}
