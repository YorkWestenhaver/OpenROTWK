using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace OpenSage.SimCore.Analyzers;

/// <summary>
/// Decides, per syntax tree, whether the SIMCORE rule set applies. This implements both
/// attachment modes of design-simcore-scaffolding §2.2/§2.3:
///
/// <list type="bullet">
/// <item><b>Full</b> - every file in the compilation is simulation code. Selected by
/// <c>build_property.SimCoreAnalyzerMode=full</c>, or implicitly when the assembly is
/// OpenSage.SimCore itself (so the wall stands even if the property is lost).</item>
/// <item><b>Scoped</b> - the migration mode used inside OpenSage.Game. Only files living
/// under a directory listed in <c>SimCoreScopedDirs.txt</c>, or declaring a type marked
/// <c>[SimState]</c>, are analyzed. The scoped-dirs list is the migration progress meter.</item>
/// </list>
///
/// Exemptions require the pair: a <c>// SIMCORE-EXEMPT:</c> file-header comment AND a matching
/// entry in <c>SimCoreExemptions.txt</c>. Either half alone does nothing, so an exemption is
/// always a two-place reviewable diff.
/// </summary>
internal sealed class SimCoreScope
{
    public const string ExemptMarker = "// SIMCORE-EXEMPT:";
    public const string SimStateAttributeName = "SimState";
    public const string ExemptionsFileName = "SimCoreExemptions.txt";
    public const string ScopedDirsFileName = "SimCoreScopedDirs.txt";
    public const string BannedSymbolsFileName = "BannedSymbols.txt";
    public const string ModeProperty = "build_property.SimCoreAnalyzerMode";

    private readonly bool _fullMode;
    private readonly ImmutableArray<string> _exemptions;
    private readonly ImmutableArray<string> _scopedDirs;

    // Scope resolution reads syntax (the [SimState] sweep), so it is memoised per tree; without
    // this the scoped attachment inside OpenSage.Game would re-walk a tree for every node.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<SyntaxTree, bool> _cache =
        new System.Collections.Concurrent.ConcurrentDictionary<SyntaxTree, bool>();

    private SimCoreScope(bool fullMode, ImmutableArray<string> exemptions, ImmutableArray<string> scopedDirs)
    {
        _fullMode = fullMode;
        _exemptions = exemptions;
        _scopedDirs = scopedDirs;
    }

    public bool IsFullMode => _fullMode;

    public ImmutableArray<string> Exemptions => _exemptions;

    public static SimCoreScope Create(CompilationStartAnalysisContext context)
    {
        var options = context.Options;

        var mode = GetModeProperty(options);
        var fullMode = string.Equals(mode, "full", StringComparison.OrdinalIgnoreCase)
            || (mode is null && IsSimCoreAssembly(context.Compilation));

        return new SimCoreScope(
            fullMode,
            ReadListFile(options, ExemptionsFileName),
            ReadListFile(options, ScopedDirsFileName));
    }

    private static bool IsSimCoreAssembly(Compilation compilation)
    {
        return compilation.AssemblyName == "OpenSage.SimCore";
    }

    private static string? GetModeProperty(AnalyzerOptions options)
    {
        return options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue(ModeProperty, out var value)
            && !string.IsNullOrWhiteSpace(value)
                ? value.Trim()
                : null;
    }

