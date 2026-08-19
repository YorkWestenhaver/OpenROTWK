// EmotionTrackerUpdate - R11 Track B PARTIAL port. BFME-only (no generals-gpl sibling) and
// no clean-room spec in bfme2-workbench/research/ (searched: only the EmotionNugget asset
// parse exists), so this ports the slice the job-009 INI chain exercises: the periodic
// emotion scan cadence and the FEAR edge - an AfraidOf/AlwaysAfraidOf-matching enemy inside
// FearScanDistance sets the EMOTION_AFRAID model condition, its absence clears it. The scan
// consumes the landed S3 partition seam (ascending ObjectId); the model condition is a
// client presentation output (not in the sim CRC), _afraid is the sim state.
//
// TODO-spec (unverified retail behavior; the cut line of this partial port):
//   - taunt/point (TauntAndPointDistance/Delay/Excluded, PointAt) - not modeled;
//   - the emotion table (AddEmotion nuggets: durations, AILockDuration, attribute-modifier
//     emotions, OVERRIDE variants), cheer/quarrel (QuarrelProbability draws), HeroScanDistance
//     hero detection - not modeled;
//   - fear immunity (ImmuneToFearLevel, IgnoreVeterancy, ModifierType.ResistFear saves) -
//     not modeled: every AfraidOf match inside FearScanDistance frightens;
//   - the retail scan cadence source (modeled: TauntAndPointUpdateDelay, the only authored
//     scan delay in the block, no startup RNG draw so the logic stream is undisturbed).

using System.Collections.Generic;
using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class EmotionTrackerUpdate : UpdateModule
{
    private readonly EmotionTrackerUpdateModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>Whether a feared enemy was inside FearScanDistance at the last scan.</summary>
    private bool _afraid;

    public EmotionTrackerUpdate(GameObject gameObject, ISimContext context, EmotionTrackerUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        if (_data.TauntAndPointUpdateDelay.Value > 0 && _data.FearScanDistance > Fix64.Zero)
        {
            SetWakeFrame(UpdateSleepTime.Frames(_data.TauntAndPointUpdateDelay));
        }
        else
        {
            SetWakeFrame(UpdateSleepTime.Forever);
        }
    }

    public bool IsAfraid => _afraid;

    public override UpdateSleepTime Update()
    {
        var wasAfraid = _afraid;
        _afraid = FearedEnemyNearby();

        if (_afraid && !wasAfraid)
        {
            GameObject.SetModelConditionState(ModelConditionFlag.EmotionAfraid);
        }
        else if (!_afraid && wasAfraid)
        {
            GameObject.ClearModelConditionState(ModelConditionFlag.EmotionAfraid);
        }

        return UpdateSleepTime.Frames(_data.TauntAndPointUpdateDelay);
    }

    /// <summary>Any live enemy inside FearScanDistance that AfraidOf or AlwaysAfraidOf
    /// matches (kind-bit matching; the authored fear filters are kind-based).</summary>
    private bool FearedEnemyNearby()
    {
        var owner = GameObject.Owner;
        if (owner is null)
        {
            return false;
        }
        foreach (var candidate in Context.Partition.QueryObjectsInRadius(GameObject, _data.FearScanDistance))
        {
            if (candidate == GameObject || candidate.IsDestroyed || candidate.IsEffectivelyDead)
            {
                continue;
            }
            if (candidate.Owner is null || !owner.Enemies.Contains(candidate.Owner))
            {
                continue;
            }
            if ((_data.AfraidOf != null && _data.AfraidOf.Matches(candidate)) ||
                (_data.AlwaysAfraidOf != null && _data.AlwaysAfraidOf.Matches(candidate)))
            {
                return true;
            }
        }
        return false;
    }

    // ---- the single walk (F8 Objects channel; declaration order, F9) ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferBool("Afraid", ref _afraid);
    }
}

