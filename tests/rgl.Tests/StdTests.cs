using Rgl.Binding;
using Rgl.Build;
using Rgl.Build.Toolchain;
using Rgl.Diagnostics;
using Rgl.Emit;
using Rgl.Project;
using Rgl.Tests.Runtime;
using Rgl.Text;

namespace Rgl.Tests;

/// <summary>La bibliothèque standard embarquée, écrite en RGL : chaque module compile et donne les bons résultats dans un vrai programme.</summary>
public class StdTests
{
    /// <summary>Compile et exécute un programme du projet qui utilise la std ; renvoie ce qu'il écrit.</summary>
    private static string Run(string main, int expectedCode = 0)
    {
        using var directory = new TempDirectory();
        var diagnostics = new DiagnosticBag();
        var source = new SourceText(Path.Combine(directory.Path, "main.rgl"), main);
        var modules = SourceGraph.Load(source, directory.Path, diagnostics);
        var program = modules is null ? null : new Binder(diagnostics).Bind(modules);
        Assert.True(program is not null, "programme invalide : " + string.Join(", ", diagnostics.Items.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Format("."))));

        File.WriteAllText(source.Path, main);
        var project = RglProject.Load("main.rgl", null, directory.Path, diagnostics)!;
        var cache = Path.Combine(Path.GetTempPath(), "rgl-tests-cache");
        var built = BuildPipeline.Run(project, cache, BuildOutput.Executable, BuildProfile.Debug, ZigToolchain.Locate(cache), diagnostics);
        Assert.True(built is not null, string.Join("\n", diagnostics.Items.Select(d => d.Format("."))));
        var executable = built.Executable!;
        var result = ZigBuild.RunWithInput(executable, null, "un argument");
        Assert.Equal(expectedCode, result.Code);
        return result.Output.Replace("\r\n", "\n");
    }

    private static string Program(string body) => "gear Test(): void {\n" + body + "\n}\nemit Fin(): uint => 0;\n!train |> Test |> Fin;";

    [Fact]
    public void Les_fichiers_de_la_std_se_lient_sans_erreur()
    {
        using var directory = new TempDirectory();
        var diagnostics = new DiagnosticBag();
        var modules = SourceGraph.Load(new SourceText(Path.Combine(directory.Path, "main.rgl"), "!train |> done;"), directory.Path, diagnostics);
        var program = modules is null ? null : new Binder(diagnostics).Bind(modules);

        Assert.True(program is not null, string.Join(", ", diagnostics.Items.Select(d => d.Format("."))));
        Assert.Contains(modules!, m => m.Name == "std.Math.box.Math");
    }

    [ZigFact]
    public void Les_chaines()
    {
        var output = Run(Program("""
            string s: '  Bonjour le monde  ';
            std.writeLine(s.trim());
            std.writeLine(s.toUpper().trim());
            std.writeLine(s.trim().substring(-5, 100));
            std.writeLine(s.contains('le'));
            std.writeLine(s.indexOf('le') ?? -1);
            std.writeLine(s.trim().startsWith('Bon'));
            std.writeLine(s.trim().endsWith('monde'));
            std.writeLine('ab'.repeat(3));
            std.writeLine('abc'.reverse());
            std.writeLine('7'.padStart(3, '0'));
            std.writeLine('a,b,,c'.split(',').join('|'));
            std.writeLine('un deux un'.replace('un', '1'));
            std.writeLine('ababa'.lastIndexOf('ba') ?? -1);
            std.writeLine('   '.isBlank());
            std.writeLine('prefix-value'.removePrefix('prefix-').removeSuffix('-absent'));
            std.writeLine('hello'.capitalize());
            var once: 'key=value=again'.splitOnce('=');
            std.writeLine(`{once.before}|{once.after}|{once.found}`);
            std.writeLine('a-a'.replaceAll('a', 'b'));
            std.writeLine('42'.toInt() ?? -1);
            """));

        Assert.Equal("Bonjour le monde\nBONJOUR LE MONDE\nmonde\ntrue\n10\ntrue\ntrue\nababab\ncba\n007\na|b||c\n1 deux 1\n3\ntrue\nvalue\nHello\nkey|value=again|true\nb-b\n42\n", output);
    }

    [ZigFact]
    public void Les_chemins_sont_portables()
    {
        var output = Run(Program("""
            std.writeLine(std.join('dossier', 'fichier.txt'));
            std.writeLine(std.normalize('a/./b/../c'));
            std.writeLine(std.filename('a/b/fichier.txt'));
            std.writeLine(std.basename('a/b/fichier.txt'));
            std.writeLine(std.extension('a/b/fichier.txt'));
            std.writeLine(std.dirname('a/b/fichier.txt'));
            std.writeLine(std.isAbsolute('a/b'));
            std.writeLine(std.currentDirectory().length > 0);
            std.writeLine(std.executableDirectory() eq std.dirname(std.executablePath()));
            std.writeLine(std.findFile(std.filename(std.executablePath()), std.executableDirectory()) not eq null);
            """));

        Assert.Equal("dossier/fichier.txt\na/c\nfichier.txt\nfichier\n.txt\na/b\nfalse\ntrue\ntrue\ntrue\n", output);
    }

    [ZigFact]
    public void Les_tableaux()
    {
        var output = Run(Program("""
            [int] xs: [5, 3, 9, 1, 3];
            std.writeLine(xs.sorted().join(','));
            std.writeLine(xs.distinct().join(','));
            std.writeLine(xs.filter((x) => x > 2).join(','));
            std.writeLine(xs.find((x) => x > 5) ?? -1);
            std.writeLine(xs.firstWhere((x) => x > 5) ?? -1);
            std.writeLine(xs.count((x) => x eq 3));
            std.writeLine(xs.take(2).join(','));
            std.writeLine(xs.drop(3).join(','));
            std.writeLine(xs.reversed().join(','));
            std.writeLine(xs.product());
            std.writeLine(xs.average() ?? 0.0);
            std.writeLine(xs.any((x) => x > 8));
            std.writeLine(xs.all((x) => x > 1));
            std.writeLine(xs.contains(9));
            std.writeLine(xs.indexOf(9) ?? -1);
            std.writeLine(xs.min() ?? -1);
            std.writeLine(xs.max() ?? -1);
            std.writeLine(xs.sum());
            std.writeLine([[1, 2], [3]].flatten().join(','));
            std.writeLine(std.map(xs, (int x) => x * 2).join(','));
            std.writeLine(std.flatMap([1, 2], (int x) => [x, -x]).join(','));
            var parts: xs.partition((x) => x > 3);
            std.writeLine(`{parts.matches.join(',')}|{parts.others.join(',')}`);
            std.writeLine(xs.takeWhile((x) => x > 2).join(','));
            std.writeLine(xs.dropWhile((x) => x > 2).join(','));
            std.writeLine(std.map(xs.chunk(2), ([int] part) => part.join('')).join('|'));
            std.writeLine(std.map(std.zip([1, 2], ['a', 'b', 'c']), ((int, string) pair) => `{pair.Item1}{pair.Item2}`).join(','));
            std.writeLine(xs.sortedWith((a, b) => a > b ? -1 : (a < b ? 1 : 0)).join(','));
            mut [int] ys: [1, 2, 3, 4];
            ys.reverse();
            std.writeLine(ys.join(','));
            ys.sortWith((a, b) => a < b ? -1 : (a > b ? 1 : 0));
            std.writeLine(ys.join(','));
            """));

        Assert.Equal("1,3,3,5,9\n5,3,9,1\n5,3,9,3\n9\n9\n2\n5,3\n1,3\n3,1,9,3,5\n405\n4.2\ntrue\nfalse\ntrue\n2\n1\n9\n21\n1,2,3\n10,6,18,2,6\n1,-1,2,-2\n5,9|3,1,3\n5,3,9\n1,3\n53|91|3\n1a,2b\n9,5,3,3,1\n4,3,2,1\n1,2,3,4\n", output);
    }

    [ZigFact]
    public void Les_maths()
    {
        var output = Run(Program("""
            std.writeLine(std.sqrt(2.0) ?? -1.0);
            std.writeLine(std.pow(2.0, -2));
            std.writeLine(std.pow(2, 10 as uint));
            std.writeLine(std.pow(9.0, 0.5));
            std.writeLine(std.round(std.cbrt(-27.0)));
            std.writeLine(std.hypot(3.0, 4.0));
            std.writeLine(std.approxEqual(0.1 + 0.2, 0.3));
            std.writeLine(std.smoothstep(0.0, 1.0, 0.5));
            std.writeLine(std.factorial(10));
            std.writeLine(std.round(std.sin(std.PI / 6.0) * 1000.0));
            std.writeLine(std.round(std.cos(std.PI / 3.0) * 1000.0));
            std.writeLine(std.round((std.ln(std.E) ?? 0.0) * 1000.0));
            std.writeLine(std.round((std.log10(1000.0) ?? 0.0) * 1000.0));
            std.writeLine(std.floor(-2.5));
            std.writeLine(std.ceil(2.1));
            std.writeLine(std.gcd(12, 18));
            std.writeLine(std.lcm(4, 6));
            std.writeLine(std.unsignedAbs(-5));
            std.writeLine(std.abs(-3));
            std.writeLine(std.clamp(15, 0, 10));
            """));

        Assert.Equal("1.4142135623730951\n0.25\n1024\n3.0\n-3\n5.0\ntrue\n0.5\n3628800\n500\n500\n1000\n3000\n-3\n3\n6\n12\n5\n3\n10\n", output.Replace("1.414213562373095\n", "1.4142135623730951\n"));
    }

    [ZigFact]
    public void Le_hasard_est_determine_par_la_graine()
    {
        var output = Run("""
            use <Random> only {Random, choice, shuffled};

            gear Test(): void {
                mut Random a: Random.seed(42);
                mut Random b: Random.seed(42);
                std.writeLine(a.nextInt(1, 6) eq b.nextInt(1, 6));
                std.writeLine(a.nextDouble() eq b.nextDouble());
                std.writeLine(choice(a, ['a', 'b', 'c']) not eq null);
                std.writeLine(shuffled(a, [1, 2, 3, 4]).sorted().join(','));
            }
            emit Fin(): uint => 0;
            !train |> Test |> Fin;
            """);

        Assert.Equal("true\ntrue\ntrue\n1,2,3,4\n", output);
    }

    [ZigFact]
    public void Le_train_d_entree_accepte_programValue_comme_code_de_sortie()
    {
        Run("""
            use <System> only {programValue};
            gear pure Fail(): programValue => programValue.FAILURE;
            emit Finish(programValue v): programValue => v;
            !train |> Fail |> Finish;
            """, expectedCode: 1);
        Run("""
            use <System> only {programValue};
            !train |> $ programValue.FAILURE;
            """, expectedCode: 1);
    }

    [ZigFact]
    public void Les_erreurs_standard_ont_un_code_par_defaut_et_des_constantes()
    {
        var output = Run("""
            use <Errors> only {NotFound, errorCode};
            use <System> only {programValue};

            gear Test(): void {
                NotFound nf: {message: 'm', where: 'w', item: 'i'};
                std.writeLine(nf.code°);
                std.writeLine(errorCode.NOT_FOUND°);
                std.writeLine(programValue.fromCode(errorCode.NOT_FOUND)°);
            }
            emit Fin(): uint => 0;
            !train |> Test |> Fin;
            """);

        Assert.Equal("0\n404\n255\n", output);
    }

    [ZigFact]
    public void Les_logs_les_vecteurs_et_les_couleurs()
    {
        var output = Run("""
            use <Logs>;
            use <Geometry> only {Vector3, degToRad};
            use <Color> only {Color};

            gear Test(): void {
                Logs.info('bonjour');
                Vector3 a: {x: 1.0, y: 2.0, z: 3.0};
                Vector3 b: a + {x: 1.0, y: 1.0, z: 1.0};
                Color c: {r: 255, g: 0, b: 10};
                std.writeLine(b.x);
                std.writeLine(a.dot(b));
                std.writeLine(std.round(degToRad(180.0) * 1000.0));
                std.writeLine(c.r);
            }
            emit Fin(): uint => 0;
            !train |> Test |> Fin;
            """);

        Assert.Equal("(i) [Test] bonjour\n2.0\n20.0\n3142\n255\n", output);
    }

    [ZigFact]
    public void Une_CastError_porte_le_texte_refuse_et_le_type_vise()
    {
        var output = Run("""
            guard lire(string t): int {
                return t as! int;
            }

            gear dire(CastError e): void {
                std.writeLine(`{e.item}|{e.target°}`);
            }

            emit fin(int v): uint => 3;
            emit echec(): uint => 4;

            train 'abc' |> lire !> [dire |> echec] |> fin;
            """, expectedCode: 4);

        Assert.Equal("abc|int\n", output);
    }

    [Fact]
    public void Un_import_std_choisit_ses_noms()
    {
        var output = Run("use <std> only {writeLine, abs as valeurAbsolue};\n" + Program("writeLine(valeurAbsolue(-7));"));

        Assert.Equal("7\n", output);
    }
}
