// UnitCrateCollide - crate collision module that spawns UnitCount units of UnitName, for the
// collecting player's team, scattered around the crate (R12 port; task packet
// unit-crate-collide).
//
// No generals-gpl checkout is available in this workspace, so this port is implemented
// directly from the task-packet behavioral summary (translate what is specified; do not
// invent beyond it) rather than a decompiled/GPL source translation.
//
// FINDINGS (behavior-fact gaps / seam constraints, filed not invented):
//   F-UCC-1 This file is deliberately NOT [SimState]-marked. CollideModule.OnCollide's
//     signature is fixed by the legacy ICollideModule interface
//     (GameObject, in Vector3, in Vector3) - unmigrated float substrate that the SimCore
//     quarantine analyzer would reject (SIMCORE002 bans anything under System.Numerics) the
//     moment a file carrying an [SimState] type also carries that override. Collide/ is not
//     yet in SimCoreScopedDirs.txt, and every actual computation below (angle, radius,
//     placement, clearance) is Fix64/FixVector3 end to end regardless - only the required
//     interface plumbing is float-shaped. A future round that migrates ICollideModule itself
//     onto a Fix64 signature can drop this file into full SimState scope.
//   F-UCC-2 Placement clearance uses a fixed constant (SpawnClearance) per live neighbour
//     rather than each neighbour's/spawned unit's real bounding geometry: ISimContext's
//     asset-store seam (IAssetStore) exposes object-definition lookup by name only, not
//     per-template geometry. A future round that grows IAssetStore can replace the constant
//     with the real bounding radii.
//   F-UCC-3 "Plays free-unit pickup audio on successful execution" (task packet) is read as
//     "at least one unit was actually spawned": an unresolvable UnitName (TC3) or
//     UnitCount <= 0 (TC4) both play no audio and spawn nothing.

using OpenSage.Data.Ini;
using OpenSage.Logic.Object.Locomotion;
using OpenSage.SimCore.Numerics;

namespace OpenSage.Logic.Object;

public sealed class UnitCrateCollide : CrateCollide
{
    // Placement clearance kept from any live neighbour (F-UCC-2).
    private static readonly Fix64 SpawnClearance = new Fix64(5);

    // Scatter radius ceiling (task packet: "0-20 unit radius").
    private static readonly Fix64 MaxScatterRadius = new Fix64(20);

    // Bounded retries per spawned unit before falling back to the last drawn candidate,
    // rather than looping forever when the crate is deep in a crowd.
    private const int MaxPlacementAttempts = 8;

    private readonly UnitCrateCollideModuleData _data;

    public UnitCrateCollide(GameObject gameObject, ISimContext context, UnitCrateCollideModuleData data)
        : base(gameObject, context)
    {
        _data = data;
    }

    public override void OnCollide(GameObject other, in System.Numerics.Vector3 location, in System.Numerics.Vector3 normal)
    {
        // The float location/normal are unused: every position/orientation this module needs
        // is pulled through the Fix64 seam below (F-UCC-1).
        Execute(other);
    }

    private void Execute(GameObject collector)
    {
        if (collector is null || _data.UnitCount <= 0)
        {
            // TC4: UnitCount <= 0 -> no spawn, no audio.
            return;
        }

        var definition = string.IsNullOrEmpty(_data.UnitName)
            ? null
            : Context.Assets.GetObjectDefinition(_data.UnitName);
        if (definition is null)
        {
            // TC3: unresolvable (or unset) UnitName fails gracefully - no spawn, no audio.
            return;
        }

        var owner = collector.Owner;
        var team = owner?.DefaultTeam;

        // TC6: spawned units inherit the collector's orientation.
        var orientation = SimTransformBridge.PullYaw(collector);
        var anchor = SimTransformBridge.PullPosition(GameObject);

        // Snapshot of nearby live objects within the scatter radius, used as placement
        // obstacles; grows as each newly spawned unit becomes an obstacle for the next.
        var obstacles = new System.Collections.Generic.List<FixVector3>();
        foreach (var candidate in Context.Partition.QueryObjectsInRadius(GameObject, MaxScatterRadius))
        {
            if (candidate == GameObject || candidate.IsEffectivelyDead)
            {
                continue;
            }
            obstacles.Add(SimTransformBridge.PullPosition(candidate));
        }

        var spawnedAny = false;
        for (var i = 0; i < _data.UnitCount; i++)
        {
            var offset = PickClearOffset(anchor, obstacles);

            // TC1/TC2/TC5/TC6: spawned at anchor + offset (0-20 unit scatter radius),
            // the collector's team, the collector's orientation.
            var spawned = Context.GameLogic.CreateObjectAt(definition, owner, GameObject, offset, orientation);
            if (spawned is null)
            {
                continue;
            }

            spawned.Team = team;
            obstacles.Add(anchor + offset);
            spawnedAny = true;
        }

        if (spawnedAny)
        {
            Context.Events.FireCrateFreeUnitPickupSound();
        }
    }

    /// <summary>
    /// Draws a scatter offset in [0, MaxScatterRadius) at a uniformly random angle, retrying
    /// up to <see cref="MaxPlacementAttempts"/> times for one that keeps
    /// <see cref="SpawnClearance"/> from every known obstacle (F-UCC-2). Falls back to the
    /// last drawn offset when no clear spot is found - a best-effort placement rather than a
    /// failed pickup.
    /// </summary>
    private FixVector3 PickClearOffset(in FixVector3 anchor, System.Collections.Generic.List<FixVector3> obstacles)
    {
        var offset = FixVector3.Zero;
        for (var attempt = 0; attempt < MaxPlacementAttempts; attempt++)
        {
            var angle = Context.GameLogicRandom.NextFix64(Fix64.Zero, Fix64.PiTimes2);
            var radius = Context.GameLogicRandom.NextFix64(Fix64.Zero, MaxScatterRadius);
            offset = new FixVector3(radius * FixTrig.Cos(angle), radius * FixTrig.Sin(angle), Fix64.Zero);

            if (IsClear(anchor + offset, obstacles))
            {
                return offset;
            }
        }
        return offset;
    }

    private static bool IsClear(in FixVector3 position, System.Collections.Generic.List<FixVector3> obstacles)
    {
        foreach (var obstacle in obstacles)
        {
            if ((position - obstacle).Length() < SpawnClearance)
            {
                return false;
            }
        }
        return true;
    }

    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        base.Load(reader);
    }
}

public sealed class UnitCrateCollideModuleData : CrateCollideModuleData
{
    internal static UnitCrateCollideModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<UnitCrateCollideModuleData> FieldParseTable = CrateCollideModuleData.FieldParseTable
        .Concat(new IniParseTable<UnitCrateCollideModuleData>
        {
            { "UnitCount", (parser, x) => x.UnitCount = parser.ParseInteger() },
            { "UnitName", (parser, x) => x.UnitName = parser.ParseAssetReference() }
        });

    public int UnitCount { get; private set; }
    public string UnitName { get; private set; }

    internal override UnitCrateCollide CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new UnitCrateCollide(gameObject, gameEngine.SimContext, this);
    }
}
