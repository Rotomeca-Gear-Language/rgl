using System.Text;
using Rgl.Diagnostics;
using Rgl.Syntax;
using Rgl.Text;

namespace Rgl.Native;

/// <summary>
/// Le texte RGL des liaisons d'un en-tête C : un fichier <c>forge: gears</c> où chaque <c>struct</c> devient un mold, chaque <c>enum</c> un enum
/// et chaque fonction convertible un gear qui enveloppe l'appel <c>native f(x)</c>. Le reste du programme les utilise comme n'importe quels autres.
/// </summary>
/// <remarks>
/// La table de conversion ne garde que ce dont une valeur RGL est l'image exacte : les entiers, les flottants, <c>bool</c>, <c>const char*</c>
/// (une <c>string</c>, copiée à la frontière), les <c>struct</c> de tels champs (un mold, copié champ par champ : le C++ généré ne suppose rien de
/// la disposition en mémoire) et les <c>enum</c>. Elle dépend de la cible : <c>long</c> a 32 bits sous Windows et 64 bits ailleurs. Tout le reste
/// est laissé de côté, avec sa raison, jusqu'à ce que les liaisons le prennent en charge.
/// </remarks>
internal static class CBindings
{
    /// <summary>Une déclaration que les liaisons n'ont pas pu convertir, avec la raison.</summary>
    /// <param name="Name">Le nom de la fonction, du <c>struct</c> ou de l'<c>enum</c>.</param>
    /// <param name="Reason">Pourquoi elle n'est pas convertie.</param>
    public sealed record Unconverted(string Name, string Reason);

    /// <summary>Le texte des liaisons et ce qu'il laisse de côté.</summary>
    /// <param name="Source">Le texte RGL du fichier <c>forge: gears</c>.</param>
    /// <param name="Converted">Le nombre de fonctions converties.</param>
    /// <param name="Unconverted">Les fonctions, <c>struct</c> et <c>enum</c> non convertis, avec leur raison.</param>
    /// <param name="OtherDeclarations">Le nombre de déclarations de l'en-tête que les liaisons ne connaissent pas encore (variables, unions…).</param>
    /// <param name="Types">Le nombre de <c>struct</c> et d'<c>enum</c> convertis.</param>
    /// <param name="Constants">Le nombre de macros converties en constantes.</param>
    public sealed record Result(string Source, int Converted, IReadOnlyList<Unconverted> Unconverted, int OtherDeclarations, int Types = 0, int Constants = 0);

