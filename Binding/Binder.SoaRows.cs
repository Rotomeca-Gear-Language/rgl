using Rgl.AST.Expressions;
using Rgl.Diagnostics;
using Rgl.IR.Expressions;
using Rgl.IR.Symbols;
using Rgl.IR.Train;
using Rgl.IR.Types;

namespace Rgl.Binding;

/// <summary>
/// Le parcours des lignes d'un <c>soa mold</c> : <c>particles |S|&gt; damp</c> appelle <c>damp</c> pour chaque rang, avec la
/// valeur de chaque colonne dans le paramètre du même nom. <c>|S|&gt;</c> exige des colonnes de même taille (le programme
/// s'arrête sinon), <c>||S|&gt;</c> s'arrête à la plus courte. C'est un parcours ordinaire d'un intervalle de rangs :
/// le résultat est le tableau des valeurs rendues, ou rien quand le gear ne renvoie rien.
/// </summary>
internal sealed partial class Binder
{
    private IrExpression? _BindSoaEach(SoaEachExpression rows)
    {
        var operatorName = rows.IsShortest ? "||S|>" : "|S|>";
        if (_BindExpression(rows.Value) is not { } value) return null;
        var source = _Unbranded(value);
        if (source.Type is not MoldType { Symbol: { IsSoa: true } soa })
        {
            _Error(source.Span, DiagnosticCodes.SoaMold, $"« {operatorName} » parcourt un soa mold, pas un « {source.Type} »", $"de type « {source.Type} »",
                "un soa mold s'écrit : soa mold Particles { [float] vx; [float] vy; }");
            return null;
        }
        if (!(source is IrVariableReference or IrParameterReference || source is IrMemberAccess { Target: IrVariableReference or IrParameterReference }))
        {
            _Unsupported(source.Span, $"« {operatorName} » sur autre chose qu'une variable ou un champ de variable");
            return null;
        }

        var target = rows.Target;
        if (target.Lambda is not null || target.Name is null)
        {
            _Unsupported(target.Span, $"« {operatorName} » avec une lambda");
            return null;
        }
        if (!_TryFind(_gears, target.Name.Name, out var gear))
        {
            _Error(target.Name.Span, DiagnosticCodes.UnknownGear, $"« {target.Name.Name} » : aucun gear de ce nom", "gear inconnu", "le gear reçoit les colonnes dans des paramètres du même nom");
            return null;
        }
        if (gear.Kind != GearKind.Gear || gear.Async != AsyncMode.None)
        {
            _Unsupported(target.Name.Span, $"« {operatorName} » avec le {(gear.Kind == GearKind.Gear ? "gear asynchrone" : gear.Kind == GearKind.Guard ? "guard" : "emit")} « {gear.Name} »");
            return null;
        }
        if (target.Arguments.Count > 0 || gear.Parameters.Count == 0)
        {
            _Unsupported(target.Span, $"« {operatorName} » avec des arguments en plus des colonnes");
            return null;
        }

        var row = new IrElementValue(target.Span, PrimitiveType.Long);
        var columns = new List<IrExpression>();
        var arguments = new List<IrExpression?>();
        foreach (var parameter in gear.Parameters)
        {
            var field = soa[parameter.Name];
            if (field is null)
            {
                _Error(target.Name.Span, DiagnosticCodes.SoaMold, $"le paramètre « {parameter.Name} » de « {gear.Name} » ne correspond à aucune colonne de « {soa.Name} »",
                    "pas de colonne de ce nom", $"les colonnes sont : {string.Join(", ", soa.Fields.Select(f => f.Name))}");
                return null;
            }
            var column = new IrMemberAccess(source.Span, source, field);
            columns.Add(column);
            arguments.Add(new IrIndex(target.Span, ((ArrayType)field.Type).Element, column, row, Checked: false));
        }

        if (_BindGearCall(target.Span, target.Name, arguments[0], arguments.Skip(1).ToArray(), asStep: true, resolved: gear) is not IrCall call) return null;

        var count = new IrRowCount(rows.OperatorSpan, columns, Strict: !rows.IsShortest);
        var range = new IrRange(rows.Span, new ArrayType(PrimitiveType.Long), new IrIntegerConstant(rows.Span, PrimitiveType.Long, 0), count,
            new IrIntegerConstant(rows.Span, PrimitiveType.Long, 1), IsExclusive: true);
        var output = call.Type != PrimitiveType.Void ? call.Type : null;
        var stage = new IrEachStage(rows.Span, call, false, null, null, output, false);
        return new IrEach(rows.Span, output is null ? PrimitiveType.Void : new ArrayType(output), range, [stage]);
    }
}
