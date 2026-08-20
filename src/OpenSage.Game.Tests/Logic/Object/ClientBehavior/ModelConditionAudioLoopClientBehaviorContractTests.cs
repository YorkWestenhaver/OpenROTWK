// Mocked-game contract tests for the ModelConditionAudioLoopClientBehavior port (R12): the
// audio-only module parses, and its runtime module (once instantiated) round-trips its empty
// state, mirroring LargeGroupAudioUpdateContractTests (R11 Track B).
//
// Unlike LargeGroupAudioUpdate, this module lives under ObjectDefinition.ClientBehaviors,
// which GameObject's module-instantiation walk does not yet iterate (see the module header
// in ClientBehavior/ModelConditionAudioLoopClientBehavior.cs, F-R11-9). So the ClientBehavior
// entry is asserted purely at the data level (parsed fields on ObjectDefinition.ClientBehaviors),
// and the runtime module is exercised by constructing it directly against a real spawned
// GameObject and its real ISimContext - the same construction CreateModule performs, just
// invoked by hand until the seam wires it up automatically.

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
        var (game, _) = Spawn("ModelCondition = REQUIRED:MOVING+ATTACKING Sound:UnitMoveLoop");
        var required = GetData(game).ModelCondition.Required;

        Assert.True(required.Get(ModelConditionFlag.Moving));
        Assert.True(required.Get(ModelConditionFlag.Attacking));
        Assert.False(required.Get(ModelConditionFlag.Dying));
    }

    [Fact]
    public void RuntimeModule_ConstructsAgainstRealGameObject_AndStaysHarmless()
    {
        var (game, unit) = Spawn("ModelCondition = REQUIRED:MOVING Sound:UnitMoveLoop EXCLUDED:DYING");
        var data = GetData(game);

        var module = new ModelConditionAudioLoopClientBehavior(unit, game.GameEngine.SimContext, data);

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

        var live = new ModelConditionAudioLoopClientBehavior(unit, game.GameEngine.SimContext, data);
        var shadow = new ModelConditionAudioLoopClientBehavior(unit, game.GameEngine.SimContext, data);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }
}
