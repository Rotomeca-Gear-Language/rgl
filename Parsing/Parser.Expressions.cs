using Rgl.AST.Collections;
using Rgl.AST.Expressions;
using Rgl.AST.Types;
using Rgl.AST.Interfaces;
using Rgl.AST.Statements;
using Rgl.AST;
using Rgl.Diagnostics;
using Rgl.Syntax;
using Rgl.Text;
using Rotomeca.Core.Collections;
using Rotomeca.Utils.Types;

namespace Rgl.Parsing;

internal sealed partial class Parser
{
    /// <summary>
    /// Expression, par parsing de Pratt : un opérande, puis tant que l'opérateur qui suit lie
    /// plus fort que <paramref name="minPrecedence"/>, l'opérateur et son opérande droit. Les
    /// précédences et associativités viennent de <see cref="SyntaxFacts.GetBinaryOperator"/>.
    /// </summary>
    /// <param name="minPrecedence">Précédence que l'opérateur suivant doit dépasser pour être pris ici.</param>
    private IExpression _ParseExpression(int minPrecedence = 0)
    {
        var left = _ParsePrefix();

        while (true)
        {
            // « a<-5 » dans une expression : « <- » n'est pas un opérateur, il est relu « a < -5 » (W008).
            if (_Expect(TokenKind.Assign) && _LessPrecedence > minPrecedence)
            {
                left = _ParseGluedAssign(left);
                continue;
            }

            // « a not eq b » : deux mots, lus comme « not (a eq b) ».
            if (_Expect(TokenKind.NotKeyword) && _Future().Kind == TokenKind.EqKeyword && _EqPrecedence > minPrecedence)
            {
                var not = _Consume();
                var eq = _Consume();
                var right = _ParseExpression(_EqPrecedence);
                var span = left.Span.Union(right.Span);
                left = new UnaryExpression(span, not.Kind, new BinaryExpression(span, left, TokenKind.EqKeyword, right, not.Span.Union(eq.Span)));
                continue;
            }

            // Pipeline : le moins lié de tous ; v |> g(a) vaut g(v, a).
            if (_Expect(TokenKind.Pipe) && minPrecedence < PipelinePrecedence)
            {
                left = _ParsePipe(left);
                continue;
            }

            // Parcours, au même niveau : nums ||> double, nums |!|> verifier.
            if (_ExpectOneOf(TokenKind.EachPipe, TokenKind.EachStrictPipe) && minPrecedence < PipelinePrecedence)
            {
                var each = _Consume();
                var target = _ParseEachTarget();
                left = new EachExpression(left.Span.Union(target.Span), left, each.Kind == TokenKind.EachStrictPipe, target, each.Span);
                continue;
            }

            // Parcours des lignes d'un mold en colonnes : particles |S|> damp.
            if (_ExpectOneOf(TokenKind.SoaPipe, TokenKind.SoaShortestPipe) && minPrecedence < PipelinePrecedence)
            {
                var rows = _Consume();
                var target = _ParseEachTarget();
                left = new SoaEachExpression(left.Span.Union(target.Span), left, rows.Kind == TokenKind.SoaShortestPipe, target, rows.Span);
                continue;
            }

            // Accumulation, au même niveau : notes |+> 0 with ajouter.
            if (_Expect(TokenKind.FoldPipe) && minPrecedence < PipelinePrecedence)
            {
                var fold = _Consume();
                var (seed, target) = _ParseFold();
                left = new FoldExpression(left.Span.Union(target.Span), left, seed, target, fold.Span);
                continue;
            }

            // p with {pos.x: 3} : une copie dont certaines parties changent. « with » est contextuel, et
            // seul « with { » compte : celui d'une accumulation est suivi d'une étape.
            if (_Expect(TokenKind.Identifier, "with") && _Future().Kind == TokenKind.OpenBrace && minPrecedence < TernaryPrecedence)
            {
                left = _ParseWith(left);
                continue;
            }

            // « !> » hors d'un train : signalé, puis lu et ignoré pour ne pas perdre la suite.
            if (_Expect(TokenKind.Rail) && !_inTrain)
            {
                _SkipRailOutsideTrain();
                continue;
            }

            // « a ?: b » : symbole réservé, signalé ; la valeur de droite est lue pour ne pas perdre la suite.
            if (_Expect(TokenKind.QuestionColon))
            {
                _ReportReservedSymbol(_Consume());
                _ParseExpression(TernaryPrecedence);
                continue;
            }

            // T is Vec2, T accept Vec2 : un test sur un type, connu à la compilation.
            if ((_Expect(TokenKind.IsKeyword) || _Expect(TokenKind.Identifier, "accept")) && left is NameExpression subject && _EqPrecedence > minPrecedence)
            {
                var structural = _Consume().Kind == TokenKind.Identifier;
                var pattern = _ParseType();
                left = new TypeTestExpression(left.Span.Union(pattern.Span), new NamedType(subject.Name), pattern, structural);
                continue;
            }

            // Cast : juste sous les opérateurs préfixes ; « -x as! int » se lit « (-x) as! int ».
            if (_Expect(TokenKind.AsKeyword) && minPrecedence < CastPrecedence)
            {
                left = _ParseCast(left);
                continue;
            }

            // Ternaire : moins lié que la coalescence, plus que le pipeline ; associatif à droite.
            if (_Expect(TokenKind.Question) && minPrecedence < TernaryPrecedence)
            {
                left = _ParseConditional(left);
                continue;
            }

            var op = SyntaxFacts.GetBinaryOperator(_Current.Kind);
            if (!op.HasValue || op.Value.Precedence <= minPrecedence) return left;

            var token = _Consume();
            var operand = _ParseExpression(op.Value.IsRightAssociative ? op.Value.Precedence - 1 : op.Value.Precedence);
            left = new BinaryExpression(left.Span.Union(operand.Span), left, op.Value.Kind, operand, token.Span);
        }
    }

