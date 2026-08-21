// FloodUpdate - the S? swarm-spawn runtime, implemented FRESH: BFME2-only module (no
// generals-gpl sibling: GeneralsMD/Generals ship no FloodUpdate.cpp) and no clean-room
// spec exists yet in bfme2-workbench/research/ (gplRef unset on the task packet). Ported
// from the design summary only (rush-ability swarms - wolf packs, mounted charges): at
// spawn, one member object is created per configured FloodMember entry, standing on its
// own cubic Bezier curve (the four ControlPointOffset* fields, spawner-relative, rotated
// once by the flood's flow angle); every logic frame each live member advances along its
// curve by MemberSpeed distance units (arc-length reparametrized, not raw-t stepped, so
// speed is actually constant along the curve) and is despawned on reaching the end.
//
// AngleOfFlow / DirectionIsRelative apply ONCE, at spawn (design summary: "applied at
// spawn time"): DirectionIsRelative adds the spawner's own facing to AngleOfFlow before
// rotating every control point offset. The facing is pulled straight off the spawner's own
// GameObject transform (FloodTransformBridge.PullYaw) rather than through a
// SimLocomotorUpdate: unlike SimHordeContain's HordeMover (which reads a LIVE steering
// state every frame), this is a one-time spawn-time read, and going through a locomotor
// would make the value depend on whether that module's own Update already ran earlier in
// the SAME frame (module order is not a frozen contract) - reading the transform directly
// is correct from frame 0 whether or not the spawner has a locomotor at all.
//
// Members are driven directly (FloodTransformBridge), not through their own
// SimLocomotorUpdate: see finding FLOOD-F1 in FloodTransformBridge.cs. Spawner-death
// mid-flood (OnDestroy) snaps every still-active member straight to its curve endpoint and
// despawns it, rather than leaving it frozen forever with nothing left to drive it -
// documented as finding FLOOD-F2 below (no spec to confirm the retail shape).

