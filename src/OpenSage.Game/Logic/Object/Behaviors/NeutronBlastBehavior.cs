// NeutronBlastBehavior - R13 port.
//
// GPL source: generals-community/GeneralsMD/Code/GameEngine/Include/GameLogic/Module/
// NeutronBlastBehavior.h (class NeutronBlastBehaviorModuleData) + generals-community/
// GeneralsMD/Code/GameEngine/Source/GameLogic/Object/Behavior/NeutonBlastBehavior.cpp (GPL's
// own filename typo, "Neuton" not "Neutron" - the class names inside are spelled correctly).
// [AddedIn(SageGame.CncGeneralsZeroHour)] - verbatim Generals/ZH content, full onDie()/
// neutronBlastToObject() implementation directly translatable.
//
// Design: ctor sleeps forever (SetWakeFrame(UpdateSleepTime.Forever)); Update() always returns
// Forever too - this module never ticks, all behavior is OnDie-driven (NeutronBlastBehavior.cpp
// :46-49,96-99). On OnDie(), scans BlastRadius via Context.Partition.QueryObjectsInRadius
// (already live-objects-only, ascending ObjectId, per IPartitionQuery's contract - no separate
// liveness filter needed), applying the hitAir pre-filter (NeutronBlastBehavior.cpp:47-52)
// before calling NeutronBlastToObject on each surviving candidate.
//
// FINDINGS (behavior-fact gaps, filed not invented):
//   F-NBB-1/F-NBB-2 (Contain branch): GameObject.Contain is always null in this engine snapshot
//   (no Contain module landed yet), so killAllContained() is wired correctly but presently
//   inert end-to-end - same class as BunkerBusterBehavior's F-BBB-1/F-BBB-2. IContainModule
//   exposes no "kill all contained" verb, so this ports as snapshot-then-kill of
//   ContainedItems.ToArray() (BunkerBusterBehavior's own pattern).
//   F-NBB-3 (FilterSameMapStatus): GPL's submarine/normal-map partition split has no modeled
//   counterpart anywhere in this engine snapshot; not invented around.
//   F-NBB-4 (AIIdle no-op): AIUpdate.AIIdle is a standing no-op stub today - same pre-existing
//   gap class as EmpUpdate's F-EMP-6 and LeafletDropBehavior's F-LDB-3.
//   F-NBB-5 (DeselectObject): no ISimContext/IGameLogic facade exposes a deselect operation, and
//   the underlying GameLogic.DeselectObject is itself a standing no-op stub - unmodeled, filed
//   not invented, same class as EmpUpdate's F-EMP-5.
//   F-NBB-6 (terrain decal clear): Drawable.SetTerrainDecal is a float-substrate, client-only
//   call with no ISimContext facade - unmodeled from [SimState] scope, same class as EmpUpdate's
//   F-EMP-5 and LeafletDropBehavior's posture toward client-only effects.
//
// This module has zero mutable sim state: GPL's own xfer() (NeutronBlastBehavior.cpp:176-187)
// persists nothing beyond the version stamp - every field this module reads lives on the
// immutable ModuleData, not on the runtime instance.

using System.Linq;
using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class NeutronBlastBehavior : UpdateModule, IDieModule
{
    private readonly NeutronBlastBehaviorModuleData _data;

    // No mutable sim state: GPL's own xfer() persists nothing beyond the version byte either
    // (NeutronBlastBehavior.cpp:176-187) - every field this module reads lives on _data.

    public NeutronBlastBehavior(GameObject gameObject, ISimContext context, NeutronBlastBehaviorModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        // GPL ctor: setWakeFrame(UPDATE_SLEEP_FOREVER) - this module never ticks; all behavior
        // is OnDie-driven.
        SetWakeFrame(UpdateSleepTime.Forever);
    }

    public override UpdateSleepTime Update()
    {
        // GPL update(): return UPDATE_SLEEP_FOREVER unconditionally.
        return UpdateSleepTime.Forever;
    }

    void IDieModule.OnDie(in DamageInfoInput damageInput)
    {
        if (_data.BlastRadius <= Fix64.Zero)
        {
            return;
        }

        var self = GameObject;

        foreach (var candidate in Context.Partition.QueryObjectsInRadius(self, _data.BlastRadius))
        {
            // GPL onDie's own hitAir gate, applied before neutronBlastToObject is ever called.
            if (!_data.AffectAirborne
                && (candidate.IsKindOf(ObjectKinds.Aircraft) || Context.Terrain.IsSignificantlyAboveTerrain(candidate)))
            {
                continue;
            }

            NeutronBlastToObject(candidate);
        }
    }

    /// <summary>GPL neutronBlastToObject() (NeutronBlastBehavior.cpp:103-157).</summary>
    private void NeutronBlastToObject(GameObject candidate)
    {
        var self = GameObject;

        if (candidate == self)
        {
            return;
        }

        if (!_data.AffectAllies && self.GetRelationship(candidate) == RelationshipType.Allies)
        {
            return;
        }

        if (candidate.IsKindOf(ObjectKinds.Infantry))
        {
            candidate.Kill();
        }

        // F-NBB-1/F-NBB-2: GameObject.Contain is always null today (no Contain module landed);
        // wired correctly, inert end-to-end until one lands. IContainModule has no
        // "kill all contained" verb, so this snapshots-then-kills each occupant instead.
        var contain = candidate.Contain;
        if (contain != null)
        {
            foreach (var occupant in contain.ContainedItems.ToArray())
            {
                occupant.Kill();
            }
        }

        if (candidate.IsKindOf(ObjectKinds.Vehicle) && !candidate.IsKindOf(ObjectKinds.Drone))
        {
            if (candidate.IsKindOf(ObjectKinds.CliffJumper))
            {
                candidate.Kill();
            }
            else
            {
                candidate.SetDisabled(DisabledType.Unmanned);

                // F-NBB-4: AIUpdate.AIIdle is a standing no-op stub today.
                candidate.AIUpdate?.AIIdle(CommandSourceType.FromAI);

                // F-NBB-5/F-NBB-6: DeselectObject (no facade, and itself a no-op stub) and the
                // terrain-decal clear (float-substrate Drawable call, no ISimContext facade) are
                // unmodeled - same class of gap as EmpUpdate's F-EMP-5.

                candidate.Team = Context.Players.NeutralPlayer.DefaultTeam;
            }
        }
    }

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        // No fields: matches GPL's own xfer(), which persists nothing beyond the version stamp.
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================

[SimDataAudited]
[AddedIn(SageGame.CncGeneralsZeroHour)]
public sealed class NeutronBlastBehaviorModuleData : BehaviorModuleData
{
    internal static NeutronBlastBehaviorModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<NeutronBlastBehaviorModuleData> FieldParseTable = new IniParseTable<NeutronBlastBehaviorModuleData>
    {
        { "BlastRadius", (parser, x) => x.BlastRadius = parser.ParseFix64() },
        { "AffectAirborne", (parser, x) => x.AffectAirborne = parser.ParseBoolean() },
        { "AffectAllies", (parser, x) => x.AffectAllies = parser.ParseBoolean() },
    };

    /// <summary>Scan radius fed straight into Context.Partition.QueryObjectsInRadius (GPL default 10.0).</summary>
    public Fix64 BlastRadius { get; private set; } = Fix64.FromDecimalLiteral("10");

    /// <summary>GPL default TRUE.</summary>
    public bool AffectAirborne { get; private set; } = true;

    /// <summary>GPL default TRUE.</summary>
    public bool AffectAllies { get; private set; } = true;

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new NeutronBlastBehavior(gameObject, gameEngine.SimContext, this);
    }
}