    /// <summary>
    /// <c>Valeur "with" "{" [ Chemin ":" Expression { "," Chemin ":" Expression } [ "," ] ] "}"</c>, où le chemin est
    /// un nom de champ suivi d'accès <c>.champ</c> et <c>[index]</c>.
    /// </summary>
    private WithExpression _ParseWith(IExpression source)
    {
        _Consume();
        _Consume();
        RArray<WithChange> changes = new();

        while (!_ExpectOneOf(TokenKind.CloseBrace, TokenKind.EndOfFile))
        {
            var name = _ParseName("nom de champ", "un changement s'écrit par exemple : p with {pos.x: 3.0}");
            var path = _ParsePostfix(new NameExpression(name));
            IExpression value;
            if (_ExpectOrReport(TokenKind.Colon, "après le chemin du changement", "un changement s'écrit par exemple : p with {hp: 10}")) value = _ParseExpression();
            else value = new MissingExpression(_MissingAtCurrent());
            changes.Push(new WithChange(path.Span.Union(value.Span), path, value));
            if (!_ExpectAndConsume(TokenKind.Comma)) break;
        }

        if (!_ExpectOrReport(TokenKind.CloseBrace, "pour fermer le with", "les changements se séparent par « , » : p with {x: 1, y: 2}"))
            _SkipToCloseBrace();
        return new WithExpression(source.Span.Union(_previous!.Span), source, [.. changes]);
    }

    /// <summary>
    /// Précédence du ternaire : il n'est lu que si la précédence minimale est en dessous. Les
    /// opérandes de la coalescence (niveau 3, associative à droite) sont lus au-dessus : le
    /// ternaire englobe donc la coalescence, comme le veut la table de précédence.
    /// </summary>
    private const int TernaryPrecedence = 2;

    /// <summary>
    /// Précédence du pipeline, sous le ternaire : il n'est lu que si la précédence minimale est
    /// en dessous. La valeur de départ d'un train est lue au-dessus : son <c>|&gt;</c> commence les
    /// étapes du train, pas un pipeline dans l'expression.
    /// </summary>
    internal const int PipelinePrecedence = 1;

    /// <summary>
    /// Un rail <c>!&gt;</c> dans une expression, hors d'un train (E069) : ses étapes sont lues comme
    /// celles d'un rail de train, puis ignorées.
    /// </summary>
    private void _SkipRailOutsideTrain()
    {
        _Error(_Current.Span, DiagnosticCodes.RailOutsideTrain, "un rail « !> » n'existe que dans un train", "hors d'un train",
            "un gear ne gère pas les erreurs de ses appels : il les laisse remonter jusqu'au train",
            "écris le rail à la fin du train, par exemple : train |> calcul |> afficher !> signaler;");

        _inTrain = true;
        while (_Expect(TokenKind.Rail)) _WithRail(new AST.Steps.MissingStep(_Current.Span));
        _inTrain = false;
    }

    /// <summary>Précédence des casts <c>as</c>, <c>as?</c> et <c>as!</c> : sous les opérateurs préfixes, au-dessus de la multiplication.</summary>
    private const int CastPrecedence = 15;

    /// <summary><c>valeur ( "as" | "as?" | "as!" ) Type</c>, la valeur déjà lue.</summary>
    private CastExpression _ParseCast(IExpression value)
    {
        _Consume();
        var kind = CastKind.Checked;
        if (_ExpectAndConsume(TokenKind.Question)) kind = CastKind.Optional;
        else if (_ExpectAndConsume(TokenKind.Bang)) kind = CastKind.Forced;

        var type = _ParseType();
        return new CastExpression(value.Span.Union(type.Span), value, kind, type);
    }

    /// <summary><c>valeur |&gt; gear(…)</c> ou <c>valeur |&gt; (x) =&gt; …</c>, la valeur déjà lue.</summary>
    private IExpression _ParsePipe(IExpression value)
    {
        var pipe = _Consume();
        if (_Expect(TokenKind.OpenParen) && _Future(_AfterGroup(0)).Kind == TokenKind.FatArrow)
        {
            var lambda = _ParseLambda(PipelinePrecedence);
            return new LambdaPipeExpression(value.Span.Union(lambda.Span), value, lambda, pipe.Span);
        }
        var gear = _ParseQualifiedName("nom de gear", "le pipeline appelle un gear, par exemple : n |> twice");
        RArray<IExpression> arguments = new();

        if (_ExpectAndConsume(TokenKind.OpenParen))
        {
            if (!_Expect(TokenKind.CloseParen))
            {
                do arguments.Push(_ParseExpression());
                while (_ExpectAndConsume(TokenKind.Comma));
            }
            _ExpectOrReport(TokenKind.CloseParen, $"pour fermer les arguments de « {gear.Name} »");
        }

        return new PipeExpression(value.Span.Union(_previous!.Span), value, gear, [.. arguments], pipe.Span);
    }

    /// <summary>
    /// <c>ÉtapeDeParcours := Lambda | Identificateur [ "(" arguments ")" ]</c> : ce qu'un <c>||&gt;</c> ou un
    /// <c>|!|&gt;</c> applique à chaque élément. Le corps d'une lambda s'arrête avant le parcours suivant :
    /// dans <c>a ||&gt; (x) =&gt; x * 2 ||&gt; g</c>, <c>||&gt; g</c> appartient au parcours.
    /// </summary>
    private EachTarget _ParseEachTarget()
    {
        if (_Expect(TokenKind.OpenParen) && _Future(_AfterGroup(0)).Kind == TokenKind.FatArrow)
        {
            var lambda = _ParseLambda(PipelinePrecedence);
            return new EachTarget(lambda.Span, null, [], lambda);
        }

        var name = _ParseQualifiedName("nom de gear", "un parcours applique un gear, un guard ou une lambda à chaque élément, par exemple : nums ||> double");
        RArray<IExpression> arguments = new();
        if (_ExpectAndConsume(TokenKind.OpenParen))
        {
            if (!_Expect(TokenKind.CloseParen))
            {
                do arguments.Push(_ParseExpression(PipelinePrecedence));
                while (_ExpectAndConsume(TokenKind.Comma));
            }
            _ExpectOrReport(TokenKind.CloseParen, $"pour fermer les arguments de « {name.Name} »");
        }
        return new EachTarget(name.Span.Union(_previous!.Span), name, [.. arguments], null);
    }

    /// <summary>
    /// <c>Accumulation := "|+&gt;" Expression "with" ÉtapeDeParcours</c>, après le <c>|+&gt;</c> : la valeur
    /// de départ, lue au-dessus du pipeline, puis l'étape. <c>with</c> est un mot contextuel.
    /// </summary>
    private (IExpression Seed, EachTarget Target) _ParseFold()
    {
        var seed = _ParseExpression(PipelinePrecedence);
        if (_Expect(TokenKind.Identifier, "with")) _Consume();
        else
            _Error(_MissingAfterPrevious(), DiagnosticCodes.ExpectedToken, "« with » attendu entre la valeur de départ et l'étape", "« with » manquant ici",
                "une accumulation s'écrit : notes |+> 0 with (acc, n) => acc + n");
        return (seed, _ParseEachTarget());
    }

