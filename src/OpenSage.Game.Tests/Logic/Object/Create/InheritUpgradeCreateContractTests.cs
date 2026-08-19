// Contract tests for the InheritUpgradeCreate port (experiment-round-4 §4.1 DoD item 4: one
// test per INI branch, minimum [create -> observable effect]). InheritUpgradeCreate is a
// fire-once create reaction with EMPTY mutable state whose Xfer is the legacy version+base
// walk (it mirrors GrantUpgradeCreate exactly - HasSimXfer is false, so the shadow-copy CRC
// kit does not apply and the byte-level save/load round trip is a harness N/A, recorded in
// modules-r10/InheritUpgradeCreate.md just as GrantUpgradeCreate.md records it). The observable
// effect is the upgrade mask: the module's whole job is to put the named upgrade on the object
// (Type=OBJECT) or its owner (Type=PLAYER) when a nearby filter-matching donor already has it.
//
// HARNESS ORDERING NOTE (finding F-IUC-3): GameLogic.CreateObject fires OnCreate BEFORE
// HeadlessSimGame.SpawnObject applies the spawn position, so the newly-created object scans
// from its initial transform (the origin here), not from the position passed to SpawnObject.
// The S3 QueryObjectsInRadius self-syncs every registered object's real transform at query
// time, so DONORS are found at their true positions; only the SCAN CENTRE is the origin. Tests
// therefore place donors relative to the origin and spawn the inheriting object at the origin.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Create;

public class InheritUpgradeCreateContractTests
{
    private const string ObjectUpgrade = "InheritTestObjectUpgrade";
    private const string PlayerUpgrade = "InheritTestPlayerUpgrade";