    /// <summary>Ce que la conversion d'un type sait : la cible, les alias et les types déjà convertis.</summary>
    private sealed class Context(bool windows, IReadOnlyDictionary<string, string> aliases)
    {
        public bool Windows { get; } = windows;
        public IReadOnlyDictionary<string, string> Aliases { get; } = aliases;
        public HashSet<string> Structs { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Enums { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>Écrit les liaisons de <paramref name="header"/>.</summary>
    /// <param name="header">Ce que l'en-tête déclare.</param>
    /// <param name="fileName">Le nom du fichier <c>forge: native</c>, pour la description.</param>
    /// <param name="target">La cible dans la syntaxe de Zig, qui fixe la taille de <c>long</c>.</param>
    public static Result Generate(CHeader header, string fileName, string target)
    {
        var context = new Context(target.Contains("windows", StringComparison.Ordinal), header.Aliases ?? new Dictionary<string, string>());
        var text = new StringBuilder();
        text.Append("forge: gears\n");
        text.Append("description: \"Liaisons de ").Append(fileName.Replace("\\", "/").Replace("\"", "")).Append("\"\n");

        List<Unconverted> unconverted = [];
        var types = 0;
        var others = header.Skipped.Count;

        // Les constantes d'abord : un fichier de gears écrit les siennes avant ses gears. Une macro dont le nom est déjà celui d'une fonction ou d'un type
        // de l'en-tête est laissée de côté (un fichier n'a qu'un nom pour chaque chose).
        HashSet<string> used = [.. header.Functions.Select(f => f.Name), .. (header.Records ?? []).Select(r => r.Name), .. (header.Enums ?? []).Select(e => e.Name)];
        var constants = 0;
        foreach (var macro in header.Macros ?? [])
        {
            if (!_IsName(macro.Name) || !used.Add(macro.Name)) continue;
            text.Append("\nconst ").Append(macro.Type).Append(' ').Append(macro.Name).Append(": ").Append(macro.Value).Append(";\n");
            ++constants;
        }

        foreach (var enumeration in header.Enums ?? [])
        {
            if (_Reason(enumeration) is { } reason)
            {
                if (enumeration.Name.Length > 0) unconverted.Add(new Unconverted(enumeration.Name, reason));
                continue;
            }

            // Les valeurs sont toutes écrites : celles du C, que RGL garde telles quelles.
            var underlying = enumeration.Constants.Any(c => c.Value is < int.MinValue or > int.MaxValue) ? "long" : "int";
            text.Append("\nenum ").Append(enumeration.Name).Append(": ").Append(underlying).Append(" {\n");
            text.Append(string.Join(",\n", enumeration.Constants.Select(c => $"    {c.Name}: {c.Value}"))).Append("\n}\n");
            context.Enums.Add(enumeration.Name);
            ++types;
        }

        // Un struct n'en contient que d'autres déjà définis : dans l'ordre de l'en-tête, chacun voit ceux qui le précèdent.
        foreach (var record in header.Records ?? [])
        {
            if (record.Name.Length == 0) continue;
            if (_Reason(record, context, out var fields) is { } reason)
            {
                unconverted.Add(new Unconverted(record.Name, reason));
                continue;
            }

            text.Append("\nmold ").Append(record.Name).Append(" {\n");
            foreach (var (type, name) in fields) text.Append("    ").Append(type).Append(' ').Append(name).Append(";\n");
            text.Append("}\n");
            context.Structs.Add(record.Name);
            ++types;
        }

        HashSet<string> names = [];
        var converted = 0;
        foreach (var function in header.Functions)
        {
            if (!names.Add(function.Name)) continue;
            if (_Reason(function, context, out var returnType, out var parameters) is { } reason)
            {
                unconverted.Add(new Unconverted(function.Name, reason));
                continue;
            }

            ++converted;
            var written = string.Join(", ", parameters.Select(p => $"{p.Type} {p.Name}"));
            var arguments = string.Join(", ", parameters.Select(p => p.Name));
            text.Append('\n').Append("gear ").Append(function.Name).Append('(').Append(written).Append("): ").Append(returnType).Append(" {\n");
            text.Append("    ").Append(returnType == "void" ? "" : "return ").Append("native ").Append(function.Name).Append('(').Append(arguments).Append(");\n");
            text.Append("}\n");
        }

        return new Result(text.ToString(), converted, unconverted, others, types, constants);
    }

    /// <summary>La raison pour laquelle l'enum n'est pas convertie, ou <see langword="null"/>.</summary>
    private static string? _Reason(CEnum enumeration)
    {
        if (enumeration.Name.Length == 0) return "enum sans nom";
        if (!_IsName(enumeration.Name)) return $"« {enumeration.Name} » est un mot réservé de RGL";
        if (enumeration.Constants.Count == 0) return "enum sans constante";
        foreach (var (name, _) in enumeration.Constants)
            if (!_IsName(name)) return $"constante « {name} » : mot réservé de RGL";
        if (enumeration.Constants.Select(c => c.Name).Distinct().Count() != enumeration.Constants.Count) return "constantes en double";
        if (enumeration.Constants.Select(c => c.Value).Distinct().Count() != enumeration.Constants.Count) return "deux constantes de même valeur";
        return null;
    }

    /// <summary>La raison pour laquelle le <c>struct</c> n'est pas converti, ou <see langword="null"/> avec les types RGL de ses champs.</summary>
    private static string? _Reason(CRecord record, Context context, out List<(string Type, string Name)> fields)
    {
        fields = [];
        if (!_IsName(record.Name)) return $"« {record.Name} » est un mot réservé de RGL";
        if (record.IsUnion) return "union";
        if (record.Fields.Count == 0) return "struct sans champ";

        foreach (var field in record.Fields)
        {
            if (field.IsBitfield) return $"champ de bits « {field.Name} »";
            if (!_IsName(field.Name)) return $"champ « {field.Name} » : mot réservé de RGL";

            // Une chaîne n'est pas permise dans un struct : le C++ généré ne sait pas à qui appartient le texte.
            var type = _Convert(field.Type, context);
            if (type is null || type == "string") return $"champ « {field.Name} » de type « {field.Type} » pas encore pris en charge";
            fields.Add((type, field.Name));
        }
        return null;
    }

    /// <summary>La raison pour laquelle la fonction n'est pas convertie, ou <see langword="null"/> avec ses types RGL.</summary>
    private static string? _Reason(CFunction function, Context context, out string returnType, out List<(string Type, string Name)> parameters)
    {
        parameters = [];
        returnType = "";
        if (!_IsName(function.Name)) return $"« {function.Name} » est un mot réservé de RGL";
        if (function.IsVariadic) return "fonction à nombre variable d'arguments";

        if (function.ReturnType == "void") returnType = "void";
        else if (_Convert(function.ReturnType, context) is { } converted) returnType = converted;
        else return $"type de retour « {function.ReturnType} » pas encore pris en charge";

        var declared = function.Parameters;
        for (var i = 0; i < declared.Count; ++i)
        {
            var type = _Convert(declared[i].Type, context);
            if (type is null) return $"paramètre de type « {declared[i].Type} » pas encore pris en charge";

            var name = declared[i].Name.Length > 0 && _IsName(declared[i].Name) && declared[i].Name != function.Name ? declared[i].Name : $"a{i}";
            parameters.Add((type, name));
        }
        return null;
    }

    /// <summary>Le type RGL d'un type C ; <see langword="null"/> pour un type que les liaisons ne connaissent pas.</summary>
    private static string? _Convert(string cType, Context context)
    {
        // Un texte C : const char *. Le texte lui-même est copié à la frontière, dans les deux sens.
        if (_Words(cType, keepConst: true).Replace(" ", "") == "constchar*") return "string";

        var words = _Words(cType, keepConst: false);

        // Un alias (size_t, int32_t, un typedef de struct) vaut le type qu'il désigne, lui-même peut en être un autre.
        for (var depth = 0; depth < 16 && context.Aliases.TryGetValue(words, out var target); ++depth)
        {
            if (_Words(target, keepConst: true).Replace(" ", "") == "constchar*") return "string";
            words = _Words(target, keepConst: false);
        }

        if (words.StartsWith("struct ", StringComparison.Ordinal) && context.Structs.Contains(words[7..])) return words[7..];
        if (words.StartsWith("enum ", StringComparison.Ordinal) && context.Enums.Contains(words[5..])) return words[5..];

        return ScalarWords(words, context.Windows);
    }

    /// <summary>Le type RGL d'un type C numérique ou <c>bool</c>, alias résolus (<c>uint32_t</c>) ; <see langword="null"/> pour tout autre type. La taille de <c>long</c> dépend de la cible.</summary>
    internal static string? Scalar(string cType, bool windows, IReadOnlyDictionary<string, string> aliases)
    {
        var words = _Words(cType, keepConst: false);
        for (var depth = 0; depth < 16 && aliases.TryGetValue(words, out var target); ++depth) words = _Words(target, keepConst: false);
        return ScalarWords(words, windows);
    }

    private static string? ScalarWords(string words, bool windows)
    {
        return words switch
        {
            "_Bool" or "bool" => "bool",
            "signed char" => "byte",
            "unsigned char" => "ubyte",
            "short" or "short int" or "signed short" or "signed short int" => "short",
            "unsigned short" or "unsigned short int" => "ushort",
            "int" or "signed" or "signed int" => "int",
            "unsigned" or "unsigned int" => "uint",
            "long" or "long int" or "signed long" or "signed long int" => windows ? "int" : "long",
            "unsigned long" or "unsigned long int" => windows ? "uint" : "ulong",
            "long long" or "long long int" or "signed long long" or "signed long long int" => "long",
            "unsigned long long" or "unsigned long long int" => "ulong",
            "float" => "float",
            "double" => "double",
            _ => null,
        };
    }

    private static string _Words(string cType, bool keepConst) =>
        string.Join(' ', cType.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w != "volatile" && (keepConst || w != "const")));

    /// <summary>Vrai si <paramref name="name"/> est un identificateur que RGL accepte comme nom (ni mot-clé, ni mot réservé).</summary>
    internal static bool _IsName(string name)
    {
        if (name.Length == 0 || !SyntaxFacts.IsIdentifierStart(name[0]) || !name.All(SyntaxFacts.IsIdentifierPart)) return false;

        var tokens = new Lexer(new SourceText("nom", name), new DiagnosticBag()).Tokenize();
        return tokens.Tokens.Count > 0 && tokens.Tokens[0].Kind == TokenKind.Identifier;
    }
}
