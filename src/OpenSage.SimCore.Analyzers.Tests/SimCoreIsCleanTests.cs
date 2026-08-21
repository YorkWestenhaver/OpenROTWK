using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Xunit;

namespace OpenSage.SimCore.Analyzers.Tests;

/// <summary>
/// The step-2 gate, second half: "SimCore itself compiles clean". <c>dotnet build</c> already
/// proves this (the analyzer is attached to OpenSage.SimCore with SIMCORE001-007 as errors),
/// but that evidence lives in build output. These tests re-run the whole rule set over the
/// real SimCore sources and registry files from disk, so the gate is an assertion that fails
/// a test run rather than a line someone has to notice in a log.
/// </summary>
public class SimCoreIsCleanTests
{
    private static IEnumerable<string> SimCoreSourceFiles =>
        Directory.EnumerateFiles(TestReferences.SimCoreProjectDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                && !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .OrderBy(path => path, StringComparer.Ordinal);

    [Fact]
    public void SimCoreSourcesAreFound()
    {
        Assert.True(SimCoreSourceFiles.Count() >= 10);
    }

    [Fact]
    public void SimCoreProducesNoDiagnostics()
    {
        var sources = SimCoreSourceFiles
            .Select(path => (Path: path, Text: File.ReadAllText(path)))
            .ToArray();

        var registries = new[]
        {
            ReadRegistry("SimCoreExemptions.txt"),
            ReadRegistry("BannedSymbols.txt"),
        };

        var diagnostics = AnalyzerHarness.Run(
            sources,
            assemblyName: "OpenSage.SimCore",
            mode: "full",
            additionalFiles: registries,
            extraReferences: new[] { TestReferences.SimCore });

        Assert.True(
            diagnostics.IsEmpty,
            "SimCore is not clean under its own quarantine:\n  "
                + string.Join("\n  ", diagnostics.Select(Describe)));
    }

    /// <summary>
    /// The mirror image: with the exemption registry withheld, the two guess-and-fixup files
    /// must light up. Without this, "SimCore is clean" could silently mean "the analyzer is
    /// not running", and the exemption pair would be untested against real code.
    /// </summary>
    [Fact]
    public void SimCoreIsOnlyCleanBecauseTheExemptionsAreHonoured()
    {
        var sources = SimCoreSourceFiles
            .Select(path => (Path: path, Text: File.ReadAllText(path)))
            .ToArray();

        var diagnostics = AnalyzerHarness.Run(
            sources,
            assemblyName: "OpenSage.SimCore",
            mode: "full",
            additionalFiles: new[] { ("SimCoreExemptions.txt", "# registry withheld\n") },
            extraReferences: new[] { TestReferences.SimCore });

        Assert.NotEmpty(diagnostics);

        var files = diagnostics
            .Select(d => Path.GetFileName(d.Location.SourceTree?.FilePath ?? string.Empty))
            .Distinct()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "Fix64.Display.cs",
                "Fix64.Division.cs",
                "Fix64.Sqrt.cs",
                // Step 4 (LogicFrame move): float legacy conveniences still called by the
                // unmigrated OpenSage.Game float sim; deleted with their callers per F11.
                "LogicFrameSpan.FloatCompat.cs",
            },
            files);
    }

    private static (string Path, string Text) ReadRegistry(string fileName)
    {
        var path = Path.Combine(TestReferences.SimCoreProjectDirectory, fileName);
        return (path, File.ReadAllText(path));
    }

    private static string Describe(Diagnostic diagnostic)
    {
        var span = diagnostic.Location.GetLineSpan();
        return Path.GetFileName(span.Path)
            + "(" + (span.StartLinePosition.Line + 1) + "): "
            + diagnostic.Id + " " + diagnostic.GetMessage();
    }
}
