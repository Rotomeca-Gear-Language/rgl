using Rgl.AST;
using Rgl.AST.Enums;
using Rgl.AST.Expressions;
using Rgl.AST.Gears;
using Rgl.AST.Ingots;
using Rgl.AST.InputOutput;
using Rgl.AST.Interfaces;
using Rgl.AST.Molds;
using Rgl.AST.Notes;
using Rgl.AST.Statements;
using Rgl.AST.Variables;
using Rgl.Diagnostics;
using Rgl.IR;
using Rgl.IR.Expressions;
using Rgl.IR.Gears;
using Rgl.IR.Statements;
using Rgl.IR.Symbols;
using Rgl.IR.Types;
using Rgl.Syntax;
using Rgl.Text;

namespace Rgl.Binding;

/// <summary>
/// Les annotations (slice 5.3). <c>@deprecated("message")</c> marque un gear obsolète : chaque appel, chaque nom pris comme valeur avertit (W061), sauf depuis un gear
/// lui-même obsolète. <c>@tap(…)</c> attache un effet de bord à un gear sans changer sa valeur : <c>@tap(before, `→ {n}` -&gt; console)</c> avant lui (<c>$</c> y est son
/// premier paramètre), <c>@tap(`← {$}` -&gt; console)</c> après (<c>$</c> y est ce qu'il renvoie), <c>@tap(closure, after, (int v) =&gt; journal(v))</c> avec une fonction
/// appelée avec <c>$</c> (après) ou avec les paramètres du gear (avant) ; les taps s'exécutent de haut en bas, un <c>before</c> écrit après un <c>after</c> avertit (W062) ;
/// un guard dont le corps échoue n'exécute pas ses taps <c>after</c> ; le gear ne peut pas être <c>pure</c> (un tap a un effet), ni générique, ni avoir de paramètre
/// <c>mut</c> ou <c>this</c> (« pas encore pris en charge »), et un emit n'en a pas. Les métadonnées <c>[…]</c> sont ignorées. <c>@async</c>, <c>@sync</c> et
/// <c>@fire</c> sont reconnues mais pas encore prises en charge. Toute annotation est permise sur un gear ; sur un mold, un enum, un ingot ou une constante,
/// seules les métadonnées le sont.
/// </summary>
internal sealed partial class Binder
{
    /// <summary>Ce que <c>$</c> désigne dans le <c>@tap</c> en cours de liaison ; <see langword="null"/> hors d'un tap, ou quand il n'y a pas de valeur.</summary>
    private IrExpression? _tapValue;

    /// <summary>Le message de <c>@deprecated</c> d'un gear (vide sans message) ; <see langword="null"/> s'il n'est pas obsolète.</summary>
    private string? _DeprecationOf(GearDeclaration gear)
    {
        if (gear.Notes?.Annotations.OfType<DeprecatedAnnotation>().FirstOrDefault() is not { } note) return null;
        if (note.Message is null) return "";
        if (note.Message is LiteralExpression { Kind: TokenKind.StringLiteral or TokenKind.RawStringLiteral } literal)
            return literal.Kind == TokenKind.RawStringLiteral ? LiteralDecoder.Raw(literal.Text) : LiteralDecoder.Quoted(literal.Text, (_, _) => { });

        _Error(note.Message.Span, DiagnosticCodes.InvalidAnnotation, "le message de @deprecated est une chaîne écrite entre guillemets", "pas un littéral chaîne",
            "par exemple : @deprecated(\"Utiliser nouveau()\")");
        return "";
    }

    /// <summary>Vrai si le gear porte la métadonnée <c>[ignoreError]</c>.</summary>
    private static bool _IgnoresErrorOf(GearDeclaration gear) =>
        gear.Notes?.Metadata.Any(block => block.Entries.Any(entry => entry.Key.Name == "ignoreError")) == true;

