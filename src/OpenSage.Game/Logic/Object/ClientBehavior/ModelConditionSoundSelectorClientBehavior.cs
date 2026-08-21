// ModelConditionSoundSelectorClientBehavior - R12 port. Client-side audio-selection module
// that maps a single unit model-condition flag to a bundle of voice/sound asset references
// (SoundState). Audio selection and playback are deliberately absent from ISimContext (S8;
// see LargeGroupAudioUpdate's header for the same finding), and the parsed state has no
// sim-visible effect - it only tells a client-side audio host which voice/sound assets to
// prefer while the owning object is in a given model condition. This is therefore a
// permanently-parked module, following the LargeGroupAudioUpdate template: it exists so
// authored objects carry a live module (module indexing, module counts, CRC walk) instead of
// a [ParseOnly] hole, with an empty mutable-state inventory (the parsed SoundState is
// immutable config, not sim state).
//
// TODO-spec (unverified, the whole audio behavior): the retail per-condition voice/sound
// selection and playback lives client-side; model it when an audio host exists.
//
// Documented in scaffolding-log.md (Task A0.1, Finding 2) as one of six known ClientBehavior
// audio modules outside the simulation contract.

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class ModelConditionSoundSelectorClientBehavior : BehaviorModule
{
    public ModelConditionSoundSelectorClientBehavior(GameObject gameObject, ISimContext context, ModelConditionSoundSelectorClientBehaviorData data)
        : base(gameObject, context)
    {
        // Audio-only module (S8): the parsed SoundState is immutable config consumed by a
        // client-side audio host, not sim state. Nothing to schedule.
    }

    // ---- the single walk: no mutable sim state (the audio selection is client-side). ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
    }
}

[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public class ModelConditionSoundSelectorClientBehaviorData : ClientBehaviorModuleData
{
    internal static ModelConditionSoundSelectorClientBehaviorData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    internal static readonly IniParseTable<ModelConditionSoundSelectorClientBehaviorData> FieldParseTable = new IniParseTable<ModelConditionSoundSelectorClientBehaviorData>
    {
        { "SoundState", (parser, x) => x.SoundState = SoundState.Parse(parser) }
    };

    public SoundState SoundState { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new ModelConditionSoundSelectorClientBehavior(gameObject, gameEngine.SimContext, this);
    }
}

public sealed class SoundState
{
    internal static SoundState Parse(IniParser parser)
    {
        var condition = parser.ParseEnum<ModelConditionFlag>();
        var result = parser.ParseBlock(FieldParseTable);
        result.Condition = condition;
        return result;
    }

    internal static readonly IniParseTable<SoundState> FieldParseTable = new IniParseTable<SoundState>
    {
         { "VoiceSelect", (parser, x) => x.VoiceSelect = parser.ParseAssetReference() },
         { "VoiceSelect2", (parser, x) => x.VoiceSelect2 = parser.ParseAssetReference() },
         { "VoiceSelectBattle", (parser, x) => x.VoiceSelectBattle = parser.ParseAssetReference() },
         { "VoiceSelectBattle2", (parser, x) => x.VoiceSelectBattle2 = parser.ParseAssetReference() },
         { "VoiceMove", (parser, x) => x.VoiceMove = parser.ParseAssetReference() },
         { "VoiceMove2", (parser, x) => x.VoiceMove2 = parser.ParseAssetReference() },
         { "VoiceMoveToCamp", (parser, x) => x.VoiceMoveToCamp = parser.ParseAssetReference() },
         { "VoiceMoveWhileAttacking", (parser, x) => x.VoiceMoveWhileAttacking = parser.ParseAssetReference() },
         { "VoiceEnterStateMove", (parser, x) => x.VoiceEnterStateMove = parser.ParseAssetReference() },
         { "VoiceEnterStateMoveToCamp", (parser, x) => x.VoiceEnterStateMoveToCamp = parser.ParseAssetReference() },
         { "VoiceEnterStateMoveWhileAttacking", (parser, x) => x.VoiceEnterStateMoveWhileAttacking = parser.ParseAssetReference() },
         { "VoiceAttack", (parser, x) => x.VoiceAttack = parser.ParseAssetReference() },
         { "VoiceAttackCharge", (parser, x) => x.VoiceAttackCharge = parser.ParseAssetReference() },
         { "VoiceAttackMachine", (parser, x) => x.VoiceAttackMachine = parser.ParseAssetReference() },
         { "VoiceAttackStructure", (parser, x) => x.VoiceAttackStructure = parser.ParseAssetReference() },
         { "VoiceFear", (parser, x) => x.VoiceFear = parser.ParseAssetReference() },
         { "VoiceRetreatToCastle", (parser, x) => x.VoiceRetreatToCastle = parser.ParseAssetReference() },
         { "VoiceGuard", (parser, x) => x.VoiceGuard = parser.ParseAssetReference() },
         { "SoundMoveStart", (parser, x) => x.SoundMoveStart = parser.ParseAssetReference() },
         { "SoundImpact", (parser, x) => x.SoundImpact = parser.ParseAssetReference() },
         { "VoicePriority", (parser, x) => x.VoicePriority = parser.ParseInteger() },
         { "UnitSpecificSounds", (parser, x) => x.UnitSpecificSounds = UnitSpecificSounds.Parse(parser) },
         { "SoundMoveLoop", (parser, x) => x.SoundMoveLoop = parser.ParseAssetReference() },
         { "VoiceEnterStateAttackCharge", (parser, x) => x.VoiceEnterStateAttackCharge = parser.ParseAssetReference() },
         { "VoiceCreated", (parser, x) => x.VoiceCreated = parser.ParseAssetReference() },
         { "VoiceFullyCreated", (parser, x) => x.VoiceFullyCreated = parser.ParseAssetReference() }
    };

    public ModelConditionFlag Condition { get; private set; }

    public string VoiceSelect { get; private set; }
    public string VoiceSelect2 { get; private set; }
    public string VoiceSelectBattle { get; private set; }
    public string VoiceSelectBattle2 { get; private set; }
    public string VoiceMove { get; private set; }
    public string VoiceMove2 { get; private set; }
    public string VoiceMoveToCamp { get; private set; }
    public string VoiceMoveWhileAttacking { get; private set; }
    public string VoiceEnterStateMove { get; private set; }
    public string VoiceEnterStateMoveToCamp { get; private set; }
    public string VoiceEnterStateMoveWhileAttacking { get; private set; }
    public string VoiceAttack { get; private set; }
    public string VoiceAttackCharge { get; private set; }
    public string VoiceAttackMachine { get; private set; }
    public string VoiceAttackStructure { get; private set; }
    public string VoiceFear { get; private set; }
    public string VoiceRetreatToCastle { get; private set; }
    public string VoiceGuard { get; private set; }
    public string SoundMoveStart { get; private set; }
    public string SoundImpact { get; private set; }
    public int VoicePriority { get; private set; }
    public UnitSpecificSounds UnitSpecificSounds { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public string SoundMoveLoop { get; private set; }

    [AddedIn(SageGame.Bfme2Rotwk)]
    public string VoiceEnterStateAttackCharge { get; private set; }

    [AddedIn(SageGame.Bfme2Rotwk)]
    public string VoiceCreated { get; private set; }

    [AddedIn(SageGame.Bfme2Rotwk)]
    public string VoiceFullyCreated { get; private set; }
}
