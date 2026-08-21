// Mocked-game contract tests for the AttributeModifierUpgrade port (R11 Track B): the
// triggered ModifierList grant into the object's modifier registry (the sim-visible
// output; effect application is the legacy client loop - see the module header), mux
// idempotence, and the shadow-copy base test.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Upgrade;

public class AttributeModifierUpgradeContractTests
{
    private const string Definitions = @"
ModifierList TestBuff
  Category = LEADERSHIP
  Modifier = ARMOR 25%
End

Upgrade Upgrade_GrantBuff
  Type = PLAYER
End

Object BuffBearer
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AttributeModifierUpgrade ModuleTag_Buff
    TriggeredBy = Upgrade_GrantBuff
    AttributeModifier = TestBuff
  End
End

Object EagerBuffBearer
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AttributeModifierUpgrade ModuleTag_Buff
    StartsActive = Yes
    AttributeModifier = TestBuff
  End
End
";

    private static HeadlessSimGame NewGame()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, 0xA77);
        game.LoadIniText(Definitions);
        return game;
    }

    private static UpgradeSet BuffSet(HeadlessSimGame game) =>
        new UpgradeSet { game.AssetStore.Upgrades.GetByName("Upgrade_GrantBuff") };

    private static AttributeModifierUpgrade ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<AttributeModifierUpgrade>().Single();

    [Fact]
    public void StartsActive_RegistersTheModifier_OnSpawn()
    {
        var game = NewGame();
        var bearer = game.SpawnObject("EagerBuffBearer", game.CivilianPlayer, Vector3.Zero);

        Assert.True(bearer.HasAttributeModifier("TestBuff"));
    }

    [Fact]
    public void UpgradeGated_RegistersOnlyWhenTriggered()
    {
        var game = NewGame();
        var bearer = game.SpawnObject("BuffBearer", game.CivilianPlayer, Vector3.Zero);

        Assert.False(bearer.HasAttributeModifier("TestBuff"));

        ModuleOf(bearer).TryUpgrade(BuffSet(game));

        Assert.True(bearer.HasAttributeModifier("TestBuff"));
    }

    [Fact]
    public void SecondTrigger_IsIdempotent()
    {
        var game = NewGame();
        var bearer = game.SpawnObject("BuffBearer", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(bearer);
        var upgrades = BuffSet(game);

        module.TryUpgrade(upgrades);
        module.TryUpgrade(upgrades);

        Assert.True(bearer.HasAttributeModifier("TestBuff"));
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var liveHost = game.SpawnObject("BuffBearer", game.CivilianPlayer, Vector3.Zero);
        var live = ModuleOf(liveHost);
        live.TryUpgrade(BuffSet(game));

        var shadowHost = game.SpawnObject("BuffBearer", game.CivilianPlayer, new Vector3(50, 0, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }
}
