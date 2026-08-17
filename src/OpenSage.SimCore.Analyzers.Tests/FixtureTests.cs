using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Xunit;

namespace OpenSage.SimCore.Analyzers.Tests
{
    /// <summary>
    /// The step-2 gate, first half: "rules demonstrably fire in a fixture".
    ///
    /// Each file under Fixtures/ is deliberately-violating C#. A line that must produce a
    /// diagnostic carries a trailing <c>// EXPECT: SIMCOREnnn</c>; every other line must be
    /// silent. The assertion is two-way - a rule that stops firing fails, and so does a rule that
    /// starts firing somewhere it should not, which is what keeps the quarantine usable.
    /// </summary>
    public class FixtureTests
    {
        public static TheoryData<string> Fixtures
        {
            get
            {
                var data = new TheoryData<string>();

                foreach (var path in Directory.EnumerateFiles(FixtureDirectory, "*.cs.txt").OrderBy(p => p))
                {
                    data.Add(Path.GetFileName(path));
                }

                return data;
            }
        }

        private static string FixtureDirectory => Path.Combine(AppContext.BaseDirectory, "Fixtures");

        /// <summary>
        /// Fixtures run against the checked-in BannedSymbols.txt, so the additive registry - not
        /// just the analyzer's built-in seed list - is exercised by the gate.
        /// </summary>
        private static (string Path, string Text)[] Registries
        {
            get
            {
                var path = Path.Combine(TestReferences.SimCoreProjectDirectory, "BannedSymbols.txt");
                return new[] { (path, File.ReadAllText(path)) };
            }
        }

        [Fact]
        public void FixturesAreDiscovered()
        {
            Assert.True(Directory.EnumerateFiles(FixtureDirectory, "*.cs.txt").Count() >= 9);
        }

        [Theory]
        [MemberData(nameof(Fixtures))]
        public void FixtureProducesExactlyTheExpectedDiagnostics(string fixtureName)
        {
            var path = Path.Combine(FixtureDirectory, fixtureName);
            var text = File.ReadAllText(path);

            var sources = new[] { (Path: path, Text: text) };
            var references = new[] { TestReferences.SimCore };

            // A fixture that does not compile would silently stop exercising the analyzer.
            var compileErrors = AnalyzerHarness.CompileErrors(sources, references);
            Assert.True(
                compileErrors.IsEmpty,
                fixtureName + " does not compile: " + string.Join("; ", compileErrors.Select(d => d.ToString())));

            var actual = AnalyzerHarness.Run(sources, additionalFiles: Registries, extraReferences: references)
                .Select(Describe)
                .ToHashSet();

            var expected = ExpectedFrom(text);

            var missing = expected.Except(actual).OrderBy(x => x).ToArray();
            var unexpected = actual.Except(expected).OrderBy(x => x).ToArray();

            Assert.True(
                missing.Length == 0 && unexpected.Length == 0,
                fixtureName
                    + "\n  expected but not reported: " + Format(missing)
                    + "\n  reported but not expected: " + Format(unexpected));
        }

        [Theory]
        [InlineData("Rule001Floats.cs.txt", "SIMCORE001")]
        [InlineData("Rule002BannedMath.cs.txt", "SIMCORE002")]
        [InlineData("Rule003Nondeterminism.cs.txt", "SIMCORE003")]
        [InlineData("Rule004Ordering.cs.txt", "SIMCORE004")]
        [InlineData("Rule005Hashing.cs.txt", "SIMCORE005")]
        [InlineData("Rule006StaticState.cs.txt", "SIMCORE006")]
        [InlineData("Rule007Concurrency.cs.txt", "SIMCORE007")]
        [InlineData("Rule010SquaredMultiply.cs.txt", "SIMCORE010")]
        public void EveryFrozenRuleFires(string fixtureName, string ruleId)
        {
            var path = Path.Combine(FixtureDirectory, fixtureName);
            var diagnostics = AnalyzerHarness.Run(
                new[] { (Path: path, Text: File.ReadAllText(path)) },
                additionalFiles: Registries,
                extraReferences: new[] { TestReferences.SimCore });

            Assert.Contains(diagnostics, d => d.Id == ruleId);
        }

        /// <summary>001-007 are errors and 010 is a warning; the freeze pins both.</summary>
        [Fact]
        public void SeveritiesMatchTheFreeze()
        {
            foreach (var name in new[] { "Rule001Floats", "Rule002BannedMath", "Rule003Nondeterminism",
                "Rule004Ordering", "Rule005Hashing", "Rule006StaticState", "Rule007Concurrency" })
            {
                var path = Path.Combine(FixtureDirectory, name + ".cs.txt");
                var diagnostics = AnalyzerHarness.Run(new[] { (Path: path, Text: File.ReadAllText(path)) },
                    additionalFiles: Registries,
                    extraReferences: new[] { TestReferences.SimCore });

                Assert.NotEmpty(diagnostics);
                Assert.All(diagnostics, d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
            }

            var squared = Path.Combine(FixtureDirectory, "Rule010SquaredMultiply.cs.txt");
            var warnings = AnalyzerHarness.Run(
                new[] { (Path: squared, Text: File.ReadAllText(squared)) },
                extraReferences: new[] { TestReferences.SimCore });

            Assert.NotEmpty(warnings);
            Assert.All(warnings, d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));
        }

        [Fact]
        public void CleanFixtureIsSilent()
        {
            var path = Path.Combine(FixtureDirectory, "Clean.cs.txt");
            var diagnostics = AnalyzerHarness.Run(
                new[] { (Path: path, Text: File.ReadAllText(path)) },
                additionalFiles: Registries,
                extraReferences: new[] { TestReferences.SimCore });

            Assert.Empty(diagnostics);
        }

        private static string Format(IEnumerable<string> items)
        {
            var list = items.ToArray();
            return list.Length == 0 ? "(none)" : string.Join(", ", list);
        }

        private static string Describe(Diagnostic diagnostic) =>
            (diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1) + ":" + diagnostic.Id;

        private static HashSet<string> ExpectedFrom(string text)
        {
            var expected = new HashSet<string>();
            var lines = text.Replace("\r\n", "\n").Split('\n');

            for (var i = 0; i < lines.Length; i++)
            {
                const string marker = "// EXPECT:";
                var index = lines[i].IndexOf(marker, StringComparison.Ordinal);
                if (index < 0)
                {
                    continue;
                }

                foreach (var id in lines[i].Substring(index + marker.Length)
                             .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    expected.Add((i + 1) + ":" + id);
                }
            }

            return expected;
        }
    }
}
