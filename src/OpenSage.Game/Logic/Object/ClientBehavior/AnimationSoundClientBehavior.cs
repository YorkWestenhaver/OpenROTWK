// AnimationSoundClientBehavior - R12 port. BFME-only (no generals-gpl sibling) and no
// clean-room spec in bfme2-workbench/research/. The retail module's entire job is
// audio presentation: it walks the AnimationSound entries below and, as an animation
// plays, fires the matching sound at the matching frame - audio is deliberately absent
// from ISimContext (S8), and none of that bookkeeping feeds anything sim-visible, so the
// runtime port is a permanently-parked module with an empty state inventory, matching
// LargeGroupAudioUpdate's pattern.
//
// A second, independent reason this stays parked: ObjectDefinition keeps ClientBehavior
// entries in their own `ClientBehaviors` dictionary, and GameObject's module-instantiation
// walk only reads `Behaviors` (see GameObject.cs) - so today nothing calls
// AnimationSoundClientBehaviorData.CreateModule at all. That seam (F-R11-9) is tracked
// separately; this port makes the ModuleData a live, non-[ParseOnly] class with a working
// CreateModule so the class is ready the moment the seam lands, without inventing the
// per-frame sound-trigger walk ahead of an audio host to run it on (S8).
//
// TODO-spec (unverified, the whole audio behavior): the retail per-frame sound-trigger scan
// and MaxUpdateRangeCap culling live client-side; model them when an audio host exists.

using System.Collections.Generic;
using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class AnimationSoundClientBehavior : BehaviorModule
{
    public AnimationSoundClientBehavior(GameObject gameObject, ISimContext context, AnimationSoundClientBehaviorData data)
        : base(gameObject, context)
    {
        // Audio-only module (S8): the AnimationSound table is read client-side to drive
        // frame-indexed sound triggers; nothing here is mutable sim state.
    }

    // ---- the single walk: no mutable sim state (the sound triggers are client-side). ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
    }
}

[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public class AnimationSoundClientBehaviorData : ClientBehaviorModuleData
{
    internal static AnimationSoundClientBehaviorData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    internal static readonly IniParseTable<AnimationSoundClientBehaviorData> FieldParseTable = new IniParseTable<AnimationSoundClientBehaviorData>
    {
        { "MaxUpdateRangeCap", (parser, x) => { var v = parser.ParseInteger(); x.MaxUpdateRangeCap = v < 0 ? 0 : v; } },
        { "AnimationSound", (parser, x) => x.AnimationSounds.Add(AnimationSoundData.Parse(parser)) },
    };

    public int MaxUpdateRangeCap { get; private set; }
    public List<AnimationSoundData> AnimationSounds { get; private set; } = new List<AnimationSoundData>();

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new AnimationSoundClientBehavior(gameObject, gameEngine.SimContext, this);
    }
}

[AddedIn(SageGame.Bfme)]
public class AnimationSoundData
{
    internal static AnimationSoundData Parse(IniParser parser) => parser.ParseAttributeList(FieldParseTable);

    internal static readonly IniParseTable<AnimationSoundData> FieldParseTable = new IniParseTable<AnimationSoundData>
    {
        { "Sound", (parser, x) => x.Sound = parser.ParseAssetReference() },
        { "Animation", (parser, x) => x.Animations.Add(parser.ParseAssetReference()) },
        { "Frames", (parser, x) => x.Frames.Add(parser.ParseInLineIntegerArray()) },
        { "ExcludedMC", (parser, x) => x.ExcludedMC = parser.ParseEnum<ModelConditionFlag>() },
        { "RequiredMC", (parser, x) => x.RequiredMC = parser.ParseEnum<ModelConditionFlag>() }
    };

    public string Sound { get; private set; }
    public List<string> Animations { get; } = new List<string>();
    public List<int[]> Frames { get; } = new List<int[]>();
    public ModelConditionFlag ExcludedMC { get; private set; }
    public ModelConditionFlag RequiredMC { get; private set; }
}
