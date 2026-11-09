using Rgl.AST;
using Rgl.AST.Interfaces;
using Rgl.AST.Molds;
using Rgl.Diagnostics;
using Rgl.IR.Expressions;
using Rgl.IR.Symbols;
using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.Binding;

/// <summary>
/// Le cycle de vie d'un mold (<c>mold Handle -&gt; fermer { … }</c>, décisions : une valeur à cycle de vie n'a qu'un propriétaire,
/// elle ne se copie jamais mais se déplace ; son gear de fin s'appelle une fois, chez celui qui la détient quand elle sort de portée ;
/// ce gear ne peut pas échouer, RGL n'ayant pas d'exceptions). Le gear de fin reçoit la valeur par référence : il ne la déplace pas.
/// Un mold qui en contient un dans un champ a lui-même un cycle de vie, sans gear de fin à lui.
/// </summary>
internal sealed partial class Binder
{
    /// <summary>
    /// Range sur chaque mold de <paramref name="declarations"/> son gear de fin : il existe, c'est un gear ordinaire (ni guard, ni emit,
    /// ni asynchrone) de la forme <c>gear fermer(Mold m): void</c>, sans <c>mut</c> (E135). Les signatures des gears sont déjà déclarées.
    /// </summary>
    private void _ResolveDestructors(IEnumerable<MoldDeclaration> declarations)
    {
        foreach (var declaration in declarations)
        {
            if (declaration.Destructor is not { } written) continue;
            if (declaration.TypeParameters is not null)
            {
                _Unsupported(written.Span, "un gear de fin dans un mold générique");
                continue;
            }
            if (_molds.GetValueOrDefault(_DeclKey(declaration.Name.Name)) is not { } mold || mold.Declaration != declaration.Name.Span) continue;

            var destructors = _TryFind(_gearOverloads, written.Name, out var overloads)
                ? overloads.Where(g => g.Kind == GearKind.Gear && !g.IsTrain && g.Async == AsyncMode.None
                    && g.ReturnType == PrimitiveType.Void && g.Parameters is [{ Type: MoldType { Symbol: var owner }, IsMutable: false }] && owner == mold).ToArray()
                : [];
            var gear = destructors.Length == 1 ? destructors[0] : null;
            if (gear is null && (!_TryFind(_gears, written.Name, out gear) || gear is null))
            {
                _Error(written.Span, DiagnosticCodes.InvalidDestructor, $"le gear de fin « {written.Name} » de « {mold.Name} » n'existe pas", "gear inconnu",
                    $"déclare-le : gear {written.Name}({mold.Name} valeur): void {{ … }}");
                continue;
            }

            var problem = gear switch
            {
                { Kind: not GearKind.Gear } => $"c'est un {(gear.Kind == GearKind.Guard ? "guard" : "emit")} : le gear de fin ne peut pas échouer",
                { IsTrain: true } => "c'est un train : le gear de fin est un gear",
                { Async: not AsyncMode.None } => "il est asynchrone : le gear de fin s'exécute tout de suite, sans attente",
                { ReturnType: var returned } when returned != PrimitiveType.Void => $"il renvoie un « {returned} » : le gear de fin ne renvoie rien",
                { Parameters: not [{ Type: MoldType }] } => $"il doit recevoir une seule valeur, un « {mold.Name} »",
                { Parameters: [{ Type: MoldType { Symbol: var owner } }] } when owner != mold => $"il doit recevoir un « {mold.Name} »",
                { Parameters: [{ IsMutable: true }] } => "son paramètre est mut : le gear de fin ne modifie pas la valeur qu'il ferme",
                _ => null,
            };
            if (problem is not null)
            {
                _Error(written.Span, DiagnosticCodes.InvalidDestructor, $"« {written.Name} » ne peut pas être le gear de fin de « {mold.Name} » : {problem}", "gear de fin invalide",
                    $"un gear de fin s'écrit : gear {written.Name}({mold.Name} valeur): void {{ … }}");
                continue;
            }
            mold.Destructor = gear;
        }
    }

    /// <summary>
    /// Un type d'erreur est une donnée : il est copié, routé vers un rail et fusionné dans des unions, alors qu'une valeur à cycle de vie n'a
    /// qu'un propriétaire. Un mold <c>error</c> qui en contient une (E137).
    /// </summary>
    private void _CheckErrorMolds(IEnumerable<MoldDeclaration> declarations)
    {
        foreach (var declaration in declarations.Where(d => d.IsError))
        {
            if (_molds.GetValueOrDefault(_DeclKey(declaration.Name.Name)) is not { } mold || mold.Declaration != declaration.Name.Span) continue;
            if (mold.Fields.FirstOrDefault(f => LifecycleRules.Has(f.Type)) is not { } field) continue;

            _Error(declaration.Name.Span, DiagnosticCodes.LifecycleUnsupported, $"l'erreur « {mold.Name} » contient « {field.Name} », un « {field.Type} » à cycle de vie : une erreur se copie et se range dans une union", "erreur à cycle de vie",
                "une erreur est une donnée : garde dans l'erreur ce qui identifie la valeur (un nom, un numéro), pas la valeur elle-même");
        }
    }

