// LargeGroupAudioUpdate - R11 Track B port. BFME-only (no generals-gpl sibling) and no
// clean-room spec in bfme2-workbench/research/. The retail module's entire output is the
// large-group ambient audio mix (per-key unit weights feeding the crowd sound chooser) -
// audio is deliberately absent from ISimContext (S8), and its bookkeeping feeds nothing
// sim-visible, so the runtime port is a permanently-parked module with an empty state
// inventory: it exists so authored objects carry a live module (module indexing, module
// counts) instead of a [ParseOnly] hole.
//
// TODO-spec (unverified, the whole audio behavior): the retail key registration/weighted
// membership scan and the crowd-audio chooser live client-side; model them when an audio
// host exists.

using System.Collections.Generic;
using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class LargeGroupAudioUpdate : UpdateModule
{
    public LargeGroupAudioUpdate(GameObject gameObject, ISimContext context, LargeGroupAudioUpdateModuleData data)
        : base(gameObject, context)
    {
        // Audio-only module (S8): nothing to schedule.
        SetWakeFrame(UpdateSleepTime.Forever);
    }

    public override UpdateSleepTime Update() => UpdateSleepTime.Forever;

    // ---- the single walk: no mutable sim state (the audio mix is client-side). ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
    }
}

[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public sealed class LargeGroupAudioUpdateModuleData : UpdateModuleData
{
    internal static LargeGroupAudioUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<LargeGroupAudioUpdateModuleData> FieldParseTable = new IniParseTable<LargeGroupAudioUpdateModuleData>
    {
       { "Key", (parser, x) => x.Keys.AddRange(parser.ParseAssetReferenceArray()) },
       { "UnitWeight", (parser, x) => x.UnitWeight = parser.ParseInteger() },
    };

    public List<string> Keys { get; } = new List<string>();
    public int UnitWeight { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new LargeGroupAudioUpdate(gameObject, gameEngine.SimContext, this);
    }
}
