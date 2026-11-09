using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Rgl.Analyzers;

namespace Rgl.Tests.Dev;

/// <summary>Tests de l'analyseur <c>RGLDEV001</c> (<see cref="TodoAnalyzer"/>).</summary>
public class TodoAnalyzerTests
{
    private const string Source = """
        using System;

        namespace Rgl.Dev
        {
            [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
            internal sealed class TodoAttribute(string description) : Attribute
            {
                public string Description { get; } = description;
            }
        }

        namespace Exemple
        {
            [Rgl.Dev.Todo("finir la classe")]
            internal class Lexer
            {
                [Rgl.Dev.Todo("gérer le BOM")]
                [Rgl.Dev.Todo("gérer les fins de ligne")]
                public void Lire() { }

                public void Fini() { }
            }
        }
        """;

    [Fact]
    public async Task En_debug_chaque_Todo_est_signale_a_sa_position()
    {
        var diagnostics = await AnalyzeAsync(debug: true);

        Assert.All(diagnostics, d => Assert.Equal(TodoAnalyzer.DiagnosticId, d.Id));
        Assert.Equal(
            [
                (14, "À faire (Lexer) : finir la classe"),
                (17, "À faire (Lexer.Lire()) : gérer le BOM"),
                (18, "À faire (Lexer.Lire()) : gérer les fins de ligne"),
            ],
            diagnostics.Select(d => (d.Location.GetLineSpan().StartLinePosition.Line + 1, d.GetMessage())));
    }

    [Fact]
    public async Task En_release_rien_n_est_signale()
    {
        Assert.Empty(await AnalyzeAsync(debug: false));
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(bool debug)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols: debug ? ["DEBUG"] : []);
        var tree = CSharpSyntaxTree.ParseText(Source, parseOptions, path: "Exemple.cs");
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "Exemple",
            [tree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));

        var diagnostics = await compilation
            .WithAnalyzers([new TodoAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();
        return [.. diagnostics.OrderBy(d => d.Location.SourceSpan.Start)];
    }
}
