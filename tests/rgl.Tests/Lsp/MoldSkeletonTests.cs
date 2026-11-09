using Rgl.Lsp;

namespace Rgl.Tests.Lsp;

/// <summary>La valeur d'un mold proposée en grisé après <c>Type nom:</c>.</summary>
public class MoldSkeletonTests
{
    private const string Header = "mold Vector2 { double x; double y; }\nmold Config { string name: 'x'; int port: 80; }\nmold Mixte { int id; string? note; bool actif: true; }\n";

    /// <summary>Le squelette à la place marquée par <c>|</c> dans le texte.</summary>
    private static MoldSkeletonProvider.Skeleton? At(string body)
    {
        var text = (Header + body).Replace("|", "");
        var offset = (Header + body).IndexOf('|');
        var document = SemanticDocument.Create("main.rgl", text, new ProjectContext("", "", []));
        return MoldSkeletonProvider.At(document, offset);
    }

    [Fact]
    public void Apres_le_deux_points_d_un_mold_les_champs_obligatoires_se_proposent_dans_l_ordre()
    {
        Assert.Equal(" {x: $1, y: $2}$0", At("gear f(): void {\n    Vector2 vec:|\n}\n")!.Snippet);
    }

    [Fact]
    public void Une_espace_deja_ecrite_n_est_pas_doublee()
    {
        Assert.Equal("{x: $1, y: $2}$0", At("gear f(): void {\n    Vector2 vec: |\n}\n")!.Snippet);
    }

    [Fact]
    public void Seuls_les_champs_obligatoires_sont_ecrits()
    {
        Assert.Equal(" {id: $1}$0", At("gear f(): void {\n    mut Mixte m:|\n}\n")!.Snippet);
    }

    [Fact]
    public void Rien_n_est_propose_sans_champ_obligatoire_pour_un_type_sans_mold_ou_quand_une_valeur_existe()
    {
        Assert.Null(At("gear f(): void {\n    Config c:|\n}\n"));
        Assert.Null(At("gear f(): void {\n    int n:|\n}\n"));
        Assert.Null(At("gear f(): void {\n    var v:|\n}\n"));
        Assert.Null(At("gear f(): void {\n    Vector2 vec:| {x: 1.0, y: 2.0};\n}\n"));
        Assert.Null(At("gear f(): void {\n    Vector2 vec: {|\n}\n"));
    }
}
