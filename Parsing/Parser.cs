using Rgl.AST;
using Rgl.Diagnostics;
using Rgl.Syntax;
using Rgl.Text;
using Rotomeca.Core.Collections;

using TokensData = Rgl.Syntax.Lexer.TokenizeResult;

namespace Rgl.Parsing;

internal sealed partial class Parser(TokensData result, DiagnosticBag bag) 
{
    private readonly SourceText _source = result.Source;
    private readonly IReadOnlyList<Token> _tokens = result.Tokens;
    private readonly DiagnosticBag _diagnosticBag = bag;

    /// <summary>Vrai pendant la lecture du train : ses rails <c>!&gt;</c> y sont permis, et nulle part ailleurs.</summary>
    private bool _inTrain;

    /// <summary>
    /// Vrai pendant la lecture du type de retour d'un gear : <c>gear f(): (int, string) =&gt; (1, "a");</c>
    /// n'y est pas lu comme un type de lambda, sauf si un corps suit ce type (voir <see cref="_ArrowStartsReturnType"/>).
    /// </summary>
    private bool _inGearReturnType;

    /// <summary>
    /// Construit l'arbre syntaxique du fichier. Les erreurs de syntaxe sont signalées dans le
    /// <see cref="DiagnosticBag"/> sans arrêter l'analyse : l'arbre est toujours complet, avec
    /// des nœuds manquants là où quelque chose était attendu.
    /// </summary>
    /// <returns>La racine de l'arbre.</returns>
    public CompilationUnit Parse() => _ParseProgram();
}

