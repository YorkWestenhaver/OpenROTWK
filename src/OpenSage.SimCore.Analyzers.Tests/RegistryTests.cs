using System;
using System.IO;
using System.Linq;
using Xunit;

namespace OpenSage.SimCore.Analyzers.Tests;

/// <summary>
/// Guards the checked-in registry files themselves, so drift is caught by a test rather than
/// discovered when a rule silently stops applying.
/// </summary>
public class RegistryTests
{
    private static string[] ExemptionEntries =>
        ReadEntries(Path.Combine(TestReferences.SimCoreProjectDirectory, "SimCoreExemptions.txt"));

    [Fact]
    public void EveryExemptedFileExistsAndCarriesTheHeader()
    {
        Assert.NotEmpty(ExemptionEntries);

        foreach (var entry in ExemptionEntries)
        {
            var path = Path.Combine(TestReferences.SimCoreProjectDirectory, entry.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(File.Exists(path), "SimCoreExemptions.txt lists a file that does not exist: " + entry);
            Assert.Contains("// SIMCORE-EXEMPT:", File.ReadAllText(path));
        }
    }

    /// <summary>
    /// The other direction: a header comment left behind after a file was removed from the
    /// registry is dead weight that reads like an active exemption.
    /// </summary>
    [Fact]
    public void EveryHeaderedFileIsListedInTheRegistry()
    {
        var entries = ExemptionEntries;

        var headered = Directory
            .EnumerateFiles(TestReferences.SimCoreProjectDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                && !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Where(path => File.ReadAllText(path).Contains("// SIMCORE-EXEMPT:"))
            .ToArray();

        foreach (var path in headered)
        {
            var relative = path
                .Substring(TestReferences.SimCoreProjectDirectory.Length)
                .TrimStart(Path.DirectorySeparatorChar)
                .Replace(Path.DirectorySeparatorChar, '/');

            Assert.Contains(relative, entries, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The step-2 ruling on the step-1 finding: the F4 display escape was split into a file of
    /// its own so the exemption covers exactly one method, leaving the integer-only F4 parse
    /// boundaries fully policed.
    /// </summary>
    [Fact]
    public void ParseBoundariesAreNotExempt()
    {
        Assert.DoesNotContain("Numerics/Fix64.Parse.cs", ExemptionEntries);
        Assert.Contains("Numerics/Fix64.Display.cs", ExemptionEntries);
    }

    [Fact]
    public void BannedSymbolsFileParsesAndIsWiredUp()
    {
        var path = Path.Combine(TestReferences.SimCoreProjectDirectory, "BannedSymbols.txt");
        var entries = ReadEntries(path);

        Assert.NotEmpty(entries);
        Assert.All(entries, entry =>
        {
            var id = entry.Split(';')[0].Trim();
            Assert.True(id.Length > 2 && id[1] == ':', "malformed symbol id: " + entry);
            Assert.Contains(id[0], "NTM");
        });

        var csproj = File.ReadAllText(
            Path.Combine(TestReferences.SimCoreProjectDirectory, "OpenSage.SimCore.csproj"));

        Assert.Contains("AdditionalFiles Include=\"BannedSymbols.txt\"", csproj);
        Assert.Contains("AdditionalFiles Include=\"SimCoreExemptions.txt\"", csproj);
        Assert.Contains("OutputItemType=\"Analyzer\"", csproj);
    }

    /// <summary>
    /// Entries added to BannedSymbols.txt must actually take effect - the file is where the
    /// OPEN-10 nondeterminism inventory lands as it grows.
    /// </summary>
    [Fact]
    public void BannedSymbolsFileEntriesTakeEffect()
    {
        const string source = @"namespace Fixture
{
    public class Uses
    {
        public int Cores() => System.Environment.ProcessorCount;
    }
}
";

        // Environment.ProcessorCount is not in the analyzer's built-in seed list.
        Assert.Empty(AnalyzerHarness.Run(new[] { ("/repo/A.cs", source) }));

        var extended = AnalyzerHarness.Run(
            new[] { ("/repo/A.cs", source) },
            additionalFiles: new[]
            {
                ("/repo/BannedSymbols.txt", "M:System.Environment.ProcessorCount;SIMCORE005;fixture entry\n")
            });

        Assert.Contains(extended, d => d.Id == "SIMCORE005" && d.GetMessage().Contains("fixture entry"));
    }

    private static string[] ReadEntries(string path) => File.ReadAllLines(path)
        .Select(line =>
        {
            var hash = line.IndexOf('#');
            return (hash < 0 ? line : line.Substring(0, hash)).Trim();
        })
        .Where(line => line.Length != 0)
        .ToArray();
}
