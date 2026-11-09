using System.Text.Json;
using System.Xml.Linq;
using Rgl.Cli;
using Rgl.Diagnostics;
using Rgl.Docs;
using Rgl.Lsp;
using Rgl.Parsing;
using Rgl.Syntax;
using Rgl.Text;

namespace Rgl.Tests.Docs;

/// <summary>
/// La documentation <c>///</c> : lecture (résumé implicite, balises XML), association aux déclarations, vérifications
/// <c>DOCxxx</c>, référence de l'API, commande <c>rgl doc</c> et ce que l'éditeur en montre.
/// </summary>
public class DocumentationTests
{
    private const string Header = "forge: gears\ndescription: \"o\"\n\n";

    private static (IReadOnlyList<DocEntry> Entries, DiagnosticBag Diagnostics) Collect(string content, string path = "Outils.forge")
    {
        var diagnostics = new DiagnosticBag();
        var source = new SourceText(path, Header + content);
        var tokens = new Lexer(source, new DiagnosticBag()).Tokenize();
        var unit = new Parser(tokens, new DiagnosticBag()).Parse();
        var entries = DocCollector.Collect("Outils", unit, tokens.Tokens, diagnostics);
        DocValidator.Validate(entries, diagnostics);
        return (entries, diagnostics);
    }

    private static DocEntry Entry(IReadOnlyList<DocEntry> entries, string name) => entries.Single(e => e.Name == name);

    private static string[] Codes(DiagnosticBag diagnostics) => [.. diagnostics.Items.Select(d => d.Code ?? "")];

    [Fact]
    public void Les_sections_personnalisees_s_affichent_et_s_exportent_sans_perdre_leurs_attributs()
    {
        var (entries, diagnostics) = Collect("/// Résumé.\n/// <attention niveau=\"fort\">Utiliser <c>f</c>.</attention>\ngear f(): int => 1;");
        Assert.Empty(diagnostics.Items);
        var comment = Entry(entries, "f").Comment!;
        Assert.Equal("Utiliser `f`.", Assert.Single(comment.Custom).Text.Markdown);
        Assert.Contains("**attention**", DocRenderer.Hover(comment));
        var xml = XDocument.Parse(DocExporter.Xml("test", entries));
        Assert.Equal("fort", xml.Descendants("attention").Single().Attribute("niveau")!.Value);
        using var json = JsonDocument.Parse(DocExporter.Json("test", entries));
        Assert.Equal("attention", json.RootElement.GetProperty("modules")[0].GetProperty("entries")[0]
            .GetProperty("doc").GetProperty("custom")[0].GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("fr-FR", "Français")]
    [InlineData("fr-CA", "Français")]
    [InlineData("de-DE", "English")]
    public void Les_langues_selectionnent_la_culture_puis_l_anglais(string culture, string expected)
    {
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new(culture);
            var (entries, diagnostics) = Collect("""
                /// <lang name="fr"><summary>Français</summary><param name="x">Nombre</param><returns>Résultat</returns></lang>
                /// <lang name="en"><summary>English</summary><param name="x">Number</param><returns>Result</returns></lang>
                gear f(int x): int => x;
                """);
            Assert.Empty(diagnostics.Items);
            var comment = Entry(entries, "f").Comment!;
            Assert.Equal(expected, comment.Summary!.Markdown);
            Assert.Equal(expected == "Français" ? "Nombre" : "Number", DocRenderer.ParameterText(comment, "x"));
            Assert.Equal(expected == "Français" ? "Résultat" : "Result", comment.Returns!.Markdown);
            Assert.Equal(2, XDocument.Parse(DocExporter.Xml("test", entries)).Descendants("lang").Count());
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = previous; }
    }

