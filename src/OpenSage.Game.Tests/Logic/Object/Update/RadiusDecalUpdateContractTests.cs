// Mocked-game contract tests for the RadiusDecalUpdate port (R12): one test per behavior
// branch, [create -> tick -> observable effect], plus the mid-behavior save/load round-trip
// and the shadow-copy base test.
//
// The module carries no INI-parsed fields of its own (see the GPL header - both fields are
// commented out in retail); the decal is driven entirely at runtime via CreateRadiusDecal/
// KillRadiusDecal/KillWhenNoLongerAttacking, mirroring how the GPL callers drive it. The
// module's public getters (IsDecalActive/DecalTemplateId/DecalRadius/DecalPosition) are the
// test observable, matching the pattern other light [SimState] modules use when they carry
// no client-visible model-condition output (e.g. SimHordeContain's public surface).

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class RadiusDecalUpdateContractTests
{
    private const string Definitions = @"
Object Caster
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = RadiusDecalUpdate ModuleTag_RadiusDecal
  End
End
";

    private static (HeadlessSimGame Game, GameObject Unit) Spawn()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, 0xDEC0);
        game.LoadIniText(Definitions);
        return (game, game.SpawnObject("Caster", game.CivilianPlayer, Vector3.Zero));
    }

    private static RadiusDecalUpdate ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<RadiusDecalUpdate>().Single();

    [Fact]
    public void CreateRadiusDecal_DisplaysAtTargetPosition()
    {
        var (_, unit) = Spawn();
        var module = ModuleOf(unit);
        var position = new FixVector3(Fix64.FromDecimalLiteral("10"), Fix64.FromDecimalLiteral("20"), Fix64.Zero);
        var radius = Fix64.FromDecimalLiteral("50");

        Assert.False(module.IsDecalActive);

        module.CreateRadiusDecal(templateId: 7, radius, position);

        Assert.True(module.IsDecalActive);
        Assert.Equal(7, module.DecalTemplateId);
        Assert.Equal(radius, module.DecalRadius);
        Assert.Equal(position, module.DecalPosition);
    }

    [Fact]
    public void Update_RefreshesDecalState_EachFrameWhileActive()
    {
        var (game, unit) = Spawn();
        var module = ModuleOf(unit);
        module.CreateRadiusDecal(1, Fix64.One, FixVector3.Zero);

        for (var i = 0; i < 5; i++)
        {
            game.Step();
            Assert.True(module.IsDecalActive);
        }
    }

    [Fact]
    public void KillOnNonAttack_Enabled_ClearsDecalWhenAttackingStops()
    {
        var (game, unit) = Spawn();
        var module = ModuleOf(unit);
        module.KillWhenNoLongerAttacking(true);
        module.CreateRadiusDecal(1, Fix64.One, FixVector3.Zero);

        // Attacking: the decal survives ticks.
        unit.SetObjectStatus(ObjectStatus.IsAttacking, true);
        game.Step();
        Assert.True(module.IsDecalActive);

        // Stops attacking: the next tick clears the decal and sleeps the module forever.
        unit.SetObjectStatus(ObjectStatus.IsAttacking, false);
        game.Step();
        Assert.False(module.IsDecalActive);
    }

    [Fact]
    public void KillOnNonAttack_Disabled_DecalPersistsWhenAttackingStops()
    {
        var (game, unit) = Spawn();
        var module = ModuleOf(unit);
        module.KillWhenNoLongerAttacking(false);
        module.CreateRadiusDecal(1, Fix64.One, FixVector3.Zero);

        unit.SetObjectStatus(ObjectStatus.IsAttacking, true);
        game.Step();
        unit.SetObjectStatus(ObjectStatus.IsAttacking, false);
        game.Step();

        Assert.True(module.IsDecalActive);
    }

    [Fact]
    public void KillRadiusDecal_EntersUpdateSleepForever()
    {
        var (_, unit) = Spawn();
        var module = ModuleOf(unit);
        module.CreateRadiusDecal(1, Fix64.One, FixVector3.Zero);

        module.KillRadiusDecal();

        Assert.False(module.IsDecalActive);
        Assert.Equal(0, module.DecalTemplateId);
        Assert.Equal(Fix64.Zero, module.DecalRadius);

        // Once inactive (and not killed-on-non-attack), Update() reports UPDATE_SLEEP_FOREVER.
        var sleep = module.Update();
        Assert.Equal(UpdateSleepTime.Forever.FrameSpan, sleep.FrameSpan);
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var (game, unit) = Spawn();
        var module = ModuleOf(unit);
        module.KillWhenNoLongerAttacking(true);
        module.CreateRadiusDecal(3, Fix64.FromDecimalLiteral("12"), new FixVector3(Fix64.One, Fix64.Two, Fix64.Zero));
        game.Step();

        var shadowHost = game.SpawnObject("Caster", game.CivilianPlayer, new Vector3(50, 0, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(module, shadow);
    }

    [Fact]
    public void SaveLoad_RestoresKillFlagAndDecalState()
    {
        var (game, unit) = Spawn();
        var module = ModuleOf(unit);
        module.KillWhenNoLongerAttacking(true);
        var position = new FixVector3(Fix64.FromDecimalLiteral("5"), Fix64.FromDecimalLiteral("6"), Fix64.FromDecimalLiteral("7"));
        var radius = Fix64.FromDecimalLiteral("25");
        module.CreateRadiusDecal(9, radius, position);
        game.Step();

        var state = PortedModuleTestKit.Save(module);

        var otherHost = game.SpawnObject("Caster", game.CivilianPlayer, new Vector3(100, 0, 0));
        var other = ModuleOf(otherHost);
        PortedModuleTestKit.Load(other, state);

        Assert.True(other.KillWhenNoLongerAttackingFlag);
        Assert.True(other.IsDecalActive);
        Assert.Equal(9, other.DecalTemplateId);
        Assert.Equal(radius, other.DecalRadius);
        Assert.Equal(position, other.DecalPosition);
    }
}