[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public sealed class EmotionTrackerUpdateModuleData : UpdateModuleData
{
    internal static EmotionTrackerUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<EmotionTrackerUpdateModuleData> FieldParseTable = new IniParseTable<EmotionTrackerUpdateModuleData>
    {
        // Deterministic S3-query radii -> Fix64; the scan delay ms -> logic frames (S5).
        { "TauntAndPointDistance", (parser, x) => x.TauntAndPointDistance = parser.ParseFix64() },
        { "TauntAndPointUpdateDelay", (parser, x) => x.TauntAndPointUpdateDelay = parser.ParseDurationLogicFrames() },
        { "TauntAndPointExcluded", (parser, x) => x.TauntAndPointExcluded = ObjectFilter.Parse(parser) },
        { "AfraidOf", (parser, x) => x.AfraidOf = ObjectFilter.Parse(parser) },
        { "AlwaysAfraidOf", (parser, x) => x.AlwaysAfraidOf = ObjectFilter.Parse(parser) },
        { "PointAt", (parser, x) => x.PointAt = ObjectFilter.Parse(parser) },
        { "FearScanDistance", (parser, x) => x.FearScanDistance = parser.ParseFix64() },
        { "AddEmotion", (parser, x) => x.Emotions.Add(Emotion.Parse(parser)) },
        { "HeroScanDistance", (parser, x) => x.HeroScanDistance = parser.ParseFix64() },
        { "QuarrelProbability", (parser, x) => x.QuarrelProbability = parser.ParseFix64Percentage() },
        { "IgnoreVeterancy", (parser, x) => x.IgnoreVeterancy = parser.ParseBoolean() },
        { "ImmuneToFearLevel", (parser, x) => x.ImmuneToFearLevel = parser.ParseInteger() }
    };

    public Fix64 TauntAndPointDistance { get; private set; }

    /// <summary>The emotion scan cadence (ms in INI, ceil-quantized at parse, S5).</summary>
    public LogicFrameSpan TauntAndPointUpdateDelay { get; private set; }

    public ObjectFilter TauntAndPointExcluded { get; private set; }
    public ObjectFilter AfraidOf { get; private set; }
    public ObjectFilter AlwaysAfraidOf { get; private set; }
    public ObjectFilter PointAt { get; private set; }
    public Fix64 FearScanDistance { get; private set; }
    public List<Emotion> Emotions { get; } = new List<Emotion>();
    public Fix64 HeroScanDistance { get; private set; }

    /// <summary>Exact fraction (40% = 0.4); the quarrel draw itself is unmodeled (TODO-spec).</summary>
    public Fix64 QuarrelProbability { get; private set; }

    public bool IgnoreVeterancy { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public int ImmuneToFearLevel { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new EmotionTrackerUpdate(gameObject, gameEngine.SimContext, this);
    }
}

public sealed class Emotion
{
    internal static Emotion Parse(IniParser parser)
    {
        var firstToken = parser.GetNextToken();
        var secondToken = parser.GetNextTokenOptional();

        var result = new Emotion();
        if (secondToken.HasValue)
        {
            result = parser.ParseBlock(FieldParseTable);
            result.Type = IniParser.ScanEnum<EmotionType>(firstToken);
            result.EmotionName = parser.ScanAssetReference(secondToken.Value);
        }
        else
        {
            result.Type = EmotionType.None;
            result.EmotionName = parser.ScanAssetReference(firstToken);
        }
        return result;
    }

    internal static readonly IniParseTable<Emotion> FieldParseTable = new IniParseTable<Emotion>
    {
        { "AttributeModifier", (parser, x) => x.AttributeModifier = parser.ParseAssetReference() },
        { "Duration", (parser, x) => x.Duration = parser.ParseInteger() },
        { "AILockDuration", (parser, x) => x.AILockDuration = parser.ParseInteger() },
    };

    public EmotionType Type { get; private set; }
    public string EmotionName { get; private set; }
    public string AttributeModifier { get; private set; }
    public int Duration { get; private set; }

    [AddedIn(SageGame.Bfme2Rotwk)]
    public int AILockDuration { get; private set; }
}
