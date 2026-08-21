// ModelConditionAudioLoopClientBehavior - R12 port. Client-side audio behavior: plays a
// looping sound while the owning object's ModelConditionFlags satisfy a Required/Excluded
// BitArray test. Audio selection and playback are deliberately absent from ISimContext (S8),
// so there is nothing sim-visible for this module to compute; the runtime port is a
// permanently-parked module with an empty state inventory, matching the pattern established
// by Update/LargeGroupAudioUpdate.cs (R11 Track B).
//
// This module IS live and reachable: GameObject's module-instantiation walk (GameObject.cs,
// same R12 round) does `objectDefinition.Behaviors.Values.Concat(objectDefinition.ClientBehaviors.Values)`,
// so every object with `ClientBehavior = ModelConditionAudioLoopClientBehavior` gets a real
// instance attached to BehaviorModules at spawn. It is deliberately parked, not dead: it does
// nothing audible for any asset that references it (ambient loops - torches, waterfalls, idle
// machinery) until an audio host exists on ISimContext, and there is currently no runtime
// indication of that gap.
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

    // Reachable (see header): GameObject's module-instantiation walk iterates
    // ObjectDefinition.ClientBehaviors alongside Behaviors, so this fires at spawn for any
    // object referencing this ClientBehavior.
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
