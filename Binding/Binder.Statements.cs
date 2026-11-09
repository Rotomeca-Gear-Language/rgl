using Rgl.AST;
using Rgl.AST.Expressions;
using Rgl.AST.InputOutput;
using Rgl.AST.Interfaces;
using Rgl.AST.Statements;
using Rgl.AST.Variables;
using Rgl.Diagnostics;
using Rgl.IR;
using Rgl.IR.Expressions;
using Rgl.IR.Statements;
using Rgl.IR.Symbols;
using Rgl.IR.Types;
using Rgl.Syntax;
using Rgl.Text;
using Rotomeca.Core.Collections;
using AstTypes = Rgl.AST.Types;

namespace Rgl.Binding;

internal sealed partial class Binder
{
    /// <summary>Le bloc courant : les variables visibles à cet endroit du source.</summary>
    private Scope _scope = new(null);

    private IrStatement? _BindStatement(IStatement statement) => statement switch
    {
        VarDeclaration declaration => _BindDeclaration(declaration),
        AST.Statements.RawStoreStatement store => _BindRawStore(store),
        AssignmentStatement assignment => _BindAssignment(assignment),
        UndefineStatement undefine => _BindUndefine(undefine),
        BlockStatement block => _BindBlock(block),
        IfStatement ifStatement => _BindIf(ifStatement),
        SwitchStatement switchStatement => _BindSwitch(switchStatement),
        BreakStatement breakStatement => _BindBreak(breakStatement),
        GotoStatement gotoStatement => _BindGoto(gotoStatement),
        ReturnStatement returnStatement => _BindReturn(returnStatement),
        FailStatement fail => _BindFail(fail),
        PanicStatement panic => _BindPanic(panic),
        CancelStatement cancel => _BindCancel(cancel),
        DoneStatement done => _BindDone(done),
        WriteStatement write => _BindWrite(write),
        FieldAssignmentStatement assignment => _BindFieldAssignment(assignment),
        ExpressionStatement expression => _BindExpressionStatement(expression),
        _ => throw new InvalidOperationException($"Instruction inattendue : {statement.GetType().Name}."),
    };

    #region Déclarations

