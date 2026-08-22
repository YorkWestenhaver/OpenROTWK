// AimWeaponBehaviorModuleData - split out of AimWeaponBehavior.cs (R15 port of the AIM_NEAR
// half; spec: bfme2-workbench/research/modules-r13/specs/AimWeaponBehaviorModuleData.md).
//
// Kept in its own file, separate from the [SimState] AimWeaponBehavior class, specifically so
// that AimHighThreshold/AimLowThreshold can stay float: the SimCore scoped-analysis mode
// (docs/simcore-analyzer.md "Attachment modes") pulls in a whole file once it declares any
// [SimState] type, and SIMCORE001 bans float there. Those two fields are HELD (F-AWB-1/-2) and
// have no sim consumer, so quantizing them would imply a reading this port deliberately does
// not invent - the same ruling, and the same file split, as GiveUpgradeUpdateModuleData.cs.
//
// The base class stays UpgradeModuleData (spec §1.5, F-AWB-3): ModuleKinds.Upgrade vs .Update
// affects ObjectDefinition.AddModuleData's template-inheritance displacement rule,
// UpdateModuleData drags in an unrelated ContainModuleData parse surface, and keeping this base
// keeps TriggeredBy/StartsActive/etc. parseable (inert) for a mod that authors them. The AotR
// census is the load-bearing fact: 0 of 61 live shipped instances author any upgrade-gate field.

using OpenSage.Data.Ini;
using OpenSage.SimCore.Numerics;

namespace OpenSage.Logic.Object;

[AddedIn(SageGame.Bfme)]
[SimDataAudited]
// Narrowed from the R4-backlog blanket note: the AIM_NEAR half is now PORTED (the runtime
// AimWeaponBehavior class drives ModelConditionFlag.AimNear off AimNearDistance). What remains
// parse-only is exactly the two threshold fields named here.
[ParseOnly("PARTIAL: AimHighThreshold/AimLowThreshold (AIM_HIGH/AIM_LOW) are HELD - F-AWB-1/F-AWB-2, no ISimContext surface for another object's position or a relative height. AimNearDistance is ported.")]
public sealed class AimWeaponBehaviorModuleData : UpgradeModuleData
{
    internal static AimWeaponBehaviorModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<AimWeaponBehaviorModuleData> FieldParseTable = UpgradeModuleData.FieldParseTable
        .Concat(new IniParseTable<AimWeaponBehaviorModuleData>
        {
            { "AimHighThreshold", (parser, x) => x.AimHighThreshold = parser.ParseFloat() },
            { "AimLowThreshold", (parser, x) => x.AimLowThreshold = parser.ParseFloat() },
            { "AimNearDistance", (parser, x) => x.AimNearDistance = parser.ParseFix64() }
        });

    /// <summary>
    /// held: F-AWB-1. Parses (unchanged ParseFloat) and is stored, but the runtime never reads
    /// it. AIM_HIGH would need another object's position or a relative height/pitch, and no
    /// member of the frozen ISimContext surface returns either; adding one is a framework
    /// change (out of scope, api-freeze-v1 §6). GPL's Weapon::isWithinTargetPitch is a
    /// different mechanism with a different unit convention and does not ground it (F-AWB-6).
    /// Blast radius: 30 of 61 shipped instances, all authoring exactly 0.15.
    /// </summary>
    public float AimHighThreshold { get; private set; }

    /// <summary>
    /// held: F-AWB-2. Same reasoning as AimHighThreshold, with which it is co-authored 1:1 in
    /// every shipped instance checked; 23 of 61 shipped instances, all authoring exactly -0.15.
    /// </summary>
    public float AimLowThreshold { get; private set; }

    /// <summary>
    /// World-unit radius fed straight to the partition seam
    /// (IPartitionQuery.QueryObjectsInRadius): the victim being inside it raises
    /// ModelConditionFlag.AimNear. ParseFix64 round-half-up at parse, S5 quantization at load
    /// (design-module-api §2.2) - every live authored value in the shipped corpus is
    /// one-decimal or integer, so this is exact. Default Fix64.Zero (was float 0) is the
    /// degenerate "never AIM_NEAR" case and the MAJORITY shape of the corpus: 56 of the 61
    /// live instances never author this field at all (F-AWB-4).
    /// </summary>
    public Fix64 AimNearDistance { get; private set; } = Fix64.Zero;

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new AimWeaponBehavior(gameObject, gameEngine.SimContext, this);
    }
}
