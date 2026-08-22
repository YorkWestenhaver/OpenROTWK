// DynamicPortalBehaviour - castle-wall traversal portal. R15 L5-P8 runtime port off the
// written behavioral spec bfme2-workbench/research/spec-dynamic-portal.md. No GPL sibling
// exists for this module (the spec records that grepping generals-gpl/ and
// generals-community/ for DynamicPortal/PortalBehavior returns zero hits - Generals/ZH has
// no climbable-wall mechanic at all), so the spec plus the AotR INI corpus is the whole
// source set. Section references below are to that spec.
//
// SPELLING IS LOAD-BEARING: the retail/INI keyword is the British "DynamicPortalBehaviour"
// (registered against that spelling in BehaviorModule.cs); this class keeps the American
// spelling per the fork's own naming convention. Do not "fix" the mismatch (spec §0).
//
// WHAT THIS MODULE IS (spec §1, §8 Q1): it materialises a short chain of helper objects
// along a run of the owning object's model bones so that ordinary pathfinding can route a
// unit through/over a wall, hands the chain's head to the pathfinder, and destroys the chain
// on teardown. It is a REGISTRATION PRODUCER, not a pathfinder and not a traversal state
// machine - the port is correspondingly small.
//
// TODO-spec (deliberate, filed not invented - spec §9 lists these as the Phase-2 boundary):
//   - The waypoint helper object's own template ("#dynamicportal_wp") and the class that
//     reads the fields stamped onto it are not recovered (spec §9 item 3). This port creates
//     the helper objects when the template resolves, and holds the activation deadline on the
//     module because there is no helper class to stamp it onto. The AllowEnemies and
//     ObjectFilter stamps have no holder at all yet and no reader either (the eligibility
//     decision lives in whatever consumes the helper object, spec §8 Q8), so they stay on the
//     ModuleData until the helper class ports. Note also that the template name begins with
//     '#', which the INI tokenizer reads as a macro-function sigil, so NO .ini file can
//     declare it: the missing-template path below is the one the fork actually takes today.
//   - The pathfinder-side registration/unregistration and the (first,last) route pairing
//     (spec §9 item 2) are calls whose callees are not identified. This port records the
//     route heads and pairs it WOULD register, deterministically, and does not invent a
//     pathfinder API. RegisteredRouteHeads/RoutePairs are the observable seam.
//   - The wall-top dock query's trailing passability predicate (spec §5.4) is unported;
//     treated as "passable" so the query's two branches agree on their success value.
//   - The seconds->frames constant for the activation deadline is spec Q4 (unsettled for
//     retail). This port uses THIS engine's own logic-frame rate, which is the only rate our
//     sim has; if the oracle run settles a different retail constant it changes here.

using System;
using System.Collections.Generic;
using System.Numerics;
using OpenSage.Data.Ini;
using OpenSage.Mathematics;

namespace OpenSage.Logic.Object;

