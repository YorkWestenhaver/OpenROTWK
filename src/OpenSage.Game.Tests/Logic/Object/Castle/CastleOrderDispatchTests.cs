// R15 S9-05: castle / build-plot orders on the ORDER PIPE.
//
// CastleSystemTests covers the handler's own guard sequences by calling it directly. This
// file covers the thing that did not exist before this packet: an OrderType, an Order
// factory, an OrderProcessor case, a GameLogic-owned handler instance, and - the part that
// decides whether any of it means anything - the LEDGER those orders charge.
//
// The failure this file is built to catch: CastleOrderHandler's bank resolver used to return
// an Economy.ResourceBank, and nothing in the engine constructs a per-player ResourceBank.
// Wiring the handler up against one would have checked affordability against a ledger nobody
// funds and withdrawn from a ledger nobody spends, so every castle purchase - human AND
// skirmish AI - would have been free while every assertion about "the handler charged the
// bank" still passed. Every money assertion below therefore reads
// player.BankAccount.Money, the live ledger, never a bank the test itself constructed.
//
// Harness: HeadlessSimGame + INI text, the same shape as CastleSystemTests.

using System.Collections.Generic;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Economy;
using OpenSage.Logic.Object;
using OpenSage.Logic.Object.Castle;
using OpenSage.Logic.Orders;
using OpenSage.Logic.Sim;
using Xunit;
using MapPlayer = OpenSage.Data.Map.Player;
using Player = OpenSage.Logic.Player;

namespace OpenSage.Tests.Logic.Object.Castle;

public class CastleOrderDispatchTests
{
    // TestBarracks BuildCost = 300; CampFoundation's Men entry UnpackCost = 500.
    // uint, so that every "starting money minus cost" expectation below stays uint and
    // compares directly against BankAccount.Money without a widening cast.
    private const uint BarracksBuildCost = 300;
    private const uint MenUnpackCost = 500;

    private const string CastleIni = @"
Object CampFoundation
  KindOf = STRUCTURE SELECTABLE IMMOBILE BASE_FOUNDATION CASTLE_CENTER
  Geometry = CYLINDER
  GeometryMajorRadius = 20.0
  GeometryHeight = 10.0
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 200
  End
  Behavior = CastleBehavior ModuleTag_Castle
    CastleToUnpackForFaction = Men TestCamp 500
    ScanDistance = 120.0
    FadeTime = 2.0
    UnpackDelayTime = 1.0
    MaxCastleRadius = 250.0
    BuildTime = 4.0
  End
End

Object TestKeep
  KindOf = STRUCTURE CASTLE_KEEP
  Geometry = CYLINDER
  GeometryMajorRadius = 10.0
  GeometryHeight = 10.0
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1000
  End
  Behavior = CastleMemberBehavior ModuleTag_Member
  End
End

Object TestPlot
  KindOf = STRUCTURE SELECTABLE IMMOBILE BASE_FOUNDATION
  Geometry = CYLINDER
  GeometryMajorRadius = 15.0
  GeometryHeight = 5.0
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object TestBarracks
  KindOf = STRUCTURE NEED_BASE_FOUNDATION
  BuildCost = 300
  BuildTime = 5.0
  Geometry = CYLINDER
  GeometryMajorRadius = 14.0
  GeometryHeight = 10.0
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 400
  End
End

Object TestSoldier
  KindOf = INFANTRY SELECTABLE CAN_ATTACK
  Geometry = CYLINDER
  GeometryMajorRadius = 2.0
  GeometryHeight = 5.0
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 50
  End
End
";

    private sealed class FakeTemplateProvider : ICastleTemplateProvider
    {
        public IReadOnlyList<CastleMemberPlacement> GetPlacements(string campName)
            => campName == "TestCamp"
                ? new[] { new CastleMemberPlacement { TemplateName = "TestKeep", Offset = Vector3.Zero, Angle = 0f } }
                : null;
    }

