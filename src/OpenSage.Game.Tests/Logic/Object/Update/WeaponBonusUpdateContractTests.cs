// Mocked-game contract tests for the WeaponBonusUpdate port (R13): the periodic scan
// (relationship/liveness/off-map filters, ALL-bits-required RequiredAffectKindOf, ANY-bit-reject
// ForbiddenAffectKindOf), F-WBU-3's unconditional self-inclusion, the contained-passenger pass
// that reaches valid contents of an invalid (kindof-failing) transport independent of the
// transport's own outcome, the F-WBU-1 per-source expiry sweep (refresh-on-re-scan, expire after
// BonusDuration of no re-scan), the unconditional BonusDelay re-arm, and the shadow-copy +
// save/load round-trip base tests. Object definitions are parsed from INI text through the real
// parser, so the BonusDuration/BonusDelay/BonusRange/kindof-list quantizing parse is on the
// tested path.
//
// Sleepy-update caveat (api-freeze-v1 S6 convention, per EmpUpdateContractTests/
// DemoTrapUpdateContractTests, spec-confirmed): a freshly spawned module's NextCallFrame is
// floored to `now` at creation, and Update() only runs once CurrentFrame >= NextCallFrame - the
// tick that observes CurrentFrame == N runs on the (N+1)th HeadlessSimGame.Step() call. This
// module's ctor additionally sets UPDATE_SLEEP_NONE (no BonusDelay-scaled initial wait), so its
// first scan specifically lands on the SECOND Step() regardless of BonusDelay's value;
// subsequent scans are BonusDelay frames apart from that point.
//
// No Contain-category module is landed yet (every Contain module in this codebase is still
// [ParseOnly]), so GameObject.Contain can't be populated through real INI/object creation. The
// contained-passenger test injects a minimal test-only IContainModule straight into the private
// backing field via reflection, the same technique BunkerBusterBehaviorContractTests uses.