    /// <summary>
    /// <c>var</c> prend le type de sa valeur (E001 s'il ne peut pas être déduit) ; une déclaration typée
    /// et <c>const</c> fixent le leur, que la valeur doit avoir (E003). Une constante dont la valeur est
    /// entièrement connue à la compilation le retient, pour devenir <c>constexpr</c> en C++. Une variable
    /// <c>mut</c> est un tableau, un mold ou un dictionnaire (E101).
    /// </summary>
    private IrStatement? _BindDeclaration(VarDeclaration declaration)
    {
        // Le type écrit d'abord : la valeur sait alors ce qu'elle doit donner.
        var written = declaration.IsIntef ? null : _BindType(declaration.Type!);
        var value = _BindStatementValue(declaration.Expression, written);
        var unconverted = value;
        IrType? type = null;

        if (declaration.IsIntef)
        {
            if (value?.Type is NullType)
                _Error(value.Span, DiagnosticCodes.CannotInferType, $"type de « {declaration.Name.Name} » impossible à déduire de null", "de type inconnu",
                    $"donne-lui un type, par exemple : string {declaration.Name.Name}: \"\";");
            else if (value is not null && _IsPending(value.Type))
                _Error(value.Span, DiagnosticCodes.CannotInferType, $"type de « {declaration.Name.Name} » incomplet : « {value.Type} »", "type incomplet",
                    $"ok(…) et err(…) ne donnent qu'un côté du résultat : déclare son type, par exemple : int ! string {declaration.Name.Name}: {declaration.Expression.Span.Text};");
            else if (value is IrIntegerConstant { Type: not PrimitiveType { Kind: PrimitiveKind.Int } } big)
                _Error(value.Span, DiagnosticCodes.CannotInferType, $"un littéral entier est un int, et {big.Value} n'y tient pas", "trop grand pour un int",
                    $"donne à la variable un type assez grand, par exemple : long {declaration.Name.Name}: {declaration.Expression.Span.Text};");
            else type = value?.Type;
        }
        else
        {
            type = written;
            if (declaration.IsMove && value?.Type is PointerType && type is PointerType)
                value = _Moved(declaration.Expression, value, declaration.Name.Name);
            if (type is not null && value is not null)
                value = _ConvertTo(value, type, $"« {declaration.Name.Name} » est déclarée « {type} »");
        }

        if (declaration.IsMove && value is not IrMove and not IrRawMemory { Operation: RawMemoryOperation.Transfer }) value = _Moved(declaration.Expression, value, declaration.Name.Name);
        if (value is not null && declaration.IsConst && _CheckLifecycleConstant(declaration.Name, type)) value = null;
        value = _NoCopy(value, declaration.Expression.Span, $"« {declaration.Name.Name} »");

        if (declaration.IsConst && type is PrimitiveType { Kind: PrimitiveKind.FileStream })
            _diagnostics.Warning(declaration.Name.Span, DiagnosticCodes.FileStreamConstant, $"« {declaration.Name.Name} » est un flux de fichier : const ne l'empêche pas de changer", "état interne",
                ["chaque lecture ou écriture avance le flux : déclare-le avec var"]);

        VariableSymbol? symbol = null;
        if (type is not null && value is not null)
        {
            if (type == PrimitiveType.Void)
            {
                _Error(value.Span, DiagnosticCodes.TypeMismatch, $"« {declaration.Name.Name} » ne peut pas recevoir une valeur void", "void",
                    "ce gear ne renvoie rien : appelle-le seul, en instruction");
                _Declare(declaration.Name, null);
                return null;
            }
            if (declaration.IsMutable && !_CheckMutType(declaration.Span, type, $"« {declaration.Name.Name} »"))
            {
                _Declare(declaration.Name, null);
                return null;
            }
            var compileTimeValue = declaration.IsConst ? ConstantFolder.Fold(value) : null;
            symbol = new VariableSymbol(declaration.Name.Name, type, declaration.IsConst, declaration.Name.Span, compileTimeValue, declaration.IsMutable)
            { IsBorrowedPointer = _BorrowedPointer(unconverted) };
        }

        _Declare(declaration.Name, symbol);
        if (symbol is not null && !declaration.IsConst) _AfterAssignment(symbol, unconverted!);
        return symbol is null ? null : new IrVariableDeclaration(declaration.Span, symbol, value!);
    }

    /// <summary>
    /// Ajoute un nom au bloc courant : E044 s'il y est déjà, W001 s'il avait été libéré par
    /// <c>undefine</c>, W009 s'il masque une variable d'un bloc englobant.
    /// </summary>
    private void _Declare(Identifier name, VariableSymbol? symbol)
    {
        if (_scope.DeclaresHere(name.Name))
        {
            var first = _scope[name.Name].Value.Declaration;
            _Error(name.Span, DiagnosticCodes.AlreadyDeclared, $"« {name.Name} » est déjà déclarée dans ce bloc (ligne {first.StartPosition.Line})", "déjà déclarée",
                $"choisis un autre nom, ou libère d'abord la première avec : undefine {name.Name};");
            return;
        }

        var released = _scope.Released(name.Name);
        var outer = _scope.Parent?[name.Name];
        if (!released.HasValue && !(outer?.HasValue ?? false) && _parameters.TryGetValue(name.Name, out var parameter))
            _diagnostics.Warning(name.Span, DiagnosticCodes.ShadowedVariable,
                $"« {name.Name} » masque le paramètre du même nom", "masque un paramètre",
                ["jusqu'à la fin de ce bloc, ce nom désigne la nouvelle variable : choisis un autre nom pour garder l'accès au paramètre"]);
        else if (released.HasValue)
            _diagnostics.Warning(name.Span, DiagnosticCodes.RedeclaredAfterUndefine,
                $"« {name.Name} » est redéclarée après avoir été libérée (ligne {released.Value.StartPosition.Line})", "redéclarée",
                ["c'est une nouvelle variable : elle ne garde rien de l'ancienne"]);
        else if (outer is { HasValue: true } shadowed)
            _diagnostics.Warning(name.Span, DiagnosticCodes.ShadowedVariable,
                $"« {name.Name} » masque la variable du même nom déclarée ligne {shadowed.Value.Declaration.StartPosition.Line}", "masque une variable englobante",
                ["jusqu'à la fin de ce bloc, ce nom désigne la nouvelle variable : choisis un autre nom pour garder l'accès à l'ancienne"]);

        _scope.Declare(name.Name, new ScopeEntry(symbol, name.Span));
        if (symbol is not null) _lambda?.Locals.Add(symbol);
    }

