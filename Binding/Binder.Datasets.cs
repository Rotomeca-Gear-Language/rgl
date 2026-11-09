using Rgl.AST;
using Rgl.AST.Collections;
using Rgl.AST.Datasets;
using Rgl.AST.Expressions;
using Rgl.AST.Interfaces;
using Rgl.AST.Molds;
using Rgl.AST.Variables;
using Rgl.Diagnostics;
using Rgl.IR;
using Rgl.IR.Expressions;
using Rgl.IR.Statements;
using Rgl.IR.Symbols;
using Rgl.IR.Types;
using Rgl.Syntax;
using Rgl.Text;
using AstTypes = Rgl.AST.Types;

namespace Rgl.Binding;

/// <summary>
/// Les datasets (slice A1.2A) : <c>dataset Moves of Move { attaquer: {…}, soin: {…} }</c>. Le binder les désucre en briques existantes : la
/// constante <c>Moves</c> (un <c>[Move]</c> dans l'ordre d'écriture, liée comme un precast), le mold <c>Move</c> quand il est créé
/// (<c>as</c>), un ingot d'identifiants propre au dataset (<c>Moves.id</c>, un <c>uint</c>) et une constante par entrée
/// (<c>Moves.attaquer</c>, l'identifiant de l'entrée). <c>id.get()</c> rend l'entrée.
/// Décisions : un identifiant est la position de l'entrée, le nom ne sert qu'à l'écrire (et aux sauvegardes) ; la table et ses
/// identifiants sont des constantes du fichier, l'identifiant d'un dataset ne se confond pas avec celui d'un autre, même du même
/// mold ; <c>get()</c> vérifie l'identifiant et arrête le programme s'il est hors de la table (un identifiant ne se construit que par
/// <c>Moves.nom</c>, mais un calcul peut le fausser) ; une table mutable n'existe pas encore.
/// </summary>
internal sealed partial class Binder
{
    /// <summary>Les datasets du programme, par nom unique.</summary>
    private readonly Dictionary<string, DatasetSymbol> _datasets = new(StringComparer.Ordinal);

    /// <summary>Le dataset que porte chaque constante de table.</summary>
    private readonly Dictionary<VarDeclaration, DatasetSymbol> _datasetTables = [];

    /// <summary>Les datasets du fichier en cours de liaison, déclarés une fois ses molds connus.</summary>
    private readonly List<(DatasetDeclaration Declaration, DatasetSymbol Symbol)> _pendingDatasets = [];

    /// <summary>Les noms qu'une entrée ne peut pas prendre : ceux des membres d'un dataset.</summary>
    private static readonly HashSet<string> _datasetMembers = new(StringComparer.Ordinal) { "id", "length", "get", "find", "name", "table", "at" };

    /// <summary>
    /// Remplace un dataset par ses déclarations : le mold créé (<c>as</c>) puis la constante de sa table. Rien après une erreur,
    /// signalée ici.
    /// </summary>
    private void _DesugarDataset(DatasetDeclaration dataset, List<IStatement> result)
    {
        if (!_CheckDatasetEntries(dataset)) return;
        // Les textes d'un fichier de données désignent une variante d'enum ou une entrée d'un autre dataset, selon le type du champ qui les reçoit.
        if (dataset.Source is not null)
            foreach (var entry in dataset.Entries) _CollectDataLiterals(entry.Value);

        if (dataset.CreatesMold)
        {
            if (_GeneratedMold(dataset) is not { } mold) return;
            result.Add(mold);
        }

        var type = new AstTypes.ArrayType(dataset.Mold.Span, new AstTypes.NamedType(dataset.Mold));
        var table = new VarDeclaration(dataset.Span, true, false, type, dataset.Name,
            new ArrayExpression(dataset.Span, [.. dataset.Entries.Select(e => e.Value)]));
        var symbol = new DatasetSymbol(dataset.Name.Name, dataset.Name.Span, [.. dataset.Entries.Select(e => e.Name.Name)]);
        _datasetTables[table] = symbol;
        _pendingDatasets.Add((dataset, symbol));
        _precastConstants.Add(table);
        result.Add(table);
    }

