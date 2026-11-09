using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.IR.Expressions;

/// <summary>L'entrée standard d'un processus enfant.</summary>
internal enum SpawnInput
{
    /// <summary>Vide : le processus lit une fin de fichier tout de suite (par défaut).</summary>
    Null,

    /// <summary>Fermée : le processus n'a pas d'entrée standard.</summary>
    None,
}

/// <summary>Une sortie d'un processus enfant.</summary>
internal enum SpawnOutput
{
    /// <summary>Capturée dans le résultat (par défaut).</summary>
    Pipe,

    /// <summary>Partagée avec ce programme : le processus écrit là où il écrit.</summary>
    Inherit,

    /// <summary>Jetée.</summary>
    Null,
}

/// <summary>Les options d'un <c>spawn</c>, connues à la compilation.</summary>
/// <param name="TimeoutMs">La durée maximale en millisecondes ; 0 pour aucune.</param>
/// <param name="WorkingDirectory">Le dossier de travail ; <see langword="null"/> pour celui de ce programme.</param>
/// <param name="Environment">Les variables d'environnement ajoutées ou remplacées.</param>
/// <param name="Input">L'entrée standard.</param>
/// <param name="Output">La sortie standard.</param>
/// <param name="Error">La sortie d'erreur.</param>
internal sealed record SpawnOptions(uint TimeoutMs, string? WorkingDirectory, IReadOnlyList<(string Name, string Value)> Environment, SpawnInput Input, SpawnOutput Output, SpawnOutput Error)
{
    /// <summary>Les options par défaut : sans limite, entrée vide, sorties capturées.</summary>
    public static SpawnOptions Default { get; } = new(0, null, [], SpawnInput.Null, SpawnOutput.Pipe, SpawnOutput.Pipe);
}

/// <summary>
/// <c>spawn "make" "-j4"</c> : lance l'exécutable avec ses arguments, attend sa fin et donne un <c>ProcessResult ! SpawnError</c> : le code de sortie et ce qu'il
/// a écrit, ou la raison pour laquelle il n'a pas pu s'exécuter (exécutable introuvable, refusé, délai dépassé).
/// </summary>
/// <param name="Span">Le lancement dans le source.</param>
/// <param name="Executable">Le chemin ou le nom de l'exécutable, une <c>string</c>.</param>
/// <param name="Arguments">Ses arguments, des <c>string</c>.</param>
/// <param name="Options">Les options.</param>
/// <param name="Type">Le résultat : <c>ProcessResult ! SpawnError</c>.</param>
internal sealed record IrSpawn(SourceSpan Span, IrExpression Executable, IReadOnlyList<IrExpression> Arguments, SpawnOptions Options, IrType Type) : IrExpression(Span, Type);