    /// <summary>
    /// Type écrit dans une déclaration. Seuls les types primitifs et les tableaux existent pour
    /// l'instant ; un nom de type est inconnu (E041).
    /// </summary>
    private IrType? _BindTypeCore(ITypeNode type)
    {
        switch (type)
        {
            case AstTypes.PrimitiveType primitive:
                PrimitiveKind? kind = primitive.Keyword switch
                {
                    TokenKind.ByteKeyword => PrimitiveKind.Byte,
                    TokenKind.UByteKeyword => PrimitiveKind.UByte,
                    TokenKind.ShortKeyword => PrimitiveKind.Short,
                    TokenKind.UShortKeyword => PrimitiveKind.UShort,
                    TokenKind.IntKeyword => PrimitiveKind.Int,
                    TokenKind.UIntKeyword => PrimitiveKind.UInt,
                    TokenKind.LongKeyword => PrimitiveKind.Long,
                    TokenKind.ULongKeyword => PrimitiveKind.ULong,
                    TokenKind.FloatKeyword => PrimitiveKind.Float,
                    TokenKind.DoubleKeyword => PrimitiveKind.Double,
                    TokenKind.BoolKeyword => PrimitiveKind.Bool,
                    TokenKind.StringKeyword => PrimitiveKind.String,
                    TokenKind.MStringKeyword => PrimitiveKind.MString,
                    TokenKind.SymbolKeyword => PrimitiveKind.Symbol,
                    TokenKind.DurationKeyword => PrimitiveKind.Duration,
                    TokenKind.RegexKeyword => PrimitiveKind.Regex,
                    TokenKind.DateTimeKeyword => PrimitiveKind.DateTime,
                    _ => null,
                };

                if (kind is { } known) return new PrimitiveType(known);
                if (primitive.Keyword == TokenKind.VoidKeyword)
                {
                    _Error(primitive.Span, DiagnosticCodes.TypeMismatch, "une variable ne peut pas être de type void", "void",
                        "void désigne l'absence de valeur : une variable a toujours une valeur");
                    return null;
                }

                // bytes : un tableau d'octets, que les conversions et les fichiers lisent comme tel.
                if (primitive.Keyword == TokenKind.BytesKeyword) return _bytes;

                // char et ptr n'ont pas encore de type dans le runtime.
                _Unsupported(primitive.Span, $"type « {primitive.Span.Text} »");
                return null;

            case AstTypes.ArrayType array:
                return _BindArrayType(array);

            case AstTypes.DictionaryType dictionary:
                return _BindDictionaryType(dictionary);

            case AstTypes.FunctionType function:
                return _BindFunctionType(function);

            case AstTypes.TupleType tuple:
                return _BindTupleType(tuple);

            // Un paramètre générique : le type que l'instance en cours lui donne.
            case AstTypes.NamedType { TypeArguments: null } named when _generic is not null && _generic.Types.TryGetValue(named.Name.Name, out var parameterType):
                return parameterType;

            // Promise<T> et Promise<T ! E> : le résultat, pas encore connu, d'un gear @async (sauf si un mold générique porte ce nom).
            case AstTypes.NamedType { Name.Name: "Promise", TypeArguments: not null } promise when !_TryFind(_genericMolds, "Promise", out _):
                return _BindPromiseType(promise);

            case AstTypes.NamedType { Name.Name: "Promise", TypeArguments: null } bare when !_TryFind(_molds, "Promise", out _):
                _Error(bare.Span, DiagnosticCodes.InvalidPromise, "Promise est une promesse : son argument s'écrit avec elle", "argument manquant", "par exemple : Promise<int> ou Promise<int ! AsyncException>");
                return null;

            case AstTypes.NamedType { TypeArguments: not null } generic:
                return _BindGenericType(generic);

            case AstTypes.NamedType named when _TryFind(_genericMolds, named.Name.Name, out _):
                _Error(named.Span, DiagnosticCodes.TypeArguments, $"« {named.Name.Name} » est générique : ses arguments s'écrivent avec lui", "arguments manquants",
                    $"par exemple : {named.Name.Name}<int>");
                return null;

            case AstTypes.NamedType named when _TryFind(_molds, named.Name.Name, out var mold):
                return new MoldType(mold);

            case AstTypes.NamedType named when _TryFind(_enums, named.Name.Name, out var enumeration):
                return new EnumType(enumeration);

            case AstTypes.NamedType named when _TryFind(_ingots, named.Name.Name, out var ingot):
                return new IngotType(ingot);

            case AstTypes.BoundedType bounded:
                return _BindBoundedType(bounded);

            case AstTypes.NamedType { Name.Name: "FileStream", TypeArguments: null }:
                return PrimitiveType.FileStream;

            case AstTypes.NamedType named:
                _Error(named.Span, DiagnosticCodes.UnknownName, $"type « {named.Name.Name} » inconnu", "déclaré nulle part",
                    _VisibleNames(_molds).ToArray() is { Length: > 0 } molds
                        ? $"molds déclarés : {string.Join(", ", molds)}"
                        : "les types sont les primitifs (int, string…), les tableaux ([int]), les molds et Exception");
                return null;

            case AstTypes.AnonymousMoldType anonymous:
                return _BindAnonymousMoldType(anonymous);

            case AstTypes.UnionType union:
                return _BindUnionType(union);

            case AstTypes.ResultType result:
                return _BindResultType(result);

            case AstTypes.NullableType nullable:
                if (nullable.Inner is AstTypes.PrimitiveType { Keyword: TokenKind.VoidKeyword })
                {
                    _Error(nullable.Span, DiagnosticCodes.InvalidNullable, "void ne peut pas être nullable", "void?", "void n'a pas de valeur, pas même null");
                    return null;
                }
                var inner = _BindType(nullable.Inner);
                if (inner is NullableType)
                {
                    _Error(nullable.Span, DiagnosticCodes.InvalidNullable, "un type n'est nullable qu'une fois", "nullable d'un nullable");
                    return null;
                }
                return inner is null ? null : new NullableType(inner);

            default:
                throw new InvalidOperationException($"Type inattendu dans un arbre sans erreur de syntaxe : {type.GetType().Name}.");
        }
    }