    /// <summary>Un dataset a au moins une entrée, aux noms uniques et qui ne prennent pas ceux de ses membres (E138).</summary>
    private bool _CheckDatasetEntries(DatasetDeclaration dataset)
    {
        var valid = true;
        if (dataset.Entries.Count == 0 && !dataset.Name.IsMissing)
        {
            _Error(dataset.Name.Span, DiagnosticCodes.InvalidDataset, $"le dataset « {dataset.Name.Name} » n'a aucune entrée", "vide",
                "un dataset a au moins une entrée, par exemple : attaquer: {name: 'Attaquer', power: 5}");
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in dataset.Entries)
        {
            if (entry.Name.IsMissing) { valid = false; continue; }
            if (_datasetMembers.Contains(entry.Name.Name))
            {
                _Error(entry.Name.Span, DiagnosticCodes.InvalidDataset, $"« {entry.Name.Name} » est un membre de tout dataset : une entrée ne peut pas le prendre", "nom réservé",
                    $"les noms {string.Join(", ", _datasetMembers.Order(StringComparer.Ordinal))} sont réservés ; choisis-en un autre");
                valid = false;
            }
            else if (!seen.Add(entry.Name.Name))
            {
                _Error(entry.Name.Span, DiagnosticCodes.InvalidDataset, $"l'entrée « {entry.Name.Name} » est déjà dans le dataset « {dataset.Name.Name} »", "en double",
                    "chaque entrée a un nom unique : c'est lui qui donne son identifiant");
                valid = false;
            }
        }
        return valid;
    }

    /// <summary>
    /// Le mold d'un dataset <c>as</c>, déduit de sa première entrée : chaque champ y est écrit avec une valeur littérale (entier, nombre à
    /// virgule, texte, booléen), d'où vient son type. Les bornes, les enums et les références à un autre dataset demandent un mold
    /// déclaré (<c>of</c>) : E138 sinon.
    /// </summary>
    private MoldDeclaration? _GeneratedMold(DatasetDeclaration dataset)
    {
        if (dataset.Entries[0].Value is not MoldExpression { Type: null } first)
        {
            _Error(dataset.Entries[0].Value.Span, DiagnosticCodes.InvalidDataset, $"le mold « {dataset.Mold.Name} » se déduit de valeurs écrites {{champ: valeur}}", "pas une valeur de mold",
                $"écris chaque entrée comme {{champ: valeur}}, ou déclare le mold et écris : dataset {dataset.Name.Name} of {dataset.Mold.Name} {{ … }}");
            return null;
        }

        var members = new List<IMoldMember>();
        foreach (var field in first.Entries.OfType<MoldFieldValue>())
        {
            var type = _FieldTypeOf(field);
            if (type is null)
            {
                _Error(field.Value.Span, DiagnosticCodes.InvalidDataset, $"le type du champ « {field.Name.Name} » ne se déduit pas de cette valeur", "type inconnu",
                    $"un mold créé par « as » n'a que des champs dont la valeur est un littéral (entier, nombre, texte, booléen) ; pour d'autres types, déclare « {dataset.Mold.Name} » et écris « of {dataset.Mold.Name} »");
                return null;
            }
            members.Add(new MoldField(field.Span, type, field.Name, null));
        }
        if (first.Entries.Any(e => e is not MoldFieldValue))
        {
            _Error(first.Span, DiagnosticCodes.InvalidDataset, "un mold créé par « as » ne se déduit pas d'une valeur qui étale un autre mold", "étalement",
                $"déclare « {dataset.Mold.Name} » et écris « of {dataset.Mold.Name} »");
            return null;
        }
        return new MoldDeclaration(dataset.Span, dataset.Mold, members);
    }

