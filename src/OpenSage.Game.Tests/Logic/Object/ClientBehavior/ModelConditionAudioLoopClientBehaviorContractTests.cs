// Mocked-game contract tests for the ModelConditionAudioLoopClientBehavior port (R12): the
// audio-only module parses, and its runtime module (once instantiated) round-trips its empty
// state, mirroring LargeGroupAudioUpdateContractTests (R11 Track B).
//
// This module lives under ObjectDefinition.ClientBehaviors, which GameObject's
// module-instantiation walk iterates alongside Behaviors (GameObject.cs, R12) - so
// game.SpawnObject already attaches a live instance to unit.BehaviorModules. The runtime
// tests below retrieve that real, live-attached instance (the MissileLauncherBuildingUpdate
// ModuleOf pattern) rather than hand-constructing a second orphan instance, so a
// double-instantiation, wrong-tag, or silent attach failure on the real object would be
// caught.

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.ClientBehavior;

public class ModelConditionAudioLoopClientBehaviorContractTests
{
    private static (HeadlessSimGame Game, GameObject Unit) Spawn(string clientBehaviorBlock)
    {
        var definitions = $@"
Object AudioLoopUnit
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  ClientBehavior = ModelConditionAudioLoopClientBehavior ModuleTag_AudioLoop
    {clientBehaviorBlock}
  End
End
";
        var game = new HeadlessSimGame(SageGame.Bfme2, 0xAD1);
        game.LoadIniText(definitions);
        return (game, game.SpawnObject("AudioLoopUnit", game.CivilianPlayer, Vector3.Zero));
    }

    private static ModelConditionAudioLoopClientBehaviorData GetData(HeadlessSimGame game)
    {
        return (ModelConditionAudioLoopClientBehaviorData)game.AssetStore.ObjectDefinitions
            .GetByName("AudioLoopUnit").ClientBehaviors["ModuleTag_AudioLoop"].Data;
    }

    private static ModelConditionAudioLoopClientBehavior ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<ModelConditionAudioLoopClientBehavior>().Single();

    [Fact]
    public void ParsesRequired_UppercaseAndMixedCaseKeyword()
    {
        var (gameUpper, _) = Spawn("ModelCondition = REQUIRED:MOVING Sound:UnitMoveLoop");
        var dataUpper = GetData(gameUpper);
        Assert.True(dataUpper.ModelCondition.Required.Get(ModelConditionFlag.Moving));
        Assert.Equal("UnitMoveLoop", dataUpper.ModelCondition.Sound);

        var (gameMixed, _) = Spawn("ModelCondition = Required:MOVING Sound:UnitMoveLoop");
        var dataMixed = GetData(gameMixed);
        Assert.True(dataMixed.ModelCondition.Required.Get(ModelConditionFlag.Moving));
    }

    [Fact]
    public void ParsesExcluded_UppercaseAndMixedCaseKeyword()
    {
        var (gameUpper, _) = Spawn("ModelCondition = EXCLUDED:DYING Sound:UnitMoveLoop");
        var dataUpper = GetData(gameUpper);
        Assert.True(dataUpper.ModelCondition.Excluded.Get(ModelConditionFlag.Dying));

        var (gameMixed, _) = Spawn("ModelCondition = Excluded:DYING Sound:UnitMoveLoop");
        var dataMixed = GetData(gameMixed);
        Assert.True(dataMixed.ModelCondition.Excluded.Get(ModelConditionFlag.Dying));
    }

    [Fact]
    public void ParsesCombinedRequiredSoundExcluded_InSingleBlock()
    {
        var (game, _) = Spawn("ModelCondition = REQUIRED:MOVING Sound:UnitMoveLoop EXCLUDED:DYING");
        var data = GetData(game);

        Assert.True(data.ModelCondition.Required.Get(ModelConditionFlag.Moving));
        Assert.Equal("UnitMoveLoop", data.ModelCondition.Sound);
        Assert.True(data.ModelCondition.Excluded.Get(ModelConditionFlag.Dying));
    }

    [Fact]
    public void ParsesSoundAssetReference_ValidAndMissing()
    {
        var (gameWithSound, _) = Spawn("ModelCondition = REQUIRED:MOVING Sound:UnitMoveLoop");
        Assert.Equal("UnitMoveLoop", GetData(gameWithSound).ModelCondition.Sound);

        // Sound omitted entirely: the field stays unset rather than defaulting to a sentinel.
        var (gameWithoutSound, _) = Spawn("ModelCondition = REQUIRED:MOVING");
        Assert.Null(GetData(gameWithoutSound).ModelCondition.Sound);
    }

    [Fact]
    public void ParsesRequired_BitArrayCompositionWithMultipleFlags()
    {
        // Multiple flags are space-separated tokens after the REQUIRED: keyword; the in-line
        // bit-array scan consumes flag tokens until it hits one that is not a flag name (here
        // the following "Sound" attribute). '+'/'-' are per-token set/clear prefixes, not an
        // infix joiner, so "MOVING ATTACKING" - not "MOVING+ATTACKING" - is the composition form.
        var (game, _) = Spawn("ModelCondition = REQUIRED:MOVING ATTACKING Sound:UnitMoveLoop");
        var required = GetData(game).ModelCondition.Required;

        Assert.True(required.Get(ModelConditionFlag.Moving));
        Assert.True(required.Get(ModelConditionFlag.Attacking));
        Assert.False(required.Get(ModelConditionFlag.Dying));
    }

    [Fact]
    public void RuntimeModule_IsAttachedExactlyOnceToTheRealGameObject_AndStaysHarmless()
    {
        var (game, unit) = Spawn("ModelCondition = REQUIRED:MOVING Sound:UnitMoveLoop EXCLUDED:DYING");

        // GameObject's module-instantiation walk iterates ObjectDefinition.ClientBehaviors
        // alongside Behaviors (GameObject.cs, R12), so SpawnObject must already have attached
        // exactly one live instance - not zero (silent attach failure) and not two
        // (double-instantiation). OfType<T>().Single() throws if that count is ever wrong.
        Assert.Single(unit.BehaviorModules.OfType<ModelConditionAudioLoopClientBehavior>());
        var module = ModuleOf(unit);

        for (var i = 0; i < 10; i++)
        {
            game.Step();
        }
        Assert.False(unit.IsDestroyed);
        Assert.NotNull(module);
    }

    [Fact]
    public void RuntimeModule_ShadowCopy_CrcEqualsLiveCrc()
    {
        var (game, unit) = Spawn("ModelCondition = REQUIRED:MOVING Sound:UnitMoveLoop EXCLUDED:DYING");
        var data = GetData(game);

        // Compare the CRC of the real, live-attached instance against a freshly constructed
        // shadow copy - not two hand-built orphans that were never attached to unit.BehaviorModules.
        var live = ModuleOf(unit);
        var shadow = new ModelConditionAudioLoopClientBehavior(unit, game.GameEngine.SimContext, data);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }
}
