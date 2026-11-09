using System.Globalization;
using System.Text;
using Rgl.IR;
using Rgl.IR.Expressions;
using Rgl.IR.Gears;
using Rgl.IR.Statements;
using Rgl.IR.Symbols;
using Rgl.IR.Train;
using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.Emit;

/// <summary>
/// Traduit l'IR en C++ : une traduction mécanique, sans vérification, puisque l'IR est déjà
/// valide. Le code produit contient le train d'entrée (<c>rgl_entry_train</c>) et son <c>main()</c>.
/// Les instructions du fichier deviennent le corps de <c>rgl_entry_train</c>, dans l'ordre du
/// source : celles qui précèdent le train s'exécutent avant lui, et le train termine la fonction.
/// </summary>
/// <remarks>
/// <para>
/// On ne compile que ce dont le programme a besoin : l'émetteur note les parties du runtime qu'il
/// utilise réellement (<see cref="RuntimeComponent"/>), n'inclut que leurs en-têtes, et les transmet
/// à la chaîne de compilation, qui ne compile et ne lie qu'elles. <c>train |&gt; done;</c> n'inclut
/// donc aucun en-tête du runtime ; seul un train qui déclare <c>[string] args</c> lit les arguments.
/// </para>
/// <para>
/// Chaque instruction est précédée d'une directive <c>#line</c> qui renvoie à sa ligne dans le
/// fichier RGL : un avertissement de Clang, un débogueur ou un sanitizer pointe ainsi vers le source
/// RGL, pas vers ce C++ intermédiaire.
/// </para>
/// <para>
/// Un train d'entrée se traduit en une suite d'étapes : <c>done</c>, <c>$ valeur</c> et un emit
/// renvoient le code de sortie ; un guard qui échoue entre dans le bloc de son rail, qui termine
/// lui aussi le train, ou, sans rail, le train échoue (code 1), comme un <c>fail</c>.
/// </para>
/// <para>
/// Sans exception C++, un guard renvoie un <c>rgl::standard::guarded&lt;T, E&gt;</c> : sa valeur, ou
/// l'erreur de son <c>fail</c>, E étant le type de ses erreurs (un mold d'erreur, ou leur union).
/// Dans un guard, ce qui peut échouer au milieu d'une expression (<c>as!</c>, un cast ou une borne
/// contrôlés, une division entière par un diviseur inconnu, l'appel d'un autre guard) s'écrit en
/// expression-instruction de Clang, <c>({ … })</c>, dont un <c>return</c> quitte le guard avec l'échec.
/// </para>
/// <para>
/// Le code généré ne nomme que des types <c>rgl::standard</c>, jamais <c>std::</c> directement
/// (sauf les entiers de taille fixe <c>std::uint32_t</c>…). Les noms RGL reçoivent un préfixe, pour
/// ne jamais entrer en conflit avec un mot-clé ou un nom du C++.
/// </para>
/// <para>
/// Une variable devient une variable locale C++ ; une constante est <c>const</c>, ou
/// <c>constexpr</c> quand sa valeur est entièrement connue à la compilation. Chaque variable reçoit
/// son propre nom C++ : une variable masquée ou redéclarée après <c>undefine</c> ne réutilise
/// jamais le nom de la précédente.
/// </para>
/// </remarks>
internal sealed partial class CppEmitter
{
    private const string Indent = "    ";

    /// <summary>Le C++ du programme et les parties du runtime qu'il utilise.</summary>
    /// <param name="program">Le programme, déjà vérifié par le binder.</param>
    /// <param name="sourcePath">
    /// Chemin du fichier RGL écrit dans les <c>#line</c> : le chemin complet pour un vrai build (les
    /// outils retrouvent ainsi le fichier), un nom court dans les tests (pour un résultat stable).
    /// </param>
    public static CppProgram Emit(IrProgram program, string sourcePath) => new CppEmitter(sourcePath)._Emit(program);

    private readonly string _sourcePath;

    private readonly string _file;

    /// <summary>Ce que font les opérations entières qui débordent.</summary>
    private OverflowMode _overflow;

    /// <summary>Profondeur maximale des gears récursifs ; <see langword="null"/> pour aucune limite.</summary>
    private uint? _recursionLimit;

    /// <summary>En-têtes du runtime réellement utilisés, écrits en tête du fichier une fois le code produit.</summary>
    private readonly SortedSet<string> _includes = new(StringComparer.Ordinal);

