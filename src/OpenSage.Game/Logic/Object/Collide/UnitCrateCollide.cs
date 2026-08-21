// UnitCrateCollide - crate collision module that spawns UnitCount units of UnitName, for the
// collecting player's team, scattered around the crate (R12 port; R13 fix pass; task packet
// unit-crate-collide).
//
// GPL reference: generals-gpl/GeneralsMD/Code/GameEngine/Source/GameLogic/Object/Collide/
// CrateCollide/{CrateCollide,UnitCrateCollide}.cpp and
// .../GameLogic/Object/PartitionManager.cpp (PartitionManager::findPositionAround).
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
//   F-UCC-2 GPL's findPositionAround (PartitionManager.cpp:3887-3961) rejects candidate rings
//     via terrain/pathfind-cell legality (cliff cells, impassable cells, water flags) that
//     ISimContext has no seam for (IAssetStore exposes object-definition lookup by name only,
//     not terrain/pathfind queries or per-template bounding geometry). This port keeps the
//     real algorithm's *shape* - one random start angle per spawned unit, then a deterministic
//     expanding-ring/angle search that mirrors findPositionAround's ringSpacing/angleSpacing
//     math exactly - but substitutes a fixed per-neighbour clearance (SpawnClearance) for the
//     terrain-legality test as the per-candidate accept/reject check. A future round that
//     grows ISimContext with terrain/pathfind + per-template geometry seams can replace that
//     substitute check with the real one.
//   F-UCC-3 "Plays free-unit pickup audio on successful execution" (task packet) is read as
//     "at least one unit was actually spawned": an unresolvable UnitName (TC3) or
//     UnitCount <= 0 (TC4) both play no audio and spawn nothing.
//   R13 fixes applied against the GPL source above (prior header wrongly claimed no GPL
//     checkout was available and this was ported from an invented behavioral summary instead):
//       - The crate is now destroyed on successful execution (CrateCollide::onCollide
//         destroys the crate whenever executeCrateBehavior returns TRUE, which
//         UnitCrateCollide::executeCrateBehavior does unconditionally once unitType resolves -
//         UnitCrateCollide.cpp:56-92).
//       - The placement search anchors on the COLLECTOR's position
//         (`Coord3D creationPoint = *other->getPosition();`, UnitCrateCollide.cpp:72), not the
//         crate's own position.
//       - The placement search now draws exactly one random value (the start angle) per
//         spawned unit, matching findPositionAround's single
//         `GameLogicRandomValueReal(0, TWO_PI)` draw (PartitionManager.cpp:3909-3914), instead
//         of drawing angle+radius per retry attempt.
//   R13.5 (crate-gate): the shared CrateCollide::isValidToExecute gate now lives on the base
//     class and is called here before anything is spawned. Previously this module had no gate
//     whatsoever, so a neutral-controlled unit, a corpse, a ForbiddenKindOf unit, a non-Unit
//     object or an airborne crate all produced free units.

using OpenSage.Data.Ini;
using OpenSage.Logic.Object.Locomotion;
using OpenSage.SimCore.Numerics;

namespace OpenSage.Logic.Object;

public sealed class UnitCrateCollide : CrateCollide
{
    // Placement clearance kept from any live neighbour (F-UCC-2 substitute legality check).
    private static readonly Fix64 SpawnClearance = new Fix64(5);

    // Scatter radius ceiling: GPL's FindPositionOptions.maxRadius = 20.0f (minRadius = 0.0f)
    // (UnitCrateCollide.cpp:75-76).
    private static readonly Fix64 MaxScatterRadius = new Fix64(20);

    // GPL's PartitionManager.cpp:3877 `static Real ringSpacing = 5.0f;` - the ring step used
    // by findPositionAround's outer search loop.
    private static readonly Fix64 RingSpacing = new Fix64(5);

    private readonly UnitCrateCollideModuleData _data;

