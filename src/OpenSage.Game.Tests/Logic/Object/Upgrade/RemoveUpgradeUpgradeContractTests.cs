// Mocked-game contract tests for the RemoveUpgradeUpgrade port
// (research/modules-r13/specs/RemoveUpgradeUpgradeModuleData.md §3): one test per spec test
// case, plus the shared shadow-copy base test. Definitions parse from INI text through the
// real parser, so both parse-correctness fixes the spec makes (UpgradeGroupsToRemove as a
// plain string, UpgradeToRemove as a LazyAssetReference array) are on the tested path.
//
// Every TriggeredBy field below names an OBJECT-type upgrade so triggering can go through the
// real GameObject.Upgrade(...) -> UpdateUpgradeableModules(...) path end to end, exactly like
// the real dain.ini/crate.ini shapes. This sidesteps a framework gap noted for the record but
// out of scope here: Player.AddUpgrade (Player.cs) carries its own
// "// TODO: Iterate all game objects owned by this player and call their
// UpdateUpgradeableModules methods" - granting a PLAYER-type upgrade does not itself fire any
// object's upgrade modules. None of AotR's own RemoveUpgradeUpgrade blocks use a PLAYER-type
// TriggeredBy (every cited TriggeredBy in the spec's corpus survey is object-scoped), so this
// does not under-test the real module - it is a pre-existing, separately-filed limitation of
// the shared upgrade-grant path, not this module's file to fix (api-freeze-v1 §6).

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Upgrade;

