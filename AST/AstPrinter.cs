using System.Text;
using Rgl.AST.Collections;
using Rgl.AST.Datasets;
using Rgl.AST.Descriptors;
using Rgl.AST.Enums;
using Rgl.AST.Expressions;
using Rgl.AST.Extensions;
using Rgl.AST.Files;
using Rgl.AST.Gears;
using Rgl.AST.Generics;
using Rgl.AST.Ingots;
using Rgl.AST.InputOutput;
using Rgl.AST.Interfaces;
using Rgl.AST.Molds;
using Rgl.AST.Notes;
using Rgl.AST.Precasts;
using Rgl.AST.Statements;
using Rgl.AST.Steps;
using Rgl.AST.Train;
using Rgl.AST.Types;
using Rgl.AST.Variables;
using Rgl.Syntax;

namespace Rgl.AST;

/// <summary>
/// Affiche un arbre syntaxique sous forme d'arbre indenté, le format des fichiers <c>.ast</c>
/// des tests golden : un nœud par ligne, indenté de deux espaces par niveau, avec sa position
/// <c>ligne:colonne</c> puis ses données en <c>clé=valeur</c>. Les enfants suivent dans
/// l'ordre du source.
/// </summary>
/// <example>
/// <code>
/// CompilationUnit 1:1
///   TrainDeclaration 1:1 unguarded=false parameters=none
///     Pipeline 1:7
///       DoneStep 1:10
/// </code>
/// </example>
internal static class AstPrinter
{
    /// <summary>Texte affiché à la place d'un nom manquant.</summary>
    private const string MissingName = "<manquant>";

    /// <summary>Affiche <paramref name="node"/> et tous ses descendants.</summary>
    /// <param name="node">Racine de l'arbre à afficher, le plus souvent un <see cref="CompilationUnit"/>.</param>
    /// <returns>L'arbre, une ligne par nœud, chaque ligne terminée par <c>\n</c>.</returns>
    public static string Print(IAstNode node)
    {
        var builder = new StringBuilder();
        _Write(builder, node, depth: 0);
        return builder.ToString();
    }

    /// <summary>Les enfants directs de <paramref name="node"/>, dans l'ordre du source (pour parcourir l'arbre sans le connaître nœud par nœud).</summary>
    public static IEnumerable<IAstNode> ChildrenOf(IAstNode node) => _Describe(node).Children;

    /// <summary>Écrit la ligne de <paramref name="node"/>, puis ses enfants un niveau plus bas.</summary>
    private static void _Write(StringBuilder builder, IAstNode node, int depth)
    {
        var (data, children) = _Describe(node);
        var position = node.Span.StartPosition;

        builder.Append(' ', depth * 2).Append(node.GetType().Name).Append(' ').Append(position.Line).Append(':').Append(position.Column);
        foreach (var item in data) builder.Append(' ').Append(item);
        builder.Append('\n');

        foreach (var child in children) _Write(builder, child, depth + 1);
    }

