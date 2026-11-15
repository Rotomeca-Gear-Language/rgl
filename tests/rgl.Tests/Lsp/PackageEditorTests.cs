using Rgl.Cli;
using Rgl.Diagnostics;
using Rgl.Lsp;
using Xunit;

namespace Rgl.Tests.Lsp;

/// <summary>
/// Les paquets dans l'éditeur (slice V1.3D) : l'éditeur lit les paquets du cache et de <c>rgl.lock</c>, sans réseau et sans modifier le projet. Complétion
/// des noms de paquets et de ce qu'ils exportent, survol, diagnostics de ce qui n'a pas été récupéré, action rapide « rgl restore ».
/// </summary>
public sealed class PackageEditorTests : IDisposable
{
    private const string Json = "github.com/alice/json";

    private readonly TempDirectory _project = new();
    private readonly TempDirectory _cache = new();

    public PackageEditorTests()
    {
        var source = new FakePackageSource();
        source.Publish(Json, "1.2.0", "json", files: new()
        {
            ["index.forge"] = "forge: index\ndescription: 'i'\n\nimport Ops;\nexport {...Ops};\n",
            ["Ops.forge"] = "forge: gears\ndescription: 'o'\n\n/// Double un entier.\nexport gear pure twice(int n): int => n * 2;\ngear pure secret(int n): int => n;\n",
        });
        _project.File("rgl.json", "{\"name\": \"app\", \"version\": \"1.0.0\", \"rgl\": \"0\"}");
        _project.File("main.rgl", "train |> done;\n");
        var add = CliRunner.Run(["add", $"{Json}@1.2.0"], new CliContext(new StringWriter(), new StringWriter(), _project.Path, _cache.Path, Packages: source));
        Assert.Equal(0, add);
        EditorPackages.CacheRoot = () => _cache.Path;
    }

    public void Dispose()
    {
        EditorPackages.CacheRoot = Rgl.Build.Cache.DefaultRoot;
        _project.Dispose();
        _cache.Dispose();
    }

    private string LockText => File.ReadAllText(Path.Combine(_project.Path, "rgl.lock"));

    private (SemanticDocument Document, string Text, int Offset) Open(string main)
    {
        var cursor = System.Text.RegularExpressions.Regex.Match(main, @"\|(?!>)");
        var text = cursor.Success ? main.Remove(cursor.Index, 1) : main;
        var path = _project.File("main.rgl", text);
        var project = new ProjectIndex().Open(path, _ => null);
        return (SemanticDocument.Create(path, text, project), text, cursor.Success ? cursor.Index : 0);
    }

    private IReadOnlyList<CompletionItem> Complete(string main)
    {
        var (document, text, offset) = Open(main);
        var before = text[..offset];
        var line = (uint)before.Count(c => c == '\n');
        var character = (uint)(offset - (before.LastIndexOf('\n') + 1));
        return CompletionProvider.Complete(document.Self.Path, text, line, character, document.Project);
    }

    [Fact]
    public void Un_paquet_recupere_s_analyse_sans_erreur_dans_l_editeur()
    {
        var text = "import package json;\ngear f(): int => json.twice(2);\ntrain |> done;\n";
        var report = DocumentAnalysis.Analyze(_project.File("main.rgl", text), text);

        Assert.DoesNotContain(report.Diagnostics, d => d.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public void Ce_que_l_index_n_exporte_pas_reste_une_erreur_dans_l_editeur()
    {
        var text = "import package json;\ngear f(): int => json.secret(2);\ntrain |> done;\n";
        var report = DocumentAnalysis.Analyze(_project.File("main.rgl", text), text);

        Assert.Contains(report.Diagnostics, d => d.Code == DiagnosticCodes.UnknownGear);
    }

    [Fact]
    public void Un_paquet_absent_du_cache_est_E155_sur_l_import_et_l_editeur_ne_touche_pas_au_lock()
    {
        var before = LockText;
        Directory.Delete(Path.Combine(_cache.Path, "packages"), recursive: true);
        // Le résultat mis en mémoire a été calculé avec le cache : on laisse passer la durée de vie d'un échec en changeant la date du manifeste.
        File.SetLastWriteTimeUtc(Path.Combine(_project.Path, "rgl.json"), DateTime.UtcNow.AddMinutes(1));
        var text = "import package json;\ngear f(): int => json.twice(2);\ntrain |> done;\n";

        var report = DocumentAnalysis.Analyze(_project.File("main.rgl", text), text);

        var onImport = Assert.Single(report.Diagnostics, d => d.Code == DiagnosticCodes.PackageNotFound && d.Span is not null);
        Assert.Contains("rgl restore", string.Join(' ', onImport.Help ?? []));
        // Aucune cascade « gear introuvable » : le paquet manque, ce n'est pas la faute du code.
        Assert.DoesNotContain(report.Diagnostics, d => d.Code == DiagnosticCodes.UnknownGear);
        Assert.Equal(before, LockText);
        Assert.False(Directory.Exists(Path.Combine(_cache.Path, "packages")));
    }

    [Fact]
    public void L_action_rapide_d_un_paquet_absent_lance_rgl_restore()
    {
        var (document, _, _) = Open("import package json;\ntrain |> done;\n");

        var actions = CodeActionProvider.At(document, 0, 0, [DiagnosticCodes.PackageNotFound]);

        var restore = Assert.Single(actions, a => a.Command == "rgl.restore");
        Assert.Empty(restore.Edits);
        Assert.Equal(CodeActionProvider.QuickFix, restore.Kind);
        Assert.DoesNotContain(CodeActionProvider.At(document, 0, 0, []), a => a.Command is not null);
    }

    [Fact]
    public void La_completion_propose_les_paquets_du_projet_par_leur_nom_puis_par_leur_adresse()
    {
        var byName = Complete("import package |");
        var json = Assert.Single(byName, i => i.Label == "json");
        Assert.Contains(Json, json.Detail);
        Assert.Contains("1.2.0", json.Detail);

        var byAddress = Complete("import package \"|");
        Assert.Contains(byAddress, i => i.Label == Json);

        // « package » se propose parmi les genres d'import.
        Assert.Contains(Complete("import |"), i => i.Label == "package");
    }

    [Fact]
    public void La_completion_d_un_only_propose_ce_que_l_index_du_paquet_exporte()
    {
        var items = Complete("import package json only {|");

        Assert.Contains(items, i => i.Label == "twice");
        Assert.DoesNotContain(items, i => i.Label == "secret");

        var fromAddress = Complete($"import package \"{Json}\" as J only {{|");
        Assert.Contains(fromAddress, i => i.Label == "twice");
    }

    [Fact]
    public void Apres_le_point_d_un_paquet_importe_la_completion_propose_ses_noms()
    {
        var items = Complete("import package json;\ngear f(): int => json.|");

        Assert.Contains(items, i => i.Label == "twice");
        Assert.DoesNotContain(items, i => i.Label == "secret");
    }

    [Fact]
    public void Le_survol_d_un_nom_d_un_paquet_montre_sa_signature_et_sa_documentation()
    {
        var (document, _, offset) = Open("import package json;\ngear f(): int => json.tw|ice(2);\ntrain |> done;\n");

        var hover = HoverProvider.At(document, offset);

        Assert.NotNull(hover);
        Assert.Contains("twice", hover.Markdown);
        Assert.Contains("Double un entier", hover.Markdown);
    }
}