    #endregion

    #region Affectations, undefine et blocs

    /// <summary>
    /// <c>nom &lt;- valeur</c> : la variable doit exister (E002, E006 si elle a été libérée), ne pas
    /// être une constante (E005), avoir le type rappelé s'il y en a un (E004), et la valeur doit
    /// avoir son type (E003).
    /// </summary>
    private IrStatement? _BindAssignment(AssignmentStatement assignment)
    {
        var target = assignment.Target;
        var entry = _scope[target.Name];
        if (!entry.HasValue && _parameters.TryGetValue(target.Name, out var mutable) && mutable.IsMutable)
            return _BindMutableParameterAssignment(assignment, mutable);

        var plain = assignment.Operator is TokenKind.Assign or TokenKind.MoveAssign;
        var assigned = plain && _scope[target.Name] is { HasValue: true } known ? known.Value.Symbol?.Type : null;
        var value = _BindStatementValue(assignment.Value, assigned);
        var hint = assignment.TypeHint is null ? null : _BindType(assignment.TypeHint);

        if (!entry.HasValue && _parameters.ContainsKey(target.Name))
        {
            _Error(target.Span, DiagnosticCodes.AssignToConstant, $"« {target.Name} » est un paramètre : il ne peut pas être réassigné", "paramètre",
                "un paramètre est passé par référence constante : copie-le dans une variable pour le modifier",
                "un paramètre mut, lui, désigne la variable de l'appelant, qu'il peut remplacer");
            return null;
        }
        if (!entry.HasValue)
        {
            _ReportMissingVariable(target, DiagnosticCodes.UnknownVariable, $"la variable « {target.Name} » n'existe pas",
                $"déclare-la d'abord, par exemple : var {target.Name}: 0;");
            return null;
        }

        if (entry.Value.Symbol is not { } variable) return null;

        if (_ReportCaptured(target, variable)) return null;
        if (variable.IsConstant)
        {
            _Error(target.Span, DiagnosticCodes.AssignToConstant, $"« {target.Name} » est une constante : elle ne peut pas être réassignée", "constante",
                $"déclarée constante ligne {variable.Declaration.StartPosition.Line} : pour la modifier, déclare-la avec var ou avec son type");
            return null;
        }

        if (hint is not null && hint != variable.Type)
        {
            _Error(assignment.TypeHint!.Span, DiagnosticCodes.TypeHintMismatch, $"« {target.Name} » est de type « {variable.Type} », pas « {hint} »", "type rappelé",
                "le type écrit après le nom doit être celui de la déclaration : ce n'est pas une conversion");
            return null;
        }

        if (value is null) return null;

        // Affectation composée : a +<- b calcule a + b, puis l'affecte à a.
        if (!plain)
        {
            TokenKind? op = assignment.Operator switch
            {
                TokenKind.PlusAssign => TokenKind.Plus,
                TokenKind.MinusAssign => TokenKind.Minus,
                TokenKind.StarAssign => TokenKind.Star,
                TokenKind.SlashAssign => TokenKind.Slash,
                _ => null,
            };
            if (op is null) return _BindBrandAssignment(assignment, variable, value);

            value = _BindOperation(assignment.Span, assignment.OperatorSpan, op.Value, new IrVariableReference(target.Span, variable), value);
            if (value is null) return null;
        }

        if (assignment.Operator == TokenKind.MoveAssign && value.Type is PointerType && variable.Type is PointerType)
        {
            value = _Moved(assignment.Value, value, target.Name);
            if (value is null) return null;
        }
        var converted = _ConvertTo(value, variable.Type, $"« {target.Name} » est de type « {variable.Type} »");
        if (converted is null) return null;
        if (assignment.Operator == TokenKind.MoveAssign && converted is not IrMove and not IrRawMemory { Operation: RawMemoryOperation.Transfer }) converted = _Moved(assignment.Value, converted, target.Name);
        converted = _NoCopy(converted, assignment.Value.Span, $"« {target.Name} »");
        if (converted is null) return null;
        variable.IsBorrowedPointer = _BorrowedPointer(value);
        _AfterAssignment(variable, value);
        return new IrAssignment(assignment.Span, variable, converted);
    }