    /// <summary><c>condition ? siVrai : siFaux</c>, la condition déjà lue ; « si faux » peut être un autre ternaire.</summary>
    private ConditionalExpression _ParseConditional(IExpression condition)
    {
        _Consume();
        var whenTrue = _ParseExpression();
        _ExpectOrReport(TokenKind.Colon, "entre les deux valeurs du ternaire", "un ternaire s'écrit : condition ? siVrai : siFaux");
        var whenFalse = _ParseExpression(TernaryPrecedence - 1);
        return new ConditionalExpression(condition.Span.Union(whenFalse.Span), condition, whenTrue, whenFalse);
    }

    private static int _LessPrecedence => SyntaxFacts.GetBinaryOperator(TokenKind.Less).Value.Precedence;
    private static int _EqPrecedence => SyntaxFacts.GetBinaryOperator(TokenKind.EqKeyword).Value.Precedence;

    /// <summary>
    /// <c>&lt;-</c> rencontré dans une expression. L'affectation n'existe qu'en début
    /// d'instruction : ici, <c>a&lt;-5</c> se lit <c>a &lt; -5</c>, avec l'avertissement W008.
    /// </summary>
    private IExpression _ParseGluedAssign(IExpression left)
    {
        var arrow = _Consume();
        var minus = new SourceSpan(_source, arrow.Span.Start + RUint.One, RUint.One);

        _diagnosticBag.Warning(arrow.Span, DiagnosticCodes.GluedAssignInExpression,
            "« <- » dans une expression est lu comme une comparaison avec un négatif", "lu comme « < - »",
            ["l'affectation « <- » n'est possible qu'en début d'instruction",
             "pour comparer à un négatif, ajoute des espaces pour lever l'ambiguïté : « a < -5 »"]);

        var operand = _ParseExpression(SyntaxFacts.PrefixPrecedence);
        var negative = new UnaryExpression(minus.Union(operand.Span), TokenKind.Minus, operand);
        // Le « < » est le premier caractère de « <- », le « - » le second.
        var less = new SourceSpan(_source, arrow.Span.Start, RUint.One);
        return new BinaryExpression(left.Span.Union(negative.Span), left, TokenKind.Less, negative, less);
    }

    /// <summary>
    /// Précédence sous laquelle aucun opérateur n'est lu : la cible d'une affectation de champ
    /// (<c>pos.x &lt;- 3.0</c>) s'arrête avant le <c>&lt;-</c>.
    /// </summary>
    private static int _AssignPrecedence => _LessPrecedence;

    /// <summary>Opérateur préfixe et son opérande (<c>-x</c>, <c>not ok</c>), ou opérande simple suivi de ses accès <c>.champ</c>.</summary>
    private IExpression _ParsePrefix()
    {
        if (_Expect(TokenKind.AwaitKeyword)) return _ParseAwait();
        if (_Expect(TokenKind.Identifier, "task") && _Future().Kind == TokenKind.Identifier) return _ParseTaskCall();
        if (AllowNativeCalls && _Expect(TokenKind.Identifier, "native") && _Future().Kind == TokenKind.Identifier) return _ParseNativeCall();
        if (!SyntaxFacts.IsPrefixOperator(_Current.Kind)) return _ParsePostfix(_ParsePrimary());
        if (_IsIncrement(_Current.Kind)) return _ParseIncrement();

        var op = _Consume();
        var operand = _ParseExpression(SyntaxFacts.PrefixPrecedence);
        return new UnaryExpression(op.Span.Union(operand.Span), op.Kind, operand);
    }

    /// <summary>
    /// <c>await Opérande</c> : attend une promesse. Comme un opérateur préfixe, il lie plus fort que les opérateurs binaires
    /// (<c>await a + await b</c> attend a, attend b, puis additionne) ; l'opérande est lu avec ses appels et ses accès.
    /// </summary>
    private AwaitExpression _ParseAwait()
    {
        var keyword = _Consume();
        var operand = _ParseExpression(SyntaxFacts.PrefixPrecedence);
        return new AwaitExpression(keyword.Span.Union(operand.Span), operand);
    }

    /// <summary><c>task appel</c> : l'appel qui suit s'exécute sur le pool de threads ; comme <c>await</c>, il lie plus fort que les opérateurs binaires.</summary>
    private TaskCallExpression _ParseTaskCall()
    {
        var keyword = _Consume();
        var call = _ParseExpression(SyntaxFacts.PrefixPrecedence);
        return new TaskCallExpression(keyword.Span.Union(call.Span), call);
    }

    /// <summary>Vrai si <c>native f(x)</c> est lu comme l'appel d'une fonction C : seulement dans les liaisons que le compilateur engendre.</summary>
    internal bool AllowNativeCalls { get; init; }

    /// <summary><c>native appel</c> : l'appel d'une fonction C, dans le texte que le compilateur engendre d'un fichier <c>forge: native</c>.</summary>
    private NativeCallExpression _ParseNativeCall()
    {
        var keyword = _Consume();
        var call = _ParseExpression(SyntaxFacts.PrefixPrecedence);
        return new NativeCallExpression(keyword.Span.Union(call.Span), call);
    }

    private static bool _IsIncrement(TokenKind kind) =>
        kind is TokenKind.IncrementKeyword or TokenKind.DecrementKeyword or TokenKind.PostIncrementKeyword or TokenKind.PostDecrementKeyword;

    /// <summary>
    /// <c>increment [ "&lt;" EntierLittéral "&gt;" ] Opérande</c>, et de même pour <c>decrement</c>,
    /// <c>post_increment</c> et <c>post_decrement</c>.
    /// </summary>
    private IncrementExpression _ParseIncrement()
    {
        var keyword = _Consume();
        LiteralExpression? step = null;

        if (_ExpectAndConsume(TokenKind.Less))
        {
            if (_Expect(TokenKind.IntegerLiteral))
            {
                var literal = _Consume();
                step = new LiteralExpression(literal.Span, literal.Kind);
            }
            else
                _Error(_MissingAtCurrent(), DiagnosticCodes.ExpectedToken, "nombre entier attendu entre les chevrons", "nombre manquant ici",
                    $"le pas s'écrit avec un entier littéral, par exemple : {keyword.Text}<2> compteur");

            _ExpectOrReport(TokenKind.Greater, "pour fermer le pas");
        }

        var operand = _ParseExpression(SyntaxFacts.PrefixPrecedence);
        return new IncrementExpression(keyword.Span.Union(operand.Span), keyword.Kind, step, operand);
    }

