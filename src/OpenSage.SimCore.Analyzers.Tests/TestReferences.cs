using System;
using System.IO;
using Microsoft.CodeAnalysis;

namespace OpenSage.SimCore.Analyzers.Tests
{
    internal static class TestReferences
    {
        /// <summary>The built SimCore assembly, so fixtures can name Fix64/FixMath.</summary>
        public static MetadataReference SimCore { get; } =
            MetadataReference.CreateFromFile(typeof(global::OpenSage.SimCore.Numerics.Fix64).Assembly.Location);

        /// <summary>
        /// Walks up from the test binaries to the repository's src/ directory, so tests can read
        /// the real SimCore sources and registry files rather than copies of them.
        /// </summary>
        public static string SourceRoot { get; } = FindSourceRoot();

        public static string SimCoreProjectDirectory => Path.Combine(SourceRoot, "OpenSage.SimCore");

        private static string FindSourceRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, "OpenSage.SimCore", "OpenSage.SimCore.csproj");
                if (File.Exists(candidate))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException("Could not locate the repository src/ directory from " + AppContext.BaseDirectory);
        }
    }
}
