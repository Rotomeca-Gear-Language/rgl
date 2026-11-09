using System.Text;
using System.Text.RegularExpressions;
using Rgl.AST.Descriptors;
using Rgl.IR.Symbols;

namespace Rgl.Native;

/// <summary>
/// Le texte RGL des liaisons d'un fichier <c>forge: descriptor</c> : chaque descriptor devient un mold à cycle de vie (<c>ulong handle</c>, l'adresse de l'objet C++, que son
/// gear de fin détruit) et une extension qui porte ses constructeurs, méthodes, statiques et champs, des guards dont le C++ est l'expression du descriptor.
/// </summary>
/// <remarks>
/// La valeur RGL est un type opaque et déplaçable : le C++ n'est jamais copié, ses champs se lisent par des accesseurs, et rien de sa disposition n'est exposé.
/// </remarks>
internal static partial class DescriptorBindings
{
    /// <summary>Le texte des liaisons et, pour chaque guard sans corps qu'il déclare, le C++ qui le réalise (par position du nom dans le texte).</summary>
    public sealed record Result(string Source, IReadOnlyDictionary<int, NativeBody> Bodies);

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex BareName();

    /// <summary>Écrit les liaisons de <paramref name="descriptors"/>.</summary>
    /// <param name="descriptors">Les descriptors du fichier.</param>
    /// <param name="fileName">Le nom du fichier forge, pour la description.</param>
    /// <param name="headers">Les chemins complets des en-têtes que le C++ de chaque membre inclut.</param>
    public static Result Generate(IReadOnlyList<DescriptorDeclaration> descriptors, string fileName, IReadOnlyList<string> headers)
    {
        var classes = descriptors.ToDictionary(d => d.Name.Name, d => Compact(d.CppType.Text));
        var text = new StringBuilder();
        text.Append("forge: gears\ndescription: \"Liaisons de ").Append(fileName.Replace("\\", "/").Replace("\"", "")).Append("\"\n");
        Dictionary<int, NativeBody> bodies = [];

        // Le nom d'un guard ou d'un gear sans corps, écrit dans le texte : sa position dit au binder quel C++ le réalise.
        void Name(string name, NativeBody body)
        {
            bodies[text.Length] = body;
            text.Append(name);
        }

        foreach (var descriptor in descriptors)
        {
            var rgl = descriptor.Name.Name;
            var generic = descriptor.TypeParameters is { Count: > 0 } ps ? "<" + string.Join(", ", ps.Select(p => p.Name.Name)) + ">" : "";
            var declarationParameters = descriptor.TypeParameters is { Count: > 0 } declarations ? "<" + string.Join(", ", declarations.Select(p => p.Span.Text)) + ">" : "";
            var instance = rgl + generic;
            var cpp = classes[rgl];
            var closer = $"{rgl}__fermer";
            text.Append("\nmold ").Append(rgl).Append(declarationParameters);
            if (generic.Length == 0) text.Append(" -> ").Append(closer);
            text.Append(" { ulong handle; }\n\n");
            if (generic.Length == 0)
            {
            text.Append("gear ");
            Name(closer, new NativeBody(NativeBodyKind.Destroy, "", 0, classes, headers));
            text.Append('(').Append(rgl).Append(" c): void;\n\n");
            }

            text.Append("extend").Append(declarationParameters).Append(' ').Append(instance).Append(" {\n");
            var overloads = new Dictionary<string, int>();
            foreach (var member in descriptor.Members)
            {
                var hasSelf = member.Kind is DescriptorMemberKind.Method or DescriptorMemberKind.Field;
                var parameters = member.Parameters.Select(p => $"{p.Type.Span.Text} {p.Name.Name}").ToList();
                if (hasSelf) parameters.Insert(0, $"this {instance} _self");

                var arguments = string.Join(", ", Enumerable.Range(0, member.Parameters.Count).Select(i => $"$arg{i}"));
                var written = member.Expression.Text.Trim();
                string expression = BareName().IsMatch(written)
                    ? member.Kind switch
                    {
                        DescriptorMemberKind.Constructor => $"{written}({arguments})",
                        DescriptorMemberKind.Method => $"$self.{written}({arguments})",
                        DescriptorMemberKind.Static => $"{cpp}::{written}({arguments})",
                        _ => $"$self.{written}",
                    }
                    : written;

                var publicName = member.Kind == DescriptorMemberKind.Constructor ? "create" : member.Name.Name;
                var number = overloads.GetValueOrDefault(publicName);
                overloads[publicName] = number + 1;
                var internalName = number == 0 ? publicName : publicName + "__overload" + number;
                text.Append(Indent).Append("guard ");
                Name(internalName, new NativeBody(NativeBodyKind.Expression, expression, hasSelf ? 0 : -1, classes, headers, publicName));
                text.Append('(').Append(string.Join(", ", parameters)).Append("): ").Append(member.Kind == DescriptorMemberKind.Constructor ? instance : member.Type!.Span.Text).Append(";\n");
                if (member.Kind == DescriptorMemberKind.Field && !member.IsConst)
                {
                    var access = BareName().IsMatch(written) ? "$self." + written : written;
                    text.Append(Indent).Append("guard ");
                    Name("set_" + member.Name.Name, new NativeBody(NativeBodyKind.Expression, "(" + access + " = $arg0)", 0, classes, headers, "set_" + member.Name.Name));
                    text.Append("(this ").Append(instance).Append(" _self, ").Append(member.Type!.Span.Text).Append(" value): void;\n");
                }
            }
            text.Append("}\n");
        }

        return new Result(text.ToString(), bodies);
    }

    private const string Indent = "    ";

    /// <summary>Le type C++ écrit sans les blancs qui séparent ses morceaux : <c>ns :: Counter</c> devient <c>ns::Counter</c>.</summary>
    private static string Compact(string cppType) => string.Concat(cppType.Where(c => !char.IsWhiteSpace(c)));
}