/// <summary>
/// Runtime module for <see cref="DynamicPortalBehaviorModuleData"/> (spec §6 items 1-4 and 6;
/// item 5, the wall-top anchor, is also implemented here because it is fully specified and
/// costs nothing - it is a pure query with no consumer in the fork yet).
/// </summary>
public sealed class DynamicPortalBehavior : UpgradeModule, ICreateModule
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>
    /// The waypoint-object array is a FIXED six-slot array in retail, not a vector (spec
    /// §4.1). A portal authored with more than six WayPoint lines overruns it there; here the
    /// extra waypoints are dropped and logged once, which is the safe reading of spec Q3
    /// ("no bound - the port must impose one").
    /// </summary>
    public const int WaypointSlotCount = 6;

    /// <summary>The waypoint helper object's template name (spec §7).</summary>
    public const string WaypointTemplateName = "#dynamicportal_wp";

    private readonly DynamicPortalBehaviorModuleData _moduleData;

    // ---- persisted state: exactly the snapshot contract of spec §4.3, in that order ----

    private readonly ObjectId[] _waypointObjects = new ObjectId[WaypointSlotCount];
    private bool _generated;
    private bool _disabled;

    // ---- rebuilt state: NOT snapshotted (spec §4.3 - waypoint positions and the chain links
    // are rebuilt from the id list plus the ModuleData, so they must not enter the walk) ----

    private readonly ObjectId[] _chainNext = new ObjectId[WaypointSlotCount];
    private readonly List<(ObjectId First, ObjectId Last)> _routePairs = new();
    private readonly List<ObjectId> _registeredRouteHeads = new();

    /// <summary>
    /// Absolute logic frame stamped onto each created waypoint object when
    /// <c>ActivationDelaySeconds &gt; 0</c> (spec §5.3 step 2, §4.2 offset +0xb4). Retail
    /// stamps an ABSOLUTE FRAME, not a countdown in seconds - a port that models this as
    /// seconds desyncs (spec Q4). Held on the module rather than on the helper object because
    /// the helper's class is unported (spec §9 item 3). Not persisted and currently read by
    /// nothing, so it cannot be a desync source until a consumer lands; when the helper class
    /// ports, this moves onto the helper and into that object's walk.
    /// </summary>
    public LogicFrame ActivationDeadline { get; private set; }

    private bool _loggedMissingWaypointTemplate;
    private bool _loggedWaypointOverflow;

    internal DynamicPortalBehavior(GameObject gameObject, IGameEngine gameEngine, DynamicPortalBehaviorModuleData moduleData)
        : base(gameObject, gameEngine, moduleData)
    {
        _moduleData = moduleData;
    }

    /// <summary>Whether Phase A has run and the helper chain exists (spec §4.1 offset 0x3c).</summary>
    public bool IsGenerated => _generated;

    /// <summary>Whether the portal has been deactivated (spec §4.1 offset 0x3d).</summary>
    public bool IsDisabled => _disabled;

    /// <summary>The six waypoint-object slots, in slot order; <see cref="ObjectId.Invalid"/> where empty.</summary>
    public IReadOnlyList<ObjectId> WaypointObjects => _waypointObjects;

    /// <summary>
    /// The route heads this module handed to the pathfinder, in link-row order - the
    /// observable stand-in for the unported registration call (spec §9 item 2).
    /// </summary>
    public IReadOnlyList<ObjectId> RegisteredRouteHeads => _registeredRouteHeads;

    /// <summary>
    /// The (first, last) waypoint-object pairs registered per link row (spec §5.3 Phase B,
    /// first bullet). The terminal hop of a route is carried by this pairing rather than by
    /// the <see cref="GetChainNext"/> chain - that asymmetry is deliberate and is spec Q1.
    /// </summary>
    public IReadOnlyList<(ObjectId First, ObjectId Last)> RoutePairs => _routePairs;

    /// <summary>The wall-top attack radius (spec §5.8); a straight ModuleData read in retail too.</summary>
    public float TopAttackRadius => _moduleData.TopAttackRadius;

    /// <summary>
    /// The next waypoint object in the chain from <paramref name="slot"/>, or
    /// <see cref="ObjectId.Invalid"/> when this slot is not a chain source.
    /// </summary>
    public ObjectId GetChainNext(int slot)
        => slot >= 0 && slot < WaypointSlotCount ? _chainNext[slot] : ObjectId.Invalid;

    // ---- activation / deactivation (spec §5.7) ----

    /// <summary>
    /// The module's own creation hook. <c>GenerateNow = Yes</c> short-circuits the upgrade
    /// wait and materialises the portal here instead (spec §1, §5.7); without it the gate
    /// declines and the portal waits for its triggering upgrade.
    /// </summary>
    void ICreateModule.OnCreate() => TryGeneratePortal();

    void ICreateModule.OnBuildComplete()
    {
        // No build-complete behavior: activation is upgrade-driven or GenerateNow (spec §5.7).
    }

    /// <summary>
    /// The generation gate (spec §5.7, "the generation gate"): the portal builds when
    /// <c>GenerateNow</c> is authored, OR when it has already been generated once - the
    /// latter being a refresh path that re-runs Phase B only. It never builds while the
    /// module is disabled.
    /// </summary>
    public void TryGeneratePortal()
    {
        if (_disabled)
        {
            return;
        }

        if (_moduleData.GenerateNow || _generated)
        {
            BuildPortal();
        }
    }

    /// <summary>
    /// The upgrade-completed hook (spec §5.7): the <c>WallBoundsMesh</c> footprint re-stamp
    /// runs BEFORE the build, then the portal builds. Ordering is load-bearing.
    /// </summary>
    protected override void OnUpgrade()
    {
        RestampWallBounds();
        BuildPortal();
    }

    /// <summary>
    /// The paired deactivate hook (spec §5.7, "the paired deactivate hook"): symmetric with
    /// <see cref="OnUpgrade"/> - the chain is torn down and the module latches disabled, so
    /// the generation gate refuses to rebuild it.
    /// </summary>
    public void Deactivate()
    {
        _disabled = true;
        TearDown();
    }

    /// <summary>
    /// Teardown (spec §5.7): the six helper objects are genuinely OWNED by the module and
    /// destroyed with it - they are not map-placed props. Clears the generated latch, so a
    /// later build re-runs Phase A.
    /// </summary>
    public void TearDown()
    {
        if (!_generated)
        {
            return;
        }

        for (var i = 0; i < WaypointSlotCount; i++)
        {
            var id = _waypointObjects[i];
            if (id.IsInvalid)
            {
                continue;
            }

            var waypointObject = GameEngine.GameLogic.GetObjectById(id);
            if (waypointObject != null)
            {
                GameEngine.GameLogic.DestroyObject(waypointObject);
            }

            _waypointObjects[i] = ObjectId.Invalid;
        }

        ClearChainState();
        _generated = false;
    }

    // ---- the graph builder (spec §5.3) ----

    /// <summary>
    /// Build/refresh the portal. Phase A (materialise the helper objects) runs at most once,
    /// gated on the generated latch; Phase B (register route heads, pair the endpoints, chain
    /// consecutive hops) runs on EVERY call, but the pairing and the chaining halves only on
    /// the first generation - matching spec §5.3 exactly.
    /// </summary>
    public void BuildPortal()
    {
        var firstGeneration = !_generated;

        if (firstGeneration)
        {
            MaterialiseWaypoints();
            _generated = true;
        }

        ChainRoutes(firstGeneration);
    }

    private void MaterialiseWaypoints()
    {
        // Step 2 of spec §5.3: the activation delay is resolved to an ABSOLUTE frame once,
        // up front, and stamped onto every created object - it is not a per-object countdown.
        ActivationDeadline = _moduleData.ActivationDelaySeconds > 0.0f
            ? GameEngine.GameLogic.CurrentFrame
              + LogicFrameSpan.FromSeconds(_moduleData.ActivationDelaySeconds, GameEngine.LogicFramesPerSecond)
            : LogicFrame.Zero;

        var template = GameEngine.AssetStore.ObjectDefinitions.GetByName(WaypointTemplateName);

        var waypoints = _moduleData.WayPoints;
        var count = waypoints.Count;

        if (count > WaypointSlotCount)
        {
            if (!_loggedWaypointOverflow)
            {
                _loggedWaypointOverflow = true;
                Logger.Warn(
                    $"DynamicPortalBehaviour on '{GameObject.Definition.Name}' authors {count} WayPoint lines; " +
                    $"only the first {WaypointSlotCount} are used (fixed slot array).");
            }

            count = WaypointSlotCount;
        }

        for (var slot = 0; slot < count; slot++)
        {
            var localPosition = GetBoneLocalPosition(waypoints[slot].Index);

            // Null-tolerant exactly like SpawnBehavior's missing-template path: an install
            // without the engine-internal waypoint template still latches _generated and
            // still computes the whole topology, it just has no helper objects to hang it on.
            if (template == null)
            {
                if (!_loggedMissingWaypointTemplate)
                {
                    _loggedMissingWaypointTemplate = true;
                    Logger.Warn(
                        $"DynamicPortalBehaviour on '{GameObject.Definition.Name}' could not resolve the waypoint " +
                        $"template '{WaypointTemplateName}'; the portal graph is computed but no helper objects exist.");
                }

                _waypointObjects[slot] = ObjectId.Invalid;
                continue;
            }

            var waypointObject = GameEngine.GameLogic.CreateObject(template, GameObject.Owner);
            if (waypointObject == null)
            {
                _waypointObjects[slot] = ObjectId.Invalid;
                continue;
            }

            waypointObject.CreatedByObjectID = GameObject.Id;
            waypointObject.SetTranslation(GameObject.ToWorldspace(localPosition));
            _waypointObjects[slot] = waypointObject.Id;
        }
    }

    private void ChainRoutes(bool firstGeneration)
    {
        // The route-head registration is what re-runs on a refresh call, so the head list is
        // "the heads handed over by the most recent call" and is rebuilt every time. The pairs
        // and the chain are registered ONCE, at first generation, and survive refreshes -
        // they are cleared only by teardown (spec §5.3 Phase B, §5.7).
        _registeredRouteHeads.Clear();

        if (firstGeneration)
        {
            Array.Clear(_chainNext, 0, _chainNext.Length);
            _routePairs.Clear();
        }

        foreach (var link in _moduleData.Links)
        {
            var route = link.Route;
            var n = route.Count;
            if (n == 0)
            {
                continue;
            }

            var head = SlotObject(route[0]);

            // Every call: the route head goes to the pathfinder.
            _registeredRouteHeads.Add(head);

            if (!firstGeneration)
            {
                continue;
            }

            // First generation only: pair the route's first and last waypoint objects...
            _routePairs.Add((head, SlotObject(route[n - 1])));

            // ...and chain consecutive hops, stopping one short. The n-2 bound is
            // instruction-level verified in the spec (§5.3 Phase B) and is NOT a decompiler
            // artefact: for `From:0 Via:4 Via:5 To:3` the chain built is 0 -> 4 -> 5 and the
            // `To` waypoint 3 is never made the target of a chain hop; the terminal hop is
            // represented by the pair registration above. See spec Q1 - if the oracle run
            // refutes that reading, this bound is the single line that changes.
            foreach (var (from, to) in GetChainHops(route))
            {
                if (from >= 0 && from < WaypointSlotCount)
                {
                    _chainNext[from] = SlotObject(to);
                }
            }
        }
    }

    private void ClearChainState()
    {
        Array.Clear(_chainNext, 0, _chainNext.Length);
        _routePairs.Clear();
        _registeredRouteHeads.Clear();
    }

    private ObjectId SlotObject(int slot)
        => slot >= 0 && slot < WaypointSlotCount ? _waypointObjects[slot] : ObjectId.Invalid;

    /// <summary>
    /// The consecutive chain hops of one link row: pairs <c>(route[i], route[i+1])</c> for
    /// <c>i &lt; n - 2</c> (spec §5.3 Phase B). Pure function of the authored row, exposed so
    /// the topology is testable without a resolvable waypoint template.
    /// </summary>
    public static IEnumerable<(int From, int To)> GetChainHops(IReadOnlyList<int> route)
    {
        if (route == null)
        {
            yield break;
        }

        for (var i = 0; i < route.Count - 2; i++)
        {
            yield return (route[i], route[i + 1]);
        }
    }

    // ---- the wall-top dock query (spec §5.4) ----

    /// <summary>
    /// The <c>AboveWall</c> dock query. <c>AboveWall</c> is an index into the WAYPOINT LIST -
    /// not a height, not a wall-segment index, not a boolean-as-int (spec §5.4, §8 Q6) - and
    /// -1 is a live sentinel meaning "this portal has no wall-top dock", which is also the
    /// authored default. Out-of-range and sentinel both fall back to the owning object's own
    /// position, which is the branch every AotR postern gate takes.
    /// </summary>
    public bool TryGetDockPosition(out Vector3 position)
    {
        var aboveWall = _moduleData.AboveWall;

        if (aboveWall < 0 || _moduleData.WayPoints.Count <= aboveWall)
        {
            position = GameObject.Translation;
            return true;
        }

        var boneIndex = _moduleData.WayPoints[aboveWall].Index;
        position = GameObject.ToWorldspace(GetBoneLocalPosition(boneIndex));

        // Retail closes with a passability predicate on the resulting point (spec §5.4). That
        // predicate is unported (TODO-spec above); treated as passable so both branches agree.
        return true;
    }

    // ---- the wall-top attack anchor (spec §5.8) ----

    /// <summary>
    /// The wall-top firing position: the object's forward vector is normalised by its LARGER
    /// horizontal component (not by its length), flattened, and its length then scales
    /// <c>TopAttackPos</c>'s X and Y but NOT its Z (spec §5.8 step 4). False when the object
    /// has no usable horizontal facing.
    /// </summary>
    public bool TryGetTopAttackPosition(out Vector3 position)
    {
        var forward = GameObject.ToWorldspace(new Vector3(1.0f, 0.0f, 0.0f)) - GameObject.Translation;

        var scale = MathF.Max(MathF.Abs(forward.X), MathF.Abs(forward.Y));
        if (scale <= 0.0f)
        {
            position = Vector3.Zero;
            return false;
        }

        forward /= scale;
        forward.Z = 0.0f;

        var length = forward.Length();
        var topAttackPos = _moduleData.TopAttackPos;

        position = GameObject.ToWorldspace(
            new Vector3(topAttackPos.X * length, topAttackPos.Y * length, topAttackPos.Z));

        return true;
    }

    // ---- helpers ----

    /// <summary>
    /// The bone-position lookup (spec §5.3 step 1, §5.4). Bones run
    /// <c>{BonePrefix}01..{BonePrefix}{NumberOfBones}</c> and the waypoint's Index is
    /// zero-based, so bone index i is the 1-based name i+1 - the same two-digit convention
    /// the rest of the fork uses for indexed bones. Falls back to the owning object's origin
    /// when there is no drawable or no such bone (headless hosts have neither), which keeps
    /// the whole graph well-defined rather than throwing mid-build.
    /// </summary>
    private Vector3 GetBoneLocalPosition(int boneIndex)
    {
        if (boneIndex < 0 || string.IsNullOrEmpty(_moduleData.BonePrefix))
        {
            return Vector3.Zero;
        }

        var drawable = GameObject.Drawable;
        if (drawable == null)
        {
            return Vector3.Zero;
        }

        var (modelInstance, bone) = drawable.FindBone(_moduleData.BonePrefix + (boneIndex + 1).ToString("D2"));
        if (modelInstance == null || bone == null)
        {
            return Vector3.Zero;
        }

        return modelInstance.AbsoluteBoneTransforms[bone.Index].Translation;
    }

    /// <summary>
    /// The <c>WallBoundsMesh</c> footprint re-stamp (spec §5.7 step 1). This is why
    /// <c>WallBoundsMesh</c> is sim-affecting rather than cosmetic: when the portal opens,
    /// the owning object's pathfind footprint is re-stamped from the named bounds mesh. The
    /// remove/re-add pair it drives is against the pathfinder subsystem, which is unported
    /// (spec §9 item 2), so this records the request and does not invent the API. No shipped
    /// AotR instance authors the field, so nothing observable is lost today.
    /// </summary>
    private void RestampWallBounds()
    {
        if (string.IsNullOrEmpty(_moduleData.WallBoundsMesh))
        {
            return;
        }

        WallBoundsRestampCount++;
    }

    /// <summary>
    /// How many times <see cref="RestampWallBounds"/> has fired - the observable seam for the
    /// unported footprint re-stamp. Rebuilt state, not snapshotted.
    /// </summary>
    public int WallBoundsRestampCount { get; private set; }

    internal override void Load(StatePersister reader)
    {
        // Spec §4.3: version marker, then SIX object ids, then the generated byte, then -
        // only when the version is greater than 1 - the disabled byte. The waypoint positions
        // and the chain links are deliberately absent: they are rebuilt from the id list plus
        // the ModuleData.
        var version = reader.PersistVersion(2);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();

        for (var i = 0; i < WaypointSlotCount; i++)
        {
            reader.PersistObjectId(ref _waypointObjects[i], $"WaypointObject{i}");
        }

        reader.PersistBoolean(ref _generated);

        if (version > 1)
        {
            reader.PersistBoolean(ref _disabled);
        }
    }
}

