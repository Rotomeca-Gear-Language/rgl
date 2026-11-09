using Rgl.AST.Expressions;
using Rgl.Syntax;

namespace Rgl.Parsing;

internal sealed partial class Parser
{
    private CppExpression _ParseCpp()
    {
        var block = _Consume();
        var start = CppBlockReader.BodyStart(_source.Content, (int)block.Span.Start.Value + 3, (int)block.Span.End.Value)!.Value;
        var (code, _) = CppBlockReader.Read(_source, start, (int)block.Span.End.Value);
        var arguments = new List<CppArgument>();
        _ExpectOrReport(TokenKind.OpenParen, "après le bloc C++ : cpp { ... }(Type nom: valeur): Retour");
        if (!_Expect(TokenKind.CloseParen))
        {
            do
            {
                var type = _ParseType();
                var name = _ParseName("nom du paramètre C++", "un argument s'écrit Type nom: valeur");
                _ExpectOrReport(TokenKind.Colon, "entre le nom et la valeur du paramètre C++");
                var value = _ParseExpression();
                arguments.Add(new CppArgument(type.Span.Union(value.Span), type, name, value));
            } while (_ExpectAndConsume(TokenKind.Comma));
        }
        _ExpectOrReport(TokenKind.CloseParen, "après les arguments C++");
        _ExpectOrReport(TokenKind.Colon, "avant le résultat du bloc C++");
        var result = _ParseType();
        return new CppExpression(block.Span.Union(result.Span), code, arguments, result);
    }
}