    /// <summary>
    /// Reads one of the plain-text registries supplied as an AdditionalFile: one entry per
    /// line, '#' starts a comment, blank lines ignored, separators normalised to '/'.
    /// </summary>
    internal static ImmutableArray<string> ReadListFile(AnalyzerOptions options, string fileName)
    {
        var builder = ImmutableArray.CreateBuilder<string>();

        foreach (var file in options.AdditionalFiles)
        {
            if (!string.Equals(FileNameOf(file.Path), fileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = file.GetText();
            if (text is null)
            {
                continue;
            }

            foreach (var line in text.Lines)
            {
                var entry = StripComment(line.ToString());
                if (entry.Length != 0)
                {
                    builder.Add(Normalize(entry));
                }
            }
        }

        return builder.ToImmutable();
    }

    internal static string StripComment(string line)
    {
        var hash = line.IndexOf('#');
        if (hash >= 0)
        {
            line = line.Substring(0, hash);
        }

        return line.Trim();
    }

    internal static string Normalize(string path) => path.Replace('\\', '/').Trim().TrimEnd('/');

    private static string FileNameOf(string path)
    {
        var normalized = Normalize(path);
        var slash = normalized.LastIndexOf('/');
        return slash < 0 ? normalized : normalized.Substring(slash + 1);
    }

    /// <summary>
    /// True when <paramref name="tree"/> holds simulation code that the rule set must police:
    /// in scope for this attachment mode, and not exempted by a complete exemption pair.
    /// </summary>
    public bool Applies(SyntaxTree tree, CancellationTokenHolder cancellation)
    {
        if (_cache.TryGetValue(tree, out var cached))
        {
            return cached;
        }

        var result = InScope(tree, cancellation) && !IsExempt(tree);
        _cache[tree] = result;
        return result;
    }

    private bool InScope(SyntaxTree tree, CancellationTokenHolder cancellation)
    {
        if (_fullMode)
        {
            return true;
        }

        var path = Normalize(tree.FilePath);

        foreach (var dir in _scopedDirs)
        {
            if (dir.Length != 0 && PathContainsDirectory(path, dir))
            {
                return true;
            }
        }

        return DeclaresSimStateType(tree, cancellation);
    }

    /// <summary>
    /// An exemption needs BOTH halves - the header comment and the registry entry. A file
    /// carrying only the comment, or only the registry line, stays fully analyzed.
    /// </summary>
    public bool IsExempt(SyntaxTree tree)
    {
        if (!HasExemptHeader(tree))
        {
            return false;
        }

        var path = Normalize(tree.FilePath);

        foreach (var entry in _exemptions)
        {
            if (PathMatchesEntry(path, entry))
            {
                return true;
            }
        }

        return false;
    }

    public static bool HasExemptHeader(SyntaxTree tree)
    {
        var root = tree.GetRoot();
        var first = root.GetFirstToken(includeZeroWidth: true);

        foreach (var trivia in first.LeadingTrivia)
        {
            if (!trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
                && !trivia.IsKind(SyntaxKind.MultiLineCommentTrivia))
            {
                continue;
            }

            if (trivia.ToString().IndexOf(ExemptMarker, StringComparison.Ordinal) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Registry entries are project-relative ("Numerics/Fix64.Sqrt.cs"); tree paths are
    /// absolute. Match on a whole trailing path segment run so "Fix64.Sqrt.cs" can never
    /// accidentally exempt "Other/NotFix64.Sqrt.cs".
    /// </summary>
    internal static bool PathMatchesEntry(string path, string entry)
    {
        if (path.Length == 0 || entry.Length == 0)
        {
            return false;
        }

        if (string.Equals(path, entry, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return path.Length > entry.Length
            && path[path.Length - entry.Length - 1] == '/'
            && path.EndsWith(entry, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool PathContainsDirectory(string path, string dir)
    {
        var needle = "/" + dir.Trim('/') + "/";
        return ("/" + path.TrimStart('/')).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool DeclaresSimStateType(SyntaxTree tree, CancellationTokenHolder cancellation)
    {
        var root = tree.GetRoot(cancellation.Token);

        foreach (var type in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            foreach (var list in type.AttributeLists)
            {
                foreach (var attribute in list.Attributes)
                {
                    if (IsSimStateAttribute(attribute.Name))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool IsSimStateAttribute(NameSyntax name)
    {
        var text = name switch
        {
            QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
            SimpleNameSyntax simple => simple.Identifier.ValueText,
            _ => name.ToString()
        };

        return text == SimStateAttributeName || text == SimStateAttributeName + "Attribute";
    }
}

/// <summary>Tiny wrapper so scope helpers can be called from any analysis context shape.</summary>
internal readonly struct CancellationTokenHolder
{
    public CancellationTokenHolder(System.Threading.CancellationToken token) => Token = token;

    public System.Threading.CancellationToken Token { get; }

    public static implicit operator CancellationTokenHolder(System.Threading.CancellationToken token) =>
        new CancellationTokenHolder(token);
}

internal static class ScopeExtensions
{
    public static bool Applies(this SimCoreScope scope, SyntaxNodeAnalysisContext context) =>
        scope.Applies(context.Node.SyntaxTree, context.CancellationToken);

    public static bool Applies(this SimCoreScope scope, OperationAnalysisContext context) =>
        context.Operation.Syntax is { } syntax && scope.Applies(syntax.SyntaxTree, context.CancellationToken);

    public static bool Applies(this SimCoreScope scope, SymbolAnalysisContext context, Location location) =>
        location.SourceTree is { } tree && scope.Applies(tree, context.CancellationToken);

    public static IEnumerable<string> Lines(this string text) =>
        text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
}