    [Fact]
    public void Une_langue_inconnue_utilise_le_premier_bloc_complet()
    {
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new("de-DE");
            var (entries, diagnostics) = Collect("""
                /// <lang name="fr"><summary>Bonjour</summary><attention>Prudence</attention><returns>Nombre</returns></lang>
                /// <lang name="es"><summary>Hola</summary><attention>Cuidado</attention><returns>Número</returns></lang>
                gear f(): int => 1;
                """);
            Assert.Empty(diagnostics.Items);
            var comment = Entry(entries, "f").Comment!;
            Assert.Equal("Bonjour", comment.Summary!.Markdown);
            Assert.Equal("Prudence", Assert.Single(comment.Custom).Text.Markdown);
            Assert.Equal("Nombre", comment.Returns!.Markdown);
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = previous; }
    }

    [Fact]
    public void Un_bloc_de_langue_accepte_un_resume_implicite_avec_mise_en_forme()
    {
        var (entries, diagnostics) = Collect("""
            /// <lang name="en">A <c>number</c>.
            /// <returns>The result.</returns></lang>
            gear f(): int => 1;
            """);
        Assert.Empty(diagnostics.Items);
        var comment = Entry(entries, "f").Comment!;
        Assert.Equal("A `number`.", comment.Summary!.Markdown);
        Assert.Equal("The result.", comment.Returns!.Markdown);
    }

    [Fact]
    public void La_correspondance_exacte_precede_le_parent_et_ignore_la_casse()
    {
        var xml = XElement.Parse("<doc><lang name=\"fr\"/><lang name=\"FR-ca\"/><lang name=\"en\"/></doc>");
        Assert.Same(xml.Elements().ElementAt(1), DocLanguage.Select(xml.Elements(), new("fr-CA")));
    }

    [Fact]
    public void Une_langue_sans_nom_est_signalee()
    {
        var (_, diagnostics) = Collect("/// <lang><summary>Texte</summary></lang>\ngear f(): int => 1;");
        Assert.Contains(DocCodes.MissingAttribute, Codes(diagnostics));
    }

    [Theory]
    [InlineData("<summary><lang name=\"fr\">Bonjour</lang></summary>")]
    [InlineData("<param name=\"x\"><lang name=\"fr\">Nombre</lang></param>")]
    [InlineData("<returns><lang name=\"fr\">Résultat</lang></returns>")]
    [InlineData("<lang name=\"fr\"><lang name=\"en\"><summary>Hello</summary></lang></lang>")]
    public void Lang_doit_englober_les_sections_et_ne_peut_pas_etre_imbrique(string xml)
    {
        var (_, diagnostics) = Collect("/// " + xml + "\ngear f(int x): int => x;");
        Assert.Contains(DocCodes.LanguagePlacement, Codes(diagnostics));
    }

    #region Lexer

    [Fact]
    public void Trois_barres_sont_un_commentaire_documentaire_quatre_un_commentaire_ordinaire()
    {
        var tokens = new Lexer(new SourceText("main.rgl", "/// doc\n//// pas doc\n// non plus\nvar x: 1;"), new DiagnosticBag()).Tokenize().Tokens;

        Assert.Equal([TriviaKind.DocComment, TriviaKind.EndOfLine, TriviaKind.SingleComment, TriviaKind.EndOfLine, TriviaKind.SingleComment, TriviaKind.EndOfLine],
            tokens[0].Before.Select(t => t.Kind));
    }

    #endregion

    #region Résumé

    [Fact]
    public void Un_texte_seul_est_le_resume_comme_la_balise_summary()
    {
        var (implicitEntries, _) = Collect("/// Additionne deux nombres.\ngear pure add(int a, int b): int => a + b;");
        var (explicitEntries, _) = Collect("/// <summary>Additionne deux nombres.</summary>\ngear pure add(int a, int b): int => a + b;");

        var implicitComment = Entry(implicitEntries, "add").Comment!;
        var explicitComment = Entry(explicitEntries, "add").Comment!;
        Assert.Equal("Additionne deux nombres.", implicitComment.Summary!.Markdown);
        Assert.Equal(explicitComment.Summary!.Markdown, implicitComment.Summary.Markdown);
        Assert.True(implicitComment.HasImplicitSummary);
        Assert.False(explicitComment.HasImplicitSummary);
    }

    [Fact]
    public void Le_resume_implicite_peut_tenir_sur_plusieurs_lignes_et_s_arrete_a_la_premiere_balise()
    {
        var (entries, diagnostics) = Collect("""
            /// Retourne le premier élément du tableau.
            /// Si le tableau est vide, retourne null.
            ///
            /// <returns>Le premier élément ou null.</returns>
            gear pure first(int a): int => a;
            """);

        var comment = Entry(entries, "first").Comment!;
        Assert.Equal("Retourne le premier élément du tableau.\nSi le tableau est vide, retourne null.", comment.Summary!.Markdown);
        Assert.Equal("Le premier élément ou null.", comment.Returns!.Markdown);
        Assert.Empty(diagnostics.Items);
    }

    [Fact]
    public void Une_documentation_qui_commence_par_une_balise_n_a_pas_de_resume_implicite()
    {
        var (entries, _) = Collect("/// <returns>Le double.</returns>\ngear double(int a): int => a * 2;");

        var comment = Entry(entries, "double").Comment!;
        Assert.Null(comment.Summary);
        Assert.False(comment.HasImplicitSummary);
        Assert.Equal("Le double.", comment.Returns!.Markdown);
    }

    [Fact]
    public void Un_resume_implicite_suivi_d_un_summary_est_signale()
    {
        var (_, diagnostics) = Collect("/// Un texte.\n/// <summary>Un autre.</summary>\ngear f(): int => 1;");

        Assert.Equal([DocCodes.Duplicate], Codes(diagnostics));
    }

    [Fact]
    public void Le_texte_libre_n_a_pas_besoin_d_echapper_les_signes_du_xml()
    {
        var (entries, diagnostics) = Collect("/// Vrai si a < b & b > 0, ou un Pair<int> : <c>a</c> en code.\ngear f(): int => 1;");

        Assert.Empty(diagnostics.Items);
        Assert.Equal("Vrai si a < b & b > 0, ou un Pair<int> : `a` en code.", Entry(entries, "f").Comment!.Summary!.Markdown);
    }

    [Fact]
    public void Un_xml_invalide_est_signale_et_le_texte_brut_sert_de_resume()
    {
        var (entries, diagnostics) = Collect("/// <summary>Pas fermé.\n/// <param name=\"a\">x</param>\ngear f(int a): int => a;");

        Assert.Contains(DocCodes.InvalidXml, Codes(diagnostics));
        var comment = Entry(entries, "f").Comment!;
        Assert.False(comment.IsValid);
        Assert.Contains("Pas fermé.", comment.Summary!.Markdown);
    }

    [Fact]
    public void Une_dtd_est_refusee()
    {
        var (_, diagnostics) = Collect("/// <!DOCTYPE doc [<!ENTITY x \"y\">]>\n/// <summary>&x;</summary>\ngear f(): int => 1;");

        Assert.Contains(DocCodes.InvalidXml, Codes(diagnostics));
    }

    [Fact]
    public void Le_diagnostic_d_une_balise_pointe_son_texte_dans_le_source()
    {
        var (_, diagnostics) = Collect("/// Un gear.\n/// <param name=\"zzz\">inconnu</param>\ngear f(int a): int => a;");

        var diagnostic = Assert.Single(diagnostics.Items, d => d.Code == DocCodes.UnknownParam);
        Assert.Equal("<param name=\"zzz\">", diagnostic.Span!.Value.Text);
    }

    [Fact]
    public void La_mise_en_forme_donne_du_markdown()
    {
        var (entries, _) = Collect("""
            /// Un gear.
            /// <remarks>
            /// <para>Premier paragraphe avec <c>code</c> et <see cref="f"/>.</para>
            /// <list type="bullet"><item>un</item><item><term>deux</term><description>second</description></item></list>
            /// </remarks>
            /// <example>
            /// <code language="rgl">
            ///     f();
            ///     g();
            /// </code>
            /// </example>
            gear f(): int => 1;
            """);

        var comment = Entry(entries, "f").Comment!;
        Assert.Equal("Premier paragraphe avec `code` et `f`.\n\n- un\n- **deux** — second", comment.Remarks!.Markdown);
        Assert.Equal("```rgl\nf();\ng();\n```", comment.Examples[0].Markdown);
    }

    #endregion

    #region Association aux déclarations

    [Fact]
    public void Une_ligne_vide_ou_un_autre_commentaire_detache_la_documentation()
    {
        var (entries, _) = Collect("/// Orpheline.\n\ngear a(): int => 1;\n\n/// Documentée.\n// ordinaire\ngear b(): int => 1;\n\n/// Bien placée.\ngear c(): int => 1;");

        Assert.Null(Entry(entries, "a").Comment);
        Assert.Null(Entry(entries, "b").Comment);
        Assert.Equal("Bien placée.", Entry(entries, "c").Comment!.Summary!.Markdown);
    }

    [Fact]
    public void Les_champs_les_variantes_et_les_membres_d_extension_ont_leur_propre_documentation()
    {
        var (entries, _) = Collect("""
            /// Un vecteur.
            mold Vec2 {
                /// Abscisse.
                float x;
                float y;
            }

            enum Team {
                /// Les rouges.
                Red,
                Blue
            }

            extend Vec2 {
                /// Longueur.
                gear pure length(this Vec2 v): float => v.x;
            }
            """);

        Assert.Equal("Abscisse.", entries.Single(e => e.Id == "Outils:Vec2.x").Comment!.Summary!.Markdown);
        Assert.Null(entries.Single(e => e.Id == "Outils:Vec2.y").Comment);
        Assert.Equal("Les rouges.", entries.Single(e => e.Id == "Outils:Team.Red").Comment!.Summary!.Markdown);
        Assert.Equal("Longueur.", entries.Single(e => e.Id == "Outils:Vec2.length").Comment!.Summary!.Markdown);
        Assert.Equal("gear pure length(this Vec2 v): float", entries.Single(e => e.Id == "Outils:Vec2.length").Signature);
        Assert.Equal("mold Vec2", entries.Single(e => e.Id == "Outils:Vec2").Signature);
    }

    [Fact]
    public void Un_commentaire_avant_export_documente_la_declaration_exportee()
    {
        var (entries, _) = Collect("/// Exportée.\nexport gear visible(): int => 1;\n\ngear cache(): int => 2;");

        Assert.Equal("Exportée.", Entry(entries, "visible").Comment!.Summary!.Markdown);
        Assert.True(Entry(entries, "visible").IsPublic);
        Assert.False(Entry(entries, "cache").IsPublic);
    }

    #endregion

    #region Vérifications

    [Fact]
    public void Un_param_qui_n_existe_pas_est_un_avertissement_et_un_param_oublie_une_notice()
    {
        var (_, diagnostics) = Collect("/// Un gear.\n/// <param name=\"a\">premier</param>\n/// <param name=\"z\">inconnu</param>\ngear f(int a, int b): int => a;");

        Assert.Equal([DocCodes.UnknownParam, DocCodes.UndocumentedParam], Codes(diagnostics));
        Assert.Equal(DiagnosticLevel.Warning, diagnostics.Items[0].Level);
        Assert.Equal(DiagnosticLevel.Info, diagnostics.Items[1].Level);
        Assert.Contains("« b »", diagnostics.Items[1].Message);
    }

    [Fact]
    public void Ne_decrire_aucun_parametre_est_un_choix()
    {
        var (_, diagnostics) = Collect("/// Un gear.\ngear f(int a, int b): int => a;");

        Assert.Empty(diagnostics.Items);
    }

    [Fact]
    public void Un_typeparam_inconnu_est_signale()
    {
        var (_, diagnostics) = Collect("/// Un gear.\n/// <typeparam name=\"U\">inconnu</typeparam>\ngear f<T>(T a): T => a;");

        Assert.Contains(DocCodes.UnknownTypeParam, Codes(diagnostics));
    }

    [Fact]
    public void Returns_sur_un_gear_void_est_une_notice_et_return_une_balise_inconnue()
    {
        var (_, diagnostics) = Collect("/// A.\n/// <returns>Rien.</returns>\ngear a(): void {}\n\n/// B.\n/// <return>Faute.</return>\ngear b(): int => 1;");

        // La lecture des commentaires signale avant la vérification des signatures.
        Assert.Equal([DocCodes.Returns, DocCodes.UnknownTag], Codes(diagnostics).Order());
        Assert.All(diagnostics.Items, d => Assert.Equal(DiagnosticLevel.Info, d.Level));
        Assert.Contains("<returns>", diagnostics.Items.Single(d => d.Code == DocCodes.UnknownTag).Help![0]);
    }

    [Fact]
    public void Un_cref_se_resout_sur_les_gears_les_types_et_les_membres()
    {
        var (_, diagnostics) = Collect("""
            mold Vec2 { float x; }

            /// Voir <see cref="Vec2"/>, <see cref="Vec2.x"/>, <see cref="autre"/>, <see cref="Outils.autre"/>, <see cref="int"/>, <see cref="a"/> et <see cref="introuvable"/>.
            /// <seealso cref="inconnu"/>
            gear f(int a): int => a;
            gear autre(): int => 1;
            """);

        Assert.Equal(["introuvable", "inconnu"], diagnostics.Items.Where(d => d.Code == DocCodes.Cref).Select(d => d.Message.Split('«', '»')[1].Trim()));
    }

    [Fact]
    public void Fails_se_verifie_sur_un_guard_et_son_type_d_erreur()
    {
        var (_, diagnostics) = Collect("""
            /// Introuvable.
            error NotFound {
                /// Le chemin.
                string path;
            }

            /// Un gear.
            /// <fails type="NotFound">Jamais.</fails>
            gear a(): int => 1;

            /// Un guard.
            /// <fails type="NotFound">Introuvable.</fails>
            /// <fails type="Fantome">Inconnu.</fails>
            guard b(int v): int { done; }
            """);

        Assert.Equal([DocCodes.Fails, DocCodes.Fails], Codes(diagnostics));
        Assert.Contains("guard", diagnostics.Items[0].Message);
        Assert.Contains("Fantome", diagnostics.Items[1].Message);
    }

    [Fact]
    public void Un_nom_public_sans_resume_est_une_notice_sauf_s_il_n_est_pas_exporte()
    {
        var (_, diagnostics) = Collect("/// <remarks>Sans résumé.</remarks>\nexport gear a(): int => 1;\n\nexport gear b(): int => 1;\n\ngear prive(): int => 1;");

        Assert.Equal([DocCodes.MissingSummary, DocCodes.MissingSummary], Codes(diagnostics));
        Assert.Equal(["a", "b"], diagnostics.Items.Select(d => d.Span!.Value.Text));
    }

    [Fact]
    public void Un_fichier_sans_export_expose_tout_son_premier_niveau()
    {
        var (entries, diagnostics) = Collect("gear a(): int => 1;");

        Assert.True(Entry(entries, "a").IsPublic);
        Assert.Equal([DocCodes.MissingSummary], Codes(diagnostics));
    }

    #endregion

    #region Référence de l'API

    [Fact]
    public void La_reference_json_est_versionnee_et_decrit_les_declarations()
    {
        var (entries, _) = Collect("""
            /// Additionne.
            /// <param name="a">Le premier.</param>
            /// <returns>La somme.</returns>
            /// <see cref="a"/>
            gear add(int a, int b): int => a + b;
            """);

        using var json = JsonDocument.Parse(DocExporter.Json("projet", entries));
        Assert.Equal(DocExporter.FormatVersion, json.RootElement.GetProperty("version").GetInt32());
        var add = json.RootElement.GetProperty("modules")[0].GetProperty("entries")[0];
        Assert.Equal("Outils:add", add.GetProperty("id").GetString());
        Assert.Equal("gear", add.GetProperty("kind").GetString());
        Assert.Equal("gear add(int a, int b): int", add.GetProperty("signature").GetString());
        var doc = add.GetProperty("doc");
        Assert.Equal("Additionne.", doc.GetProperty("summary").GetString());
        Assert.Equal("a", doc.GetProperty("params")[0].GetProperty("name").GetString());
        Assert.Equal("La somme.", doc.GetProperty("returns").GetString());
    }

    [Fact]
    public void La_reference_xml_garde_les_balises_ecrites()
    {
        var (entries, _) = Collect("/// Additionne <c>a</c> et b.\n/// <param name=\"a\">Le premier.</param>\ngear add(int a, int b): int => a + b;");

        var member = XDocument.Parse(DocExporter.Xml("projet", entries)).Descendants("member").Single(m => (string?)m.Attribute("name") == "add");
        Assert.Equal("Outils:add", (string?)member.Attribute("id"));
        Assert.Equal("Additionne <c>a</c> et b.", string.Concat(member.Element("summary")!.Nodes()).Trim());
        Assert.Equal("a", (string?)member.Element("param")!.Attribute("name"));
    }

    #endregion

    #region Commande rgl doc

    private static (int ExitCode, string Out, string Error) Run(string directory, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exitCode = CliRunner.Run(args, new CliContext(output, error, directory, directory));
        return (exitCode, output.ToString(), error.ToString());
    }

    private static TempDirectory Project(string forge)
    {
        var directory = new TempDirectory();
        directory.File("rgl.json", "{ \"name\": \"p\", \"version\": \"1.0.0\", \"rgl\": \"0\" }");
        directory.File("Outils.forge", Header + forge);
        directory.File("main.rgl", "import Outils;\n!train |> done;");
        return directory;
    }

    [Fact]
    public void Doc_check_compte_les_elements_et_signale_les_avertissements_sans_echouer()
    {
        using var project = Project("/// Un gear.\n/// <param name=\"zzz\">inconnu</param>\ngear f(int a): int => a;");

        var (exitCode, output, error) = Run(project.Path, "doc", "check", "-d", project.Path);

        Assert.Equal(CliRunner.Success, exitCode);
        Assert.Contains(DocCodes.UnknownParam, error);
        Assert.Contains("1 élément(s) documenté(s) sur 2, 1 avertissement(s)", output);
    }

    [Fact]
    public void Doc_check_strict_echoue_sur_un_avertissement()
    {
        using var project = Project("/// Un gear.\n/// <param name=\"zzz\">inconnu</param>\ngear f(int a): int => a;");

        Assert.Equal(CliRunner.Failure, Run(project.Path, "doc", "check", "-d", project.Path, "--strict").ExitCode);
    }

    [Fact]
    public void Doc_check_strict_reussit_sur_une_documentation_juste()
    {
        using var project = Project("/// Un gear.\n/// <param name=\"a\">Le nombre.</param>\ngear f(int a): int => a;");

        var (exitCode, _, error) = Run(project.Path, "doc", "check", "-d", project.Path, "--strict");

        Assert.Equal(CliRunner.Success, exitCode);
        Assert.DoesNotContain("DOC", error);
    }

    [Fact]
    public void Doc_build_ecrit_api_json_et_api_xml_dans_out_docs()
    {
        using var project = Project("/// Un gear.\ngear f(int a): int => a;");

        var (exitCode, output, _) = Run(project.Path, "doc", "build", "-d", project.Path);

        Assert.Equal(CliRunner.Success, exitCode);
        Assert.Contains("api.json", output);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(project.Path, "out", "docs", "api.json")));
        Assert.Contains(json.RootElement.GetProperty("modules").EnumerateArray(), m => m.GetProperty("name").GetString() == "Outils");
        Assert.DoesNotContain(json.RootElement.GetProperty("modules").EnumerateArray(), m => m.GetProperty("name").GetString() == "Math");
        XDocument.Load(Path.Combine(project.Path, "out", "docs", "api.xml"));
    }

    [Fact]
    public void Une_documentation_fausse_ne_fait_pas_echouer_la_compilation()
    {
        using var project = Project("/// <summary>Pas fermé.\n/// <param name=\"zzz\">x</param>\ngear f(int a): int => a;");

        var (exitCode, _, error) = Run(project.Path, "compile", "-d", project.Path, "--emit-cpp");

        Assert.Equal(CliRunner.Success, exitCode);
        Assert.DoesNotContain("DOC", error);
    }

    [Fact]
    public void Doc_demande_une_action()
    {
        using var directory = new TempDirectory();

        var (exitCode, _, error) = Run(directory.Path, "doc");

        Assert.Equal(CliRunner.UsageError, exitCode);
        Assert.Contains("build", error);
    }

    #endregion

    #region Éditeur

    private static (SemanticDocument Document, int Offset, TempDirectory Directory) Open(string forge, string main)
    {
        var directory = new TempDirectory();
        directory.File("rgl.json", "{ \"name\": \"p\", \"version\": \"1.0.0\", \"rgl\": \"0\" }");
        directory.File("Outils.forge", Header + forge);
        var cursor = System.Text.RegularExpressions.Regex.Match(main, @"\|(?!>)");
        var text = main.Remove(cursor.Index, 1);
        var path = directory.File("main.rgl", text);
        return (SemanticDocument.Create(path, text, new ProjectIndex().Open(path, _ => null)), cursor.Index, directory);
    }

    private const string Documented = """
        /// Limite une valeur.
        ///
        /// <param name="v">La valeur.</param>
        /// <param name="lo">La borne basse.</param>
        /// <returns>La valeur ramenée.</returns>
        gear clamp(int v, int lo): int => v;
        """;

    [Fact]
    public void Le_survol_montre_le_resume_les_parametres_et_le_resultat()
    {
        var (document, offset, directory) = Open(Documented, "import Outils only {clamp};\ngear g(): int => cla|mp(1, 2);\n!train |> done;");
        using (directory)
        {
            var hover = HoverProvider.At(document, offset)!.Markdown;

            Assert.Contains("gear clamp(int v, int lo): int", hover);
            Assert.Contains("Limite une valeur.", hover);
            Assert.Contains("**Paramètres**\n- `v` — La valeur.\n- `lo` — La borne basse.", hover);
            Assert.Contains("**Retourne** La valeur ramenée.", hover);
            Assert.DoesNotContain("<param", hover);
        }
    }

    [Fact]
    public void L_aide_a_la_saisie_decrit_le_parametre_en_cours()
    {
        var (document, offset, directory) = Open(Documented, "import Outils only {clamp};\ngear g(): int => clamp(1, |\n!train |> done;");
        using (directory)
        {
            var signature = SignatureHelpProvider.At(document, offset)!;

            Assert.Equal(["La valeur.", "La borne basse."], signature.ParameterDocumentation);
            Assert.Equal(1, signature.Active);
        }
    }

    [Fact]
    public void Les_commentaires_ordinaires_restent_montres_au_survol()
    {
        var (document, offset, directory) = Open("// Un simple commentaire.\ngear f(): int => 1;", "import Outils only {f};\ngear g(): int => |f();\n!train |> done;");
        using (directory)
            Assert.Contains("Un simple commentaire.", HoverProvider.At(document, offset)!.Markdown);
    }

    #endregion

    #region Squelette

    private static IReadOnlyList<CompletionItem> Skeleton(string content, uint line, uint character) => CompletionProvider.Complete(content, line, character);

    [Fact]
    public void Trois_barres_au_dessus_d_un_gear_proposent_le_squelette_de_ses_balises()
    {
        var items = Skeleton("///\ngear f<T>(T a, int b): T => a;\n!train |> done;", 0, 3);

        var item = Assert.Single(items);
        Assert.Equal(" ${1:Résumé.}\n///\n/// <typeparam name=\"T\">$2</typeparam>\n/// <param name=\"a\">$3</param>\n/// <param name=\"b\">$4</param>\n/// <returns>$5</returns>", item.InsertText);
    }

    [Fact]
    public void Le_squelette_d_un_guard_propose_un_echec_et_celui_d_un_gear_void_aucun_resultat()
    {
        var guard = Assert.Single(Skeleton("///\nguard g(int v): int { done; }\n!train |> done;", 0, 3));
        Assert.Contains("<fails type=\"${4:Erreur}\">$5</fails>", guard.InsertText);

        var none = Assert.Single(Skeleton("///\ngear h(): void {}\n!train |> done;", 0, 3));
        Assert.Equal(" ${1:Résumé.}", none.InsertText);
    }

    [Fact]
    public void Le_squelette_d_un_champ_est_son_resume()
    {
        var items = Skeleton("mold M {\n    ///\n    int x;\n}\n!train |> done;", 1, 7);

        Assert.Equal(" ${1:Résumé.}", Assert.Single(items).InsertText);
    }

    [Fact]
    public void Le_squelette_n_est_propose_ni_sur_une_division_ni_sur_une_documentation_deja_commencee()
    {
        Assert.Empty(Skeleton("gear f(int a): int => a /\n!train |> done;", 0, 25));
        Assert.Empty(Skeleton("/// Déjà là.\n///\ngear f(): int => 1;\n!train |> done;", 1, 3));
        Assert.Empty(Skeleton("///\n\nvar x: 1;\n!train |> done;", 0, 3));
    }

    #endregion

    #region Std

    /// <summary>Les sources de la std du dépôt, lues sous un chemin qui n'est pas celui de la std installée : ses diagnostics ne sont alors pas masqués.</summary>
    private static (List<DocEntry> Entries, DiagnosticBag Diagnostics) StdDocumentation([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "std"));
        var diagnostics = new DiagnosticBag();
        var entries = new List<DocEntry>();
        foreach (var file in Directory.EnumerateFiles(root, "*.forge", SearchOption.AllDirectories).Order())
        {
            var source = new SourceText("std-" + Path.GetFileName(file), File.ReadAllText(file));
            var tokens = new Lexer(source, new DiagnosticBag()).Tokenize();
            var unit = new Parser(tokens, new DiagnosticBag()).Parse();
            entries.AddRange(DocCollector.Collect(Path.GetFileNameWithoutExtension(file), unit, tokens.Tokens, diagnostics));
        }
        DocValidator.Validate(entries, diagnostics);
        return (entries, diagnostics);
    }

    [Theory]
    [InlineData("fr-FR")]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public void La_documentation_de_la_std_est_sans_avertissement_ni_notice(string culture)
    {
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new(culture);
            var (entries, diagnostics) = StdDocumentation();
            Assert.True(diagnostics.Items.Count == 0, string.Join(Environment.NewLine, diagnostics.Items.Select(d => d.Format(Directory.GetCurrentDirectory()))));
            Assert.NotEmpty(entries);
            foreach (var entry in entries.Where(e => e.Comment is not null))
            {
                var comment = entry.Comment!;
                Assert.False(string.IsNullOrWhiteSpace(comment.Summary?.Markdown), entry.Id);
                Assert.NotNull(comment.LocalizedXml);
                var xml = XElement.Parse("<doc>" + comment.LocalizedXml + "</doc>");
                Assert.Equal(["fr", "en"], xml.Elements().Select(e => (string?)e.Attribute("name")));
                var fr = xml.Elements().First();
                var en = xml.Elements().Last();
                static IEnumerable<string> Structure(XElement element) => element.Descendants().Select(e =>
                    e.Name + " " + string.Join(" ", e.Attributes().OrderBy(a => a.Name.ToString()).Select(a => a.Name + "=" + a.Value)));
                Assert.Equal(Structure(fr), Structure(en));
                Assert.Equal(fr.Descendants("code").Select(e => e.Value), en.Descendants("code").Select(e => e.Value));
            }
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = previous; }
    }

    [Theory]
    [InlineData("fr-FR", "Écrit la valeur sur la console, puis passe à la ligne.", "La valeur à écrire.")]
    [InlineData("en-US", "Writes a value to the console followed by a newline.", "The value to write.")]
    [InlineData("de-DE", "Writes a value to the console followed by a newline.", "The value to write.")]
    public void La_std_embarquee_affiche_la_traduction_au_survol_et_dans_l_aide_aux_parametres(string culture, string summary, string parameter)
    {
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new(culture);
            var (document, offset, directory) = Open("", "gear g(): void { std.write|Line(1); }\n!train |> done;");
            using (directory)
            {
                var hover = HoverProvider.At(document, offset)!.Markdown;
                Assert.Contains(summary, hover);
                Assert.Contains(parameter, hover);
                Assert.DoesNotContain("<lang", hover);
            }
            var (call, position, callDirectory) = Open("", "gear g(): void { std.writeLine(|1); }\n!train |> done;");
            using (callDirectory)
                Assert.Equal(parameter, Assert.Single(SignatureHelpProvider.At(call, position)!.ParameterDocumentation!));
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = previous; }
    }

    #endregion
}
