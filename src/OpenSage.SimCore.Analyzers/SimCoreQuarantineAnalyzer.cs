using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace OpenSage.SimCore.Analyzers
{
    /// <summary>
    /// Wall 2 of the float quarantine (api-freeze-v1 F10, design-simcore-scaffolding §2.2):
    /// SIMCORE001-007 as errors plus the SIMCORE010 overflow-guard warning. One analyzer carries
    /// the whole rule set so the scope/exemption decision is computed once per tree.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class SimCoreQuarantineAnalyzer : DiagnosticAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(
                Descriptors.FloatingPointType,
                Descriptors.BannedMathApi,
                Descriptors.NondeterministicSource,
                Descriptors.UnorderedIteration,
                Descriptors.UnstableHash,
                Descriptors.MutableStaticState,
                Descriptors.Concurrency,
                Descriptors.SquaredMultiply);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(OnCompilationStart);
        }

        private static void OnCompilationStart(CompilationStartAnalysisContext context)
        {
            var scope = SimCoreScope.Create(context);
            var banned = BannedSymbols.Create(context.Options);
            var state = new AnalysisState(scope, banned);

            context.RegisterSyntaxNodeAction(state.AnalyzePredefinedType, SyntaxKind.PredefinedType);
            context.RegisterSyntaxNodeAction(state.AnalyzeLiteral, SyntaxKind.NumericLiteralExpression);
            context.RegisterSyntaxNodeAction(
                state.AnalyzeName,
                SyntaxKind.IdentifierName,
                SyntaxKind.GenericName);
            context.RegisterSyntaxNodeAction(state.AnalyzeForEach, SyntaxKind.ForEachStatement);
            context.RegisterSyntaxNodeAction(state.AnalyzeInvocation, SyntaxKind.InvocationExpression);
            context.RegisterSyntaxNodeAction(state.AnalyzeField, SyntaxKind.FieldDeclaration);
            context.RegisterSyntaxNodeAction(state.AnalyzeMultiply, SyntaxKind.MultiplyExpression);
            context.RegisterSyntaxNodeAction(
                state.AnalyzeAsync,
                SyntaxKind.MethodDeclaration,
                SyntaxKind.LocalFunctionStatement,
                SyntaxKind.SimpleLambdaExpression,
                SyntaxKind.ParenthesizedLambdaExpression,
                SyntaxKind.AnonymousMethodExpression);
        }

        private sealed class AnalysisState
        {
            private readonly SimCoreScope _scope;
            private readonly BannedSymbols _banned;

            public AnalysisState(SimCoreScope scope, BannedSymbols banned)
            {
                _scope = scope;
                _banned = banned;
            }

            private bool Skip(SyntaxNodeAnalysisContext context) => !_scope.Applies(context);

            // ---------------------------------------------------------------- SIMCORE001

            public void AnalyzePredefinedType(SyntaxNodeAnalysisContext context)
            {
                if (Skip(context))
                {
                    return;
                }

                var node = (PredefinedTypeSyntax)context.Node;
                switch (node.Keyword.Kind())
                {
                    case SyntaxKind.FloatKeyword:
                    case SyntaxKind.DoubleKeyword:
                        context.ReportDiagnostic(Diagnostic.Create(
                            Descriptors.FloatingPointType, node.GetLocation(), node.Keyword.ValueText));
                        break;
                }
            }

            public void AnalyzeLiteral(SyntaxNodeAnalysisContext context)
            {
                if (Skip(context))
                {
                    return;
                }

                var node = (LiteralExpressionSyntax)context.Node;
                var type = context.SemanticModel.GetTypeInfo(node, context.CancellationToken).Type;

                if (IsFloatingPoint(type))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        Descriptors.FloatingPointType, node.GetLocation(), node.Token.ValueText));
                }
            }

            // --------------------------------------------- SIMCORE001 / 002 / 003 / 005 / 007

            /// <summary>
            /// Every simple/generic name that binds to a symbol goes through one gate: float types
            /// (SIMCORE001) first, then the BannedSymbols table (002/003/005/007).
            /// </summary>
            public void AnalyzeName(SyntaxNodeAnalysisContext context)
            {
                if (Skip(context))
                {
                    return;
                }

                var node = (SimpleNameSyntax)context.Node;

                // Report once per qualified reference: skip the left-hand parts of
                // "System.Numerics.Vector3" / "System.Math.Max", whose rightmost name is visited
                // too and carries the same ban.
                if (IsQualifierOfAnotherName(node) || IsMemberOfPredefinedType(node))
                {
                    return;
                }

                var symbol = context.SemanticModel.GetSymbolInfo(node, context.CancellationToken).Symbol;
                if (symbol is null or INamespaceSymbol)
                {
                    return;
                }

                // Declaring "override int GetHashCode()" is fine; calling one is what 005 bans, and
                // that is handled at the invocation.
                if (symbol is ITypeSymbol type && IsFloatingPoint(type))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        Descriptors.FloatingPointType, node.GetLocation(), type.Name));
                    return;
                }

                if (symbol is not ITypeSymbol && IsFloatingPoint(symbol.ContainingType))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        Descriptors.FloatingPointType,
                        node.GetLocation(),
                        BannedSymbols.FullName(symbol.ContainingType!) + "." + symbol.Name));
                    return;
                }

                if (_banned.TryMatch(symbol, out var entry))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        entry.Descriptor, node.GetLocation(), Display(symbol), entry.Message));
                }
            }

            // ---------------------------------------------------------------- SIMCORE004

            public void AnalyzeForEach(SyntaxNodeAnalysisContext context)
            {
                if (Skip(context))
                {
                    return;
                }

                var node = (ForEachStatementSyntax)context.Node;
                var type = context.SemanticModel.GetTypeInfo(node.Expression, context.CancellationToken).Type;

                if (IsUnorderedCollection(type))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        Descriptors.UnorderedIteration,
                        node.Expression.GetLocation(),
                        "foreach over '" + BannedSymbols.FullName(type!) + "'"));
                }
            }

            // -------------------------------------------------------- SIMCORE004 / SIMCORE005

            public void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
            {
                if (Skip(context))
                {
                    return;
                }

                var node = (InvocationExpressionSyntax)context.Node;
                if (context.SemanticModel.GetSymbolInfo(node, context.CancellationToken).Symbol
                    is not IMethodSymbol method)
                {
                    return;
                }

                if (IsUnstableHashCall(context, node, method))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        Descriptors.UnstableHash,
                        node.GetLocation(),
                        "'" + Display(method) + "'",
                        "string hashing is randomised per process and reference types fall back to the identity hash"));
                    return;
                }

                var containing = method.ContainingType is null ? null : BannedSymbols.FullName(method.ContainingType);
                if (containing is not "System.Linq.Enumerable" and not "System.Linq.Queryable")
                {
                    return;
                }

                switch (method.Name)
                {
                    case "GroupBy":
                    case "Distinct":
                    case "DistinctBy":
                    case "ToDictionary":
                    case "ToHashSet":
                    case "ToLookup":
                        context.ReportDiagnostic(Diagnostic.Create(
                            Descriptors.UnorderedIteration,
                            node.GetLocation(),
                            "'Enumerable." + method.Name + "'"));
                        break;

                    // "OrderBy with default comparer over unstable source": an ordering pass whose
                    // input is itself unordered only becomes total if the key comparer is explicit.
                    case "OrderBy":
                    case "OrderByDescending":
                        if (method.Parameters.Length <= 2 && ReceiverIsUnordered(context, node))
                        {
                            context.ReportDiagnostic(Diagnostic.Create(
                                Descriptors.UnorderedIteration,
                                node.GetLocation(),
                                "'Enumerable." + method.Name + "' with the default comparer over an unordered source"));
                        }

                        break;
                }
            }

            private bool ReceiverIsUnordered(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax node)
            {
                if (node.Expression is not MemberAccessExpressionSyntax access)
                {
                    return false;
                }

                var type = context.SemanticModel.GetTypeInfo(access.Expression, context.CancellationToken).Type;
                return IsUnorderedCollection(type);
            }

            /// <summary>
            /// GetHashCode is deterministic for the primitive value types (long.GetHashCode is a
            /// fixed xor-fold), but string hashing is randomised per process and reference types
            /// fall back to the identity hash. Only the unstable receivers are reported, which is
            /// what keeps SimCore's own struct GetHashCode overrides legal.
            /// </summary>
            private static bool IsUnstableHashCall(
                SyntaxNodeAnalysisContext context,
                InvocationExpressionSyntax node,
                IMethodSymbol method)
            {
                if (method.Name != "GetHashCode" || method.Parameters.Length != 0)
                {
                    return false;
                }

                var receiver = node.Expression is MemberAccessExpressionSyntax access
                    ? context.SemanticModel.GetTypeInfo(access.Expression, context.CancellationToken).Type
                    : context.SemanticModel.GetEnclosingSymbol(node.SpanStart, context.CancellationToken)
                        ?.ContainingType;

                if (receiver is null)
                {
                    return false;
                }

                return receiver.SpecialType == SpecialType.System_String
                    || receiver.SpecialType == SpecialType.System_Object
                    || receiver.IsReferenceType;
            }

            // ---------------------------------------------------------------- SIMCORE006

            public void AnalyzeField(SyntaxNodeAnalysisContext context)
            {
                if (Skip(context))
                {
                    return;
                }

                var node = (FieldDeclarationSyntax)context.Node;
                var modifiers = node.Modifiers;

                if (!modifiers.Any(SyntaxKind.StaticKeyword)
                    || modifiers.Any(SyntaxKind.ConstKeyword)
                    || modifiers.Any(SyntaxKind.ReadOnlyKeyword))
                {
                    return;
                }

                foreach (var declarator in node.Declaration.Variables)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        Descriptors.MutableStaticState,
                        declarator.Identifier.GetLocation(),
                        declarator.Identifier.ValueText));
                }
            }

            // ---------------------------------------------------------------- SIMCORE007

            public void AnalyzeAsync(SyntaxNodeAnalysisContext context)
            {
                if (Skip(context))
                {
                    return;
                }

                var modifiers = context.Node switch
                {
                    MethodDeclarationSyntax m => m.Modifiers,
                    LocalFunctionStatementSyntax l => l.Modifiers,
                    AnonymousFunctionExpressionSyntax a => a.Modifiers,
                    _ => default
                };

                if (modifiers.Any(SyntaxKind.AsyncKeyword))
                {
                    var token = modifiers.First(t => t.IsKind(SyntaxKind.AsyncKeyword));
                    context.ReportDiagnostic(Diagnostic.Create(
                        Descriptors.Concurrency,
                        token.GetLocation(),
                        "'async'",
                        "continuation scheduling is not reproducible across runs"));
                }
            }

            // ---------------------------------------------------------------- SIMCORE010

            public void AnalyzeMultiply(SyntaxNodeAnalysisContext context)
            {
                if (Skip(context))
                {
                    return;
                }

                var node = (BinaryExpressionSyntax)context.Node;

                if (IsInsideFixMath(node))
                {
                    return;
                }

                var type = context.SemanticModel.GetTypeInfo(node, context.CancellationToken).Type;
                if (type?.Name != "Fix64")
                {
                    return;
                }

                var left = SquaredName(node.Left);
                var right = SquaredName(node.Right);

                if (left is not null && right is not null)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        Descriptors.SquaredMultiply, node.GetLocation(), left, right));
                }
            }

            private static bool IsInsideFixMath(SyntaxNode node)
            {
                for (var current = node.Parent; current is not null; current = current.Parent)
                {
                    if (current is TypeDeclarationSyntax declaration
                        && declaration.Identifier.ValueText == "FixMath")
                    {
                        return true;
                    }
                }

                return false;
            }

            /// <summary>
            /// The §1.2-R2 heuristic named in the freeze: an operand whose source name ends in
            /// "Sq"/"Squared" is already a squared magnitude, so multiplying two of them is a
            /// fourth power and saturates Fix64 at ~2.1e9.
            /// </summary>
            private static string? SquaredName(ExpressionSyntax expression)
            {
                var name = expression switch
                {
                    IdentifierNameSyntax id => id.Identifier.ValueText,
                    MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
                    InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax a } =>
                        a.Name.Identifier.ValueText,
                    InvocationExpressionSyntax { Expression: IdentifierNameSyntax i } => i.Identifier.ValueText,
                    ParenthesizedExpressionSyntax parenthesized => SquaredName(parenthesized.Expression),
                    _ => null
                };

                if (name is null)
                {
                    return null;
                }

                var isSquared = name.EndsWith("Sq", System.StringComparison.Ordinal)
                    || name.EndsWith("Squared", System.StringComparison.Ordinal)
                    || name.EndsWith("Sqr", System.StringComparison.Ordinal);

                return isSquared ? name : null;
            }

            // ---------------------------------------------------------------- helpers

            private static bool IsQualifierOfAnotherName(SimpleNameSyntax node) => node.Parent switch
            {
                QualifiedNameSyntax qualified => qualified.Left == node,
                MemberAccessExpressionSyntax access => access.Expression == node,
                AliasQualifiedNameSyntax alias => alias.Alias == node,
                _ => false
            };

            // "float.MaxValue" already draws a SIMCORE001 from the 'float' keyword itself; don't
            // report the member name a second time.
            private static bool IsMemberOfPredefinedType(SimpleNameSyntax node) =>
                node.Parent is MemberAccessExpressionSyntax { Expression: PredefinedTypeSyntax } access
                && access.Name == node;

            private static bool IsFloatingPoint(ITypeSymbol? type) => type?.SpecialType switch
            {
                SpecialType.System_Single => true,
                SpecialType.System_Double => true,
                _ => type is not null && BannedSymbols.FullName(type) == "System.Half"
            };

            private static bool IsUnorderedCollection(ITypeSymbol? type)
            {
                if (type is null)
                {
                    return false;
                }

                return BannedSymbols.FullName(type.OriginalDefinition) switch
                {
                    "System.Collections.Generic.Dictionary" => true,
                    "System.Collections.Generic.Dictionary.KeyCollection" => true,
                    "System.Collections.Generic.Dictionary.ValueCollection" => true,
                    "System.Collections.Generic.HashSet" => true,
                    "System.Collections.Generic.IDictionary" => true,
                    "System.Collections.Generic.ISet" => true,
                    "System.Collections.Concurrent.ConcurrentDictionary" => true,
                    "System.Collections.Immutable.ImmutableDictionary" => true,
                    "System.Collections.Immutable.ImmutableHashSet" => true,
                    _ => false
                };
            }

            private static string Display(ISymbol symbol) => symbol switch
            {
                ITypeSymbol type => BannedSymbols.FullName(type),
                { ContainingType: { } containing } => BannedSymbols.FullName(containing) + "." + symbol.Name,
                _ => symbol.Name
            };
        }
    }
}
