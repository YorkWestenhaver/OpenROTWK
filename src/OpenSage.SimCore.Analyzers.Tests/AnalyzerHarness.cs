using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using OpenSage.SimCore.Analyzers;

namespace OpenSage.SimCore.Analyzers.Tests
{
    /// <summary>
    /// Drives <see cref="SimCoreQuarantineAnalyzer"/> over an in-memory compilation, with the same
    /// inputs MSBuild would supply: AdditionalFiles for the three registries and a global
    /// analyzer-config option for the attachment mode.
    /// </summary>
    internal static class AnalyzerHarness
    {
        /// <summary>Every assembly of the running runtime, so fixtures can name any BCL type.</summary>
        private static readonly ImmutableArray<MetadataReference> RuntimeReferences = CreateRuntimeReferences();

        private static ImmutableArray<MetadataReference> CreateRuntimeReferences()
        {
            var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty;

            return trusted
                .Split(Path.PathSeparator)
                .Where(path => path.Length != 0 && File.Exists(path))
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
                .ToImmutableArray();
        }

        public static ImmutableArray<Diagnostic> Run(
            IEnumerable<(string Path, string Text)> sources,
            string assemblyName = "SimCoreFixture",
            string? mode = "full",
            IEnumerable<(string Path, string Text)>? additionalFiles = null,
            IEnumerable<MetadataReference>? extraReferences = null)
        {
            var trees = sources
                .Select(source => CSharpSyntaxTree.ParseText(
                    SourceText.From(source.Text),
                    new CSharpParseOptions(LanguageVersion.Latest),
                    path: source.Path))
                .ToImmutableArray();

            var references = RuntimeReferences;
            if (extraReferences is not null)
            {
                references = references.AddRange(extraReferences);
            }

            var compilation = CSharpCompilation.Create(
                assemblyName,
                trees,
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

            var options = new AnalyzerOptions(
                (additionalFiles ?? Array.Empty<(string, string)>())
                    .Select(file => (AdditionalText)new InMemoryAdditionalText(file.Path, file.Text))
                    .ToImmutableArray(),
                new FixedOptionsProvider(mode));

            var withAnalyzers = compilation.WithAnalyzers(
                ImmutableArray.Create<DiagnosticAnalyzer>(new SimCoreQuarantineAnalyzer()),
                options);

            return withAnalyzers.GetAnalyzerDiagnosticsAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <summary>Compiler errors in the fixture itself, so a broken fixture fails loudly.</summary>
        public static ImmutableArray<Diagnostic> CompileErrors(
            IEnumerable<(string Path, string Text)> sources,
            IEnumerable<MetadataReference>? extraReferences = null)
        {
            var trees = sources
                .Select(source => CSharpSyntaxTree.ParseText(
                    SourceText.From(source.Text),
                    new CSharpParseOptions(LanguageVersion.Latest),
                    path: source.Path))
                .ToImmutableArray();

            var references = RuntimeReferences;
            if (extraReferences is not null)
            {
                references = references.AddRange(extraReferences);
            }

            var compilation = CSharpCompilation.Create(
                "SimCoreFixtureCompile",
                trees,
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

            return compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToImmutableArray();
        }

        private sealed class InMemoryAdditionalText : AdditionalText
        {
            private readonly SourceText _text;

            public InMemoryAdditionalText(string path, string text)
            {
                Path = path;
                _text = SourceText.From(text);
            }

            public override string Path { get; }

            public override SourceText GetText(CancellationToken cancellationToken = default) => _text;
        }

        private sealed class FixedOptionsProvider : AnalyzerConfigOptionsProvider
        {
            public FixedOptionsProvider(string? mode) => GlobalOptions = new FixedOptions(mode);

            public override AnalyzerConfigOptions GlobalOptions { get; }

            public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => GlobalOptions;

            public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => GlobalOptions;
        }

        private sealed class FixedOptions : AnalyzerConfigOptions
        {
            private readonly ImmutableDictionary<string, string> _values;

            public FixedOptions(string? mode)
            {
                var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
                if (mode is not null)
                {
                    builder["build_property.SimCoreAnalyzerMode"] = mode;
                }

                _values = builder.ToImmutable();
            }

            public override bool TryGetValue(string key, out string value) => _values.TryGetValue(key, out value!);
        }
    }
}
