// ModelConditionAudioLoopClientBehavior - R12 port. Client-side audio behavior: plays a
// looping sound while the owning object's ModelConditionFlags satisfy a Required/Excluded
// BitArray test. Audio selection and playback are deliberately absent from ISimContext (S8),
// so there is nothing sim-visible for this module to compute; the runtime port is a
// permanently-parked module with an empty state inventory, matching the pattern established
// by Update/LargeGroupAudioUpdate.cs (R11 Track B).
//
// Unlike LargeGroupAudioUpdate, this module cannot yet be reached at all: ClientBehavior
// entries are parsed into ObjectDefinition.ClientBehaviors (see ClientBehavior.cs), but
// GameObject's module-instantiation walk (GameObject.cs, the `objectDefinition.Behaviors`
// loop) only ever iterates the separate `Behaviors` dictionary - ClientBehaviors is parsed
// and inheritance-merged (ObjectDefinition.cs) but never instantiated into live modules.
// CreateModule is still implemented here, forward-looking, so the module is ready the moment
// that seam lands; until then it is unreachable dead code, not a functional gap in this port.
//
// TODO-spec (unverified, the whole audio behavior): the retail model-condition-driven sound
// loop selection lives client-side; model it when an audio host exists.

using OpenSage.Data.Ini;
using OpenSage.Mathematics;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class ModelConditionAudioLoopClientBehavior : BehaviorModule
{
    public ModelConditionAudioLoopClientBehavior(GameObject gameObject, ISimContext context, ModelConditionAudioLoopClientBehaviorData data)
        : base(gameObject, context)
    {
        // Audio-only module (S8): nothing to schedule, nothing to compute.
    }

    // ---- the single walk: no mutable sim state (the audio loop selection is client-side). ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
    }
}

[AddedIn(SageGame.Bfme2)]
[SimDataAudited]
public sealed class ModelConditionAudioLoopClientBehaviorData : ClientBehaviorModuleData
{
    internal static ModelConditionAudioLoopClientBehaviorData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    internal static readonly IniParseTable<ModelConditionAudioLoopClientBehaviorData> FieldParseTable = new IniParseTable<ModelConditionAudioLoopClientBehaviorData>
    {
        { "ModelCondition", (parser, x) => x.ModelCondition = ModelCondition.Parse(parser) }
    };

    public ModelCondition ModelCondition { get; private set; }

    // Forward-looking (see header): not yet reachable via GameObject's module-instantiation
    // walk, which does not iterate ObjectDefinition.ClientBehaviors. Implemented now so the
    // module activates automatically once that seam is wired.
    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new ModelConditionAudioLoopClientBehavior(gameObject, gameEngine.SimContext, this);
    }
}

public sealed class ModelCondition
{
    internal static ModelCondition Parse(IniParser parser) => parser.ParseAttributeList(FieldParseTable);

    internal static readonly IniParseTable<ModelCondition> FieldParseTable = new IniParseTable<ModelCondition>
    {
        { "REQUIRED", (parser, x) => x.Required = parser.ParseInLineEnumBitArray<ModelConditionFlag>() },
        { "Required", (parser, x) => x.Required = parser.ParseInLineEnumBitArray<ModelConditionFlag>() },

        { "Sound", (parser, x) => x.Sound = parser.ParseAssetReference() },

        { "EXCLUDED", (parser, x) => x.Excluded = parser.ParseInLineEnumBitArray<ModelConditionFlag>() },
        { "Excluded", (parser, x) => x.Excluded = parser.ParseInLineEnumBitArray<ModelConditionFlag>() }
    };

    public BitArray<ModelConditionFlag> Required { get; private set; }
    public BitArray<ModelConditionFlag> Excluded { get; private set; }
    public string Sound { get; private set; }
}
