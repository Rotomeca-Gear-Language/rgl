using Rgl.Binding;
using Rgl.Diagnostics;
using Rgl.Emit;
using Rgl.Parsing;
using Rgl.Project;
using Rgl.Syntax;
using Rgl.Tests.Runtime;
using Rgl.Text;

namespace Rgl.Tests;

/// <summary>Le format <c>xml</c> : <c>&lt;- xml chemin</c>, <c>noeud -&gt; xml chemin;</c>, <c>xml&lt;train&gt;</c> et <c>import xml</c>.</summary>
public class XmlTests
{
    private const string Header = """
        use <Errors> only {Exception, IoError, ParseError};
        use <Xml> only {XmlNode};
        emit Signaler(Exception | IoError | ParseError e): uint {
            match (e) {
                ParseError p |> { `{p.message}` -> console; }
                IoError i |> { `{i.message}` -> console; }
                Exception x |> { `{x.message}` -> console; }
            }
            return 7;
        }
        gear Afficher(string s): void { s -> console; }

        """;

    /// <summary>Compile et exécute un programme du projet ; <c>%P%</c> est le chemin du fichier de travail, <c>%D%</c> son dossier.</summary>
    private static (int Code, string Output) Run(string main, Action<string>? check = null, string? file = null)
    {
        using var directory = new TempDirectory();
        var work = directory.Path.Replace('\\', '/') + "/travail";
        Directory.CreateDirectory(work);
        if (file is not null) System.IO.File.WriteAllText(work + "/f.xml", file);
        main = main.Replace("%P%", work + "/f.xml").Replace("%D%", work);

        var diagnostics = new DiagnosticBag();
        var source = new SourceText(Path.Combine(directory.Path, "main.rgl"), main);
        var modules = SourceGraph.Load(source, directory.Path, diagnostics);
        var program = modules is null ? null : new Binder(diagnostics).Bind(modules);
        Assert.True(program is not null, "programme invalide : " + string.Join(", ", diagnostics.Items.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Format("."))));

