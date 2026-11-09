using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.IR.Expressions;

/// <summary>
/// <c>await promesse</c> : suspend le gear async courant jusqu'à ce que la promesse soit terminée, puis donne son résultat.
/// </summary>
/// <param name="Span">L'expression dans le source.</param>
/// <param name="Operand">La promesse attendue.</param>
/// <param name="Type"><see cref="PromiseType.Awaited"/> de la promesse.</param>
internal sealed record IrAwait(SourceSpan Span, IrExpression Operand, IrType Type) : IrExpression(Span, Type);

/// <summary>
/// Dans un gear <c>@sync</c> : attend la promesse sur place — la boucle d'événements tourne jusqu'à sa fin — et donne son résultat.
/// </summary>
/// <param name="Span">L'expression dans le source.</param>
/// <param name="Operand">La promesse attendue.</param>
/// <param name="Type"><see cref="PromiseType.Awaited"/> de la promesse.</param>
internal sealed record IrBlockOn(SourceSpan Span, IrExpression Operand, IrType Type) : IrExpression(Span, Type);

/// <summary>
/// <c>whenAny(promesses)</c> : une promesse du résultat de la première promesse du tableau qui finit ; les autres continuent.
/// </summary>
/// <param name="Span">L'appel dans le source.</param>
/// <param name="Promises">Un tableau de promesses de même type.</param>
/// <param name="Promise">Le type des promesses du tableau.</param>
internal sealed record IrWhenAny(SourceSpan Span, IrExpression Promises, PromiseType Promise) : IrExpression(Span, Promise);

/// <summary>
/// <c>task f(x)</c> : l'appel d'un gear ordinaire, exécuté sur le pool de threads du runtime avec des copies de ses arguments ; une promesse de son résultat.
/// </summary>
/// <param name="Span">L'expression dans le source.</param>
/// <param name="Call">L'appel, dont les arguments sont évalués tout de suite.</param>
/// <param name="Promise"><c>Promise&lt;T ! AsyncException&gt;</c>, où <c>T</c> est ce que le gear renvoie.</param>
internal sealed record IrTaskCall(SourceSpan Span, IrCall Call, PromiseType Promise) : IrExpression(Span, Promise);

/// <summary><c>cancel promesse;</c> : demande l'annulation de la tâche ; elle s'arrête à son prochain point de suspension (tout de suite si elle attend).</summary>
/// <param name="Span">L'instruction dans le source.</param>
/// <param name="Operand">La promesse de la tâche à annuler.</param>
internal sealed record IrCancel(SourceSpan Span, IrExpression Operand) : IrExpression(Span, PrimitiveType.Void);

/// <summary><c>promesse.state</c> : où en est la tâche (<c>TaskState</c> de la std).</summary>
/// <param name="Span">L'accès dans le source.</param>
/// <param name="Operand">La promesse lue.</param>
/// <param name="Enum">Le type <c>TaskState</c>.</param>
internal sealed record IrPromiseState(SourceSpan Span, IrExpression Operand, EnumType Enum) : IrExpression(Span, Enum);

/// <summary>
/// <c>readAsync(chemin)</c> et <c>writeAsync(chemin, texte)</c> : une promesse dont le travail bloquant (lire ou écrire le fichier) s'exécute sur le pool
/// de threads du runtime, pendant que les autres tâches continuent.
/// </summary>
/// <param name="Span">L'appel dans le source.</param>
/// <param name="Path">Le chemin du fichier.</param>
/// <param name="Text">Le texte à écrire ; <see langword="null"/> pour une lecture.</param>
/// <param name="Promise"><c>Promise&lt;string ! AsyncException&gt;</c> pour une lecture, <c>Promise&lt;void ! AsyncException&gt;</c> pour une écriture.</param>
internal sealed record IrAsyncFile(SourceSpan Span, IrExpression Path, IrExpression? Text, PromiseType Promise) : IrExpression(Span, Promise);

/// <summary>
/// <c>spawnAsync(exécutable, arguments…)</c> : une promesse du <c>ProcessResult</c> d'un processus enfant, lancé et attendu par le pool de threads du runtime
/// pendant que les autres tâches continuent. Un échec du lancement ou un délai est une <c>AsyncException</c>.
/// </summary>
/// <param name="Span">L'appel dans le source.</param>
/// <param name="Executable">L'exécutable.</param>
/// <param name="Arguments">Ses arguments.</param>
/// <param name="Options">Les options (<c>spawnAsyncWith</c>), celles par défaut sinon.</param>
/// <param name="Promise"><c>Promise&lt;ProcessResult ! AsyncException&gt;</c>.</param>
internal sealed record IrAsyncSpawn(SourceSpan Span, IrExpression Executable, IReadOnlyList<IrExpression> Arguments, SpawnOptions Options, PromiseType Promise) : IrExpression(Span, Promise);

/// <summary><c>sleep(durée)</c> : une promesse qui se termine une fois la durée écoulée, sans bloquer le thread.</summary>
/// <param name="Span">L'appel dans le source.</param>
/// <param name="Duration">La durée à attendre.</param>
/// <param name="Promise">Le type de la promesse : <c>Promise&lt;void ! AsyncException&gt;</c>.</param>
internal sealed record IrSleep(SourceSpan Span, IrExpression Duration, PromiseType Promise) : IrExpression(Span, Promise);