[AddedIn(SageGame.Bfme)]
public class DynamicPortalBehaviorModuleData : UpgradeModuleData
{
    internal static DynamicPortalBehaviorModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    // TriggeredBy / ConflictsWith / CustomAnimAndDuration / RequiresAllTriggers /
    // RequiresAllConflictingTriggers / Permanent all come from the shared upgrade-mux field
    // block (UpgradeModuleData.UpgradeData) rather than a private copy on this class
    // (spec-dynamic-portal.md §3.1, §6).
    private static new readonly IniParseTable<DynamicPortalBehaviorModuleData> FieldParseTable = UpgradeModuleData.FieldParseTable
        .Concat(new IniParseTable<DynamicPortalBehaviorModuleData>
        {
            { "GenerateNow", (parser, x) => x.GenerateNow = parser.ParseBoolean() },
            { "AllowKindOf", (parser, x) => x.AllowKindOf = parser.ParseEnumBitArray<ObjectKinds>() },
            { "RejectKindOf", (parser, x) => x.RejectKindOf = parser.ParseEnumBitArray<ObjectKinds>() },
            { "AllowEnemies", (parser, x) => x.AllowEnemies = parser.ParseBoolean() },
            { "BonePrefix", (parser, x) => x.BonePrefix = parser.ParseString() },
            { "NumberOfBones", (parser, x) => x.NumberOfBones = parser.ParseInteger() },
            { "WayPoint", (parser, x) => x.WayPoints.Add(DynamicPortalWayPoint.Parse(parser)) },
            { "Link", (parser, x) => x.Links.Add(DynamicPortalLink.Parse(parser)) },
            { "WallBoundsMesh", (parser, x) => x.WallBoundsMesh = parser.ParseString() },
            { "ActivationDelaySeconds", (parser, x) => x.ActivationDelaySeconds = parser.ParseFloat() },
            { "AboveWall", (parser, x) => x.AboveWall = parser.ParseInteger() },
            { "TopAttackPos", (parser, x) => x.TopAttackPos = parser.ParseVector3() },
            { "TopAttackRadius", (parser, x) => x.TopAttackRadius = parser.ParseFloat() },
            { "ObjectFilter", (parser, x) => x.ObjectFilter = ObjectFilter.Parse(parser) }
        });

