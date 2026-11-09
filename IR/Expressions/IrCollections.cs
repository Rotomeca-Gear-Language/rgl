using Rgl.IR.Symbols;
using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.IR.Expressions;

/// <summary>
/// Un tableau écrit : <c>[1, 2]</c>, <c>[...a, x]</c>. Ses éléments sont déjà du type des éléments du
/// tableau ; un élément étalé est un tableau de mêmes éléments.
/// </summary>
/// <param name="Span">Le tableau dans le source.</param>
/// <param name="Array">Son type.</param>
/// <param name="Parts">Les éléments et les tableaux étalés, dans l'ordre d'écriture.</param>
internal sealed record IrArrayValue(SourceSpan Span, ArrayType Array, IReadOnlyList<IrArrayPart> Parts) : IrExpression(Span, Array)
{
    /// <summary>Vrai s'il n'étale aucun tableau : son nombre d'éléments est connu à la compilation.</summary>
    public bool HasKnownCount => Parts.All(p => !p.IsSpread);
}

/// <summary>Un morceau d'un tableau écrit : un élément, ou un tableau étalé (<c>...a</c>).</summary>
/// <param name="Value">L'élément, ou le tableau étalé.</param>
/// <param name="IsSpread">Vrai pour <c>...a</c>.</param>
internal sealed record IrArrayPart(IrExpression Value, bool IsSpread);

/// <summary>Un dictionnaire écrit : <c>["a": 1, "b": 2]</c> ; ses clés et valeurs sont déjà de ses types.</summary>
/// <param name="Span">Le dictionnaire dans le source.</param>
/// <param name="Dictionary">Son type.</param>
/// <param name="Entries">Les paires, dans l'ordre d'écriture.</param>
internal sealed record IrDictionaryValue(SourceSpan Span, DictionaryType Dictionary, IReadOnlyList<(IrExpression Key, IrExpression Value)> Entries) : IrExpression(Span, Dictionary);

/// <summary>Un tuple écrit : <c>(1, "a")</c> ; ses éléments sont déjà de ses types.</summary>
/// <param name="Span">Le tuple dans le source.</param>
/// <param name="Tuple">Son type.</param>
/// <param name="Elements">Ses éléments, dans l'ordre.</param>
internal sealed record IrTupleValue(SourceSpan Span, TupleType Tuple, IReadOnlyList<IrExpression> Elements) : IrExpression(Span, Tuple);

/// <summary>
/// <c>a[i]</c> : un élément de tableau par son index, ou une valeur de dictionnaire par sa clé.
/// Contrôlé, un index hors limites ou une clé absente arrête le programme, ou fait échouer le guard
/// qui l'évalue.
/// </summary>
/// <param name="Span">La lecture dans le source : sa position est celle de l'échec.</param>
/// <param name="Type">Le type de l'élément ou de la valeur.</param>
/// <param name="Target">Le tableau ou le dictionnaire.</param>
/// <param name="Index">L'index (un entier) ou la clé.</param>
/// <param name="Checked">Faux quand l'index est connu et dans les limites.</param>
internal sealed record IrIndex(SourceSpan Span, IrType Type, IrExpression Target, IrExpression Index, bool Checked) : IrExpression(Span, Type);

/// <summary><c>t.Item1</c>, <c>t.x</c> : un élément de tuple.</summary>
/// <param name="Span">La lecture dans le source.</param>
/// <param name="Type">Le type de l'élément.</param>
/// <param name="Target">Le tuple.</param>
/// <param name="Position">La position de l'élément, à partir de 0.</param>
internal sealed record IrTupleItem(SourceSpan Span, IrType Type, IrExpression Target, int Position) : IrExpression(Span, Type);

/// <summary><c>a.length</c> : le nombre d'éléments d'un tableau ou de paires d'un dictionnaire, un <c>uint</c>.</summary>
/// <summary>
/// Le nombre de lignes d'un <c>soa mold</c> parcouru par <c>|S|&gt;</c> : la taille commune de ses colonnes (le programme s'arrête
/// si elles diffèrent), ou la plus petite pour <c>||S|&gt;</c>.
/// </summary>
/// <param name="Span">Le parcours dans le source : sa position est celle de l'arrêt.</param>
/// <param name="Columns">Les colonnes lues, toutes des tableaux.</param>
/// <param name="Strict">Vrai pour <c>|S|&gt;</c> : les colonnes doivent avoir la même taille.</param>
internal sealed record IrRowCount(SourceSpan Span, IReadOnlyList<IrExpression> Columns, bool Strict) : IrExpression(Span, PrimitiveType.Long);

/// <param name="Span">La lecture dans le source.</param>
/// <param name="Target">Le tableau ou le dictionnaire.</param>
internal sealed record IrLength(SourceSpan Span, IrExpression Target) : IrExpression(Span, PrimitiveType.UInt);

/// <summary><c>d.has(k)</c> : vrai si le dictionnaire a la clé.</summary>
/// <param name="Span">L'appel dans le source.</param>
/// <param name="Target">Le dictionnaire.</param>
/// <param name="Key">La clé.</param>
internal sealed record IrContains(SourceSpan Span, IrExpression Target, IrExpression Key) : IrExpression(Span, PrimitiveType.Bool);

