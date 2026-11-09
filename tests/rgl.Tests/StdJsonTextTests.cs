using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary><c>parseJson&lt;T&gt;(texte)</c> et <c>toJson(valeur)</c>.</summary>
public class StdJsonTextTests
{
    private const string Header = """
        mold Joueur { string nom; int niveau: 1; [string] objets; }
        """;

    [ZigFact]
    public void Lit_un_texte_json_dans_un_mold()
    {
        var output = StdHarness.Main(Header + """

            gear Test(): void {
                var r: parseJson<Joueur>('{"nom": "Ana", "objets": ["epee", "arc"]}');
                match (r) {
                    ok j |> { `{j.nom} {j.niveau} {j.objets.length}\n` -> console; }
                    err e |> { `ERREUR {e.message} {e.input}\n` -> console; }
                }
                var mauvais: parseJson<Joueur>('{"nom": 3}');
                match (mauvais) {
                    ok j |> { `ok\n` -> console; }
                    err e |> { `ERREUR {e.message} {e.input}\n` -> console; }
                }
                var casse: parseJson<Joueur>('{nom');
                match (casse) {
                    ok j |> { `ok\n` -> console; }
                    err e |> { `ERREUR\n` -> console; }
                }
            }
            emit Fin(): uint => 0;
            !train |> Test |> Fin;
            """);

        Assert.Equal("Ana 1 2\nERREUR $.nom : attendu une string, reçu un entier $.nom\nERREUR\n", output);
    }

    [ZigFact]
    public void Ecrit_une_valeur_en_json_et_la_relit()
    {
        var output = StdHarness.Main(Header + """

            gear Test(): void {
                Joueur j: {nom: 'Zed', niveau: 7, objets: ['a']};
                var texte: toJson(j) ?? 'x';
                texte -> console;
                '\n' -> console;
                var retour: parseJson<Joueur>(texte);
                match (retour) {
                    ok v |> { `{v.nom}/{v.niveau}\n` -> console; }
                    err e |> { `ERREUR\n` -> console; }
                }
            }
            emit Fin(): uint => 0;
            !train |> Test |> Fin;
            """);

        Assert.Contains("\"nom\": \"Zed\"", output);
        Assert.EndsWith("Zed/7\n", output);
    }
}
