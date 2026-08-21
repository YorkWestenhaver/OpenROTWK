// FirestormDynamicGeometryInfoUpdate - R12 port. Behavioral reference (semantics only):
// generals-gpl GeneralsMD GameLogic/Object/Update/FirestormDynamicGeometryInfoUpdate.cpp
// (class FirestormDynamicGeometryInfoUpdate, extending DynamicGeometryInfoUpdate).
//
// PARKED (permanently, pending substrate growth - same shape as LargeGroupAudioUpdate,
// R11 Track B): every piece of this module's actual behavior routes through capabilities
// that ISimContext (S8, frozen member list) deliberately does not yet expose, AND its base
// class (DynamicGeometryInfoUpdate - the morphing-geometry major-radius interpolation and
// direction-reversal driver) has never itself been ported to [SimState]. There is no
// faithful subset to write without inventing sim behavior:
//
//   - the base class's own job (interpolating GeometryInfo's major radius between
//     InitialMajorRadius/FinalMajorRadius over TransitionTime, and flipping
//     m_switchedDirections when ReverseAtTransitionTime fires) does not exist in
//     OpenSage.Game/Logic/Object at all - not even the base's [ParseOnly] hole is present,
//     because DynamicGeometryInfoUpdate as a class was never authored here;
//   - GPL's TheParticleSystemManager->createParticleSystem/findParticleSystem gives the
//     original a per-emitter id it can look up every frame and mutate
//     (setEmissionVolumeSphereRadius/CylinderRadius) to keep up to MAX_FIRESTORM_SYSTEMS=16
//     emitters tracking the geometry's current major radius. ISimEvents.
//     FireParticleSystemAtObject is deliberately fire-and-forget (research/TransitionDamageFX
//     finding F-TDF-1: "the client owns the created emitter's lifetime, so the sim keeps no
//     particle-system id") - there is no member that returns an id, and no member that lets a
//     module push a live radius into an already-fired emitter;
//   - GPL's TheGameClient->addScorch places a client-side terrain decal; ISimEvents has no
//     scorch-mark member;
//   - GPL's doDamageScan iterates ThePartitionManager->iterateObjectsInRange and calls
//     Object::attemptDamage (the full armor/body damage pipeline, DAMAGE_FLAME /
//     DEATH_BURNED). IPartitionQuery.QueryObjectsInRadius exists (S3) and could serve the
//     scan half, but ISimContext has no damage-dealing member at all - modules never call an
//     attempt-damage pipeline directly, and none is exposed here.
//
// TODO-spec (unverified, the whole behavior, blocked on the above): reconsider this module
// when (a) DynamicGeometryInfoUpdate lands as a [SimState] base with a Fix64 GeometryInfo
// major-radius seam, (b) ISimEvents grows an emitter-handle member that returns an id and
// accepts a later radius update (or an explicit "sync emitter radius to my geometry" member),
// (c) ISimEvents grows a scorch-mark member, and (d) ISimContext grows an area-damage member.
// Until then this module exists so authored objects (e.g. the Ent Firestorm-mine payload)
// carry a live module - module indexing, module counts - instead of a [ParseOnly] hole.

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class FirestormDynamicGeometryInfoUpdate : UpdateModule
{
    public FirestormDynamicGeometryInfoUpdate(GameObject gameObject, ISimContext context, FirestormDynamicGeometryInfoUpdateModuleData data)
        : base(gameObject, context)
    {
        // Parked (see file header): nothing sim-visible to schedule.
        SetWakeFrame(UpdateSleepTime.Forever);
    }

    public override UpdateSleepTime Update() => UpdateSleepTime.Forever;

    // ---- the single walk: no mutable sim state (every effect is client-side output that
    // ISimContext does not yet expose a tracking/mutation seam for - see file header). ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
    }
}

public sealed class FirestormDynamicGeometryInfoUpdateModuleData : UpdateModuleData
{
    internal static FirestormDynamicGeometryInfoUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    // GPL's INI::parseDurationReal (used for DelayBetweenDamageFrames below) converts ms -> Real
    // frames with NO rounding (ConvertDurationFromMsecsToFrames = msec * fps / 1000, straight
    // division) - unlike the ceil-to-uint INI::parseDurationUnsignedInt used for InitialDelay/
    // TransitionTime, which the port maps onto the existing ParseDurationLogicFrames() helper.
    // No shared Fix64-returning equivalent of parseDurationReal exists yet (IniParser.Fix64.cs
    // only has the ceil/LogicFrameSpan variant), so the conversion is done locally here using
    // plain Fix64 arithmetic (the guess+fixup operator/, F1) rather than reaching for the
    // pre-Fix64 float helpers (ParseTimeMillisecondsToLogicFramesFloat) that predate the S5
    // quantization rules.
    private static Fix64 ConvertDurationMsToFramesReal(Fix64 milliseconds, SageGame sageGame)
    {
        var fps = Fix64.FromRaw((long)sageGame.LogicFramesPerSecond() << 32);
        var thousand = Fix64.FromRaw(1000L << 32);
        return milliseconds * fps / thousand;
    }

