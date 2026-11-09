using System.Globalization;
using System.Text;
using Rgl.IR;
using Rgl.IR.Expressions;
using Rgl.IR.Gears;
using Rgl.IR.Statements;
using Rgl.IR.Symbols;
using Rgl.IR.Train;
using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.Emit;

/// <summary>
/// Les molds et les enums : un struct par mold et un enum class par enum, écrits seulement
/// s'ils servent (avec rgl_write s'ils s'écrivent en texte), et les valeurs et champs de mold.
/// </summary>
internal sealed partial class CppEmitter
{
    /// <summary>
    /// Un struct par mold utilisé, chacun après ceux qu'il contient : ses champs (<c>rgl_f_nom</c>, dans
    /// l'ordre du mold), l'égalité champ par champ (<c>operator==</c> par défaut du C++20) et, s'il
    /// s'écrit en texte, une fonction <c>rgl_write</c> que <c>rgl::standard::interpolate</c> trouve par
    /// ADL. Un mold jamais nommé dans le code (une valeur <c>{…}</c> qui a pris un autre type) n'en a pas.
    /// </summary>
    private string _Structs(IReadOnlyList<MoldSymbol> molds, bool allLayouts = true)
    {
        var output = new StringBuilder();
        var written = new HashSet<MoldSymbol>();
        var layouts = new HashSet<MoldSymbol>();

        // Le Layout d'un mold rangé en colonnes : après son struct, et après les Layouts de ses colonnes.
        void WriteLayout(MoldSymbol mold)
        {
            if (!layouts.Add(mold)) return;
            Write(mold);
            foreach (var inner in mold.Fields.SelectMany(f => _SoaMoldsIn(f.Type)).ToArray()) WriteLayout(inner);
            output.Append(_Layout(mold));
        }

        void Write(MoldSymbol mold)
        {
            if (!written.Add(mold)) return;
            foreach (var field in mold.Fields)
            {
                foreach (var inner in _ContainedMolds(field.Type)) Write(inner);
            }
            // Un champ [Autre] en colonnes : le Layout d'Autre doit précéder ce struct.
            foreach (var inner in mold.Fields.SelectMany(f => _SoaMoldsIn(f.Type)).ToArray()) WriteLayout(inner);

            var name = _moldNames[mold];
            output.Append("struct ").Append(name).Append(" {\n");
            // Les champs s'écrivent dans l'ordre physique (alignement décroissant : pas de padding entre eux) ; l'ordre du mold reste celui
            // du texte, des valeurs écrites et du constructeur.
            var physical = _PhysicalFields(mold);
            foreach (var field in physical)
                output.Append(Indent).Append(_Type(field.Type)).Append(" rgl_f_").Append(field.Name).Append(";\n");
            var lifecycle = LifecycleRules.HasMold(mold);
            if (mold.Destructor is not null)
            {
                _includes.Add("rgl/standard/lifecycle.hpp");
                output.Append(Indent).Append("rgl::standard::lifecycle rgl_life;\n");
            }
            if (mold.Fields.Count > 0 || mold.Destructor is not null) output.Append('\n');
            if (mold.Destructor is not null) output.Append(_LifecycleMembers(mold, name));
            else if (!physical.SequenceEqual(mold.Fields))
            {
                // Réordonné : plus un agrégat dans l'ordre du mold, donc un constructeur qui prend les champs dans cet ordre.
                var parameters = string.Join(", ", mold.Fields.Select((f, i) => $"{_Type(f.Type)} rgl_a{i}"));
                var initializers = string.Join(", ", physical.Select(f => $"rgl_f_{f.Name}(std::move(rgl_a{mold.Fields.ToList().IndexOf(f)}))"));
                // Comme un agrégat, il n'y a de constructeur par défaut que si tous les champs en ont un : sinon celui-ci est supprimé, ce que
                // Clang signale par un avertissement (une erreur sous -Werror) que rien ici ne justifie.
                output.Append("#pragma clang diagnostic push\n#pragma clang diagnostic ignored \"-Wdefaulted-function-deleted\"\n")
                    .Append(Indent).Append("constexpr ").Append(name).Append("() = default;\n")
                    .Append("#pragma clang diagnostic pop\n")
                    .Append(Indent).Append("constexpr ").Append(name).Append('(').Append(parameters).Append(") : ").Append(initializers).Append(" {}\n\n");
            }
            // Deux lambdas ne se comparent pas, ni une valeur à cycle de vie, qui n'a qu'un propriétaire : un mold qui en contient n'a pas d'égalité.
            if (!lifecycle && !FunctionType.IsInside(new MoldType(mold)))
                output.Append(Indent).Append("friend bool operator==(const ").Append(name).Append("&, const ").Append(name).Append("&) = default;\n");

            if (_writtenMolds.Contains(mold) && mold.IsError)
            {
                // Une erreur s'écrit avec l'endroit de son fail : « négatif (main.rgl:3:5) ».
                output.Append('\n').Append(Indent).Append("template <class Out>\n")
                    .Append(Indent).Append("friend void rgl_write(Out& out, const ").Append(name).Append("& value) {\n")
                    .Append(Indent).Append(Indent).Append("out.append(value.rgl_f_message.data(), value.rgl_f_message.size());\n")
                    .Append(Indent).Append(Indent).Append("out.append(\" (\", 2);\n")
                    .Append(Indent).Append(Indent).Append("out.append(value.rgl_f_where.data(), value.rgl_f_where.size());\n")
                    .Append(Indent).Append(Indent).Append("out.push_back(')');\n")
                    .Append(Indent).Append("}\n");
            }
            else if (_writtenMolds.Contains(mold))
            {
                output.Append('\n').Append(Indent).Append("template <class Out>\n")
                    .Append(Indent).Append("friend void rgl_write(Out& out, const ").Append(name).Append("& value) {\n");
                for (var i = 0; i < mold.Fields.Count; ++i)
                {
                    var label = (i == 0 ? "{" : ", ") + mold.Fields[i].Name + ": ";
                    output.Append(Indent).Append(Indent).Append("out.append(\"").Append(label).Append("\", ").Append(Encoding.UTF8.GetByteCount(label)).Append(");\n");
                    output.Append(Indent).Append(Indent).Append("rgl::standard::detail::write_field(out, value.rgl_f_").Append(mold.Fields[i].Name).Append(");\n");
                }
                output.Append(Indent).Append(Indent).Append(mold.Fields.Count == 0 ? "out.append(\"{}\", 2);\n" : "out.push_back('}');\n");
                output.Append(Indent).Append("}\n");
            }

            output.Append("};\n");
            output.Append(_LayoutAssertions(mold, name, physical, lifecycle)).Append('\n');
        }

        foreach (var mold in molds.Where(_usedMolds.Contains).ToArray()) Write(mold);
        // Écrire un Layout peut en demander d'autres (ses colonnes) : jusqu'à ce qu'il n'en manque plus.
        while (allLayouts && _soaMolds.Except(layouts).ToArray() is { Length: > 0 } missing)
            foreach (var mold in missing) WriteLayout(mold);
        return output.ToString();
    }

