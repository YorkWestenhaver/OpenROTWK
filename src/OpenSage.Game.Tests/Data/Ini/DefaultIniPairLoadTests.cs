using System.Linq;
using OpenSage.Content;
using OpenSage.IO;
using OpenSage.Logic.Object;
using Xunit;

namespace OpenSage.Tests.Data.Ini;

/// <summary>
/// A few INI files never appear in subsystemlegend.ini; the engine loads them by name, and always
/// as a pair — <c>Data\INI\Default\X.ini</c> first, then <c>Data\INI\X.ini</c> over the top of it.
/// Water is the case that matters: this engine loaded the second half and skipped the first, and
/// the first half is where a mod parks the <c>#define</c> constants the rest of its data is written
/// against, plus a <c>GameData</c> overlay. A missing define is not a parse error at the point of
/// definition — it detonates at every use site, one dropped top-level block at a time, which is why
/// the symptom (thousands of blocks missing across the tree) looked nothing like the cause (one
/// unloaded file).
/// </summary>
public class DefaultIniPairLoadTests
{
    private const string DefaultWaterPath = @"Data\INI\Default\Water.ini";
    private const string WaterPath = @"Data\INI\Water.ini";

    /// <summary>
    /// The Default half is loaded first so the override half can overwrite it — the reverse order
    /// would silently invert every setting the pair exists to express.
    /// </summary>
    [Fact]
    public void DefaultHalfComesBeforeTheOverrideHalf()
    {
        var paths = SubsystemLoader.ExpandDefaultPair("Water.ini").ToArray();

        Assert.Equal(new[] { DefaultWaterPath, WaterPath }, paths);
    }

    [Fact]
    public void BothHalvesAreLoadedWhenBothExist()
    {
        var fileSystem = new InMemoryFileSystem()
            .AddFile(DefaultWaterPath, "")
            .AddFile(WaterPath, "");

        var resolved = SubsystemLoader
            .ResolveExistingFiles(fileSystem, SubsystemLoader.ExpandDefaultPair("Water.ini"))
            .Select(entry => entry.FilePath)
            .ToArray();

        Assert.Equal(2, resolved.Length);
        Assert.Equal(FileSystem.NormalizeFilePath(DefaultWaterPath), resolved[0]);
        Assert.Equal(FileSystem.NormalizeFilePath(WaterPath), resolved[1]);
    }

    /// <summary>
    /// Half a pair is routinely absent — a stock install may ship only the override, a mod only the
    /// default. Neither case may abort the boot, which is what dereferencing the null lookup did.
    /// </summary>
    [Fact]
    public void AbsentDefaultHalfIsSkippedRatherThanThrowing()
    {
        var fileSystem = new InMemoryFileSystem().AddFile(WaterPath, "");

        var resolved = SubsystemLoader
            .ResolveExistingFiles(fileSystem, SubsystemLoader.ExpandDefaultPair("Water.ini"))
            .ToArray();

        Assert.Equal(FileSystem.NormalizeFilePath(WaterPath), Assert.Single(resolved).FilePath);
    }

    [Fact]
    public void AbsentOverrideHalfIsSkippedRatherThanThrowing()
    {
        var fileSystem = new InMemoryFileSystem().AddFile(DefaultWaterPath, "");

        var resolved = SubsystemLoader
            .ResolveExistingFiles(fileSystem, SubsystemLoader.ExpandDefaultPair("Water.ini"))
            .ToArray();

        Assert.Equal(FileSystem.NormalizeFilePath(DefaultWaterPath), Assert.Single(resolved).FilePath);
    }

    [Fact]
    public void APairWithNeitherHalfPresentLoadsNothing()
    {
        var fileSystem = new InMemoryFileSystem();

        var resolved = SubsystemLoader
            .ResolveExistingFiles(fileSystem, SubsystemLoader.ExpandDefaultPair("Water.ini"))
            .ToArray();

        Assert.Empty(resolved);
    }