    /// <summary>Données affichées sur la ligne du nœud (<c>clé=valeur</c>) et enfants, dans l'ordre du source.</summary>
    private static (IEnumerable<string> Data, IEnumerable<IAstNode> Children) _Describe(IAstNode node) => node switch
    {
        CompilationUnit unit => ([], [.. unit.Header is null ? Array.Empty<IAstNode>() : [unit.Header], .. unit.Statements]),
        FileHeader header => (
            [$"kind={(header.Kind is null ? "none" : _Name(header.Kind))}", $"description={(header.Description is { } description ? description.Text : "none")}", $"metadata={_Bool(header.Metadata is not null)}"],
            []),
        ImportDeclaration import => (
            [$"module={_Name(import.Module)}", $"only={(import.Only is null ? "none" : import.Only.Count.ToString())}", .. import.Kind == ImportKind.Plain ? Array.Empty<string>() : [$"kind={import.Kind}"],
             .. import.Alias is null ? Array.Empty<string>() : [$"alias={_Name(import.Alias)}"]],
            import.Only ?? []),
        ExportDeclaration export => ([$"names={string.Join(",", export.Names.Select(n => n.Kind switch { ExportKind.Spread => "..." + _Name(n.Name), ExportKind.Module => "module " + _Name(n.Name), _ => _Name(n.Name) }))}", .. export.IsInline ? ["inline=true"] : Array.Empty<string>()], []),
        ImportName name => ([$"name={_Name(name.Name)}", .. name.Alias is null ? Array.Empty<string>() : [$"alias={_Name(name.Alias)}"]], []),
        TrainBlock block => (
            [$"name={(block.Name is null ? "default" : _Name(block.Name))}"],
            [.. block.Declarations, .. block.Train is null ? Array.Empty<IAstNode>() : [block.Train]]),

        TrainDeclaration train => (
            [$"unguarded={_Bool(train.IsUnguarded)}", $"parameters={train.Parameters?.Count.ToString() ?? "none"}"],
            [
                .. train.Parameters ?? [],
                .. train.StartValue is null ? Array.Empty<IAstNode>() : [train.StartValue],
                train.Pipeline,
                .. train.NativeReturnType is null ? Array.Empty<IAstNode>() : [train.NativeReturnType],
            ]),
        Parameter parameter => ([$"name={_Name(parameter.Name)}", .. _Mut(parameter.IsMutable), .. parameter.IsThis ? ["this=true"] : Array.Empty<string>()], [parameter.Type, .. parameter.Default is null ? Array.Empty<IAstNode>() : [parameter.Default]]),
        DatasetDeclaration dataset => ([$"name={_Name(dataset.Name)}", $"{(dataset.CreatesMold ? "as" : "of")}={_Name(dataset.Mold)}", .. dataset.Source is { } source ? [$"from={source.Span.Text}"] : Array.Empty<string>()], [.. dataset.Entries]),
        DatasetExtension extension => ([$"name={_Name(extension.Name)}"], [.. extension.Entries]),
        DatasetEntry entry => ([$"name={_Name(entry.Name)}"], [entry.Value]),
        PrecastDeclaration precast => ([$"name={_Name(precast.Name)}"], [.. precast.Type is null ? Array.Empty<IAstNode>() : [precast.Type], .. precast.Values, .. precast.Fields]),
        NamespaceDeclaration space => ([$"name={space.Name}"], []),
        ExtendDeclaration extend => ([], [.. extend.TypeParameters ?? [], extend.Target, .. extend.Members]),
        Pipeline pipeline => ([], pipeline.Steps),
        Rail rail => ([], rail.Steps),

        CallStep call => (
            [$"gear={_Name(call.Gear)}"],
            [
                .. call.Checkpoint is null ? Array.Empty<IAstNode>() : [call.Checkpoint],
                .. call.Arguments,
                .. call.Repeat is null ? Array.Empty<IAstNode>() : [call.Repeat],
                .. call.Rail is null ? Array.Empty<IAstNode>() : [call.Rail],
            ]),
        LambdaStep lambda => ([], [lambda.Lambda]),
        Checkpoint checkpoint => ([$"number={checkpoint.Number.Text}"], []),
        Repeat repeat => ([], repeat.Limit is null ? [] : [repeat.Limit]),
        RepeatLimit limit => ([$"count={limit.Count.Text}", $"fails={_Bool(limit.FailsWhenReached)}"], []),
        ResumeStep resume => ([$"number={resume.Number.Text}"], resume.Limit is null ? [] : [resume.Limit]),
        DoneStep => ([], []),
        FailStep fail => ([], fail.Value is null ? [] : [fail.Value]),
        ExitCodeStep exit => ([], [exit.Code]),
        MissingStep => ([], []),

        PrimitiveType primitive => ([$"type={primitive.Span.Text}"], []),
        NamedType named => ([$"name={_Name(named.Name)}"], named.TypeArguments ?? []),
        TypeParameter parameter => ([$"name={_Name(parameter.Name)}"], [.. parameter.Constraint is null ? Array.Empty<IAstNode>() : [parameter.Constraint], .. parameter.ValueType is null ? Array.Empty<IAstNode>() : [parameter.ValueType]]),
        ValueTypeArgument valueArgument => ([], [valueArgument.Value]),
        TypeTestExpression test => ([$"kind={(test.IsStructural ? "accept" : "is")}"], [test.Subject, test.Pattern]),
        ArrayType array => (
            array.Kind == ArrayTypeKind.Dynamic ? [] : [$"kind={array.Kind}"],
            [array.Element, .. array.Size is null ? Array.Empty<IAstNode>() : [array.Size], .. array.Max is null ? Array.Empty<IAstNode>() : [array.Max]]),
        DictionaryType dictionary => ([], [dictionary.Key, dictionary.Value]),
        TupleType tuple => ([], tuple.Elements),
        FunctionType function => (function.IsPure ? ["pure=true"] : [], [.. function.Parameters, function.Return]),
        LambdaExpression lambda => (
            [$"parameters={lambda.Parameters.Count}"],
            [.. lambda.Parameters, .. lambda.Body is null ? Array.Empty<IAstNode>() : [lambda.Body], .. lambda.ExpressionBody is null ? Array.Empty<IAstNode>() : [lambda.ExpressionBody]]),
        LambdaParameter parameter => ([$"name={_Name(parameter.Name)}", .. _Mut(parameter.IsMutable)], parameter.Type is null ? [] : [parameter.Type]),
        TupleTypeElement element => (element.Name is null ? [] : [$"name={_Name(element.Name)}"], [element.Type]),
        ArrayExpression array => ([], array.Elements),
        SpreadElement spread => ([], [spread.Value]),
        DictionaryExpression dictionary => ([], dictionary.Entries),
        DictionaryEntry entry => ([], [entry.Key, entry.Value]),
        TupleExpression tuple => ([], tuple.Elements),
        TupleElement element => (element.Name is null ? [] : [$"name={_Name(element.Name)}"], [element.Value]),
        IndexExpression index => ([], [index.Target, index.Index]),
        MissingType => ([], []),

        LiteralExpression literal => ([$"kind={literal.Kind}", $"text={_Escape(literal.Text)}"], []),
        NameExpression name => ([$"name={_Name(name.Name)}"], []),
        GenericTypeExpression generic => ([], [generic.Target, .. generic.Arguments]),
        CallExpression call => ([], [call.Callee, .. call.TypeArguments ?? [], .. call.Arguments]),
        RawStoreStatement store => ([], [store.Target, store.Value]),
        PointerType pointer => ([$"kind={(pointer.Owned ? "ptr" : "uptr")}"], [pointer.Element]),
        UnaryExpression unary => ([$"op={_Operator(unary.Operator)}"], [unary.Operand]),
        BinaryExpression binary => ([$"op={_Operator(binary.Operator)}"], [binary.Left, binary.Right]),
        ParenthesizedExpression parenthesized => ([], [parenthesized.Inner]),
        MissingExpression => ([], []),
        DollarExpression => ([], []),
        SoaEachExpression rows => ([$"shortest={_Bool(rows.IsShortest)}"], [rows.Value, rows.Target]),
        EachExpression each => ([$"strict={_Bool(each.IsStrict)}"], [each.Value, each.Target]),
        EachTarget target => (target.Name is null ? [] : [$"name={_Name(target.Name)}"], [.. target.Arguments, .. target.Lambda is null ? Array.Empty<IAstNode>() : [target.Lambda]]),
        EachStep each => ([$"strict={_Bool(each.IsStrict)}"], [each.Target, .. each.Rail is null ? Array.Empty<IAstNode>() : [each.Rail]]),
        WithExpression with => ([], [with.Source, .. with.Changes]),
        WithChange change => ([], [change.Path, change.Value]),
        FoldExpression fold => ([], [fold.Value, fold.Seed, fold.Target]),
        FoldStep fold => ([], [fold.Seed, fold.Target, .. fold.Rail is null ? Array.Empty<IAstNode>() : [fold.Rail]]),
        RangeExpression range => ([$"exclusive={_Bool(range.IsExclusive)}"], [range.Start, range.End, .. range.Step is null ? Array.Empty<IAstNode>() : [range.Step]]),
        PipeExpression pipe => ([$"gear={_Name(pipe.Gear)}"], [pipe.Value, .. pipe.Arguments]),
        IncrementExpression increment => (
            increment.Step is null ? [$"op={_Operator(increment.Operator)}"] : [$"op={_Operator(increment.Operator)}", $"step={increment.Step.Text}"],
            [increment.Operand]),
        ConditionalExpression conditional => ([], [conditional.Condition, conditional.WhenTrue, conditional.WhenFalse]),
        TemplateExpression template => ([], template.Parts),
        TemplateText text => ([$"text={_Escape(text.Span.Text)}"], []),
        TemplateInterpolation interpolation => ([], [interpolation.Expression]),

        VarDeclaration declaration => (
            [$"keyword={_Keyword(declaration)}", $"name={_Name(declaration.Name)}", .. _Mut(declaration.IsMutable), .. declaration.IsMove ? ["move=true"] : Array.Empty<string>()],
            [.. _NotesOf(declaration.Notes), .. declaration.Type is null ? Array.Empty<IAstNode>() : [declaration.Type], declaration.Expression]),

        AssignmentStatement assignment => (
            assignment.Operator == TokenKind.Assign
                ? [$"target={_Name(assignment.Target)}"]
                : [$"target={_Name(assignment.Target)}", $"op={_Operator(assignment.Operator)}"],
            [.. assignment.TypeHint is null ? Array.Empty<IAstNode>() : [assignment.TypeHint], assignment.Value]),
        UndefineStatement undefine => ([$"name={_Name(undefine.Name)}"], []),
        ExpressionStatement statement => ([], [statement.Expression]),
        GearDeclaration gear => (
            [$"kind={_Operator(gear.Keyword)}", $"name={_Name(gear.Name)}", $"pure={_Bool(gear.IsPure)}", $"parameters={gear.Parameters.Count}"],
            [.. _NotesOf(gear.Notes), .. gear.TypeParameters ?? [], .. gear.Parameters, gear.ReturnType, .. gear.Body is null ? Array.Empty<IAstNode>() : [gear.Body], .. gear.ExpressionBody is null ? Array.Empty<IAstNode>() : [gear.ExpressionBody]]),
        ReturnStatement returnStatement => ([], returnStatement.Value is null ? [] : [returnStatement.Value]),
        FailStatement fail => ([], fail.Message is null ? [] : [fail.Message]),
        CancelStatement cancel => ([], [cancel.Operand]),
        PanicStatement panic => ([], panic.Message is null ? [] : [panic.Message]),
        DoneStatement done => ([], done.Value is null ? Array.Empty<IAstNode>() : [done.Value]),
        SpawnExpression spawn => ([], [.. spawn.Options is null ? Array.Empty<IAstNode>() : [spawn.Options], .. spawn.Parts]),
        AwaitExpression await => ([], [await.Operand]),
        TaskCallExpression task => ([], [task.Call]),
        CppExpression cpp => (["arguments=" + cpp.Arguments.Count], [.. cpp.Arguments, cpp.ReturnType]),
        CppArgument argument => ([], [argument.Type, argument.Name, argument.Value]),
        NativeCallExpression native => ([], [native.Call]),
        DescriptorDeclaration descriptor => ([$"type={descriptor.CppType.Text}", $"name={_Name(descriptor.Name)}"], descriptor.Members),
        DescriptorMember member => ([$"kind={member.Kind}", $"name={_Name(member.Name)}", .. member.IsConst ? ["const=true"] : Array.Empty<string>(), $"cpp={member.Expression.Text}"], [.. member.Parameters, .. member.Type is null ? Array.Empty<IAstNode>() : [member.Type]]),
        DeclarationNotes notes => ([], [.. notes.Annotations, .. notes.Metadata]),
        DeprecatedAnnotation deprecated => ([], deprecated.Message is null ? [] : [deprecated.Message]),
        TapAnnotation tap => ([$"mode={tap.Mode}", .. tap.IsClosure ? ["closure=true"] : Array.Empty<string>(), .. tap.Target is null ? Array.Empty<string>() : [$"target={_Name(tap.Target)}"]],
            [tap.Value, .. tap.Path is null ? Array.Empty<IAstNode>() : [tap.Path]]),
        OtherAnnotation other => ([$"name={_Name(other.Name)}"], []),
        BeforeAnnotation before => ([], [before.Check]),
        CustomAnnotation custom => ([$"name={_Name(custom.Name)}"], custom.Arguments),
        AnnotationDeclaration declaration => ([$"name={_Name(declaration.Name)}"], [.. declaration.Parameters, .. declaration.Body]),
        MetadataBlock metadata => ([], metadata.Entries),
        MetadataEntry entry => ([$"key={_Name(entry.Key)}"], entry.Value is null ? [] : [entry.Value]),
        WriteStatement write => ([$"target={_Name(write.Target)}"], [write.Value, .. write.Custom is null ? Array.Empty<IAstNode>() : [write.Custom], .. write.Path is null ? Array.Empty<IAstNode>() : [write.Path]]),
        ReadExpression read => ([$"source={_Name(read.Source)}"], [.. read.Custom is null ? Array.Empty<IAstNode>() : [read.Custom], .. read.Path is null ? Array.Empty<IAstNode>() : [read.Path]]),
        CustomFormat custom => (custom.Train is null ? [] : [$"train={_Name(custom.Train)}"], [custom.Type]),
        CastExpression cast => ([$"kind={cast.Kind}"], [cast.Value, cast.Type]),
        MoldDeclaration mold => ([$"name={_Name(mold.Name)}", .. mold.IsError ? ["error=true"] : Array.Empty<string>(), .. mold.IsSoa ? ["soa=true"] : Array.Empty<string>(), .. mold.Destructor is { } destructor ? [$"destructor={_Name(destructor)}"] : Array.Empty<string>()], [.. _NotesOf(mold.Notes), .. mold.TypeParameters ?? [], .. mold.Members]),
        MoldField field => ([$"name={_Name(field.Name)}"], [field.Type, .. field.Default is null ? Array.Empty<IAstNode>() : [field.Default]]),
        MoldSpread spread => ([], [spread.Source]),
        MoldOperator overload => ([$"operator={overload.Operator}", $"pure={_Bool(overload.IsPure)}", .. overload.IsMutable ? ["mutable=true"] : Array.Empty<string>()], [overload.ReturnType, .. overload.Parameters, overload.Body]),
        AnonymousMoldType anonymous => ([], anonymous.Members),
        MoldExpression mold => ([], [.. mold.Type is null ? Array.Empty<IAstNode>() : [mold.Type], .. mold.Entries]),
        MoldFieldValue field => (field.IsShorthand ? [$"name={_Name(field.Name)}", "shorthand=true"] : [$"name={_Name(field.Name)}"], field.IsShorthand ? [] : [field.Value]),
        MoldSpreadValue spread => ([], [spread.Value]),
        MemberAccessExpression access => ([$"member={_Name(access.Member)}", .. access.IsSafe ? ["safe=true"] : Array.Empty<string>(), .. access.Namespace is null ? Array.Empty<string>() : [$"namespace={access.Namespace}"]], [access.Target]),
        NullableType nullable => ([], [nullable.Inner]),
        BoundedType bounded => ([], [bounded.Base, bounded.Min, bounded.Max]),
        IngotDeclaration ingot => ([$"name={_Name(ingot.Name)}"], [.. _NotesOf(ingot.Notes), ingot.Base]),
        UnbrandExpression unbrand => ([], [unbrand.Value]),
        UnionType union => ([], union.Members),
        EnumDeclaration enumeration => ([$"name={_Name(enumeration.Name)}"], [.. _NotesOf(enumeration.Notes), .. enumeration.Underlying is null ? Array.Empty<IAstNode>() : [enumeration.Underlying], .. enumeration.Variants]),
        EnumVariant variant => ([$"name={_Name(variant.Name)}"], variant.Value is null ? [] : [variant.Value]),
        ResultType result => ([], [result.Value, result.Error]),
        MatchExpression match => ([], [match.Subject, .. match.Arms]),
        MatchArm arm => (
            arm.Name is null ? [$"kind={arm.Kind}"] : [$"kind={arm.Kind}", $"name={_Name(arm.Name)}"],
            [.. arm.Type is null ? Array.Empty<IAstNode>() : [arm.Type], .. arm.Values ?? [], .. arm.Condition is null ? Array.Empty<IAstNode>() : [arm.Condition], .. arm.Value is null ? Array.Empty<IAstNode>() : [arm.Value],
             .. arm.Body is null ? Array.Empty<IAstNode>() : [arm.Body]]),
        FieldAssignmentStatement assignment => (
            assignment.Operator == TokenKind.Assign ? [] : [$"op={_Operator(assignment.Operator)}"],
            [assignment.Target, assignment.Value]),
        IfStatement ifStatement => ([], [ifStatement.Condition, ifStatement.Then, .. ifStatement.Else is null ? Array.Empty<IAstNode>() : [ifStatement.Else]]),
        SwitchStatement switchStatement => ([], [switchStatement.Subject, .. switchStatement.Sections]),
        SwitchSection section => (
            [$"kind={section.Kind}"],
            [.. section.Values, .. section.Condition is null ? Array.Empty<IAstNode>() : [section.Condition], .. section.Body]),
        BreakStatement breakStatement => (
            breakStatement.All ? ["depth=all"] : breakStatement.Depth is null ? [] : [$"depth={breakStatement.Depth.Text}"], []),
        GotoStatement gotoStatement => ([], [gotoStatement.Target]),
        BlockStatement block => (block.IsArena ? ["arena=true"] : [], [.. block.ArenaSize is { } size ? [size] : Array.Empty<IAstNode>(), .. block.Statements]),

        Identifier identifier => ([$"name={_Name(identifier)}"], []),

        _ => throw new ArgumentException($"Nœud sans affichage : {node.GetType().Name}. Ajouter son cas dans AstPrinter.", nameof(node)),
    };

