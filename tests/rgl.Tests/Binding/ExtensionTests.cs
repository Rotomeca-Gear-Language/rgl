using Rgl.Binding;
using Rgl.Diagnostics;
using Rgl.Emit;
using Rgl.IR;
using Rgl.Parsing;
using Rgl.Project;
using Rgl.Syntax;
using Rgl.Tests.Runtime;
using Rgl.Text;

namespace Rgl.Tests.Binding;

/// <summary>
/// Tests des extensions (<c>extend T { … }</c>) : constantes <c>T.nom</c>, méthodes <c>v.f()</c> à premier paramètre
/// <c>this</c>, gears sur le type, portée du fichier, erreurs E119.
/// </summary>
public class ExtensionTests
{
    /// <summary>Lie un fichier <c>forge: gears</c> (les extensions n'existent que là) puis un fichier d'entrée.</summary>
    private static (IrProgram? Program, IReadOnlyList<Diagnostic> Diagnostics) Bind(string forge, string main = "!train |> done;")
    {
        var diagnostics = new DiagnosticBag();
        var modules = new List<SourceModule>();
        var files = new[] { ("Outils", "Outils.forge", "forge: gears\ndescription: \"o\"\n\n" + forge, false), ("main", "main.rgl", main, true) };
        foreach (var (name, path, content, isEntry) in files)
        {
            var tokens = new Lexer(new SourceText(path, content), diagnostics).Tokenize();
            var unit = new Parser(tokens, diagnostics).Parse();
            Assert.False(diagnostics.HasErrors, "erreur de syntaxe dans le cas de test : " + string.Join(", ", diagnostics.Items.Select(d => d.Format("."))));
            FileChecks.Check(unit, isEntry, diagnostics);
            if (diagnostics.HasErrors) return (null, diagnostics.Items);
            modules.Add(new SourceModule(name, unit, isEntry));
        }

        var program = new Binder(diagnostics).Bind(modules);
        return (program, diagnostics.Items);
    }