    /// <summary>Parties du runtime à compiler et à lier.</summary>
    private readonly HashSet<RuntimeComponent> _components = [];

    /// <summary>Nom C++ de chaque variable, attribué à sa déclaration.</summary>
    private readonly Dictionary<VariableSymbol, string> _variableNames = [];

    /// <summary>Les regex littérales du programme, une par motif et flags (sans <c>g</c>) : leur struct est écrit avant le code.</summary>
    private readonly List<(string Pattern, string Flags)> _regexes = [];

    /// <summary>Noms des symboles littéraux du programme : chacun reçoit une entrée statique, avant le code.</summary>
    private readonly SortedSet<string> _symbols = new(StringComparer.Ordinal);

    /// <summary>
    /// Les chaînes littérales longues (plus de 22 octets), chacune écrite une seule fois en constante
    /// statique : une vue sur le texte du programme, sans allocation à chaque évaluation.
    /// </summary>
    private readonly Dictionary<string, string> _longLiterals = new(StringComparer.Ordinal);

    /// <summary>Nombre de constantes temporaires déjà créées, pour leur donner des noms uniques.</summary>
    private int _temporaryCount;

    /// <summary>Les types d'erreur standard : celui d'un <c>fail</c> de texte et d'une division par zéro, celui d'un cast ou d'une borne.</summary>
    private MoldSymbol _exceptionMold = null!;

    private MoldSymbol _castErrorMold = null!;

    /// <summary>Le mold <c>LoopError</c> du programme, que reçoit le rail d'une boucle <c>*&lt;!N&gt;</c> qui atteint sa limite.</summary>
    private MoldSymbol _loopErrorMold = null!;

    /// <summary>Le mold <c>IoError</c> du programme, envoyé par la lecture ou l'écriture d'un fichier qui échoue.</summary>
    private MoldSymbol _ioErrorMold = null!;

    /// <summary>Le gear dont on écrit le corps ; <see langword="null"/> dans le train et au niveau du fichier.</summary>
    private GearSymbol? _gear;

    /// <summary>Le C++ de la valeur qui entre dans l'étape courante d'un chemin (<see cref="IrPipelineValue"/>).</summary>
    private string? _pipelineValue;

    /// <summary>Vrai si le code contient une expression-instruction <c>({ … })</c> : Clang ne doit pas en avertir.</summary>
    private bool _usesStatementExpressions;

    /// <summary>Nom C++ de chaque mold : <c>rgl_m_Vec2</c>, ou <c>rgl_a_1</c>, <c>rgl_a_2</c>… pour les molds anonymes.</summary>
    private readonly Dictionary<MoldSymbol, string> _moldNames = [];

    /// <summary>Les molds nommés dans le code produit : seuls leurs structs sont écrits (avec ceux qu'ils contiennent).</summary>
    private readonly HashSet<MoldSymbol> _usedMolds = [];

    /// <summary>Les enums nommés dans le code produit : seuls eux sont écrits.</summary>
    private readonly HashSet<EnumSymbol> _usedEnums = [];

    /// <summary>Les enums écrits en texte : ils reçoivent une fonction <c>rgl_write</c> qui donne le nom de la variante.</summary>
    private readonly HashSet<EnumSymbol> _writtenEnums = [];

    /// <summary>Les molds écrits en texte (template, console) : leur struct reçoit une fonction <c>rgl_write</c>.</summary>
    private readonly HashSet<MoldSymbol> _writtenMolds = [];

    /// <summary>Nom C++ de chaque valeur évaluée d'abord dans une valeur de mold (<see cref="IrPart"/>).</summary>
    private readonly Dictionary<IrExpression, string> _partNames = new(ReferenceEqualityComparer.Instance);

    /// <summary>Vrai pendant l'écriture des constantes du fichier : hors fonction, une lambda ne capture rien.</summary>
    private bool _atFileLevel;

    /// <summary>Vrai dans un guard : un échec au milieu d'une expression le quitte par un <c>return</c>.</summary>
    private bool _InGuard => _gear is { Kind: GearKind.Guard } && !_callableTrain;

    /// <summary>Vrai dans le corps d'un gear <c>@async</c> ou <c>@fire</c> : une coroutine, qui termine par <c>co_return</c> et peut contenir des <c>co_await</c>.</summary>
    private bool _InCoroutine => _gear is { IsCoroutine: true };