using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.Mathematics;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class WeaponBonusUpdateContractTests
{
    // 5 Hz logic rate (F6): 1000ms = 5 frames. BonusDuration = 2000ms = 10 frames,
    // BonusDelay = 1000ms = 5 frames, BonusRange = 50.
    private const string Definitions = @"
Object BonusGranter
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = WeaponBonusUpdate ModuleTag_Bonus
    RequiredAffectKindOf = INFANTRY
    ForbiddenAffectKindOf = STRUCTURE
    BonusDuration = 2000
    BonusDelay = 1000
    BonusRange = 50
    BonusConditionType = HORDE
  End
End

Object SelfQualifyingGranter
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = WeaponBonusUpdate ModuleTag_Bonus
    RequiredAffectKindOf = INFANTRY
    ForbiddenAffectKindOf = STRUCTURE
    BonusDuration = 2000
    BonusDelay = 1000
    BonusRange = 50
    BonusConditionType = HORDE
  End
End

Object AllyInfantry
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 10
  End
End

Object EnemyInfantry
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 10
  End
End

Object AllyStructure
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 10
  End
End

Object TransportVehicle
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 50
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xB09A) // "wbonus"-ish
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

    private static WeaponBonusUpdate ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<WeaponBonusUpdate>().Single();

    private static uint _nextTeamId = 9000;

    /// <summary>
    /// Establishes an Allies relationship from <paramref name="granter"/> toward
    /// <paramref name="candidate"/> the way GameObject.GetRelationship actually reads it
    /// (Team-based, not the separate Player.Allies/Enemies hash sets some other modules read
    /// directly - EmpUpdateContractTests' note). HeadlessSimGame.SpawnObject never assigns a
    /// Team, and GetRelationship short-circuits to Neutral whenever either side's Team is null,
    /// so both objects need one; there is no same-team-is-automatically-Allies shortcut in this
    /// engine snapshot (Team.GetRelationship falls through to a plain
    /// Player.SetRelationship-backed override table), so the override is set explicitly, same
    /// shape as EmpUpdateContractTests.AirborneAlliedTransport_IsSpared. The override direction
    /// matters: WeaponBonusUpdate calls GameObject.GetRelationship(candidate) from the
    /// GRANTER's side, so the override belongs on the granter's owner pointed at the
    /// candidate's owner (the reverse of a "candidate.GetRelationship(self)" caller like
    /// EmpUpdate).
    /// </summary>
    private static void MakeAlly(HeadlessSimGame game, GameObject granter, GameObject candidate)
    {
        if (granter.Team == null)
        {
            var granterTeamId = _nextTeamId++;
            granter.Team = new Team(new TeamTemplate(game.TeamFactory, granterTeamId, "GranterTeam", granter.Owner, isSingleton: true), granterTeamId);
        }

        var candidateTeamId = _nextTeamId++;
        candidate.Team = new Team(new TeamTemplate(game.TeamFactory, candidateTeamId, "CandidateTeam", candidate.Owner, isSingleton: true), candidateTeamId);
        granter.Owner.SetRelationship(candidate.Owner, RelationshipType.Allies);
    }

    /// <summary>
    /// GameObject exposes AddWeaponBonusType/RemoveWeaponBonusType as write-only setters (no
    /// public "is this bonus active" getter exists, and adding one would touch the shared
    /// GameObject.cs file the spec's name reservations explicitly keep out of scope for this
    /// port). Reads the private _weaponBonusTypes BitArray back via reflection instead - the
    /// "_weaponBonusTypes test accessor" the spec's test plan calls out as the alternative to a
    /// (non-existent) public accessor.
    /// </summary>
    private static bool HasHordeBonus(GameObject obj)
    {
        var field = typeof(GameObject).GetField("_weaponBonusTypes", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        var bits = (BitArray<WeaponBonusType>)field!.GetValue(obj)!;
        return bits.Get(WeaponBonusType.Horde);
    }

    /// <summary>
    /// GameObject.Contain has no landed real setter (no Contain-category module is ported yet),
    /// so this reaches straight into the auto-property's compiler-generated backing field - the
    /// same reflection-injection technique BunkerBusterBehaviorContractTests.InjectContain uses.
    /// </summary>
    private static void InjectContain(GameObject obj, IContainModule contain)
    {
        var field = typeof(GameObject).GetField("<Contain>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(obj, contain);
    }

    private sealed class FakeTransportContain : IContainModule
    {
        private readonly GameObject[] _items;

        public FakeTransportContain(params GameObject[] items) => _items = items;

        public bool IsGarrisonable => false;
        public bool IsImmuneToClearBuildingAttacks => false;
        public bool IsRiderChangeContain => false;
        public uint ContainCount => (uint)_items.Length;
        public float ContainedItemsMass => 0f;
        public ReadOnlySpan<GameObject> ContainedItems => _items;
        public void OrderAllPassengersToIdle(CommandSourceType commandType) { }
        public void OrderAllPassengersToHackInternet(CommandSourceType commandType) { }
    }

    [Fact]
    public void FirstScanOnSecondStep_NoInitialDelay()
    {
        var game = NewGame();
        var granter = game.SpawnObject("BonusGranter", game.CivilianPlayer, new Vector3(0, 0, 0));
        var ally = game.SpawnObject("AllyInfantry", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));
        MakeAlly(game, granter, ally);

        game.Step();
        Assert.False(HasHordeBonus(ally), "the module has not ticked yet after only one Step()");

        game.Step();
        Assert.True(HasHordeBonus(ally), "UPDATE_SLEEP_NONE means the first scan lands on the second Step()");
    }

    [Fact]
    public void AlliedInfantry_InRange_ReceivesBonus()
    {
        var game = NewGame();
        var granter = game.SpawnObject("BonusGranter", game.CivilianPlayer, new Vector3(0, 0, 0));
        var ally = game.SpawnObject("AllyInfantry", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));
        MakeAlly(game, granter, ally);

        StepFrames(game, 2);

        Assert.True(HasHordeBonus(ally));
    }

    [Fact]
    public void EnemyInfantry_InRange_NoBonus()
    {
        // No Team/SetRelationship override at all: GameObject.GetRelationship defaults to
        // Neutral (both a null Team and an unregistered player pair resolve the same way), which
        // already fails the ALLOW_ALLIES-only filter - no explicit Enemies override needed to
        // pin this branch.
        var game = NewGame();
        game.SpawnObject("BonusGranter", game.CivilianPlayer, new Vector3(0, 0, 0));
        var enemy = game.SpawnObject("EnemyInfantry", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));

        StepFrames(game, 2);

        Assert.False(HasHordeBonus(enemy));
    }

    [Fact]
    public void ForbiddenKindOf_Structure_NoBonus()
    {
        var game = NewGame();
        var granter = game.SpawnObject("BonusGranter", game.CivilianPlayer, new Vector3(0, 0, 0));
        var structure = game.SpawnObject("AllyStructure", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));
        MakeAlly(game, granter, structure);

        StepFrames(game, 2);

        Assert.False(HasHordeBonus(structure));
    }

    [Fact]
    public void Self_UnconditionallyIncluded_WhenQualifying()
    {
        // F-WBU-3: GPL's position-based query always includes self (no AllowSelf field exists
        // to gate this, unlike AttributeModifierAuraUpdate). SelfQualifyingGranter is
        // KindOf = INFANTRY only, so it passes RequiredAffectKindOf = INFANTRY and carries no
        // ForbiddenAffectKindOf = STRUCTURE bit.
        var game = NewGame();
        var granter = game.SpawnObject("SelfQualifyingGranter", game.CivilianPlayer, new Vector3(0, 0, 0));

        StepFrames(game, 2);

        Assert.True(HasHordeBonus(granter));
    }

    [Fact]
    public void ContainedPassenger_BonusedIndependentlyOfTransportKindOf()
    {
        // The single most important behavioral fact this module has (the GPL source comment's
        // stated purpose): a transport that itself fails the kindof gate is skipped for its own
        // bonus, but its direct passengers are still checked and bonused individually.
        var game = NewGame();
        var granter = game.SpawnObject("BonusGranter", game.CivilianPlayer, new Vector3(0, 0, 0));
        var transport = game.SpawnObject("TransportVehicle", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));
        MakeAlly(game, granter, transport);
        // The passenger is reached only through Contain.ContainedItems, never through the
        // partition scan, so its own relationship/team is irrelevant - the module's
        // contained-item pass applies the kindof gate only, no relationship re-check.
        var passenger = game.SpawnObject("AllyInfantry", game.PlayerManager.NeutralPlayer, new Vector3(999, 999, 0));
        InjectContain(transport, new FakeTransportContain(passenger));

        StepFrames(game, 2);

        Assert.False(HasHordeBonus(transport), "the transport itself fails RequiredAffectKindOf = INFANTRY");
        Assert.True(HasHordeBonus(passenger), "the contained-item pass applies independent of the container's own qualification");
    }

    [Fact]
    public void SameStatusReScan_RefreshesTimerWithoutDoubleApplying()
    {
        // First grant lands on the 2nd Step() (frame 1). Second scan (BonusDelay = 5 later)
        // lands on the 7th Step() (frame 6), re-applying and extending ExpireFrame to
        // 6 + BonusDuration(10) = 16. By frame 10 the naive first-grant expiry (1 + 10 = 11)
        // has NOT yet passed either, so this test drives further, to frame 12 (13th Step()) -
        // past the first grant's naive 11 but still well before the refreshed 16 - to prove the
        // re-scan's refresh, not just the first grant's own still-live window.
        var game = NewGame();
        var granter = game.SpawnObject("BonusGranter", game.CivilianPlayer, new Vector3(0, 0, 0));
        var ally = game.SpawnObject("AllyInfantry", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));
        MakeAlly(game, granter, ally);

        StepFrames(game, 2); // frame 1: first grant, expire=11
        Assert.True(HasHordeBonus(ally));

        StepFrames(game, 10); // through frame 11 (12 steps total): second scan at frame 6 refreshed expire to 16

        Assert.True(HasHordeBonus(ally), "the second scan's re-apply must have extended the expiry past the first grant's naive 11");
    }

    [Fact]
    public void OutOfRangeAfterGrant_ExpiresAfterBonusDuration_NotBeforeNotIndefinitely()
    {
        // Grant at frame 1 (2nd Step()), expire = 1 + 10 = 11. Move the ally out of range
        // immediately after so no further re-scan refreshes it. Assert still active just before
        // expiry and cleared by/after expiry (allowing up to one extra BonusDelay-scale tick of
        // drift per F-WBU-1's filed expiry-granularity gap - the sweep only runs on this
        // module's own BonusDelay cadence, not on an exact per-target timer).
        var game = NewGame();
        var granter = game.SpawnObject("BonusGranter", game.CivilianPlayer, new Vector3(0, 0, 0));
        var ally = game.SpawnObject("AllyInfantry", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));
        MakeAlly(game, granter, ally);

        StepFrames(game, 2); // frame 1: grant, expire=11
        Assert.True(HasHordeBonus(ally));

        ally.UpdateTransform(new Vector3(999, 999, 0));
        ally.UpdateColliders();

        StepFrames(game, 8); // through frame 9: still before expire=11
        Assert.True(HasHordeBonus(ally), "must still be active just before the expiry frame");

        StepFrames(game, 5); // through frame 14: the sweep on the next BonusDelay-cadence scan (frame 11) clears it
        Assert.False(HasHordeBonus(ally), "must be cleared once the expiry sweep has had a chance to run");
    }

    [Fact]
    public void Rearms_OnBonusDelay_Unconditionally()
    {
        // No qualifying candidates at first: spawn one just before the expected second-scan
        // frame (BonusDelay = 5 frames after the first scan at frame 1, so frame 6) and confirm
        // it gets bonused exactly then, not a frame earlier.
        var game = NewGame();
        var granter = game.SpawnObject("BonusGranter", game.CivilianPlayer, new Vector3(0, 0, 0));

        StepFrames(game, 2); // frame 1: first scan, no candidates yet

        var lateAlly = game.SpawnObject("AllyInfantry", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));
        MakeAlly(game, granter, lateAlly);

        StepFrames(game, 4); // through frame 5: one frame before the second scan (frame 6)
        Assert.False(HasHordeBonus(lateAlly), "the second scan has not run yet");

        game.Step(); // frame 6: the second scan
        Assert.True(HasHordeBonus(lateAlly), "the unconditional BonusDelay re-arm must have fired exactly on schedule");
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidGrant()
    {
        var game = NewGame();
        var liveHost = game.SpawnObject("BonusGranter", game.CivilianPlayer, new Vector3(0, 0, 0));
        var ally = game.SpawnObject("AllyInfantry", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));
        MakeAlly(game, liveHost, ally);
        StepFrames(game, 2);
        var live = ModuleOf(liveHost);
        Assert.NotEmpty(live.Grants);

        var shadowHost = game.SpawnObject("BonusGranter", game.CivilianPlayer, new Vector3(400, 400, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidGrant_SaveLoadRoundTrip_ContinuesIdentically()
    {
        var trajectoryA = RunScenario(roundTripAtFrame: -1);
        var trajectoryB = RunScenario(roundTripAtFrame: 4);
        Assert.Equal(trajectoryA, trajectoryB);
    }

    private static bool[] RunScenario(int roundTripAtFrame)
    {
        var game = NewGame(seed: 0xF00D);
        var granter = game.SpawnObject("BonusGranter", game.CivilianPlayer, new Vector3(0, 0, 0));
        var ally = game.SpawnObject("AllyInfantry", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));
        MakeAlly(game, granter, ally);
        var module = ModuleOf(granter);

        var trajectory = new bool[14];
        for (var i = 0; i < trajectory.Length; i++)
        {
            if (i == roundTripAtFrame)
            {
                var state = PortedModuleTestKit.Save(module);
                var wake = module.NextWakeFrameForWalk;
                PortedModuleTestKit.Load(module, state);
                module.NextWakeFrameForWalk = wake;
            }

            game.Step();
            trajectory[i] = HasHordeBonus(ally);
        }

        return trajectory;
    }
}
