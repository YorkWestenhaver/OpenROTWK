using Xunit;

namespace OpenSage.SimCore.Analyzers.Tests;

/// <summary>
/// Both attachment modes of design-simcore-scaffolding §2.2/§2.3, which the freeze (F10)
/// requires the analyzer to support:
///
///   * full   - always-on inside OpenSage.SimCore, every file is sim code;
///   * scoped - opt-in inside OpenSage.Game during migration, driven by SimCoreScopedDirs.txt
///              and by the [SimState] marker attribute.
/// </summary>
public class AttachmentModeTests
{
    private const string Violating = @"namespace OpenSage.Logic
{
    public class Mover
    {
        public double Speed;
    }
}
";

    private const string ViolatingSimState = @"namespace OpenSage.Logic
{
    [SimState]
    public class Mover
    {
        public double Speed;
    }

    public class SimStateAttribute : System.Attribute
    {
    }
}
";

    private const string GamePath = "/repo/src/OpenSage.Game/Logic/Object/Mover.cs";

    [Fact]
    public void FullModeAnalyzesEveryFile()
    {
        var diagnostics = AnalyzerHarness.Run(new[] { (GamePath, Violating) }, mode: "full");

        Assert.Contains(diagnostics, d => d.Id == "SIMCORE001");
    }

    [Fact]
    public void ScopedModeIgnoresUnlistedDirectories()
    {
        var diagnostics = AnalyzerHarness.Run(
            new[] { (GamePath, Violating) },
            mode: "scoped",
            additionalFiles: new[] { ("/repo/src/OpenSage.Game/SimCoreScopedDirs.txt", "# empty\n") });

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void ScopedModeAnalyzesListedDirectories()
    {
        var diagnostics = AnalyzerHarness.Run(
            new[] { (GamePath, Violating) },
            mode: "scoped",
            additionalFiles: new[] { ("/repo/src/OpenSage.Game/SimCoreScopedDirs.txt", "Logic/Object\n") });

        Assert.Contains(diagnostics, d => d.Id == "SIMCORE001");
    }

    [Fact]
    public void ScopedModeAnalyzesSimStateMarkedFiles()
    {
        var diagnostics = AnalyzerHarness.Run(
            new[] { ("/repo/src/OpenSage.Game/Elsewhere/Mover.cs", ViolatingSimState) },
            mode: "scoped",
            additionalFiles: new[] { ("/repo/src/OpenSage.Game/SimCoreScopedDirs.txt", "# empty\n") });

        Assert.Contains(diagnostics, d => d.Id == "SIMCORE001");
    }

    /// <summary>
    /// The mode property can be lost (a project that forgets CompilerVisibleProperty, or a
    /// design-time build). SimCore must not silently fall out of the quarantine when that
    /// happens, so the assembly name is a second, independent trigger for full mode.
    /// </summary>
    [Fact]
    public void SimCoreAssemblyIsAnalyzedEvenWithoutTheModeProperty()
    {
        var diagnostics = AnalyzerHarness.Run(
            new[] { ("/repo/src/OpenSage.SimCore/Numerics/Thing.cs", Violating) },
            assemblyName: "OpenSage.SimCore",
            mode: null);

        Assert.Contains(diagnostics, d => d.Id == "SIMCORE001");
    }

    [Fact]
    public void OtherAssembliesDefaultToScopedWhenTheModePropertyIsMissing()
    {
        var diagnostics = AnalyzerHarness.Run(
            new[] { (GamePath, Violating) },
            assemblyName: "OpenSage.Game",
            mode: null);

        Assert.Empty(diagnostics);
    }
}
