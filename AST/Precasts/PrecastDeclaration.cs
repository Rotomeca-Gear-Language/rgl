using Rgl.AST.Expressions;
using Rgl.AST.Interfaces;
using Rgl.AST.Molds;
using Rgl.Text;

namespace Rgl.AST.Precasts;

/// <summary>
/// Un precast : une instance nommée d'un mold, entièrement résolue à la compilation. Trois formes :
/// <c>precast Vec2 Zero { x: 0.0, y: 0.0 }</c> (le mold, importé, avant le nom),
/// <c>precast {string name, bool debug} AppConfig { name: "MonApp"; debug: false; }</c> (le mold écrit en anonyme) et
/// <c>precast AppConfig { string name: "MonApp"; bool debug: false; }</c> (le mold déclaré par les champs mêmes).
/// </summary>
/// <param name="Span">Du mot <c>precast</c> à l'accolade fermante.</param>
/// <param name="Type">Le mold écrit avant le nom ; <see langword="null"/> quand les champs le déclarent.</param>
/// <param name="Name">Le nom du precast.</param>
/// <param name="Values">Les valeurs <c>champ: valeur</c> des deux premières formes.</param>
/// <param name="Fields">Les champs typés avec leur valeur de la troisième forme (<c>Type champ: valeur</c>).</param>
internal sealed record PrecastDeclaration(SourceSpan Span, ITypeNode? Type, Identifier Name, IReadOnlyList<MoldFieldValue> Values, IReadOnlyList<MoldField> Fields) : IStatement;
