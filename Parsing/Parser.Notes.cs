using Rgl.AST;
using Rgl.AST.Enums;
using Rgl.AST.Expressions;
using Rgl.AST.Gears;
using Rgl.AST.Ingots;
using Rgl.AST.InputOutput;
using Rgl.AST.Interfaces;
using Rgl.AST.Molds;
using Rgl.AST.Notes;
using Rgl.AST.Variables;
using Rgl.Diagnostics;
using Rgl.Syntax;
using Rotomeca.Core.Collections;

namespace Rgl.Parsing;

/// <summary>
/// Les annotations <c>@nom</c> et les métadonnées <c>[clé, clé: valeur]</c> écrites devant une déclaration (gear, guard, emit, mold, error, enum, ingot,
/// constante). La liste des annotations est fermée : <c>@deprecated</c> et <c>@tap</c>, plus <c>@async</c>, <c>@sync</c> et <c>@fire</c>, reconnues mais pas
/// encore prises en charge ; toute autre est E125. Les métadonnées sont ignorées par le compilateur.
/// </summary>
internal sealed partial class Parser
{
    /// <summary>Vrai si une annotation, ou des métadonnées suivies d'une déclaration, commencent ici.</summary>
    private bool _AtNotes() => _Expect(TokenKind.At) || _AtMetadata();

    /// <summary>
    /// Vrai si le <c>[</c> courant ouvre des métadonnées : son <c>]</c> est suivi d'une annotation, d'autres métadonnées ou d'un mot qui commence une
    /// déclaration. <c>[int] nombres: …</c>, lui, est une déclaration typée d'un tableau.
    /// </summary>
    private bool _AtMetadata()
    {
        if (!_Expect(TokenKind.OpenBracket)) return false;
        var depth = 0;
        for (var i = 0; ; ++i)
        {
            var token = _Future(i);
            switch (token.Kind)
            {
                case TokenKind.EndOfFile:
                    return false;
                case TokenKind.OpenBracket:
                    ++depth;
                    break;
                case TokenKind.CloseBracket:
                    if (--depth > 0) break;
                    var next = _Future(i + 1);
                    return next.Kind is TokenKind.At or TokenKind.OpenBracket or TokenKind.GearKeyword or TokenKind.GuardKeyword or TokenKind.EmitKeyword
                            or TokenKind.MoldKeyword or TokenKind.EnumKeyword or TokenKind.IngotKeyword or TokenKind.ConstKeyword
                        || (next.Kind == TokenKind.Identifier && next.Text is "error" or "export");
            }
        }
    }

    /// <summary>Toutes les notes écrites à la suite, dans l'ordre.</summary>
    private DeclarationNotes _ParseNotes()
    {
        var first = _Current;
        List<IAnnotation> annotations = [];
        List<MetadataBlock> metadata = [];
        while (_AtNotes())
        {
            if (_Expect(TokenKind.At)) annotations.Add(_ParseAnnotation());
            else metadata.Add(_ParseMetadataBlock());
        }
        return new DeclarationNotes(first.Span.Union(_previous!.Span), annotations, metadata);
    }

    private static readonly string[] _annotationNames = ["deprecated", "tap", "before", "async", "sync", "fire", "task", "trusted", "trust", "checked", "unchecked"];

    /// <summary><c>@nom</c> ou <c>@nom(…)</c>.</summary>
    private IAnnotation _ParseAnnotation()
    {
        var at = _Consume();
        if (_Current.Kind == TokenKind.EndOfFile || _Current.Kind is TokenKind.Semicolon or TokenKind.OpenBrace)
        {
            _Error(_MissingAfterPrevious(), DiagnosticCodes.InvalidAnnotation, "nom d'annotation attendu après « @ »", "nom manquant ici", $"annotations : {string.Join(", ", _annotationNames)}");
            return new OtherAnnotation(at.Span, new Identifier(at.Span));
        }

        var name = new Identifier(_Consume().Span);
        switch (name.Name)
        {
            case "trusted" or "trust":
                return new OtherAnnotation(at.Span.Union(name.Span), name);

            case "deprecated":
                IExpression? message = null;
                if (_ExpectAndConsume(TokenKind.OpenParen))
                {
                    message = _ParseExpression();
                    _ExpectOrReport(TokenKind.CloseParen, "pour fermer @deprecated");
                }
                return new DeprecatedAnnotation(at.Span.Union(_previous!.Span), message);

            case "tap":
                return _ParseTap(at);

            case "before":
                return _ParseBefore(at);

            case "async" or "sync" or "fire" or "task" or "checked" or "unchecked":
                _SkipAnnotationArguments();
                return new OtherAnnotation(at.Span.Union(_previous!.Span), name);

            default:
                // Une annotation déclarée par le programme : le binder la cherche (E125 si elle n'existe pas).
                return _ParseCustomAnnotation(at, name);
        }
    }

