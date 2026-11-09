using System.Runtime.CompilerServices;
using Rgl.AST;
using Rgl.Diagnostics;
using Rgl.Parsing;
using Rgl.Syntax;
using Rgl.Text;

namespace Rgl.Tests.Golden;

/// <summary>
/// Tests golden : chaque fichier <c>Golden/parser/*.rgl</c> est lexé et parsé, puis l'arbre
/// affiché et les diagnostics sont comparés aux fichiers attendus posés à côté :
/// <c>nom.ast</c> (format de <see cref="AstPrinter"/>) et <c>nom.diagnostics</c> (un diagnostic par
/// ligne, format de <see cref="Diagnostic.Format"/> ; vide s'il n'y en a aucun).
/// </summary>
/// <remarks>
/// En cas d'écart, ou si un fichier attendu manque, le résultat obtenu est écrit dans
/// <c>nom.ast.actual</c> / <c>nom.diagnostics.actual</c> (ignorés par git) : on les relit, et s'ils
/// sont justes on les renomme sans <c>.actual</c>. Les fichiers sont lus dans les sources du dépôt,
/// pas dans le dossier de sortie, pour que les <c>.actual</c> atterrissent à côté des attendus.
/// </remarks>
public class GoldenTests
{
    /// <summary>Dossier des cas du parseur, retrouvé à partir de l'emplacement de ce fichier source.</summary>
    private static string ParserCases([CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here)!, "parser");

    /// <summary>Nom de chaque cas (fichier <c>.rgl</c> sans dossier), pour xUnit.</summary>
    public static TheoryData<string> Cases() =>
        new(Directory.GetFiles(ParserCases(), "*.rgl").Select(Path.GetFileName).Order()!);

    [Theory]
    [MemberData(nameof(Cases))]
    public void Le_parseur_produit_l_arbre_et_les_diagnostics_attendus(string file)
    {
        var directory = ParserCases();
        var diagnostics = new DiagnosticBag();

        var source = SourceText.FromPath(Path.Combine(directory, file), diagnostics);
        var ast = "";
        if (source is not null)
        {
            var tokens = new Lexer(source, diagnostics).Tokenize();
            ast = AstPrinter.Print(new Parser(tokens, diagnostics).Parse());
        }

        var lines = diagnostics.Items.Select(diagnostic => diagnostic.Format(directory) + "\n");

        var basePath = Path.Combine(directory, Path.GetFileNameWithoutExtension(file));
        var failures = new[]
        {
            _Compare(basePath + ".ast", ast),
            _Compare(basePath + ".diagnostics", string.Concat(lines)),
        }.Where(failure => failure is not null).ToArray();

        Assert.True(failures.Length == 0, string.Join("\n", failures));
    }

    /// <summary>
    /// Compare <paramref name="actual"/> au contenu de <paramref name="expectedPath"/>, fins de ligne
    /// normalisées. Écrit ou supprime le fichier <c>.actual</c> selon le résultat.
    /// </summary>
    /// <returns>Un message d'échec, ou <see langword="null"/> si le résultat est celui attendu.</returns>
    private static string? _Compare(string expectedPath, string actual)
    {
        var actualPath = expectedPath + ".actual";

        if (File.Exists(expectedPath) && File.ReadAllText(expectedPath).ReplaceLineEndings("\n") == actual)
        {
            File.Delete(actualPath);
            return null;
        }

        File.WriteAllText(actualPath, actual);
        return File.Exists(expectedPath)
            ? $"{Path.GetFileName(expectedPath)} diffère du résultat : comparer avec {Path.GetFileName(actualPath)}."
            : $"{Path.GetFileName(expectedPath)} n'existe pas : relire {Path.GetFileName(actualPath)} et le renommer s'il est juste.";
    }
}