    public UnitCrateCollide(GameObject gameObject, ISimContext context, UnitCrateCollideModuleData data)
        : base(gameObject, context, data)
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
        // R13.5 (crate-gate): GPL's CrateCollide::onCollide never reaches
        // UnitCrateCollide::executeCrateBehavior without isValidToExecute passing first
        // (CrateCollide.cpp). This port used to have NO gate at all - a neutral-controlled,
        // dead, ForbiddenKindOf, airborne-crate or non-Unit collector all collected freely.
        // UnitCrateCollide adds no leaf checks of its own, so the shared base gate IS the
        // whole gate here.
        if (!IsValidToExecute(collector))
        {
            return;
        }

        if (_data.UnitCount <= 0)
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

        // GPL anchors the placement search on the COLLECTOR's position
        // (`Coord3D creationPoint = *other->getPosition();`, UnitCrateCollide.cpp:72), not the
        // crate's own position.
        var anchor = SimTransformBridge.PullPosition(collector);

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

        // GPL's CrateCollide::onCollide destroys the crate whenever executeCrateBehavior
        // returns TRUE (CrateCollide.cpp:115-148); UnitCrateCollide::executeCrateBehavior
        // returns TRUE unconditionally once unitType resolves, regardless of how many
        // individual newObject calls actually succeeded (UnitCrateCollide.cpp:56-92). We
        // already returned above for an unresolvable UnitName/UnitCount<=0, so reaching here
        // means unitType resolved and the crate is always consumed.
        Context.GameLogic.DestroyObject(GameObject);
    }

    /// <summary>
    /// Ports GPL's PartitionManager::findPositionAround (PartitionManager.cpp:3887-3961) for a
    /// single spawned unit: draws exactly one random start angle, then walks the same
    /// expanding-ring / ping-ponging-angle search GPL uses (ringSpacing = 5, angleSpacing =
    /// 2*Pi at the innermost ring and (ringSpacing/(dist+1)) * (2*Pi/6) beyond it), accepting
    /// the first candidate that clears every known obstacle by <see cref="SpawnClearance"/>
    /// (F-UCC-2 substitute for GPL's terrain/pathfind legality test, which ISimContext has no
    /// seam for). Falls back to the anchor itself (offset zero) when no ring position clears -
    /// this matches GPL's own fallback: `findPositionAround(&creationPoint, ..., &creationPoint)`
    /// passes the same pointer as both center and result, so when the search runs out of rings
    /// without success, `result` (and thus the unit's spawn point) is left at the original
    /// `creationPoint` - the collector's position - untouched.
    /// </summary>
    private FixVector3 PickClearOffset(in FixVector3 anchor, System.Collections.Generic.List<FixVector3> obstacles)
    {
        var startAngle = Context.GameLogicRandom.NextFix64(Fix64.Zero, Fix64.PiTimes2);

        for (var dist = Fix64.Zero; dist <= MaxScatterRadius; dist += RingSpacing)
        {
            var angleSpacing = dist == Fix64.Zero
                ? Fix64.PiTimes2
                : (RingSpacing / (dist + Fix64.One)) * (Fix64.PiTimes2 / new Fix64(6));

            var samples = (int)Fix64.Ceiling((Fix64.PiTimes2 / angleSpacing) / Fix64.Two);

            for (var i = 0; i < samples; i++)
            {
                var candidate = TryRingOffset(anchor, dist, startAngle + angleSpacing * new Fix64(i), obstacles);
                if (candidate.HasValue)
                {
                    return candidate.Value;
                }

                if (i != 0)
                {
                    candidate = TryRingOffset(anchor, dist, startAngle - angleSpacing * new Fix64(i), obstacles);
                    if (candidate.HasValue)
                    {
                        return candidate.Value;
                    }
                }
            }
        }

        // No ring position cleared: fall back to the anchor (the collector's position),
        // matching GPL's untouched-`creationPoint` fallback described above.
        return FixVector3.Zero;
    }

    private static FixVector3? TryRingOffset(in FixVector3 anchor, Fix64 dist, Fix64 angle, System.Collections.Generic.List<FixVector3> obstacles)
    {
        var offset = new FixVector3(dist * FixTrig.Cos(angle), dist * FixTrig.Sin(angle), Fix64.Zero);
        return IsClear(anchor + offset, obstacles) ? offset : null;
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