    private static string _Bool(bool value) => value ? "true" : "false";

    /// <summary>Mot-clé de la déclaration : <c>var</c>, <c>const</c>, ou <c>none</c> pour une déclaration typée.</summary>
    private static string _Keyword(VarDeclaration declaration) =>
        declaration.IsIntef ? "var" : declaration.IsConst ? "const" : "none";

    /// <summary>L'attribut <c>mut=true</c> d'une déclaration ou d'un paramètre <c>mut</c> ; rien sinon.</summary>
    private static string[] _Mut(bool isMutable) => isMutable ? ["mut=true"] : [];

    /// <summary>Les notes d'une déclaration, enfants de son nœud ; aucun sans notes.</summary>
    private static IAstNode[] _NotesOf(DeclarationNotes? notes) => notes is null ? [] : [notes];

    private static string _Name(Identifier identifier) => identifier.IsMissing ? MissingName : identifier.Name;

    /// <summary>Texte de l'opérateur (<c>+</c>, <c>eq</c>), ou le nom de son kind s'il n'en a pas.</summary>
    private static string _Operator(TokenKind kind) => SyntaxFacts.GetText(kind).ContinueIfNull(kind.ToString);

    /// <summary>
    /// Garde une ligne par nœud : les vrais sauts de ligne et tabulations d'un littéral (template
    /// sur plusieurs lignes) s'affichent <c>\n</c>, <c>\r</c>, <c>\t</c>. Les antislashs écrits
    /// dans le source ne sont pas doublés, pour que <c>"a\n"</c> reste lisible.
    /// </summary>
    private static string _Escape(string text) =>
        text.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
}