    /// <summary><c>undefine nom</c> : libère une variable visible (E002 sinon, E006 si elle l'est déjà).</summary>
    private IrStatement? _BindUndefine(UndefineStatement undefine)
    {
        var name = undefine.Name;
        var entry = _scope[name.Name];
        if (!entry.HasValue)
        {
            _ReportMissingVariable(name, DiagnosticCodes.UnknownVariable, $"la variable « {name.Name} » n'existe pas",
                "undefine libère une variable déclarée plus haut");
            return null;
        }

        if (entry.Value.Symbol is { } captured && _ReportCaptured(name, captured)) return null;
        if (entry.Value.Symbol is { IsBorrowedPointer: true })
        {
            _Error(undefine.Span, DiagnosticCodes.UnsafeRequired, "cette adresse est empruntée et ne peut pas être libérée", "libération interdite");
            return null;
        }
        _scope.Release(name.Name, undefine.Span);
        if (entry.Value.Symbol is { } released) _scope.Widen(released);
        return entry.Value.Symbol is { } variable ? new IrUndefine(undefine.Span, variable) : null;
    }

    /// <summary>
    /// Une variable absente : E006 si elle a été libérée par <c>undefine</c>, sinon l'erreur
    /// <paramref name="code"/> donnée.
    /// </summary>
    private void _ReportMissingVariable(Identifier name, string code, string message, string help)
    {
        var released = _scope.Released(name.Name);
        if (released.HasValue)
        {
            _Error(name.Span, DiagnosticCodes.VariableWithoutValue, $"« {name.Name} » a été libérée ligne {released.Value.StartPosition.Line} (undefine, ou déplacée par « :: » ou « <-| »)", "libérée",
                $"redéclare-la avant de t'en servir, par exemple : var {name.Name}: 0;");
            return;
        }

        _Error(name.Span, code, message, "déclarée nulle part", help);
    }

