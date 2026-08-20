// Mocked-game contract tests for the ModelConditionSoundSelectorClientBehavior port (R12):
// the audio-only module parses its condition-indexed SoundState config, instantiates as a
// live (permanently parked) runtime module, and round-trips its empty state - the
// [ParseOnly] hole is closed without inventing sim behavior for a client-audio feature (see
// the module header, and LargeGroupAudioUpdate's header for the sibling finding).
//
// NOTE on wiring: ClientBehaviorModuleData ("ClientBehavior = ..." in INI) is parsed into
// ObjectDefinition.ClientBehaviors, which - unlike ObjectDefinition.Behaviors - is never
// walked by GameObject's module-creation loop (GameObject.cs has no ClientBehaviors
// consumer; only Drawable.cs consumes the sibling ClientUpdates dict). So a
// "ClientBehavior = ModelConditionSoundSelectorClientBehavior" block never produces a live
// module via GameObject.SpawnObject today (scaffolding-log.md Task A0.1, Finding 2: this
// category sits under the CreateModule contract but outside the wired sim-object graph).
// These tests therefore exercise CreateModule directly against a spawned unit's own
// GameObject/IGameEngine rather than asserting the module shows up in
// GameObject.BehaviorModules.

using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.ClientBehavior;

public class ModelConditionSoundSelectorClientBehaviorContractTests
{
    private const string Definitions = @"
Object VocalGrunt
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  ClientBehavior = ModelConditionSoundSelectorClientBehavior ModuleTag_SoundSelect
    SoundState = MOVING
      VoiceSelect = VocalGruntVoiceSelect
      VoiceAttack = VocalGruntVoiceAttack
      VoiceMove = VocalGruntVoiceMove
      VoicePriority = 3
      SoundMoveStart = VocalGruntMoveStart
      SoundImpact = VocalGruntImpact
      UnitSpecificSounds
        VoiceEject = VocalGruntEject
      End
    End
  End
End
";

    private static (HeadlessSimGame Game, GameObject Unit) Spawn()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, 0xAD1);
        game.LoadIniText(Definitions);
        return (game, game.SpawnObject("VocalGrunt", game.CivilianPlayer, Vector3.Zero));
    }

    private static ModelConditionSoundSelectorClientBehaviorData GetData(HeadlessSimGame game) =>
        (ModelConditionSoundSelectorClientBehaviorData)game.AssetStore.ObjectDefinitions
            .GetByName("VocalGrunt").ClientBehaviors["ModuleTag_SoundSelect"].Data;

    [Fact]
    public void ParsesAndCreatesRuntimeModule()
    {
        var (game, unit) = Spawn();
        var data = GetData(game);

        var module = data.CreateModule(unit, game.GameEngine);
        Assert.NotNull(module);
        Assert.IsType<ModelConditionSoundSelectorClientBehavior>(module);
    }

    [Fact]
    public void SoundState_ConditionAndAssetReferencesParsed()
    {
        var (game, _) = Spawn();
        var data = GetData(game);

        Assert.Equal(ModelConditionFlag.Moving, data.SoundState.Condition);
        Assert.Equal("VocalGruntVoiceSelect", data.SoundState.VoiceSelect);
        Assert.Equal("VocalGruntVoiceAttack", data.SoundState.VoiceAttack);
        Assert.Equal("VocalGruntVoiceMove", data.SoundState.VoiceMove);
    }

    [Fact]
    public void VoicePriority_IntegerFieldParsed()
    {
        var (game, _) = Spawn();
        var data = GetData(game);

        Assert.Equal(3, data.SoundState.VoicePriority);
    }

    [Fact]
    public void SoundMoveStartAndSoundImpact_Parsed()
    {
        var (game, _) = Spawn();
        var data = GetData(game);

        Assert.Equal("VocalGruntMoveStart", data.SoundState.SoundMoveStart);
        Assert.Equal("VocalGruntImpact", data.SoundState.SoundImpact);
    }

    [Fact]
    public void UnitSpecificSounds_NestedBlockParsed()
    {
        var (game, _) = Spawn();
        var data = GetData(game);

        Assert.NotNull(data.SoundState.UnitSpecificSounds);
        Assert.True(data.SoundState.UnitSpecificSounds.ContainsKey("VoiceEject"));
    }

    [Fact]
    public void SteppingIsHarmless_ModuleStaysParked()
    {
        var (game, unit) = Spawn();
        var data = GetData(game);
        var module = (ModelConditionSoundSelectorClientBehavior)data.CreateModule(unit, game.GameEngine);

        for (var i = 0; i < 10; i++)
        {
            game.Step();
        }

        Assert.False(unit.IsDestroyed);
        Assert.NotNull(module);
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc()
    {
        var (game, unit) = Spawn();
        var data = GetData(game);
        var live = (ModelConditionSoundSelectorClientBehavior)data.CreateModule(unit, game.GameEngine);

        var shadowHost = game.SpawnObject("VocalGrunt", game.CivilianPlayer, new Vector3(50, 0, 0));
        var shadow = (ModelConditionSoundSelectorClientBehavior)data.CreateModule(shadowHost, game.GameEngine);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }
}
