using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.IR.Expressions;

/// <summary>Comment un flux s'ouvre.</summary>
internal enum FileOpenMode
{
    /// <summary><c>file "a.txt"</c> : en lecture.</summary>
    Read,

    /// <summary><c>file&lt;write&gt; "a.txt"</c> : en écriture, le fichier créé ou remplacé.</summary>
    Write,

    /// <summary><c>file&lt;append&gt; "a.txt"</c> : en écriture, à la fin du fichier.</summary>
    Append,
}

/// <summary>
/// <c>&lt;- file chemin</c> : ouvre le fichier et donne un <c>FileStream</c>, fermé quand la dernière copie disparaît ou par <c>close</c>. Seulement dans
/// un guard : un échec envoie une <c>IoError</c>.
/// </summary>
/// <param name="Span">Le format et le chemin dans le source.</param>
/// <param name="Path">Le chemin du fichier, une <c>string</c>.</param>
/// <param name="Mode">Lecture, écriture ou ajout.</param>
internal sealed record IrOpenFile(SourceSpan Span, IrExpression Path, FileOpenMode Mode) : IrExpression(Span, PrimitiveType.FileStream);

/// <summary>Ce qu'on fait d'un flux.</summary>
internal enum FileOperation
{
    /// <summary><c>f.readLine()</c> : la ligne suivante, <c>null</c> en fin de fichier.</summary>
    ReadLine,

    /// <summary><c>f.readBytes(n)</c> : jusqu'à <c>n</c> octets, aucun en fin de fichier.</summary>
    ReadBytes,

    /// <summary><c>f.write(texte)</c> ou <c>f.write(octets)</c> : écrit à la suite.</summary>
    Write,

    /// <summary><c>f.close()</c> : ferme le fichier, pour toutes les copies du flux.</summary>
    Close,
}

/// <summary>
/// Une méthode d'un <c>FileStream</c>. Tout sauf <c>close</c> se fait dans un guard : un échec envoie une <c>IoError</c>.
/// </summary>
/// <param name="Span">L'appel dans le source.</param>
/// <param name="Target">Le flux.</param>
/// <param name="Operation">Ce qu'on en fait.</param>
/// <param name="Argument">Le nombre d'octets de <c>readBytes</c>, la valeur de <c>write</c> ; <see langword="null"/> sinon.</param>
/// <param name="Type">Ce que donne la méthode : <c>string?</c>, <c>bytes</c> ou <c>void</c>.</param>
internal sealed record IrFileOp(SourceSpan Span, IrExpression Target, FileOperation Operation, IrExpression? Argument, IrType Type) : IrExpression(Span, Type);
