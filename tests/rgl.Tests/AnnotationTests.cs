using Rgl.Binding;
using Rgl.Diagnostics;
using Rgl.Emit;
using Rgl.Project;
using Rgl.Tests.Runtime;
using Rgl.Text;

namespace Rgl.Tests;

/// <summary>Les annotations <c>@deprecated</c> et <c>@tap</c>, et les métadonnées <c>[…]</c>.</summary>
public class AnnotationTests
{
    /// <summary>Compile et exécute un programme du projet ; renvoie son code et ce qu'il écrit.</summary>
    private static (int Code, string Output) Run(string main)
    {
        using var directory = new TempDirectory();
        var diagnostics = new DiagnosticBag();
        var source = new SourceText(Path.Combine(directory.Path, "main.rgl"), main);
        var modules = SourceGraph.Load(source, directory.Path, diagnostics);
        var program = modules is null ? null : new Binder(diagnostics).Bind(modules);
        Assert.True(program is not null, "programme invalide : " + string.Join(", ", diagnostics.Items.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Format("."))));

        var cpp = Path.Combine(directory.Path, "main.cpp");
        System.IO.File.WriteAllText(cpp, CppEmitter.Emit(program, "main.rgl").Code);
        var executable = ZigBuild.Compile(directory.Path, "programme", warningsAsErrors: true,
            Path.Combine(ZigBuild.Runtime, "src", "arguments.cpp"), Path.Combine(ZigBuild.Runtime, "src", "console.cpp"), cpp);
        var result = ZigBuild.RunWithInput(executable, null);
        return (result.Code, result.Output.Replace("\r\n", "\n"));
    }

    /// <summary>Les erreurs et les avertissements d'un programme d'un seul fichier.</summary>
    private static (string[] Errors, string[] Warnings) Diagnose(string content)
    {
        using var directory = new TempDirectory();
        var path = directory.File("main.rgl", content);
        var diagnostics = new DiagnosticBag();
        var modules = SourceGraph.Load(new SourceText(path, content), directory.Path, diagnostics);
        if (modules is not null) new Binder(diagnostics).Bind(modules);
        string[] Of(DiagnosticLevel level) => [.. diagnostics.Items.Where(d => d.Level == level).Select(d => d.Code + " " + d.Message)];
        return (Of(DiagnosticLevel.Error), Of(DiagnosticLevel.Warning));
    }

    private const string Tail = """

        emit Fin(): uint => 0;
        !train |> Test |> Fin;
        """;

    [ZigFact]
    public void Un_tap_s_execute_avant_et_apres_le_gear_sans_changer_sa_valeur()
    {
        var (code, output) = Run("""
            @tap(before, `entrée {n}|` -> console)
            @tap(before, `premier {$}|` -> console)
            @tap(`sortie {$}|` -> console)
            gear doubler(int n): int => n * 2;

            @tap(`fin|` -> console)
            gear dire(string s): void { s -> console; }

            gear Test(): void {
                dire(`{doubler(21)}|`);
            }
            """ + Tail);

        Assert.Equal((0, "entrée 21|premier 21|sortie 42|42|fin|"), (code, output));
    }

    [ZigFact]
    public void Un_tap_closure_recoit_la_valeur_ou_les_parametres_du_gear()
    {
        var (code, output) = Run("""
            @tap(closure, before, (int x, int y) => { `somme de {x} et {y}|` -> console; })
            @tap(closure, after, (int v) => { `donne {v}|` -> console; })
            gear somme(int a, int b): int {
                return a + b;
            }

            gear Test(): void {
                int s: somme(2, 3);
            }
            """ + Tail);

        Assert.Equal((0, "somme de 2 et 3|donne 5|"), (code, output));
    }

    [ZigFact]
    public void Une_lambda_sans_parametre_est_un_tap_closure_implicite_et_garde_ses_dependances()
    {
        var (code, output) = Run("""
            use <Console> only {writeLine};

            @tap(() => writeLine('Démarrage du serveur...'))
            gear creer(): int => 42;

            gear Test(): void {
                int valeur: creer();
            }
            """ + Tail);

        Assert.Equal((0, "Démarrage du serveur...\n"), (code, output));
    }

    [ZigFact]
    public void Un_guard_qui_echoue_n_execute_pas_ses_taps_apres()
    {
        var program = """
            use <Errors> only {Exception};
            @tap(before, `racine de {n}|` -> console)
            @tap(`ok {$}|` -> console)
            guard racine(int n): int {
                if (n < 0) { fail "négatif"; }
                return n;
            }

            guard Test(): int {
                int a: racine(4);
                int b: racine(NEG);
                return a + b;
            }
            emit Signaler(Exception e): uint {
                `{e.message}` -> console;
                return 7;
            }
            train |> Test !> [Signaler] |> done;
            """;

        Assert.Equal((0, "racine de 4|ok 4|racine de 4|ok 4|8"), Run(program.Replace("NEG", "4").Replace("train |> Test !> [Signaler] |> done;", "gear Dire(int n): void { `{n}` -> console; }\ntrain |> Test !> [Signaler] |> Dire |> done;")));
        Assert.Equal((7, "racine de 4|ok 4|racine de -1|négatif"), Run(program.Replace("NEG", "-1")));
    }

    [Fact]
    public void Un_gear_obsolete_avertit_a_chaque_usage_sauf_depuis_un_gear_obsolete()
    {
        var (errors, warnings) = Diagnose("""
            @deprecated("Utiliser nouveau()")
            gear ancien(): int => 1;

            @deprecated
            gear aussi(): int => ancien();

            gear nouveau(): int => ancien() + 1;

            gear viaLambda(): int {
                var f: ancien;
                return f();
            }
            train |> done;
            """);

        Assert.Empty(errors);
        var deprecated = warnings.Where(w => w.StartsWith("W061", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, deprecated.Length);                                  // nouveau() et la référence de viaLambda ; pas ancien() dans aussi()
        Assert.All(deprecated, w => Assert.Contains("ancien", w));
        Assert.Contains("Utiliser nouveau()", deprecated[0]);
    }

    [Fact]
    public void Les_metadonnees_sont_ignorees_et_un_tableau_n_en_est_pas()
    {
        var (errors, warnings) = Diagnose("""
            [db, cache: "5min", description: "Charge le profil"]
            @deprecated("x")
            [autre]
            gear charge(int id): int => id;

            [clé]
            const int N: 3;

            [int] nombres: [1, 2];
            train |> done;
            """);

        // « [int] nombres: … » est une déclaration typée (que le fichier refuse à son niveau), pas des métadonnées.
        Assert.DoesNotContain(errors, e => e.StartsWith("E125", StringComparison.Ordinal) || e.StartsWith("E030", StringComparison.Ordinal));
        Assert.DoesNotContain(warnings, w => w.StartsWith("W061", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("@inconnue\ngear f(): void {}\ntrain |> done;", "E125")]
    [InlineData("@tap(`x` -> console)\ngear pure f(): void {}\ntrain |> done;", "E125")]                      // un tap a un effet
    [InlineData("@tap(`x` -> console)\nemit f(): uint => 0;\n!train |> f;", "E125")]
    [InlineData("@tap(`x` -> console)\nmold M { int x; }\ntrain |> done;", "E125")]                           // pas sur un mold
    [InlineData("@deprecated\nconst int N: 1;\ntrain |> done;", "E125")]
    [InlineData("@deprecated(3)\ngear f(): void {}\ntrain |> done;", "E125")]                                 // un message est une chaîne littérale
    [InlineData("@tap(`{$}` -> console)\ngear f(): void {}\ntrain |> done;", "E048")]                         // pas de valeur à $
    [InlineData("@tap(before, `{$}` -> console)\ngear f(): void {}\ntrain |> done;", "E048")]                // pas de paramètre
    [InlineData("@sync\nmold M { int x; }\ntrain |> done;", "")]                                               // @sync ne se pose que sur un gear
    [InlineData("@tap(closure, after, (string v) => { v -> console; })\ngear f(): int => 1;\ntrain |> done;", "E003")]
    public void Une_annotation_mal_utilisee_est_signalee(string content, string code)
    {
        var (errors, _) = Diagnose(content);

        Assert.NotEmpty(errors);
        if (code.Length > 0) Assert.Contains(errors, e => e.StartsWith(code, StringComparison.Ordinal));
    }

    [Fact]
    public void Un_tap_before_apres_un_tap_after_avertit()
    {
        var (errors, warnings) = Diagnose("@tap(`a` -> console)\n@tap(before, `b` -> console)\ngear f(int n): int => n;\ntrain |> done;");

        Assert.Empty(errors);
        Assert.Contains(warnings, w => w.StartsWith("W062", StringComparison.Ordinal));
    }
}
