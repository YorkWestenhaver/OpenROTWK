// RandomSoundSelectorClientBehavior - R12 port off the [ParseOnly] backlog (census:
// ClientBehavior). The retail module is a client-side audio selector: each tick (or once,
// if RerollOnEveryFrame is false) it rolls against Chance to decide whether to queue one of
// its sounds, and VoicePriority arbitrates which selector's sound wins audio focus against
// other selectors on the same object. None of that reaches simulation state - the choice of
// which sound plays, and when, is presentation only and never influences game logic - and
// audio is deliberately absent from ISimContext (S8), so (like LargeGroupAudioUpdate, R11
// Track B) this is a permanently-parked runtime module with an empty state inventory: it
// exists so authored objects carry a live module (module indexing, module counts) instead of
// a [ParseOnly] hole, without inventing a client audio host that does not exist yet.
//
// TODO-spec (unverified, the whole audio-selection behavior): the retail per-frame reroll,
// Chance gate, and VoicePriority arbitration between sibling selectors live client-side;
// model them when an audio host exists.

using OpenSage.Data.Ini;
using OpenSage.Mathematics;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class RandomSoundSelectorClientBehavior : BehaviorModule
{
    public RandomSoundSelectorClientBehavior(GameObject gameObject, ISimContext context, RandomSoundSelectorClientBehaviorData data)
        : base(gameObject, context)
    {
        // Audio-only module (S8): nothing to schedule, nothing to simulate.
    }

    // ---- the single walk: no mutable sim state (the audio roll is client-side). ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
    }
}

[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public class RandomSoundSelectorClientBehaviorData : ClientBehaviorModuleData
{
    internal static RandomSoundSelectorClientBehaviorData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    internal static readonly IniParseTable<RandomSoundSelectorClientBehaviorData> FieldParseTable = new IniParseTable<RandomSoundSelectorClientBehaviorData>
    {
        { "Chance", (parser, x) => x.Chance = parser.ParsePercentage() },
        { "RerollOnEveryFrame", (parser, x) => x.RerollOnEveryFrame = parser.ParseBoolean() },
        { "VoicePriority", (parser, x) => x.VoicePriority = parser.ParseInteger() }
    };

    public Percentage Chance { get; private set; }
    public bool RerollOnEveryFrame { get; private set; }
    public int VoicePriority { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new RandomSoundSelectorClientBehavior(gameObject, gameEngine.SimContext, this);
    }
}