    /// <summary>Avertit (W061) de l'usage d'un gear obsolète, sauf depuis un gear obsolète lui-même.</summary>
    private void _WarnDeprecated(GearSymbol gear, SourceSpan span)
    {
        _CheckTrustUse(gear, span);
        if (gear.Deprecation is not { } message || _currentGear?.Deprecation is not null) return;
        _diagnostics.Warning(span, DiagnosticCodes.DeprecatedUse, message.Length == 0 ? $"« {gear.Name} » est obsolète" : $"« {gear.Name} » est obsolète : {message}", "obsolète",
            message.Length == 0 ? [] : [message]);
    }

    /// <summary>Vérifie les annotations posées sur une déclaration du premier niveau, autre que les <c>@tap</c> d'un gear (liés avec son corps).</summary>
    private void _CheckNotes(IStatement statement)
    {
        var (notes, isGear) = statement switch
        {
            GearDeclaration gear => (gear.Notes, true),
            MoldDeclaration mold => (mold.Notes, false),
            EnumDeclaration enumeration => (enumeration.Notes, false),
            IngotDeclaration ingot => (ingot.Notes, false),
            VarDeclaration declaration => (declaration.Notes, false),
            _ => (null, false),
        };
        if (notes is null) return;
        var trust = notes.Annotations.OfType<OtherAnnotation>().Where(a => a.Name.Name is "trusted" or "trust").ToArray();
        if (trust.Length > 1) _Error(trust[1].Span, DiagnosticCodes.InvalidTrust, "un seul contrat @trusted se pose sur un gear", "contrat en double");
        if (!isGear && trust.Length > 0) _Error(trust[0].Span, DiagnosticCodes.InvalidTrust, "@trusted se pose sur un gear, guard ou emit", "pas de fonction");

        foreach (var annotation in notes.Annotations)
        {
            switch (annotation)
            {
                case CustomAnnotation custom when !_annotations.ContainsKey(custom.Name.Name):
                    _ReportUnknownAnnotation(custom);
                    break;
                case CustomAnnotation or BeforeAnnotation when !isGear:
                    _Error(annotation.Span, DiagnosticCodes.InvalidAnnotation, "cette annotation ne se pose que sur un gear", "devant autre chose qu'un gear",
                        "les métadonnées [clé: valeur], elles, se posent devant toute déclaration");
                    break;
                case OtherAnnotation { Name.Name: "async" or "fire" or "sync" or "task" or "trusted" or "trust" } when isGear:
                    // Lues avec la signature du gear (_AsyncModeOf).
                    break;
                case OtherAnnotation { Name.Name: "checked" or "unchecked" } when isGear:
                    // Lues avec le corps du gear (_OverflowOf) ; les deux ensemble se contredisent.
                    if (notes.Annotations.OfType<OtherAnnotation>().Count(a => a.Name.Name is "checked" or "unchecked") > 1 && annotation == notes.Annotations.OfType<OtherAnnotation>().Last(a => a.Name.Name is "checked" or "unchecked"))
                        _Error(annotation.Span, DiagnosticCodes.InvalidAnnotation, "@checked et @unchecked se contredisent sur un même gear", "annotation contraire", "garde l'une des deux");
                    break;
                case OtherAnnotation { Name.Name: "checked" or "unchecked" }:
                    _Error(annotation.Span, DiagnosticCodes.InvalidAnnotation, $"@{((OtherAnnotation)annotation).Name.Name} se pose sur un gear, guard ou emit", "pas une fonction",
                        "elle règle le débordement des entiers dans le corps du gear");
                    break;
                case OtherAnnotation other:
                    _Unsupported(other.Span, $"l'annotation @{other.Name.Name}");
                    break;
                case DeprecatedAnnotation or TapAnnotation when !isGear:
                    _Error(annotation.Span, DiagnosticCodes.InvalidAnnotation, annotation is TapAnnotation ? "@tap ne se pose que sur un gear" : "@deprecated ne se pose pour l'instant que sur un gear",
                        "devant autre chose qu'un gear", "les métadonnées [clé: valeur], elles, se posent devant toute déclaration");
                    break;
            }
        }
    }