    /// <summary><c>@before(contrôle)</c>.</summary>
    private IAnnotation _ParseBefore(Token at)
    {
        if (!_ExpectAndConsume(TokenKind.OpenParen))
        {
            _ExpectOrReport(TokenKind.OpenParen, "après @before", "par exemple : @before(controleDAcces)");
            return new BeforeAnnotation(at.Span, new MissingExpression(_MissingAfterPrevious()));
        }
        var check = _ParseExpression();
        _ExpectOrReport(TokenKind.CloseParen, "pour fermer @before");
        return new BeforeAnnotation(at.Span.Union(_previous!.Span), check);
    }

    /// <summary><c>@nom</c> ou <c>@nom(argument, …)</c> d'une annotation déclarée par le programme.</summary>
    private IAnnotation _ParseCustomAnnotation(Token at, Identifier name)
    {
        List<IExpression> arguments = [];
        if (_ExpectAndConsume(TokenKind.OpenParen))
        {
            if (!_Expect(TokenKind.CloseParen))
            {
                do arguments.Add(_ParseExpression());
                while (_ExpectAndConsume(TokenKind.Comma));
            }
            _ExpectOrReport(TokenKind.CloseParen, $"pour fermer @{name.Name}");
        }
        return new CustomAnnotation(at.Span.Union(_previous!.Span), name, arguments);
    }

    /// <summary>Vrai si <c>annotation</c> (contextuel) commence une déclaration d'annotation : un nom, puis <c>(</c>.</summary>
    private bool _AtAnnotationDeclaration() =>
        _Expect(TokenKind.Identifier, "annotation") && _Future().Kind == TokenKind.Identifier && _Future(2).Kind == TokenKind.OpenParen;

    /// <summary><c>annotation nom(paramètres) = @annotation … ;</c> : un paquet d'annotations dont les paramètres se lisent comme des constantes.</summary>
    private AnnotationDeclaration _ParseAnnotationDeclaration()
    {
        var keyword = _Consume();
        var name = _ParseName("nom d'annotation", "par exemple : annotation exigeRole(params [Role] roles) = @before(controle(roles));");
        var parameters = _ParseParameters($"de l'annotation « {name.Name} »");
        _ExpectOrReport(TokenKind.Equals, "après les paramètres de l'annotation", "par exemple : annotation exigeRole(params [Role] roles) = @before(controle(roles));");
        List<IAnnotation> body = [];
        while (_Expect(TokenKind.At)) body.Add(_ParseAnnotation());
        if (body.Count == 0)
            _Error(_MissingAfterPrevious(), DiagnosticCodes.InvalidAnnotation, $"l'annotation « {name.Name} » développe au moins une annotation", "annotation attendue ici", "par exemple : = @before(controle(roles));");
        _ExpectOrReport(TokenKind.Semicolon, "pour terminer la déclaration de l'annotation");
        return new AnnotationDeclaration(keyword.Span.Union(_previous!.Span), name, parameters, body);
    }

    /// <summary>Saute les parenthèses qui suivent une annotation dont on ne lit pas les arguments.</summary>
    private void _SkipAnnotationArguments()
    {
        if (!_Expect(TokenKind.OpenParen)) return;
        var depth = 0;
        do
        {
            if (_Expect(TokenKind.OpenParen)) ++depth;
            else if (_Expect(TokenKind.CloseParen)) --depth;
            _Consume();
        }
        while (depth > 0 && !_Expect(TokenKind.EndOfFile));
    }