    public bool GenerateNow { get; private set; }

    /// <summary>
    /// BFME1-era field: this build's retail parse table carries <see cref="ObjectFilter"/> in
    /// its place and has no row for it (spec §3.1, §3.2 gap 6). Kept for tolerance; nothing
    /// in the runtime module reads it.
    /// </summary>
    [AddedIn(SageGame.Bfme)]
    public BitArray<ObjectKinds> AllowKindOf { get; private set; }

    /// <summary>BFME1-era field; see <see cref="AllowKindOf"/>.</summary>
    [AddedIn(SageGame.Bfme)]
    public BitArray<ObjectKinds> RejectKindOf { get; private set; }

    /// <summary>
    /// Copied verbatim onto each created waypoint object; the module itself evaluates no
    /// relationship condition and contains no breach/capture logic (spec §8 Q8).
    /// </summary>
    public bool AllowEnemies { get; private set; }

    public string BonePrefix { get; private set; }
    public int NumberOfBones { get; private set; }

    /// <summary>
    /// The ordered waypoint sequence. Each entry's <c>Index</c> is a BONE index; the entry's
    /// POSITION in this list is the waypoint number that <see cref="Links"/> and
    /// <see cref="AboveWall"/> refer to. Three separate index spaces (spec §2.1).
    /// </summary>
    public List<DynamicPortalWayPoint> WayPoints { get; private set; } = new List<DynamicPortalWayPoint>();