using System.Collections.Generic;
using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class FloodUpdate : UpdateModule
{
    private readonly FloodUpdateModuleData _data;

    /// <summary>One spawned swarm member's curve-follow state (our shape).</summary>
    internal struct FloodMemberState
    {
        public ObjectId MemberId;
        public FixVector3 P0;
        public FixVector3 P1;
        public FixVector3 P2;
        public FixVector3 P3;
        public Fix64 Speed;
        public Fix64 DistanceTraveled;
        public Fix64 TotalLength;
        public bool Done;
    }

    /// <summary>
    /// Fixed sample count for the deterministic arc-length approximation (F2). Spawn-time
    /// TotalLength and every per-frame FindTForLength bisection MUST resample the curve at
    /// the same segment count: a coarser bisection polyline has a strictly shorter t=1
    /// length than a finer spawn-time one for any genuinely curved (non-collinear) Bezier
    /// (R13 review finding), so the bisection would never be able to reach a targetLength
    /// approaching the stored TotalLength and would incorrectly converge on t~1 (member
    /// snapped to the curve endpoint) for one or more frames before DistanceTraveled
    /// actually crosses TotalLength - a visible freeze-then-despawn glitch.
    /// </summary>
    private const int ArcSegments = 32;
    private const int BisectIterations = 20;

    private static readonly Fix64 Three = new Fix64(3);

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----
    private bool _initialized;
    private readonly List<FloodMemberState> _members = new();

    public FloodUpdate(GameObject gameObject, ISimContext context, FloodUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;
        SetWakeFrame(UpdateSleepTime.None);
    }

    // ---- public surface (read by tests) ----

    public int MemberCount => _members.Count;

    public int ActiveMemberCount
    {
        get
        {
            var count = 0;
            foreach (var member in _members)
            {
                if (!member.Done)
                {
                    count++;
                }
            }
            return count;
        }
    }

    internal FloodMemberState GetMember(int index) => _members[index];

    // ---- per-frame ----

    public override UpdateSleepTime Update()
    {
        EnsureInitialized();

        var anyActive = false;
        for (var i = 0; i < _members.Count; i++)
        {
            var member = _members[i];
            if (member.Done)
            {
                continue;
            }

            var memberObject = Context.GameLogic.GetObjectById(member.MemberId);
            if (memberObject == null || memberObject.IsDestroyed)
            {
                member.Done = true;
                _members[i] = member;
                continue;
            }

            AdvanceMember(memberObject, ref member);
            _members[i] = member;
            anyActive |= !member.Done;
        }

        return (_initialized && !anyActive) ? UpdateSleepTime.Forever : UpdateSleepTime.None;
    }

    /// <summary>
    /// Spawner-death cleanup (finding FLOOD-F2): once this module stops being ticked
    /// (its GameObject is gone) nothing is left to drive still-travelling members, so on
    /// the way out every active member is snapped straight to its curve endpoint and
    /// despawned rather than left standing forever mid-flight.
    /// </summary>
    protected internal override void OnDestroy()
    {
        for (var i = 0; i < _members.Count; i++)
        {
            var member = _members[i];
            if (member.Done)
            {
                continue;
            }

            var memberObject = Context.GameLogic.GetObjectById(member.MemberId);
            if (memberObject != null && !memberObject.IsDestroyed)
            {
                FloodTransformBridge.Push(memberObject, member.P3, FacingAt(member, Fix64.One));
                Context.GameLogic.DestroyObject(memberObject);
            }

            member.Done = true;
            _members[i] = member;
        }
    }

    // ---- internals ----

    private void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }
        _initialized = true;

        var totalAngle = _data.AngleOfFlow;
        if (_data.DirectionIsRelative)
        {
            totalAngle += FloodTransformBridge.PullYaw(GameObject);
        }
        var cos = FixTrig.Cos(totalAngle);
        var sin = FixTrig.Sin(totalAngle);

        // Pulled once (F4 wire boundary): every curve point below is stored WORLD-space so
        // AdvanceMember's per-frame FloodTransformBridge.Push can write it straight to the
        // member's transform without a second spawner-position crossing every frame.
        var spawnerPos = FloodTransformBridge.PullPosition(GameObject);

        foreach (var memberSpec in _data.FloodMembers)
        {
            var definition = Context.Assets.GetObjectDefinition(memberSpec.MemberTemplateName);
            if (definition == null)
            {
                continue;
            }

            var offset0 = Rotate(memberSpec.ControlPointOffsetOne, cos, sin);
            var p0 = spawnerPos + offset0;
            var p1 = spawnerPos + Rotate(memberSpec.ControlPointOffsetTwo, cos, sin);
            var p2 = spawnerPos + Rotate(memberSpec.ControlPointOffsetThree, cos, sin);
            var p3 = spawnerPos + Rotate(memberSpec.ControlPointOffsetFour, cos, sin);

            var orientation = InitialOrientation(p0, p1, p2, p3, totalAngle);
            var created = Context.GameLogic.CreateObjectAt(definition, GameObject.Owner, GameObject, offset0, orientation);
            if (created == null)
            {
                continue;
            }

            var totalLength = ArcLength(p0, p1, p2, p3, Fix64.One, ArcSegments);
            _members.Add(new FloodMemberState
            {
                MemberId = created.Id,
                P0 = p0,
                P1 = p1,
                P2 = p2,
                P3 = p3,
                Speed = memberSpec.MemberSpeed,
                DistanceTraveled = Fix64.Zero,
                TotalLength = totalLength,
                Done = totalLength == Fix64.Zero,
            });
        }
    }

    /// <summary>
    /// Spawner-relative offset -> world offset: rotated about Z by the flow angle; Z carries
    /// through untouched (the flow rotation is a ground-plane turn, offsets already encode
    /// their own elevation/depression).
    /// </summary>
    private static FixVector3 Rotate(in FixVector3 offset, Fix64 cos, Fix64 sin)
    {
        return new FixVector3(
            offset.X * cos - offset.Y * sin,
            offset.X * sin + offset.Y * cos,
            offset.Z);
    }

    private static Fix64 InitialOrientation(
        in FixVector3 p0, in FixVector3 p1, in FixVector3 p2, in FixVector3 p3, Fix64 fallbackAngle)
    {
        var tangent = Tangent(p0, p1, p2, p3, Fix64.Zero);
        if (tangent.X == Fix64.Zero && tangent.Y == Fix64.Zero)
        {
            return fallbackAngle;
        }
        return FixTrig.Atan2(tangent.Y, tangent.X);
    }

    private void AdvanceMember(GameObject memberObject, ref FloodMemberState member)
    {
        var target = member.DistanceTraveled + member.Speed;
        if (target >= member.TotalLength)
        {
            target = member.TotalLength;
        }
        member.DistanceTraveled = target;

        var t = FindTForLength(member.P0, member.P1, member.P2, member.P3, target, member.TotalLength);
        var position = BezierPoint(member.P0, member.P1, member.P2, member.P3, t);
        var yaw = FacingAt(member, t);
        FloodTransformBridge.Push(memberObject, position, yaw);

        if (target >= member.TotalLength)
        {
            member.Done = true;
            Context.GameLogic.DestroyObject(memberObject);
        }
    }

    private static Fix64 FacingAt(in FloodMemberState member, Fix64 t)
    {
        var tangent = Tangent(member.P0, member.P1, member.P2, member.P3, t);
        if (tangent.X == Fix64.Zero && tangent.Y == Fix64.Zero)
        {
            return Fix64.Zero;
        }
        return FixTrig.Atan2(tangent.Y, tangent.X);
    }

    /// <summary>Cubic Bezier position at parameter t in [0,1] (standard Bernstein form).</summary>
    private static FixVector3 BezierPoint(
        in FixVector3 p0, in FixVector3 p1, in FixVector3 p2, in FixVector3 p3, Fix64 t)
    {
        var u = Fix64.One - t;
        var uu = u * u;
        var uuu = uu * u;
        var tt = t * t;
        var ttt = tt * t;

        var a = uuu;
        var b = Three * uu * t;
        var c = Three * u * tt;
        var d = ttt;

        return new FixVector3(
            a * p0.X + b * p1.X + c * p2.X + d * p3.X,
            a * p0.Y + b * p1.Y + c * p2.Y + d * p3.Y,
            a * p0.Z + b * p1.Z + c * p2.Z + d * p3.Z);
    }

    /// <summary>Cubic Bezier derivative (unnormalized tangent) at parameter t in [0,1].</summary>
    private static FixVector3 Tangent(
        in FixVector3 p0, in FixVector3 p1, in FixVector3 p2, in FixVector3 p3, Fix64 t)
    {
        var u = Fix64.One - t;
        var uu = u * u;
        var tt = t * t;

        var a = Three * uu;
        var b = Fix64.FromDecimalLiteral("6") * u * t;
        var c = Three * tt;

        return new FixVector3(
            a * (p1.X - p0.X) + b * (p2.X - p1.X) + c * (p3.X - p2.X),
            a * (p1.Y - p0.Y) + b * (p2.Y - p1.Y) + c * (p3.Y - p2.Y),
            a * (p1.Z - p0.Z) + b * (p2.Z - p1.Z) + c * (p3.Z - p2.Z));
    }

    /// <summary>
    /// Poly-line arc length of the curve from t=0 to <paramref name="t"/>, sampled at
    /// <paramref name="segments"/> evenly spaced steps. Deterministic (fixed segment
    /// count, Fix64 math throughout) - the same inputs always sample the same points.
    /// </summary>
    private static Fix64 ArcLength(
        in FixVector3 p0, in FixVector3 p1, in FixVector3 p2, in FixVector3 p3, Fix64 t, int segments)
    {
        var length = Fix64.Zero;
        var prev = p0;
        var segmentCount = new Fix64(segments);
        for (var i = 1; i <= segments; i++)
        {
            var s = t * new Fix64(i) / segmentCount;
            var cur = BezierPoint(p0, p1, p2, p3, s);
            length += (cur - prev).Length();
            prev = cur;
        }
        return length;
    }

    /// <summary>
    /// Arc-length reparametrization: finds t such that the poly-line arc length from 0 to t
    /// equals <paramref name="targetLength"/>, by fixed-iteration-count bisection (ArcSegments
    /// x BisectIterations sample points every call - determinism over a stored table, R2's
    /// wide-length-squared rule already keeps each sample cheap). Must resample at the same
    /// ArcSegments count used to compute TotalLength at spawn (see ArcSegments doc comment):
    /// otherwise a coarser bisection polyline can never reach a targetLength approaching the
    /// finer TotalLength and incorrectly converges on t~1 early.
    /// </summary>
    private static Fix64 FindTForLength(
        in FixVector3 p0, in FixVector3 p1, in FixVector3 p2, in FixVector3 p3,
        Fix64 targetLength, Fix64 totalLength)
    {
        if (targetLength <= Fix64.Zero)
        {
            return Fix64.Zero;
        }
        if (targetLength >= totalLength)
        {
            return Fix64.One;
        }

        var lo = Fix64.Zero;
        var hi = Fix64.One;
        for (var i = 0; i < BisectIterations; i++)
        {
            var mid = (lo + hi) * Fix64.Half;
            var len = ArcLength(p0, p1, p2, p3, mid, ArcSegments);
            if (len < targetLength)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }
        return (lo + hi) * Fix64.Half;
    }

    // ---- the single walk (F8 Objects channel; field order = declaration order, F9) ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferBool("Initialized", ref _initialized);
        xfer.XferList("Members", _members, XferMember);
    }

    private static void XferMember(IXfer xfer, ref FloodMemberState member)
    {
        xfer.XferObjectId("MemberId", ref member.MemberId);
        xfer.XferFixVector3("P0", ref member.P0, Tolerance.Band);
        xfer.XferFixVector3("P1", ref member.P1, Tolerance.Band);
        xfer.XferFixVector3("P2", ref member.P2, Tolerance.Band);
        xfer.XferFixVector3("P3", ref member.P3, Tolerance.Band);
        xfer.XferFix64("Speed", ref member.Speed);
        xfer.XferFix64("DistanceTraveled", ref member.DistanceTraveled, Tolerance.Band);
        xfer.XferFix64("TotalLength", ref member.TotalLength, Tolerance.Band);
        xfer.XferBool("Done", ref member.Done);
    }
}