    /// <summary>
    /// Les <c>static_assert</c> qui vérifient, sur le vrai C++, la disposition que le compilateur a décidée : les champs viennent par
    /// alignement décroissant, chacun à la position qui suit exactement le précédent (aucun padding entre eux) et, sans marque de cycle
    /// de vie qui s'ajoute, la taille du struct est la somme des champs arrondie à son alignement. Si l'alignement d'un type que le
    /// compilateur estime (<see cref="_Alignment"/>) n'est pas celui du C++, le build échoue ici, pas en silence.
    /// </summary>
    private static string _LayoutAssertions(MoldSymbol mold, string name, IReadOnlyList<MoldFieldSymbol> physical, bool lifecycle)
    {
        if (physical.Count == 0) return "";
        var output = new StringBuilder();
        output.Append("#pragma clang diagnostic push\n#pragma clang diagnostic ignored \"-Winvalid-offsetof\"\n");
        var sizes = new List<string>();
        for (var i = 0; i < physical.Count; ++i)
        {
            var field = $"rgl_f_{physical[i].Name}";
            var offset = sizes.Count == 0 ? "0" : string.Join(" + ", sizes);
            output.Append("static_assert(offsetof(").Append(name).Append(", ").Append(field).Append(") == ").Append(offset)
                .Append(", \"disposition de ").Append(name).Append(" : le champ ").Append(physical[i].Name).Append(" n'est pas à la position prévue\");\n");
            if (i > 0)
                output.Append("static_assert(alignof(decltype(").Append(name).Append("::rgl_f_").Append(physical[i - 1].Name).Append(")) >= alignof(decltype(")
                    .Append(name).Append("::").Append(field).Append(")), \"disposition de ").Append(name).Append(" : les champs ne sont pas par alignement décroissant\");\n");
            sizes.Add($"sizeof(decltype({name}::{field}))");
        }
        if (!lifecycle)
            output.Append("static_assert(sizeof(").Append(name).Append(") == ((").Append(string.Join(" + ", sizes)).Append(" + alignof(").Append(name)
                .Append(") - 1) / alignof(").Append(name).Append(")) * alignof(").Append(name).Append("), \"disposition de ").Append(name).Append(" : de la place perdue\");\n");
        output.Append("#pragma clang diagnostic pop\n");
        return output.ToString();
    }

