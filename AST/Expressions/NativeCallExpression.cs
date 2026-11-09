using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Expressions;

/// <summary>
/// <c>native f(x)</c> : l'appel d'une fonction C. Le compilateur écrit cette forme lui-même dans les liaisons qu'il engendre d'un fichier
/// <c>forge: native</c> ; <c>native</c> n'est un mot de syntaxe que dans ce texte, partout ailleurs c'est un nom comme un autre.
/// </summary>
/// <param name="Span">Du mot <c>native</c> à la parenthèse fermante.</param>
/// <param name="Call">L'appel de la fonction C, dont les arguments sont ceux du gear qui l'enveloppe.</param>
internal sealed record NativeCallExpression(SourceSpan Span, IExpression Call) : IExpression;
