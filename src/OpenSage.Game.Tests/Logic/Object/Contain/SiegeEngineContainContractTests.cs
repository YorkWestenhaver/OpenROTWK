// Contract tests for the SiegeEngineContain R13 port
// (modules-r13/specs/SiegeEngineContainModuleData.md): capacity/relationship gates (TC-1..4),
// ExitDelay spacing (TC-5), NumberOfExitPaths index determinism and RNG draw discipline
// (TC-6), once-only death damage (TC-7), GoAggressiveOnExit (TC-8), the held-field
// documentation test (TC-9), and the shared shadow-copy/save-load base tests (TC-10).
//
// Definitions parse from INI text through the real parser, so the audited quantizing parse
// functions (ParseFix64Percentage, ParseDurationLogicFrames) are on the tested path.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Ticking;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Contain;

public class SiegeEngineContainContractTests
{
    private const string Definitions = @"
Object SiegeRider
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AIUpdateInterface ModuleTag_AI
  End
End

Object SiegeRam                    ; the workhorse fixture (real corpus shape)
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 200
  End
  Behavior = SiegeEngineContain ModuleTag_Contain
    Slots                 = 2
    DamagePercentToUnits  = 50%
    AllowAlliesInside     = Yes
    AllowEnemiesInside    = No
    AllowNeutralInside    = No
    ExitDelay             = 500        ; ceil(500*5/1000) = 3 frames
    NumberOfExitPaths     = 0
    GoAggressiveOnExit    = Yes
    ; held fields present on purpose, to prove they parse and stay inert:
    ObjectStatusOfCrew    = UNSELECTABLE UNATTACKABLE
    PassengerFilter       = NONE +CAN_RIDE_BATTERING_RAM
    KillPassengersOnDeath = Yes
    CrewFilter            = NONE +INFANTRY
    CrewMax               = 6
    InitialCrew           = SiegeRider 6
    TypeOneForWeaponSet   = CAN_RIDE_BATTERING_RAM
    SpeedPercentPerCrew   = 50%
    EjectPassengersOnDeath = Yes
    ShowPips              = No
  End
End

Object SiegeRamZeroDamage
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 200
  End
  Behavior = SiegeEngineContain ModuleTag_Contain
    Slots                 = 2
    DamagePercentToUnits  = 0%
    AllowAlliesInside     = Yes
    ExitDelay             = 500
  End
End

Object SiegeRamMultiPath           ; the RNG branch (no shipping content exercises it)
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 200
  End
  Behavior = SiegeEngineContain ModuleTag_Contain
    Slots             = 4
    NumberOfExitPaths = 3
    ExitDelay         = 0
    AllowAlliesInside = Yes
  End
End

Object SiegeRamDefaults            ; NO Allow*Inside / NumberOfExitPaths keys at all
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 200
  End
  Behavior = SiegeEngineContain ModuleTag_Contain
    Slots = 1
  End
End

Object SiegeGrond                  ; grond.ini shape: Slots = 0
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 200
  End
  Behavior = SiegeEngineContain ModuleTag_Contain
    Slots                = 0
    DamagePercentToUnits = 100%
    AllowAlliesInside    = No
    AllowEnemiesInside   = No
    AllowNeutralInside   = No
  End
End

Object SiegeGrondTwoSlots          ; SiegeGrond shape but with a nonzero capacity, so the
  KindOf = VEHICLE                 ; 100%-death case has occupants to kill (TC-7b)
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 200
  End
  Behavior = SiegeEngineContain ModuleTag_Contain
    Slots                = 2
    DamagePercentToUnits = 100%
    AllowAlliesInside    = Yes
    AllowEnemiesInside   = No
    AllowNeutralInside   = No
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0x9052)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static SiegeEngineContain ContainOf(GameObject obj) =>
        obj.BehaviorModules.OfType<SiegeEngineContain>().Single();

    private static SiegeEngineContainModuleData DataOf(GameObject obj) =>
        obj.Definition.Behaviors.Values
            .Select(v => v.Data)
            .OfType<SiegeEngineContainModuleData>()
            .Single();

    /// <summary>
    /// Gives every object in <paramref name="objects"/> a real (non-null) Team under
    /// CivilianPlayer, and pins CivilianPlayer as its own ally - so that
    /// GameObject.GetRelationship resolves to Allies between them instead of the Neutral
    /// default (GameObject.GetRelationship short-circuits to Neutral whenever either side's
    /// Team is null, and Player.GetRelationship itself defaults to Neutral with no override).
    /// Used by every test whose fixture object sets AllowNeutralInside = No (the real corpus
    /// value on SiegeRam/SiegeGrondTwoSlots) but is not itself testing the relationship gate.
    /// </summary>
    private static void MakeAllied(HeadlessSimGame game, params GameObject[] objects)
    {
        game.CivilianPlayer.SetRelationship(game.CivilianPlayer, RelationshipType.Allies);
        var teamId = 900u;
        foreach (var obj in objects)
        {
            obj.Team = new Team(new TeamTemplate(game.TeamFactory, teamId, $"AlliedTeam{teamId}", game.CivilianPlayer, isSingleton: true), teamId);
            teamId++;
        }
    }

    /// <summary>
    /// Steps <paramref name="game"/> until <paramref name="condition"/> holds (or fails the
    /// test if it never does within <paramref name="maxSteps"/>). Needed because a freshly
    /// constructed UpdateModule's SetWakeFrame(None) schedules its FIRST tick one frame after
    /// construction (UpdateModule.SetWakeFrame: now + FrameSpan.One), so the exact game.Step()
    /// call on which SiegeEngineContain.Update() first runs - and therefore drains a queued
    /// exit or applies death damage - is an engine-scheduling detail these tests do not pin,
    /// only the eventual outcome.
    /// </summary>
    private static void StepUntil(HeadlessSimGame game, System.Func<bool> condition, int maxSteps = 30)
    {
        for (var i = 0; i < maxSteps && !condition(); i++)
        {
            game.Step();
        }
        Assert.True(condition(), $"condition not reached within {maxSteps} steps");
    }

    // ---- TC-1..3: capacity gate ----

    [Fact]
    public void Capacity_RefusesAtSlotsPlusOne()
    {
        var game = NewGame();
        var ram = game.SpawnObject("SiegeRam", game.CivilianPlayer, Vector3.Zero);
        var contain = ContainOf(ram);
        var riders = new[]
        {
            game.SpawnObject("SiegeRider", game.CivilianPlayer, Vector3.Zero),
            game.SpawnObject("SiegeRider", game.CivilianPlayer, Vector3.Zero),
            game.SpawnObject("SiegeRider", game.CivilianPlayer, Vector3.Zero),
        };
        MakeAllied(game, new[] { ram }.Concat(riders).ToArray());

        Assert.True(contain.TryAddOccupant(riders[0]));
        Assert.True(contain.TryAddOccupant(riders[1]));
        Assert.False(contain.TryAddOccupant(riders[2]));

        Assert.Equal(2, contain.OccupiedSlots);
        Assert.True(contain.IsFull);
        Assert.Equal(new[] { riders[0].Id, riders[1].Id }, contain.OccupantIds);
    }

    [Fact]
    public void Capacity_ZeroSlots_AdmitsNobody()
    {
        var game = NewGame();
        var grond = game.SpawnObject("SiegeGrond", game.CivilianPlayer, Vector3.Zero);
        var contain = ContainOf(grond);
        var rider = game.SpawnObject("SiegeRider", game.CivilianPlayer, Vector3.Zero);

        Assert.False(contain.TryAddOccupant(rider));
        Assert.Equal(0, contain.OccupiedSlots);
        Assert.Equal(0, contain.TotalSlots);
    }

    [Fact]
    public void Capacity_DuplicateAdd_IsRefused()
    {
        var game = NewGame();
        var ram = game.SpawnObject("SiegeRam", game.CivilianPlayer, Vector3.Zero);
        var contain = ContainOf(ram);
        var rider = game.SpawnObject("SiegeRider", game.CivilianPlayer, Vector3.Zero);
        MakeAllied(game, ram, rider);

        Assert.True(contain.TryAddOccupant(rider));
        Assert.False(contain.TryAddOccupant(rider));
        Assert.Equal(1, contain.OccupiedSlots);
    }

    // ---- TC-4: relationship gate matrix ----

    [Fact]
    public void Relationship_Ally_Accepted()
    {
        var game = NewGame();
        var ram = game.SpawnObject("SiegeRam", game.CivilianPlayer, Vector3.Zero);
        var contain = ContainOf(ram);
        var rider = game.SpawnObject("SiegeRider", game.PlayerManager.NeutralPlayer, Vector3.Zero);

        game.PlayerManager.NeutralPlayer.SetRelationship(game.CivilianPlayer, RelationshipType.Allies);
        ram.Team = new Team(new TeamTemplate(game.TeamFactory, 801, "SiegeTeam", game.CivilianPlayer, isSingleton: true), 801);
        rider.Team = new Team(new TeamTemplate(game.TeamFactory, 802, "RiderTeam", game.PlayerManager.NeutralPlayer, isSingleton: true), 802);

        Assert.True(contain.TryAddOccupant(rider));
    }

    [Fact]
    public void Relationship_Enemy_Rejected()
    {
        var game = NewGame();
        var ram = game.SpawnObject("SiegeRam", game.CivilianPlayer, Vector3.Zero);
        var contain = ContainOf(ram);
        var rider = game.SpawnObject("SiegeRider", game.PlayerManager.NeutralPlayer, Vector3.Zero);

        game.PlayerManager.NeutralPlayer.SetRelationship(game.CivilianPlayer, RelationshipType.Enemies);
        ram.Team = new Team(new TeamTemplate(game.TeamFactory, 803, "SiegeTeam", game.CivilianPlayer, isSingleton: true), 803);
        rider.Team = new Team(new TeamTemplate(game.TeamFactory, 804, "RiderTeam", game.PlayerManager.NeutralPlayer, isSingleton: true), 804);

        Assert.False(contain.TryAddOccupant(rider));
        Assert.Equal(0, contain.OccupiedSlots);
    }

    [Fact]
    public void Relationship_Neutral_Rejected()
    {
        // Default relationship (no SetRelationship override) is Neutral - SiegeRam's
        // AllowNeutralInside = No must reject it.
        var game = NewGame();
        var ram = game.SpawnObject("SiegeRam", game.CivilianPlayer, Vector3.Zero);
        var contain = ContainOf(ram);
        var rider = game.SpawnObject("SiegeRider", game.PlayerManager.NeutralPlayer, Vector3.Zero);

        ram.Team = new Team(new TeamTemplate(game.TeamFactory, 805, "SiegeTeam", game.CivilianPlayer, isSingleton: true), 805);
        rider.Team = new Team(new TeamTemplate(game.TeamFactory, 806, "RiderTeam", game.PlayerManager.NeutralPlayer, isSingleton: true), 806);

        Assert.False(contain.TryAddOccupant(rider));
        Assert.Equal(0, contain.OccupiedSlots);
    }

    [Fact]
    public void Relationship_DefaultsAreAllow()
    {
        // SiegeRamDefaults sets no Allow*Inside keys at all - pins the GPL TRUE defaults
        // (OpenContain.cpp:85-87) and guards against the current stub's `false` defaults
        // regressing back in.
        var game = NewGame();
        var ram = game.SpawnObject("SiegeRamDefaults", game.CivilianPlayer, Vector3.Zero);
        var contain = ContainOf(ram);
        var rider = game.SpawnObject("SiegeRider", game.PlayerManager.NeutralPlayer, Vector3.Zero);

        game.PlayerManager.NeutralPlayer.SetRelationship(game.CivilianPlayer, RelationshipType.Enemies);
        ram.Team = new Team(new TeamTemplate(game.TeamFactory, 807, "SiegeTeam", game.CivilianPlayer, isSingleton: true), 807);
        rider.Team = new Team(new TeamTemplate(game.TeamFactory, 808, "RiderTeam", game.PlayerManager.NeutralPlayer, isSingleton: true), 808);

        Assert.True(contain.TryAddOccupant(rider));
    }

    // ---- TC-5: ExitDelay spacing ----

    [Fact]
    public void ExitDelay_SpacesSuccessiveExitsAcrossTicks()
    {
        var game = NewGame();
        var ram = game.SpawnObject("SiegeRam", game.CivilianPlayer, Vector3.Zero);
        var contain = ContainOf(ram);
        var riders = new[]
        {
            game.SpawnObject("SiegeRider", game.CivilianPlayer, Vector3.Zero),
            game.SpawnObject("SiegeRider", game.CivilianPlayer, Vector3.Zero),
        };
        MakeAllied(game, new[] { ram }.Concat(riders).ToArray());
        foreach (var rider in riders)
        {
            Assert.True(contain.TryAddOccupant(rider));
        }

        Assert.Equal(new LogicFrameSpan(3), DataOf(ram).ExitDelay);

        contain.ExitAll();

        // Spacing property: the queue must NOT drain in a single Update() call - there must
        // be an observed intermediate state (OccupiedSlots == 1) before it reaches 0. A
        // single Step()-by-Step() history capture (rather than pinning exact step indices)
        // is robust to the module's own wake-scheduling delay (see StepUntil's doc).
        var sawIntermediateState = false;
        for (var i = 0; i < 30 && contain.OccupiedSlots > 0; i++)
        {
            game.Step();
            if (contain.OccupiedSlots == 1)
            {
                sawIntermediateState = true;
            }
        }

        Assert.True(sawIntermediateState, "expected to observe exactly one occupant released before the second");
        Assert.Equal(0, contain.OccupiedSlots);
    }

    [Fact]
    public void ExitDelay_Zero_DrainsWholeQueueInOneTick()
    {
        var game = NewGame();
        var ram = game.SpawnObject("SiegeRamMultiPath", game.CivilianPlayer, Vector3.Zero);
        var contain = ContainOf(ram);
        for (var i = 0; i < 4; i++)
        {
            Assert.True(contain.TryAddOccupant(game.SpawnObject("SiegeRider", game.CivilianPlayer, Vector3.Zero)));
        }

        contain.ExitAll();

        // With ExitDelay = 0 the gate never arms (SiegeEngineContain.Update's `if
        // (_data.ExitDelay.Value > 0)` guard), so once the module's own tick runs, the whole
        // queue drains within that single Update() call - no intermediate occupancy count is
        // ever observable between 4 and 0.
        var observedCounts = new System.Collections.Generic.HashSet<int> { contain.OccupiedSlots };
        for (var i = 0; i < 30 && contain.OccupiedSlots > 0; i++)
        {
            game.Step();
            observedCounts.Add(contain.OccupiedSlots);
        }

        Assert.Equal(0, contain.OccupiedSlots);
        Assert.Equal(new[] { 0, 4 }, observedCounts.OrderBy(c => c).ToArray());
    }

    // ---- TC-6: exit-path index determinism / draw discipline ----

    [Fact]
    public void ExitPathIndex_MultiPath_IsDeterministicUnderSeededRandom()
    {
        var indicesA = RunMultiPathScenario();
        var indicesB = RunMultiPathScenario();

        Assert.Equal(indicesA, indicesB);
        Assert.All(indicesA, i => Assert.InRange(i, 1, 3));
    }

    private static int[] RunMultiPathScenario()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, 0x9052);
        game.LoadIniText(Definitions);
        var ram = game.SpawnObject("SiegeRamMultiPath", game.CivilianPlayer, Vector3.Zero);
        var contain = ContainOf(ram);
        var riders = new GameObject[4];
        for (var i = 0; i < riders.Length; i++)
        {
            riders[i] = game.SpawnObject("SiegeRider", game.CivilianPlayer, Vector3.Zero);
            Assert.True(contain.TryAddOccupant(riders[i]));
        }

        var indices = new int[riders.Length];
        for (var i = 0; i < riders.Length; i++)
        {
            var expectedRemaining = riders.Length - (i + 1);
            contain.RequestExit(riders[i].Id);
            StepUntil(game, () => contain.OccupiedSlots == expectedRemaining);
            indices[i] = contain.LastExitPathIndex;
        }
        return indices;
    }

    [Fact]
    public void ExitPathIndex_DrawCount()
    {
        var game = NewGame();
        var random = game.GameEngine.SimContext.GameLogicRandom;

        var multi = game.SpawnObject("SiegeRamMultiPath", game.CivilianPlayer, Vector3.Zero);
        var multiContain = ContainOf(multi);
        var multiRiders = new GameObject[4];
        for (var i = 0; i < multiRiders.Length; i++)
        {
            multiRiders[i] = game.SpawnObject("SiegeRider", game.CivilianPlayer, Vector3.Zero);
            Assert.True(multiContain.TryAddOccupant(multiRiders[i]));
        }

        var drawsBefore = random.DrawCount;
        multiContain.ExitAll();
        StepUntil(game, () => multiContain.OccupiedSlots == 0); // ExitDelay = 0: whole queue drains in one Update() call
        Assert.Equal(drawsBefore + (ulong)multiRiders.Length, random.DrawCount);

        // SiegeRam has NumberOfExitPaths = 0: no draws, ever, across a full ExitAll + drain.
        var zero = game.SpawnObject("SiegeRam", game.CivilianPlayer, Vector3.Zero);
        var zeroContain = ContainOf(zero);
        var zeroRiders = new[]
        {
            game.SpawnObject("SiegeRider", game.CivilianPlayer, Vector3.Zero),
            game.SpawnObject("SiegeRider", game.CivilianPlayer, Vector3.Zero),
        };
        MakeAllied(game, new[] { zero }.Concat(zeroRiders).ToArray());
        foreach (var rider in zeroRiders)
        {
            Assert.True(zeroContain.TryAddOccupant(rider));
        }

        var drawsBeforeZero = random.DrawCount;
        zeroContain.ExitAll();
        StepUntil(game, () => zeroContain.OccupiedSlots == 0);
        Assert.Equal(drawsBeforeZero, random.DrawCount);
        Assert.Equal(-1, zeroContain.LastExitPathIndex);
    }

    [Fact]
    public void ExitPathIndex_SinglePath_IsZeroAndDrawsNothing()
    {
        var game = NewGame();
        var random = game.GameEngine.SimContext.GameLogicRandom;
        var ram = game.SpawnObject("SiegeRamDefaults", game.CivilianPlayer, Vector3.Zero); // no key -> default 1
        Assert.Equal(1, DataOf(ram).NumberOfExitPaths);
        var contain = ContainOf(ram);
        var rider = game.SpawnObject("SiegeRider", game.CivilianPlayer, Vector3.Zero);
        Assert.True(contain.TryAddOccupant(rider));

        var drawsBefore = random.DrawCount;
        contain.RequestExit(rider.Id);
        StepUntil(game, () => contain.OccupiedSlots == 0);

        Assert.Equal(0, contain.LastExitPathIndex);
        Assert.Equal(drawsBefore, random.DrawCount);
    }

    // ---- TC-7: once-only death damage ----

    [Fact]
    public void DeathDamage_AppliesPercentOfMaxHealthOnce()
    {
        var game = NewGame();
        var ram = game.SpawnObject("SiegeRam", game.CivilianPlayer, Vector3.Zero);
        var contain = ContainOf(ram);
        var riders = new[]
        {
            game.SpawnObject("SiegeRider", game.CivilianPlayer, Vector3.Zero),
            game.SpawnObject("SiegeRider", game.CivilianPlayer, Vector3.Zero),
        };
        MakeAllied(game, new[] { ram }.Concat(riders).ToArray());
        foreach (var rider in riders)
        {
            Assert.True(contain.TryAddOccupant(rider));
        }

        PortedModuleTestKit.ApplyDamage(ram, 999f, kill: true);
        StepUntil(game, () => contain.OccupiedSlots == 0);

        foreach (var rider in riders)
        {
            var body = (ActiveBody)rider.BodyModule;
            Assert.Equal(Fix64.FromDecimalLiteral("50"), body.DamageCore.CurrentHealth);
        }

        // A further tick must not re-apply the damage (_deathDamageApplied guard).
        game.Step();
        game.Step();
        foreach (var rider in riders)
        {
            var body = (ActiveBody)rider.BodyModule;
            Assert.Equal(Fix64.FromDecimalLiteral("50"), body.DamageCore.CurrentHealth);
        }
    }

    [Fact]
    public void DeathDamage_HundredPercent_KillsOccupants()
    {
        var game = NewGame();
        var ram = game.SpawnObject("SiegeGrondTwoSlots", game.CivilianPlayer, Vector3.Zero);
        var contain = ContainOf(ram);
        var riders = new[]
        {
            game.SpawnObject("SiegeRider", game.CivilianPlayer, Vector3.Zero),
            game.SpawnObject("SiegeRider", game.CivilianPlayer, Vector3.Zero),
        };
        MakeAllied(game, new[] { ram }.Concat(riders).ToArray());
        foreach (var rider in riders)
        {
            Assert.True(contain.TryAddOccupant(rider));
        }

        PortedModuleTestKit.ApplyDamage(ram, 999f, kill: true);
        StepUntil(game, () => riders.All(r => r.IsEffectivelyDead));

        foreach (var rider in riders)
        {
            Assert.True(rider.IsEffectivelyDead);
        }
    }

    [Fact]
    public void DeathDamage_ZeroPercent_LeavesOccupantsUntouched()
    {
        var game = NewGame();
        var ram = game.SpawnObject("SiegeRamZeroDamage", game.CivilianPlayer, Vector3.Zero);
        var contain = ContainOf(ram);
        var rider = game.SpawnObject("SiegeRider", game.CivilianPlayer, Vector3.Zero);
        Assert.True(contain.TryAddOccupant(rider));

        PortedModuleTestKit.ApplyDamage(ram, 999f, kill: true);
        StepUntil(game, () => contain.OccupiedSlots == 0);

        var body = (ActiveBody)rider.BodyModule;
        Assert.Equal(Fix64.FromDecimalLiteral("100"), body.DamageCore.CurrentHealth);
    }

    // ---- TC-8: GoAggressiveOnExit ----

    [Fact]
    public void GoAggressiveOnExit_SetsRiderAttitude()
    {
        var game = NewGame();
        var ram = game.SpawnObject("SiegeRam", game.CivilianPlayer, Vector3.Zero); // GoAggressiveOnExit = Yes
        var contain = ContainOf(ram);
        var rider = game.SpawnObject("SiegeRider", game.CivilianPlayer, Vector3.Zero);
        MakeAllied(game, ram, rider);
        Assert.True(contain.TryAddOccupant(rider));

        contain.RequestExit(rider.Id);
        StepUntil(game, () => contain.OccupiedSlots == 0);

        Assert.Equal(AttitudeType.Aggressive, rider.AIUpdate.Attitude);
    }

    [Fact]
    public void GoAggressiveOnExit_No_LeavesAttitudeUnchanged()
    {
        var game = NewGame();
        var ram = game.SpawnObject("SiegeRamZeroDamage", game.CivilianPlayer, Vector3.Zero); // GoAggressiveOnExit not set -> false
        var contain = ContainOf(ram);
        var rider = game.SpawnObject("SiegeRider", game.CivilianPlayer, Vector3.Zero);
        var attitudeBefore = rider.AIUpdate.Attitude;
        Assert.True(contain.TryAddOccupant(rider));

        contain.RequestExit(rider.Id);
        StepUntil(game, () => contain.OccupiedSlots == 0);

        Assert.Equal(attitudeBefore, rider.AIUpdate.Attitude);
    }

    // ---- TC-9: held-field inertness ----

    [Fact]
    public void HeldFields_ParseAndStayInert()
    {
        var game = NewGame();
        var ram = game.SpawnObject("SiegeRam", game.CivilianPlayer, Vector3.Zero);
        var data = DataOf(ram);
        var contain = ContainOf(ram);

        Assert.Equal(6, data.CrewMax);
        Assert.Equal(6, data.InitialCrew.NumMembers);
        Assert.NotNull(data.PassengerFilter);
        Assert.False(data.ShowPips);
        Assert.Equal(Fix64.FromDecimalLiteral("0.5"), data.SpeedPercentPerCrew);
        Assert.Equal(ObjectKinds.CanRideBatteringRam, data.TypeOneForWeaponSet);

        // PassengerFilter would reject a VEHICLE-kind unit (it only allows
        // CAN_RIDE_BATTERING_RAM); held, so entry is still accepted (disclosed gap 1).
        var vehicle = game.SpawnObject("SiegeRamDefaults", game.CivilianPlayer, new Vector3(50, 0, 0));
        MakeAllied(game, ram, vehicle);
        Assert.True(contain.TryAddOccupant(vehicle));

        // No crew object was spawned by InitialCrew - no SiegeRider exists in the world at
        // all, even though this test never explicitly spawned one.
        Assert.DoesNotContain(
            game.GameEngine.SimContext.GameLogic.ObjectsAscendingId,
            o => o.Definition.Name == "SiegeRider");
    }

    // ---- TC-10: shadow-copy CRC / save-load ----

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidExitQueue()
    {
        var game = NewGame();
        var ram = game.SpawnObject("SiegeRam", game.CivilianPlayer, Vector3.Zero);
        var live = ContainOf(ram);
        var riders = new[]
        {
            game.SpawnObject("SiegeRider", game.CivilianPlayer, Vector3.Zero),
            game.SpawnObject("SiegeRider", game.CivilianPlayer, Vector3.Zero),
        };
        MakeAllied(game, new[] { ram }.Concat(riders).ToArray());
        foreach (var rider in riders)
        {
            Assert.True(live.TryAddOccupant(rider));
        }
        live.RequestExit(riders[0].Id);
        StepUntil(game, () => live.OccupiedSlots == 1); // releases riders[0]: arms _nextExitAllowedAfter, sets _lastExitPathIndex

        var shadowHost = game.SpawnObject("SiegeRam", game.CivilianPlayer, new Vector3(50, 0, 0));
        var shadow = ContainOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void SaveLoad_MidExitQueue_ResumesIdentically()
    {
        var (framesA, indicesA) = RunSaveLoadScenario(roundTripBeforeStep: -1);
        var (framesB, indicesB) = RunSaveLoadScenario(roundTripBeforeStep: 1);

        Assert.Equal(framesA, framesB);
        Assert.Equal(indicesA, indicesB);
    }

    private static (uint FrameFullyDrained, int[] Indices) RunSaveLoadScenario(int roundTripBeforeStep)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, 0xFEED2);
        game.LoadIniText(Definitions);
        var ram = game.SpawnObject("SiegeRam", game.CivilianPlayer, Vector3.Zero);
        var contain = ContainOf(ram);
        var riders = new[]
        {
            game.SpawnObject("SiegeRider", game.CivilianPlayer, Vector3.Zero),
            game.SpawnObject("SiegeRider", game.CivilianPlayer, Vector3.Zero),
        };
        MakeAllied(game, new[] { ram }.Concat(riders).ToArray());
        foreach (var rider in riders)
        {
            Assert.True(contain.TryAddOccupant(rider));
        }
        contain.ExitAll();

        var indices = new System.Collections.Generic.List<int>();
        var step = 0;
        while (contain.OccupiedSlots > 0 && step < 40)
        {
            if (step == roundTripBeforeStep)
            {
                PortedModuleTestKit.Load(contain, PortedModuleTestKit.Save(contain));
            }
            var before = contain.OccupiedSlots;
            game.Step();
            if (contain.OccupiedSlots < before)
            {
                indices.Add(contain.LastExitPathIndex);
            }
            step++;
        }

        return (game.GameEngine.SimContext.CurrentFrame.Value, indices.ToArray());
    }
}
