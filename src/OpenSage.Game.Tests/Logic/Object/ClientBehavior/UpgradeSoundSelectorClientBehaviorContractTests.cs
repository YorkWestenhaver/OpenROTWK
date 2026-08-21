// Mocked-game contract tests for the UpgradeSoundSelectorClientBehavior port (R13):
// the audio-only module parses its list of gated SoundUpgrade bundles (each gated by
// RequiredModelConditions AND ExcludedUpgrades), instantiates as a live (permanently parked)
// runtime module, and round-trips its empty state - the [ParseOnly] hole is closed without
// inventing sim behavior for a client-audio feature (see the module header, and
// ModelConditionSoundSelectorClientBehavior's header for the sibling finding this port
// templates from).
//
// NOTE on wiring: ClientBehaviorModuleData ("ClientBehavior = ..." in INI) is parsed into
// ObjectDefinition.ClientBehaviors, which - unlike ObjectDefinition.Behaviors - is never
// walked by GameObject's module-creation loop (GameObject.cs has no ClientBehaviors
// consumer; only Drawable.cs consumes the sibling ClientUpdates dict). So a
// "ClientBehavior = UpgradeSoundSelectorClientBehavior" block never produces a live module via
// GameObject.SpawnObject today (scaffolding-log.md Task A0.1, Finding 2: this category sits
// under the CreateModule contract but outside the wired sim-object graph). These tests
// therefore exercise CreateModule directly against a spawned unit's own GameObject/IGameEngine
// rather than asserting the module shows up in GameObject.BehaviorModules (matching
// ModelConditionSoundSelectorClientBehaviorContractTests's own convention).
//
// The sleepy-update caveat (S6) does not apply here: UpgradeSoundSelectorClientBehavior is a
// BehaviorModule, not an UpdateModule - it has no Update() and is never enqueued on the
// SleepyUpdateQueue, so construction-time assertions are made immediately after CreateModule,
// with no second-Step() margin required.

using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.ClientBehavior;

public class UpgradeSoundSelectorClientBehaviorContractTests
{
    private const string Definitions = @"
Object VocalVeteran
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  ClientBehavior = UpgradeSoundSelectorClientBehavior ModuleTag_SoundSelect
    SoundUpgrade
      RequiredModelConditions = MOVING
      ExcludedUpgrades = Upgrade_SilentMode
      VoiceSelect = VocalVeteranVoiceSelect
      VoiceAttack = VocalVeteranVoiceAttack
      VoiceMove = VocalVeteranVoiceMove
      VoicePriority = 3
      SoundImpact = VocalVeteranImpact
      UnitSpecificSounds
        VoiceEject = VocalVeteranEject
      End
    End
    SoundUpgrade
      RequiredModelConditions = GARRISONED
      VoiceSelect = VocalVeteranEliteVoiceSelect
      VoicePriority = 5
    End
  End
End
";

