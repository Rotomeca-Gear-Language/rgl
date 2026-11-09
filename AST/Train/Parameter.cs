using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Train;

/// <summary>
/// Un paramètre de gear ou de train : un type suivi d'un nom (<c>[string] args</c>), précédé de
/// <c>mut</c> quand le gear modifie la variable de l'appelant (<c>mut [Particle] ps</c>).
/// </summary>
/// <param name="Span">Du type (ou de <c>mut</c>) au nom, ou à la fin de la valeur par défaut.</param>
/// <param name="Type">Le type du paramètre.</param>
/// <param name="Name">Le nom du paramètre.</param>
/// <param name="IsMutable">Vrai pour <c>mut</c>.</param>
/// <param name="IsThis">Vrai pour le premier paramètre d'un gear d'extension écrit <c>this Type nom</c> : la valeur sur laquelle il s'appelle.</param>
/// <param name="Default">La valeur par défaut écrite après <c>:</c> (<c>string b: "x"</c>) ; <see langword="null"/> sans.</param>
internal sealed record Parameter(SourceSpan Span, ITypeNode Type, Identifier Name, bool IsMutable = false, bool IsThis = false, IExpression? Default = null, bool IsParams = false) : IAstNode;
