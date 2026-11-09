using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.InputOutput;

/// <summary>
/// Les arguments entre chevrons d'un format : <c>custom&lt;M, train&gt;</c> — le type <c>M</c> que le fichier représente, et le gear ou
/// guard qui convertit (en lecture il reçoit les <c>bytes</c> du fichier et renvoie <c>M</c>, en écriture il reçoit <c>M</c> et renvoie
/// des <c>bytes</c>) — ou <c>json&lt;M&gt;</c>, le type seul.
/// </summary>
/// <param name="Span">De <c>&lt;</c> à <c>&gt;</c>.</param>
/// <param name="Type">Le type <c>M</c>.</param>
/// <param name="Train">Le nom du gear ou du guard de conversion de <c>custom</c> ; <see langword="null"/> pour <c>json</c>.</param>
internal sealed record CustomFormat(SourceSpan Span, ITypeNode Type, Identifier? Train = null) : IAstNode;
