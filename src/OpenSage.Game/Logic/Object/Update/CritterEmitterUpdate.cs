// CritterEmitterUpdate - R13 port. BFME-only (no generals-gpl sibling - the only GPL spawner
// module, SpawnBehavior, is a pool/budding/replace-on-death system with no field on this
// three-field ModuleData to hang any of that from; see bfme2-workbench/research/modules-r13
// /specs/CritterEmitterUpdateModuleData.md sec 0 for the full contrast). Ported from field
// names + the landed periodic-cadence idiom (PickupStuffUpdate) plus the live AotR record
// (natureunits.ini:4720-4724), which is the FX-only shape: SpawnObject commented out.
//
// F-CEU-1 (filed, not invented around): no field selects FX orientation or spawn-offset, so
// this port takes the oriented FireFXAtObject(string, ObjectId) overload and the
// exactly-at-self CreateObjectAt(definition, owner, at) overload - the plain default reading
// with nothing more specific to key off, matching EmpUpdate/HordeSiegeEngineContain's choice.

using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class CritterEmitterUpdate : UpdateModule
{
    private readonly CritterEmitterUpdateModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>How many times this emitter has fired (spawn-or-FX-only cadence ticks).</summary>
    private int _numEmissions;

    public CritterEmitterUpdate(GameObject gameObject, ISimContext context, CritterEmitterUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        SetWakeFrame(
            _data.ReloadTime.Value > 0
                ? UpdateSleepTime.Frames(_data.ReloadTime)
                : UpdateSleepTime.Forever);
    }

    public int NumEmissions => _numEmissions;

    public override UpdateSleepTime Update()
    {
        // F-CEU-1: oriented FX at self, always fired on cadence.
        Context.Events.FireFXAtObject(_data.FX, GameObject.Id);

        // SpawnObject is optional (live AotR usage leaves it unset - the FX-only emitter):
        // no spawn attempted when null.
        var spawnTemplate = _data.SpawnObject?.Value;
        if (spawnTemplate != null)
        {
            Context.GameLogic.CreateObjectAt(spawnTemplate, GameObject.Owner, GameObject);
        }

        _numEmissions++;

        return UpdateSleepTime.Frames(_data.ReloadTime);
    }

    // ---- the single walk (save/load + CRC + deep-dump + conformance) ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferInt("NumEmissions", ref _numEmissions);
    }
}

[SimDataAudited]
[AddedIn(SageGame.Bfme)]
public sealed class CritterEmitterUpdateModuleData : UpdateModuleData
{
    internal static CritterEmitterUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<CritterEmitterUpdateModuleData> FieldParseTable = new IniParseTable<CritterEmitterUpdateModuleData>
    {
        { "FX", (parser, x) => x.FX = parser.ParseAssetReference() },
        { "SpawnObject", (parser, x) => x.SpawnObject = parser.ParseObjectReference() },
        { "ReloadTime", (parser, x) => x.ReloadTime = parser.ParseDurationLogicFrames() }
    };

    public string FX { get; private set; }

    /// <summary>Optional - commented out in the only enabled AotR usage found (FX-only emitter).</summary>
    public LazyAssetReference<ObjectDefinition> SpawnObject { get; private set; }

    /// <summary>Frames between emissions (ms in INI, ceil-quantized at parse, S5 finding -
    /// was ParseInteger/int).</summary>
    public LogicFrameSpan ReloadTime { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new CritterEmitterUpdate(gameObject, gameEngine.SimContext, this);
    }
}