    /// <summary>Le type que donne à un champ sa valeur littérale ; <see langword="null"/> si elle n'en donne pas.</summary>
    private static ITypeNode? _FieldTypeOf(MoldFieldValue field) => _ValueTypeOf(field.Value);

    /// <summary>Le type d'une valeur littérale (entier, nombre à virgule, texte, booléen), ou d'un tableau de littéraux de même sorte.</summary>
    private static ITypeNode? _ValueTypeOf(IExpression value)
    {
        if (value is ArrayExpression { Elements.Count: > 0 } array)
            return _ValueTypeOf(array.Elements[0]) is { } element ? new AstTypes.ArrayType(array.Span, element) : null;

        var literal = value switch
        {
            UnaryExpression { Operator: TokenKind.Minus, Operand: LiteralExpression number } => number,
            LiteralExpression text => text,
            _ => null,
        };
        TokenKind? keyword = literal?.Kind switch
        {
            TokenKind.IntegerLiteral => TokenKind.IntKeyword,
            TokenKind.FloatLiteral => TokenKind.DoubleKeyword,
            TokenKind.StringLiteral or TokenKind.RawStringLiteral or TokenKind.TemplateStringLiteral or TokenKind.MutableStringLiteral => TokenKind.StringKeyword,
            TokenKind.TrueKeyword or TokenKind.FalseKeyword => TokenKind.BoolKeyword,
            _ => null,
        };
        return keyword is { } known ? new AstTypes.PrimitiveType(value.Span, known) : null;
    }

    /// <summary>
    /// Déclare les datasets du fichier avant ses molds, dont les champs peuvent porter leurs identifiants : son ingot d'identifiants
    /// <c>Nom.id</c>, rangé sous la clé <c>Nom::id</c> comme le type d'une extension, et le dataset lui-même.
    /// </summary>
    private void _DeclareDatasets()
    {
        foreach (var (declaration, symbol) in _pendingDatasets)
        {
            var key = _DeclKey(declaration.Name.Name);
            symbol.QualifiedName = key;
            symbol.IdIngot = new IngotSymbol(declaration.Name.Name + ".id", declaration.Name.Span) { Base = new PrimitiveType(PrimitiveKind.UInt), Dataset = symbol };
            _ingots[key + "::id"] = symbol.IdIngot;
            _datasets[key] = symbol;
        }
    }

    /// <summary>Range sur chaque dataset du fichier le mold de ses entrées, une fois les molds connus (E138 s'il est inconnu).</summary>
    private void _ResolveDatasetMolds()
    {
        foreach (var (declaration, symbol) in _pendingDatasets)
        {
            if (!_TryFind(_molds, declaration.Mold.Name, out var mold))
            {
                _Error(declaration.Mold.Span, DiagnosticCodes.InvalidDataset, $"le mold « {declaration.Mold.Name} » du dataset « {declaration.Name.Name} » n'existe pas", "mold inconnu",
                    $"déclare-le : mold {declaration.Mold.Name} {{ … }}, ou fais-le créer par le dataset : dataset {declaration.Name.Name} as {declaration.Mold.Name} {{ … }}");
                continue;
            }
            symbol.Mold = mold;
        }
        _pendingDatasets.Clear();
    }

    /// <summary>Le dataset que désigne <paramref name="target"/> : le nom d'un dataset visible (<c>Moves</c>), ou <c>Fichier.Moves</c> ; <see langword="null"/> sinon.</summary>
    private DatasetSymbol? _DatasetNamed(IExpression target)
    {
        var name = target switch
        {
            NameExpression n => n.Name.Name,
            _ => _QualifiedName(target),
        };
        if (name is null || !_TryFind(_datasets, name, out var dataset)) return null;
        // Une variable de même nom qui n'est pas sa table (un local qui la masque) n'est pas le dataset.
        if (target is NameExpression && _scope[name] is { HasValue: true } entry && entry.Value.Symbol != dataset.Table) return null;
        return dataset;
    }

