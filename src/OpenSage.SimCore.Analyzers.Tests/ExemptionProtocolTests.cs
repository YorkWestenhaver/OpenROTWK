using System.Linq;
using Xunit;

namespace OpenSage.SimCore.Analyzers.Tests
{
    /// <summary>
    /// The exemption pair protocol of design-simcore-scaffolding §2.2: a file escapes the
    /// quarantine only when it carries BOTH the <c>// SIMCORE-EXEMPT:</c> header comment and a
    /// matching line in SimCoreExemptions.txt. Half a pair is not an exemption, which is what
    /// makes lifting the wall a two-place, reviewable diff.
    /// </summary>
    public class ExemptionProtocolTests
    {
        private const string Violating = @"// SIMCORE-EXEMPT: guess accelerator, result is guess-independent
namespace Fixture
{
    public class Divider
    {
        public long Guess(long a, long b) => (long)((double)a / (double)b);
    }
}
";

        private const string WithoutHeader = @"namespace Fixture
{
    public class Divider
    {
        public long Guess(long a, long b) => (long)((double)a / (double)b);
    }
}
";

        private const string Registry = "# comment\nNumerics/Fix64.Division.cs\n";

        private const string Path = "/repo/src/OpenSage.SimCore/Numerics/Fix64.Division.cs";

        [Fact]
        public void BothHalvesPresentExemptsTheFile()
        {
            var diagnostics = AnalyzerHarness.Run(
                new[] { (Path, Violating) },
                additionalFiles: new[] { ("/repo/src/OpenSage.SimCore/SimCoreExemptions.txt", Registry) });

            Assert.Empty(diagnostics);
        }

        [Fact]
        public void HeaderWithoutRegistryEntryIsNotAnExemption()
        {
            var diagnostics = AnalyzerHarness.Run(
                new[] { (Path, Violating) },
                additionalFiles: new[] { ("/repo/src/OpenSage.SimCore/SimCoreExemptions.txt", "# nothing listed\n") });

            Assert.Contains(diagnostics, d => d.Id == "SIMCORE001");
        }

        [Fact]
        public void RegistryEntryWithoutHeaderIsNotAnExemption()
        {
            var diagnostics = AnalyzerHarness.Run(
                new[] { (Path, WithoutHeader) },
                additionalFiles: new[] { ("/repo/src/OpenSage.SimCore/SimCoreExemptions.txt", Registry) });

            Assert.Contains(diagnostics, d => d.Id == "SIMCORE001");
        }

        [Fact]
        public void NeitherHalfIsNotAnExemption()
        {
            var diagnostics = AnalyzerHarness.Run(new[] { (Path, WithoutHeader) });

            Assert.Contains(diagnostics, d => d.Id == "SIMCORE001");
        }

        /// <summary>
        /// The registry entry is a whole trailing path run, so a same-named file in another
        /// directory does not inherit somebody else's exemption.
        /// </summary>
        [Fact]
        public void RegistryEntryDoesNotLeakToASimilarlyNamedFile()
        {
            var diagnostics = AnalyzerHarness.Run(
                new[] { ("/repo/src/OpenSage.SimCore/Numerics/NotFix64.Division.cs", Violating) },
                additionalFiles: new[] { ("/repo/src/OpenSage.SimCore/SimCoreExemptions.txt", Registry) });

            Assert.Contains(diagnostics, d => d.Id == "SIMCORE001");
        }

        /// <summary>An exemption suspends the whole rule set for that file, not just SIMCORE001.</summary>
        [Fact]
        public void ExemptionCoversEveryRule()
        {
            const string kitchenSink = @"// SIMCORE-EXEMPT: fixture
using System;
namespace Fixture
{
    public class Everything
    {
        public static int Counter;
        public double Value;
        public long Larger(long a, long b) => Math.Max(a, b);
        public int Id() => Guid.NewGuid().GetHashCode();
    }
}
";

            var diagnostics = AnalyzerHarness.Run(
                new[] { ("/repo/src/OpenSage.SimCore/Numerics/Everything.cs", kitchenSink) },
                additionalFiles: new[] { ("/repo/src/OpenSage.SimCore/SimCoreExemptions.txt", "Numerics/Everything.cs\n") });

            Assert.Empty(diagnostics);

            // ... and the same file without the pair is loud.
            var unexempted = AnalyzerHarness.Run(
                new[] { ("/repo/src/OpenSage.SimCore/Numerics/Everything.cs", kitchenSink) });

            Assert.True(unexempted.Select(d => d.Id).Distinct().Count() >= 4);
        }
    }
}