    /// <summary>
    /// Les champs d'un mold dans l'ordre où le struct C++ les range : alignement décroissant, à alignement égal dans l'ordre du mold
    /// (un tri stable). Un champ moins aligné ne s'intercale jamais entre deux plus alignés : aucun padding entre les champs, seulement
    /// à la fin. L'ordre est fixe, calculé du seul mold : il ne dépend ni du programme ni du compilateur C++.
    /// </summary>
    private static IReadOnlyList<MoldFieldSymbol> _PhysicalFields(MoldSymbol mold) =>
        [.. mold.Fields.Select((field, position) => (field, position)).OrderByDescending(f => _Alignment(f.field.Type)).ThenBy(f => f.position).Select(f => f.field)];

    /// <summary>
    /// Les membres d'un mold à gear de fin, après ses champs et sa marque <c>rgl_life</c> : un constructeur qui prend tous les champs
    /// (<c>rgl_m_Handle{x, y}</c> garde la forme d'une valeur de mold), le déplacement qui transmet la marque, la fin qui appelle
    /// <c>rgl_release</c> — définie plus loin, une fois les gears déclarés (<see cref="_LifecycleReleases"/>) — et jamais de copie.
    /// </summary>
    private string _LifecycleMembers(MoldSymbol mold, string name)
    {
        var members = new StringBuilder();
        var fields = mold.Fields;
        if (fields.Count == 0) members.Append(Indent).Append(name).Append("() = default;\n");
        else
        {
            var parameters = string.Join(", ", fields.Select((f, i) => $"{_Type(f.Type)} rgl_a{i}"));
            // Paramètres dans l'ordre du mold, membres initialisés dans l'ordre où ils sont déclarés (l'ordre physique).
            var initializers = string.Join(", ", _PhysicalFields(mold).Select(f => $"rgl_f_{f.Name}(std::move(rgl_a{fields.ToList().IndexOf(f)}))"));
            members.Append(Indent).Append(name).Append('(').Append(parameters).Append(") : ").Append(initializers).Append(" {}\n");
        }

        var moved = string.Join("", _PhysicalFields(mold).Select(f => $"rgl_f_{f.Name}(std::move(other.rgl_f_{f.Name})), ")) + "rgl_life(std::move(other.rgl_life))";
        members.Append(Indent).Append(name).Append('(').Append(name).Append("&& other) noexcept : ").Append(moved).Append(" {}\n");
        members.Append(Indent).Append(name).Append("& operator=(").Append(name).Append("&& other) noexcept {\n")
            .Append(Indent).Append(Indent).Append("if (this != &other) {\n")
            .Append(Indent).Append(Indent).Append(Indent).Append("rgl_release();\n");
        foreach (var field in fields)
            members.Append(Indent).Append(Indent).Append(Indent).Append("rgl_f_").Append(field.Name).Append(" = std::move(other.rgl_f_").Append(field.Name).Append(");\n");
        members.Append(Indent).Append(Indent).Append(Indent).Append("rgl_life = std::move(other.rgl_life);\n")
            .Append(Indent).Append(Indent).Append("}\n")
            .Append(Indent).Append(Indent).Append("return *this;\n")
            .Append(Indent).Append("}\n");
        members.Append(Indent).Append('~').Append(name).Append("() { rgl_release(); }\n");
        members.Append(Indent).Append("void rgl_release();\n\n");
        return members.ToString();
    }