    /// <summary>
    /// Les <c>@tap</c> de <paramref name="gear"/>, liés dans son corps : ses paramètres sont visibles, et <c>$</c> désigne sa valeur (après) ou son premier
    /// paramètre (avant).
    /// </summary>
    private List<IrTap> _BindTaps(GearDeclaration gear, GearSymbol symbol)
    {
        List<IrTap> result = [];
        var taps = gear.Notes?.Annotations.OfType<TapAnnotation>().ToArray() ?? [];
        if (taps.Length == 0) return result;

        if (symbol.Kind == GearKind.Emit)
        {
            _Error(taps[0].Span, DiagnosticCodes.InvalidAnnotation, "@tap ne se pose pas sur un emit : il termine le chemin du train", "sur un emit", "pose-le sur un gear ou un guard");
            return result;
        }
        if (symbol.IsPure)
        {
            _Error(taps[0].Span, DiagnosticCodes.InvalidAnnotation, $"un tap a un effet : « {symbol.Name} » ne peut pas être pure", "effet dans un gear pure", "retire pure, ou retire le tap");
            return result;
        }
        if (gear.TypeParameters is not null || symbol.Parameters.Any(p => p.IsMutable) || gear.Parameters.Any(p => p.IsThis))
        {
            _Unsupported(taps[0].Span, "@tap sur un gear générique, ou dont un paramètre est mut ou this");
            return result;
        }
        if (symbol.IsCoroutine)
        {
            _Unsupported(taps[0].Span, "@tap sur un gear async");
            return result;
        }

        var afterSeen = false;
        foreach (var tap in taps)
        {
            if (tap.Mode == TapMode.Before && afterSeen)
                _diagnostics.Warning(tap.Span, DiagnosticCodes.TapOrder, "un @tap(before, …) écrit après un tap qui s'exécute après le gear : il s'exécute pourtant avant", "avant, quoi qu'il en soit",
                    ["écris les taps before au-dessus des autres : ils s'exécutent de haut en bas"]);
            afterSeen |= tap.Mode == TapMode.After;

            var saved = _tapValue;
            _tapValue = tap.Mode == TapMode.After
                ? symbol.ReturnType == PrimitiveType.Void ? null : new IrTapValue(tap.Span, symbol.ReturnType)
                : symbol.Parameters.Count > 0 ? new IrParameterReference(tap.Span, symbol.Parameters[0]) : null;
            var statement = _BindTapBody(tap, gear);
            _tapValue = saved;
            if (statement is not null) result.Add(new IrTap(tap.Span, tap.Mode == TapMode.After, statement));
        }
        return result;
    }

    private IrStatement? _BindTapBody(TapAnnotation tap, GearDeclaration gear)
    {
        if (tap.IsClosure)
        {
            // La fonction reçoit $ (après), ou les paramètres du gear (avant).
            // Une lambda sans paramètre est simplement appelée : elle sert précisément aux effets
            // qui n'ont pas besoin de la valeur ou des paramètres du gear.
            IReadOnlyList<IExpression> arguments = tap.Value is LambdaExpression { Parameters.Count: 0 }
                ? []
                : tap.Mode == TapMode.After ? [new DollarExpression(tap.Span)] : [.. gear.Parameters.Select(p => new NameExpression(p.Name))];
            return _BindExpression(new CallExpression(tap.Span, tap.Value, arguments)) is { } call ? new IrExpressionStatement(tap.Span, call) : null;
        }
        return tap.Target is not null
            ? _BindStatement(new WriteStatement(tap.Span, tap.Value, tap.Target, tap.Path))
            : _BindStatement(new ExpressionStatement(tap.Span, tap.Value));
    }
}
