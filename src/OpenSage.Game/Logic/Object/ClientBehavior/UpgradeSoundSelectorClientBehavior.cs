// UpgradeSoundSelectorClientBehavior - R13 port. Client-side audio-selection module that maps
// a gated bundle of upgrade-exclusion/model-condition requirements (SoundUpgrade) to a bundle
// of voice/sound asset references. Audio selection and playback are deliberately absent from
// ISimContext (S8; see LargeGroupAudioUpdate's header for the same finding, and
// ModelConditionSoundSelectorClientBehavior's header for the identical finding on the sibling
// this port templates from), and the parsed state has no sim-visible effect - it only tells a
// future client-side audio host which voice/sound assets to prefer for a given
// model-condition/upgrade-exclusion gate. This is therefore a permanently-parked module,
// following the ModelConditionSoundSelectorClientBehavior/LargeGroupAudioUpdate template: it
// exists so authored objects carry a live module (module indexing, module counts, CRC walk)
// instead of a [ParseOnly] hole, with an empty mutable-state inventory (the parsed
// SoundUpgrade list is immutable config, not sim state).
//
// TODO-spec (unverified, the whole audio behavior): the retail per-upgrade voice/sound
// selection and playback lives client-side; model it when an audio host exists. Tie-break
// order across simultaneously-satisfiable SoundUpgrade entries is unspecified by any GPL or
// landed source - do not invent one here (see the port spec packet §5).

using System.Collections.Generic;
using OpenSage.Data.Ini;
using OpenSage.Mathematics;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class UpgradeSoundSelectorClientBehavior : BehaviorModule
{
    public UpgradeSoundSelectorClientBehavior(GameObject gameObject, ISimContext context, UpgradeSoundSelectorClientBehaviorData data)
        : base(gameObject, context)
    {
        // Audio-only module (S8), same posture as ModelConditionSoundSelectorClientBehavior:
        // the parsed SoundUpgrade list is immutable config consumed by a client-side audio
        // host, not sim state. Nothing to schedule.
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
public class UpgradeSoundSelectorClientBehaviorData : ClientBehaviorModuleData
{
    internal static UpgradeSoundSelectorClientBehaviorData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    internal static readonly IniParseTable<UpgradeSoundSelectorClientBehaviorData> FieldParseTable = new IniParseTable<UpgradeSoundSelectorClientBehaviorData>
    {
        { "SoundUpgrade", (parser, x) => x.SoundUpgrades.Add(SoundUpgrade.Parse(parser)) }
    };

    public List<SoundUpgrade> SoundUpgrades { get; private set; } = new List<SoundUpgrade>();

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new UpgradeSoundSelectorClientBehavior(gameObject, gameEngine.SimContext, this);
    }
}


public sealed class SoundUpgrade
{
    internal static SoundUpgrade Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    internal static readonly IniParseTable<SoundUpgrade> FieldParseTable = new IniParseTable<SoundUpgrade>
    {
        { "RequiredModelConditions", (parser, x) => x.RequiredModelConditions = parser.ParseEnumBitArray<ModelConditionFlag>() },
        { "VoiceSelect", (parser, x) => x.VoiceSelect = parser.ParseAssetReference() },
        { "ExcludedUpgrades", (parser, x) => x.ExcludedUpgrades = parser.ParseAssetReferenceArray() },
        { "VoiceAttack", (parser, x) => x.VoiceAttack = parser.ParseAssetReference() },
        { "VoiceAttackAir", (parser, x) => x.VoiceAttackAir = parser.ParseAssetReference() },
        { "VoiceAttackCharge", (parser, x) => x.VoiceAttackCharge = parser.ParseAssetReference() },
        { "VoiceAttackMachine", (parser, x) => x.VoiceAttackMachine = parser.ParseAssetReference() },
        { "VoiceAttackStructure", (parser, x) => x.VoiceAttackStructure = parser.ParseAssetReference() },
        { "VoiceCreated", (parser, x) => x.VoiceCreated = parser.ParseAssetReference() },
        { "VoiceFear", (parser, x) => x.VoiceFear = parser.ParseAssetReference() },
        { "VoiceFullyCreated", (parser, x) => x.VoiceFullyCreated = parser.ParseAssetReference() },
        { "VoiceGuard", (parser, x) => x.VoiceGuard = parser.ParseAssetReference() },
        { "VoiceMove", (parser, x) => x.VoiceMove = parser.ParseAssetReference() },
        { "VoiceMoveToCamp", (parser, x) => x.VoiceMoveToCamp = parser.ParseAssetReference() },
        { "VoiceMoveWhileAttacking", (parser, x) => x.VoiceMoveWhileAttacking = parser.ParseAssetReference() },
        { "VoicePriority", (parser, x) => x.VoicePriority = parser.ParseInteger() },
        { "VoiceRetreatToCastle", (parser, x) => x.VoiceRetreatToCastle = parser.ParseAssetReference() },
        { "VoiceSelectBattle", (parser, x) => x.VoiceSelectBattle = parser.ParseAssetReference() },
        { "SoundImpact", (parser, x) => x.SoundImpact = parser.ParseAssetReference() },
        { "UnitSpecificSounds", (parser, x) => x.UnitSpecificSounds = UnitSpecificSounds.Parse(parser) },
    };

    [AddedIn(SageGame.Bfme2)]
    public BitArray<ModelConditionFlag> RequiredModelConditions { get; private set; }

    public string VoiceSelect { get; private set; }
    public string[] ExcludedUpgrades { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public string VoiceAttack { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public string VoiceAttackAir { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public string VoiceAttackCharge { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public string VoiceAttackMachine { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public string VoiceAttackStructure { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public string VoiceCreated { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public string VoiceFear { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public string VoiceFullyCreated { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public string VoiceGuard { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public string VoiceMove { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public string VoiceMoveToCamp { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public string VoiceMoveWhileAttacking { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public int VoicePriority { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public string VoiceRetreatToCastle { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public string VoiceSelectBattle { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public string SoundImpact { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public UnitSpecificSounds UnitSpecificSounds { get; private set; }
}