    private IrBlock _BindBlock(BlockStatement block) => block.IsArena ? _BindArenaBlock(block) : _BindStatements(block.Span, block.Statements);

    #endregion

    #region Entrées et sorties

    /// <summary>Les formats d'entrée et de sortie qui ne sont pas encore pris en charge.</summary>
    private static readonly HashSet<string> _laterFormats = new(["file"], StringComparer.Ordinal);

    /// <summary>
    /// <c>bytes</c> : un tableau dynamique d'octets, <c>[ubyte]</c>. Les octets se lisent et se parcourent comme ceux de
    /// n'importe quel tableau ; ils ne se modifient qu'à travers une variable <c>mut</c>.
    /// </summary>
    private static readonly ArrayType _bytes = new(new PrimitiveType(PrimitiveKind.UByte));

    /// <summary>
    /// <c>valeur -&gt; console;</c> : la valeur s'écrit comme dans un template (chaîne, nombre, bool,
    /// symbole) ou c'est une Exception (E040 sinon) ; pas dans un gear <c>pure</c> (E066).
    /// <c>valeur -&gt; raw chemin;</c> écrit une <c>string</c> ou des <c>bytes</c> dans un fichier, dans un guard.
    /// </summary>
    private IrStatement? _BindWrite(WriteStatement write)
    {
        var value = _BindExpression(write.Value);
        var target = write.Target.Name;

        if (target is "raw" or "custom") return _BindFileWrite(write, value);
        if (target == "json") return _BindJsonWrite(write, value);
        if (target == "xml") return _BindXmlWrite(write, value);

        if (target != "console")
        {
            if (_laterFormats.Contains(target)) _Unsupported(write.Target.Span, $"écriture vers « {target} »");
            else
                _Error(write.Target.Span, DiagnosticCodes.UnknownName, $"cible d'écriture « {target} » inconnue", "inconnue",
                    "une valeur s'écrit vers un format, par exemple : total -> console;");
            return null;
        }

        if (write.Path is not null)
        {
            _Error(write.Path.Span, DiagnosticCodes.ExpectedToken, "la console n'a pas de chemin", "chemin en trop",
                "seuls les formats de fichier prennent un chemin, par exemple : texte -> raw \"a.txt\";");
            return null;
        }

        if (!_AllowInputOutput(write.Span, "écrire sur la console") || value is null) return null;

        if (!_IsWritable(value.Type))
        {
            _Error(value.Span, DiagnosticCodes.TypeMismatch, $"une valeur « {value.Type} » ne s'écrit pas sur la console", $"de type « {value.Type} »",
                "la console écrit des chaînes, des nombres, des bool, des symboles et des Exception");
            return null;
        }

        return new IrWrite(write.Span, value);
    }