    /// <summary>
    /// <c>Moves.attaquer</c> : l'identifiant de l'entrée, une constante de type <c>Moves.id</c>. <see langword="null"/> sans erreur si
    /// <paramref name="member"/> n'est pas une entrée : c'est alors un membre ordinaire de la table (<c>Moves.length</c>).
    /// </summary>
    private IrExpression? _DatasetEntry(MemberAccessExpression access, DatasetSymbol dataset)
    {
        var index = dataset.Entries.ToList().IndexOf(access.Member.Name);
        if (index < 0 || dataset.IdIngot is not { } ingot) return null;
        return new IrRetype(access.Span, new IngotType(ingot), new IrIntegerConstant(access.Span, ingot.Base, index));
    }

    /// <summary>
    /// <c>id.get()</c> : l'entrée du dataset que désigne l'identifiant, un mold de la table (<c>joueur.job.get().atk</c>). L'accès est vérifié :
    /// un identifiant faussé par un calcul arrête le programme, comme un index hors d'un tableau.
    /// </summary>
    private IrExpression? _BindDatasetGet(CallExpression call, MemberAccessExpression getter, DatasetSymbol dataset)
    {
        var target = _BindExpression(getter.Target);
        if (target is null) return null;
        if (dataset.Table is not { Type: IR.Types.ArrayType table } variable)
        {
            _Error(call.Span, DiagnosticCodes.InvalidDataset, $"la table du dataset « {dataset.Name} » n'est pas encore liée", "table inconnue",
                $"« {dataset.Name} » se déclare au niveau du fichier, avant les gears qui lisent ses entrées");
            return null;
        }
        return new IrIndex(call.Span, table.Element, new IrVariableReference(getter.Target.Span, variable),
            new IrRetype(getter.Target.Span, dataset.IdIngot!.Base, target), Checked: true);
    }

    /// <summary>
    /// <c>Moves.table()</c> : une copie de la table du dataset, un <c>[Move]</c> ordinaire où l'on ajoute des entrées (<c>push</c>) : une table mutable.
    /// Les identifiants restent des positions : ceux des entrées du dataset ne changent jamais, les nouvelles reçoivent les suivantes
    /// (<c>Moves.at(t.length - 1)</c>) ; on ne retire rien.
    /// </summary>
    private IrExpression? _BindDatasetTable(CallExpression call, DatasetSymbol dataset)
    {
        if (dataset.Table is not { Type: IR.Types.ArrayType } variable)
        {
            _Error(call.Span, DiagnosticCodes.InvalidDataset, $"la table du dataset « {dataset.Name} » n'est pas encore liée", "table inconnue",
                $"« {dataset.Name} » se déclare au niveau du fichier, avant les gears qui lisent ses entrées");
            return null;
        }
        return new IrVariableReference(call.Span, variable);
    }

    /// <summary>
    /// <c>id.get(table)</c> : l'entrée que désigne l'identifiant dans une table mutable (<c>Moves.table()</c> et ses ajouts), ou <c>null</c> si la position n'y est pas
    /// (<c>Move?</c>). L'identifiant et la table sont lus deux fois : ils ne doivent pas avoir d'effet.
    /// </summary>
    private IrExpression? _BindDatasetGetFrom(CallExpression call, MemberAccessExpression getter, DatasetSymbol dataset)
    {
        var id = _BindExpression(getter.Target);
        var table = _BindExpression(call.Arguments[0]);
        if (id is null || table is null) return null;
        if (table.Type is not IR.Types.ArrayType { Kind: ArrayKind.Dynamic } array || array.Element is not MoldType mold || mold.Symbol != dataset.Mold)
        {
            _Error(call.Arguments[0].Span, DiagnosticCodes.InvalidDataset, $"« get(table) » lit une table mutable du dataset « {dataset.Name} », un « [{dataset.Mold?.Name}] »", "table attendue",
                $"une table s'obtient par : [{dataset.Mold?.Name}] t: {dataset.Name}.table();");
            return null;
        }
        if (IrEffects.HasEffects(id) || IrEffects.HasEffects(table))
        {
            _Error(call.Span, DiagnosticCodes.InvalidDataset, "l'identifiant et la table de « get(table) » se lisent deux fois : ils ne doivent pas avoir d'effet", "effet",
                "range-les dans des variables avant");
            return null;
        }

        var nullable = new NullableType(array.Element);
        var position = new IrRetype(getter.Target.Span, dataset.IdIngot!.Base, id);
        var inside = new IrBinary(call.Span, PrimitiveType.Bool, IrBinaryOperator.Less, position, new IrLength(call.Span, table), call.Span);
        var entry = new IrConversion(call.Span, nullable, new IrIndex(call.Span, array.Element, table, position, Checked: false));
        return new IrConditional(call.Span, nullable, inside, entry, new IrConversion(call.Span, nullable, new IrNullConstant(call.Span)));
    }