public class RemoveUpgradeUpgradeContractTests
{
    private const string Definitions = @"
Upgrade Upgrade_Trigger
  Type = OBJECT
End

Upgrade Upgrade_A
  Type = OBJECT
End

Upgrade Upgrade_B
  Type = OBJECT
End

Upgrade Upgrade_CHW01
  Type = OBJECT
End

Upgrade Upgrade_IsMounted
  Type = OBJECT
End

Upgrade Upgrade_MiniHordeLvl2
  Type = OBJECT
End

Upgrade Upgrade_CrateTrigger
  Type = OBJECT
End

Upgrade Upgrade_RingHero
  Type = PLAYER
End

Upgrade Upgrade_SweepTrigger
  Type = OBJECT
End

Upgrade Upgrade_FortressRingHero
  Type = PLAYER
End

Upgrade Upgrade_ObjectMarker
  Type = OBJECT
End

Upgrade Upgrade_CombinedTrigger
  Type = OBJECT
End

Upgrade Upgrade_GroupMember1
  Type = OBJECT
  GroupName = SomeGroup
End

Upgrade Upgrade_GroupMember2
  Type = OBJECT
  GroupName = SomeGroup
End

Upgrade Upgrade_Direct
  Type = OBJECT
End

Upgrade Upgrade_Survivor
  Type = OBJECT
End

Upgrade Upgrade_NoOpTrigger
  Type = OBJECT
End

Upgrade Upgrade_NeverHeld
  Type = OBJECT
End

Upgrade Upgrade_EvaTrigger
  Type = OBJECT
End

Upgrade Upgrade_Companion
  Type = OBJECT
End

Upgrade Upgrade_RemoveTrigger
  Type = OBJECT
End

Object GenericBody
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

; Spec §3 test 1: parse round-trip, all four fields simultaneously.
Object ParseAllFields
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = RemoveUpgradeUpgrade Test
    TriggeredBy = Upgrade_Trigger
    UpgradeToRemove = Upgrade_A Upgrade_B
    UpgradeGroupsToRemove = SomeGroup
    RemoveFromAllPlayerObjects = Yes
    SuppressEvaEventForRemoval = Yes
  End
End

; Spec §3 test 2: parse round-trip, group-only (createaheroremoveupgradeupgrades.inc shape).
Object ParseGroupOnly
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = RemoveUpgradeUpgrade Test
    TriggeredBy = Upgrade_CHW01
    UpgradeGroupsToRemove = CreateAHero_Weapon
  End
End

; Spec §3 test 3: dain.ini's mutual-exclusion pair, object-type targets, self-scope only.
Object MountedToggle
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = RemoveUpgradeUpgrade ModuleTag_RespawnSummonCorrecter
    TriggeredBy = Upgrade_IsMounted
    UpgradeToRemove = Upgrade_MiniHordeLvl2
  End
  Behavior = RemoveUpgradeUpgrade ModuleTag_RespawnSummonCorrecter2
    TriggeredBy = Upgrade_MiniHordeLvl2
    UpgradeToRemove = Upgrade_IsMounted
  End
End

; Spec §3 test 4: crate.ini shape, Player-type target, self-scope (default false).
Object RingRemover
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = RemoveUpgradeUpgrade ModuleTag_RemoveRing
    TriggeredBy = Upgrade_CrateTrigger
    UpgradeToRemove = Upgrade_RingHero
    SuppressEvaEventForRemoval = Yes
  End
End

; Spec §3 test 5: saruman.ini's Ring-Hero-transition shape, RemoveFromAllPlayerObjects = Yes.
Object RingSweeper
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = RemoveUpgradeUpgrade ModuleTag_RemoveRing
    TriggeredBy = Upgrade_SweepTrigger
    UpgradeToRemove = Upgrade_RingHero Upgrade_FortressRingHero Upgrade_ObjectMarker
    RemoveFromAllPlayerObjects = Yes
    SuppressEvaEventForRemoval = Yes
  End
End

; Spec §3 test 6: combined UpgradeToRemove + UpgradeGroupsToRemove (data-derivation, no direct
; corpus precedent - §1.1).
Object CombinedHost
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = RemoveUpgradeUpgrade ModuleTag_Combined
    TriggeredBy = Upgrade_CombinedTrigger
    UpgradeToRemove = Upgrade_Direct
    UpgradeGroupsToRemove = SomeGroup
  End
End

; Spec §3 test 7: no-op on an absent upgrade (idempotency, §1.5).
Object NoOpRemover
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = RemoveUpgradeUpgrade ModuleTag_NoOp
    TriggeredBy = Upgrade_NoOpTrigger
    UpgradeToRemove = Upgrade_NeverHeld
  End
End

; Spec §3 test 8: SuppressEvaEventForRemoval non-effect (F-RUU-1 regression guard).
Object EvaSuppressed
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = RemoveUpgradeUpgrade ModuleTag_Eva
    TriggeredBy = Upgrade_EvaTrigger
    UpgradeToRemove = Upgrade_A
    SuppressEvaEventForRemoval = Yes
  End
End

Object EvaNotSuppressed
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = RemoveUpgradeUpgrade ModuleTag_Eva
    TriggeredBy = Upgrade_EvaTrigger
    UpgradeToRemove = Upgrade_A
    SuppressEvaEventForRemoval = No
  End
End

; Spec §3 test 9: module-reset limitation (F-RUU-2 regression guard, known-gap test).
Object CompanionHost
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AllowBannerSpawnUpgrade ModuleTag_Companion
    TriggeredBy = Upgrade_Companion
  End
  Behavior = RemoveUpgradeUpgrade ModuleTag_Remover
    TriggeredBy = Upgrade_RemoveTrigger
    UpgradeToRemove = Upgrade_Companion
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0x2055Cu)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static UpgradeTemplate Upgrade(HeadlessSimGame game, string name) =>
        game.AssetStore.Upgrades.GetByName(name);

    private static RemoveUpgradeUpgradeModuleData DataOf(HeadlessSimGame game, string definitionName) =>
        game.AssetStore.ObjectDefinitions.GetByName(definitionName)
            .Behaviors.Values.Select(x => x.Data).OfType<RemoveUpgradeUpgradeModuleData>().Single();

    // ------------------------------------------------------------------------------------
    // Test 1/2: parse round-trip.
    // ------------------------------------------------------------------------------------

    [Fact]
    public void Parse_AllFourFieldsTogether_RoundTripsCorrectly()
    {
        var game = NewGame();
        var data = DataOf(game, "ParseAllFields");

        Assert.Equal(2, data.UpgradeToRemove.Length);
        Assert.Equal("Upgrade_A", data.UpgradeToRemove[0].Value.Name);
        Assert.Equal("Upgrade_B", data.UpgradeToRemove[1].Value.Name);

        // Regression guard for the ParseAssetReference -> ParseString fix (spec §2): a plain
        // string, and parsing it must not throw on an asset-table miss.
        Assert.Equal("SomeGroup", data.UpgradeGroupsToRemove);

        Assert.True(data.RemoveFromAllPlayerObjects);
        Assert.True(data.SuppressEvaEventForRemoval);
    }

    [Fact]
    public void Parse_GroupOnly_RealCorpusShape_UpgradeToRemoveIsEmpty()
    {
        var game = NewGame();
        var data = DataOf(game, "ParseGroupOnly");

        Assert.Empty(data.UpgradeToRemove);
        Assert.Equal("CreateAHero_Weapon", data.UpgradeGroupsToRemove);
    }

    // ------------------------------------------------------------------------------------
    // Test 3: named-upgrade removal, object-type target, self-scope only (dain.ini shape).
    // ------------------------------------------------------------------------------------

    [Fact]
    public void NamedObjectUpgrade_MutualToggle_RemovesOnlyOnTheTriggeringObject()
    {
        var game = NewGame();
        var toggle = game.SpawnObject("MountedToggle", game.CivilianPlayer, Vector3.Zero);
        var otherToggle = game.SpawnObject("MountedToggle", game.CivilianPlayer, new Vector3(50, 0, 0));

        toggle.Upgrade(Upgrade(game, "Upgrade_MiniHordeLvl2"));
        otherToggle.Upgrade(Upgrade(game, "Upgrade_MiniHordeLvl2"));
        Assert.True(toggle.HasUpgrade(Upgrade(game, "Upgrade_MiniHordeLvl2")));

        toggle.Upgrade(Upgrade(game, "Upgrade_IsMounted"));

        Assert.False(toggle.HasUpgrade(Upgrade(game, "Upgrade_MiniHordeLvl2")));
        Assert.True(toggle.HasUpgrade(Upgrade(game, "Upgrade_IsMounted")));

        // RemoveFromAllPlayerObjects defaults false in this data: the unrelated object with
        // the same starting upgrades is untouched.
        Assert.True(otherToggle.HasUpgrade(Upgrade(game, "Upgrade_MiniHordeLvl2")));
        Assert.False(otherToggle.HasUpgrade(Upgrade(game, "Upgrade_IsMounted")));
    }

    // ------------------------------------------------------------------------------------
    // Test 4: named-upgrade removal, Player-type target, self-scope (crate.ini shape).
    // ------------------------------------------------------------------------------------

    [Fact]
    public void NamedPlayerUpgrade_RemovalDispatchesToTheOwningPlayer()
    {
        var game = NewGame();
        game.CivilianPlayer.AddUpgrade(Upgrade(game, "Upgrade_RingHero"), UpgradeStatus.Completed);
        Assert.True(game.CivilianPlayer.HasUpgrade(Upgrade(game, "Upgrade_RingHero")));

        var remover = game.SpawnObject("RingRemover", game.CivilianPlayer, Vector3.Zero);
        remover.Upgrade(Upgrade(game, "Upgrade_CrateTrigger"));

        // Proves the type-dispatching UpgradeTemplate.RemoveUpgrade(GameObject) call is used:
        // a test built against the non-dispatching GameObject.RemoveUpgrade overload would
        // incorrectly show the Player-type upgrade still held (spec §0's integration-point
        // note).
        Assert.False(game.CivilianPlayer.HasUpgrade(Upgrade(game, "Upgrade_RingHero")));
    }

    // ------------------------------------------------------------------------------------
    // Test 5: RemoveFromAllPlayerObjects = Yes sweep (Ring-Hero-transition shape).
    // ------------------------------------------------------------------------------------

    [Fact]
    public void RemoveFromAllPlayerObjects_SweepsOnlySameOwnerObjects()
    {
        var game = NewGame();
        game.CivilianPlayer.AddUpgrade(Upgrade(game, "Upgrade_RingHero"), UpgradeStatus.Completed);

        var sweeper = game.SpawnObject("RingSweeper", game.CivilianPlayer, Vector3.Zero);
        var sameOwnerA = game.SpawnObject("GenericBody", game.CivilianPlayer, new Vector3(10, 0, 0));
        var sameOwnerB = game.SpawnObject("GenericBody", game.CivilianPlayer, new Vector3(20, 0, 0));
        var otherOwner = game.SpawnObject("GenericBody", game.PlayerManager.NeutralPlayer, new Vector3(30, 0, 0));

        // The Object-type sweep target, held on one same-owner object and, separately, on a
        // different-owner object - the negative case that proves the sweep is owner-filtered.
        sameOwnerA.Upgrade(Upgrade(game, "Upgrade_ObjectMarker"));
        otherOwner.Upgrade(Upgrade(game, "Upgrade_ObjectMarker"));

        sweeper.Upgrade(Upgrade(game, "Upgrade_SweepTrigger"));

        Assert.False(game.CivilianPlayer.HasUpgrade(Upgrade(game, "Upgrade_RingHero")));
        Assert.False(sameOwnerA.HasUpgrade(Upgrade(game, "Upgrade_ObjectMarker")));

        // sameOwnerB never held the marker; the assertion here is just that the whole-world
        // walk touched it without throwing.
        Assert.False(sameOwnerB.HasUpgrade(Upgrade(game, "Upgrade_ObjectMarker")));

        // Cross-player safety: never touched by a different owner's trigger.
        Assert.True(otherOwner.HasUpgrade(Upgrade(game, "Upgrade_ObjectMarker")));
    }

    // ------------------------------------------------------------------------------------
    // Test 6: combined UpgradeToRemove + UpgradeGroupsToRemove (data-derivation, §1.1/F-RUU-3).
    // ------------------------------------------------------------------------------------

    [Fact]
    public void CombinedNamedAndGroupRemoval_UnionSemantics()
    {
        var game = NewGame();
        var host = game.SpawnObject("CombinedHost", game.CivilianPlayer, Vector3.Zero);

        host.Upgrade(Upgrade(game, "Upgrade_GroupMember1"));
        host.Upgrade(Upgrade(game, "Upgrade_GroupMember2"));
        host.Upgrade(Upgrade(game, "Upgrade_Direct"));
        host.Upgrade(Upgrade(game, "Upgrade_Survivor"));

        host.Upgrade(Upgrade(game, "Upgrade_CombinedTrigger"));

        Assert.False(host.HasUpgrade(Upgrade(game, "Upgrade_GroupMember1")));
        Assert.False(host.HasUpgrade(Upgrade(game, "Upgrade_GroupMember2")));
        Assert.False(host.HasUpgrade(Upgrade(game, "Upgrade_Direct")));
        Assert.True(host.HasUpgrade(Upgrade(game, "Upgrade_Survivor")));
    }

    // ------------------------------------------------------------------------------------
    // Test 7: no-op on an absent upgrade (idempotency, §1.5).
    // ------------------------------------------------------------------------------------

    [Fact]
    public void AbsentUpgrade_IsASilentNoOp_ButTheMuxStillTriggers()
    {
        var game = NewGame();
        var host = game.SpawnObject("NoOpRemover", game.CivilianPlayer, Vector3.Zero);
        var module = host.BehaviorModules.OfType<RemoveUpgradeUpgrade>().Single();

        host.Upgrade(Upgrade(game, "Upgrade_NoOpTrigger"));

        // The mux fires regardless of whether the removal found anything - CanUpgrade only
        // gates on the TriggeredBy/ConflictsWith sets, not on the removal loop's outcome.
        Assert.True(module.Triggered);
        Assert.False(host.HasUpgrade(Upgrade(game, "Upgrade_NeverHeld")));
    }

    // ------------------------------------------------------------------------------------
    // Test 8: SuppressEvaEventForRemoval is parsed but inert (F-RUU-1 regression guard).
    // ------------------------------------------------------------------------------------

    [Fact]
    public void SuppressEvaEventForRemoval_HasNoObservableEffect()
    {
        var gameA = NewGame();
        var suppressed = gameA.SpawnObject("EvaSuppressed", gameA.CivilianPlayer, Vector3.Zero);
        suppressed.Upgrade(Upgrade(gameA, "Upgrade_A"));
        suppressed.Upgrade(Upgrade(gameA, "Upgrade_EvaTrigger"));

        var gameB = NewGame();
        var notSuppressed = gameB.SpawnObject("EvaNotSuppressed", gameB.CivilianPlayer, Vector3.Zero);
        notSuppressed.Upgrade(Upgrade(gameB, "Upgrade_A"));
        notSuppressed.Upgrade(Upgrade(gameB, "Upgrade_EvaTrigger"));

        // Identical removal behavior either way: there is no EVA-shaped member on ISimEvents
        // to gate (spec §1.3), so the flag is observably inert regardless of its value.
        Assert.False(suppressed.HasUpgrade(Upgrade(gameA, "Upgrade_A")));
        Assert.False(notSuppressed.HasUpgrade(Upgrade(gameB, "Upgrade_A")));

        Assert.True(DataOf(gameA, "EvaSuppressed").SuppressEvaEventForRemoval);
        Assert.False(DataOf(gameB, "EvaNotSuppressed").SuppressEvaEventForRemoval);
    }

    // ------------------------------------------------------------------------------------
    // Test 9: module-reset limitation (F-RUU-2 regression guard, known-gap test).
    // ------------------------------------------------------------------------------------

    [Fact]
    public void RemovingAnUpgrade_ClearsMembership_ButDoesNotYetResetItsOwnGrantingModule()
    {
        var game = NewGame();
        var host = game.SpawnObject("CompanionHost", game.CivilianPlayer, Vector3.Zero);

        // Granting the companion's own trigger upgrade fires AllowBannerSpawnUpgrade through
        // the real GameObject.Upgrade -> UpdateUpgradeableModules path.
        host.Upgrade(Upgrade(game, "Upgrade_Companion"));
        Assert.True(host.HasUpgrade(Upgrade(game, "Upgrade_Companion")));

        host.Upgrade(Upgrade(game, "Upgrade_RemoveTrigger"));

        // Grounded by spec §1.4: membership removal is verified. Re-triggerability of the
        // companion module (its own Triggered flag flipping back to false so it could refire)
        // is explicitly NOT asserted here - GameObject.RemoveUpgrade's own
        // "// TODO: Set _triggered to false for all affected upgrade modules" (F-RUU-2) means
        // it does not happen yet in this engine snapshot. A future framework fix closing that
        // TODO is expected to require revisiting this test, not silently break it.
        Assert.False(host.HasUpgrade(Upgrade(game, "Upgrade_Companion")));
    }

    // ------------------------------------------------------------------------------------
    // Test 10: Save -> Load -> CRC parity (shadow-copy test).
    // ------------------------------------------------------------------------------------

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_AfterTrigger()
    {
        var game = NewGame();

        var liveHost = game.SpawnObject("NoOpRemover", game.CivilianPlayer, Vector3.Zero);
        var live = liveHost.BehaviorModules.OfType<RemoveUpgradeUpgrade>().Single();
        liveHost.Upgrade(Upgrade(game, "Upgrade_NoOpTrigger"));
        game.Step();

        var shadowHost = game.SpawnObject("NoOpRemover", game.CivilianPlayer, new Vector3(100, 0, 0));
        var shadow = shadowHost.BehaviorModules.OfType<RemoveUpgradeUpgrade>().Single();

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidState_SaveLoadRoundTrip_PreservesTriggeredFlag()
    {
        var game = NewGame();
        var host = game.SpawnObject("NoOpRemover", game.CivilianPlayer, Vector3.Zero);
        var module = host.BehaviorModules.OfType<RemoveUpgradeUpgrade>().Single();
        host.Upgrade(Upgrade(game, "Upgrade_NoOpTrigger"));

        var saved = PortedModuleTestKit.Save(module);

        var freshHost = game.SpawnObject("NoOpRemover", game.CivilianPlayer, new Vector3(50, 0, 0));
        var fresh = freshHost.BehaviorModules.OfType<RemoveUpgradeUpgrade>().Single();
        Assert.NotEqual(PortedModuleTestKit.LiveCrc(module), PortedModuleTestKit.LiveCrc(fresh));

        PortedModuleTestKit.Load(fresh, saved);
        Assert.Equal(PortedModuleTestKit.LiveCrc(module), PortedModuleTestKit.LiveCrc(fresh));
    }
}