[AddedIn(SageGame.Bfme2)]
[SimDataAudited]
public sealed class FloodUpdateModuleData : UpdateModuleData
{
    internal static FloodUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<FloodUpdateModuleData> FieldParseTable = new IniParseTable<FloodUpdateModuleData>
    {
        { "AngleOfFlow", (parser, x) => x.AngleOfFlow = parser.ParseAngleDegrees() },
        { "DirectionIsRelative", (parser, x) => x.DirectionIsRelative = parser.ParseBoolean() },
        { "FloodMember", (parser, x) => x.FloodMembers.Add(FloodMember.Parse(parser)) }
    };

    public Fix64 AngleOfFlow { get; private set; }
    public bool DirectionIsRelative { get; private set; }
    public List<FloodMember> FloodMembers { get; } = new List<FloodMember>();

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new FloodUpdate(gameObject, gameEngine.SimContext, this);
    }
}

public sealed class FloodMember
{
    internal static FloodMember Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<FloodMember> FieldParseTable = new IniParseTable<FloodMember>
    {
        { "MemberTemplateName", (parser, x) => x.MemberTemplateName = parser.ParseAssetReference() },
        { "ControlPointOffsetOne", (parser, x) => x.ControlPointOffsetOne = parser.ParseFixVector3() },
        { "ControlPointOffsetTwo", (parser, x) => x.ControlPointOffsetTwo = parser.ParseFixVector3() },
        { "ControlPointOffsetThree", (parser, x) => x.ControlPointOffsetThree = parser.ParseFixVector3() },
        { "ControlPointOffsetFour", (parser, x) => x.ControlPointOffsetFour = parser.ParseFixVector3() },
        { "MemberSpeed", (parser, x) => x.MemberSpeed = parser.ParseFix64() }
    };

    public string MemberTemplateName { get; private set; }
    public FixVector3 ControlPointOffsetOne { get; private set; }
    public FixVector3 ControlPointOffsetTwo { get; private set; }
    public FixVector3 ControlPointOffsetThree { get; private set; }
    public FixVector3 ControlPointOffsetFour { get; private set; }
    public Fix64 MemberSpeed { get; private set; }
}
