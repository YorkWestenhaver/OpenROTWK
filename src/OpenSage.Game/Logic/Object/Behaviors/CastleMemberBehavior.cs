// CastleMemberBehavior - the member-side half of the castle system (R9 castles task).
//
// Behavioral reference: spec-castles.md §3.2. Runtime state is exactly the back-pointer pair
// retail writes from CastleBehavior::unpack (spec-castles.md): the castle
// object id and the castle's native player index - the key used for Eva routing and the
// pack cascade. The Eva event fields themselves are parsed vocabulary; Eva routing waits on
// an Eva surface in ISimEvents (frozen member list - finding F-CAS-10), but the death
// cascade (keep death / VITAL_FOR_BASE_SURVIVAL member death -> castle initiatePack) is
// live through OnDie.

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Orders;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class CastleMemberBehavior : BehaviorModule, IDieModule
{
    private readonly CastleMemberBehaviorModuleData _moduleData;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>The owning castle's object id (retail CMB +0x14); invalid until stamped.</summary>
    private ObjectId _castleObjectId = ObjectId.Invalid;

    /// <summary>The castle's native player index (retail CMB +0x18).</summary>
    private int _nativePlayerIndex = -1;

    internal CastleMemberBehavior(GameObject gameObject, IGameEngine gameEngine, CastleMemberBehaviorModuleData moduleData)
        : base(gameObject, gameEngine)
    {
        _moduleData = moduleData;
    }

    internal ObjectId CastleObjectId => _castleObjectId;
    internal int NativePlayerIndex => _nativePlayerIndex;

    /// <summary>Written by CastleBehavior.unpack (spec §3.2 runtime state).</summary>
    internal void SetCastleBackReference(ObjectId castleObjectId, int nativePlayerIndex)
    {
        _castleObjectId = castleObjectId;
        _nativePlayerIndex = nativePlayerIndex;
    }

    void IDieModule.OnDie(in DamageInfoInput damageInput)
    {
        if (_castleObjectId.IsInvalid)
        {
            return;
        }

        GameEngine.GameLogic.GetObjectById(_castleObjectId)
            ?.FindBehavior<CastleBehavior>()
            ?.OnMemberDied(GameObject);
    }

    // ---- the single walk (declaration order = OUR order, F9) ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferObjectId("CastleObjectId", ref _castleObjectId);
        xfer.XferInt("NativePlayerIndex", ref _nativePlayerIndex);
    }
}

[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public class CastleMemberBehaviorModuleData : BehaviorModuleData
{
    internal static CastleMemberBehaviorModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    internal static readonly IniParseTable<CastleMemberBehaviorModuleData> FieldParseTable = new IniParseTable<CastleMemberBehaviorModuleData>
    {
        { "CountsForEvaCastleBreached", (parser, x) => x.CountsForEvaCastleBreached = parser.ParseBoolean() },
        { "UnderAttackEvaEventIfKeep", (parser, x) => x.UnderAttackEvaEventIfKeep = parser.ParseAssetReference() },
        { "UnderAttackAllyEvaEventIfKeep", (parser, x) => x.UnderAttackAllyEvaEventIfKeep = parser.ParseAssetReference() },
        { "CampDestroyedOwnerEvaEvent", (parser, x) => x.CampDestroyedOwnerEvaEvent = parser.ParseAssetReference() },
        { "CampDestroyedAllyEvaEvent", (parser, x) => x.CampDestroyedAllyEvaEvent = parser.ParseAssetReference() },
        { "CampDestroyedAttackerEvaEvent", (parser, x) => x.CampDestroyedAttackerEvaEvent = parser.ParseAssetReference() },
        { "StoreUpgradePrice", (parser, x) => x.StoreUpgradePrice = parser.ParseBoolean() },
        { "BeingBuiltSound", (parser, x) => x.BeingBuiltSound = parser.ParseAssetReference() }
    };

    public bool CountsForEvaCastleBreached { get; private set; }
    public string UnderAttackEvaEventIfKeep { get; private set; }
    public string UnderAttackAllyEvaEventIfKeep { get; private set; }
    public string CampDestroyedOwnerEvaEvent { get; private set; }
    public string CampDestroyedAllyEvaEvent { get; private set; }
    public string CampDestroyedAttackerEvaEvent { get; private set; }
    public bool StoreUpgradePrice { get; private set; }
    public string BeingBuiltSound { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new CastleMemberBehavior(gameObject, gameEngine, this);
    }
}
