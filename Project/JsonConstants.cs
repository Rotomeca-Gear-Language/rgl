using System.Globalization;
using System.Text;
using System.Text.Json;
using Rgl.Diagnostics;
using Rgl.Syntax;
using Rgl.Text;

namespace Rgl.Project;

/// <summary>
/// <c>import json settings;</c> : le contenu de <c>settings.json</c> devient une constante, par un texte RGL engendré
/// (<c>var settings: {nom: "x", niveaux: [1, 2]};</c>) que le chargeur analyse comme n'importe quelle déclaration — le binder déduit
/// le type, comme pour une valeur écrite à la main. Un objet dont toutes les clés sont des noms RGL devient un mold anonyme, sinon un
/// dictionnaire <c>[clé: valeur]</c> ; un tableau, un tableau ; un entier JSON (sans point ni exposant, qui tient dans un <c>long</c>), un
/// entier, tout autre nombre un <c>double</c> ; <c>null</c> et un tableau vide n'ont pas de type à déduire : ils sont refusés (un objet vide est un mold sans champ).
/// </summary>
internal static class JsonConstants
{
    /// <summary>La profondeur maximale d'un document importé.</summary>
    private const int MaxDepth = 256;

    /// <summary>
    /// La déclaration <c>var nom: valeur;</c> qui écrit <paramref name="json"/>, sur plusieurs lignes ; <see langword="null"/> après avoir
    /// signalé l'erreur à <paramref name="error"/>.
    /// </summary>
    /// <param name="json">Le contenu du fichier.</param>
    /// <param name="name">Le nom de la constante.</param>
    /// <param name="error">Reçoit le message d'une erreur du fichier, avec le chemin de la valeur fautive.</param>
    public static string? ToSource(string json, string name, Action<string> error)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaxDepth });
        }
        catch (JsonException exception)
        {
            error($"JSON invalide, ligne {(exception.LineNumber ?? 0) + 1}, colonne {(exception.BytePositionInLine ?? 0) + 1} : {exception.Message}");
            return null;
        }

        using (document)
        {
            var text = new StringBuilder();
            text.Append("var ").Append(name).Append(": ");
            var ok = Write(text, document.RootElement, "$", 0, error);
            text.Append(";\n");
            return ok ? text.ToString() : null;
        }
    }

    /// <summary>
    /// Les entrées d'un dataset lues d'un JSON : <c>dataset Nom of|as Mold { nom: {…}, … }</c>. Le JSON est un objet dont les clés sont les noms des
    /// entrées, ou un tableau d'objets dont le champ <c>id</c> (une chaîne) est le nom de l'entrée ; chaque entrée est un objet dont les clés sont des
    /// noms de champ. <see langword="null"/> après avoir signalé l'erreur à <paramref name="error"/>.
    /// </summary>
    /// <param name="json">Le contenu du fichier.</param>
    /// <param name="dataset">Le nom du dataset.</param>
    /// <param name="keyword"><c>of</c> (mold déclaré) ou <c>as</c> (mold créé).</param>
    /// <param name="mold">Le nom du mold des entrées.</param>
    /// <param name="error">Reçoit le message d'une erreur du fichier, avec le chemin de la valeur fautive.</param>
    public static string? ToDataset(string json, string dataset, string keyword, string mold, Action<string> error)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaxDepth });
        }
        catch (JsonException exception)
        {
            error($"JSON invalide, ligne {(exception.LineNumber ?? 0) + 1}, colonne {(exception.BytePositionInLine ?? 0) + 1} : {exception.Message}");
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            var entries = new List<(string Name, JsonElement Record, string Path)>();
            switch (root.ValueKind)
            {
                case JsonValueKind.Object:
                    // Les clés en double : la dernière l'emporte, comme ailleurs.
                    foreach (var member in root.EnumerateObject().Reverse().DistinctBy(m => m.Name, StringComparer.Ordinal).Reverse())
                        entries.Add((member.Name, member.Value, $"$.{member.Name}"));
                    break;

                case JsonValueKind.Array:
                    var position = 0;
                    foreach (var item in root.EnumerateArray())
                    {
                        var path = $"$[{position++}]";
                        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String)
                        {
                            error($"{path} : chaque entrée d'un tableau est un objet dont le champ « id » est une chaîne : le nom de l'entrée");
                            return null;
                        }
                        entries.Add((id.GetString()!, item, path));
                    }
                    break;

                default:
                    error("$ : les entrées d'un dataset sont un objet {\"nom\": {…}} ou un tableau d'objets avec un champ « id »");
                    return null;
            }

            var text = new StringBuilder();
            text.Append("dataset ").Append(dataset).Append(' ').Append(keyword).Append(' ').Append(mold).Append(" {\n");
            foreach (var (name, record, path) in entries)
            {
                if (!IsName(name))
                {
                    error($"{path} : « {name} » n'est pas un nom d'entrée valide : un nom d'entrée est un nom RGL (lettres, chiffres et _, sans mot-clé)");
                    return null;
                }
                if (record.ValueKind != JsonValueKind.Object)
                {
                    error($"{path} : une entrée est un objet {{\"champ\": valeur}}");
                    return null;
                }
                var fields = record.EnumerateObject().Reverse().DistinctBy(m => m.Name, StringComparer.Ordinal).Reverse()
                    .Where(m => !(root.ValueKind == JsonValueKind.Array && m.Name == "id")).ToArray();
                if (fields.Any(f => !IsName(f.Name)))
                {
                    error($"{path} : les champs d'une entrée sont des noms RGL (« {fields.First(f => !IsName(f.Name)).Name} » n'en est pas un)");
                    return null;
                }

                text.Append(Indent(1)).Append(name).Append(": {");
                for (var i = 0; i < fields.Length; ++i)
                {
                    text.Append('\n').Append(Indent(2)).Append(fields[i].Name).Append(": ");
                    if (!Write(text, fields[i].Value, $"{path}.{fields[i].Name}", 2, error)) return null;
                    if (i + 1 < fields.Length) text.Append(',');
                }
                text.Append(fields.Length == 0 ? "}" : "\n" + Indent(1) + "}").Append(",\n");
            }
            text.Append("}\n");
            return text.ToString();
        }
    }

    private static string Indent(int depth) => new(' ', depth * 4);

    private static bool Write(StringBuilder text, JsonElement element, string path, int depth, Action<string> error)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.True:
                text.Append("true");
                return true;

            case JsonValueKind.False:
                text.Append("false");
                return true;

            case JsonValueKind.Null:
                error($"{path} : null n'a pas de type : le type d'une constante importée se déduit de ses valeurs");
                return false;

            case JsonValueKind.String:
                WriteString(text, element.GetString()!);
                return true;

            case JsonValueKind.Number:
                return WriteNumber(text, element, path, error);

            case JsonValueKind.Array:
                var items = element.EnumerateArray().ToArray();
                if (items.Length == 0)
                {
                    error($"{path} : un tableau vide n'a pas de type : le type d'une constante importée se déduit de ses valeurs");
                    return false;
                }
                text.Append("[\n");
                for (var i = 0; i < items.Length; ++i)
                {
                    text.Append(Indent(depth + 1));
                    if (!Write(text, items[i], $"{path}[{i}]", depth + 1, error)) return false;
                    text.Append(i + 1 < items.Length ? ",\n" : "\n");
                }
                text.Append(Indent(depth)).Append(']');
                return true;

            case JsonValueKind.Object:
                var members = element.EnumerateObject().ToArray();
                // Les clés en double : la dernière l'emporte, comme au chargement d'un JSON ailleurs.
                members = [.. members.Reverse().DistinctBy(m => m.Name, StringComparer.Ordinal).Reverse()];
                var asMold = members.All(m => IsName(m.Name));
                if (members.Length == 0)
                {
                    text.Append("{}");
                    return true;
                }
                text.Append(asMold ? "{\n" : "[\n");
                for (var i = 0; i < members.Length; ++i)
                {
                    text.Append(Indent(depth + 1));
                    if (asMold) text.Append(members[i].Name);
                    else WriteString(text, members[i].Name);
                    text.Append(": ");
                    if (!Write(text, members[i].Value, $"{path}.{members[i].Name}", depth + 1, error)) return false;
                    text.Append(i + 1 < members.Length ? ",\n" : "\n");
                }
                text.Append(Indent(depth)).Append(asMold ? '}' : ']');
                return true;

            default:
                error($"{path} : valeur JSON inconnue");
                return false;
        }
    }

    /// <summary>Un entier sans point ni exposant qui tient dans un <c>long</c> s'écrit tel quel ; tout autre nombre est un flottant.</summary>
    private static bool WriteNumber(StringBuilder text, JsonElement element, string path, Action<string> error)
    {
        var raw = element.GetRawText();
        if (!raw.AsSpan().ContainsAny(".eE") && element.TryGetInt64(out var integer))
        {
            text.Append(integer.ToString(CultureInfo.InvariantCulture));
            return true;
        }

        if (!element.TryGetDouble(out var number) || double.IsInfinity(number))
        {
            error($"{path} : le nombre {raw} ne tient pas dans un double");
            return false;
        }

        // Le texte le plus court qui se relit pareil, toujours avec un point : 2 s'écrit 2.0, 1E+20 s'écrit 1.0e20.
        var shortest = number.ToString("R", CultureInfo.InvariantCulture);
        var e = shortest.IndexOf('E');
        var mantissa = e < 0 ? shortest : shortest[..e];
        if (!mantissa.Contains('.')) mantissa += ".0";
        text.Append(mantissa);
        if (e >= 0) text.Append('e').Append(int.Parse(shortest[(e + 1)..], CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
        return true;
    }

    /// <summary>Une chaîne classique <c>"…"</c> : guillemet, antislash et caractères de contrôle échappés, le reste tel quel (UTF-8).</summary>
    internal static void WriteString(StringBuilder text, string value)
    {
        text.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"': text.Append("\\\""); break;
                case '\\': text.Append("\\\\"); break;
                case '\n': text.Append("\\n"); break;
                case '\r': text.Append("\\r"); break;
                case '\t': text.Append("\\t"); break;
                case < ' ': text.Append("\\x").Append(((int)character).ToString("X2", CultureInfo.InvariantCulture)); break;
                default: text.Append(character); break;
            }
        }
        text.Append('"');
    }

    /// <summary>Vrai si <paramref name="key"/> est un nom RGL (ni mot-clé, ni mot réservé) : une clé qui n'en est pas fait un dictionnaire.</summary>
    private static bool IsName(string key)
    {
        if (key.Length == 0 || !SyntaxFacts.IsIdentifierStart(key[0]) || !key.All(SyntaxFacts.IsIdentifierPart)) return false;
        var tokens = new Lexer(new SourceText("clé", key), new DiagnosticBag()).Tokenize().Tokens;
        return tokens.Count == 2 && tokens[0].Kind == TokenKind.Identifier;
    }
}