    /// <summary>
    /// <c>valeur -&gt; raw chemin;</c> : une <c>string</c> ou des <c>bytes</c> (E003 sinon), écrits dans le
    /// fichier ; seulement dans un guard (E060), où un échec envoie une <c>IoError</c>.
    /// </summary>
    private IrStatement? _BindFileWrite(WriteStatement write, IrExpression? value)
    {
        var custom = write.Target.Name == "custom";
        var path = _BindFilePath(write.Path, write.Target.Span, write.Target.Name, custom ? "config -> custom<Config, versToml> \"a.toml\";" : "texte -> raw \"a.txt\";");
        if (!_AllowInputOutput(write.Span, "écrire un fichier") || !_RequireGuard(write.Span, "l'écriture d'un fichier") || path is null || value is null) return null;

        // custom<M, train> : le train convertit la valeur en bytes, que le fichier reçoit.
        if (custom)
        {
            value = _BindCustomTrain(write.Custom, write.Target.Span, write.Span, value);
            value = value is null ? null : _ConvertTo(value, _bytes, "le train de custom écrit des bytes");
            if (value is null) return null;
        }

        if (value.Type is PrimitiveType { Kind: PrimitiveKind.String or PrimitiveKind.MString })
            value = _Coerce(value, PrimitiveType.String);
        else if (value.Type is not ArrayType { Element: PrimitiveType { Kind: PrimitiveKind.UByte } })
        {
            _Error(value.Span, DiagnosticCodes.TypeMismatch, $"« raw » écrit une string ou des bytes, pas un « {value.Type} »", $"de type « {value.Type} »",
                "pour écrire un nombre ou un mold, mets-le d'abord en texte : `{valeur}` -> raw \"a.txt\";");
            return null;
        }

        return value is null ? null : new IrWriteFile(write.Span, value, path);
    }

    /// <summary>
    /// Le chemin qui suit un format de fichier : une <c>string</c> (E003 sinon), obligatoire.
    /// </summary>
    private IrExpression? _BindFilePath(IExpression? path, SourceSpan format, string name, string example)
    {
        if (path is null)
        {
            _Error(format, DiagnosticCodes.ExpectedToken, $"« {name} » attend le chemin du fichier", "chemin manquant", $"par exemple : {example}");
            return null;
        }

        var bound = _BindExpected(path, PrimitiveType.String);
        return bound is null ? null : _ConvertTo(bound, PrimitiveType.String, "un chemin de fichier est une string");
    }

    /// <summary>
    /// <c>&lt;- custom&lt;M, train&gt; chemin</c> : les <c>bytes</c> du fichier passés au gear ou au guard <c>train</c>, qui renvoie
    /// le <c>M</c> lu. C'est un appel ordinaire (arguments, types, erreurs du guard) dont l'argument est la lecture ;
    /// dans un guard seulement, comme toute lecture.
    /// </summary>
    private IrExpression? _BindCustomRead(ReadExpression read)
    {
        const string example = "var config <- custom<Config, lireToml> \"a.toml\";";
        var path = _BindFilePath(read.Path, read.Source.Span, "custom", example);
        if (!_AllowInputOutput(read.Span, "lire un fichier") || !_RequireGuard(read.Span, "la lecture d'un fichier") || path is null) return null;

        var type = read.Custom is null ? null : _BindType(read.Custom.Type);
        if (read.Custom?.Train is null)
            _Error(read.Source.Span, DiagnosticCodes.ExpectedToken, "« custom » attend le type lu et son train : custom<M, train>", "arguments manquants", $"par exemple : {example}");
        if (type is null || read.Custom?.Train is null) return null;

        var bytes = new IrReadFile(read.Span, path, _bytes);
        var converted = _BindCustomTrain(read.Custom, read.Source.Span, read.Span, bytes);
        return converted is null ? null : _ConvertTo(converted, type, $"custom<{type}, {read.Custom.Train!.Name}> lit un « {type} »");
    }

    /// <summary>
    /// L'appel du train de <c>custom&lt;M, train&gt;</c> sur <paramref name="argument"/> (les bytes lus, ou la valeur écrite) :
    /// un appel ordinaire de gear ou de guard, dont l'argument est déjà lié.
    /// </summary>
    private IrExpression? _BindCustomTrain(CustomFormat? custom, SourceSpan format, SourceSpan span, IrExpression argument)
    {
        if (custom?.Train is null)
        {
            _Error(format, DiagnosticCodes.ExpectedToken, "« custom » attend le type et son train : custom<M, train>", "arguments manquants",
                "par exemple : config -> custom<Config, versToml> \"a.toml\";");
            return null;
        }

        var placeholder = new NameExpression(new Identifier(span));
        _pathOverrides[placeholder] = argument;
        try
        {
            return _BindExpression(new CallExpression(span, new NameExpression(custom.Train), [placeholder]));
        }
        finally
        {
            _pathOverrides.Remove(placeholder);
        }
    }