    public List<DynamicPortalLink> Links { get; private set; } = new List<DynamicPortalLink>();

    /// <summary>
    /// Wall/pathfind footprint mesh name re-stamped onto the owning object when the portal's
    /// upgrade completes. Absent from every shipped AotR instance but load-bearing at runtime
    /// (spec-dynamic-portal.md §3.1 row 5, §5.7).
    /// </summary>
    public string WallBoundsMesh { get; private set; } = string.Empty;

    public float ActivationDelaySeconds { get; private set; }

    /// <summary>
    /// Index into <see cref="WayPoints"/> naming the "dock" waypoint used for the wall-top
    /// attack anchor query. Retail default is -1, a live sentinel meaning "this portal has no
    /// wall-top dock" (spec-dynamic-portal.md §3.2 gap 3, §3.3, §5.4) - not 0, which would
    /// silently pick waypoint 0 as the dock point on every portal that omits this field.
    /// </summary>
    public int AboveWall { get; private set; } = -1;

    public Vector3 TopAttackPos { get; private set; }

    /// <summary>Retail parses this as a float (default 5.0f), not an int (spec-dynamic-portal.md §3.1 row 9, §3.2 gap 2/4, §3.3).</summary>
    public float TopAttackRadius { get; private set; } = 5.0f;