    /// <summary>
    /// Roster: 0 neutral, 1 civilian, 2 "Men" (stands in for the human), 3 "Orcs" (stands in
    /// for the skirmish AI). Both are ordinary roster players - that is the whole point: the
    /// order pipe does not know or care which one is driven by a person.
    /// </summary>
    private static (HeadlessSimGame Game, Player Men, Player Orcs, OrderProcessor Orders) CreateGame()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2);
        game.PlayerManager.OnNewGame(
            [
                MapPlayer.CreateNeutralPlayer(),
                MapPlayer.CreateCivilianPlayer(),
                new MapPlayer { Name = "plyrMen", Faction = "FactionMen", DisplayName = "Men" },
                new MapPlayer { Name = "plyrOrcs", Faction = "FactionOrcs", DisplayName = "Orcs" },
            ],
            GameType.Skirmish);
        game.LoadIniText(CastleIni);

        return (game, game.PlayerManager.Players[2], game.PlayerManager.Players[3], new OrderProcessor(game));
    }

    private static int BarracksDefinitionId(HeadlessSimGame game)
        => game.AssetStore.ObjectDefinitions.GetByName("TestBarracks").InternalId;

    private static void Dispatch(OrderProcessor orders, Order order) => orders.Process(new[] { order });

    // ================================================================
    // The headline: the order pipe charges the LIVE ledger
    // ================================================================

    [Fact]
    public void FoundationConstructOrder_ChargesPlayerBankAccount_AndSpawnsTheStructure()
    {
        var (game, men, _, orders) = CreateGame();
        men.BankAccount.Money = 1000;
        var plot = game.SpawnObject("TestPlot", men, new Vector3(300, 300, 0));

        Dispatch(orders, Order.CreateFoundationConstruct((int)men.Id, plot.Id, BarracksDefinitionId(game)));

        // The money came out of Player.BankAccount - the ledger Player.FromMapData funds,
        // OrderProcessor's other cases spend, and Player.Persist saves. Not a side ledger.
        Assert.Equal(1000u - BarracksBuildCost, men.BankAccount.Money);

        var structure = CastleUnpackStamper.FindStructureOnPlot(plot, game.GameEngine);
        Assert.NotNull(structure);
        Assert.Equal("TestBarracks", structure.Definition.Name);
        Assert.Equal(men, structure.Owner);
        Assert.True(structure.IsBeingConstructed());
    }

    [Fact]
    public void FoundationConstructOrder_ForAnAiPlayer_ChargesThatPlayersOwnBankAccount()
    {
        // The "or the AI builds for free" regression, stated directly. Two players build the
        // same structure through the same dispatch on the same frame; each one's own balance
        // moves, and neither builds without paying.
        var (game, men, orcs, orders) = CreateGame();
        men.BankAccount.Money = 1000;
        orcs.BankAccount.Money = 1000;

        var menPlot = game.SpawnObject("TestPlot", men, new Vector3(300, 300, 0));
        var orcPlot = game.SpawnObject("TestPlot", orcs, new Vector3(900, 900, 0));
        var barracksId = BarracksDefinitionId(game);

        orders.Process(new[]
        {
            Order.CreateFoundationConstruct((int)men.Id, menPlot.Id, barracksId),
            Order.CreateFoundationConstruct((int)orcs.Id, orcPlot.Id, barracksId),
        });

        Assert.Equal(1000u - BarracksBuildCost, men.BankAccount.Money);
        Assert.Equal(1000u - BarracksBuildCost, orcs.BankAccount.Money);
        Assert.NotNull(CastleUnpackStamper.FindStructureOnPlot(menPlot, game.GameEngine));
        Assert.NotNull(CastleUnpackStamper.FindStructureOnPlot(orcPlot, game.GameEngine));
    }

    [Fact]
    public void FoundationConstructOrder_WithoutTheMoney_IsRejected_NothingBuilt_NothingCharged()
    {
        var (game, men, _, orders) = CreateGame();
        men.BankAccount.Money = BarracksBuildCost - 1;
        var plot = game.SpawnObject("TestPlot", men, new Vector3(300, 300, 0));

        Dispatch(orders, Order.CreateFoundationConstruct((int)men.Id, plot.Id, BarracksDefinitionId(game)));

        Assert.Equal(BarracksBuildCost - 1, men.BankAccount.Money);
        Assert.Null(CastleUnpackStamper.FindStructureOnPlot(plot, game.GameEngine));
    }

    [Fact]
    public void FoundationConstructCancelOrder_RefundsIntoPlayerBankAccount()
    {
        var (game, men, _, orders) = CreateGame();
        men.BankAccount.Money = 1000;
        var plot = game.SpawnObject("TestPlot", men, new Vector3(300, 300, 0));

        Dispatch(orders, Order.CreateFoundationConstruct((int)men.Id, plot.Id, BarracksDefinitionId(game)));
        Assert.Equal(1000u - BarracksBuildCost, men.BankAccount.Money);

        Dispatch(orders, Order.CreateFoundationConstructCancel((int)men.Id, plot.Id));

        Assert.Equal(1000u, men.BankAccount.Money);

        game.Step(); // reap the cancelled structure
        Assert.Null(CastleUnpackStamper.FindStructureOnPlot(plot, game.GameEngine));
    }

    [Fact]
    public void CastleUnpackOrder_ChargesUnpackCost_AndInitiatesTheUnpack()
    {
        var (game, men, _, orders) = CreateGame();
        men.BankAccount.Money = 1000;

        var foundation = game.SpawnObject("CampFoundation", men, Vector3.Zero);
        var castle = foundation.FindBehavior<CastleBehavior>();
        castle.TemplateProvider = new FakeTemplateProvider();

        Dispatch(orders, Order.CreateCastleUnpack((int)men.Id, foundation.Id));

        Assert.Equal(1000u - MenUnpackCost, men.BankAccount.Money);
        Assert.Equal(CastleState.UnpackInitiated, castle.State);
    }

    [Fact]
    public void CastlePackOrder_PacksAnUnpackedCastle()
    {
        var (game, men, _, orders) = CreateGame();
        var foundation = game.SpawnObject("CampFoundation", men, Vector3.Zero);
        var castle = foundation.FindBehavior<CastleBehavior>();
        castle.TemplateProvider = new FakeTemplateProvider();
        castle.Unpack(men, instant: true);
        Assert.Equal(CastleState.Unpacked, castle.State);

        Dispatch(orders, Order.CreateCastlePack((int)men.Id, foundation.Id));

        Assert.Equal(CastleState.Packing, castle.State);
    }

    // ================================================================
    // Rejections are loud, and malformed payloads never take the sim down
    // ================================================================

    [Fact]
    public void CastleOrders_RejectedByAGuard_LeaveTheWorldAndTheLedgerUntouched()
    {
        // Every guard in the handler returns a CastleOrderResult that OrderProcessor logs.
        // What the dispatch layer must guarantee on top of that is that a rejection is inert:
        // no half-charge, no orphan structure, no throw.
        var (game, men, orcs, orders) = CreateGame();
        men.BankAccount.Money = 1000;
        orcs.BankAccount.Money = 1000;
        var plot = game.SpawnObject("TestPlot", men, new Vector3(300, 300, 0));
        var soldier = game.SpawnObject("TestSoldier", men, new Vector3(600, 600, 0));
        var barracksId = BarracksDefinitionId(game);

        // NotOwner: Orcs ordering a build on the Men player's plot.
        Dispatch(orders, Order.CreateFoundationConstruct((int)orcs.Id, plot.Id, barracksId));
        // NotAFoundation: the target is a soldier.
        Dispatch(orders, Order.CreateFoundationConstruct((int)men.Id, soldier.Id, barracksId));
        // NoSuchObject: an id that was never allocated.
        Dispatch(orders, Order.CreateCastleUnpack((int)men.Id, new ObjectId(9999)));
        // NothingToCancel: nothing is being built on this plot.
        Dispatch(orders, Order.CreateFoundationConstructCancel((int)men.Id, plot.Id));

        Assert.Equal(1000u, men.BankAccount.Money);
        Assert.Equal(1000u, orcs.BankAccount.Money);
        Assert.Null(CastleUnpackStamper.FindStructureOnPlot(plot, game.GameEngine));
    }

    [Fact]
    public void FoundationConstructOrder_WithAnUnallocatedDefinitionId_IsDroppedNotThrown()
    {
        // An order's payload is untrusted input (stale replay, mismatched mod, malformed
        // packet). ScopedAssetCollection.GetByInternalId throws on a miss, so OrderProcessor
        // catches it: the order is logged and dropped, and the sim survives.
        var (game, men, _, orders) = CreateGame();
        men.BankAccount.Money = 1000;
        var plot = game.SpawnObject("TestPlot", men, new Vector3(300, 300, 0));

        Dispatch(orders, Order.CreateFoundationConstruct((int)men.Id, plot.Id, 0x7FFFFFFF));

        Assert.Equal(1000u, men.BankAccount.Money);
        Assert.Null(CastleUnpackStamper.FindStructureOnPlot(plot, game.GameEngine));
    }

    // ================================================================
    // GameLogic owns the handler and ticks it
    // ================================================================

    [Fact]
    public void CastleOrders_HandlerIsOwnedByGameLogic_OneInstancePerMatch()
    {
        // Both players' orders must land in the SAME handler, or the in-flight construction
        // table (which is Xfer'd sim state) would fragment per caller.
        var (game, _, _, _) = CreateGame();

        Assert.Null(game.GameLogic.CastleOrdersIfCreated);
        var handler = game.GameLogic.CastleOrders;
        Assert.NotNull(handler);
        Assert.Same(handler, game.GameLogic.CastleOrders);
        Assert.Same(handler, game.GameLogic.CastleOrdersIfCreated);
    }

    [Fact]
    public void GameLogicTick_PrunesTheConstructionRow_SoTheEmptiedPlotIsBuildableAgain()
    {
        // The handler's construction table keeps a row per plot until the structure finishes
        // or dies, and that row is itself an occupancy guard. Nothing calls
        // PruneFinishedConstructions except GameLogic.Update, so if the tick were missing the
        // row would outlive its structure and the plot would stay permanently
        // FoundationOccupied. This asserts the tick by its only observable effect - and note
        // that nothing here touches the handler directly.
        var (game, men, _, orders) = CreateGame();
        men.BankAccount.Money = 2000;
        var plot = game.SpawnObject("TestPlot", men, new Vector3(300, 300, 0));
        var barracksId = BarracksDefinitionId(game);

        Dispatch(orders, Order.CreateFoundationConstruct((int)men.Id, plot.Id, barracksId));
        var structure = CastleUnpackStamper.FindStructureOnPlot(plot, game.GameEngine);
        Assert.NotNull(structure);

        // The structure dies some other way than a cancel (so the handler is never told).
        game.GameLogic.DestroyObject(structure);
        game.Step(); // GameLogic.Update prunes the stale row, then the destroy list is reaped.

        Assert.Null(CastleUnpackStamper.FindStructureOnPlot(plot, game.GameEngine));

        // Buildable again: with a stale row still in the table this would be FoundationOccupied
        // and nothing would be built.
        Dispatch(orders, Order.CreateFoundationConstruct((int)men.Id, plot.Id, barracksId));
        Assert.NotNull(CastleUnpackStamper.FindStructureOnPlot(plot, game.GameEngine));
        Assert.Equal(2000u - (2 * BarracksBuildCost), men.BankAccount.Money);
    }

    // ================================================================
    // The ledger reconciliation itself
    // ================================================================

    [Fact]
    public void CastleOrderLedger_ProductionResolverBindsToPlayerBankAccount()
    {
        // PlayerFunds.ForPlayer is the single place that decides which ledger a player's money
        // lives in, and GameLogic.CastleOrders installs exactly this resolver.
        var (_, men, _, _) = CreateGame();
        men.BankAccount.Money = 500;

        var funds = PlayerFunds.ForPlayer(men);
        Assert.NotNull(funds);
        Assert.Same(men.BankAccount, Assert.IsType<BankAccountFunds>(funds).Account);

        Assert.True(funds.CanAfford(500));
        Assert.False(funds.CanAfford(501));

        Assert.Equal(200u, funds.Withdraw(200));
        Assert.Equal(300u, men.BankAccount.Money);

        funds.Deposit(50);
        Assert.Equal(350u, men.BankAccount.Money);

        // GPL Money::withdraw clamps rather than going negative, and BankAccount agrees.
        Assert.Equal(350u, funds.Withdraw(9999));
        Assert.Equal(0u, men.BankAccount.Money);
    }

    [Fact]
    public void CastleOrderLedger_NullPlayerResolvesToNoFunds()
    {
        Assert.Null(PlayerFunds.ForPlayer(null));
    }

    [Fact]
    public void CastleOrderLedger_ResourceBankStillSatisfiesTheSameContract()
    {
        // The S4/SimPlayer ledger implements IPlayerFunds too, so the existing tests that
        // construct banks directly keep working and the eventual swap to SimPlayer is one line
        // in PlayerFunds.ForPlayer rather than a rewrite of every handler.
        IPlayerFunds funds = new ResourceBank(400);

        Assert.True(funds.CanAfford(400));
        Assert.False(funds.CanAfford(401));
        Assert.Equal(400u, funds.Withdraw(1000));
        funds.Deposit(25);
        Assert.True(funds.CanAfford(25));
    }

    // ================================================================
    // Order factories: documented argument shapes
    // ================================================================

    [Fact]
    public void CastleOrderFactories_ProduceTheDocumentedArgumentShapes()
    {
        var plot = new ObjectId(42);

        var construct = Order.CreateFoundationConstruct(2, plot, 77);
        Assert.Equal(OrderType.FoundationConstruct, construct.OrderType);
        Assert.Equal(2, construct.PlayerIndex);
        Assert.Equal(2, construct.Arguments.Count);
        Assert.Equal(OrderArgumentType.ObjectId, construct.Arguments[0].ArgumentType);
        Assert.Equal(plot, construct.Arguments[0].Value.ObjectId);
        Assert.Equal(OrderArgumentType.Integer, construct.Arguments[1].ArgumentType);
        Assert.Equal(77, construct.Arguments[1].Value.Integer);

        var cancel = Order.CreateFoundationConstructCancel(2, plot);
        Assert.Equal(OrderType.FoundationConstructCancel, cancel.OrderType);
        Assert.Equal(1, cancel.Arguments.Count);
        Assert.Equal(plot, cancel.Arguments[0].Value.ObjectId);

        var unpack = Order.CreateCastleUnpack(3, plot);
        Assert.Equal(OrderType.CastleUnpack, unpack.OrderType);
        Assert.Equal(3, unpack.PlayerIndex);
        Assert.Equal(1, unpack.Arguments.Count);
        Assert.Equal(plot, unpack.Arguments[0].Value.ObjectId);

        var pack = Order.CreateCastlePack(3, plot);
        Assert.Equal(OrderType.CastlePack, pack.OrderType);
        Assert.Equal(1, pack.Arguments.Count);
        Assert.Equal(plot, pack.Arguments[0].Value.ObjectId);
    }

    [Fact]
    public void CastleOrderTypeValues_AreEngineLocal_AndCollideWithNothing()
    {
        // The four values are appended, never renumbered over an existing member, and sit in a
        // band (2000+) outside the recovered 1000-1999 network range precisely because their
        // recovered numbers were already taken here by unrelated ZH members.
        Assert.Equal(2001, (int)OrderType.FoundationConstruct);
        Assert.Equal(2002, (int)OrderType.FoundationConstructCancel);
        Assert.Equal(2003, (int)OrderType.CastleUnpack);
        Assert.Equal(2004, (int)OrderType.CastlePack);

        // The members that already own the recovered numbers, unchanged.
        Assert.Equal(1049, (int)OrderType.BuildObject);
        Assert.Equal(1085, (int)OrderType.Unknown1085);
        Assert.Equal(1086, (int)OrderType.DirectParticleCannon);

        // ...and therefore none of the four is an alias of an existing member.
        Assert.NotEqual(OrderType.BuildObject, OrderType.FoundationConstruct);
        Assert.NotEqual(OrderType.Unknown1085, OrderType.CastleUnpack);
        Assert.NotEqual(OrderType.DirectParticleCannon, OrderType.CastlePack);
    }
}