    /// <summary>
    /// Vrai là où une expression composée s'écrit en expression-instruction <c>({ … })</c> plutôt qu'en lambda appelée sur place : dans un guard (un échec
    /// doit quitter le guard, pas la lambda) et dans une coroutine (un <c>co_await</c> ne peut pas vivre dans une lambda ordinaire).
    /// </summary>
    private bool _UseStatementExpressions => _InGuard || _InCoroutine;

    /// <summary>Nombre de variables déjà nommées pour chaque nom RGL.</summary>
    private readonly Dictionary<string, int> _nameCounts = new(StringComparer.Ordinal);

    /// <summary>Le corps de chaque gear, pour suivre ce qu'un gear appelé modifie.</summary>
    private Dictionary<GearSymbol, IrBlock> _bodies = [];

    /// <summary>Les lectures d'une variable à son dernier usage, qui la déplacent au lieu de la copier (<see cref="LastUses"/>).</summary>
    private HashSet<IrVariableReference> _moves = [];

    /// <summary>Les tableaux étalés en tête d'un tableau écrit qu'on reprend tels quels, plutôt que de les recopier (<see cref="LastUses"/>).</summary>
    private HashSet<IrExpression> _spreadMoves = [];

    /// <summary>Les gears à récursion terminale, écrits en boucle (<see cref="TailCalls"/>).</summary>
    private HashSet<GearSymbol> _tailGears = [];

    /// <summary>Les optimisations appliquées ou refusées, dans l'ordre où le code est écrit (<see cref="OptimizationNote"/>).</summary>
    private readonly List<OptimizationNote> _notes = [];

    private void _Note(Rgl.Text.SourceSpan span, string message) => _AddNote(new OptimizationNote(span, false, message));

    private void _AddNote(OptimizationNote note)
    {
        // Une expression écrite plusieurs fois ne se signale qu'une fois.
        if (!_notes.Any(n => n.Span == note.Span && n.Message == note.Message)) _notes.Add(note);
    }

    private CppEmitter(string sourcePath)
    {
        _sourcePath = sourcePath;
        _file = _Quote(sourcePath);
    }