    /// <summary>
    /// La fin de chaque mold à gear de fin écrit : appeler le gear une fois, tant que la marque n'est pas tombée (déplacée ou déjà fermée).
    /// Écrite après la déclaration des gears, que le struct ne connaît pas encore.
    /// </summary>
    private string _LifecycleReleases(IReadOnlyList<MoldSymbol> molds)
    {
        var output = new StringBuilder();
        foreach (var mold in molds.Where(m => _usedMolds.Contains(m) && m.Destructor is not null))
        {
            output.Append("inline void ").Append(_moldNames[mold]).Append("::rgl_release() {\n")
                .Append(Indent).Append("if (rgl_life.alive) {\n")
                .Append(Indent).Append(Indent).Append("rgl_life.alive = false;\n")
                .Append(Indent).Append(Indent).Append("rgl_g_").Append(mold.Destructor!.CppName).Append("(*this);\n")
                .Append(Indent).Append("}\n}\n\n");
        }
        return output.ToString();
    }

    /// <summary>
    /// Un <c>enum class</c> par enum utilisé, sur son type sous-jacent (un entier ou <c>bool</c> ; pour un
    /// enum de flottants, de chaînes ou de symboles, sur sa position). Un enum écrit en texte reçoit une
    /// fonction <c>rgl_write</c> qui donne le nom de la variante : seuls ces noms restent dans le binaire.
    /// </summary>
    private string _Enums(IReadOnlyList<EnumSymbol> enums, bool writers = true)
    {
        var output = new StringBuilder();
        foreach (var symbol in enums.Where(_usedEnums.Contains))
        {
            var name = $"rgl_e_{symbol.CppName}";
            var byIndex = !(symbol.Underlying.IsInteger || symbol.Underlying == PrimitiveType.Bool) || !symbol.HasValues;
            var underlying = byIndex && symbol.HasValues ? _IndexType(symbol.Variants.Count) : symbol.Underlying;

            output.Append("enum class ").Append(name).Append(" : ").Append(_Type(underlying)).Append(" {\n");
            foreach (var variant in symbol.Variants)
            {
                var value = byIndex ? (object)(long)variant.Index : variant.Value!;
                var text = value switch
                {
                    bool flag => flag ? "true" : "false",
                    long number when underlying == PrimitiveType.Bool => number != 0 ? "true" : "false",
                    long number => number.ToString(CultureInfo.InvariantCulture),
                    _ => throw new InvalidOperationException($"Valeur de variante inattendue : {value}."),
                };
                output.Append(Indent).Append("rgl_c_").Append(variant.Name).Append(" = ").Append(text).Append(",\n");
            }
            output.Append("};\n\n");

            if (!writers || !_writtenEnums.Contains(symbol)) continue;
            output.Append("template <class Out>\nstatic void rgl_write(Out& out, ").Append(name).Append(" value) {\n");
            output.Append(Indent).Append("switch (value) {\n");
            foreach (var variant in symbol.Variants)
                output.Append(Indent).Append(Indent).Append("case ").Append(name).Append("::rgl_c_").Append(variant.Name).Append(": out.append(\"")
                    .Append(variant.Name).Append("\", ").Append(variant.Name.Length).Append("); break;\n");
            output.Append(Indent).Append("}\n}\n\n");
        }
        return output.ToString();
    }

