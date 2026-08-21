using System.Collections.Generic;
using System.Numerics;
using OpenSage.Data.Ini;
using OpenSage.Mathematics;

namespace OpenSage.Logic.Object;

// SPELLING IS LOAD-BEARING: the retail/INI keyword is the British "DynamicPortalBehaviour"
// (registered against that spelling in BehaviorModule.cs); this class keeps the American
// spelling per the fork's own naming convention. Do not "fix" the mismatch (spec-dynamic-portal.md §0).
[AddedIn(SageGame.Bfme)]
[ParseOnly("Round-4 backlog; census: Behavior")]
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
            { "WayPoint", (parser, x) => x.WayPoints.Add(WayPoint.Parse(parser)) },
            { "Link", (parser, x) => x.Links.Add(Link.Parse(parser)) },
            { "WallBoundsMesh", (parser, x) => x.WallBoundsMesh = parser.ParseString() },
            { "ActivationDelaySeconds", (parser, x) => x.ActivationDelaySeconds = parser.ParseFloat() },
            { "AboveWall", (parser, x) => x.AboveWall = parser.ParseInteger() },
            { "TopAttackPos", (parser, x) => x.TopAttackPos = parser.ParseVector3() },
            { "TopAttackRadius", (parser, x) => x.TopAttackRadius = parser.ParseFloat() },
            { "ObjectFilter", (parser, x) => x.ObjectFilter = ObjectFilter.Parse(parser) }
        });

    public bool GenerateNow { get; private set; }
    public BitArray<ObjectKinds> AllowKindOf { get; private set; }
    public BitArray<ObjectKinds> RejectKindOf { get; private set; }
    public bool AllowEnemies { get; private set; }
    public string BonePrefix { get; private set; }
    public int NumberOfBones { get; private set; }
    public List<WayPoint> WayPoints { get; private set; } = new List<WayPoint>();
    public List<Link> Links { get; private set; } = new List<Link>();

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
}

public enum WayPointType
{
    None = 0,

    [IniEnum("PreClimb")]
    PreClimb,

    [IniEnum("Climb")]
    Climb,

    [IniEnum("Walk")]
    Walk
}

public sealed class WayPoint
{
    internal static WayPoint Parse(IniParser parser)
    {
        return new WayPoint()
        {
            Index = parser.ParseAttributeInteger("Index"),
            Type = parser.ParseAttributeEnum<WayPointType>("Type")
        };
    }

    public int Index { get; private set; }
    public WayPointType Type { get; private set; }
}

public sealed class Link
{
    internal static Link Parse(IniParser parser) => parser.ParseAttributeList(FieldParseTable);

    internal static readonly IniParseTable<Link> FieldParseTable = new IniParseTable<Link>
    {
        { "From", (parser, x) => x.From = parser.ParseInteger() },
        { "Via", (parser, x) => x.Vias.Add(parser.ParseInteger()) },
        { "To", (parser, x) => x.To = parser.ParseInteger() }
    };

    public int From { get; private set; }
    public List<int> Vias { get; } = new List<int>();
    public int To { get; private set; }
}
