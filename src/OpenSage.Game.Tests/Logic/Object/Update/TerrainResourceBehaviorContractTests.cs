// Mocked-game contract tests for the TerrainResourceBehavior port (R13): the passive area-income
// tick (baseline deposit every IncomeInterval, the UpgradeBonusPercent extra when a nearby
// matching+upgraded object is present, the null-is-inert "no bonus configured" path, the sleepy
// re-arm shape, and the shadow-copy base test), plus coexistence with the already-landed R12
// TerrainResourceClientBehavior companion. See bfme2-workbench/research/modules-r13/specs/
// TerrainResourceBehaviorModuleData.md for the full behavioral grounding.
//
// SLEEPY-UPDATE CAVEAT (applies to every case below that asserts an income deposit): a freshly
// spawned UpdateModule's wake frame is not guaranteed live in the same HeadlessSimGame.Step()
// call that spawned it - GameLogic.CreateObject constructs the module (setting its initial
// NextCallFrame) before that frame's sleepy-queue pass has necessarily captured it, so the
// module's first Update() call lands on the SECOND Step() after spawn, not the first. Every case
// that expects "income granted after N intervals" calls game.Step() once extra (N + 1 total
// steps from spawn, or equivalently: Spawn() counts as frame 0, first Step() reaches frame 1
// without guaranteeing the module ran, second Step() (frame 2) is the first frame the module's
// Update() is guaranteed to have executed).

