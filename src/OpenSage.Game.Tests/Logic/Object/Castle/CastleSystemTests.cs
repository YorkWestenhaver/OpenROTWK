// R9 castles system tests (spec-castles.md; template v1.1 shape - HeadlessSimGame + INI
// text per branch + shadow-copy + mid-state save/load continuation).
//
// The headless host has no .bse files, so tests inject an ICastleTemplateProvider; the
// production BseCastleTemplateProvider is exercised by the real game path only (the .bse
// chunk readers themselves are covered by the map-layer tests).

using System.Collections.Generic;
using System.Numerics;
using OpenSage.Data.Map;
using OpenSage.Logic;
using OpenSage.Logic.Economy;
using OpenSage.Logic.Object;
using OpenSage.Logic.Object.Castle;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Orders;
using Xunit;
using MapPlayer = OpenSage.Data.Map.Player;
using Player = OpenSage.Logic.Player;

namespace OpenSage.Tests.Logic.Object.Castle;

public class CastleSystemTests
{
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
    CastleToUnpackForFaction = Dwarves DwarfCamp 400
    ScanDistance = 120.0
    FadeTime = 2.0
    UnpackDelayTime = 1.0
    KeepDeathKillsEverything = Yes
    TransferFoundationHealthToCastleUponUnpack = Yes
    MaxCastleRadius = 250.0
    BuildTime = 4.0
    DecalName = TestDecal
    DecalSize = 40.0
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

Object TestWall
  KindOf = STRUCTURE
  Geometry = CYLINDER
  GeometryMajorRadius = 5.0
  GeometryHeight = 5.0
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 300
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
        public string LastRequestedCamp;

