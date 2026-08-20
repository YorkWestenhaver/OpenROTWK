// TerrainResourceClientBehavior - R12 port. The client-side counterpart to the sim-side
// TerrainResourceBehavior (Logic/Object/Behaviors/TerrainResourceBehavior.cs, not yet
// ported). Its INI block declares no fields, so the module has no configuration to hold
// and no sim-visible state of its own. Nothing in ISimContext (S8) models a client-side
// tracking role, so - like LargeGroupAudioUpdate (R11 Track B) - this is a parked module
// with an empty state inventory: it exists so authored objects instantiate a live module
// rather than nothing. It never schedules an update and never touches draw/client-update
// callbacks (ClientBehaviorModuleData is a distinct module family from
// ClientUpdateModuleData/UpdateModuleData).
//
// TODO-spec (unverified): if a client-side terrain-resource tracking host is ever built,
// model the client/server module pairing here. See the workbench research notes for the
// behavioral spec.

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
