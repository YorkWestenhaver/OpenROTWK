// PickupStuffUpdate - R11 Track B port. BFME2-only (no generals-gpl sibling) and no
// clean-room spec in bfme2-workbench/research/, so this is the minimal behavior the INI
// chain needs (AotR MordorFighterHorde ModuleTag_PickupStuffUpdate: SkirmishAIOnly = Yes,
// StuffToPickUp = NONE +CRATE, ScanRange = 200, ScanIntervalSeconds = 0.5): a periodic
// scan for pickup targets on the landed S3 partition seam, consuming the FIRST match
// (lowest ObjectId - the seam's deterministic order) by destroying it.
//
// TODO-spec (unverified retail behavior, filed not invented):
//   - the retail flow orders a MOVE to the stuff and collects on arrival/collide; no move
//     order is modeled (S5 move orders are AIUpdate-side, unported), so the pickup here is
//     immediate consumption of an in-range match - the crate's reward pipeline (its
//     CrateCollide modules) is NOT run;
//   - SkirmishAIOnly gates on the skirmish AI controller; modeled as "a non-human owner"
//     (Player.IsHuman false), the closest landed predicate;
//   - the retail choice among multiple in-range crates (nearest?) - modeled: lowest
//     ObjectId, the frozen partition order.

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class PickupStuffUpdate : UpdateModule
{
    private readonly PickupStuffUpdateModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>How many stuff objects this module has consumed.</summary>
    private int _numPickedUp;

    public PickupStuffUpdate(GameObject gameObject, ISimContext context, PickupStuffUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        if (_data.ScanInterval.Value > 0 && _data.ScanRange > Fix64.Zero)
        {
            SetWakeFrame(UpdateSleepTime.Frames(_data.ScanInterval));
        }
        else
        {
            SetWakeFrame(UpdateSleepTime.Forever);
        }
    }

    public int NumPickedUp => _numPickedUp;

    public override UpdateSleepTime Update()
    {
        // SkirmishAIOnly: human-owned objects never scan (TODO-spec note above).
        if (_data.SkirmishAIOnly && (GameObject.Owner?.IsHuman ?? true))
        {
            return UpdateSleepTime.Frames(_data.ScanInterval);
        }

        foreach (var candidate in Context.Partition.QueryObjectsInRadius(GameObject, _data.ScanRange))
        {
            if (candidate == GameObject || candidate.IsDestroyed)
            {
                continue;
            }
            if (_data.StuffToPickUp != null && _data.StuffToPickUp.Matches(candidate))
            {
                Context.GameLogic.DestroyObject(candidate);
                _numPickedUp++;
                break;
            }
        }

        return UpdateSleepTime.Frames(_data.ScanInterval);
    }

    // ---- the single walk (F8 Objects channel; declaration order, F9) ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferInt("NumPickedUp", ref _numPickedUp);
    }
}

[AddedIn(SageGame.Bfme2)]
[SimDataAudited]
public sealed class PickupStuffUpdateModuleData : UpdateModuleData
{
    internal static PickupStuffUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<PickupStuffUpdateModuleData> FieldParseTable = new IniParseTable<PickupStuffUpdateModuleData>
    {
        { "SkirmishAIOnly", (parser, x) => x.SkirmishAIOnly = parser.ParseBoolean() },
        { "StuffToPickUp", (parser, x) => x.StuffToPickUp = ObjectFilter.Parse(parser) },
        // Deterministic S3-query radius -> Fix64; seconds -> logic frames (S5 wire boundary).
        { "ScanRange", (parser, x) => x.ScanRange = parser.ParseFix64() },
        { "ScanIntervalSeconds", (parser, x) => x.ScanInterval = parser.ParseDurationLogicFramesSeconds() }
    };

    public bool SkirmishAIOnly { get; private set; }
    public ObjectFilter StuffToPickUp { get; private set; }
    public Fix64 ScanRange { get; private set; }

    /// <summary>Frames between scans (seconds in INI, ceil-quantized at parse, S5).</summary>
    public LogicFrameSpan ScanInterval { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new PickupStuffUpdate(gameObject, gameEngine.SimContext, this);
    }
}