    /// <summary>
    /// The whole point of the Default half: its <c>#define</c>s outlive the file they are declared
    /// in and are visible to everything parsed afterwards, because the macro table lives on the
    /// shared <see cref="OpenSage.Data.Ini.IniDataContext"/>, not on the parser.
    /// </summary>
    [Fact]
    public void DefinesFromTheDefaultHalfReachLaterFiles()
    {
        var context = new IniParseTestContext();

        context.ParseFileText(
            "#define TEST_BUILD_HEIGHT_VARIATION 25.0\n",
            DefaultWaterPath);

        var parser = context.ParseFileText(
            "GameData\n" +
            "  AllowedHeightVariationForBuilding = TEST_BUILD_HEIGHT_VARIATION\n" +
            "End\n",
            @"Data\INI\GameData.ini");

        Assert.Empty(parser.ParseErrors);
        Assert.Equal(25.0f, context.AssetStore.GameData.Current.AllowedHeightVariationForBuilding);
    }

    /// <summary>
    /// The failure this fix removes, stated as a test: with the Default half unloaded the macro is
    /// unknown, the use site throws, and error containment drops the entire enclosing top-level
    /// block. One skipped file, arbitrarily many silently missing blocks.
    /// </summary>
    [Fact]
    public void WithoutTheDefaultHalfTheConsumingBlockIsDropped()
    {
        var context = new IniParseTestContext();

        var parser = context.ParseFileText(
            "GameData\n" +
            "  AllowedHeightVariationForBuilding = TEST_BUILD_HEIGHT_VARIATION\n" +
            "End\n",
            @"Data\INI\GameData.ini");

        Assert.NotEmpty(parser.ParseErrors);
        Assert.NotEqual(25.0f, context.AssetStore.GameData.Current.AllowedHeightVariationForBuilding);
    }

    /// <summary>
    /// Multi-token object filters are the commonest shape of these macros, and the one victory
    /// detection depends on: the Default half defines the filter, a later file spends it.
    /// </summary>
    [Fact]
    public void MultiTokenFilterDefinedInTheDefaultHalfSurvivesIntoGameData()
    {
        var context = new IniParseTestContext();

        context.ParseFileText(
            "#define TEST_VICTORY_STRUCTURE_FILTER NONE +STRUCTURE -WALL_SEGMENT\n",
            DefaultWaterPath);

        var parser = context.ParseFileText(
            "GameData\n" +
            "  VictoryConditionStructureObjectFilter = TEST_VICTORY_STRUCTURE_FILTER\n" +
            "End\n",
            WaterPath);

        Assert.Empty(parser.ParseErrors);

        var filter = context.AssetStore.GameData.Current.VictoryConditionStructureObjectFilter;
        Assert.True(filter.Rules.Get(ObjectFilterRule.None));
        Assert.True(filter.Include.Get(ObjectKinds.Structure));
        Assert.True(filter.Exclude.Get(ObjectKinds.WallSegment));
    }

    /// <summary>
    /// Load order is the mechanism, not an accident: whatever the override half sets last wins.
    /// </summary>
    [Fact]
    public void OverrideHalfWinsOverTheDefaultHalf()
    {
        var context = new IniParseTestContext();

        context.ParseFileText(
            "GameData\n" +
            "  AllowedHeightVariationForBuilding = 25.0\n" +
            "End\n",
            DefaultWaterPath);

        context.ParseFileText(
            "GameData\n" +
            "  AllowedHeightVariationForBuilding = 40.0\n" +
            "End\n",
            WaterPath);

        Assert.Equal(40.0f, context.AssetStore.GameData.Current.AllowedHeightVariationForBuilding);
    }

    /// <summary>
    /// The Default half is parsed once per boot, but its macro names are ordinary tokens: a
    /// redefinition later in the load must register under the name being defined, never under the
    /// expansion of the name it is replacing.
    /// </summary>
    [Fact]
    public void RedefiningADefaultHalfMacroLaterKeepsTheName()
    {
        var context = new IniParseTestContext();

        context.ParseFileText("#define TEST_HEIGHT 25.0\n", DefaultWaterPath);
        context.ParseFileText("#define TEST_HEIGHT 40.0\n", WaterPath);

        var parser = context.ParseFileText(
            "GameData\n" +
            "  AllowedHeightVariationForBuilding = TEST_HEIGHT\n" +
            "End\n",
            @"Data\INI\GameData.ini");

        Assert.Empty(parser.ParseErrors);
        Assert.Equal(40.0f, context.AssetStore.GameData.Current.AllowedHeightVariationForBuilding);
    }
}
