using Rgl.Lsp;

namespace Rgl.Tests.Lsp;

/// <summary>
/// Ce que fait l'éditeur d'un vrai langage : coloration par sens (un ingot a la couleur d'un type primitif, en italique), autocomplétion de la
/// std, des extensions et du projet, import automatique avec <c>only</c>.
/// </summary>
public class EditorTests
{
    private const string Ids = """
        forge: mold
        description: "Identifiants"

        ingot userId: long;
        mold Player { userId id; string name; }
        enum Team { Red, Blue }
        """;

    private static (SemanticDocument Document, TempDirectory Directory, string Text, int Offset) Open(string main)
    {
        var directory = new TempDirectory();
        directory.File("rgl.json", "{ \"name\": \"p\", \"version\": \"1.0.0\", \"rgl\": \"0\" }");
        directory.File("Ids.forge", Ids);
        var cursor = System.Text.RegularExpressions.Regex.Match(main, @"\|(?!>)");
        var text = cursor.Success ? main.Remove(cursor.Index, 1) : main;
        var path = directory.File("main.rgl", text);
        var project = new ProjectIndex().Open(path, _ => null);
        return (SemanticDocument.Create(path, text, project), directory, text, cursor.Success ? cursor.Index : 0);
    }

    /// <summary>Les jetons de la coloration sémantique, décodés : le texte, sa sorte et ses précisions.</summary>
    private static List<(string Text, string Type, string[] Modifiers)> Tokens(string main)
    {
        var (document, directory, text, _) = Open(main);
        using (directory)
        {
            var data = SemanticTokensProvider.Encode(document);
            var tokens = new List<(string, string, string[])>();
            var (line, column) = (0, 0);
            var lines = text.Split('\n');
            for (var i = 0; i < data.Length; i += 5)
            {
                line += data[i];
                column = data[i] == 0 ? column + data[i + 1] : data[i + 1];
                var modifiers = SemanticTokensProvider.Modifiers.Where((_, bit) => (data[i + 4] & (1 << bit)) != 0).ToArray();
                tokens.Add((lines[line].Substring(column, data[i + 2]), SemanticTokensProvider.Types[data[i + 3]], modifiers));
            }
            return tokens;
        }
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

    #region Coloration

    [Fact]
    public void Un_ingot_a_sa_propre_sorte_a_la_declaration_comme_a_l_usage()
    {
        var tokens = Tokens("ingot score: int;\ngear f(score s): score => score(3);\n!train |> done;");

        Assert.Equal(["ingot", "ingot", "ingot", "ingot"], tokens.Where(t => t.Text == "score").Select(t => t.Type));
        Assert.Contains("declaration", tokens.First(t => t.Text == "score").Modifiers);
    }

    [Fact]
    public void Chaque_sorte_de_nom_a_la_sienne()
    {
        var tokens = Tokens("""
            import Ids only {Player};
            mold Point { int x; }
            enum Dir { Up }
            const int LIMIT: 3;
            gear f(Point p, int n): int {
                var total: p.x + n;
                return total + LIMIT;
            }
            !train |> done;
            """);

        string TypeOf(string text, int occurrence = 0) => tokens.Where(t => t.Text == text).ElementAt(occurrence).Type;
        Assert.Equal("struct", TypeOf("Point"));
        Assert.Equal("enum", TypeOf("Dir"));
        Assert.Equal("enumMember", TypeOf("Up"));
        Assert.Equal("variable", TypeOf("LIMIT"));
        Assert.Contains("readonly", tokens.First(t => t.Text == "LIMIT").Modifiers);
        Assert.Equal("function", TypeOf("f"));
        Assert.Equal("parameter", TypeOf("p"));
        Assert.Equal("variable", TypeOf("total"));
        Assert.Equal("property", TypeOf("x", 1));
        Assert.Equal("struct", TypeOf("Player"));
    }

    [Fact]
    public void La_std_est_marquee_et_std_est_un_espace_de_noms()
    {
        var tokens = Tokens("use <Math> only {sqrt};\ngear g(): double? => sqrt(2.0);\ngear h(): double? => std.sqrt(2.0);\n!train |> done;");

        Assert.All(tokens.Where(t => t.Text == "sqrt"), t => Assert.Contains("defaultLibrary", t.Modifiers));
        Assert.Equal("namespace", tokens.First(t => t.Text == "std").Type);
        Assert.Equal("function", tokens.Last(t => t.Text == "sqrt").Type);
    }

    [Fact]
    public void Un_gear_d_extension_est_une_methode()
    {
        var tokens = Tokens("gear g([int] xs): int => xs.sum();\n!train |> done;");
        var sum = tokens.First(t => t.Text == "sum");

        Assert.Equal("method", sum.Type);
        Assert.Contains("defaultLibrary", sum.Modifiers);
    }

    #endregion

    #region Autocomplétion

    [Fact]
    public void La_completion_suit_une_chaine_de_champs_dont_les_types_viennent_d_autres_fichiers()
    {
        using var directory = new TempDirectory();
        directory.File("rgl.json", "{ \"name\": \"p\", \"version\": \"1.0.0\", \"rgl\": \"0\" }");
        directory.File("Base.forge", "forge: mold\ndescription: \"b\"\n\nmold Named { string name; }\n");
        directory.File("Move.forge", "forge: mold\ndescription: \"m\"\n\nimport Base only {Named};\n\nmold Move { ...Named; int power; }\n");
        directory.File("Moves.forge", "forge: gears\ndescription: \"d\"\n\nimport Move only {Move};\n\ndataset Moves of Move { hit: {name: 'Hit', power: 5}, }\n");
        directory.File("Fighter.forge", "forge: mold\ndescription: \"f\"\n\nimport Moves only {Moves};\n\nmold Fighter { uint pv; [Moves.id+] skills; }\n");
        directory.File("Hero.forge", "forge: mold\ndescription: \"h\"\n\nimport Fighter only {Fighter};\n\nmold Hero { ...Fighter; int lv; }\n");
        const string source = "import Hero only {Hero};\ngear f(Hero h): uint => h.skills[0].get().;\n!train |> done;";
        var path = directory.File("main.rgl", source);
        var project = new ProjectIndex().Open(path, _ => null);
        var offset = source.IndexOf("get().;", StringComparison.Ordinal) + 6;

        var items = CompletionProvider.Complete(path, source, 1, (uint)(offset - source.IndexOf('\n') - 1), project);

        Assert.Contains(items, i => i.Label == "name");
        Assert.Contains(items, i => i.Label == "power");
    }

    [Fact]
    public void Les_champs_d_un_mold_etale_viennent_d_un_fichier_importe_par_le_mold()
    {
        using var directory = new TempDirectory();
        directory.File("rgl.json", "{ \"name\": \"p\", \"version\": \"1.0.0\", \"rgl\": \"0\" }");
        directory.File("Base.forge", "forge: mold\ndescription: \"b\"\n\nmold Base { uint pv; uint pm; }\n");
        directory.File("Hero.forge", "forge: mold\ndescription: \"h\"\n\nimport Base only {Base};\n\nmold Hero { ...Base; int lv; }\n");
        const string source = "import Hero only {Hero};\ngear f(Hero h): uint => h.;\n!train |> done;";
        var path = directory.File("main.rgl", source);
        var project = new ProjectIndex().Open(path, _ => null);
        var offset = source.IndexOf("h.;", StringComparison.Ordinal) + 2;

        var items = CompletionProvider.Complete(path, source, 1, (uint)(offset - source.IndexOf('\n') - 1), project);

        Assert.Contains(items, i => i.Label == "pv");
        Assert.Contains(items, i => i.Label == "pm");
        Assert.Contains(items, i => i.Label == "lv");
    }

    [Fact]
    public void Un_nom_de_la_std_se_propose_et_ajoute_son_use_avec_only()
    {
        var item = Complete("gear f(): double? => sqr|\n!train |> done;").First(i => i.Label == "sqrt");

        Assert.Equal("sqrt($0)", item.InsertText);
        Assert.Contains("use <Math> only {sqrt};", Assert.Single(item.Edits!).Text);
    }

    [Fact]
    public void Un_nom_de_la_std_s_ajoute_au_use_deja_ecrit()
    {
        var item = Complete("use <Math> only {abs};\ngear f(): double? => sqr|\n!train |> done;").First(i => i.Label == "sqrt");

        Assert.Equal(", sqrt", Assert.Single(item.Edits!).Text);
    }

    [Fact]
    public void Apres_std_point_toute_la_std_est_proposee()
    {
        var labels = Complete("gear f(): void { std.|\n}\n!train |> done;").Select(i => i.Label).ToArray();

        Assert.Contains("writeLine", labels);
        Assert.Contains("sqrt", labels);
        Assert.Contains("PI", labels);
    }

    [Fact]
    public void Les_extensions_se_proposent_selon_le_type_de_la_valeur()
    {
        var numbers = Complete("gear f([int] xs): void {\n    xs.|\n}\n!train |> done;").Select(i => i.Label).ToArray();
        var words = Complete("gear f([string] xs): void {\n    xs.|\n}\n!train |> done;").Select(i => i.Label).ToArray();
        var text = Complete("gear f(string s): void {\n    s.|\n}\n!train |> done;").Select(i => i.Label).ToArray();

        Assert.Contains("sum", numbers);
        Assert.Contains("sorted", numbers);
        Assert.DoesNotContain("sum", words);
        Assert.Contains("join", words);
        Assert.Contains("trim", text);
        Assert.DoesNotContain("sorted", text);
    }

    [Fact]
    public void Une_extension_se_propose_avec_ses_parentheses_et_sa_signature()
    {
        var item = Complete("gear f([int] xs): void {\n    xs.ind|\n}\n!train |> done;").First(i => i.Label == "indexOf");

        Assert.Equal("indexOf($0)", item.InsertText);
        Assert.Contains("(this", "(this" + item.Detail, StringComparison.Ordinal);
        Assert.Contains("T what", item.Detail);
        Assert.StartsWith("extension", item.Source);
    }

    [Fact]
    public void Les_noms_du_projet_se_proposent_avec_leur_import()
    {
        var item = Complete("gear f(): void {\n    Play|\n}\n!train |> done;").First(i => i.Label == "Player");

        Assert.Contains("import Ids only {Player};", Assert.Single(item.Edits!).Text);
    }

    [Fact]
    public void Une_extension_ecrite_dans_le_fichier_se_propose_sur_un_champ_du_type_etendu()
    {
        var labels = Complete("ingot line: [bool;3];\nextend line {\n    gear pure asString(this line v): string => 'x';\n}\nmold M { line top; }\ngear s(M m): void {\n    m.top.|\n}\n!train |> done;")
            .Select(i => i.Label).ToArray();

        Assert.Contains("asString", labels);
    }

    [Theory]
    [InlineData("ingot line: [bool;3];\nextend line {\n    const line ZERO: [false, false, false];\n}\nmold M { line top; }\ngear s(): void {\n    var z: line.|\n}\n!train |> done;")]
    [InlineData("ingot line: [bool;3];\nextend line {\n    const line ZERO: [false, false, false];\n}\nmold M { line top; }\nextend M {\n    const M ZERO: {top: line.|};\n}\n!train |> done;")]
    public void Un_type_propose_les_constantes_et_les_gears_statiques_de_ses_extensions(string main)
    {
        var labels = Complete(main).Select(i => i.Label).ToArray();

        Assert.Contains("ZERO", labels);
        Assert.DoesNotContain("length", labels);
    }

    [Fact]
    public void Un_type_de_la_std_propose_les_constantes_de_son_extension()
    {
        var labels = Complete("use <Errors> only {errorCode};\ngear g(): void {\n    var c: errorCode.|\n}\n!train |> done;").Select(i => i.Label).ToArray();

        Assert.Contains("NONE", labels);
        Assert.Contains("NOT_FOUND", labels);
    }

    [Fact]
    public void Un_gear_statique_d_extension_se_propose_avec_ses_parentheses()
    {
        var item = Complete("mold R { int s; }\nextend R {\n    gear seed(int n): R => {s: n};\n}\ngear g(): void {\n    var r: R.|\n}\n!train |> done;").First(i => i.Label == "seed");

        Assert.Equal("seed($0)", item.InsertText);
    }

    [Fact]
    public void Un_ingot_d_un_tableau_ne_propose_pas_les_membres_du_tableau()
    {
        var labels = Complete("ingot line: [bool;3];\nmold M { line top; }\ngear s(M m): void {\n    m.top.|\n}\n!train |> done;").Select(i => i.Label).ToArray();

        Assert.DoesNotContain("length", labels);
        Assert.DoesNotContain("push", labels);
    }

    [Fact]
    public void Un_tableau_de_taille_fixe_ne_propose_pas_push()
    {
        var fixedSize = Complete("gear s([int;3] a, [int] b): void {\n    a.|\n}\n!train |> done;").Select(i => i.Label).ToArray();
        var growing = Complete("gear s([int;3] a, [int] b): void {\n    b.|\n}\n!train |> done;").Select(i => i.Label).ToArray();

        Assert.Contains("length", fixedSize);
        Assert.DoesNotContain("push", fixedSize);
        Assert.Contains("push", growing);
    }

    [Fact]
    public void Un_ingot_de_mold_propose_les_champs_du_mold()
    {
        var labels = Complete("mold P { int x; }\ningot Q: P;\ngear s(Q q): void {\n    q.|\n}\n!train |> done;").Select(i => i.Label).ToArray();

        Assert.Contains("x", labels);
    }

    [Fact]
    public void La_std_se_propose_aussi_dans_un_document_non_enregistre()
    {
        const string text = "gear nom(int n): int {\n    wr\n}\n";
        var project = new ProjectIndex().Open("untitled:Untitled-1", _ => null);

        var labels = CompletionProvider.Complete("untitled:Untitled-1", text, 1, 6, project).Select(i => i.Label).ToArray();

        Assert.Contains("writeLine", labels);
    }

    #endregion

    [Theory]
    [InlineData("mold { int x; }\ngear (): void { }\n!train |> done;")]
    [InlineData("enum { A, B }\nextend { }\ngear f(): void { }\n!train |> done;")]
    [InlineData("mold P { int ; string }\n!train |> done;")]
    public void Le_plan_d_un_fichier_en_cours_d_ecriture_n_a_pas_d_element_sans_nom(string main)
    {
        var (document, directory, _, _) = Open(main);
        using (directory)
        {
            var names = new List<string>();
            void Collect(IEnumerable<DocumentSymbolEntry> entries)
            {
                foreach (var entry in entries)
                {
                    names.Add(entry.Name);
                    Collect(entry.Children);
                }
            }
            Collect(DocumentSymbolProvider.Of(document));
            Assert.All(names, name => Assert.False(string.IsNullOrWhiteSpace(name)));
        }
    }
}
