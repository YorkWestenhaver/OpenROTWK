// Mocked-game unit tests for the WeaponBonusUpgrade port (experiment-round-4 §4.1 DoD item 4).
// One test per INI branch, each shaped [create -> trigger upgrade -> observable effect].
//
// Behavioral reference: generals-gpl GeneralsMD WeaponBonusUpgrade.cpp (GPL as fact source only).
// The observable effect is the PLAYER_UPGRADE bit on the GameObject's weapon-bonus condition
// channel (GameObject._weaponBonusTypes) - the same channel HordeUpdate feeds and the WeaponSet
// chooser reads to select WeaponBonus= scaled stats. That channel is private with no read
// accessor, and GameObject is a sealed, heavily-shared file that merge-hygiene keeps this branch
// out of; the tests therefore observe the bit through reflection (test-only, no production edit)
// rather than by adding an accessor.
//
// FRAMEWORK NOTE (finding, see research/modules-r9/WeaponBonusUpgrade.md): the whole Upgrade
// module category is still on the legacy BehaviorModule ctor (GameObject, IGameEngine) +
// StatePersister Load - unlike the Die/Body categories, UpgradeModule was never migrated to the
// (GameObject, ISimContext) contract ctor + IXfer. This port matches its 40 sibling upgrade
// modules (WeaponSetUpgrade/ArmorUpgrade/...) rather than migrating the shared base, which would
// collide with the three other R9 upgrade branches that share the identical UpgradeModule.cs.
// Consequently there is no contract Xfer to shadow-copy here; the module is stateless (GPL xfer =
// version byte + base), and the only persisted state is the GameObject-level bit exercised below.

using System.Linq;
using System.Numerics;
using System.Reflection;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.Mathematics;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Upgrade;

public class WeaponBonusUpgradeContractTests
{
    private const string Definitions = @"
Upgrade Upgrade_WeaponBonus
  Type = OBJECT
End

Upgrade Upgrade_Unrelated
  Type = OBJECT
End

; The normal case: the bonus is granted when its prerequisite upgrade completes.
Object BonusUnit
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = WeaponBonusUpgrade ModuleTag_Bonus
    TriggeredBy = Upgrade_WeaponBonus
  End
End

; StartsActive: the callback fires at construction, before any upgrade completes.
Object StartsActiveUnit
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = WeaponBonusUpgrade ModuleTag_Bonus
    TriggeredBy = Upgrade_WeaponBonus
    StartsActive = Yes
  End
End
";

    private static readonly FieldInfo WeaponBonusTypesField =
        typeof(GameObject).GetField("_weaponBonusTypes", BindingFlags.NonPublic | BindingFlags.Instance);

    private static bool HasPlayerUpgradeBonus(GameObject obj)
    {
        var bits = (BitArray<WeaponBonusType>)WeaponBonusTypesField.GetValue(obj);
        return bits.Get(WeaponBonusType.PlayerUpgrade);
    }

    private static HeadlessSimGame NewGame(uint seed = 0xB0Au)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static UpgradeTemplate Upgrade(HeadlessSimGame game, string name) =>
        game.AssetStore.Upgrades.GetByName(name);

    private static WeaponBonusUpgradeModuleData DataOf(HeadlessSimGame game, string definitionName) =>
        game.AssetStore.ObjectDefinitions.GetByName(definitionName)
            .Behaviors.Values.Select(x => x.Data).OfType<WeaponBonusUpgradeModuleData>().Single();

    [Fact]
    public void BeforeUpgrade_HasNoWeaponBonus()
    {
        var game = NewGame();
        var unit = game.SpawnObject("BonusUnit", game.CivilianPlayer, Vector3.Zero);

        Assert.False(HasPlayerUpgradeBonus(unit));
    }

    [Fact]
    public void CompletingThePrerequisite_GrantsThePlayerUpgradeBonus()
    {
        var game = NewGame();
        var unit = game.SpawnObject("BonusUnit", game.CivilianPlayer, Vector3.Zero);

        unit.Upgrade(Upgrade(game, "Upgrade_WeaponBonus"));

        Assert.True(HasPlayerUpgradeBonus(unit));
    }

    [Fact]
    public void AnUnrelatedUpgrade_DoesNotGrantTheBonus()
    {
        // The mux gate: only the TriggeredBy upgrade fires OnUpgrade. Completing a different
        // upgrade must leave the bonus channel untouched.
        var game = NewGame();
        var unit = game.SpawnObject("BonusUnit", game.CivilianPlayer, Vector3.Zero);

        unit.Upgrade(Upgrade(game, "Upgrade_Unrelated"));

        Assert.False(HasPlayerUpgradeBonus(unit));

        // Control: the prerequisite still works afterwards, so the assertion above measures the
        // gate and not a broken setup.
        unit.Upgrade(Upgrade(game, "Upgrade_WeaponBonus"));
        Assert.True(HasPlayerUpgradeBonus(unit));
    }

    [Fact]
    public void StartsActive_GrantsTheBonusAtConstruction()
    {
        var game = NewGame();
        var unit = game.SpawnObject("StartsActiveUnit", game.CivilianPlayer, Vector3.Zero);

        // No Upgrade() call: StartsActive triggered the callback inside the ctor.
        Assert.True(HasPlayerUpgradeBonus(unit));
    }

    [Fact]
    public void ReCompletingThePrerequisite_IsIdempotent()
    {
        // Once triggered the mux refuses to re-fire (UpgradeLogic.CanUpgrade returns false while
        // _triggered). The bonus bit is a set-idempotent operation regardless, but this pins that
        // a second completion is a no-op rather than a re-application with side effects.
        var game = NewGame();
        var unit = game.SpawnObject("BonusUnit", game.CivilianPlayer, Vector3.Zero);
        var module = unit.FindBehavior<WeaponBonusUpgrade>();

        unit.Upgrade(Upgrade(game, "Upgrade_WeaponBonus"));
        Assert.True(module.Triggered);
        Assert.True(HasPlayerUpgradeBonus(unit));

        unit.UpdateUpgradeableModules();   // re-run the trigger sweep
        Assert.True(module.Triggered);
        Assert.True(HasPlayerUpgradeBonus(unit));
    }

    [Fact]
    public void ModuleData_ParsesOnlyTheSharedMuxFields()
    {
        // WeaponBonusUpgrade defines no fields of its own (GPL has no buildFieldParse); the only
        // parsed configuration is the inherited upgrade-mux table. A regression that added or
        // dropped a field here would change this object's behavior surface.
        var game = NewGame();
        var data = DataOf(game, "BonusUnit");

        Assert.NotNull(data);
        Assert.Single(data.UpgradeData.TriggeredBy);
        Assert.Equal("Upgrade_WeaponBonus", data.UpgradeData.TriggeredBy[0].Value.Name);
    }
}