    [AddedIn(SageGame.Bfme2)]
    public ObjectFilter ObjectFilter { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
        => new DynamicPortalBehavior(gameObject, gameEngine, this);
}

/// <summary>
/// Movement type of one waypoint entry. NOTE (spec §3.4): retail's numeric encoding is
/// <c>Walk = 2, Climb = 3, PreClimb = 4</c>, a different numbering AND ordering from the
/// names below. The fork's numbering is kept deliberately: no routine inside the module reads
/// the type half of a waypoint entry (spec §8 Q5), so no serialization or CRC path observes
/// the raw value. Renumber only if one ever does.
/// </summary>
public enum DynamicPortalWayPointType
{
    None = 0,

    [IniEnum("PreClimb")]
    PreClimb,

    [IniEnum("Climb")]
    Climb,

    [IniEnum("Walk")]
    Walk
}

public sealed class DynamicPortalWayPoint
{
    internal static DynamicPortalWayPoint Parse(IniParser parser)
    {
        return new DynamicPortalWayPoint()
        {
            Index = parser.ParseAttributeInteger("Index"),
            Type = parser.ParseAttributeEnum<DynamicPortalWayPointType>("Type")
        };
    }

    /// <summary>A BONE index, not a waypoint number (spec §2.1).</summary>
    public int Index { get; private set; }

