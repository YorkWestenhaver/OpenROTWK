// Contract tests for the AnimationSoundClientBehavior port (R12): the audio-only module
// parses fully and its runtime module (a permanently-parked, empty-state leaf, matching
// LargeGroupAudioUpdate's pattern) constructs and round-trips - the [ParseOnly] hole is
// closed without inventing sim behavior for a client-audio feature (see the module header).
//
// ClientBehavior entries live in ObjectDefinition.ClientBehaviors, a dictionary GameObject's
// module-instantiation walk does not read yet (F-R11-9), so unlike a Behavior-category port
// there is no HeadlessSimGame.SpawnObject path that puts this module on
// GameObject.BehaviorModules. The runtime-construction test below therefore calls
// AnimationSoundClientBehaviorData.CreateModule directly, using a real (unrelated) spawned
// GameObject + IGameEngine as the construction context, so the exact code path the seam will
// eventually call is already exercised.

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.Tests.Data.Ini;
using Xunit;

namespace OpenSage.Tests.Logic.Object.ClientBehavior;

public class AnimationSoundClientBehaviorContractTests
{
    private static ObjectDefinition ParseObject(IniParseTestContext context, string body)
    {
        var parser = context.ParseFileText(
            "Object AnimationSoundTestObject\n" + body + "End\n");

        Assert.Empty(parser.ParseErrors);

        var definition = context.AssetStore.ObjectDefinitions.GetByName("AnimationSoundTestObject");
        Assert.NotNull(definition);
        return definition;
    }

    private static AnimationSoundClientBehaviorData ParseAnimationSoundClientBehavior(string body)
    {
        var context = new IniParseTestContext();
        var definition = ParseObject(context,
            "  ClientBehavior = AnimationSoundClientBehavior ModuleTag_Sound\n" +
            body +
            "  End\n");

        return Assert.IsType<AnimationSoundClientBehaviorData>(
            Assert.Single(definition.ClientBehaviors).Value.Data);
    }

    [Fact]
    public void MultipleAnimationSoundEntriesAreAllStored()
    {
        var data = ParseAnimationSoundClientBehavior(
            "    AnimationSound = Sound:SwordSwing Animation:Attack Frames:3\n" +
            "    AnimationSound = Sound:FootStep Animation:Walk Frames:1\n" +
            "    AnimationSound = Sound:FootStep Animation:Run Frames:1\n");

        Assert.Equal(3, data.AnimationSounds.Count);
        Assert.Equal("SwordSwing", data.AnimationSounds[0].Sound);
        Assert.Equal("FootStep", data.AnimationSounds[1].Sound);
        Assert.Equal("FootStep", data.AnimationSounds[2].Sound);
    }

    [Fact]
    public void MultipleAnimationNamesAndFramesArraysAreParsedCorrectly()
    {
        var data = ParseAnimationSoundClientBehavior(
            "    AnimationSound = Sound:SwordSwing Animation:Attack Animation:AttackA Animation:AttackB " +
            "Frames:2 Frames:4 7\n");

        var entry = Assert.Single(data.AnimationSounds);
        Assert.Equal(new[] { "Attack", "AttackA", "AttackB" }, entry.Animations);
        Assert.Equal(2, entry.Frames.Count);
        Assert.Equal(new[] { 2 }, entry.Frames[0]);
        Assert.Equal(new[] { 4, 7 }, entry.Frames[1]);
    }

    [Fact]
    public void RequiredMCAndExcludedMCAreParsedAndStored()
    {
        var data = ParseAnimationSoundClientBehavior(
            "    AnimationSound = Sound:SwordSwing Animation:Attack Frames:3 " +
            "RequiredMC:MOVING ExcludedMC:DYING\n");

        var entry = Assert.Single(data.AnimationSounds);
        Assert.Equal(ModelConditionFlag.Moving, entry.RequiredMC);
        Assert.Equal(ModelConditionFlag.Dying, entry.ExcludedMC);
    }

    [Fact]
    public void MaxUpdateRangeCapIsParsedAndClampedToValidRange()
    {
        var positive = ParseAnimationSoundClientBehavior("    MaxUpdateRangeCap = 250\n");
        Assert.Equal(250, positive.MaxUpdateRangeCap);

        var negative = ParseAnimationSoundClientBehavior("    MaxUpdateRangeCap = -50\n");
        Assert.Equal(0, negative.MaxUpdateRangeCap);
    }

    [Fact]
    public void EmptyAnimationSoundsListWhenNoEntriesPresent()
    {
        var data = ParseAnimationSoundClientBehavior("    MaxUpdateRangeCap = 100\n");

        Assert.Empty(data.AnimationSounds);
    }

    [Fact]
    public void SoundAssetReferenceIsParsedAndStoredForEachEntry()
    {
        var data = ParseAnimationSoundClientBehavior(
            "    AnimationSound = Sound:ImpactMetal Animation:Hit Frames:0\n");

        Assert.Equal("ImpactMetal", Assert.Single(data.AnimationSounds).Sound);
    }

    [Fact]
    public void CreateModuleConstructsAndRoundTripsThePermanentlyParkedRuntimeModule()
    {
        // GameObject -> ClientBehaviors instantiation is blocked (F-R11-9), so this exercises
        // CreateModule directly rather than through HeadlessSimGame.SpawnObject.
        var game = new HeadlessSimGame(SageGame.Bfme2, 0xA5D);
        game.LoadIniText(@"
Object AnimationSoundHost
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  ClientBehavior = AnimationSoundClientBehavior ModuleTag_Sound
    MaxUpdateRangeCap = 200
    AnimationSound = Sound:SwordSwing Animation:Attack Frames:3
  End
End
");
        var host = game.SpawnObject("AnimationSoundHost", game.CivilianPlayer, Vector3.Zero);

        var data = (AnimationSoundClientBehaviorData)game.AssetStore.ObjectDefinitions
            .GetByName("AnimationSoundHost").ClientBehaviors["ModuleTag_Sound"].Data;

        var live = Assert.IsType<AnimationSoundClientBehavior>(data.CreateModule(host, game.GameEngine));
        var shadow = Assert.IsType<AnimationSoundClientBehavior>(data.CreateModule(host, game.GameEngine));
        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void SteppingIsHarmless_ModuleStaysParked()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, 0xA5E);
        game.LoadIniText(@"
Object AnimationSoundHost2
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  ClientBehavior = AnimationSoundClientBehavior ModuleTag_Sound
    AnimationSound = Sound:SwordSwing Animation:Attack Frames:3
  End
End
");
        var unit = game.SpawnObject("AnimationSoundHost2", game.CivilianPlayer, Vector3.Zero);
        for (var i = 0; i < 10; i++)
        {
            game.Step();
        }
        Assert.False(unit.IsDestroyed);

        // The ClientBehaviors instantiation seam (F-R11-9) has since landed, so the module is
        // now a live module on the spawned object - and, being permanently parked, stepping it
        // ten frames changes nothing about the object.
        Assert.Single(unit.BehaviorModules.OfType<AnimationSoundClientBehavior>());
    }
}
