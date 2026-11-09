using Rgl.AST.Extensions;
using Rgl.AST.Gears;
using Rgl.AST.Interfaces;
using Rgl.AST.Variables;

namespace Rgl.Lsp;

/// <summary>Un gear d'extension qui s'appelle sur une valeur : <c>v.somme()</c>.</summary>
/// <param name="Module">Le fichier <c>forge: extend</c> qui l'écrit.</param>
/// <param name="Extend">L'extension qui le contient.</param>
/// <param name="Gear">Le gear, dont le premier paramètre est marqué <c>this</c>.</param>
internal sealed record ExtensionMethod(ModuleInfo Module, ExtendDeclaration Extend, GearDeclaration Gear)
{
    /// <summary>Son nom.</summary>
    public string Name => Gear.Name.Name;

    /// <summary>Ses paramètres sans <c>this</c>, tels qu'on les écrit à l'appel : <c>(T what)</c>.</summary>
    public string Parameters => "(" + string.Join(", ", Gear.Parameters.Skip(1).Select(p => Signatures.Collapse(p.Span.Text))) + ")";

    /// <summary>Son type de retour, tel qu'écrit.</summary>
    public string Return => Signatures.Collapse(Gear.ReturnType.Span.Text);
}

/// <summary>
/// Les extensions que voit un fichier : celles de tous les fichiers <c>forge: extend</c> du projet et de la std, qui s'appliquent à tout le
/// programme sans import. Pour une valeur d'un type donné, on garde les gears dont le type étendu a la forme de celui de la valeur
/// (<c>[T]</c> convient à <c>[int]</c>, <c>T</c> à tout) et dont les contraintes (<c>T: number</c>) sont respectées.
/// </summary>
internal static class ExtensionMethods
{
    private static readonly HashSet<string> _numbers = ["byte", "ubyte", "short", "ushort", "int", "uint", "long", "ulong", "float", "double"];

    /// <summary>
    /// Les gears d'extension qui s'appellent sur une valeur de type <paramref name="typeText"/> : ceux des fichiers <c>forge: extend</c> (tout le
    /// programme), ceux écrits dans <paramref name="document"/> lui-même, et ceux d'un fichier du projet pour un type que ce fichier déclare
    /// (une extension suit son type d'un fichier à l'autre).
    /// </summary>
    public static IEnumerable<ExtensionMethod> For(ProjectContext project, string typeText, SemanticDocument? document = null)
    {
        var actual = Signatures.Collapse(SemanticDocument.StripNullable(typeText));
        var seen = new HashSet<(string, string)>();
        foreach (var (module, extend) in _Sources(project, document))
        {
            if (!Matches(extend, actual)) continue;
            foreach (var gear in extend.Members.OfType<GearDeclaration>().Where(g => g.Parameters.Count > 0 && g.Parameters[0].IsThis))
            {
                if (seen.Add((module.Path, gear.Name.Name + "@" + gear.Span.Start.Value))) yield return new ExtensionMethod(module, extend, gear);
            }
        }
    }

    private static IEnumerable<(ModuleInfo Module, ExtendDeclaration Extend)> _Sources(ProjectContext project, SemanticDocument? document)
    {
        foreach (var module in project.Extensions)
            foreach (var extend in module.Extends) yield return (module, extend);

        if (document is not null)
            foreach (var extend in document.Self.Extends) yield return (document.Self, extend);

        // Une extension d'un type déclaré dans son propre fichier suit ce type : tout fichier du projet qui le voit la voit.
        foreach (var module in project.Modules.Where(m => m.Kind != "extend").Concat(project.Std ?? []))
        {
            foreach (var extend in module.Extends)
            {
                var target = Signatures.Collapse(extend.Target.Span.Text);
                if (module.Symbols.TryGetValue(target, out var declared) && declared.IsType) yield return (module, extend);
            }
        }
    }

    /// <summary>
    /// Ce que <c>Type.</c> offre en plus de ses membres : les constantes (<c>line.ZERO</c>) et les gears sans <c>this</c> (<c>Random.seed(42)</c>) des
    /// extensions du type nommé <paramref name="typeName"/>, d'où qu'elles viennent.
    /// </summary>
    public static IEnumerable<RglSymbol> StaticMembers(ProjectContext project, string typeName, SemanticDocument? document = null)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (module, extend) in _Sources(project, document))
        {
            if (extend.TypeParameters is not null || Signatures.Collapse(extend.Target.Span.Text) != typeName) continue;
            foreach (var member in extend.Members)
            {
                RglSymbol? symbol = member switch
                {
                    VarDeclaration { IsConst: true } constant => new RglSymbol(constant.Name.Name, RglSymbolKind.Constant, constant.Name.Span, constant, module,
                        constant.Type is { } type ? Signatures.Collapse(type.Span.Text) : typeName, Container: typeName, Documentation: module.DocumentationFor(constant.Span)),
                    GearDeclaration gear when !(gear.Parameters.Count > 0 && gear.Parameters[0].IsThis) => new RglSymbol(gear.Name.Name, gear.Keyword switch
                    {
                        Syntax.TokenKind.GuardKeyword => RglSymbolKind.Guard,
                        Syntax.TokenKind.EmitKeyword => RglSymbolKind.Emit,
                        _ => RglSymbolKind.Gear,
                    }, gear.Name.Span, gear, module, Container: typeName, Documentation: module.DocumentationFor(gear.Span)),
                    _ => null,
                };
                if (symbol is not null && !string.IsNullOrWhiteSpace(symbol.Name) && seen.Add(symbol.Name)) yield return symbol;
            }
        }
    }

    /// <summary>Vrai si une valeur de type <paramref name="actual"/> est du type que <paramref name="extend"/> étend.</summary>
    public static bool Matches(ExtendDeclaration extend, string actual)
    {
        var pattern = Signatures.Collapse(extend.Target.Span.Text);
        var generics = extend.TypeParameters?.Select(p => p.Name.Name).ToArray() ?? [];
        if (generics.Length == 0) return pattern == actual;

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        SemanticDocument.Unify(pattern, actual, generics, map);
        if (generics.Any(g => !map.ContainsKey(g)) || SemanticDocument.Substitute(pattern, map) != actual) return false;

        // T: number
        foreach (var parameter in extend.TypeParameters!)
        {
            if (parameter.Constraint is { } constraint && Signatures.Collapse(constraint.Span.Text) == "number" && !_numbers.Contains(map[parameter.Name.Name])) return false;
        }
        return true;
    }

    /// <summary>
    /// Le texte inséré pour un gear : son nom et ses parenthèses, le curseur entre elles s'il attend des arguments
    /// (<c>sum()</c>, <c>indexOf($0)</c>) ; un snippet de l'éditeur.
    /// </summary>
    public static string CallSnippet(string name, int parameters) => parameters == 0 ? name + "()" : name + "($0)";
}
