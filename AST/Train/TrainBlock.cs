using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Train;

/// <summary>
/// Un train d'un fichier <c>forge: train</c> avec ses déclarations locales : <c>train nom { … train(params) … ; }</c>,
/// ou, sans nom, le bloc du train par défaut <c>{ … train(params) … ; }</c>.
/// </summary>
/// <param name="Span">Du <c>train</c> (ou de l'accolade ouvrante) à l'accolade fermante.</param>
/// <param name="Name">Le nom du train ; <see langword="null"/> pour le bloc du train par défaut.</param>
/// <param name="Declarations">Les constantes, gears, molds… locaux, dans l'ordre du source.</param>
/// <param name="Train">Le train, dernière instruction du bloc ; <see langword="null"/> s'il manque.</param>
internal sealed record TrainBlock(SourceSpan Span, Identifier? Name, IReadOnlyList<IStatement> Declarations, TrainDeclaration? Train) : IStatement;