    /// <summary>Le plus petit type qui numérote <paramref name="count"/> variantes.</summary>
    private static PrimitiveType _IndexType(int count) => count switch
    {
        <= 256 => new PrimitiveType(PrimitiveKind.UByte),
        <= 65_536 => new PrimitiveType(PrimitiveKind.UShort),
        _ => PrimitiveType.UInt,
    };

    /// <summary>Note qu'une valeur de ce type s'écrit en texte : un mold, et les molds qu'il contient, reçoivent <c>rgl_write</c>.</summary>
    private void _MarkWritten(IrType type)
    {
        if (type is EnumType enumeration)
        {
            _writtenEnums.Add(enumeration.Symbol);
            return;
        }
        if (type is UnionType union)
        {
            foreach (var member in union.Members) _MarkWritten(member);
            return;
        }
        if (type is ResultType result)
        {
            _MarkWritten(result.Value);
            _MarkWritten(result.Error);
            return;
        }
        if (type is NullableType nullable)
        {
            _MarkWritten(nullable.Inner);
            return;
        }
        if (type is IngotType ingot)
        {
            _MarkWritten(ingot.Base);
            return;
        }
        if (type is ArrayType or DictionaryType or TupleType)
        {
            var elements = type switch
            {
                ArrayType array => [array.Element],
                DictionaryType dictionary => [dictionary.Key, dictionary.Value],
                _ => ((TupleType)type).Elements,
            };
            foreach (var element in elements) _MarkWritten(element);
            return;
        }
        if (type is not MoldType { Symbol: var mold } || !_writtenMolds.Add(mold)) return;
        foreach (var field in mold.Fields) _MarkWritten(field.Type);
    }

    /// <summary>
    /// Une valeur de mold : <c>rgl_m_Vec2{x, y}</c>, les champs dans l'ordre du mold. Si des valeurs
    /// doivent être évaluées d'abord (spread, effet), elles sont rangées dans des constantes, dans
    /// l'ordre d'écriture, au début d'une lambda appelée sur place (une expression-instruction dans
    /// un guard, dont un échec doit pouvoir sortir).
    /// </summary>
    private string _MoldValue(IrMoldValue mold)
    {
        var type = _Type(mold.Type);
        if (mold.Parts.Count == 0) return $"{type}{{{string.Join(", ", mold.Fields.Select(_Expression))}}}";

        var block = new StringBuilder();
        for (var i = 0; i < mold.Parts.Count; ++i)
        {
            var part = mold.Parts[i];
            var name = $"rgl_t{++_temporaryCount}";
            // Une valeur à cycle de vie est rangée telle quelle et déplacée dans le mold : elle ne se copie pas.
            var owning = LifecycleRules.Has(part.Type);
            block.Append(owning ? "auto" : _Store(part, mold.Parts.Skip(i + 1))).Append(' ').Append(name).Append(" = ").Append(_Expression(part)).Append("; ");
            _partNames[part] = owning ? $"std::move({name})" : name;
        }
        var value = $"{type}{{{string.Join(", ", mold.Fields.Select(_Expression))}}}";

        if (_UseStatementExpressions)
        {
            _usesStatementExpressions = true;
            return $"({{ {block}{value}; }})";
        }
        return $"[{(_atFileLevel ? "" : "&")}]() -> {type} {{ {block}return {value}; }}()";
    }

    /// <summary><c>valeur.champ</c>.</summary>
    private string _MemberAccess(IrMemberAccess access, Func<IrExpression, string> operand) =>
        _Field(operand(access.Target), access.Target.Type, access.Field);

    /// <summary>Le champ <paramref name="field"/> de la valeur C++ <paramref name="target"/>.</summary>
    private static string _Field(string target, IrType type, MoldFieldSymbol field) => $"{target}.rgl_f_{field.Name}";
}
