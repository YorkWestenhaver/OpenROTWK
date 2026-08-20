// PartTheHeavensUpdate - R12 port. No generals-gpl sibling and no clean-room spec in
// bfme2-workbench/research/. The retail module's entire output is an animated ring/circle
// visual effect (texture, RGBA tint, and time-based radius/opacity/angle FCurve animations)
// rendered client-side - it carries no simulation-visible state (nothing in ISimContext
// depends on it), so the runtime port is a permanently-parked module with an empty state
// inventory: it exists so authored objects carry a live module (module indexing, module
// counts) instead of a [ParseOnly] hole. Matches the LargeGroupAudioUpdate (R11) exemplar
// pattern.
//
// TODO-spec (unverified, the whole visual behavior): the retail FCurve evaluation
// (Bezier tangent interpolation, HOLD/CYCLE padding) and the ring rendering live
// client-side; model them when a rendering host exists.

using System.Collections.Generic;
using OpenSage.Data.Ini;
using OpenSage.Mathematics;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class PartTheHeavensUpdate : UpdateModule
{
    public PartTheHeavensUpdate(GameObject gameObject, ISimContext context, PartTheHeavensUpdateModuleData data)
        : base(gameObject, context)
    {
        // Visual-only module: nothing to schedule.
        SetWakeFrame(UpdateSleepTime.Forever);
    }

    public override UpdateSleepTime Update() => UpdateSleepTime.Forever;

    // ---- the single walk: no mutable sim state (the ring animation is client-side). ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
    }
}

[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public sealed class PartTheHeavensUpdateModuleData : UpdateModuleData
{
    internal static PartTheHeavensUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<PartTheHeavensUpdateModuleData> FieldParseTable = new IniParseTable<PartTheHeavensUpdateModuleData>
        {
            { "Texture", (parser, x) => x.Texture = parser.ParseAssetReference() },
            { "Color", (parser, x) => x.Color = parser.ParseColorRgba() },
            { "Radius", (parser, x) => x.Radius = FCurve.Parse(parser) },
            { "Opacity", (parser, x) => x.Opacity = FCurve.Parse(parser) },
            { "Angle", (parser, x) => x.Angle = FCurve.Parse(parser) },
        };

    public string Texture { get; private set; }
    public ColorRgba Color { get; private set; }
    public FCurve Radius { get; private set; }
    public FCurve Opacity { get; private set; }
    public FCurve Angle { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new PartTheHeavensUpdate(gameObject, gameEngine.SimContext, this);
    }
}

public sealed class FCurve
{
    internal static FCurve Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    internal static readonly IniParseTable<FCurve> FieldParseTable = new IniParseTable<FCurve>
    {
        { "Key", (parser, x) => x.Keys.Add(Key.Parse(parser)) },
        { "InPadding", (parser, x) => x.InPadding = parser.ParseEnum<Padding>() },
        { "OutPadding", (parser, x) => x.OutPadding = parser.ParseEnum<Padding>() },
    };

    public List<Key> Keys { get; } = new List<Key>();
    public Padding InPadding { get; private set; }
    public Padding OutPadding { get; private set; }
}

public sealed class Key
{
    internal static Key Parse(IniParser parser) => parser.ParseAttributeList(FieldParseTable);

    internal static readonly IniParseTable<Key> FieldParseTable = new IniParseTable<Key>
    {
        { "T", (parser, x) => x.T = parser.ParseFloat() },
        { "V", (parser, x) => x.V = parser.ParseFloat() },
        { "I", (parser, x) => x.I = parser.ParseFloat() },
        { "O", (parser, x) => x.O = parser.ParseFloat() },
    };

    public float T { get; private set; }
    public float V { get; private set; }
    public float I { get; private set; }
    public float O { get; private set; }
}

public enum Padding
{
    [IniEnum("HOLD")]
    Hold,

    [IniEnum("CYCLE")]
    Cycle,
}
