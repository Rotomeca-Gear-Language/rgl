using Rgl.Lsp;

namespace Rgl.Tests.Lsp;

/// <summary>L'éditeur et les annotations : complétion après <c>@</c>, survol, définition, coloration, plan du document.</summary>
public class AnnotationEditorTests
{
    private const string Roles = """
        forge: gears
        description: "Rôles"

        /// Exige l'un des rôles donnés.
        annotation exigeRole(params [int] roles) = @before(controle);

        gear controle(int n): string? => null;
        """;

    private static (SemanticDocument Document, TempDirectory Directory, string Text, int Offset) Open(string main)
    {
        var directory = new TempDirectory();
        directory.File("rgl.json", "{ \"name\": \"p\", \"version\": \"1.0.0\", \"rgl\": \"0\" }");
        directory.File("roles.forge", Roles);
        var cursor = System.Text.RegularExpressions.Regex.Match(main, @"\|(?!>)");
        var text = cursor.Success ? main.Remove(cursor.Index, 1) : main;
        var path = directory.File("main.rgl", text);
        var project = new ProjectIndex().Open(path, _ => null);
        return (SemanticDocument.Create(path, text, project), directory, text, cursor.Success ? cursor.Index : 0);
    }

    private static IReadOnlyList<CompletionItem> Complete(string main)
    {
        var (document, directory, text, offset) = Open(main);
        using (directory)
        {
            var before = text[..offset];
            var line = (uint)before.Count(c => c == '\n');
            var character = (uint)(offset - (before.LastIndexOf('\n') + 1));
            return CompletionProvider.Complete(document.Self.Path, text, line, character, document.Project);
        }
    }

    [Fact]
    public void Apres_un_arobase_on_propose_les_annotations_du_langage_et_celles_du_programme()
    {
        var items = Complete("annotation locale(int n) = @before(c);\n@|\ngear f(): void {}\ntrain |> done;");

        Assert.Contains(items, i => i.Label == "before" && i.InsertText == "before($0)");
        Assert.Contains(items, i => i.Label == "tap");
        Assert.Contains(items, i => i.Label == "exigeRole" && i.InsertText == "exigeRole($0)" && i.Documentation == "Exige l'un des rôles donnés.");
        Assert.Contains(items, i => i.Label == "locale");
        Assert.DoesNotContain(items, i => i.Label == "gear");
    }

    [Fact]
    public void Le_survol_et_la_definition_d_une_annotation_menent_a_sa_declaration()
    {
        var (document, directory, _, offset) = Open("@exige|Role(1)\ngear f(): void {}\ntrain |> done;");
        using (directory)
        {
            var target = document.TargetAt(offset);
            Assert.NotNull(target?.Symbol);
            Assert.Equal(RglSymbolKind.Annotation, target!.Symbol!.Kind);
            Assert.EndsWith("roles.forge", target.Symbol.Module!.Path);

            var hover = HoverProvider.At(document, offset)!.Markdown;
            Assert.Contains("annotation exigeRole(params [int] roles) = @before(controle)", hover);
            Assert.Contains("Exige l'un des rôles donnés.", hover);
        }
    }

    [Fact]
    public void Une_annotation_du_langage_est_expliquee_au_survol()
    {
        var (document, directory, _, offset) = Open("@be|fore(controle)\ngear f(): void {}\ntrain |> done;");
        using (directory) Assert.Contains("avant le corps du gear", HoverProvider.At(document, offset)!.Markdown);
    }

    [Fact]
    public void Le_nom_d_une_annotation_est_colore_et_apparait_dans_le_plan()
    {
        var (document, directory, text, _) = Open("annotation locale(int n) = @before(c);\n@locale(1)\ngear f(): void {}\ntrain |> done;");
        using (directory)
        {
            var data = SemanticTokensProvider.Encode(document);
            var decorators = new List<int>();
            for (var i = 0; i < data.Length; i += 5)
                if (SemanticTokensProvider.Types[data[i + 3]] == "decorator") decorators.Add(data[i]);
            Assert.NotEmpty(decorators);

            var plan = DocumentSymbolProvider.Of(document);
            Assert.Contains(plan, e => e.Name == "@locale" && e.Kind == 12);
        }
    }
}
