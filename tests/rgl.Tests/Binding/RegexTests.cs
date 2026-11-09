using System.Text;
using Rgl.Binding;
using Rgl.Diagnostics;
using Rgl.IR;
using Rgl.Parsing;
using Rgl.Syntax;
using Rgl.Text;
using Rgl.Tests.Emit;
using Rgl.Tests.Runtime;

namespace Rgl.Tests.Binding;

/// <summary>Tests des regex (slice 5.4) : le littéral <c>/motif/flags</c>, le type <c>regex</c> et ses méthodes (moteur CTRE).</summary>
public class RegexTests
{
    private static (IrProgram? Program, IReadOnlyList<Diagnostic> Diagnostics) Bind(string content)
    {
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(new SourceText("main.rgl", content), diagnostics).Tokenize();
        var unit = new Parser(tokens, diagnostics).Parse();
        Assert.False(diagnostics.HasErrors, "erreur de syntaxe dans le cas de test : " + string.Join(", ", diagnostics.Items.Select(d => d.Format("."))));
        return (new Binder(diagnostics).Bind(unit), diagnostics.Items);
    }

    private static string[] Errors(string content)
    {
        var (program, diagnostics) = Bind(content);
        Assert.Null(program);
        return [.. diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Code)];
    }

    /// <summary>Un gear qui contient <paramref name="body"/> ; ce qui précède « @@ » se place avant lui, au niveau du fichier.</summary>
    private static string InGear(string body)
    {
        var parts = body.Split("@@");
        return $"{(parts.Length > 1 ? parts[0] + "\n" : "")}gear f(): uint {{\n{parts[^1]}\nreturn 0;\n}}\n!train |> $ f();";
    }

    /// <summary>Des motifs que le moteur compile : le validateur les accepte, et Clang aussi (voir le test de compilation).</summary>
    public static readonly string[] Accepted =
    [
        @"a+", @"^abc$", @"[a-z]+", @"[^a-z0-9_]", @"\d{2,4}-\d+", @"(a|b)*c", @"(?:ab)+", @"(?<annee>\d{4})-(?<mois>\d\d)",
        @"(?=a)a", @"(?!b)a", @"(?<=a)b", @"(?<!a)b", @"a+?", @"a*+", @"(a)\1", @"[[:alpha:]_]", @"[\d\-]", @"\x41é\x{1F600}",
        @"\.\*\+\?\(\)\[\]\{\}\|\^\$\\\/", "é+", @"(|a)", @"(a|)", @"a|b|", @"\bmot\b", @"\A\w+\z", @"x{3}", @"x{3,}", @"x{3,5}?",
        @"(?i)abc", @"(?m)^a", @".", @"\s*\S+", @"[\w.]+@[\w.]+", @"\/", @"[a-f\x30-\x39]", @"(?>a+)b", @"[é-ü]+", @"a{0}", @"((a)(b))\2",
        @"\h+\H\v\V\N", @"[^\n]*", @"\t\r\n\f\a\e\0", @"[a\]]", @"\d+(\.\d+)?", @"[[:^digit:]]",
    ];

    /// <summary>Des motifs refusés avec E127 (jamais une erreur de Clang).</summary>
    public static readonly string[] Refused =
    [
        @"*a", @"a**", @"a{2", @"a{,3}", @"a{3,2}", @"(a", @"a)", @"[a", @"[]", @"[a-]", @"[-a]", @"\p{L}", @"\g1", @"\q", @"\1", @"a||b", @"|a",
        @"(?i:a)", @"{", @"}", @"[z-a]", @"(?P<n>a)", @"\k<n>", @"a{1001}", @"[[:nope:]]", @"[\d-z]", @"\x4", @"\u12", @"(?", @"[\b]", @"a+*", @"(*)",
    ];

    [Fact]
    public void Le_validateur_accepte_les_motifs_du_moteur()
    {
        foreach (var pattern in Accepted)
            Assert.True(RegexSyntax.Check(pattern) is null, $"« {pattern} » refusé : {RegexSyntax.Check(pattern)?.Message}");
    }

    [Fact]
    public void Le_validateur_refuse_ce_que_le_moteur_ne_compile_pas()
    {
        foreach (var pattern in Refused)
            Assert.True(RegexSyntax.Check(pattern) is not null, $"« {pattern} » accepté");
    }

    [ZigFact]
    public void Tout_motif_accepte_se_compile_avec_le_moteur()
    {
        var code = new StringBuilder("#define CTRE_ENABLE_UTF8_RANGE\n#include <ctre.hpp>\nint main() {\n");
        foreach (var pattern in Accepted)
            code.Append("    static_cast<void>(ctre::search<u8R\"rgl(").Append(pattern).Append(")rgl\">);\n");
        code.Append("    return 0;\n}\n");

        using var directory = new TempDirectory();
        var source = Path.Combine(directory.Path, "corpus.cpp");
        File.WriteAllText(source, code.ToString());
        var executable = ZigBuild.Compile(directory.Path, "corpus", warningsAsErrors: true, source);
        Assert.Equal(0, ZigBuild.Run(executable).Code);
    }

    [Theory]
    [InlineData("regex r: /a+/g;\nvar ok: r.test(\"aa\");\nvar groupes: /(\\d+)-(\\d+)/.match(\"1-2\");\nvar tous: /\\w+/gi.find(\"a b\");\nvar texte: r.replace(\"caab\", \"X\");")]
    [InlineData("const regex R: /^[a-z]+$/m;\nregex autre: R;\nautre <- /x/;")]
    [InlineData("mold Config { regex filtre; string nom: \"x\"; }@@Config c: {filtre: /a/};\nvar ok: c.filtre.test(\"a\");\n[regex] liste: [/a/, /b/i];\nvar b: liste[1].test(\"B\");")]
    [InlineData("gear compte(regex r, string s): uint => r.find(s).length;@@var n: compte(/o/g, \"foo\");")]
    public void Les_regex_se_lient(string body)
    {
        var (program, diagnostics) = Bind(InGear(body));
        Assert.True(program is not null, string.Join(", ", diagnostics.Select(d => d.Format("."))));
    }

    [Theory]
    [InlineData("var r: /a+/x;", "E127")]                          // flag inconnu
    [InlineData("var r: /a+/gg;", "E127")]                         // flag en double
    [InlineData("var r: /+a/;", "E127")]                           // motif refusé
    [InlineData("var r: /a/.test(5);", "E003")]                    // le texte cherché est une string
    [InlineData("var r: /a/.test();", "E058")]
    [InlineData("var r: /a/.replace(\"x\");", "E058")]
    [InlineData("bool b: /a/ eq /a/;", "E040")]                     // une regex ne se compare pas
    [InlineData("var t: `{/a/}`;", "E040")]                         // ni ne s'écrit en texte
    [InlineData("regex r: \"a\";", "E003")]
    [InlineData("var n: /a/ as int;", "E085")]
    public void Une_regex_mal_ecrite_est_refusee(string body, string code) =>
        Assert.Contains(code, Errors(InGear(body)));

    [ZigFact]
    public void Les_regex_s_executent()
    {
        var (code, output) = CppEmitterTests.CompileAndRun(
            "gear calcul(): uint {\n"
            + "regex mot: /\\w+/g;\nregex paire: /(\\w+)=(\\d+)?/;\nregex casse: /ABC/i;\nregex lignes: /^b$/m;\n"
            + "var tous: mot.find(\"un deux trois\");\n"
            + "var premier: /\\w+/.find(\"un deux\");\n"
            + "var m: paire.match(\"x=12 y=\");\n"
            + "var rien: paire.match(\"???\");\n"
            + "var t: `{tous.length}|{premier.length}|{mot.replace(\"un deux\", \"<$0>\")}|{casse.test(\"xxabcxx\")}|{lignes.test(\"a\\nb\\nc\")}|{rien eq null}|{(m ?? [\"-\"])[1]}|{(m ?? [\"-\"]).length}`;\n"
            + "t -> console;\nreturn 7;\n}\n!train |> $ calcul();");

        Assert.Equal(7, code);
        Assert.Equal("3|1|<un> <deux>|true|true|true|x|3", output);
    }

    [ZigFact]
    public void Une_regex_est_une_valeur_reaffectable_et_unicode()
    {
        var (code, output) = CppEmitterTests.CompileAndRun(
            "gear calcul(): uint {\n"
            + "regex r: /a/;\nr <- /é./g;\n"
            + "var l: r.find(\"été éï\");\n"
            + "var t: `{l.length}|{l[0]}|{l[2]}|{/\\d+/.replace(\"a1b22\", \"#\")}|{/(\\w)(\\w)/g.replace(\"abcd\", \"$2$1\")}`;\n"
            + "t -> console;\nreturn 3;\n}\n!train |> $ calcul();");

        Assert.Equal(3, code);
        Assert.Equal("3|ét|éï|a#b22|badc", output);
    }
}
