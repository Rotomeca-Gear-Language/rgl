using Rgl.Lsp;

namespace Rgl.Tests.Lsp;

/// <summary>Entre les accolades d'une valeur de mold, l'éditeur propose les champs qui restent à écrire.</summary>
public class MoldCompletionTests
{
    private const string Vec = "mold Vec { double x; double y; string label: 'v'; int? tag; }\n";

    private static IReadOnlyList<CompletionItem> Complete(string source)
    {
        var offset = source.IndexOf('|');
        var text = source.Remove(offset, 1);
        var before = text[..offset];
        return CompletionProvider.Complete(text, (uint)before.Count(c => c == '\n'), (uint)(offset - (before.LastIndexOf('\n') + 1)));
    }

    private static string[] Fields(IReadOnlyList<CompletionItem> items) => [.. items.Where(i => i.Kind == CompletionKind.Field).Select(i => i.Label)];

    [Fact]
    public void Apres_new_le_mold_ecrit_donne_ses_champs_obligatoires_d_abord()
    {
        var items = Complete(Vec + "gear f(): Vec => new Vec { | };\n!train |> done;");

        Assert.Equal(["x", "y", "label", "tag"], Fields(items));
        Assert.StartsWith("0_", items.First(i => i.Label == "x").SortText);
        Assert.StartsWith("1_", items.First(i => i.Label == "label").SortText);
        Assert.StartsWith("1_", items.First(i => i.Label == "tag").SortText);
        Assert.Equal("x: $0", items.First(i => i.Label == "x").InsertText);
    }

    [Fact]
    public void Les_champs_deja_ecrits_ne_sont_plus_proposes_et_le_snippet_remplit_les_obligatoires()
    {
        var items = Complete(Vec + "gear f(): Vec => new Vec { label: 'a', | };\n!train |> done;");
        Assert.Equal(["x", "y", "tag"], Fields(items));
        Assert.Equal("x: $1, y: $2", items.First(i => i.Kind == CompletionKind.Snippet).InsertText);

        var rest = Complete(Vec + "gear f(): Vec => new Vec { x: 1.0, | };\n!train |> done;");
        Assert.Equal(["y", "label", "tag"], Fields(rest));
        Assert.DoesNotContain(rest, i => i.Kind == CompletionKind.Snippet);
    }

    [Fact]
    public void Une_declaration_typee_donne_le_type_attendu_sans_new()
    {
        var items = Complete(Vec + "gear f(): void {\n    Vec v: { | };\n}\n!train |> done;");

        Assert.Equal(["x", "y", "label", "tag"], Fields(items));
    }

    [Fact]
    public void Un_argument_un_retour_et_un_champ_imbrique_trouvent_leur_type()
    {
        Assert.Equal(["x", "y", "label", "tag"], Fields(Complete(Vec + "gear show(Vec v): void {}\ngear f(): void { show({ | }); }\n!train |> done;")));
        Assert.Equal(["x", "y", "label", "tag"], Fields(Complete(Vec + "gear f(): Vec { return { | }; }\n!train |> done;")));
        Assert.Equal(["x", "y", "label", "tag"], Fields(Complete(Vec + "mold Seg { Vec a; Vec b; }\ngear f(): Seg => new Seg { a: { | } };\n!train |> done;")));
    }

    [Fact]
    public void Apres_les_deux_points_ou_dans_un_bloc_rien_de_special()
    {
        Assert.Empty(Fields(Complete(Vec + "gear f(): Vec => new Vec { x: | };\n!train |> done;")));
        Assert.Empty(Fields(Complete(Vec + "gear f(): void { | }\n!train |> done;")));
    }

    [Fact]
    public void Un_mot_commence_filtre_a_la_charge_de_l_editeur_et_n_est_pas_double()
    {
        var items = Complete(Vec + "gear f(): Vec => new Vec { la| };\n!train |> done;");

        Assert.Contains("label", Fields(items));
    }
}
