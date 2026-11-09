using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>Analyse des arguments de la ligne de commande.</summary>
public class StdCliTests
{
    private const string Prelude = """
        use <Cli> only {CliSpec, CliArguments, option, flag, parseArguments, usage};
        use <Errors> only {ParseError};

        const CliSpec SPEC: {
            program: 'outil',
            description: 'Un outil de test.',
            options: [
                option('port', 'Le port', 'p', '8080'),
                option('name', 'Le nom'),
                flag('verbose', 'Plus de détails', 'v'),
                flag('quiet', 'Moins de détails', 'q')
            ]
        };

        const CliSpec STRICT: {program: 'strict', description: 'Avec une option obligatoire.', options: [option('mode', 'Le mode', null, null, true), flag('verbose', 'Détails')]};

        gear show([string] args): string {
            const CliArguments ! ParseError result: parseArguments(args, SPEC);
            return match (result) {
                ok a |> `port={a.value('port') ?? '-'} n={a.intValue('port') ?? -1} name={a.value('name') ?? '-'} v={a.has('verbose')} q={a.has('quiet')} rest={a.positional.join('|')}`;
                err e |> `erreur : {e.message} [{e.input}]`;
            };
        }
        """;

    [ZigFact]
    public void Les_options_longues_courtes_les_drapeaux_et_le_reste()
    {
        var output = StdHarness.Main(Prelude + """

            gear Test(): void {
                std.writeLine(show(['--port', '9000', '-vq', 'a.txt', '--name=Zoé', '--', '--brut', '-x']));
                std.writeLine(show([]));
                std.writeLine(show(['-p', '81']));
                std.writeLine(show(['-p82']));
                std.writeLine(show(['-p=83']));
                std.writeLine(show(['-5', 'x', '-']));
            }
            emit Fin(): uint => 0;
            !train |> Test |> Fin;
            """);

        Assert.Equal(
            "port=9000 n=9000 name=Zoé v=true q=true rest=a.txt|--brut|-x\n"
            + "port=8080 n=8080 name=- v=false q=false rest=\n"
            + "port=81 n=81 name=- v=false q=false rest=\n"
            + "port=82 n=82 name=- v=false q=false rest=\n"
            + "port=83 n=83 name=- v=false q=false rest=\n"
            + "port=8080 n=8080 name=- v=false q=false rest=-5|x|-\n", output);
    }

    [ZigFact]
    public void Les_erreurs_disent_l_argument_en_cause_et_l_aide_est_generee()
    {
        var output = StdHarness.Main(Prelude + """

            gear showStrict([string] args): string {
                const CliArguments ! ParseError result: parseArguments(args, STRICT);
                return match (result) {
                    ok a |> 'ok';
                    err e |> `{e.message} [{e.input}]`;
                };
            }

            gear Test(): void {
                std.writeLine(show(['--inconnue']));
                std.writeLine(show(['--port']));
                std.writeLine(show(['-z']));
                std.writeLine(show(['--verbose=1']));
                std.writeLine(show(['-vp']));
                std.writeLine(showStrict([]));
                std.writeLine(showStrict(['--mode', 'x']));
                std.writeLine(usage(SPEC));
            }
            emit Fin(): uint => 0;
            !train |> Test |> Fin;
            """);

        Assert.Equal(
            "erreur : option inconnue : --inconnue [--inconnue]\n"
            + "erreur : l'option --port attend une valeur [--port]\n"
            + "erreur : option inconnue : -z [-z]\n"
            + "erreur : l'option --verbose n'attend pas de valeur [--verbose=1]\n"
            + "erreur : l'option -p attend une valeur [-vp]\n"
            + "l'option --mode est obligatoire []\n"
            + "ok\n"
            + "Usage : outil [options] [arguments]\nUn outil de test.\n\nOptions :\n"
            + "  -p, --port <valeur>  Le port (défaut : 8080)\n"
            + "      --name <valeur>  Le nom\n"
            + "  -v, --verbose        Plus de détails\n"
            + "  -q, --quiet          Moins de détails\n", output);
    }
}