    /// <summary>
    /// <see cref="_BindTypeCore"/> puis un contrôle : un tableau, un dictionnaire, un tuple, un nullable, une union, un résultat, une promesse
    /// ou un type de fonction qui contient un type à cycle de vie copierait ses valeurs : pas encore pris en charge (E137).
    /// </summary>
    private IrType? _BindType(ITypeNode type)
    {
        if (type is AST.Types.PointerType pointer)
        {
            if (!_RequireUnsafe(type.Span)) return null;
            var element = _BindType(pointer.Element);
            if (element == PrimitiveType.Void && pointer.Owned || element is not null && LifecycleRules.Has(element))
            {
                _Error(type.Span, DiagnosticCodes.UnsafeRequired, "un pointeur brut ne porte pas un cycle de vie RGL", "type pointé");
                return null;
            }
            return element is null ? null : new PointerType(element, pointer.Owned);
        }
        if (type is AST.Types.PrimitiveType { Keyword: Syntax.TokenKind.CharKeyword })
            return _RequireUnsafe(type.Span) ? PrimitiveType.Char : null;
        var bound = _BindTypeCore(type);
        if (bound is not null && LifecycleRules.HoldsInside(bound))
        {
            _Error(type.Span, DiagnosticCodes.LifecycleUnsupported, $"« {bound} » : une valeur à cycle de vie n'a qu'un propriétaire, elle ne se range pas dans cette composition", "pas encore pris en charge",
                "garde la valeur dans une variable ou un champ de mold, et passe-la aux gears (par référence)");
            return null;
        }
        return bound;
    }

    /// <summary>Vrai si l'expression désigne une valeur qui existe déjà (variable, paramètre, champ, élément) : la lire pour la ranger la copierait.</summary>
    private static bool _IsPlace(IrExpression value) => value is IrVariableReference or IrParameterReference or IrMemberAccess or IrIndex or IrTupleItem or IrNonNull or IrUnionField;

    /// <summary>
    /// Refuse de copier une valeur à cycle de vie (E136) : ranger dans une variable, un champ ou un retour la valeur d'une variable, d'un paramètre
    /// ou d'un champ la copierait. Une valeur calculée (appel, valeur de mold) ou déjà déplacée passe. Rend <paramref name="value"/>, ou
    /// <see langword="null"/> après l'erreur.
    /// </summary>
    /// <param name="value">La valeur liée.</param>
    /// <param name="span">La valeur écrite.</param>
    /// <param name="where">Ce qui la reçoit, pour le message : « la variable », « le champ »…</param>
    private IrExpression? _NoCopy(IrExpression? value, SourceSpan span, string where)
    {
        if (value is null || !LifecycleRules.Has(value.Type) || !_IsPlace(value)) return value;

        var reason = value switch
        {
            IrParameterReference => " : un paramètre appartient à l'appelant",
            IrVariableReference { Variable.IsConstant: true } => " : une constante ne se déplace pas",
            IrMemberAccess => " : le champ appartient à sa valeur",
            _ => "",
        };
        _Error(span, DiagnosticCodes.LifecycleCopy, $"« {value.Type} » a un cycle de vie : {where} ne peut pas en recevoir une copie{reason}", "copie interdite",
            "une valeur à cycle de vie n'a qu'un propriétaire : déplace-la avec « nom:: valeur » ou « nom <-| valeur », ou rends une valeur calculée");
        return null;
    }

    /// <summary><c>return nom;</c> : une variable locale de la fonction, non constante, se déplace d'elle-même ; toute autre valeur existante serait copiée.</summary>
    private IrExpression? _NoCopyOnReturn(IrExpression? value, SourceSpan span)
    {
        if (value is IrVariableReference { Variable.IsConstant: false } local && !(_lambda is not null && !_lambda.Locals.Contains(local.Variable))) return value;
        return _NoCopy(value, span, "le retour");
    }

    /// <summary>
    /// Une variable ou un paramètre à cycle de vie lu dans une lambda, hors d'elle : la lambda le capturerait par copie (E137).
    /// Rend vrai après l'erreur.
    /// </summary>
    private bool _CheckLifecycleCapture(SourceSpan span, string name, IrType type, bool captured)
    {
        if (!captured || !LifecycleRules.Has(type)) return false;
        _Error(span, DiagnosticCodes.LifecycleUnsupported, $"« {name} » a un cycle de vie : une lambda ne peut pas la capturer, elle en garderait une copie", "capturée",
            "passe la valeur en paramètre de la lambda, ou fais le travail dans un gear");
        return true;
    }

    /// <summary>Une constante d'un type à cycle de vie : sa fin ne s'exécuterait jamais (E137).</summary>
    private bool _CheckLifecycleConstant(Identifier name, IrType? type)
    {
        if (type is null || !LifecycleRules.Has(type)) return false;
        _Error(name.Span, DiagnosticCodes.LifecycleUnsupported, $"« {name.Name} » est une constante de type « {type} », qui a un cycle de vie", "constante interdite",
            $"déclare-la avec var : « {name.Name} » ne sera fermée qu'en sortant de son bloc");
        return true;
    }
}