    public DynamicPortalWayPointType Type { get; private set; }
}

/// <summary>
/// One authored <c>Link</c> row. Retail stores and consumes the row as a FLAT, variable-length
/// int vector in authored order - <c>[From, Via1 ... ViaN, To]</c> - and the chaining loop
/// walks consecutive pairs of it (spec §5.6, §6). The former {From, Vias, To} shape happened
/// to round-trip the same data, but any consumer written against it reproduces the wrong
/// iteration bound, so the flat vector is the shape.
/// </summary>
public sealed class DynamicPortalLink
{
    internal static DynamicPortalLink Parse(IniParser parser) => parser.ParseAttributeList(FieldParseTable);

    // Each handler APPENDS, and ParseAttributeList walks the row's attributes strictly left to
    // right, so _route ends up in authored order without a bespoke parser. Retail's final read
    // is purely positional (it does not check that the last attribute is spelled "To"); this
    // table is stricter, which is a tolerance difference only.
    internal static readonly IniParseTable<DynamicPortalLink> FieldParseTable = new IniParseTable<DynamicPortalLink>
    {
        { "From", (parser, x) => x._route.Add(parser.ParseInteger()) },
        { "Via", (parser, x) => x._route.Add(parser.ParseInteger()) },
        { "To", (parser, x) => x._route.Add(parser.ParseInteger()) }
    };

    private readonly List<int> _route = new();

    /// <summary>The waypoint numbers this route visits, in authored order: From, Vias, To.</summary>
    public IReadOnlyList<int> Route => _route;

    /// <summary>The route's first waypoint number, or -1 for an empty row.</summary>
    public int From => _route.Count > 0 ? _route[0] : -1;

    /// <summary>The route's last waypoint number, or -1 for an empty row.</summary>
    public int To => _route.Count > 0 ? _route[_route.Count - 1] : -1;

    /// <summary>The intermediate waypoint numbers between <see cref="From"/> and <see cref="To"/>.</summary>
    public IEnumerable<int> Vias
    {
        get
        {
            for (var i = 1; i < _route.Count - 1; i++)
            {
                yield return _route[i];
            }
        }
    }
}