    private const string Definitions = @"
Upgrade " + ObjectUpgrade + @"
  Type = OBJECT
End

Upgrade " + PlayerUpgrade + @"
  Type = PLAYER
End

; a potential donor: a STRUCTURE (matches the inheriter's ObjectFilter)
Object DonorStructure
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

; a potential donor that the ObjectFilter (STRUCTURE-only) rejects
Object DonorInfantry
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

; inherits an OBJECT upgrade from a nearby STRUCTURE that already has it
Object InheritStructUnit
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = InheritUpgradeCreate ModuleTag_Inherit
    Radius = 100
    Upgrade = " + ObjectUpgrade + @"
    ObjectFilter = ANY +STRUCTURE
  End
End

; inherits a PLAYER upgrade -> routes to the inheriting object's owner
Object InheritPlayerUnit
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = InheritUpgradeCreate ModuleTag_Inherit
    Radius = 100
    Upgrade = " + PlayerUpgrade + @"
    ObjectFilter = ANY +STRUCTURE
  End
End

; Upgrade names an asset that does not exist -> silent no-op, no crash
Object InheritMissingUnit
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = InheritUpgradeCreate ModuleTag_Inherit
    Radius = 100
    Upgrade = ThisUpgradeDoesNotExist
    ObjectFilter = ANY +STRUCTURE
  End
End

; Upgrade omitted entirely -> silent no-op, no crash
Object InheritNoNameUnit
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = InheritUpgradeCreate ModuleTag_Inherit
    Radius = 100
    ObjectFilter = ANY +STRUCTURE
  End
End
";

    private static readonly Vector3 Origin = Vector3.Zero;

    private static HeadlessSimGame NewGame(uint seed = 0x1417A1Eu)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static UpgradeTemplate Upgrade(HeadlessSimGame game, string name) =>
        game.AssetStore.Upgrades.GetByName(name);

    private static InheritUpgradeCreateModuleData DataOf(HeadlessSimGame game, string definitionName) =>
        Assert.IsType<InheritUpgradeCreateModuleData>(
            game.AssetStore.ObjectDefinitions.GetByName(definitionName)
                .Behaviors.Values.Single(b => b.Data is InheritUpgradeCreateModuleData).Data);

    // Spawns a STRUCTURE donor at the origin already carrying the OBJECT upgrade.
    private static GameObject SpawnDonorWithObjectUpgrade(HeadlessSimGame game, in Vector3 at)
    {
        var donor = game.SpawnObject("DonorStructure", game.CivilianPlayer, at);
        donor.Upgrade(Upgrade(game, ObjectUpgrade));
        return donor;
    }

    // ---- INI branch: a nearby matching donor with the upgrade -> the object inherits it ----

    [Fact]
    public void DonorInRangeWithUpgrade_InheritsObjectUpgrade()
    {
        var game = NewGame();
        SpawnDonorWithObjectUpgrade(game, Origin);

        var unit = game.SpawnObject("InheritStructUnit", game.CivilianPlayer, Origin);

        Assert.True(unit.HasUpgrade(Upgrade(game, ObjectUpgrade)));
    }

    // ---- INI branch: the donor is outside Radius -> nothing is inherited ----

    [Fact]
    public void DonorOutOfRange_DoesNotInherit()
    {
        var game = NewGame();
        SpawnDonorWithObjectUpgrade(game, new Vector3(500, 0, 0)); // > Radius(100) from the origin

        var unit = game.SpawnObject("InheritStructUnit", game.CivilianPlayer, Origin);

        Assert.False(unit.HasUpgrade(Upgrade(game, ObjectUpgrade)));
    }

    // ---- INI branch: an in-range donor the ObjectFilter rejects -> nothing is inherited ----

    [Fact]
    public void DonorRejectedByObjectFilter_DoesNotInherit()
    {
        var game = NewGame();
        // INFANTRY donor WITH the upgrade, in range, but the filter is STRUCTURE-only.
        var donor = game.SpawnObject("DonorInfantry", game.CivilianPlayer, Origin);
        donor.Upgrade(Upgrade(game, ObjectUpgrade));

        var unit = game.SpawnObject("InheritStructUnit", game.CivilianPlayer, Origin);

        Assert.False(unit.HasUpgrade(Upgrade(game, ObjectUpgrade)));
    }

    // ---- INI branch: an in-range matching donor that lacks the upgrade -> nothing to inherit ----

    [Fact]
    public void MatchingDonorWithoutUpgrade_DoesNotInherit()
    {
        var game = NewGame();
        game.SpawnObject("DonorStructure", game.CivilianPlayer, Origin); // matches filter, but no upgrade

        var unit = game.SpawnObject("InheritStructUnit", game.CivilianPlayer, Origin);

        Assert.False(unit.HasUpgrade(Upgrade(game, ObjectUpgrade)));
    }

    // ---- INI branch: no donor at all -> nothing inherited (also covers self-exclusion: the
    //      inheriting STRUCTURE matches its own filter but has no upgrade and is skipped) ----

    [Fact]
    public void NoDonor_DoesNotInherit()
    {
        var game = NewGame();
        var unit = game.SpawnObject("InheritStructUnit", game.CivilianPlayer, Origin);

        Assert.False(unit.HasUpgrade(Upgrade(game, ObjectUpgrade)));
    }

    // ---- INI branch: a PLAYER-type inherited upgrade routes to the owner, not the object ----

    [Fact]
    public void PlayerUpgrade_RoutesToTheInheritingOwner()
    {
        var game = NewGame();
        var donorOwner = game.PlayerManager.NeutralPlayer;
        var playerUpgrade = Upgrade(game, PlayerUpgrade);

        // Donor owned by a DIFFERENT player already holds the PLAYER upgrade (on that player's
        // mask); HasUpgrade for a PLAYER upgrade delegates to the object's owner.
        var donor = game.SpawnObject("DonorStructure", donorOwner, Origin);
        donorOwner.AddUpgrade(playerUpgrade, UpgradeStatus.Completed);
        Assert.True(donor.HasUpgrade(playerUpgrade));

        // The inheriting object is owned by the civilian player, who does NOT have it yet.
        Assert.False(game.CivilianPlayer.HasUpgrade(playerUpgrade));

        var unit = game.SpawnObject("InheritPlayerUnit", game.CivilianPlayer, Origin);

        // Inherited -> routed to the inheriting object's owner (the civilian), and readable
        // through the object's own HasUpgrade delegation.
        Assert.True(game.CivilianPlayer.HasUpgrade(playerUpgrade));
        Assert.True(unit.HasUpgrade(playerUpgrade));
    }

    // ---- INI branch: an unresolved Upgrade asset name is a silent no-op ----

    [Fact]
    public void MissingUpgradeName_IsASilentNoOp()
    {
        var game = NewGame();
        SpawnDonorWithObjectUpgrade(game, Origin); // a valid donor is present; the name is the problem

        // No crash despite a bad Upgrade name, and nothing is granted.
        var unit = game.SpawnObject("InheritMissingUnit", game.CivilianPlayer, Origin);

        Assert.False(unit.HasUpgrade(Upgrade(game, ObjectUpgrade)));
    }

    // ---- INI branch: an omitted Upgrade name is a silent no-op ----

    [Fact]
    public void EmptyUpgradeName_IsASilentNoOp()
    {
        var game = NewGame();
        SpawnDonorWithObjectUpgrade(game, Origin);

        var unit = game.SpawnObject("InheritNoNameUnit", game.CivilianPlayer, Origin);

        Assert.False(unit.HasUpgrade(Upgrade(game, ObjectUpgrade)));
    }

    // ---- parse: Radius is quantized to Fix64 at the F4 wire boundary (parser.ParseFix64) ----

    [Fact]
    public void Radius_ParsesAsFix64()
    {
        var game = NewGame();
        Assert.Equal((Fix64)100L, DataOf(game, "InheritStructUnit").Radius);
    }
}