    private static string[] Codes(string forge, string main = "!train |> done;") =>
        [.. Bind(forge, main).Diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Code ?? "-")];

    private static void Valid(string forge)
    {
        var (program, diagnostics) = Bind(forge);
        Assert.DoesNotContain(diagnostics, d => d.Level == DiagnosticLevel.Error);
        Assert.NotNull(program);
    }

    private const string Types = "ingot ProgramValue: uint;\nmold Vec2 { float x; float y; }\n";

    #region Liaison

    [Theory]
    [InlineData("extend ProgramValue { const ProgramValue Valid: 0; }\ngear f(): ProgramValue => ProgramValue.Valid;")]
    [InlineData("extend Vec2 { const Vec2 Zero: {x: 0.0, y: 0.0}; }\ngear f(): Vec2 => Vec2.Zero;")]
    [InlineData("enum Sens { Haut, Bas }\nextend Sens { const uint N: 2; }\ngear f(): uint => Sens.N;")]
    [InlineData("extend Vec2 { gear pure norme(this Vec2 v): float => v.x + v.y; }\ngear f(Vec2 a): float => a.norme();")]
    [InlineData("extend Vec2 { gear pure ajoute(this Vec2 v, float k): Vec2 => {x: v.x + k, y: v.y + k}; }\ngear f(Vec2 a): Vec2 => a.ajoute(1.0).ajoute(2.0);")]
    [InlineData("extend Vec2 { gear pure origine(): Vec2 => {x: 0.0, y: 0.0}; }\ngear f(): Vec2 => Vec2.origine();")]
    [InlineData("extend Vec2 { gear pure norme(this Vec2 v): float => v.x; }\ngear f(Vec2 a): float => Vec2.norme(a);")]
    [InlineData("extend int { gear pure twice(this int n): int => n * 2; }\ngear f(int a): int => a.twice();")]
    [InlineData("extend [int] { gear pure premier(this [int] xs): int => xs[0]; }\ngear f([int] a): int => a.premier();")]
    [InlineData("extend ProgramValue { gear pure plus(this ProgramValue p, uint n): ProgramValue => ProgramValue(p° + n); }\ngear f(ProgramValue a): ProgramValue => a.plus(2);")]
    [InlineData("extend Vec2 { gear pure a(this Vec2 v): float => v.x; }\nextend Vec2 { gear pure b(this Vec2 v): float => v.a(); }")]
    [InlineData("extend Vec2 { gear pure twice(float k): float => k * 2.0; }\ngear f(float a): float => a |> Vec2.twice;")]
    [InlineData("extend Vec2 { gear pure twice(float k): float => k * 2.0; }\ngear f(Vec2 v): float => v.x |> Vec2.twice;")]
    [InlineData("extend ProgramValue { emit setProgramValue(uint n): uint => n; guard verifier(uint n): uint { if (n > 9) { fail \"trop grand\"; } done; } }")]
    public void Les_extensions_se_lient(string extension)
    {
        Valid(Types + extension);
    }

    [Fact]
    public void Un_champ_passe_avant_une_extension_du_meme_nom()
    {
        const string forge = "mold Op { (int) => int f; }\nextend Op { gear pure f(this Op o): int => 1; }\ngear g(Op o): int => o.f(3);";

        var (program, diagnostics) = Bind(forge);

        Assert.DoesNotContain(diagnostics, d => d.Level == DiagnosticLevel.Error);
        Assert.NotNull(program);
    }

    [Fact]
    public void Une_constante_d_extension_peut_servir_de_case()
    {
        Valid(Types + "extend ProgramValue { const ProgramValue Valid: 0; }\ngear f(ProgramValue p): uint { switch (p) { case ProgramValue.Valid: return 1; default: return 2; } }");
    }

    #endregion

    #region Erreurs

    [Fact]
    public void Une_extension_d_un_type_du_fichier_suit_ce_type_chez_ceux_qui_l_importent()
    {
        const string forge = Types + "extend Vec2 { gear pure norme(this Vec2 v): float => v.x; gear pure origine(): Vec2 => {x: 0.0, y: 0.0}; const float PI: 3.0; }\n"
            + "extend ProgramValue { const ProgramValue Valid: 0; }";

        foreach (var main in new[]
        {
            "import Outils;\ngear f(Outils.Vec2 a): float => a.norme() + Outils.Vec2.PI;\n!train |> done;",
            "import Outils;\ngear f(): float => Outils.Vec2.origine().norme();\n!train |> done;",
            "import Outils only {Vec2};\ngear f(): float => Vec2.origine().norme() + Vec2.PI;\n!train |> done;",
            "import Outils;\ngear f(): Outils.ProgramValue => Outils.ProgramValue.Valid;\n!train |> done;",
            "import Outils;\ngear f(): float => Outils.Vec2.norme(Outils.Vec2.origine());\n!train |> done;",
        })
        {
            var (program, diagnostics) = Bind(forge, main);
            Assert.True(program is not null, main + " : " + string.Join(", ", diagnostics.Select(d => d.Format("."))));
        }
    }

    [Fact]
    public void Une_extension_d_un_autre_type_reste_dans_son_fichier()
    {
        const string forge = "extend int { gear pure twice(this int n): int => n * 2; }";

        Assert.NotEmpty(Codes(forge, "import Outils;\ngear f(int a): int => a.twice();\n!train |> done;"));
    }

    [Fact]
    public void Un_emit_qui_renvoie_un_uint_borne_peut_terminer_le_train_d_entree()
    {
        const string forge = "mold Vec2 { int x; }\nextend Vec2 { emit fin(int n): uint<0..255> => 3; }";

        var (program, diagnostics) = Bind(forge, "import Outils;\n!train([string] args) 3 |> Outils.Vec2.fin;");

        Assert.True(program is not null, string.Join(", ", diagnostics.Select(d => d.Format("."))));
    }

    [Theory]
    [InlineData("extend Vec2 { gear pure f(this Vec2 v): float => 1.0; gear pure f(this Vec2 v): float => 2.0; }")]            // doublon
    [InlineData("extend Vec2 { gear pure f(this ProgramValue v): float => 1.0; }")]                                              // this d'un autre type
    [InlineData("extend int { gear pure f(): int => 1; }")]                                                                      // sans this sur un type non nommé
    [InlineData("extend int { const int N: 3; }")]                                                                               // constante sur un type non nommé
    [InlineData("extend Vec2 { const float N: 1.0; gear pure N(this Vec2 v): float => 1.0; }")]                                  // constante et gear de même nom
    [InlineData("extend Vec2 { const float N: 1.0; const float N: 2.0; }")]
    public void Une_extension_invalide_est_signalee(string extension)
    {
        Assert.Contains("E119", Codes(Types + extension));
    }

    [Fact]
    public void Un_nom_inconnu_sur_une_valeur_reste_une_erreur_de_champ()
    {
        Assert.Equal(["E074"], Codes(Types + "extend Vec2 { gear pure a(this Vec2 v): float => v.x; }\ngear f(Vec2 v): float => v.inconnu();"));
    }

    [Fact]
    public void Une_extension_ne_s_ecrit_que_dans_un_fichier_gears()
    {
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(new SourceText("main.rgl", "mold M { int n; }\nextend M { const uint N: 1; }\n!train |> done;"), diagnostics).Tokenize();
        var unit = new Parser(tokens, diagnostics).Parse();
        FileChecks.Check(unit, true, diagnostics);

        Assert.Contains(diagnostics.Items, d => d.Code == DiagnosticCodes.MisplacedDeclaration);

        var tokens2 = new Lexer(new SourceText("T.forge", "forge: train\ndescription: \"t\"\n\nmold M { int n; }\nextend M { const uint N: 1; }\ntrain |> done;"), diagnostics).Tokenize();
        var unit2 = new Parser(tokens2, diagnostics).Parse();
        var second = new DiagnosticBag();
        FileChecks.Check(unit2, false, second);

        Assert.Contains(second.Items, d => d.Code == DiagnosticCodes.MisplacedDeclaration);
    }

    #endregion

    #region Extensions génériques et pure

    [Theory]
    [InlineData("extend<T> [T] { gear pure premier(this [T] xs): T => xs[0]; }\ngear f([int] a): int => a.premier();")]
    [InlineData("extend<T: number> [T] { gear pure premier(this [T] xs): T => xs[0]; }\ngear f([float] a): float => a.premier();")]
    [InlineData("extend<T: number> [T] { gear pure premier(this [T] xs): T => xs[0]; }\ngear f([int] a, [float] b): double => a.premier() + b.premier();")]
    [InlineData("extend<T> T? { gear pure ou(this T? v, T autre): T => v ?? autre; }\ngear f(int? a): int => a.ou(3);")]
    [InlineData("extend<K, V> [K:V] { gear pure combien(this [K:V] d): uint => d.length; }\ngear f([string:int] d): uint => d.combien();")]
    [InlineData("extend<T> [T] { gear pure premier(this [T] xs): T => xs[0]; }\nextend<T> [T] { gear pure dernier(this [T] xs): T => xs[xs.length - 1]; }\ngear f([int] a): int => a.premier() + a.dernier();")]
    public void Une_extension_generique_se_lie(string extension)
    {
        Valid(extension);
    }

    [Fact]
    public void Une_extension_generique_ne_s_applique_qu_a_la_forme_du_type()
    {
        // un [T] n'est pas un int : aucune extension ne convient, c'est l'erreur habituelle d'un nom inconnu sur une valeur
        Assert.NotEmpty(Codes("extend<T> [T] { gear pure premier(this [T] xs): T => xs[0]; }\ngear f(int a): int => a.premier();"));
    }

    [Fact]
    public void Une_contrainte_non_respectee_est_signalee()
    {
        Assert.Equal(["E114"], Codes("extend<T: number> [T] { gear pure premier(this [T] xs): T => xs[0]; }\ngear f([string] a): string => a.premier();"));
    }

    [Theory]
    [InlineData("extend<T> [T] { gear pure premier([T] xs): T => xs[0]; }")]                                   // sans this
    [InlineData("extend<T> [T] { gear pure premier(this [int] xs): int => xs[0]; }")]                           // this d'un autre type
    [InlineData("extend<T> [T] { gear pure a(this [T] xs): uint => xs.length; gear pure a(this [T] xs): uint => 0; }")]   // doublon
    public void Une_extension_generique_invalide_est_signalee(string extension)
    {
        Assert.Contains("E119", Codes(extension));
    }

    [Fact]
    public void Un_gear_pure_n_a_pas_de_parametre_mut()
    {
        Assert.Equal(["E122"], Codes("gear pure f(mut [int] a): int => 1;"));
        Assert.Equal(["E122"], Codes("extend [int] { gear pure vide(this mut [int] xs): int => 1; }"));
        Valid("gear f(mut [int] a): int => 1;");
        Valid("extend [int] { gear vide(this mut [int] xs): int => 1; }");
    }

    #endregion

    #region Namespaces

    private static IReadOnlyList<Diagnostic> BindFiles(params (string Name, string Content)[] forges)
    {
        var diagnostics = new DiagnosticBag();
        var modules = new List<SourceModule>();
        var files = forges.Select(f => (f.Name, Path: f.Name + ".forge", Content: "forge: gears\ndescription: \"o\"\n\n" + f.Content, IsEntry: false)).Append(("main", "main.rgl", "!train |> done;", true));
        foreach (var (name, path, content, isEntry) in files)
        {
            var tokens = new Lexer(new SourceText(path, content), diagnostics).Tokenize();
            var unit = new Parser(tokens, diagnostics).Parse();
            if (diagnostics.HasErrors) return diagnostics.Items;
            FileChecks.Check(unit, isEntry, diagnostics);
            if (diagnostics.HasErrors) return diagnostics.Items;
            modules.Add(new SourceModule(name, unit, isEntry));
        }

        new Binder(diagnostics).Bind(modules);
        return diagnostics.Items;
    }

    private static string[] ErrorCodes(IReadOnlyList<Diagnostic> diagnostics) => [.. diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Code ?? "-")];

    private const string Mathematiques = "namespace A/Math;\nmold Vec2 { float x; float y; }\nextend Vec2 { gear pure n(this Vec2 v): float => v.x; const float PI: 3.0; }";

    private static IReadOnlyList<Diagnostic> Conflit(string appel) => BindFiles(
        ("Outils", Mathematiques),
        ("Autres", "import Outils;\nnamespace B/Math;\nextend Outils.Vec2 { gear pure n(this Outils.Vec2 v): float => v.y; const float PI: 4.0; }\n" + appel));

    [Fact]
    public void Deux_extensions_de_meme_nom_demandent_un_namespace_a_l_appel()
    {
        Assert.Equal(["E119"], ErrorCodes(Conflit("gear f(Outils.Vec2 v): float => v.n();")));
        Assert.Equal(["E119"], ErrorCodes(Conflit("gear f(): float => Outils.Vec2.PI;")));
    }

    [Theory]
    [InlineData("gear f(Outils.Vec2 v): float => v.<A/Math>n();")]
    [InlineData("gear f(Outils.Vec2 v): float => v.<B/Math>n();")]
    [InlineData("gear f(Outils.Vec2 v): float => v.<A/Math>n() + v.<B/Math>n();")]
    [InlineData("gear f(): float => Outils.Vec2.<A/Math>PI + Outils.Vec2.<B/Math>PI;")]
    public void Le_namespace_ecrit_choisit_l_extension(string appel)
    {
        Assert.Empty(ErrorCodes(Conflit(appel)));
    }

    [Theory]
    [InlineData("gear f(Outils.Vec2 v): float => v.<C/Autre>n();")]          // namespace inconnu
    [InlineData("gear f(Outils.Vec2 v): float => v.<A/Math>absent();")]       // gear absent de cette extension
    [InlineData("gear f(Outils.Vec2 v): float => v.<A/Math>x;")]              // pas un appel ni une constante
    public void Un_namespace_mal_utilise_est_signale(string appel)
    {
        Assert.Equal(["E119"], ErrorCodes(Conflit(appel)));
    }

    [Fact]
    public void Un_namespace_est_unique_dans_le_projet()
    {
        var codes = ErrorCodes(BindFiles(("Un", "namespace Mon/Ns;"), ("Deux", "namespace Mon/Ns;")));

        Assert.Equal(["E123"], codes);
        Assert.Empty(ErrorCodes(BindFiles(("Un", "namespace Mon/Ns;"), ("Deux", "namespace Mon/Autre;"))));
    }

    [Theory]
    [InlineData("namespace Mon Ns;")]
    [InlineData("namespace /Ns;")]
    [InlineData("namespace Mon/;")]
    [InlineData("namespace Mon/Ns;\nnamespace Autre;")]
    public void Un_namespace_invalide_est_signale(string declaration)
    {
        Assert.Contains("E123", ErrorCodes(BindFiles(("Un", declaration))));
    }

    [Theory]
    [InlineData("namespace Mon/Name-Space_2;")]
    [InlineData("namespace A;")]
    public void Un_namespace_accepte_lettres_chiffres_tiret_et_souligne(string declaration)
    {
        Assert.Empty(ErrorCodes(BindFiles(("Un", declaration))));
    }

    [Fact]
    public void Un_namespace_ne_s_ecrit_que_dans_un_fichier_gears()
    {
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(new SourceText("main.rgl", "namespace A/B;\n!train |> done;"), diagnostics).Tokenize();
        var unit = new Parser(tokens, diagnostics).Parse();
        FileChecks.Check(unit, true, diagnostics);

        Assert.Contains(diagnostics.Items, d => d.Code == DiagnosticCodes.MisplacedDeclaration);
    }

    #endregion

    [Fact]
    public void Le_genre_de_fichier_extend_est_connu()
    {
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(new SourceText("E.forge", "forge: extend\ndescription: \"e\"\n\nnamespace A/B;\nmold V { int n; }\nextend V { gear pure f(this V v): int => v.n; }"), diagnostics).Tokenize();
        var unit = new Parser(tokens, diagnostics).Parse();
        FileChecks.Check(unit, false, diagnostics);

        Assert.DoesNotContain(diagnostics.Items, d => d.Level == DiagnosticLevel.Error);
    }

    #region Exécution

    [Fact]
    public void Un_programme_avec_des_extensions_compile_et_s_execute()
    {
        // Les extensions ne s'écrivent que dans un fichier gears : tout s'exécute depuis un gear de ce fichier.
        const string forge = """
            const uint Valid: 5;
            ingot ProgramValue: uint;
            mold Vec2 { float x; float y; }
            extend ProgramValue {
                const ProgramValue Valid: 0;
                const ProgramValue Failure: 1;
                gear pure plus(this ProgramValue p, uint n): ProgramValue => ProgramValue(p° + n);
                gear pure make(uint n): ProgramValue => ProgramValue(n);
            }
            extend Vec2 {
                const Vec2 Zero: {x: 0.0, y: 0.0};
                gear pure norme(this Vec2 v): float => v.x + v.y;
                gear pure ajoute(this Vec2 v, float k): Vec2 => {x: v.x + k, y: v.y + k};
            }
            extend int { gear pure twice(this int n): int => n * 2; }
            extend [int] { gear pure premier(this [int] xs): int => xs[0]; }

            gear calcul(): uint {
                Vec2 a: {x: 1.0, y: 2.0};
                ProgramValue p: ProgramValue.Valid;
                ProgramValue q: p.plus(3).plus(4);
                ProgramValue r: ProgramValue.make(10);
                [int] xs: [7, 8];
                int k: 21;
                `{a.norme()} {a.ajoute(1.0).norme()} {Vec2.Zero.norme()} {q°} {r.plus(1)°} {ProgramValue.Failure°} {Valid} {k.twice()} {xs.premier()}` -> console;
                return q°;
            }
            """;

        var (program, diagnostics) = Bind(forge, "import Outils only {calcul};\nemit code(uint n): uint => n;\n!train([string] args) calcul() |> code;");
        Assert.True(program is not null, string.Join(", ", diagnostics.Select(d => d.Format("."))));

        using var output = new TempDirectory();
        var main = Path.Combine(output.Path, "main.cpp");
        File.WriteAllText(main, CppEmitter.Emit(program, "main.rgl").Code);
        var executable = ZigBuild.Compile(output.Path, "programme", warningsAsErrors: true,
            Path.Combine(ZigBuild.Runtime, "src", "arguments.cpp"), Path.Combine(ZigBuild.Runtime, "src", "console.cpp"), main);
        var (code, text) = ZigBuild.RunWithInput(executable, null, "un argument");

        Assert.Equal(7, code);
        Assert.Equal("3.0 5.0 0.0 7 11 1 5 42 7", text);
    }

    #endregion
}
