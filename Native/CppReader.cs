using System.Text.Json;

namespace Rgl.Native;

/// <summary>
/// Lit, dans l'arbre JSON de Clang, les classes C++ d'un en-tête : leurs constructeurs, méthodes et champs <b>publics</b>. Ce que Clang ajoute de lui-même (constructeurs
/// implicites, opérateur d'affectation, destructeur) et ce qu'on ne décrit pas (copie, déplacement, opérateurs, patrons) est laissé de côté.
/// </summary>
internal static class CppReader
{
    /// <summary>Lit un nœud : un espace de noms (ses classes, avec son préfixe), une classe ou un patron de classe.</summary>
    /// <param name="node">Le nœud de l'arbre.</param>
    /// <param name="prefix">Le préfixe d'espace de noms : <c>demo::</c>.</param>
    /// <param name="addClass">Reçoit chaque classe lue.</param>
    /// <param name="addTemplate">Reçoit le nom de chaque patron de classe.</param>
    public static void Read(JsonElement node, string prefix, Action<CppClass> addClass, Action<string> addTemplate)
    {
        var kind = _String(node, "kind");
        var name = _String(node, "name");
        switch (kind)
        {
            case "NamespaceDecl":
                if (node.TryGetProperty("inner", out var inner))
                    foreach (var child in inner.EnumerateArray()) Read(child, name.Length == 0 ? prefix : prefix + name + "::", addClass, addTemplate);
                break;
            case "ClassTemplateDecl":
                if (name.Length > 0) addTemplate(prefix + name);
                break;
            case "CXXRecordDecl":
                if (name.Length > 0 && !IsImplicit(node) && node.TryGetProperty("completeDefinition", out var complete) && complete.ValueKind == JsonValueKind.True)
                    addClass(_Class(node, prefix + name, name));
                break;
        }
    }

    private static bool IsImplicit(JsonElement node) => node.TryGetProperty("isImplicit", out var value) && value.ValueKind == JsonValueKind.True;

    private static bool _Flag(JsonElement node, string property) => node.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static CppClass _Class(JsonElement node, string qualified, string simple)
    {
        List<string> bases = [];
        if (node.TryGetProperty("bases", out var written))
            foreach (var item in written.EnumerateArray())
                if (item.TryGetProperty("type", out var type)) bases.Add(_String(type, "qualType"));

        var isAbstract = node.TryGetProperty("definitionData", out var data) && _Flag(data, "isAbstract");

        // Un « class » commence privé, un « struct » public ; un AccessSpecDecl change l'accès des membres qui le suivent.
        var isPublic = _String(node, "tagUsed") != "class";
        List<CppMember> members = [];
        List<(string, string)> skipped = [];
        if (node.TryGetProperty("inner", out var inner))
        {
            foreach (var child in inner.EnumerateArray())
            {
                var kind = _String(child, "kind");
                if (kind == "AccessSpecDecl")
                {
                    isPublic = _String(child, "access") == "public";
                    continue;
                }
                if (!isPublic || IsImplicit(child)) continue;

                var name = _String(child, "name");
                switch (kind)
                {
                    case "CXXConstructorDecl":
                        if (_Flag(child, "explicitlyDeleted")) break;
                        var parameters = _Parameters(child);
                        // Les constructeurs de copie et de déplacement n'ont pas de sens pour un objet que RGL ne copie jamais.
                        if (parameters is [var only] && _Bare(only.Type) == simple) break;
                        members.Add(new CppMember(CppMemberKind.Constructor, name, "", parameters, false, false, false));
                        break;

                    case "CXXMethodDecl":
                        if (_Flag(child, "explicitlyDeleted")) break;
                        if (name.StartsWith("operator", StringComparison.Ordinal))
                        {
                            skipped.Add((name, "opérateur"));
                            break;
                        }
                        var signature = child.TryGetProperty("type", out var methodType) ? _String(methodType, "qualType") : "";
                        if (!_SplitSignature(signature, out var returnType, out var qualifiers)) break;
                        members.Add(new CppMember(CppMemberKind.Method, name, returnType, _Parameters(child), _String(child, "storageClass") == "static", qualifiers.Contains("const"), _Flag(child, "virtual")));
                        break;

                    case "FieldDecl":
                        var fieldType = child.TryGetProperty("type", out var typeNode) ? _Type(typeNode) : "";
                        members.Add(new CppMember(CppMemberKind.Field, name, _WithoutConst(fieldType), [], false, fieldType.StartsWith("const ", StringComparison.Ordinal), false));
                        break;

                    case "FunctionTemplateDecl":
                        skipped.Add((name, "patron de fonction"));
                        break;
                }
            }
        }
        return new CppClass(qualified, isAbstract, bases, members, skipped);
    }

    private static List<CParameter> _Parameters(JsonElement node)
    {
        List<CParameter> parameters = [];
        if (node.TryGetProperty("inner", out var inner))
            foreach (var child in inner.EnumerateArray())
                if (_String(child, "kind") == "ParmVarDecl")
                    parameters.Add(new CParameter(_String(child, "name"), child.TryGetProperty("type", out var type) ? _Type(type) : ""));
        return parameters;
    }

    /// <summary>
    /// Sépare « int (int, int) const noexcept » en son type de retour et ses qualificatifs de fin : la liste des paramètres est la dernière parenthèse fermée avant eux.
    /// </summary>
    internal static bool _SplitSignature(string signature, out string returnType, out string qualifiers)
    {
        returnType = "";
        qualifiers = "";
        var end = signature.LastIndexOf(')');
        if (end < 0) return false;

        // Ce qui suit la parenthèse fermante : const, noexcept, volatile, & et && ; noexcept(…) garde ses propres parenthèses.
        var tail = signature[(end + 1)..];
        if (tail.Contains('(')) return false;
        qualifiers = tail.Trim();

        var depth = 0;
        for (var i = end; i >= 0; --i)
        {
            if (signature[i] == ')') ++depth;
            else if (signature[i] == '(' && --depth == 0)
            {
                returnType = signature[..i].Trim();
                return true;
            }
        }
        return false;
    }

    /// <summary>Le type sans <c>const</c> devant, sans <c>&amp;</c> ni <c>&amp;&amp;</c> derrière : <c>const Counter &amp;</c> devient <c>Counter</c>.</summary>
    internal static string _Bare(string type)
    {
        var text = type.Trim();
        while (text.EndsWith('&') || text.EndsWith(' ')) text = text[..^1];
        return _WithoutConst(text).Trim();
    }

    private static string _WithoutConst(string type) => type.StartsWith("const ", StringComparison.Ordinal) ? type[6..] : type;

    private static string _Type(JsonElement type) =>
        type.TryGetProperty("desugaredQualType", out var desugared) && desugared.ValueKind == JsonValueKind.String ? desugared.GetString()! : _String(type, "qualType");

    private static string _String(JsonElement node, string property) =>
        node.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
}