    /// <summary>
    /// <c>&lt;- console</c> : une ligne lue, une <c>string</c> ; pas dans un gear <c>pure</c> (E066).
    /// <c>&lt;- raw chemin</c> : tout le fichier, en <c>string</c> ou, si le contexte attend des <c>bytes</c>, en octets ;
    /// seulement dans un guard, où un échec envoie une <c>IoError</c>.
    /// </summary>
    private IrExpression? _BindRead(ReadExpression read, IrType? expected)
    {
        var format = read.Source.Name;
        if (format == "console") return _AllowInputOutput(read.Span, "lire la console") ? new IrRead(read.Span) : null;
        if (format == "clock") return _AllowInputOutput(read.Span, "lire l'horloge") ? new IrNow(read.Span) : null;

        if (format == "custom") return _BindCustomRead(read);
        if (format == "json") return _BindJsonRead(read, expected);
        if (format == "xml") return _BindXmlRead(read);
        if (format == "file") return _BindFileOpen(read);

        if (format != "raw")
        {
            _Unsupported(read.Source.Span, $"lecture depuis « {format} »");
            return null;
        }

        var path = _BindFilePath(read.Path, read.Source.Span, "raw", "var texte <- raw \"a.txt\";");
        if (!_AllowInputOutput(read.Span, "lire un fichier") || !_RequireGuard(read.Span, "la lecture d'un fichier") || path is null) return null;

        return new IrReadFile(read.Span, path, expected is ArrayType { Element: PrimitiveType { Kind: PrimitiveKind.UByte } } ? _bytes : PrimitiveType.String);
    }

    /// <summary>
    /// Une entrée ou une sortie n'est permise que dans le corps d'un gear qui n'est pas <c>pure</c>
    /// (E066), ou d'un guard ou d'un emit ; pas au niveau du fichier (E053).
    /// </summary>
    private bool _AllowInputOutput(SourceSpan span, string what)
    {
        // Une lambda d'une constante du fichier s'exécute dans le gear qui l'appelle.
        if (_currentGear is null && _lambda is null)
        {
            _Error(span, DiagnosticCodes.StatementOutsideGear, $"{what} se fait dans un gear", "au niveau du fichier",
                "les entrées et sorties s'écrivent dans un gear, un guard ou un emit");
            return false;
        }

        return _AllowEffect(span, DiagnosticCodes.InputOutputInPure, what);
    }

    /// <summary>
    /// Indique si une valeur de ce type s'écrit en texte : chaîne, nombre, bool, symbole, Exception, ou
    /// mold dont tous les champs s'écrivent.
    /// </summary>
    private static bool _IsWritable(IrType type) =>
        type is PrimitiveType { IsNumeric: true } or PrimitiveType { Kind: PrimitiveKind.Bool or PrimitiveKind.String or PrimitiveKind.MString or PrimitiveKind.Symbol or PrimitiveKind.Duration or PrimitiveKind.DateTime }
        || (type is MoldType mold && mold.Symbol.Fields.All(f => _IsWritable(f.Type)))
        || (type is NullableType nullable && _IsWritable(nullable.Inner))
        || type is EnumType
        || (type is IngotType ingot && _IsWritable(ingot.Base))
        || (type is ArrayType array && _IsWritable(array.Element))
        || (type is DictionaryType dictionary && _IsWritable(dictionary.Key) && _IsWritable(dictionary.Value))
        || (type is TupleType tuple && tuple.Elements.All(_IsWritable))
        || (type is UnionType union && union.Members.All(_IsWritable))
        || (type is ResultType { IsPending: false } result && _IsWritable(result.Value) && _IsWritable(result.Error));

    /// <summary>Des instructions dans une nouvelle portée : un bloc, une branche, un cas de switch.</summary>
    private IrBlock _BindStatements(SourceSpan span, IReadOnlyList<IStatement> statements)
    {
        _scope = new Scope(_scope);
        RArray<IrStatement> bound = new();
        foreach (var statement in statements)
        {
            if (_BindStatement(statement) is { } result) bound.Push(result);
        }
        _scope = _scope.Parent!;

        return new IrBlock(span, [.. bound]);
    }

    #endregion
}
