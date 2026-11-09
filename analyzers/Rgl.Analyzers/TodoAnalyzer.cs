using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Rgl.Analyzers;

/// <summary>
/// Signale chaque usage de <c>[Todo("…")]</c> (<c>Rgl.Dev.TodoAttribute</c>) quand le symbole
/// <c>DEBUG</c> est défini, c'est-à-dire en configuration Debug ; ne fait rien en Release.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TodoAnalyzer : DiagnosticAnalyzer
{
    /// <summary>Identifiant du diagnostic émis pour chaque <c>[Todo]</c>.</summary>
    public const string DiagnosticId = "RGLDEV001";

    private const string TodoAttributeName = "Rgl.Dev.TodoAttribute";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Élément marqué [Todo]",
        messageFormat: "À faire ({0}) : {1}",
        category: "Rgl.Dev",
        // Un avertissement, car les diagnostics Info n'apparaissent pas dans la sortie de dotnet build.
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        var debug = context.Compilation.SyntaxTrees
            .Any(tree => tree.Options.PreprocessorSymbolNames.Contains("DEBUG"));
        var todoAttribute = context.Compilation.GetTypeByMetadataName(TodoAttributeName);
        if (!debug || todoAttribute is null)
            return;

        context.RegisterSymbolAction(
            symbolContext => ReportTodos(symbolContext, todoAttribute),
            SymbolKind.NamedType,
            SymbolKind.Method);
    }

    private static void ReportTodos(SymbolAnalysisContext context, INamedTypeSymbol todoAttribute)
    {
        foreach (var attribute in context.Symbol.GetAttributes())
        {
            if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, todoAttribute))
                continue;

            var description = attribute.ConstructorArguments.FirstOrDefault().Value as string ?? "";
            var location = attribute.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation()
                ?? context.Symbol.Locations.FirstOrDefault()
                ?? Location.None;
            context.ReportDiagnostic(Diagnostic.Create(Rule, location, context.Symbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat), description));
        }
    }
}