    private static readonly IniParseTable<FirestormDynamicGeometryInfoUpdateModuleData> FieldParseTable = new IniParseTable<FirestormDynamicGeometryInfoUpdateModuleData>
    {
        // Base class fields (DynamicGeometryInfoUpdateModuleData::buildFieldParse,
        // DynamicGeometryInfoUpdate.cpp:65-79) - never separately authored as a [SimState]
        // base here (see file header), so its parse-only fields are absorbed directly.
        { "InitialDelay", (parser, x) => x.InitialDelay = parser.ParseDurationLogicFrames() },
        { "InitialHeight", (parser, x) => x.InitialHeight = parser.ParseFix64() },
        { "InitialMajorRadius", (parser, x) => x.InitialMajorRadius = parser.ParseFix64() },
        { "InitialMinorRadius", (parser, x) => x.InitialMinorRadius = parser.ParseFix64() },

        { "FinalHeight", (parser, x) => x.FinalHeight = parser.ParseFix64() },
        { "FinalMajorRadius", (parser, x) => x.FinalMajorRadius = parser.ParseFix64() },
        { "FinalMinorRadius", (parser, x) => x.FinalMinorRadius = parser.ParseFix64() },

        { "TransitionTime", (parser, x) => x.TransitionTime = parser.ParseDurationLogicFrames() },
        { "ReverseAtTransitionTime", (parser, x) => x.ReverseAtTransitionTime = parser.ParseBoolean() },

        // Derived class fields (FirestormDynamicGeometryInfoUpdate.cpp:69-96).
        { "DelayBetweenDamageFrames", (parser, x) => x.DelayBetweenDamageFrames = ConvertDurationMsToFramesReal(parser.ParseFix64(), parser.SageGame) },
        { "DamageAmount", (parser, x) => x.DamageAmount = parser.ParseFix64() },
        { "MaxHeightForDamage", (parser, x) => x.MaxHeightForDamage = parser.ParseFix64() },

        { "ParticleSystem1", (parser, x) => x.ParticleSystem1 = parser.ParseAssetReference() },
        { "ParticleSystem2", (parser, x) => x.ParticleSystem2 = parser.ParseAssetReference() },
        { "ParticleSystem3", (parser, x) => x.ParticleSystem3 = parser.ParseAssetReference() },
        { "ParticleSystem4", (parser, x) => x.ParticleSystem4 = parser.ParseAssetReference() },
        { "ParticleSystem5", (parser, x) => x.ParticleSystem5 = parser.ParseAssetReference() },
        { "ParticleSystem6", (parser, x) => x.ParticleSystem6 = parser.ParseAssetReference() },
        { "ParticleSystem7", (parser, x) => x.ParticleSystem7 = parser.ParseAssetReference() },
        { "ParticleSystem8", (parser, x) => x.ParticleSystem8 = parser.ParseAssetReference() },
        { "ParticleSystem9", (parser, x) => x.ParticleSystem9 = parser.ParseAssetReference() },
        { "ParticleSystem10", (parser, x) => x.ParticleSystem10 = parser.ParseAssetReference() },
        { "ParticleSystem11", (parser, x) => x.ParticleSystem11 = parser.ParseAssetReference() },
        { "ParticleSystem12", (parser, x) => x.ParticleSystem12 = parser.ParseAssetReference() },
        { "ParticleSystem13", (parser, x) => x.ParticleSystem13 = parser.ParseAssetReference() },
        { "ParticleSystem14", (parser, x) => x.ParticleSystem14 = parser.ParseAssetReference() },
        { "ParticleSystem15", (parser, x) => x.ParticleSystem15 = parser.ParseAssetReference() },
        { "ParticleSystem16", (parser, x) => x.ParticleSystem16 = parser.ParseAssetReference() },
        { "FXList", (parser, x) => x.FXList = parser.ParseAssetReference() },
        { "ParticleOffsetZ", (parser, x) => x.ParticleOffsetZ = parser.ParseFix64() },
        { "ScorchSize", (parser, x) => x.ScorchSize = parser.ParseFix64() },
    };

    public LogicFrameSpan InitialDelay { get; private set; }
    public Fix64 InitialHeight { get; private set; }
    public Fix64 InitialMajorRadius { get; private set; }
    public Fix64 InitialMinorRadius { get; private set; }

    public Fix64 FinalHeight { get; private set; }
    public Fix64 FinalMajorRadius { get; private set; }
    public Fix64 FinalMinorRadius { get; private set; }

    public LogicFrameSpan TransitionTime { get; private set; }
    public bool ReverseAtTransitionTime { get; private set; }

    public Fix64 ScorchSize { get; private set; }
    public Fix64 ParticleOffsetZ { get; private set; }
    public string ParticleSystem1 { get; private set; }
    public string ParticleSystem2 { get; private set; }
    public string ParticleSystem3 { get; private set; }
    public string ParticleSystem4 { get; private set; }
    public string ParticleSystem5 { get; private set; }
    public string ParticleSystem6 { get; private set; }
    public string ParticleSystem7 { get; private set; }
    public string ParticleSystem8 { get; private set; }
    public string ParticleSystem9 { get; private set; }
    public string ParticleSystem10 { get; private set; }
    public string ParticleSystem11 { get; private set; }
    public string ParticleSystem12 { get; private set; }
    public string ParticleSystem13 { get; private set; }
    public string ParticleSystem14 { get; private set; }
    public string ParticleSystem15 { get; private set; }
    public string ParticleSystem16 { get; private set; }
    public string FXList { get; private set; }

    // GPL default: FirestormDynamicGeometryInfoUpdateModuleData ctor sets
    // m_maxHeightForDamage = 20.0f (FirestormDynamicGeometryInfoUpdate.cpp:60).
    public Fix64 MaxHeightForDamage { get; private set; } = Fix64.FromDecimalLiteral("20.0");

    public Fix64 DelayBetweenDamageFrames { get; private set; }
    public Fix64 DamageAmount { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new FirestormDynamicGeometryInfoUpdate(gameObject, gameEngine.SimContext, this);
    }
}
