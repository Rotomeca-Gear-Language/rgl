using Rgl.Lsp;

namespace Rgl.Tests.Lsp;

/// <summary>Le formatage du code : l'indentation vient de la structure, rien de ce qui est écrit ne change.</summary>
public class FormatterTests
{
    /// <summary>Le style de l'utilisateur (format.forge du design system), moins ses deux lignes d'alignement de rails et sa double ligne vide.</summary>
    private const string Styled = """
        forge: gears
        description: ''

        gear nom(int n): int {
            var a: 0;
            int b: 5;
            a: int <- b;

            switch ('test') {
                case 'bjr':
                    break;

                case 'a':
                case 'b':
                    goto 'bjr';

                case eval $ eq 'bjr':
                    return 5;

                default:
                    return 0;
            }

            if (a eq 5) { oneline(); }
            if (
                a eq 5 and
                b eq 7 and
                (8 not eq 5) or
                a eq 6
            ) {
                /*
                multiline
                */
            }

            match (valeur) {
                ok v |> v;
                err e |> 0;
            }

            var tmp: {a: 5, b: 'yolo'};
            var bcp: {
                a: 5,
                b: 5,
                c: 5,
                d: {
                    a: 'yolo'
                }
            };

            var arr: [0, 1, 5];

            return 1;
        }

        const int DEFAULT_VALUE: 0;
        mold Nom {
            int champ: DEFAULT_VALUE;
        }

        train |> a |> bguard(5) !> [c |> dguard !> [c]
                |> x()]
            |> done;

        """;

    private static SemanticDocument Open(string text, string path = "format.forge") => SemanticDocument.Create(path, text, new ProjectContext("", "", []));

    /// <summary>Formate le texte, comme l'éditeur applique le changement.</summary>
    private static string Format(string text, int? first = null, int? last = null)
    {
        var edit = Formatter.Format(Open(text), first, last);
        if (edit is null) return text;

        var eol = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var endsWithEol = text.EndsWith('\n');
        if (endsWithEol) lines.RemoveAt(lines.Count - 1);
        var keptBefore = lines.Take(edit.FirstLine);
        var keptAfter = lines.Skip(edit.LastLine + 1);
        var middle = edit.Text.EndsWith(eol) && edit.LastLine == lines.Count - 1 ? edit.Text[..^eol.Length] : edit.Text.TrimEnd('\r', '\n');
        var all = keptBefore.Concat(middle.Length == 0 && edit.Text.Length == 0 ? [] : middle.Split(eol)).Concat(keptAfter);
        return string.Join(eol, all) + (endsWithEol || edit.LastLine == lines.Count - 1 ? eol : "");
    }

    private static string Flatten(string text) => string.Join("\n", text.Split('\n').Select(l => l.TrimStart()));

    [Fact]
    public void Le_style_de_l_utilisateur_se_retrouve_depuis_un_texte_sans_indentation()
    {
        Assert.Equal(Styled.Replace("\r\n", "\n"), Format(Flatten(Styled.Replace("\r\n", "\n"))));
    }

    [Fact]
    public void Le_formatage_est_idempotent_et_ne_change_rien_a_un_texte_deja_formate()
    {
        var once = Format(Flatten(Styled.Replace("\r\n", "\n")));

        Assert.Equal(once, Format(once));
        Assert.Null(Formatter.Format(Open(once)));
    }

    [Fact]
    public void Le_blanc_de_fin_de_ligne_les_lignes_vides_multiples_et_la_fin_de_fichier_sont_regles()
    {
        var text = "\n\nforge: gears   \ndescription: ''\n\n\n\ngear f(): int {  \n    return 1;   \n}\n\n\n";

        Assert.Equal("forge: gears\ndescription: ''\n\ngear f(): int {\n    return 1;\n}\n", Format(text));
        Assert.Equal("gear f(): int => 1;\n", Format("gear f(): int => 1;"));
    }

    [Fact]
    public void Les_fins_de_ligne_windows_sont_gardees()
    {
        Assert.Equal("gear f(): int {\r\n    return 1;\r\n}\r\n", Format("gear f(): int {\r\nreturn 1;   \r\n}\r\n"));
    }

    [Fact]
    public void Le_style_ne_se_regle_pas_quatre_espaces_pour_tout_le_monde()
    {
        // Des tabulations ou deux espaces écrits par quelqu'un d'autre redeviennent quatre espaces.
        Assert.Equal("gear f(): int {\n    return 1;\n}\n", Format("gear f(): int {\n\treturn 1;\n}\n"));
        Assert.Equal("gear f(): int {\n    return 1;\n}\n", Format("gear f(): int {\n  return 1;\n}\n"));
        Assert.Equal("    ", Formatter.IndentUnit);
    }

    [Fact]
    public void Le_texte_entier_se_formate_d_un_coup_et_un_texte_formate_ne_change_pas()
    {
        var text = "gear f(): int {\nreturn 1;   \n}\n\n\n";

        var formatted = Formatter.FormatText("f.forge", text);

        Assert.Equal("gear f(): int {\n    return 1;\n}\n", formatted);
        Assert.Equal(formatted, Formatter.FormatText("f.forge", formatted));
        Assert.Equal("", Formatter.FormatText("f.forge", ""));
    }

    [Fact]
    public void Le_contenu_d_un_texte_et_d_un_bloc_cpp_ne_bouge_pas()
    {
        var text = "gear f(): string {\n    return `a\n   b  \n\n\n  c`;\n}\n";

        Assert.Equal(text, Format(text));
        Assert.Equal(text, Format(text.Replace("    return", "return")));
    }

    [Fact]
    public void Un_commentaire_de_bloc_garde_l_ecart_de_ses_lignes()
    {
        var text = "gear f(): int {\n/*\n  un\n    deux\n*/\nreturn 1;\n}\n";

        Assert.Equal("gear f(): int {\n    /*\n      un\n        deux\n    */\n    return 1;\n}\n", Format(text));
    }

    [Fact]
    public void Une_plage_ne_touche_que_ses_lignes()
    {
        var text = "gear f(): int {\nint a: 1;\nint b: 2;\nreturn a + b;\n}\n";

        Assert.Equal("gear f(): int {\nint a: 1;\n    int b: 2;\nreturn a + b;\n}\n", Format(text, first: 2, last: 2));
    }

    [Fact]
    public void Un_pipeline_qui_continue_est_decale_et_un_operateur_en_fin_de_ligne_aussi()
    {
        var text = "gear f(int a): int {\nreturn a +\n1;\n}\ntrain |> a\n|> b\n|> done;\n";

        Assert.Equal("gear f(int a): int {\n    return a +\n        1;\n}\ntrain |> a\n    |> b\n    |> done;\n", Format(text));
    }
}
