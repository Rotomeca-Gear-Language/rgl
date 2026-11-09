using Rgl.Diagnostics;
using Rgl.Lsp;

namespace Rgl.Tests;

/// <summary>L'index qu'écrit « Nouveau dossier Box… » : un fichier valide dès sa création, importable par <c>import box</c>.</summary>
public class NewBoxIndexTests
{
    /// <summary>Le contenu écrit par <c>indexContent</c> de l'extension.</summary>
    private const string Index = "forge: index\ndescription: 'La base'\n\n// Importez les fichiers de la box, puis exportez leurs noms :\n// import Fichier;\n// export {...Fichier};\n";

    [Fact]
    public void Un_index_neuf_est_valide_et_la_box_s_importe()
    {
        using var directory = new TempDirectory();
        directory.File("rgl.json", "{ \"name\": \"p\", \"version\": \"1.0.0\", \"rgl\": \"0\" }");
        directory.File("Base.box/index.forge", Index);
        var text = "import box Base;\ntrain |> done;\n";
        var path = directory.File("main.rgl", text);

        var report = DocumentAnalysis.Analyze(path, text, null);

        Assert.DoesNotContain(report.Diagnostics, d => d.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public void L_index_ouvert_dans_l_editeur_n_a_pas_d_erreur()
    {
        using var directory = new TempDirectory();
        directory.File("rgl.json", "{ \"name\": \"p\", \"version\": \"1.0.0\", \"rgl\": \"0\" }");
        var path = directory.File("Base.box/index.forge", Index);

        var report = DocumentAnalysis.Analyze(path, Index, null);

        Assert.Empty(report.Diagnostics.Where(d => d.Level == DiagnosticLevel.Error));
    }
}