        var cpp = Path.Combine(directory.Path, "main.cpp");
        System.IO.File.WriteAllText(cpp, CppEmitter.Emit(program, "main.rgl").Code);
        var executable = ZigBuild.Compile(directory.Path, "programme", warningsAsErrors: true,
            Path.Combine(ZigBuild.Runtime, "src", "arguments.cpp"), Path.Combine(ZigBuild.Runtime, "src", "console.cpp"),
            Path.Combine(ZigBuild.Runtime, "src", "file.cpp"), Path.Combine(ZigBuild.Runtime, "src", "xml.cpp"), cpp);
        var result = ZigBuild.RunWithInput(executable, null);
        check?.Invoke(work);
        return (result.Code, result.Output.Replace("\r\n", "\n"));
    }

    private const string Lire = """

        guard Lire(string chemin): string {
            XmlNode n <- xml chemin;
            return `{n.name}|{n.attributes['nom']}|{n.children.length}|{n.children[0].text}|{n.children[0].attributes['id']}`;
        }
        train '%P%' |> Lire !> [Signaler] |> Afficher |> done;
        """;

    [ZigFact]
    public void Un_document_se_lit_en_XmlNode()
    {
        var (code, output) = Run(Header + Lire, file: """
            <?xml version="1.0"?>
            <!-- un commentaire -->
            <jeu nom="A &amp; B">
              <joueur id="1">Léa &lt;3</joueur>
              <vide/>
            </jeu>
            """);

        Assert.Equal((0, "jeu|A & B|2|Léa <3|1"), (code, output));
    }

    [ZigFact]
    public void Un_XmlNode_s_ecrit_en_XML_et_se_relit_a_l_identique()
    {
        var (code, output) = Run(Header + """

            guard Aller(string chemin): string {
                XmlNode n: {name: 'a', attributes: ['k': 'v&w', 'z': '"'], children: [{name: 'b', attributes: [:], children: [], text: 'x<y'}, {name: 'c', attributes: [:], children: [], text: ''}], text: ''};
                n -> xml chemin;
                XmlNode relu <- xml chemin;
                return `{relu eq n}`;
            }
            train '%P%' |> Aller !> [Signaler] |> Afficher |> done;
            """, work =>
        {
            var expected = """
                <?xml version="1.0" encoding="UTF-8"?>
                <a k="v&amp;w" z="&quot;">
                  <b>x&lt;y</b>
                  <c/>
                </a>

                """.ReplaceLineEndings("\n");
            Assert.Equal(expected, System.IO.File.ReadAllText(work + "/f.xml").ReplaceLineEndings("\n"));
        });

        Assert.Equal((0, "true"), (code, output));
    }

    [ZigFact]
    public void Un_XML_invalide_un_DOCTYPE_un_fichier_absent_et_un_nom_de_balise_refuse_sont_signales()
    {
        var (code, output) = Run(Header + Lire, file: "<a><b></a>");
        Assert.Equal(7, code);
        Assert.StartsWith("XML invalide, ligne 1, colonne ", output);

        (code, output) = Run(Header + Lire, file: "<!DOCTYPE a [<!ENTITY x \"y\">]><a/>");
        Assert.Equal((7, "XML invalide, ligne 1, colonne 1 : une balise DOCTYPE n'est pas prise en charge"), (code, output));

        (code, output) = Run(Header + Lire.Replace("%P%", "%D%/absent.xml"));
        Assert.Equal((7, "fichier introuvable"), (code, output));

        (code, output) = Run(Header + """

            guard Aller(string chemin): string {
                XmlNode n: {name: 'a b', attributes: [:], children: [], text: ''};
                n -> xml chemin;
                return '';
            }
            train '%P%' |> Aller !> [Signaler] |> Afficher |> done;
            """);
        Assert.Equal((7, "« a b » n'est pas un nom de balise XML"), (code, output));
    }

    [ZigFact]
    public void Xml_entre_chevrons_passe_par_un_train_en_lecture_et_en_ecriture()
    {
        var (code, output) = Run(Header + """

            mold Config { string nom; int niveau; }
            gear LireConfig(XmlNode n): Config => {nom: n.attributes['nom'], niveau: 3};
            gear VersXml(Config c): XmlNode => {name: 'config', attributes: ['nom': c.nom], children: [], text: `{c.niveau}`};
            guard Aller(string chemin): string {
                Config c: {nom: 'Héros', niveau: 7};
                c -> xml<VersXml> chemin;
                XmlNode brut <- xml chemin;
                Config relu <- xml<LireConfig> chemin;
                return `{brut.text}|{relu.nom}|{relu.niveau}`;
            }
            train '%P%' |> Aller !> [Signaler] |> Afficher |> done;
            """);

        Assert.Equal((0, "7|Héros|3"), (code, output));
    }

    [ZigFact]
    public void Un_import_xml_donne_une_constante_XmlNode_lue_a_la_compilation()
    {
        using var directory = new TempDirectory();
        directory.File("main.rgl", """
            import xml layout;
            gear Montrer(): void {
                `{layout.name}|{layout.attributes['titre']}|{layout.children.length}|{layout.children[0].text}|{layout.children[1].attributes['xmlns:x']}|{layout.children[1].name}` -> console;
            }
            emit Fin(): uint => 0;
            !train |> Montrer |> Fin;
            """);
        directory.File("ui/layout.xml", """
            <?xml version="1.0"?>
            <ui titre="Titre &amp; co">
              <bouton><![CDATA[<OK>]]></bouton>
              <x:zone xmlns:x="urn:z"/>
            </ui>
            """);

        var diagnostics = new DiagnosticBag();
        var path = Path.Combine(directory.Path, "main.rgl");
        var modules = SourceGraph.Load(new SourceText(path, System.IO.File.ReadAllText(path)), directory.Path, diagnostics);
        var program = modules is null ? null : new Binder(diagnostics).Bind(modules);
        Assert.True(program is not null, string.Join(", ", diagnostics.Items.Select(d => d.Format("."))));

        var cpp = Path.Combine(directory.Path, "main.cpp");
        System.IO.File.WriteAllText(cpp, CppEmitter.Emit(program, "main.rgl").Code);
        var executable = ZigBuild.Compile(directory.Path, "programme", warningsAsErrors: true, Path.Combine(ZigBuild.Runtime, "src", "console.cpp"), cpp);

        Assert.Equal((0, "ui|Titre & co|2|<OK>|urn:z|x:zone"), ZigBuild.Run(executable));
    }

    /// <summary>Les diagnostics d'un projet d'un seul fichier (avec la std, qui définit <c>XmlNode</c>), sans le compiler en C++.</summary>
    private static string[] Diagnose(string content)
    {
        using var directory = new TempDirectory();
        var path = directory.File("main.rgl", content);
        var diagnostics = new DiagnosticBag();
        var modules = SourceGraph.Load(new SourceText(path, content), directory.Path, diagnostics);
        if (modules is not null) new Binder(diagnostics).Bind(modules);
        return [.. diagnostics.Items.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Code + " " + d.Message)];
    }

    [Theory]
    [InlineData("use <Xml> only {XmlNode};\ngear f(string p): XmlNode {\n    XmlNode n <- xml p;\n    return n;\n}\ntrain |> done;", "E060")]   // lire seulement dans un guard
    [InlineData("guard f(string p): void {\n    int n <- xml p;\n}\ntrain |> done;", "E003")]                                      // une lecture donne un XmlNode
    [InlineData("guard f(string p): void {\n    5 -> xml p;\n}\ntrain |> done;", "E003")]                                           // un nombre n'est pas un XmlNode
    [InlineData("guard f(string p): void {\n    var n <- xml<(int) => int> p;\n}\ntrain |> done;", "E030")]                      // xml<train> veut un nom
    [InlineData("guard f(string p): void {\n    var n <- xml<inconnu> p;\n}\ntrain |> done;", "E035")]
    [InlineData("import xml absent;\n!train |> done;", "E107")]
    public void Un_XML_mal_utilise_est_signale(string content, string code)
    {
        var errors = Diagnose(content);

        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.StartsWith(code, StringComparison.Ordinal));
    }
}
