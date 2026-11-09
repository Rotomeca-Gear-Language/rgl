using System.Text;

namespace Rgl.Native;

/// <summary>
/// Le squelette d'un fichier <c>forge: descriptor</c> d'une classe C++ : un membre par constructeur, méthode et champ public que RGL sait décrire, et, en commentaire avec
/// la raison, ceux qu'il ne sait pas encore (référence ou pointeur, type inconnu, surcharge, opérateur…). C'est un point de départ à relire, pas une liaison finie.
/// </summary>
internal static class DescriptorSkeleton
{
    /// <summary>Un type C++ que le descripteur sait passer : son type RGL, et comment une valeur y entre et en sort dans l'expression C++.</summary>
    private sealed record Mapped(string Rgl, Func<string, string> ToCpp, Func<string, string> FromCpp, bool Converts);

    /// <summary>Écrit le squelette de <paramref name="cppClass"/>.</summary>
    /// <param name="cppClass">La classe lue dans l'en-tête.</param>
    /// <param name="rglName">Le nom du type côté RGL.</param>
    /// <param name="headerPath">Le chemin de l'en-tête tel qu'il s'écrira dans <c>header:</c>, relatif au fichier engendré.</param>
    /// <param name="target">La cible dans la syntaxe de Zig, qui fixe la taille de <c>long</c>.</param>
    /// <param name="aliases">Les <c>typedef</c> de l'en-tête (<c>uint32_t</c>).</param>
    public static string Generate(CppClass cppClass, string rglName, string headerPath, string target, IReadOnlyDictionary<string, string>? aliases = null)
    {
        var windows = target.Contains("windows", StringComparison.Ordinal);
        aliases ??= new Dictionary<string, string>();
        var simple = cppClass.QualifiedName.Split("::")[^1];
        var text = new StringBuilder();
        text.Append("// Squelette généré par « rgl descriptor » à partir de ").Append(headerPath.Replace("\\", "/")).Append(" : à relire et à compléter.\n");
        text.Append("// Les membres que RGL ne sait pas encore décrire sont écrits en commentaire, avec la raison.\n");
        text.Append("forge: descriptor\ndescription: \"La classe ").Append(cppClass.QualifiedName).Append("\"\n\n");
        text.Append("header: \"").Append(headerPath.Replace("\\", "/")).Append("\"\nlink: static\n\n");
        text.Append("descriptor ").Append(cppClass.QualifiedName).Append(" -> ").Append(rglName).Append(" {\n");
        foreach (var bases in cppClass.Bases) text.Append("    // hérite de ").Append(bases).Append(" : seuls les membres déclarés dans la classe sont décrits\n");
        if (cppClass.IsAbstract) text.Append("    // classe abstraite : pas de constructeur\n");

        Mapped? Map(string cppType, bool forReturn, out string reason)
        {
            reason = "";
            var raw = cppType.Trim();
            if (raw.Contains('*')) { reason = "pointeur"; return null; }
            if (raw.Contains("&&")) { reason = "référence rvalue"; return null; }
            var isReference = raw.EndsWith('&');
            var isConstReference = isReference && raw.StartsWith("const ", StringComparison.Ordinal);
            if (isReference && (forReturn || !isConstReference))
            {
                reason = forReturn ? "renvoie une référence" : "référence non constante";
                return null;
            }

            var bare = CppReader._Bare(raw);
            if (bare == simple || bare == cppClass.QualifiedName)
            {
                if (!isReference && !forReturn) { reason = "objet passé par valeur (RGL ne le copie pas)"; return null; }
                return new Mapped(rglName, x => x, x => x, false);
            }
            if (bare.Contains("basic_string<char") || bare is "std::string")
                return new Mapped("string", x => $"std::string({x}.data(), {x}.size())", x => $"rgl::standard::string(({x}).c_str())", true);
            if (CBindings.Scalar(bare, windows, aliases) is { } scalar) return new Mapped(scalar, x => x, x => x, false);

            reason = $"type « {bare} » pas encore pris en charge";
            return null;
        }

        void Skipped(string line, string reason) => text.Append("    // ").Append(line).Append(" : ").Append(reason).Append('\n');

        HashSet<string> names = [];
        foreach (var member in cppClass.Members)
        {
            var parameters = new List<(string Name, Mapped Type)>();
            var failure = "";
            var description = member.Kind == CppMemberKind.Constructor ? $"constructor({string.Join(", ", member.Parameters.Select(p => p.Type))})" : $"{member.Name}({string.Join(", ", member.Parameters.Select(p => p.Type))})";
            if (member.Kind == CppMemberKind.Constructor && cppClass.IsAbstract) continue;

            if (member.Kind != CppMemberKind.Constructor && !CBindings._IsName(member.Name)) failure = $"« {member.Name} » est un mot réservé de RGL";
            else
            {
                for (var i = 0; i < member.Parameters.Count && failure.Length == 0; ++i)
                {
                    if (Map(member.Parameters[i].Type, forReturn: false, out failure) is { } mapped)
                        parameters.Add((member.Parameters[i].Name.Length > 0 && CBindings._IsName(member.Parameters[i].Name) ? member.Parameters[i].Name : $"a{i}", mapped));
                }
            }

            Mapped? result = null;
            if (failure.Length == 0 && member.Kind != CppMemberKind.Constructor && member.ReturnType != "void")
            {
                result = Map(member.ReturnType, forReturn: member.Kind != CppMemberKind.Field, out failure);
                if (result is null && failure.Length == 0) failure = "type pas encore pris en charge";
            }
            if (failure.Length > 0)
            {
                Skipped(description, failure);
                continue;
            }

            var signature = (member.Kind == CppMemberKind.Constructor ? "create" : member.Name) + "(" + string.Join(",", parameters.Select(p => p.Type.Rgl)) + ")";
            if (!names.Add(signature))
            {
                Skipped(description, "surcharge : même signature après conversion en RGL");
                continue;
            }
            var written = string.Join(", ", parameters.Select(p => $"{p.Type.Rgl} {p.Name}"));
            var arguments = string.Join(", ", parameters.Select((p, i) => p.Type.ToCpp($"$arg{i}")));
            var simpleCall = !parameters.Any(p => p.Type.Converts);
            switch (member.Kind)
            {
                case CppMemberKind.Constructor:
                    text.Append("    constructor(").Append(written).Append(") => ").Append(cppClass.QualifiedName).Append('(').Append(arguments).Append(");\n");
                    break;

                case CppMemberKind.Field:
                    if (result is { Converts: true } && !member.IsConst)
                    {
                        text.Append("    ").Append(member.Name).Append("(): ").Append(result.Rgl).Append(" => ").Append(result.FromCpp($"$self.{member.Name}")).Append(";\n");
                        text.Append("    set_").Append(member.Name).Append('(').Append(result.Rgl).Append(" value): void => ($self.").Append(member.Name).Append(" = ").Append(result.ToCpp("$arg0")).Append(");\n");
                        break;
                    }
                    text.Append("    ").Append(member.IsConst ? "const " : "").Append("field ").Append(member.Name).Append(": ").Append(result!.Rgl).Append(" => ")
                        .Append(result.Converts ? result.FromCpp($"$self.{member.Name}") : member.Name).Append(";\n");
                    break;

                default:
                    var returns = result is null ? "void" : result.Rgl;
                    var call = member.IsStatic ? $"{cppClass.QualifiedName}::{member.Name}({arguments})" : $"$self.{member.Name}({arguments})";
                    var expression = result is { Converts: true } ? result.FromCpp(call) : simpleCall && !member.IsStatic ? member.Name : simpleCall ? member.Name : call;
                    text.Append("    ").Append(member.IsStatic ? "static " : "").Append(member.Name).Append('(').Append(written).Append("): ").Append(returns).Append(" => ").Append(expression).Append(";\n");
                    break;
            }
        }

        foreach (var (name, reason) in cppClass.SkippedMembers) Skipped(name, reason);
        text.Append("}\n");
        return text.ToString();
    }
}
