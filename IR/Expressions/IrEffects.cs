namespace Rgl.IR.Expressions;

/// <summary>Parcours des expressions de l'IR : leurs sous-expressions, et ce qui modifie quelque chose.</summary>
internal static class IrEffects
{
    /// <summary>Les sous-expressions directes de <paramref name="expression"/>, dans l'ordre d'évaluation (de gauche à droite).</summary>
    public static IEnumerable<IrExpression> Children(IrExpression expression) => expression switch
    {
        IrRawMemory memory => memory.Arguments,
        IrUnary unary => [unary.Operand],
        IrBinary binary => [binary.Left, binary.Right],
        IrConversion conversion => [conversion.Operand],
        IrMove move => [move.Source],
        IrConditional conditional => [conditional.Condition, conditional.WhenTrue, conditional.WhenFalse],
        IrTemplate template => template.Parts,
        IrCall call => call.Arguments,
        IrNativeCall native => native.Arguments,
        IrParseNumber parse => [parse.Text],
        IrReadFile read => [read.Path],
        IrReadJson read => [read.Path],
        IrReadXml read => [read.Path],
        IrParseJson parse => [parse.Text],
        IrToJson write => [write.Value],
        IrOpenFile open => [open.Path],
        IrSpawn spawn => [spawn.Executable, .. spawn.Arguments],
        IrAwait await => [await.Operand],
        IrSleep sleep => [sleep.Duration],
        IrCancel cancel => [cancel.Operand],
        IrTaskCall task => [task.Call],
        IrWhenAny any => [any.Promises],
        IrBlockOn block => [block.Operand],
        IrAsyncSpawn spawn => [spawn.Executable, .. spawn.Arguments],
        IrAsyncFile file => file.Text is null ? [file.Path] : [file.Path, file.Text],
        IrPromiseState state => [state.Operand],
        IrFileOp operation => operation.Argument is null ? [operation.Target] : [operation.Target, operation.Argument],
        IrStringToBytes bytes => [bytes.Value],
        IrBytesToString text => [text.Value],
        IrMoldValue mold => [.. mold.Parts, .. mold.Fields],
        IrMemberAccess access => [access.Target],
        IrUnionField access => [access.Target],
        IrNonNull nonNull => [nonNull.Value],
        IrCoalesce coalesce => [coalesce.Left, coalesce.Right],
        IrEmptyCoalesce coalesce => [coalesce.Left, coalesce.Right],
        IrSafeAccess access => [access.Target],
        IrTryParseNumber parse => [parse.Text],
        IrToUnion union => [union.Value],
        IrUnionWiden widen => [widen.Value],
        IrWith with => [with.Source, .. with.Changes.SelectMany(c => new[] { c.Target, c.Value })],
        IrUnionConvert convert => [convert.Value],
        IrResultValue result => [result.Value],
        IrRetype retype => [retype.Value],
        IrBoundCheck check => [check.Value],
        IrNumericCast cast => [cast.Value],
        IrRegexOp operation => operation.Replacement is null ? [operation.Target, operation.Subject] : [operation.Target, operation.Subject, operation.Replacement],
        IrDurationMilliseconds milliseconds => [milliseconds.Value],
        IrDurationOf duration => [duration.Value],
        IrDateTimeOf date => [date.Value],
        IrTryCast cast => [cast.Value],
        IrArrayValue array => array.Parts.Select(p => p.Value),
        IrDictionaryValue dictionary => dictionary.Entries.SelectMany(e => new[] { e.Key, e.Value }),
        IrTupleValue tuple => tuple.Elements,
        IrIndex index => [index.Target, index.Index],
        IrTupleItem item => [item.Target],
        IrLength length => [length.Target],
        IrRowCount rows => rows.Columns,
        IrPoolValue pool => [pool.Value],
        IrContains contains => [contains.Target, contains.Key],
        IrDictionaryParts parts => [parts.Target],
        IrDictionaryPut put => [put.Target, put.Key, put.Value],
        IrDictionaryWithout without => [without.Target, without.Key],
        IrDatasetFind find => [find.Names, find.Text],
        IrPush push => [push.Target, push.Value],
        IrAdd add => [add.Target, add.Key, add.Value],
        IrArrayPack pack => [pack.Value],
        IrArrayConvert convert => [convert.Value],
        IrInvoke invoke => [invoke.Target, .. invoke.Arguments],
        IrEach each => [each.Source, .. each.Stages.Select(s => s.Apply), .. each.Fold is { } fold ? [fold.Seed, fold.Apply] : Array.Empty<IrExpression>()],
        IrRange range => [range.Start, range.End, range.Step],
        IrMatch match => [match.Subject, .. match.Arms.Where(a => a.Test is not null).Select(a => a.Test!), .. match.Arms.Where(a => a.Value is not null).Select(a => a.Value!)],
        _ => [],
    };

    /// <summary>
    /// Indique si évaluer <paramref name="expression"/> a un effet : modifier une variable
    /// (<see cref="IrIncrement"/>, <see cref="IrPush"/>, <see cref="IrAdd"/>, un argument <c>mut</c>), lire la console
    /// (<see cref="IrRead"/>), appeler un gear ou une lambda qui n'est pas <c>pure</c>. Créer une lambda
    /// n'exécute pas son corps : ce n'est pas un effet.
    /// </summary>
    public static bool HasEffects(IrExpression expression) =>
        expression is IrRawMemory { Operation: RawMemoryOperation.Allocate or RawMemoryOperation.Delete or RawMemoryOperation.Store or RawMemoryOperation.Transfer } or IrIncrement or IrRead or IrNow or IrReadFile or IrReadJson or IrReadXml or IrOpenFile or IrFileOp or IrSpawn or IrAwait or IrBlockOn or IrSleep or IrCancel or IrTaskCall or IrNativeCall or IrCall { Gear.IsPure: false } or IrInvoke { Function.IsPure: false } or IrPush or IrAdd
        || (expression is IrCall call && call.Gear.Parameters.Any(p => p.IsMutable))
        || Children(expression).Any(HasEffects);
}