/// <summary>
/// <c>nums.push(v)</c> : ajoute un élément à la fin d'un tableau : une variable locale, un paramètre
/// <c>mut</c>, ou un tableau dans le contenu d'une variable <c>mut</c> (<c>m.items.push(v)</c>). Dans un
/// <c>[T;N..M]</c> plein, le programme s'arrête, ou le guard échoue.
/// </summary>
/// <param name="Span">L'appel dans le source : sa position est celle de l'échec.</param>
/// <param name="Target">Le tableau modifié : une suite d'accès qui part d'une variable ou d'un paramètre (<see cref="Statements.IrMutation.Root"/>).</param>
/// <param name="Value">L'élément ajouté, du type des éléments.</param>
internal sealed record IrPush(SourceSpan Span, IrExpression Target, IrExpression Value) : IrExpression(Span, PrimitiveType.Void);

/// <summary>
/// <c>d.add(k, v)</c> : ajoute une clé nouvelle à un dictionnaire, modifiable comme le tableau d'un
/// <see cref="IrPush"/> ; une clé déjà présente arrête le programme, ou fait échouer le guard.
/// </summary>
/// <param name="Span">L'appel dans le source : sa position est celle de l'échec.</param>
/// <param name="Target">Le dictionnaire modifié.</param>
/// <param name="Key">La clé.</param>
/// <param name="Value">La valeur.</param>
internal sealed record IrAdd(SourceSpan Span, IrExpression Target, IrExpression Key, IrExpression Value) : IrExpression(Span, PrimitiveType.Void);

/// <summary>Comment passer d'une sorte de tableau à une autre.</summary>
internal enum ArrayConversion
{
    /// <summary>Toujours possible (vers <c>[T]</c>, ou une taille déjà prouvée).</summary>
    Safe,

    /// <summary>Contrôlée : hors des tailles permises, arrêt du programme, ou échec du guard.</summary>
    Checked,

    /// <summary><c>as?</c> : <c>null</c> hors des tailles permises.</summary>
    Optional,
}

/// <summary>Un tableau d'une sorte vers une autre (<c>[T+]</c> vers <c>[T]</c>, <c>[T]</c> vers <c>[T;3]</c>…).</summary>
/// <param name="Span">La conversion dans le source : sa position est celle de l'échec.</param>
/// <param name="Type">Le type obtenu : <paramref name="Target"/>, ou son nullable pour <see cref="ArrayConversion.Optional"/>.</param>
/// <param name="Value">Le tableau converti, de mêmes éléments.</param>
/// <param name="Target">La sorte de tableau visée.</param>
/// <param name="Mode">Comment la conversion se contrôle.</param>
internal sealed record IrArrayConvert(SourceSpan Span, IrType Type, IrExpression Value, ArrayType Target, ArrayConversion Mode) : IrExpression(Span, Type);

/// <summary><c>d.keys()</c> ou <c>d.values()</c> : un tableau des clés ou des valeurs d'un dictionnaire, dans l'ordre des clés.</summary>
/// <param name="Span">L'appel dans le source.</param>
/// <param name="Target">Le dictionnaire.</param>
/// <param name="Values">Vrai pour les valeurs, faux pour les clés.</param>
/// <param name="Array">Le type du tableau rendu.</param>
internal sealed record IrDictionaryParts(SourceSpan Span, IrExpression Target, bool Values, ArrayType Array) : IrExpression(Span, Array);

/// <summary><c>d.put(k, v)</c> : une copie du dictionnaire où la clé <c>k</c> vaut <c>v</c> (ajoutée ou remplacée).</summary>
/// <param name="Span">L'appel dans le source.</param>
/// <param name="Target">Le dictionnaire.</param>
/// <param name="Key">La clé.</param>
/// <param name="Value">La valeur.</param>
internal sealed record IrDictionaryPut(SourceSpan Span, IrExpression Target, IrExpression Key, IrExpression Value) : IrExpression(Span, Target.Type);

/// <summary><c>d.without(k)</c> : une copie du dictionnaire sans la clé <c>k</c> (inchangé si elle n'y est pas).</summary>
/// <param name="Span">L'appel dans le source.</param>
/// <param name="Target">Le dictionnaire.</param>
/// <param name="Key">La clé retirée.</param>
internal sealed record IrDictionaryWithout(SourceSpan Span, IrExpression Target, IrExpression Key) : IrExpression(Span, Target.Type);

/// <summary>
/// <c>ArenaPool.set(valeur)</c> : <paramref name="Value"/> évaluée avec le pool pour allocateur ; ce qu'elle construit (tableaux, textes, dictionnaires) en
/// tire sa mémoire. Même type que la valeur.
/// </summary>
/// <param name="Span">L'appel dans le source.</param>
/// <param name="Type">Le type de la valeur.</param>
/// <param name="Value">La valeur construite avec le pool.</param>
internal sealed record IrPoolValue(SourceSpan Span, IrType Type, IrExpression Value) : IrExpression(Span, Type);