using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class TerrainResourceBehaviorContractTests
{
    // IncomeInterval 1000 ms -> 5 frames at the frozen 5 Hz (F6); Radius 50.
    private const string Definitions = @"
Upgrade SomeUpgrade
  Type = PLAYER
End

Object ResourceMarker
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = TerrainResourceBehavior ModuleTag_ServerResource
    Radius = 50
    MaxIncome = 1000
    IncomeInterval = 1000
  End
  ClientBehavior = TerrainResourceClientBehavior ModuleTag_ClientResource
  End
End

Object BonusResourceMarker
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = TerrainResourceBehavior ModuleTag_ServerResource
    Radius = 50
    MaxIncome = 1000
    IncomeInterval = 1000
    Upgrade = SomeUpgrade
    UpgradeBonusPercent = 50%
    UpgradeMustBePresent = NONE +STRUCTURE
  End
End

Object UpgradeBonusStructure
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object NonStructureCandidate
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xC71)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static void StepFrames(HeadlessSimGame game, int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            game.Step();
        }
    }

    private static TerrainResourceBehavior ModuleOf(GameObject gameObject) =>
        gameObject.FindBehavior<TerrainResourceBehavior>();

    private static TerrainResourceBehaviorModuleData DataOf(HeadlessSimGame game, string definitionName) =>
        (TerrainResourceBehaviorModuleData)game.AssetStore.ObjectDefinitions
            .GetByName(definitionName).Behaviors["ModuleTag_ServerResource"].Data;

    [Fact]
    public void ParserAssignsAllFields()
    {
        var game = NewGame();

        var data = DataOf(game, "ResourceMarker");

        Assert.Equal(50, data.Radius);
        Assert.Equal(1000, data.MaxIncome);
        // ms -> frame quantization goes through the same ParseDurationLogicFrames() the
        // AttributeModifierAuraUpdate/EmpUpdate ports already exercise: 1000ms -> 5 frames at
        // the frozen 5 Hz (F6), same conversion those sibling tests hardcode.
        Assert.Equal(5u, data.IncomeInterval.Value);
    }

    [Fact]
    public void BaselineIncomeGrantedEveryInterval_NoUpgradeConfigured()
    {
        var game = NewGame();
        var owner = game.CivilianPlayer;
        owner.BankAccount.Money = 0;

        var marker = game.SpawnObject("ResourceMarker", owner, Vector3.Zero);
        var interval = DataOf(game, "ResourceMarker").IncomeInterval.Value;

        // Sleepy-update caveat: N intervals' worth of deposits need N+1 Steps from spawn.
        StepFrames(game, 2 * interval + 1);

        Assert.Equal(2u * 1000u, owner.BankAccount.Money);
        Assert.NotNull(ModuleOf(marker));
    }

    [Fact]
    public void BonusAppliedWhenMatchingUpgradedObjectWithinRadius()
    {
        var game = NewGame();
        var owner = game.CivilianPlayer;
        owner.BankAccount.Money = 0;

        game.SpawnObject("BonusResourceMarker", owner, Vector3.Zero);
        var nearby = game.SpawnObject("UpgradeBonusStructure", owner, new Vector3(10, 0, 0));
        owner.AddUpgrade(game.AssetStore.Upgrades.GetByName("SomeUpgrade"), UpgradeStatus.Completed);

        var interval = DataOf(game, "BonusResourceMarker").IncomeInterval.Value;
        StepFrames(game, 2 * interval + 1);

        // Uncapped-extra reading (spec F-TRB-1): base + (int)(base * 0.5), twice.
        Assert.Equal(2u * (1000u + 500u), owner.BankAccount.Money);
        Assert.NotNull(nearby);
    }

    [Fact]
    public void NoBonusWhenMatchingObjectOutsideRadius()
    {
        var game = NewGame();
        var owner = game.CivilianPlayer;
        owner.BankAccount.Money = 0;

        game.SpawnObject("BonusResourceMarker", owner, Vector3.Zero);
        game.SpawnObject("UpgradeBonusStructure", owner, new Vector3(100, 0, 0));
        owner.AddUpgrade(game.AssetStore.Upgrades.GetByName("SomeUpgrade"), UpgradeStatus.Completed);

        var interval = DataOf(game, "BonusResourceMarker").IncomeInterval.Value;
        StepFrames(game, 2 * interval + 1);

        Assert.Equal(2u * 1000u, owner.BankAccount.Money);
    }

    [Fact]
    public void NoBonusWhenNearbyObjectFailsFilter()
    {
        var game = NewGame();
        var owner = game.CivilianPlayer;
        owner.BankAccount.Money = 0;

        game.SpawnObject("BonusResourceMarker", owner, Vector3.Zero);
        game.SpawnObject("NonStructureCandidate", owner, new Vector3(10, 0, 0));
        owner.AddUpgrade(game.AssetStore.Upgrades.GetByName("SomeUpgrade"), UpgradeStatus.Completed);

        var interval = DataOf(game, "BonusResourceMarker").IncomeInterval.Value;
        StepFrames(game, 2 * interval + 1);

        Assert.Equal(2u * 1000u, owner.BankAccount.Money);
    }

    [Fact]
    public void NoBonusWhenNearbyMatchingObjectLacksUpgrade()
    {
        var game = NewGame();
        var owner = game.CivilianPlayer;
        owner.BankAccount.Money = 0;

        game.SpawnObject("BonusResourceMarker", owner, Vector3.Zero);
        game.SpawnObject("UpgradeBonusStructure", owner, new Vector3(10, 0, 0));
        // No upgrade granted.

        var interval = DataOf(game, "BonusResourceMarker").IncomeInterval.Value;
        StepFrames(game, 2 * interval + 1);

        Assert.Equal(2u * 1000u, owner.BankAccount.Money);
    }

    [Fact]
    public void ModuleSleepsBetweenTicks()
    {
        var game = NewGame();
        var owner = game.CivilianPlayer;
        owner.BankAccount.Money = 0;

        game.SpawnObject("ResourceMarker", owner, Vector3.Zero);
        var interval = DataOf(game, "ResourceMarker").IncomeInterval.Value;

        // One frame short of the second interval boundary: only the first guaranteed tick
        // has fired, proving the module re-arms via UpdateSleepTime.Frames(IncomeInterval)
        // rather than ticking every frame like EmpUpdate does.
        StepFrames(game, 2 * interval);

        Assert.Equal(1000u, owner.BankAccount.Money);
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc()
    {
        var game = NewGame();
        var owner = game.CivilianPlayer;

        // Reuse the module instances GameLogic.CreateObject already wired up (rather than a
        // second manual CreateModule call on the same host, which would double-enroll the
        // host in the sleepy-update queue via this module's ctor SetWakeFrame call) - the same
        // ModuleOf(...) idiom AttributeModifierAuraUpdateContractTests.ShadowCopy_
        // CrcEqualsLiveCrc_MidBehavior uses for a real (auto-instantiated) Behavior module.
        var liveHost = game.SpawnObject("ResourceMarker", owner, Vector3.Zero);
        var live = ModuleOf(liveHost);

        var shadowHost = game.SpawnObject("ResourceMarker", owner, new Vector3(50, 0, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void CoexistsWithClientBehaviorCounterpart()
    {
        var game = NewGame();
        var marker = game.SpawnObject("ResourceMarker", game.CivilianPlayer, Vector3.Zero);

        var definition = game.AssetStore.ObjectDefinitions.GetByName("ResourceMarker");
        Assert.True(definition.Behaviors.ContainsKey("ModuleTag_ServerResource"));
        Assert.True(definition.ClientBehaviors.ContainsKey("ModuleTag_ClientResource"));

        var serverModule = marker.FindBehavior<TerrainResourceBehavior>();
        Assert.NotNull(serverModule);

        var clientData = (TerrainResourceClientBehaviorData)definition.ClientBehaviors["ModuleTag_ClientResource"].Data;
        var clientModule = clientData.CreateModule(marker, game.GameEngine);
        Assert.NotNull(clientModule);
        Assert.NotSame(serverModule, clientModule);

        clientModule.Dispose();
    }
}
