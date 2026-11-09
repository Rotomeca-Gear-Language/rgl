using Rgl.AST;
using Rgl.AST.Expressions;
using Rgl.AST.Interfaces;
using Rgl.Diagnostics;
using Rgl.Syntax;

namespace Rgl.Parsing;

internal sealed partial class Parser
{
    private static bool _IsSpawnText(TokenKind kind) => kind is TokenKind.StringLiteral or TokenKind.RawStringLiteral or TokenKind.TemplateStringLiteral or TokenKind.MutableStringLiteral;

    /// <summary>Vrai si le mot-clé <c>spawn</c> commence ici un lancement de processus.</summary>
    private bool _AtSpawn() => _Expect(TokenKind.SpawnKeyword);

    /// <summary><c>Spawn := "spawn" [ "(" Chaîne ")" ] Élément { Élément }</c>, où un élément est une chaîne ou un nom.</summary>
    private SpawnExpression _ParseSpawn()
    {
        var keyword = _Consume();
        IExpression? options = null;
        if (_ExpectAndConsume(TokenKind.OpenParen))
        {
            options = _ParsePrimary();
            _ExpectOrReport(TokenKind.CloseParen, "pour fermer les options de spawn");
        }

        List<IExpression> parts = [];
        while (true)
        {
            if (_IsSpawnText(_Current.Kind)) parts.Add(_ParsePrimary());
            else if (_Expect(TokenKind.Identifier)) parts.Add(new NameExpression(new Identifier(_Consume().Span)));
            else if (_ExpectOneOf(TokenKind.Minus, TokenKind.MinusMinus) && parts.Count > 0)
            {
                _Error(_Current.Span, DiagnosticCodes.ExpectedToken, "un argument de spawn est une chaîne : un drapeau nu est interdit", "à écrire entre guillemets",
                    "par exemple : spawn \"make\" \"-j4\";");
                _SkipToSemicolon();
                break;
            }
            else break;
        }
        return new SpawnExpression(keyword.Span.Union(_previous!.Span), options, parts);
    }
}