    /// <summary><c>Moves.at(position)</c> : l'identifiant de cette position ; il n'est vérifié qu'à la lecture (<c>get()</c> de la table du dataset, <c>get(table)</c> d'une table mutable).</summary>
    private IrExpression? _BindDatasetAt(CallExpression call, DatasetSymbol dataset)
    {
        var position = _BindExpected(call.Arguments[0], PrimitiveType.UInt);
        if (position is null) return null;
        var converted = _ConvertTo(position, PrimitiveType.UInt, $"« {dataset.Name}.at » prend une position, un entier non signé");
        return converted is null ? null : new IrRetype(call.Span, new IngotType(dataset.IdIngot!), converted);
    }

    /// <summary>
    /// La constante des noms d'un dataset, <c>[string;N]</c> dans l'ordre des entrées, que <c>name()</c> et <c>find</c> lisent. Liée comme un
    /// precast : écrite en C++ seulement si le programme la lit.
    /// </summary>
    private IrVariableDeclaration _DatasetNames(DatasetSymbol dataset, SourceSpan span)
    {
        var type = new IR.Types.ArrayType(PrimitiveType.String, ArrayKind.Fixed, (uint)dataset.Entries.Count);
        var value = new IrArrayValue(span, type, [.. dataset.Entries.Select(n => new IrArrayPart(new IrStringConstant(span, PrimitiveType.String, n), false))]);
        var variable = new VariableSymbol(dataset.Name + "_names", type, true, span, ConstantFolder.Fold(value));
        _precastVariables.Add(variable);
        dataset.Names = variable;
        return new IrVariableDeclaration(span, variable, value);
    }

    /// <summary><c>id.name()</c> : le nom de l'entrée, une chaîne ; de quoi écrire une sauvegarde sans dépendre des positions.</summary>
    private IrExpression? _BindDatasetName(CallExpression call, MemberAccessExpression member, DatasetSymbol dataset)
    {
        var target = _BindExpression(member.Target);
        if (target is null) return null;
        if (dataset.Names is not { } names)
        {
            _Error(call.Span, DiagnosticCodes.InvalidDataset, $"les noms du dataset « {dataset.Name} » ne sont pas encore liés", "noms inconnus",
                $"« {dataset.Name} » se déclare au niveau du fichier, avant les gears qui lisent ses entrées");
            return null;
        }
        return new IrIndex(call.Span, PrimitiveType.String, new IrVariableReference(member.Target.Span, names),
            new IrRetype(member.Target.Span, dataset.IdIngot!.Base, target), Checked: true);
    }

