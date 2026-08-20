// TerrainResourceClientBehavior - R12 port. A client-side placeholder for the sim-side
// TerrainResourceBehavior (Logic/Object/Behaviors/TerrainResourceBehavior.cs, itself still
// [ParseOnly]): the retail module carries no configuration parameters and produces no
// sim-visible state of its own (no income, no visuals, no audio) - its entire role in
// retail is a client-side marker object that mirrors the paired server behavior so the
// client can find/track the resource point. Nothing in ISimContext (S8) models that
// client-tracking role, so - like LargeGroupAudioUpdate (R11 Track B) - this is a
// permanently-parked module with an empty state inventory: it exists so authored objects
// carry a live module instead of a [ParseOnly] hole. It never schedules an update and never
// touches draw/client-update callbacks (ClientBehaviorModuleData is a distinct module
// family from ClientUpdateModuleData/UpdateModuleData).
//
// TODO-spec (unverified): if a client-side terrain-resource marker/tracking host is ever
// built, model the retail client<->server pairing here.

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class TerrainResourceClientBehavior : BehaviorModule
{
    public TerrainResourceClientBehavior(GameObject gameObject, ISimContext context, TerrainResourceClientBehaviorData data)
        : base(gameObject, context)
    {
        // Instantiation-only marker module (S8): no configuration, no scheduled work.
    }

    // ---- the single walk: no mutable sim state (the retail module carries none). ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
    }
}

[AddedIn(SageGame.Bfme2)]
[SimDataAudited]
public class TerrainResourceClientBehaviorData : ClientBehaviorModuleData
{
    internal static TerrainResourceClientBehaviorData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    internal static readonly IniParseTable<TerrainResourceClientBehaviorData> FieldParseTable = new IniParseTable<TerrainResourceClientBehaviorData>
    {
    };

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new TerrainResourceClientBehavior(gameObject, gameEngine.SimContext, this);
    }
}
