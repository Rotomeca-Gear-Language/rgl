using Rgl.AST;
using Rgl.AST.Descriptors;
using Rgl.AST.Train;
using Rgl.AST.Interfaces;
using Rgl.Diagnostics;
using Rgl.Syntax;
using Rgl.Text;
using Rotomeca.Core.Collections;

namespace Rgl.Parsing;

internal sealed partial class Parser
{
    private const string DescriptorExample = "un descriptor s'écrit par exemple : descriptor Counter -> Compteur { constructor(int départ) => Counter(départ); add(int n): int => $self.add(n); }";

    /// <summary>
    /// <c>Descriptor := "descriptor" TypeC++ "-&gt;" Nom "{" { Membre } "}"</c>, avec
    /// <c>Membre := "constructor" "(" Paramètres ")" "=&gt;" C++ ";" | [ "const" ] "field" nom ":" Type "=&gt;" C++ ";" | [ "static" ] nom "(" Paramètres ")" ":" Type "=&gt;" C++ ";"</c>.
    /// Le C++ après la flèche est gardé tel qu'écrit, jusqu'au « ; » de même niveau.
    /// </summary>
    private DescriptorDeclaration _ParseDescriptor()
    {
        var keyword = _Consume();

        // Le type C++ : tout ce qui précède « -> » (un nom qualifié, ns::Counter).
        SourceSpan? cppType = null;
        while (!_ExpectOneOf(TokenKind.Arrow, TokenKind.OpenBrace, TokenKind.EndOfFile))
        {
            var token = _Consume();
            cppType = cppType is null ? token.Span : cppType.Value.Union(token.Span);
        }
        if (cppType is null) _Error(_MissingAfterPrevious(), DiagnosticCodes.ExpectedToken, "type C++ attendu après « descriptor »", "type manquant ici", DescriptorExample);
        _ExpectOrReport(TokenKind.Arrow, "entre le type C++ et son nom RGL", DescriptorExample);
        var name = _ParseName("nom du type RGL", DescriptorExample);
        var typeParameters = _Expect(TokenKind.Less) ? _ParseTypeParameters() : null;

        RArray<DescriptorMember> members = new();
        if (_ExpectOrReport(TokenKind.OpenBrace, "pour ouvrir le descriptor", DescriptorExample))
        {
            while (!_ExpectOneOf(TokenKind.CloseBrace, TokenKind.EndOfFile) && !_AtDeclarationStart())
            {
                if (_ParseDescriptorMember() is { } member) members.Push(member);
                else _SkipToSemicolon();
            }
            _ExpectOrReport(TokenKind.CloseBrace, "pour fermer le descriptor");
        }

        return new DescriptorDeclaration(keyword.Span.Union(_previous!.Span), cppType ?? keyword.Span, name, [.. members], typeParameters);
    }

    private DescriptorMember? _ParseDescriptorMember()
    {
        var first = _Current;
        var isConst = false;
        var kind = DescriptorMemberKind.Method;

        if (_Expect(TokenKind.ConstKeyword) && _Future().Kind == TokenKind.Identifier && _Future().Text == "field")
        {
            _Consume();
            isConst = true;
        }

        Identifier name;
        IReadOnlyList<Parameter> parameters = [];
        ITypeNode? type = null;
        if (_Expect(TokenKind.Identifier, "field") && _Future().Kind == TokenKind.Identifier)
        {
            _Consume();
            kind = DescriptorMemberKind.Field;
            name = _ParseName("nom du champ", "un champ s'écrit par exemple : field total: int => _total;");
            if (_ExpectAndConsume(TokenKind.Colon)) type = _ParseType();
            else _Error(_MissingAfterPrevious(), DiagnosticCodes.ExpectedToken, "« : » et le type du champ attendus", "type manquant ici", "un champ s'écrit par exemple : field total: int => _total;");
        }
        else if (isConst)
        {
            _Error(_Current.Span, DiagnosticCodes.ExpectedToken, "« field » attendu après « const »", "pas un champ", "un champ constant s'écrit par exemple : const field capacité: int => _capacity;");
            return null;
        }
        else
        {
            if (_Expect(TokenKind.Identifier, "static") && _Future().Kind == TokenKind.Identifier)
            {
                _Consume();
                kind = DescriptorMemberKind.Static;
            }
            else if (_Expect(TokenKind.Identifier, "constructor") && _Future().Kind == TokenKind.OpenParen) kind = DescriptorMemberKind.Constructor;

            name = _ParseName(kind == DescriptorMemberKind.Constructor ? "constructor" : "nom du membre", DescriptorExample);
            if (_Expect(TokenKind.OpenParen)) parameters = _ParseParameters($"de « {name.Name} »");
            else _ExpectOrReport(TokenKind.OpenParen, "pour ouvrir les paramètres du membre", DescriptorExample);
            if (kind != DescriptorMemberKind.Constructor)
            {
                if (_ExpectAndConsume(TokenKind.Colon)) type = _ParseType();
                else _Error(_MissingAfterPrevious(), DiagnosticCodes.ExpectedToken, "« : » et le type de retour attendus", "type de retour manquant ici", "un membre déclare son type de retour : Nom(): int => …;");
            }
        }

        if (!_ExpectOrReport(TokenKind.FatArrow, "entre la signature et l'expression C++", DescriptorExample)) return null;

        // L'expression C++ : tous les tokens jusqu'au « ; » qui n'est dans aucune parenthèse, accolade ou crochet.
        SourceSpan? expression = null;
        var depth = 0;
        while (!_Expect(TokenKind.EndOfFile) && !(depth == 0 && _Expect(TokenKind.Semicolon)))
        {
            if (_ExpectOneOf(TokenKind.OpenParen, TokenKind.OpenBrace, TokenKind.OpenBracket)) ++depth;
            else if (_ExpectOneOf(TokenKind.CloseParen, TokenKind.CloseBrace, TokenKind.CloseBracket) && --depth < 0) break;
            var token = _Consume();
            expression = expression is null ? token.Span : expression.Value.Union(token.Span);
        }
        if (expression is null)
        {
            _Error(_MissingAfterPrevious(), DiagnosticCodes.ExpectedToken, "expression C++ attendue après « => »", "expression manquante ici", DescriptorExample);
            return null;
        }
        _ExpectOrReport(TokenKind.Semicolon, "à la fin du membre", DescriptorExample);
        return new DescriptorMember(first.Span.Union(_previous!.Span), kind, isConst, name, parameters, type, expression.Value);
    }
}