    /// <summary><c>Moves.find(nom)</c> : l'identifiant de l'entrée de ce nom, <c>null</c> s'il n'y en a pas ; <c>Moves.id?</c>.</summary>
    private IrExpression? _BindDatasetFind(CallExpression call, MemberAccessExpression member, DatasetSymbol dataset)
    {
        var text = _BindExpected(call.Arguments[0], PrimitiveType.String);
        if (text is null) return null;
        var converted = _ConvertTo(text, PrimitiveType.String, $"« {dataset.Name}.find » cherche un nom, une chaîne");
        if (converted is null) return null;
        if (dataset.Names is not { } names)
        {
            _Error(call.Span, DiagnosticCodes.InvalidDataset, $"les noms du dataset « {dataset.Name} » ne sont pas encore liés", "noms inconnus",
                $"« {dataset.Name} » se déclare au niveau du fichier, avant les gears qui lisent ses entrées");
            return null;
        }
        return new IrDatasetFind(call.Span, new NullableType(new IngotType(dataset.IdIngot!)), new IrVariableReference(member.Target.Span, names), converted);
    }

    /// <summary>Les littéraux texte lus d'un fichier de données : ils prennent le sens que leur donne le type du champ.</summary>
    private readonly HashSet<LiteralExpression> _dataLiterals = new(ReferenceEqualityComparer.Instance);

    private void _CollectDataLiterals(IExpression expression)
    {
        switch (expression)
        {
            case LiteralExpression { Kind: TokenKind.StringLiteral or TokenKind.RawStringLiteral } text:
                _dataLiterals.Add(text);
                break;
            case MoldExpression mold:
                foreach (var field in mold.Entries.OfType<MoldFieldValue>()) _CollectDataLiterals(field.Value);
                break;
            case ArrayExpression array:
                foreach (var element in array.Elements) _CollectDataLiterals(element);
                break;
        }
    }

    /// <summary>
    /// Un texte lu d'un fichier de données : une variante d'enum quand le champ en est un (<c>"element": "Feu"</c>), l'identifiant d'une entrée
    /// d'un autre dataset quand le champ en est un (<c>"job": "guerrier"</c>), un texte sinon. E138 si le nom est inconnu.
    /// </summary>
    private IrExpression? _BindDataLiteral(LiteralExpression literal, IrType? expected)
    {
        var text = _BindLiteral(literal);
        if (text is not IrStringConstant { Value: var name }) return text;

        switch (expected is NullableType nullable ? nullable.Inner : expected)
        {
            case EnumType enumeration:
                if (enumeration.Symbol[name] is { } variant) return new IrEnumValue(literal.Span, enumeration, variant.Index);
                _Error(literal.Span, DiagnosticCodes.InvalidDataset, $"« {name} » n'est pas une variante de « {enumeration.Symbol.Name} »", "variante inconnue",
                    $"variantes : {string.Join(", ", enumeration.Symbol.Variants.Select(v => v.Name))}");
                return null;

            case IngotType { Symbol: { Dataset: { } dataset } ingot }:
                var index = dataset.Entries.ToList().IndexOf(name);
                if (index >= 0) return new IrRetype(literal.Span, new IngotType(ingot), new IrIntegerConstant(literal.Span, ingot.Base, index));
                _Error(literal.Span, DiagnosticCodes.InvalidDataset, $"« {name} » n'est pas une entrée du dataset « {dataset.Name} »", "entrée inconnue",
                    $"entrées de {dataset.Name} : {string.Join(", ", dataset.Entries)}");
                return null;

            default:
                return text;
        }
    }

    /// <summary><c>Moves.attaqer</c> : le nom n'est pas une entrée du dataset (E138), avec celles qui existent.</summary>
    private IrExpression? _UnknownDatasetEntry(MemberAccessExpression access, DatasetSymbol dataset)
    {
        _Error(access.Member.Span, DiagnosticCodes.InvalidDataset, $"« {access.Member.Name} » n'est pas une entrée du dataset « {dataset.Name} »", "entrée inconnue",
            $"entrées de {dataset.Name} : {string.Join(", ", dataset.Entries)}", $"un dataset a aussi : length, find(nom) ; et id.get(), id.name() sur un identifiant");
        return null;
    }
}