    private CppProgram _Emit(IrProgram program)
    {
        var body = new StringBuilder();
        var entry = program.Entry;
        _overflow = program.Overflow;
        (_exceptionMold, _castErrorMold, _loopErrorMold, _ioErrorMold) = (program.Exception, program.CastError, program.LoopError, program.IoError);
        (_parseErrorMold, _fieldDefaults, _xmlNodeMold) = (program.ParseError, program.FieldDefaults, program.XmlNode);

        // Le train d'entrée : avec le paramètre des arguments seulement s'il le déclare.
        string parameters;
        if (entry.Arguments is { } arguments)
        {
            _Use(RuntimeComponent.Arguments, "rgl/standard/arguments.hpp");
            parameters = $"[[maybe_unused]] const {_Type(arguments.Type)}& {_Name(arguments)}";
        }
        else parameters = "";

        // Les noms des molds anonymes, dans l'ordre de leur apparition.
        var anonymous = 0;
        foreach (var mold in program.Molds) _moldNames[mold] = mold.CppName is { } name ? "rgl_m_" + name : $"rgl_a_{++anonymous}";
        _ComputeFieldLiveness(program);
        var lastUses = LastUses.Of(program);
        (_moves, _spreadMoves) = (lastUses.Moves, lastUses.Spreads);
        _tailGears = TailCalls.Of(program);
        _bodies = program.Gears.ToDictionary(g => g.Symbol, g => g.Body);

        // Les gears sont déclarés d'abord : un gear peut appeler un gear écrit plus bas, ou lui-même,
        // et une constante du fichier peut en appeler un.
        _recursionLimit = program.RecursionLimit;
        foreach (var gear in program.Gears)
        {
            if (gear.Symbol.Async == AsyncMode.Task) body.Append(_Signature(gear.Symbol, TaskBodySuffix)).Append(";\n");
            body.Append(_Signature(gear.Symbol)).Append(";\n");
            foreach (var given in _DefaultEntries(gear.Symbol)) body.Append(_Signature(gear.Symbol, _EntrySuffix(given), _EntryParameters(gear.Symbol, given))).Append(";\n");
        }
        if (program.Gears.Count > 0) body.Append('\n');
        var releasesAt = body.Length;

        // Les constantes du fichier, visibles des gears et du train.
        var constants = program.Statements.OfType<IrVariableDeclaration>().ToArray();
        _atFileLevel = true;
        foreach (var constant in constants) _Statement(body, constant, "");
        _atFileLevel = false;
        if (constants.Length > 0) body.Append('\n');

        // Les fonctions JSON s'écrivent ici, après les constantes (un défaut de champ peut en lire une), mais avant les gears qui les appellent ;
        // on ne sait lesquelles existent qu'une fois les gears écrits.
        var jsonAt = body.Length;

        foreach (var gear in program.Gears)
        {
            _overflow = gear.Overflow ?? program.Overflow;
            _Gear(body, gear);
            foreach (var given in _DefaultEntries(gear.Symbol)) _DefaultEntry(body, gear, given);
        }

        _overflow = program.Overflow;
        body.Append("static std::uint32_t rgl_entry_train(").Append(parameters).Append(") {\n");
        _Statement(body, entry, Indent);
        body.Append("}\n\n");

        // main() : lit les arguments seulement si le train les déclare.
        body.Append(entry.Arguments is null
            ? "int main() { return static_cast<int>(rgl_entry_train()); }\n"
            : "int main(int argc, char** argv) { return static_cast<int>(rgl_entry_train(rgl::standard::read_arguments(argc, argv))); }\n");

        body.Insert(jsonAt, _JsonFunctions() + _XmlFunctions());

        // Les structs des molds puis les enums, une fois le code écrit : on sait alors lesquels sont
        // utilisés et lesquels s'écrivent en texte (un struct peut utiliser un enum).
        var structs = _Structs(program.Molds);
        var enums = _Enums(program.Enums);
        _FinalizeNativeUnits();
        body.Insert(releasesAt, _LifecycleReleases(program.Molds));

        var output = new StringBuilder();
        output.Append("// Généré par rgl à partir de ").Append(Path.GetFileName(_sourcePath)).Append(" : ne pas modifier.\n");
        output.Append("#include <cstdint>\n");
        foreach (var include in _includes) output.Append("#include \"").Append(include).Append("\"\n");
        // Les en-têtes C s'incluent en C : sans extern "C", les noms de leurs fonctions seraient ceux du C++.
        foreach (var header in program.Natives?.SelectMany(n => n.Headers) ?? [])
            output.Append("extern \"C\" {\n#include \"").Append(header.Replace('\\', '/')).Append("\"\n}\n");
        output.Append('\n');
        // Les noms des fichiers des positions compactes : le runtime les retrouve pour écrire « main.rgl:44:19 ».
        if (_positionFiles.Count > 0)
            output.Append("namespace rgl::standard { const char* const position_files[] = {")
                .Append(string.Join(", ", _positionFiles.Select(_Quote).Append("nullptr"))).Append("}; }\n\n");
        if (_usesStatementExpressions)
            output.Append("// Les guards quittent une expression en échec par ({ … return …; }), une extension de Clang.\n")
                .Append("#pragma clang diagnostic ignored \"-Wgnu-statement-expression\"\n\n");

        // Une entrée par symbole littéral : inline la rend unique dans tout le programme, et
        // tous les §nom de même nom pointent vers elle.
        foreach (var symbol in _symbols)
            output.Append("inline constexpr rgl::standard::symbol_entry rgl_s_").Append(symbol)
                .Append("{\"").Append(symbol).Append("\", ").Append(symbol.Length).Append("};\n");
        if (_symbols.Count > 0) output.Append('\n');

        foreach (var (pattern, flags) in _regexes) output.Append(_RegexStruct(_regexes.IndexOf((pattern, flags)), pattern, flags));
        if (_regexes.Count > 0) output.Append('\n');

        foreach (var (value, name) in _longLiterals)
            output.Append("static constexpr rgl::standard::string ").Append(name).Append(" = ").Append(value).Append(";\n");
        if (_longLiterals.Count > 0) output.Append('\n');

        output.Append(enums);
        output.Append(structs);
        output.Append(body);

        return new CppProgram(output.ToString(), _components, program.Natives, _nativeUnits, _notes);
    }

    /// <summary>Note qu'une partie du runtime est utilisée, avec son en-tête.</summary>
    private void _Use(RuntimeComponent component, string header)
    {
        _components.Add(component);
        _includes.Add(header);
    }
}