    /// <summary><c>@tap([before | after] [closure] , corps)</c> ; le corps est une écriture <c>valeur -&gt; cible</c>, un appel ou une fonction. Une lambda implique <c>closure</c>.</summary>
    private IAnnotation _ParseTap(Token at)
    {
        var mode = TapMode.After;
        var isClosure = false;
        if (!_ExpectAndConsume(TokenKind.OpenParen))
        {
            _ExpectOrReport(TokenKind.OpenParen, "après @tap", "par exemple : @tap(`← {$}` -> console)");
            return new TapAnnotation(at.Span, mode, false, new MissingExpression(_MissingAfterPrevious()), null, null);
        }

        // before, after, closure : des mots contextuels, chacun suivi d'une virgule.
        while (_Expect(TokenKind.Identifier) && _Current.Text is "before" or "after" or "closure" && _Future().Kind == TokenKind.Comma)
        {
            var word = _Consume().Text;
            _Consume();
            if (word == "closure") isClosure = true;
            else mode = word == "before" ? TapMode.Before : TapMode.After;
        }

        var value = _ParseExpression();
        // @tap(() => effet()) est la forme naturelle : une lambda est toujours appelée, même si
        // l'auteur n'a pas écrit le mot contextuel « closure ».
        if (value is LambdaExpression) isClosure = true;
        Identifier? target = null;
        IExpression? path = null;
        if (!isClosure && _ExpectAndConsume(TokenKind.Arrow))
        {
            target = _ParseName("nom de cible", "un tap écrit vers une cible, par exemple : @tap(`← {$}` -> console)");
            path = _CanStartExpression() ? _ParseExpression() : null;
        }
        _ExpectOrReport(TokenKind.CloseParen, "pour fermer @tap");
        return new TapAnnotation(at.Span.Union(_previous!.Span), mode, isClosure, value, target, path);
    }

    /// <summary><c>[</c> entrée { <c>,</c> entrée } <c>]</c>, une entrée étant <c>clé</c> ou <c>clé: valeur</c>.</summary>
    private MetadataBlock _ParseMetadataBlock()
    {
        var open = _Consume();
        List<MetadataEntry> entries = [];
        while (!_ExpectOneOf(TokenKind.CloseBracket, TokenKind.EndOfFile))
        {
            var key = _ParseName("clé de métadonnée", "par exemple : [db, cache: \"5min\"]");
            IExpression? value = _ExpectAndConsume(TokenKind.Colon) ? _ParseExpression() : null;
            entries.Add(new MetadataEntry(value is null ? key.Span : key.Span.Union(value.Span), key, value));
            if (!_ExpectAndConsume(TokenKind.Comma)) break;
        }
        _ExpectOrReport(TokenKind.CloseBracket, "pour fermer les métadonnées");
        return new MetadataBlock(open.Span.Union(_previous!.Span), entries);
    }

    /// <summary>
    /// Une déclaration précédée de notes : les notes sont lues, la déclaration qui suit aussi, et elles s'y attachent (à la dernière des instructions que
    /// l'élément a produites : <c>export gear f()</c> en produit deux). Des notes devant autre chose qu'une déclaration sont E125.
    /// </summary>
    private void _ParseNotedDeclaration(RArray<IStatement> statements, ref bool hasTrain)
    {
        var notes = _ParseNotes();
        RArray<IStatement> parsed = new();
        _ParseTopLevelItem(parsed, ref hasTrain);

        List<IStatement> items = [.. parsed];
        if (items.Count > 0)
        {
            var attached = items[^1] switch
            {
                GearDeclaration gear => gear with { Notes = notes },
                MoldDeclaration mold => mold with { Notes = notes },
                EnumDeclaration enumeration => enumeration with { Notes = notes },
                IngotDeclaration ingot => ingot with { Notes = notes },
                VarDeclaration { IsConst: true } constant => constant with { Notes = notes },
                _ => null as IStatement,
            };
            if (attached is null)
                _Error(notes.Span, DiagnosticCodes.InvalidAnnotation, "des annotations ne se posent que devant un gear, un mold, un enum, un ingot ou une constante", "devant autre chose",
                    "elles se placent juste avant la déclaration qu'elles décrivent");
            else items[^1] = attached;
        }
        foreach (var item in items) statements.Push(item);
    }
}
