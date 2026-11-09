using Rgl.AST;
using Rgl.AST.Enums;
using Rgl.AST.Expressions;
using Rgl.AST.Files;
using Rgl.AST.Gears;
using Rgl.AST.Generics;
using Rgl.AST.Ingots;
using Rgl.AST.Interfaces;
using Rgl.AST.Molds;
using Rgl.AST.Steps;
using Rgl.AST.Train;
using Rgl.AST.Types;
using Rgl.AST.Variables;
using Rgl.Syntax;
using Rgl.Text;

namespace Rgl.Lsp;

/// <summary>Ce qui est sous le curseur : le mot, et ce qu'il désigne s'il désigne une déclaration.</summary>
/// <param name="Range">Le mot dans le document.</param>
/// <param name="Symbol">La déclaration désignée ; <see langword="null"/> pour un mot-clé ou un nom inconnu.</param>
/// <param name="Token">Le token du mot.</param>
internal sealed record Target(SourceSpan Range, RglSymbol? Symbol, Token Token);

/// <summary>Retrouve ce que désigne le mot placé sous le curseur, pour le survol et l'accès à la définition.</summary>
internal sealed partial class SemanticDocument
{
    /// <summary>Le token qui contient <paramref name="offset"/> ; <see langword="null"/> entre deux tokens ou sur du blanc.</summary>
    public Token? TokenContaining(int offset)
    {
        var tokens = Self.Tokens;
        int low = 0, high = tokens.Count - 1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            var token = tokens[middle];
            if (offset < token.Span.Start.Value) high = middle - 1;
            else if (offset >= token.Span.End.Value) low = middle + 1;
            else return token;
        }
        return null;
    }

    /// <summary>Vrai si le token qui suit <paramref name="token"/> est un point : <c>std</c> dans <c>std.nom</c>.</summary>
    public bool IsFollowedByDot(Token token)
    {
        var next = TokenContaining((int)token.Span.End.Value);
        return next is { Kind: TokenKind.Dot };
    }

    /// <summary>Ce que désigne le mot en <paramref name="offset"/> ; <see langword="null"/> si le curseur n'est pas sur un mot.</summary>
    public Target? TargetAt(int offset)
    {
        var token = TokenContaining(offset) ?? (offset > 0 ? TokenContaining(offset - 1) : null);
        if (token is null || token.Kind == TokenKind.EndOfFile) return null;
        if (token.Kind != TokenKind.Identifier) return new Target(token.Span, null, token);

        var start = (int)token.Span.Start.Value;
        if (start > 0 && TokenContaining(start - 1) is { Kind: TokenKind.At })
            return new Target(token.Span, AnnotationSymbols().FirstOrDefault(s => s.Name == token.Text), token);
        var path = PathAt(start, inclusiveEnd: true);
        for (var i = path.Count - 1; i >= 0; --i)
        {
            if (_SymbolFor(path, i, token, start) is { } symbol) return new Target(token.Span, symbol, token);
        }
        return new Target(token.Span, ResolveName(token.Text, start), token);
    }

    /// <summary>Ce que désigne <paramref name="token"/> dans le nœud <c>path[index]</c> ; <see langword="null"/> si ce nœud ne le nomme pas.</summary>
    private RglSymbol? _SymbolFor(List<IAstNode> path, int index, Token token, int start)
    {
        bool On(Identifier? id) => id is not null && !id.IsMissing && id.Span.Start.Value <= start && start < id.Span.End.Value;

        switch (path[index])
        {
            case MemberAccessExpression member when member.Member.Span.Start.Value == start:
                return _ResolveMember(member, start);

            case NameExpression name when On(name.Name):
                return ResolveName(name.Name.Name, start);

            case NamedType named when On(named.Name):
                return _ResolveQualified(named.Name, start, type: true);

            case CallStep step when On(step.Gear):
                return _ResolveQualified(step.Gear, start, type: false);

            case PipeExpression pipe when On(pipe.Gear):
                return _ResolveQualified(pipe.Gear, start, type: false);

            case EachTarget target when On(target.Name):
                return _ResolveQualified(target.Name!, start, type: false);

            case ImportDeclaration import when On(import.Module):
                return Project.Find(import.Module.Name.Split('.')[^1]) is { } importedModule ? ModuleSymbol(importedModule) : null;

            case ImportName importName when On(importName.Name):
                return path.ElementAtOrDefault(index - 1) is ImportDeclaration importOwner && Project.Find(importOwner.Module.Name) is { } source
                    && source.Exported.TryGetValue(importName.Name.Name, out var importedSymbol) ? importedSymbol : null;

            case GearDeclaration or MoldDeclaration or EnumDeclaration or IngotDeclaration when On(_NameOf(path[index])):
                return _DeclaredSymbol(path[index]);

            case VarDeclaration declaration when On(declaration.Name):
                return declaration.IsConst && Self.Symbols.TryGetValue(declaration.Name.Name, out var constant) && constant.NameSpan == declaration.Name.Span ? constant : _VariableSymbol(declaration);

            case Parameter parameter when On(parameter.Name):
                return _ParameterSymbol(parameter);

            case LambdaParameter lambda when On(lambda.Name):
                return new RglSymbol(lambda.Name.Name, RglSymbolKind.Parameter, lambda.Name.Span, lambda, Self, lambda.Type is null ? null : Signatures.Collapse(lambda.Type.Span.Text));

            case TypeParameter typeParameter when On(typeParameter.Name):
                return typeParameter.ValueType is null
                    ? new RglSymbol(typeParameter.Name.Name, RglSymbolKind.TypeParameter, typeParameter.Name.Span, typeParameter, Self, typeParameter.Constraint is null ? null : Signatures.Collapse(typeParameter.Constraint.Span.Text))
                    : new RglSymbol(typeParameter.Name.Name, RglSymbolKind.Constant, typeParameter.Name.Span, typeParameter, Self, Signatures.Collapse(typeParameter.ValueType.Span.Text));

            case MoldField field when On(field.Name):
                return path.ElementAtOrDefault(index - 1) is MoldDeclaration moldOwner
                    ? new RglSymbol(field.Name.Name, RglSymbolKind.Field, field.Name.Span, field, Self, Signatures.Collapse(field.Type.Span.Text), Container: moldOwner.Name.Name, Documentation: Self.DocumentationFor(field.Span))
                    : null;

            case EnumVariant variant when On(variant.Name):
                return path.ElementAtOrDefault(index - 1) is EnumDeclaration enumeration
                    ? new RglSymbol(variant.Name.Name, RglSymbolKind.Variant, variant.Name.Span, variant, Self, variant.Value is null ? null : Signatures.Collapse(variant.Value.Span.Text), Container: enumeration.Name.Name, Documentation: Self.DocumentationFor(variant.Span))
                    : null;

            case MoldFieldValue value when On(value.Name):
                return path.ElementAtOrDefault(index - 1) is MoldExpression { Type: { } valueType } && MembersOf(Signatures.Collapse(valueType.Span.Text), start).FirstOrDefault(m => m.Name == value.Name.Name) is { } valueField
                    ? valueField
                    : null;

            default:
                return null;
        }
    }

    private static Identifier? _NameOf(IAstNode node) => node switch
    {
        GearDeclaration gear => gear.Name,
        MoldDeclaration mold => mold.Name,
        EnumDeclaration enumeration => enumeration.Name,
        IngotDeclaration ingot => ingot.Name,
        _ => null,
    };

    /// <summary>Le symbole d'une déclaration de premier niveau de ce document, ou d'une déclaration locale à un train.</summary>
    private RglSymbol? _DeclaredSymbol(IAstNode declaration)
    {
        var name = _NameOf(declaration)!;
        if (Self.Symbols.TryGetValue(name.Name, out var symbol) && symbol.NameSpan == name.Span) return symbol;
        return ModuleInfo.SymbolsOf([(IStatement)declaration], Self).FirstOrDefault();
    }

    /// <summary>
    /// Un nom écrit avec ou sans qualificatif (<c>Geometry.Vec2</c>) : le curseur sur le qualificatif désigne le fichier, sur le dernier
    /// nom la déclaration que le fichier exporte.
    /// </summary>
    private RglSymbol? _ResolveQualified(Identifier identifier, int start, bool type)
    {
        var text = identifier.Name;
        var dot = text.IndexOf('.');
        if (dot < 0) return type ? ResolveType(text, start) ?? ResolveName(text, start) : ResolveName(text, start);

        var qualifier = text[..dot];
        var onQualifier = start - (int)identifier.Span.Start.Value <= dot;
        // Moves.id : le dataset, puis le type de ses identifiants.
        if (ResolveName(qualifier, start) is { Kind: RglSymbolKind.Dataset } typedDataset)
            return onQualifier ? typedDataset : DatasetMembers(typedDataset).FirstOrDefault(m => m.Name == text[(dot + 1)..]);
        if (onQualifier) return _modules.TryGetValue(qualifier, out var module) ? ModuleSymbol(module) : null;
        return _qualified.TryGetValue(qualifier, out var names) && names.TryGetValue(text[(dot + 1)..], out var member) ? member : null;
    }

    private RglSymbol? _ResolveMember(MemberAccessExpression member, int offset)
    {
        var name = member.Member.Name;
        // Moves.attaquer, Moves.id, Moves.find : ce que le dataset déclare.
        if (DatasetOf(member.Target, offset) is { } dataset) return DatasetMembers(dataset).FirstOrDefault(m => m.Name == name);
        if (_AsTypeSymbol(member.Target, offset) is { IsType: true } typeSymbol)
        {
            if (typeSymbol.Kind == RglSymbolKind.Enum && VariantsOf(typeSymbol).FirstOrDefault(v => v.Name == name) is { } variant) return variant;
            if (DescriptorEditorModel.Members(typeSymbol, true, SplitGeneric(member.Target.Span.Text).Arguments).FirstOrDefault(m => m.Name == name) is { } native) return native;
            // line.ZERO, Random.seed : ce que les extensions du type lui ajoutent.
            if (ExtensionMethods.StaticMembers(Project, typeSymbol.Name, this).FirstOrDefault(m => m.Name == name) is { } staticMember) return staticMember;
            if (typeSymbol.Kind == RglSymbolKind.Enum) return null;
        }

        if (member.Target is NameExpression qualifier && !_IsValue(qualifier.Name.Name, offset) && QualifiedNames(qualifier.Name.Name) is { } names)
            return names.GetValueOrDefault(name);

        if (TypeOf(member.Target, offset) is not { } target) return null;
        var found = MembersOf(StripNullable(target), offset).FirstOrDefault(m => m.Name == name);
        if (found is null && ExtensionMethods.For(Project, target, this).FirstOrDefault(m => m.Name == name) is { } extension)
        {
            return new RglSymbol(name, RglSymbolKind.Method, extension.Gear.Name.Span, extension.Gear, extension.Module, $"{name}{extension.Parameters}: {extension.Return}",
                Container: StripNullable(target), Documentation: extension.Module.DocumentationFor(extension.Gear.Span));
        }
        return found is null ? null : found with { Container = found.Container ?? StripNullable(target) };
    }
}