    private static (HeadlessSimGame Game, GameObject Unit) Spawn()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, 0xAD2);
        game.LoadIniText(Definitions);
        return (game, game.SpawnObject("VocalVeteran", game.CivilianPlayer, Vector3.Zero));
    }

    private static UpgradeSoundSelectorClientBehaviorData GetData(HeadlessSimGame game) =>
        (UpgradeSoundSelectorClientBehaviorData)game.AssetStore.ObjectDefinitions
            .GetByName("VocalVeteran").ClientBehaviors["ModuleTag_SoundSelect"].Data;

    [Fact]
    public void ParsesAndCreatesRuntimeModule()
    {
        var (game, unit) = Spawn();
        var data = GetData(game);

        var module = data.CreateModule(unit, game.GameEngine);
        Assert.NotNull(module);
        Assert.IsType<UpgradeSoundSelectorClientBehavior>(module);
    }

    [Fact]
    public void SingleSoundUpgrade_FieldsParsed()
    {
        var (game, _) = Spawn();
        var data = GetData(game);

        Assert.Equal(2, data.SoundUpgrades.Count);

        var first = data.SoundUpgrades[0];
        Assert.True(first.RequiredModelConditions.Get(ModelConditionFlag.Moving));
        Assert.Equal(new[] { "Upgrade_SilentMode" }, first.ExcludedUpgrades);
        Assert.Equal("VocalVeteranVoiceSelect", first.VoiceSelect);
        Assert.Equal("VocalVeteranVoiceAttack", first.VoiceAttack);
        Assert.Equal("VocalVeteranVoiceMove", first.VoiceMove);
        Assert.Equal(3, first.VoicePriority);
        Assert.Equal("VocalVeteranImpact", first.SoundImpact);
    }

    [Fact]
    public void MultipleSoundUpgrade_BlocksParseInDeclarationOrder()
    {
        var (game, _) = Spawn();
        var data = GetData(game);

        Assert.Equal(2, data.SoundUpgrades.Count);

        var second = data.SoundUpgrades[1];
        Assert.True(second.RequiredModelConditions.Get(ModelConditionFlag.Garrisoned));
        Assert.Equal("VocalVeteranEliteVoiceSelect", second.VoiceSelect);
        Assert.Equal(5, second.VoicePriority);
        // Fields not set on this block stay at their type defaults - no cross-block bleed.
        Assert.Null(second.ExcludedUpgrades);
        Assert.Null(second.VoiceAttack);
    }

    [Fact]
    public void UnitSpecificSounds_NestedBlockParsed()
    {
        var (game, _) = Spawn();
        var data = GetData(game);

        var first = data.SoundUpgrades[0];
        Assert.NotNull(first.UnitSpecificSounds);
        Assert.True(first.UnitSpecificSounds.ContainsKey("VoiceEject"));
    }

    [Fact]
    public void ExcludedUpgrades_NamingUndefinedUpgrade_DoesNotThrowAtParseTime()
    {
        // Upgrade_SilentMode is never defined as an Upgrade block anywhere in Definitions;
        // ExcludedUpgrades is a raw string[] (same shape as ObjectDefinition.ExcludedUpgrades)
        // resolved only by a future audio-host consumer, not at parse or construction time.
        var (game, _) = Spawn();
        var data = GetData(game);

        Assert.Equal("Upgrade_SilentMode", data.SoundUpgrades[0].ExcludedUpgrades[0]);
    }

    [Fact]
    public void SteppingIsHarmless_ModuleStaysParked()
    {
        var (game, unit) = Spawn();
        var data = GetData(game);
        var module = (UpgradeSoundSelectorClientBehavior)data.CreateModule(unit, game.GameEngine);

        for (var i = 0; i < 10; i++)
        {
            game.Step();
        }

        Assert.False(unit.IsDestroyed);
        Assert.NotNull(module);
    }

    [Fact]
    public void SaveLoadRoundTrip_IsNoOp()
    {
        var (game, unit) = Spawn();
        var data = GetData(game);
        var module = (UpgradeSoundSelectorClientBehavior)data.CreateModule(unit, game.GameEngine);

        for (var i = 0; i < 3; i++)
        {
            game.Step();
        }

        var liveCrc = PortedModuleTestKit.LiveCrc(module);
        var saved = PortedModuleTestKit.Save(module);
        PortedModuleTestKit.Load(module, saved);

        Assert.Equal(liveCrc, PortedModuleTestKit.LiveCrc(module));
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc()
    {
        var (game, unit) = Spawn();
        var data = GetData(game);
        var live = (UpgradeSoundSelectorClientBehavior)data.CreateModule(unit, game.GameEngine);

        var shadowHost = game.SpawnObject("VocalVeteran", game.CivilianPlayer, new Vector3(50, 0, 0));
        var shadow = (UpgradeSoundSelectorClientBehavior)data.CreateModule(shadowHost, game.GameEngine);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }
}
