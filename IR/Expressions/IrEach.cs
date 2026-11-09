using Rgl.IR.Symbols;
using Rgl.IR.Train;
using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.IR.Expressions;

/// <summary>
/// Le parcours d'un tableau : <c>nums ||&gt; f ||&gt; g</c>. Chaque élément traverse toutes les étapes,
/// de bout en bout, avant le suivant ; le résultat est le tableau <c>[T]</c> des valeurs qui sortent
/// de la dernière étape, ou rien (<c>void</c>) quand elle ne renvoie rien.
/// </summary>
/// <param name="Span">Le parcours dans le source.</param>
/// <param name="Type">Le tableau des valeurs obtenues, ou <c>void</c>.</param>
/// <param name="Source">Le tableau parcouru, évalué une fois.</param>
/// <param name="Stages">Les étapes, dans l'ordre.</param>
/// <param name="Fold">
/// L'accumulation qui suit les étapes (<c>|+&gt;</c>) : le résultat est alors sa valeur, et non un
/// tableau ; <see langword="null"/> sans accumulation.
/// </param>
internal sealed record IrEach(SourceSpan Span, IrType Type, IrExpression Source, IReadOnlyList<IrEachStage> Stages, IrEachFold? Fold = null) : IrExpression(Span, Type)
{
    /// <summary>Vrai quand la première étape modifie les éléments sur place : la source est alors modifiée.</summary>
    public bool Modifies => Stages is [{ Modifies: true }, ..];
}

/// <summary>
/// L'accumulation d'un parcours : l'accumulateur part de <see cref="Seed"/>, puis reçoit le résultat de
/// <see cref="Apply"/> sur lui-même (<see cref="IrAccumulatorValue"/>) et sur chaque élément. Un guard
/// appliqué qui fait <c>done</c> arrête l'accumulation avec sa valeur ; son échec la fait échouer tout entière.
/// </summary>
/// <param name="Span">L'accumulation dans le source.</param>
/// <param name="Seed">La valeur de départ, du type de l'accumulateur.</param>
/// <param name="Apply">L'appel appliqué à l'accumulateur et à l'élément, du type de l'accumulateur.</param>
/// <param name="Guard">Le guard appelé ; <see langword="null"/> pour un gear ou une lambda.</param>
/// <param name="Rail">Le rail de l'accumulation, dans un train ; <see langword="null"/> sans rail.</param>
/// <param name="Type">Le type de l'accumulateur, et du résultat.</param>
internal sealed record IrEachFold(SourceSpan Span, IrExpression Seed, IrExpression Apply, GearSymbol? Guard, IrPath? Rail, IrType Type);

/// <summary>L'accumulateur d'une accumulation, premier argument de son étape.</summary>
/// <param name="Span">L'étape qui le reçoit.</param>
/// <param name="Type">Son type.</param>
internal sealed record IrAccumulatorValue(SourceSpan Span, IrType Type) : IrExpression(Span, Type);

/// <summary>
/// Une étape d'un parcours : ce qui est appliqué à l'élément courant (<see cref="IrElementValue"/>).
/// Un gear ou une lambda transforme l'élément ; un guard le garde s'il passe (sa valeur, ou l'élément
/// lui-même pour un guard <c>void</c>). Avec <c>||&gt;</c>, un élément raté est écarté, après son rail
/// s'il en a un ; avec <c>|!|&gt;</c>, le premier échec abandonne tout le parcours.
/// </summary>
/// <param name="Span">L'étape dans le source.</param>
/// <param name="Apply">L'appel appliqué à l'élément courant.</param>
/// <param name="IsStrict">Vrai pour <c>|!|&gt;</c>.</param>
/// <param name="Guard">Le guard appelé, s'il peut échouer ; <see langword="null"/> sinon.</param>
/// <param name="Rail">Le rail de l'étape, dans un train : par élément avec <c>||&gt;</c>, final avec <c>|!|&gt;</c>.</param>
/// <param name="Output">Le type de l'élément après l'étape ; <see langword="null"/> si elle ne renvoie rien.</param>
/// <param name="Modifies">
/// Vrai quand l'étape reçoit l'élément dans un paramètre <c>mut</c> : elle le modifie sur place, dans le
/// tableau parcouru (une variable <c>mut</c>, ou son contenu). Seule la première étape le peut.
/// </param>
internal sealed record IrEachStage(SourceSpan Span, IrExpression Apply, bool IsStrict, GearSymbol? Guard, IrPath? Rail, IrType? Output, bool Modifies = false);

/// <summary>L'élément courant d'un parcours, premier argument de son étape.</summary>
/// <param name="Span">L'étape qui le reçoit.</param>
/// <param name="Type">Son type.</param>
internal sealed record IrElementValue(SourceSpan Span, IrType Type) : IrExpression(Span, Type);

/// <summary>Le rang de l'élément courant d'un parcours, depuis 0 : le dernier paramètre de la lambda d'une étape, un <c>int</c>.</summary>
/// <param name="Span">L'étape qui le reçoit.</param>
internal sealed record IrIndexValue(SourceSpan Span) : IrExpression(Span, PrimitiveType.Int);