    /// <summary>
    /// <c>"&lt;" Namespace "&gt;"</c> après un point : le namespace de l'extension appelée, <c>v.&lt;Mon/Ns&gt;f()</c>. Les segments
    /// sont collés les uns aux autres.
    /// </summary>
    private string _ParseNamespaceQualifier()
    {
        _Consume();
        var first = _Current;
        var any = false;
        while (!_ExpectOneOf(TokenKind.Greater, TokenKind.EndOfFile, TokenKind.Semicolon, TokenKind.OpenParen))
        {
            _Consume();
            any = true;
        }
        var text = any ? first.Span.Union(_previous!.Span).Text : "";
        if (!_IsNamespaceName(text))
            _Error(first.Span, DiagnosticCodes.InvalidNamespace, "un namespace s'écrit avec des lettres, des chiffres, _ et -, séparés par /", "namespace invalide",
                "par exemple : valeur.<Mon/Namespace>nom()");
        _ExpectOrReport(TokenKind.Greater, "pour fermer le namespace");
        return text;
    }

    /// <summary>Vrai si <paramref name="text"/> est un namespace : des segments de lettres, chiffres, <c>_</c> et <c>-</c>, séparés par <c>/</c>.</summary>
    private static bool _IsNamespaceName(string text) =>
        text.Length > 0 && text.Split('/').All(segment => segment.Length > 0 && segment.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'));

    /// <summary>
    /// Les accès <c>.champ</c>, <c>?.champ</c>, <c>[index]</c>, les appels <c>.nom(…)</c> et le <c>°</c>
    /// d'un ingot, qui suivent un opérande, les plus liés de tous : <c>a.b?.c</c>, <c>t[0].x</c>, <c>uid°</c>. Un <c>++</c> ou <c>--</c> collé derrière est
    /// signalé (E076).
    /// </summary>
    private IExpression _ParsePostfix(IExpression operand)
    {
        while (true)
        {
            if (_Expect(TokenKind.Less) && _SkipGroup(0, TokenKind.Less, TokenKind.Greater) is >= 0 and var typeEnd && _Future(typeEnd).Kind == TokenKind.Dot)
            {
                var (arguments, end) = _ParseTypeArguments();
                operand = new GenericTypeExpression(operand.Span.Union(_previous!.Span), operand, arguments);
                continue;
            }
            if (_ExpectOneOf(TokenKind.Dot, TokenKind.QuestionDot))
            {
                var safe = _Consume().Kind == TokenKind.QuestionDot;
                var space = !safe && _Expect(TokenKind.Less) ? _ParseNamespaceQualifier() : null;
                // Après un point, « match » est le nom d'une méthode (r.match(s)), pas l'expression match.
                var member = _Expect(TokenKind.MatchKeyword) ? new Identifier(_Consume().Span) : _ParseName("nom de champ", "un champ se lit par exemple : pos.x");
                operand = new MemberAccessExpression(operand.Span.Union(member.Span), operand, member, safe, space);
                // nums.push(4) : un appel sur une valeur.
                if (!safe && _Expect(TokenKind.OpenParen)) operand = _ParseCall(operand);
                continue;
            }

            // a[i] : un élément par son index, ou une valeur par sa clé.
            if (_Expect(TokenKind.OpenBracket))
            {
                _Consume();
                var index = _ParseExpression();
                _ExpectOrReport(TokenKind.CloseBracket, "pour fermer l'index", "un élément se lit par exemple : nums[0]");
                operand = new IndexExpression(operand.Span.Union(_previous!.Span), operand, index);
                continue;
            }

            if (_ExpectOneOf(TokenKind.PlusPlus, TokenKind.MinusMinus))
            {
                _ReportReservedSymbol(_Consume());
                continue;
            }

            // ops[0](3), fabrique(2)(5), (f)(1) : l'appel d'une valeur, une lambda.
            if (_Expect(TokenKind.OpenParen) && operand is IndexExpression or CallExpression or ParenthesizedExpression)
            {
                operand = _ParseCall(operand);
                continue;
            }

            // uid° : la valeur de l'ingot sans sa marque.
            if (_Expect(TokenKind.Degree))
            {
                operand = new UnbrandExpression(operand.Span.Union(_Consume().Span), operand);
                continue;
            }

            return operand;
        }
    }

    /// <summary>
    /// Signale (E076) un symbole réservé : <c>++</c>, <c>--</c> ou <c>?:</c>, avec ce que RGL utilise à la place.
    /// </summary>
    private void _ReportReservedSymbol(Token symbol)
    {
        var help = symbol.Kind switch
        {
            TokenKind.PlusPlus => "pour ajouter 1, écris increment v (nouvelle valeur) ou post_increment v (ancienne valeur)",
            TokenKind.MinusMinus => "pour retirer 1, écris decrement v (nouvelle valeur) ou post_decrement v (ancienne valeur)",
            _ => "RGL n'a pas de « ?: » : écris a ?? b si a peut être null, a ?° b si a peut être vide, a or b pour deux bool",
        };
        _Error(symbol.Span, DiagnosticCodes.ReservedSymbol, $"« {symbol.Text} » n'existe pas en RGL", "symbole réservé", help);
    }

    /// <summary>Indique si le token courant peut commencer une expression.</summary>
    private bool _CanStartExpression() =>
        _IsLiteral(_Current.Kind) || SyntaxFacts.IsPrefixOperator(_Current.Kind)
        || _Current.Kind is TokenKind.CppBlock or TokenKind.Identifier or TokenKind.OpenParen or TokenKind.OpenBracket or TokenKind.Dollar or TokenKind.NewKeyword or TokenKind.MatchKeyword or TokenKind.SpawnKeyword or TokenKind.AwaitKeyword;

    /// <summary>
    /// Littéral, nom (éventuellement appelé : <c>f(a)</c>), expression entre parenthèses, ou valeur
    /// de mold (<c>{x: 1}</c>, <c>new Vec2 {…}</c>).
    /// </summary>
    private IExpression _ParsePrimary()
    {
        if (_Expect(TokenKind.CppBlock)) return _ParseCpp();
        if (_Expect(TokenKind.OpenBrace)) return _ParseMoldValue(null, _Current);
        if (_Expect(TokenKind.OpenBracket)) return _ParseCollection();
        if (_Expect(TokenKind.MatchKeyword)) return _ParseMatch();
        if (_ExpectOneOf(TokenKind.PlusPlus, TokenKind.MinusMinus))
        {
            _ReportReservedSymbol(_Consume());
            return _ParsePrefix();
        }
        if (_AtSpawn()) return _ParseSpawn();
        if (_Expect(TokenKind.NewKeyword))
        {
            var keyword = _Consume();
            var type = _ParseType();
            if (_Expect(TokenKind.OpenBrace)) return _ParseMoldValue(type, keyword);

            _ExpectOrReport(TokenKind.OpenBrace, "après le type de new", "new crée un mold, par exemple : new Vec2 {x: 1.0, y: 2.0}");
            return new MoldExpression(keyword.Span.Union(_previous!.Span), type, []);
        }

        if (_Expect(TokenKind.TemplateStringLiteral)) return _ParseTemplate(_Consume());
        if (_Expect(TokenKind.Dollar)) return new DollarExpression(_Consume().Span);

        if (_IsLiteral(_Current.Kind))
        {
            var literal = _Consume();
            return new LiteralExpression(literal.Span, literal.Kind);
        }

        if (_Expect(TokenKind.Identifier))
        {
            IExpression name = new NameExpression(new Identifier(_Consume().Span));
            // f<int>(x) : un appel avec ses arguments génériques écrits.
            if (_Expect(TokenKind.Less) && _SkipGroup(0, TokenKind.Less, TokenKind.Greater) is >= 0 and var after && _Future(after).Kind == TokenKind.OpenParen)
            {
                var (typeArguments, _) = _ParseTypeArguments();
                return _ParseCall(name, typeArguments);
            }
            return _Expect(TokenKind.OpenParen) ? _ParseCall(name) : name;
        }

        if (SyntaxFacts.IsPrimitiveType(_Current.Kind))
        {
            var keyword = _Consume();
            // int.max, double.min : les bornes d'un type numérique se lisent sur son nom.
            if (_Expect(TokenKind.Dot) && _Future().Kind == TokenKind.Identifier)
                return new NameExpression(new Identifier(keyword.Span));
            _Error(keyword.Span, DiagnosticCodes.TypeNameAsIdentifier, $"« {keyword.Text} » est un nom de type, pas une valeur", "nom de type",
                "un nom de type ne peut pas servir de nom de variable ni de valeur");
            return new NameExpression(new Identifier(keyword.Span));
        }

        if (_Expect(TokenKind.ReservedKeyword))
        {
            var word = _Consume();
            _ReportReserved(word);
            return new NameExpression(new Identifier(word.Span));
        }

        if (_Expect(TokenKind.OpenParen)) return _Future(_AfterGroup(0)).Kind == TokenKind.FatArrow ? _ParseLambda() : _ParseParenthesizedOrTuple();

        var missing = new MissingExpression(_MissingAtCurrent());
        _Error(missing.Span, DiagnosticCodes.ExpectedExpression, "expression attendue", "expression manquante ici",
            "vous pouvez écrire un littéral (0, \"texte\"), un nom ou une expression entre parenthèses");
        return missing;
    }

    /// <summary>
    /// <c>Lambda := "(" [ [ Type ] nom { "," [ Type ] nom } ] ")" "=&gt;" ( Bloc | Expression )</c>.
    /// Un paramètre sans type le reçoit du type attendu. Après la flèche, une <c>{</c> ouvre toujours
    /// un bloc : une lambda qui renvoie un mold écrit <c>=&gt; ({x: 1})</c> ou <c>=&gt; new P {…}</c>.
    /// </summary>
    /// <param name="bodyPrecedence">
    /// Précédence minimale du corps expression : 0 en général ; au-dessus du pipeline pour l'étape d'un
    /// parcours, que le parcours suivant ne doit pas rejoindre.
    /// </param>
    private LambdaExpression _ParseLambda(int bodyPrecedence = 0)
    {
        var open = _Consume();
        RArray<LambdaParameter> parameters = new();
        if (!_Expect(TokenKind.CloseParen))
        {
            do
            {
                // « mut » d'abord ; « v » seul (suivi de « , » ou « ) ») : sans type ; sinon un type, puis le nom.
                var mut = _Expect(TokenKind.MutKeyword) ? _Consume() : null;
                ITypeNode? type = _Expect(TokenKind.Identifier) && _Future().Kind is TokenKind.Comma or TokenKind.CloseParen ? null : _ParseType();
                var name = _ParseName("nom de paramètre", "un paramètre de lambda s'écrit avec son type puis son nom, par exemple : (int v) => v * 2");
                var start = mut?.Span ?? type?.Span ?? name.Span;
                parameters.Push(new LambdaParameter(start.Union(name.Span), type, name, mut is not null));
            }
            while (_ExpectAndConsume(TokenKind.Comma));
        }
        _ExpectOrReport(TokenKind.CloseParen, "pour fermer les paramètres de la lambda");
        _ExpectOrReport(TokenKind.FatArrow, "après les paramètres de la lambda", "par exemple : (int v) => v * 2");

        // « => {x: 1} » : une valeur de mold, pas un bloc ; signalée, puis lue comme valeur.
        if (_Expect(TokenKind.OpenBrace) && _LooksLikeMoldValue())
        {
            var mold = _ParseMoldValue(null, _Current);
            _Error(mold.Span, DiagnosticCodes.ExpectedToken, "après « => », une accolade ouvre le bloc de la lambda", "lu comme un bloc",
                $"pour renvoyer un mold, entoure-le de parenthèses : => ({mold.Span.Text})");
            return new LambdaExpression(open.Span.Union(mold.Span), [.. parameters], mold, null);
        }
        if (_Expect(TokenKind.OpenBrace))
        {
            var body = _ParseBlock();
            return new LambdaExpression(open.Span.Union(body.Span), [.. parameters], null, body);
        }
        var expression = _ParseExpression(bodyPrecedence);
        return new LambdaExpression(open.Span.Union(expression.Span), [.. parameters], expression, null);
    }

    /// <summary>
    /// Indique si la <c>{</c> courante commence une valeur de mold plutôt qu'un bloc : <c>{...p}</c>, ou
    /// <c>{x: …</c> dont la valeur finit par <c>,</c> ou <c>}</c> (un bloc aurait <c>x: T &lt;- v;</c>).
    /// </summary>
    private bool _LooksLikeMoldValue()
    {
        if (_Future().Kind == TokenKind.DotDotDot) return true;
        if (_Future().Kind != TokenKind.Identifier || _Future(2).Kind != TokenKind.Colon) return false;

        var offset = 3;
        while (true)
        {
            switch (_Future(offset).Kind)
            {
                case TokenKind.Comma or TokenKind.CloseBrace:
                    return true;
                case TokenKind.Semicolon or TokenKind.Assign or TokenKind.EndOfFile:
                    return false;
                case TokenKind.OpenParen or TokenKind.OpenBracket or TokenKind.OpenBrace:
                    offset = _AfterGroup(offset);
                    break;
                default:
                    ++offset;
                    break;
            }
        }
    }

    /// <summary>
    /// <c>"(" Expression ")"</c>, ou un tuple <c>"(" [ nom ":" ] Expression "," … ")"</c> : au moins deux
    /// éléments, chacun avec un nom facultatif (<c>(x: 1, y: 2)</c>).
    /// </summary>
    private IExpression _ParseParenthesizedOrTuple()
    {
        var open = _Consume();
        RArray<TupleElement> elements = new();
        do
        {
            Identifier? name = null;
            if (_Expect(TokenKind.Identifier) && _Future().Kind == TokenKind.Colon)
            {
                name = new Identifier(_Consume().Span);
                _Consume();
            }
            var value = _ParseExpression();
            elements.Push(new TupleElement(name is null ? value.Span : name.Span.Union(value.Span), name, value));
        }
        while (_ExpectAndConsume(TokenKind.Comma));

        _ExpectOrReport(TokenKind.CloseParen, "pour fermer la parenthèse");
        var span = open.Span.Union(_previous!.Span);
        IReadOnlyList<TupleElement> items = [.. elements];
        if (items is [{ Name: null } single]) return new ParenthesizedExpression(span, single.Value);

        if (items.Count == 1)
            _Error(span, DiagnosticCodes.ExpectedToken, "un tuple a au moins deux éléments", "un seul élément", "par exemple : (x: 1, y: 2)");
        return new TupleExpression(span, items);
    }

    /// <summary>
    /// Un tableau écrit <c>[a, ...b]</c> (<c>[]</c> vide), ou un dictionnaire <c>[clé: valeur, …]</c>
    /// (<c>[:]</c> vide) : la première paire décide.
    /// </summary>
    private IExpression _ParseCollection()
    {
        var open = _Consume();
        const string help = "un tableau s'écrit [1, 2, ...autres], un dictionnaire [\"a\": 1, \"b\": 2] ([:] s'il est vide)";

        if (_Expect(TokenKind.Colon) && _Future().Kind == TokenKind.CloseBracket)
        {
            _Consume();
            _Consume();
            return new DictionaryExpression(open.Span.Union(_previous!.Span), []);
        }
        if (_ExpectAndConsume(TokenKind.CloseBracket)) return new ArrayExpression(open.Span.Union(_previous!.Span), []);

        var first = _ParseCollectionElement();
        if (first is not SpreadElement && _Expect(TokenKind.DotDot)) return _ParseRange(open, first);
        if (first is not SpreadElement && _ExpectAndConsume(TokenKind.Colon))
        {
            RArray<DictionaryEntry> entries = new();
            var value = _ParseExpression();
            entries.Push(new DictionaryEntry(first.Span.Union(value.Span), first, value));
            while (_ExpectAndConsume(TokenKind.Comma) && !_Expect(TokenKind.CloseBracket))
            {
                var key = _ParseExpression();
                _ExpectOrReport(TokenKind.Colon, "entre la clé et la valeur", help);
                var entryValue = _ParseExpression();
                entries.Push(new DictionaryEntry(key.Span.Union(entryValue.Span), key, entryValue));
            }
            if (!_ExpectOrReport(TokenKind.CloseBracket, "pour fermer le dictionnaire", help)) _SkipToCloseBracket();
            return new DictionaryExpression(open.Span.Union(_previous!.Span), [.. entries]);
        }

        RArray<IExpression> elements = new();
        elements.Push(first);
        while (_ExpectAndConsume(TokenKind.Comma) && !_Expect(TokenKind.CloseBracket)) elements.Push(_ParseCollectionElement());
        if (!_ExpectOrReport(TokenKind.CloseBracket, "pour fermer le tableau", help)) _SkipToCloseBracket();
        return new ArrayExpression(open.Span.Union(_previous!.Span), [.. elements]);
    }

    /// <summary>
    /// <c>Intervalle := "[" Expression ".." [ "&lt;" ] Expression [ "by" Expression ] "]"</c>, le début déjà lu.
    /// <c>by</c> est un mot contextuel. <c>[0..&lt;-5]</c> est lexé avec <c>&lt;-</c> : lu comme <c>..&lt;</c> puis <c>-5</c>.
    /// </summary>
    private RangeExpression _ParseRange(Token open, IExpression start)
    {
        _Consume();
        var exclusive = _ExpectAndConsume(TokenKind.Less);
        IExpression end;
        if (!exclusive && _Expect(TokenKind.Assign))
        {
            var glued = _Consume();
            exclusive = true;
            var operand = _ParseExpression(SyntaxFacts.PrefixPrecedence);
            end = new UnaryExpression(glued.Span.Union(operand.Span), TokenKind.Minus, operand);
        }
        else end = _ParseExpression();

        IExpression? step = null;
        if (_Expect(TokenKind.Identifier, "by"))
        {
            _Consume();
            step = _ParseExpression();
        }

        if (!_ExpectOrReport(TokenKind.CloseBracket, "pour fermer l'intervalle", "un intervalle s'écrit [0..10], [0..<10] ou [0..10 by 2]")) _SkipToCloseBracket();
        return new RangeExpression(open.Span.Union(_previous!.Span), start, end, exclusive, step);
    }

    /// <summary>Un élément de tableau écrit : une valeur, ou <c>...tableau</c>.</summary>
    private IExpression _ParseCollectionElement()
    {
        if (!_Expect(TokenKind.DotDotDot)) return _ParseExpression();
        var dots = _Consume();
        var value = _ParseExpression();
        return new SpreadElement(dots.Span.Union(value.Span), value);
    }

    /// <summary>Après une erreur dans un tableau écrit : saute jusqu'à son <c>]</c> (compris), sans dépasser l'instruction.</summary>
    private void _SkipToCloseBracket()
    {
        var depth = 0;
        while (!_ExpectOneOf(TokenKind.EndOfFile, TokenKind.Semicolon))
        {
            var token = _Consume();
            if (token.Kind == TokenKind.OpenBracket) ++depth;
            else if (token.Kind == TokenKind.CloseBracket && depth-- == 0) return;
        }
    }

    /// <summary>
    /// Découpe un template en texte et en interpolations. <c>\</c> échappe le caractère suivant
    /// (<c>\{</c> et <c>\}</c> pour une accolade) ; chaque <c>{…}</c> contient une expression, lue
    /// par un lexer et un parseur limités à ses accolades. Une accolade ouvrante sans fermante, ou
    /// une fermante seule, donne E045.
    /// </summary>
    private TemplateExpression _ParseTemplate(Token token)
    {
        var content = _source.Content;
        var start = (int)token.Span.Start.Value;
        // Sans accent grave final, le template n'est pas fermé (E021 est déjà signalé par le lexer).
        var end = (int)token.Span.End.Value;
        if (token.Text.Length >= 2 && token.Text[^1] == '`') --end;

        RArray<ITemplatePart> parts = new();
        var textStart = start + 1;

        void PushText(int to)
        {
            if (to > textStart) parts.Push(new TemplateText(_SpanOf(textStart, to)));
        }

        for (var i = start + 1; i < end; ++i)
        {
            switch (content[i])
            {
                case '\\':
                    ++i;
                    break;

                case '{':
                    PushText(i);
                    var close = _FindInterpolationEnd(content, i, end);
                    if (close < 0)
                    {
                        _Error(_SpanOf(i, i + 1), DiagnosticCodes.MalformedTemplate, "« { » sans « } » dans le template", "ouverte ici",
                            "ferme l'expression avec « } », ou écris \\{ pour une accolade dans le texte");
                        return new TemplateExpression(token.Span, [.. parts]);
                    }

                    parts.Push(new TemplateInterpolation(_SpanOf(i, close + 1), _ParseInterpolation(i + 1, close)));
                    i = close;
                    textStart = close + 1;
                    break;

                case '}':
                    _Error(_SpanOf(i, i + 1), DiagnosticCodes.MalformedTemplate, "« } » sans « { » dans le template", "seule",
                        "pour une accolade dans le texte, écris \\}");
                    break;
            }
        }

        PushText(end);
        return new TemplateExpression(token.Span, [.. parts]);
    }

    /// <summary>
    /// L'expression entre <c>[start, end[</c>, le contenu d'une interpolation. Vide : E032 ; suivie
    /// d'autre chose : E030 « } » attendu.
    /// </summary>
    private IExpression _ParseInterpolation(int start, int end)
    {
        var tokens = new Lexer(_source, _diagnosticBag, start, end).Tokenize();
        var parser = new Parser(tokens, _diagnosticBag);

        if (parser._Expect(TokenKind.EndOfFile))
        {
            var missing = new MissingExpression(_SpanOf(start, start));
            _Error(missing.Span, DiagnosticCodes.ExpectedExpression, "expression attendue entre les accolades", "expression manquante ici",
                "une interpolation contient une valeur, par exemple : `total = {a + b}`");
            return missing;
        }

        var expression = parser._ParseExpression();
        if (!parser._Expect(TokenKind.EndOfFile))
            parser._Error(parser._Current.Span, DiagnosticCodes.ExpectedToken, "« } » attendu après l'expression interpolée", "en trop",
                "une interpolation ne contient qu'une seule expression");
        return expression;
    }

    /// <summary>
    /// Indice de l'accolade qui ferme celle ouverte en <paramref name="open"/>, en sautant les
    /// accolades des chaînes et des templates imbriqués de l'expression ; <c>-1</c> si elle n'est
    /// pas fermée avant <paramref name="end"/>.
    /// </summary>
    private static int _FindInterpolationEnd(string content, int open, int end)
    {
        var depth = 0;
        for (var i = open; i < end; ++i)
        {
            switch (content[i])
            {
                case '{':
                    ++depth;
                    break;
                case '}':
                    if (--depth == 0) return i;
                    break;
                case '"' or '\'' or '`':
                    i = _FindStringEnd(content, i, end);
                    if (i < 0) return -1;
                    break;
            }
        }
        return -1;
    }

    /// <summary>
    /// Indice du délimiteur qui ferme la chaîne ouverte en <paramref name="open"/> : une chaîne
    /// classique (échappements par <c>\</c>), brute (sans échappement) ou un template (avec ses
    /// interpolations) ; <c>-1</c> si elle n'est pas fermée avant <paramref name="end"/>.
    /// </summary>
    private static int _FindStringEnd(string content, int open, int end)
    {
        var quote = content[open];
        for (var i = open + 1; i < end; ++i)
        {
            var current = content[i];
            if (current == quote) return i;
            if (current == '\\' && quote != '\'') ++i;
            else if (current == '{' && quote == '`')
            {
                i = _FindInterpolationEnd(content, i, end);
                if (i < 0) return -1;
            }
        }
        return -1;
    }

    /// <summary>Span du fichier entre deux indices.</summary>
    private SourceSpan _SpanOf(int start, int end) => new(_source, RUint.CreateChecked(start), RUint.CreateChecked(end - start));

    /// <summary>
    /// <c>ValeurMold := [ "new" Type ] "{" [ Entrée { "," Entrée } [ "," ] ] "}"</c>, avec
    /// <c>Entrée := Identificateur ":" Expression | Identificateur | "..." Expression</c>.
    /// </summary>
    /// <param name="type">Le type écrit après <c>new</c>, ou <see langword="null"/>.</param>
    /// <param name="first">Le premier token de la valeur (<c>new</c> ou l'accolade).</param>
    private MoldExpression _ParseMoldValue(ITypeNode? type, Token first)
    {
        _Consume();
        RArray<IMoldEntry> entries = new();

        while (!_ExpectOneOf(TokenKind.CloseBrace, TokenKind.EndOfFile))
        {
            if (_Expect(TokenKind.DotDotDot))
            {
                var dots = _Consume();
                var value = _ParseExpression();
                entries.Push(new MoldSpreadValue(dots.Span.Union(value.Span), value));
            }
            else
            {
                var name = _ParseName("nom de champ", "une valeur de mold s'écrit par exemple : {x: 1.0, y: 2.0}");
                if (_ExpectAndConsume(TokenKind.Colon))
                {
                    var value = _ParseExpression();
                    entries.Push(new MoldFieldValue(name.Span.Union(value.Span), name, value, false));
                }
                else entries.Push(new MoldFieldValue(name.Span, name, new NameExpression(name), true));
            }

            if (!_ExpectAndConsume(TokenKind.Comma)) break;
        }

        if (!_ExpectOrReport(TokenKind.CloseBrace, "pour fermer la valeur de mold", "les champs se séparent par « , » : {x: 1.0, y: 2.0}"))
            _SkipToCloseBrace();
        return new MoldExpression(first.Span.Union(_previous!.Span), type, [.. entries]);
    }

    /// <summary>
    /// Après une erreur dans une valeur <c>{…}</c> : saute jusqu'à son accolade fermante incluse, pour
    /// que la resynchronisation ne la prenne pas pour celle du bloc. S'arrête avant un <c>;</c>.
    /// </summary>
    private void _SkipToCloseBrace()
    {
        var depth = 0;
        while (!_ExpectOneOf(TokenKind.EndOfFile, TokenKind.Semicolon))
        {
            var token = _Consume();
            if (token.Kind == TokenKind.OpenBrace) ++depth;
            else if (token.Kind == TokenKind.CloseBrace && depth-- == 0) return;
        }
    }

    /// <summary>
    /// <c>Match := "match" "(" Expression ")" "{" { Bras } "}"</c>, avec
    /// <c>Bras := Motif "|&gt;" ( Bloc | InstructionDeSortie | Expression ";" )</c> et
    /// <c>Motif := "default" [ Nom ] | "case" Expression { "," Expression } | "case" "eval" Expression | "ok" [ Nom ] | "err" [ Nom ] | Type [ Nom ]</c> ;
    /// les valeurs d'un <c>case</c> se lisent au-dessus du pipeline, dont le <c>|&gt;</c> ouvre le bras.
    /// </summary>
    private MatchExpression _ParseMatch()
    {
        var keyword = _Consume();
        _ExpectOrReport(TokenKind.OpenParen, "après match", "match s'écrit : match (valeur) { int i |> … }");
        var subject = _ParseExpression();
        _ExpectOrReport(TokenKind.CloseParen, "après la valeur du match");
        RArray<MatchArm> arms = new();

        if (_ExpectOrReport(TokenKind.OpenBrace, "pour ouvrir les bras du match"))
        {
            while (!_ExpectOneOf(TokenKind.CloseBrace, TokenKind.EndOfFile) && !_AtDeclarationStart())
            {
                var before = _position;
                arms.Push(_ParseMatchArm());
                if (_position == before) _Consume();   // rien n'a pu être lu : on avance quand même
            }
            _ExpectOrReport(TokenKind.CloseBrace, "pour fermer le match");
        }

        return new MatchExpression(keyword.Span.Union(_previous!.Span), subject, [.. arms]);
    }

    private MatchArm _ParseMatchArm()
    {
        var first = _Current;
        var kind = MatchPatternKind.Type;
        ITypeNode? type = null;
        RArray<IExpression> values = new();
        IExpression? condition = null;

        if (_ExpectAndConsume(TokenKind.DefaultKeyword)) kind = MatchPatternKind.Default;
        else if (_ExpectAndConsume(TokenKind.CaseKeyword))
        {
            // « eval » est contextuel : suivi d'une condition, il en fait un case eval.
            if (_Expect(TokenKind.Identifier, "eval") && _Future().Kind is not (TokenKind.Pipe or TokenKind.Comma))
            {
                _Consume();
                kind = MatchPatternKind.Eval;
                condition = _ParseExpression(PipelinePrecedence);
            }
            else
            {
                kind = MatchPatternKind.Case;
                do values.Push(_ParseExpression(PipelinePrecedence));
                while (_ExpectAndConsume(TokenKind.Comma));
            }
        }
        else if (_Expect(TokenKind.Identifier, "ok") && _Future().Kind is TokenKind.Identifier or TokenKind.Pipe)
        {
            _Consume();
            kind = MatchPatternKind.Ok;
        }
        else if (_Expect(TokenKind.Identifier, "err") && _Future().Kind is TokenKind.Identifier or TokenKind.Pipe)
        {
            _Consume();
            kind = MatchPatternKind.Err;
        }
        else type = _ParseType();

        Identifier? name = kind is MatchPatternKind.Type or MatchPatternKind.Ok or MatchPatternKind.Err or MatchPatternKind.Default && _Expect(TokenKind.Identifier) ? new Identifier(_Consume().Span) : null;
        _ExpectOrReport(TokenKind.Pipe, "après le motif du bras", "un bras s'écrit par exemple : int i |> i * 2;");

        IExpression? value = null;
        BlockStatement? body = null;
        if (_Expect(TokenKind.OpenBrace))
        {
            body = _ParseBlock();
            _ExpectAndConsume(TokenKind.Semicolon);
        }
        else if (_ExpectOneOf(TokenKind.ReturnKeyword, TokenKind.FailKeyword, TokenKind.BreakKeyword, TokenKind.GotoKeyword)
            || (_Expect(TokenKind.Identifier, "done") && _Future().Kind == TokenKind.Semicolon))
        {
            var statement = _ParseStatement();
            body = new BlockStatement(statement.Span, [statement]);
        }
        else
        {
            value = _ParseExpression();
            if (!_ExpectOrReport(TokenKind.Semicolon, "à la fin du bras", "un bras valeur se termine par « ; »")) _SkipToSemicolon();
        }

        return new MatchArm(first.Span.Union(_previous!.Span), kind, type, name, value, body, kind == MatchPatternKind.Case ? [.. values] : null, condition);
    }

    /// <summary>Arguments d'un appel : <c>"(" [ Expression { "," Expression } ] ")"</c>.</summary>
    private CallExpression _ParseCall(IExpression callee, IReadOnlyList<ITypeNode>? typeArguments = null)
    {
        _Consume();
        RArray<IExpression> arguments = new();

        if (!_Expect(TokenKind.CloseParen))
        {
            do arguments.Push(_ParseExpression());
            while (_ExpectAndConsume(TokenKind.Comma));
        }

        _ExpectOrReport(TokenKind.CloseParen, "pour fermer les arguments de l'appel");
        return new CallExpression(callee.Span.Union(_previous!.Span), callee, [.. arguments], typeArguments);
    }

    /// <summary>Littéraux : nombres, durées, chaînes, symboles, regex, <c>true</c>, <c>false</c>, <c>null</c>.</summary>
    private static bool _IsLiteral(TokenKind kind) => kind is
        TokenKind.IntegerLiteral or TokenKind.FloatLiteral or TokenKind.DurationLiteral
        or TokenKind.StringLiteral or TokenKind.RawStringLiteral or TokenKind.TemplateStringLiteral
        or TokenKind.MutableStringLiteral or TokenKind.SymbolLiteral or TokenKind.RegexLiteral
        or TokenKind.TrueKeyword or TokenKind.FalseKeyword or TokenKind.NullKeyword;
}
