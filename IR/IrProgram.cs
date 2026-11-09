using Rgl.IR.Statements;
using Rgl.IR.Train;

namespace Rgl.IR;

/// <summary>
/// Racine de l'IR : un programme dont toute la sémantique a été vérifiée. Il n'existe que si le
/// binder n'a signalé aucune erreur ; il ne contient donc jamais d'élément manquant ni de nom
/// non résolu.
/// </summary>
/// <param name="Statements">Les constantes de niveau fichier, dans l'ordre, puis le train d'entrée.</param>
/// <param name="Overflow">Ce que font les opérations entières qui débordent.</param>
/// <param name="Gears">Les gears du fichier, dans l'ordre d'écriture.</param>
/// <param name="RecursionLimit">Profondeur maximale des gears récursifs ; <see langword="null"/> pour aucune limite.</param>
/// <param name="Molds">Les molds du programme : les molds déclarés, dans l'ordre, puis les molds anonymes.</param>
/// <param name="Enums">Les enums du programme, dans l'ordre.</param>
/// <param name="Exception">Le mold d'erreur <c>Exception</c> (aussi dans <paramref name="Molds"/>).</param>
/// <param name="CastError">Le mold d'erreur <c>CastError</c>, envoyé par un cast ou une borne qui échoue dans un guard.</param>
/// <param name="IoError">Le mold d'erreur <c>IoError</c>, envoyé par la lecture ou l'écriture d'un fichier qui échoue dans un guard.</param>
/// <param name="ParseError">Le mold d'erreur <c>ParseError</c>, envoyé par la lecture ou l'écriture d'un JSON qui ne convient pas.</param>
/// <param name="FieldDefaults">La valeur par défaut des champs de mold qui en ont une, que la lecture d'un JSON donne aux champs absents.</param>
/// <param name="XmlNode">Le mold <c>XmlNode</c> de la std ; <see langword="null"/> sans la std.</param>
/// <param name="AsyncException">Le mold d'erreur <c>AsyncException</c> de la std, envoyé par une promesse annulée ou interrompue ; <see langword="null"/> sans la std.</param>
/// <param name="Natives">Les bibliothèques C que les fichiers <c>forge: native</c> du programme décrivent ; <see langword="null"/> sans eux.</param>
/// <param name="LoopError">Le mold d'erreur <c>LoopError</c>, envoyé par une boucle de guard <c>*&lt;!N&gt;</c> qui atteint sa limite.</param>
internal sealed record IrProgram(IReadOnlyList<IrStatement> Statements, OverflowMode Overflow, IReadOnlyList<Gears.IrGear> Gears, uint? RecursionLimit,
    IReadOnlyList<Symbols.MoldSymbol> Molds, IReadOnlyList<Symbols.EnumSymbol> Enums, Symbols.MoldSymbol Exception, Symbols.MoldSymbol CastError,
    Symbols.MoldSymbol LoopError, Symbols.MoldSymbol IoError, Symbols.MoldSymbol ParseError,
    IReadOnlyDictionary<Symbols.MoldFieldSymbol, Expressions.IrExpression> FieldDefaults, Symbols.MoldSymbol? XmlNode = null, Symbols.MoldSymbol? AsyncException = null,
    IReadOnlyList<Native.CLibrary>? Natives = null)
{
    /// <summary>
    /// Les precasts et constantes de données du programme (hors std), même ceux que rien ne lit et que le C++ n'écrit donc pas : c'est ce que
    /// <c>rgl precast export</c> donne aux outils.
    /// </summary>
    public IReadOnlyList<Statements.IrVariableDeclaration> Precasts { get; init; } = [];

    /// <summary>Le train d'entrée du programme.</summary>
    public IrEntryTrain Entry => Statements.OfType<IrEntryTrain>().First();
}