        public IReadOnlyList<CastleMemberPlacement> GetPlacements(string campName)
        {
            LastRequestedCamp = campName;
            if (campName != "TestCamp")
            {
                return null;
            }

            return new[]
            {
                new CastleMemberPlacement { TemplateName = "TestKeep", Offset = Vector3.Zero, Angle = 0f },
                new CastleMemberPlacement { TemplateName = "TestWall", Offset = new Vector3(30, 0, 0), Angle = 0f },
            };
        }
    }

    private static HeadlessSimGame CreateGame()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2);
        game.LoadIniText(CastleIni);
        return game;
    }

    /// <summary>A headless game with a "Men" faction player appended to the roster.</summary>
    private static (HeadlessSimGame Game, Player MenPlayer) CreateGameWithMenPlayer()
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
        return (game, game.PlayerManager.Players[2]);
    }

    private static CastleBehavior SpawnCamp(HeadlessSimGame game, Player owner, out GameObject foundation)
    {
        foundation = game.SpawnObject("CampFoundation", owner, Vector3.Zero);
        var castle = foundation.FindBehavior<CastleBehavior>();
        castle.TemplateProvider = new FakeTemplateProvider();
        return castle;
    }

    // ================================================================
    // §3.1 parse table
    // ================================================================

    [Fact]
    public void ParseTable_FullRetailTable_IncludingTheThreeRecoveredFields()
    {
        var game = CreateGame();
        var definition = game.AssetStore.ObjectDefinitions.GetByName("CampFoundation");
        var data = (CastleBehaviorModuleData)definition.Behaviors["ModuleTag_Castle"].Data;

        Assert.Equal(2, data.CastleToUnpackForFactions.Count);
        Assert.Equal("Men", data.CastleToUnpackForFactions[0].FactionName);
        Assert.Equal("TestCamp", data.CastleToUnpackForFactions[0].Camp);
        Assert.Equal(500, data.CastleToUnpackForFactions[0].UnpackCost);

        // ScanDistance is a REAL (Fix64), not the old guessed int/100.
        Assert.Equal(new Fix64(120), data.ScanDistance);

        // Seconds are frame-quantized at parse (ceil, 5 Hz).
        Assert.Equal(new LogicFrameSpan(10), data.FadeTimeFrames);
        Assert.Equal(new LogicFrameSpan(5), data.UnpackDelayTimeFrames);

        // The three fields missing from the old parser.
        Assert.Equal(new Fix64(4), data.BuildTime);
        Assert.Equal(new LogicFrameSpan(20), data.BuildTimeFrames);
        Assert.Equal("TestDecal", data.DecalName);
        Assert.True(data.TransferFoundationHealthToCastleUponUnpack);

        Assert.True(data.KeepDeathKillsEverything);
        Assert.Equal(new Fix64(250), data.MaxCastleRadius);
        Assert.Equal(new Fix64(40), data.DecalSize);
    }

    [Fact]
    public void ParseTable_ScanDistanceDefaultsToZero_DisablingCapture()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2);
        game.LoadIniText(@"
Object BareCamp
  KindOf = STRUCTURE BASE_FOUNDATION
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = CastleBehavior ModuleTag_Castle
    CastleToUnpackForFaction = Men TestCamp
  End
End");
        var data = (CastleBehaviorModuleData)game.AssetStore.ObjectDefinitions.GetByName("BareCamp").Behaviors["ModuleTag_Castle"].Data;
        Assert.Equal(Fix64.Zero, data.ScanDistance);      // Q2 default
        Assert.Equal(0, data.CastleToUnpackForFactions[0].UnpackCost);
    }

    // ================================================================
    // §5.2/§5.3 unpack
    // ================================================================

    [Fact]
    public void InstantUnpack_StampsMembers_SetsBackRefs_TransfersState()
    {
        var (game, men) = CreateGameWithMenPlayer();
        var castle = SpawnCamp(game, game.CivilianPlayer, out var foundation);

        castle.Unpack(men, instant: true);

        Assert.Equal(CastleState.Unpacked, castle.State);
        Assert.True(castle.IsUnpacked);
        Assert.Equal(2, castle.MemberIds.Count);

        // Foundation handed to the unpacking player and hidden.
        Assert.Equal(men, foundation.Owner);
        Assert.True(foundation.Hidden);
        Assert.False(foundation.IsSelectable);

        // Members owned by the player; the keep is the anchor; back-refs written.
        var keep = game.GameLogic.GetObjectById(castle.CastleAnchorId);
        Assert.NotNull(keep);
        Assert.True(keep.Definition.KindOf.Get(ObjectKinds.CastleKeep));
        Assert.Equal(men, keep.Owner);

        var member = keep.FindBehavior<CastleMemberBehavior>();
        Assert.Equal(foundation.Id, member.CastleObjectId);
        // Native player = the foundation's spawn owner (civilian, roster index 1).
        Assert.Equal(1, member.NativePlayerIndex);
    }

    [Fact]
    public void DeferredUnpack_WaitsUnpackDelayTime()
    {
        var (game, men) = CreateGameWithMenPlayer();
        var castle = SpawnCamp(game, men, out _);

        castle.InitiateUnpack(men, explicitEntryIndex: -1, instant: false);
        Assert.Equal(CastleState.UnpackInitiated, castle.State);

        // UnpackDelayTime = 1.0 s = 5 frames (Q1 pin: UnpackDelayTime drives the delay).
        // The module update for frame N runs during the (N+1)th Step, so the unpack lands
        // on the 6th Step.
        for (var i = 0; i < 5; i++)
        {
            Assert.NotEqual(CastleState.Unpacked, castle.State);
            game.Step();
        }

        game.Step();
        Assert.Equal(CastleState.Unpacked, castle.State);
        Assert.Equal(2, castle.MemberIds.Count);
    }

    // ================================================================
    // §5.6 order guard sequence + S4 economy
    // ================================================================

    [Fact]
    public void UnpackOrder_GuardSequence_InRetailOrder()
    {
        var (game, men) = CreateGameWithMenPlayer();
        var orcs = game.PlayerManager.Players[3];
        var castle = SpawnCamp(game, men, out var foundation);

        var banks = new Dictionary<Player, ResourceBank> { [men] = new ResourceBank(100) };
        var handler = new CastleOrderHandler(game.GameEngine, p => banks.TryGetValue(p, out var b) ? b : null);

        // Guard 1: object exists.
        Assert.Equal(CastleOrderResult.NoSuchObject,
            handler.HandleCastleUnpack(men, new ObjectId(9999)));

        // Guard 2: issuing player must own the object.
        Assert.Equal(CastleOrderResult.NotOwner,
            handler.HandleCastleUnpack(orcs, foundation.Id));

        // Guard 3: the object must carry a CastleBehavior.
        var soldier = game.SpawnObject("TestSoldier", men, new Vector3(500, 0, 0));
        Assert.Equal(CastleOrderResult.NoCastleBehavior,
            handler.HandleCastleUnpack(men, soldier.Id));

        // Guard 6: affordability - 100 < 500, and the bank is untouched by the failure.
        Assert.Equal(CastleOrderResult.CannotAfford,
            handler.HandleCastleUnpack(men, foundation.Id));
        Assert.Equal(100u, banks[men].Money);

        // Funded: the charge is exactly the matched entry's UnpackCost.
        banks[men].Deposit(900);
        Assert.Equal(CastleOrderResult.Ok,
            handler.HandleCastleUnpack(men, foundation.Id));
        Assert.Equal(500u, banks[men].Money);
        Assert.Equal(CastleState.UnpackInitiated, castle.State);

        // Guard 4 now rejects a second unpack (no longer packed).
        Assert.Equal(CastleOrderResult.CannotUnpack,
            handler.HandleCastleUnpack(men, foundation.Id));
    }

    [Fact]
    public void PackOrder_KillsMembers_RestoresFoundationAfterFadeTime()
    {
        var (game, men) = CreateGameWithMenPlayer();
        var castle = SpawnCamp(game, men, out var foundation);
        castle.Unpack(men, instant: true);
        var keepId = castle.CastleAnchorId;

        var handler = new CastleOrderHandler(game.GameEngine, _ => null);
        Assert.Equal(CastleOrderResult.Ok, handler.HandleCastlePack(men, foundation.Id));
        Assert.Equal(CastleState.Packing, castle.State);

        // FadeTime = 2.0 s = 10 frames; then the foundation is restored, capturable, civilian.
        for (var i = 0; i < 11 && castle.State == CastleState.Packing; i++)
        {
            game.Step();
        }

        Assert.Equal(CastleState.Packed, castle.State);
        Assert.False(foundation.Hidden);
        Assert.True(foundation.IsSelectable);
        Assert.Equal(game.CivilianPlayer, foundation.Owner);
        Assert.Null(game.GameLogic.GetObjectById(keepId));
    }

    // ================================================================
    // §5.7 keep-death cascade
    // ================================================================

    [Fact]
    public void KeepDeath_WithKeepDeathKillsEverything_PacksTheCastle()
    {
        var (game, men) = CreateGameWithMenPlayer();
        var castle = SpawnCamp(game, men, out _);
        castle.Unpack(men, instant: true);

        var keep = game.GameLogic.GetObjectById(castle.CastleAnchorId);
        PortedModuleTestKit.TriggerDeath(keep);

        // The member's OnDie pushed the cascade: the castle is packing.
        Assert.Equal(CastleState.Packing, castle.State);
    }

    // ================================================================
    // §5.4 capture scan
    // ================================================================

    [Fact]
    public void CaptureScan_HandsCampToTheOnlyFactionPresent()
    {
        var (game, men) = CreateGameWithMenPlayer();
        var castle = SpawnCamp(game, game.CivilianPlayer, out var foundation);

        game.SpawnObject("TestSoldier", men, new Vector3(50, 0, 0));

        game.Step();
        game.Step();

        Assert.Equal(men, foundation.Owner);
        Assert.Equal(CastleState.Packed, castle.State); // captured, still packed
    }

    [Fact]
    public void CaptureScan_EnemyContestBlocksCapture()
    {
        var (game, men) = CreateGameWithMenPlayer();
        var orcs = game.PlayerManager.Players[3];
        men.AddEnemy(orcs);
        orcs.AddEnemy(men);

        var castle = SpawnCamp(game, game.CivilianPlayer, out var foundation);
        castle.TemplateProvider = new FakeTemplateProvider();

        game.SpawnObject("TestSoldier", men, new Vector3(50, 0, 0));
        game.SpawnObject("TestSoldier", orcs, new Vector3(-50, 0, 0));

        // Neither ever wins while both are present... but neither is an enemy of the
        // CIVILIAN owner, so the first tally CAN hand the camp over. Make them mutual
        // enemies of the owner by capturing first: men grabs it alone...
        game.Step();
        game.Step();
        var ownerAfterFirstScan = foundation.Owner;

        // ...and once one faction owns it, the other faction's presence contests every
        // later scan: ownership can no longer flip while both armies stand there.
        game.Step();
        game.Step();
        Assert.Equal(ownerAfterFirstScan, foundation.Owner);
    }

    [Fact]
    public void CaptureScan_EmptyScanRevertsToCivilianAfterGrace()
    {
        var (game, men) = CreateGameWithMenPlayer();
        var castle = SpawnCamp(game, men, out var foundation);
        Assert.Equal(men, foundation.Owner);

        // Nobody in range; past frame 5 the camp reverts to the CIVILIAN player (Q3:
        // retail's PlyrCivilian, not the spawn owner).
        for (var i = 0; i < 8; i++)
        {
            game.Step();
        }

        Assert.Equal(game.CivilianPlayer, foundation.Owner);
    }

    [Fact]
    public void CaptureTally_WeightsAndTies_AreDeterministic()
    {
        // One real unit (w=2) beats one structure (w=1).
        var result = CastleCaptureScan.Tally(stackalloc CaptureCandidate[]
        {
            new(playerIndex: 3, isRealUnit: false, templateCaptureBonus: 0, isEnemyOfCurrentOwner: false),
            new(playerIndex: 4, isRealUnit: true, templateCaptureBonus: 0, isEnemyOfCurrentOwner: false),
        });
        Assert.Equal(4, result.WinnerPlayerIndex);
        Assert.False(result.EnemyContest);

        // Capture bonus multiplies by the unit weight: 1*(1+2*1)... structure with bonus 2
        // scores 1 + 2*1 = 3, beating one plain real unit's 2.
        result = CastleCaptureScan.Tally(stackalloc CaptureCandidate[]
        {
            new(3, false, 2, false),
            new(4, true, 0, false),
        });
        Assert.Equal(3, result.WinnerPlayerIndex);

        // Exact tie: the LOWEST player index wins (pinned, F-CAS-5).
        result = CastleCaptureScan.Tally(stackalloc CaptureCandidate[]
        {
            new(7, true, 0, false),
            new(2, true, 0, false),
        });
        Assert.Equal(2, result.WinnerPlayerIndex);

        // Enemy presence is reported even when the enemy is outnumbered.
        result = CastleCaptureScan.Tally(stackalloc CaptureCandidate[]
        {
            new(2, true, 0, false),
            new(2, true, 0, false),
            new(7, true, 0, true),
        });
        Assert.Equal(2, result.WinnerPlayerIndex);
        Assert.True(result.EnemyContest);

        // Empty scan: no winner.
        Assert.False(CastleCaptureScan.Tally(default).AnyCandidates);
    }

    // ================================================================
    // §5.8 critter geometry (pure)
    // ================================================================

    [Fact]
    public void CritterScareTarget_Is150AlongTheAwayDirection()
    {
        var animal = new FixVector3(new Fix64(100), Fix64.Zero, Fix64.Zero);
        var keep = new FixVector3(Fix64.Zero, Fix64.Zero, Fix64.Zero);

        var target = CastleMath.ComputeCritterScareTarget(animal, keep);
        Assert.Equal(new Fix64(250), target.X);
        Assert.Equal(Fix64.Zero, target.Y);

        // Degenerate: animal exactly at the keep stays put.
        var degenerate = CastleMath.ComputeCritterScareTarget(keep, keep);
        Assert.Equal(keep, degenerate);
    }

    // ================================================================
    // §5.9 foundation construct + cancel (S4 economy)
    // ================================================================

    [Fact]
    public void FoundationConstruct_ChargesSpawnsAndOccupies_CancelRefunds()
    {
        var (game, men) = CreateGameWithMenPlayer();
        var plot = game.SpawnObject("TestPlot", men, new Vector3(300, 300, 0));

        var bank = new ResourceBank(1000);
        var handler = new CastleOrderHandler(game.GameEngine, _ => bank);

        // Template must be NEED_BASE_FOUNDATION.
        Assert.Equal(CastleOrderResult.TemplateNotBuildableOnFoundation,
            handler.HandleFoundationConstruct(men, plot.Id, "TestSoldier"));

        // The purchase: BuildCost 300 withdrawn, structure standing on the plot, building.
        Assert.Equal(CastleOrderResult.Ok,
            handler.HandleFoundationConstruct(men, plot.Id, "TestBarracks"));
        Assert.Equal(700u, bank.Money);

        var structure = CastleUnpackStamper.FindStructureOnPlot(plot, game.GameEngine);
        Assert.NotNull(structure);
        Assert.Equal("TestBarracks", structure.Definition.Name);
        Assert.Equal(men, structure.Owner);
        Assert.True(structure.IsBeingConstructed());

        // Socket now occupied.
        Assert.Equal(CastleOrderResult.FoundationOccupied,
            handler.HandleFoundationConstruct(men, plot.Id, "TestBarracks"));

        // Cancel during construction: full refund (F-CAS-3 pin), structure removed.
        Assert.Equal(CastleOrderResult.Ok,
            handler.HandleFoundationConstructCancel(men, plot.Id));
        Assert.Equal(1000u, bank.Money);

        game.Step(); // reap the destroyed structure
        Assert.Null(CastleUnpackStamper.FindStructureOnPlot(plot, game.GameEngine));

        // The plot is buildable again.
        Assert.Equal(CastleOrderResult.Ok,
            handler.HandleFoundationConstruct(men, plot.Id, "TestBarracks"));
    }

    [Fact]
    public void FoundationConstruct_GuardsOwnerAndFoundationKind()
    {
        var (game, men) = CreateGameWithMenPlayer();
        var orcs = game.PlayerManager.Players[3];
        var plot = game.SpawnObject("TestPlot", men, new Vector3(300, 300, 0));
        var soldier = game.SpawnObject("TestSoldier", men, new Vector3(600, 600, 0));

        var handler = new CastleOrderHandler(game.GameEngine, _ => new ResourceBank(1000));

        Assert.Equal(CastleOrderResult.NotOwner,
            handler.HandleFoundationConstruct(orcs, plot.Id, "TestBarracks"));
        Assert.Equal(CastleOrderResult.NotAFoundation,
            handler.HandleFoundationConstruct(men, soldier.Id, "TestBarracks"));
        Assert.Equal(CastleOrderResult.NothingToCancel,
            handler.HandleFoundationConstructCancel(men, plot.Id));
    }

    // ================================================================
    // Xfer: shadow-copy + mid-state save/load continuation
    // ================================================================

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var (game, men) = CreateGameWithMenPlayer();

        // Live castle mid-flight: deferred unpack two frames in.
        var live = SpawnCamp(game, men, out _);
        live.InitiateUnpack(men, explicitEntryIndex: -1, instant: false);
        game.Step();
        game.Step();

        // Differently-stated shadow on a second foundation.
        var shadow = SpawnCamp(game, game.CivilianPlayer, out _);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidStateSaveLoad_PackingCountdown_ContinuesIdentically()
    {
        var (game, men) = CreateGameWithMenPlayer();

        var live = SpawnCamp(game, men, out var liveFoundation);
        live.Unpack(men, instant: true);
        live.InitiatePack();
        game.Step();
        game.Step();
        Assert.Equal(CastleState.Packing, live.State);

        // Save mid-countdown; load into a shadow module on a second foundation.
        var state = PortedModuleTestKit.Save(live);
        var shadow = SpawnCamp(game, game.CivilianPlayer, out var shadowFoundation);
        PortedModuleTestKit.Load(shadow, state);

        Assert.Equal(CastleState.Packing, shadow.State);
        Assert.Equal(PortedModuleTestKit.LiveCrc(live), PortedModuleTestKit.LiveCrc(shadow));

        // Both countdowns expire on the same frame and both castles land Packed.
        for (var i = 0; i < 12 && (live.State == CastleState.Packing || shadow.State == CastleState.Packing); i++)
        {
            game.Step();
        }

        Assert.Equal(CastleState.Packed, live.State);
        Assert.Equal(CastleState.Packed, shadow.State);
        Assert.Equal(PortedModuleTestKit.LiveCrc(live), PortedModuleTestKit.LiveCrc(shadow));
    }

    // ================================================================
    // §3.3 CastleUpgrade distribution
    // ================================================================

    [Fact]
    public void CastleUpgrade_DistributesUpgradeToAllMembers()
    {
        var (game, men) = CreateGameWithMenPlayer();
        game.LoadIniText(@"
Upgrade Upgrade_TestTrigger
  Type = OBJECT
End

Upgrade Upgrade_TestStonework
  Type = OBJECT
End

Object UpgradableCamp
  KindOf = STRUCTURE SELECTABLE IMMOBILE BASE_FOUNDATION CASTLE_CENTER
  Geometry = CYLINDER
  GeometryMajorRadius = 20.0
  GeometryHeight = 10.0
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 200
  End
  Behavior = CastleBehavior ModuleTag_Castle
    CastleToUnpackForFaction = Men TestCamp 0
  End
  Behavior = CastleUpgrade ModuleTag_CU
    TriggeredBy = Upgrade_TestTrigger
    Upgrade = Upgrade_TestStonework
  End
End");

        var foundation = game.SpawnObject("UpgradableCamp", men, Vector3.Zero);
        var castle = foundation.FindBehavior<CastleBehavior>();
        castle.TemplateProvider = new FakeTemplateProvider();
        castle.Unpack(men, instant: true);
        Assert.Equal(2, castle.MemberIds.Count);

        var trigger = game.AssetStore.Upgrades.GetByName("Upgrade_TestTrigger");
        var stonework = game.AssetStore.Upgrades.GetByName("Upgrade_TestStonework");

        foundation.Upgrade(trigger);

        foreach (var memberId in castle.MemberIds)
        {
            var member = game.GameLogic.GetObjectById(memberId);
            Assert.True(member.HasUpgrade(stonework));
        }
    }
}
