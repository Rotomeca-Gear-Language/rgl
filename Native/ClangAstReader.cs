using System.Buffers;
using System.Text.Json;

namespace Rgl.Native;

/// <summary>Une fonction C lue dans un en-tête : son nom, son type de retour, ses paramètres, tels que Clang les écrit.</summary>
/// <param name="Name">Le nom de la fonction.</param>
/// <param name="ReturnType">Le type de retour, sans les alias (<c>size_t</c> devient <c>unsigned long long</c>).</param>
/// <param name="Parameters">Les paramètres, dans l'ordre ; le nom est vide s'il n'est pas écrit.</param>
/// <param name="IsVariadic">Vrai pour une fonction à nombre variable d'arguments (<c>...</c>).</param>
internal sealed record CFunction(string Name, string ReturnType, IReadOnlyList<CParameter> Parameters, bool IsVariadic);

/// <summary>Un paramètre d'une fonction C.</summary>
/// <param name="Name">Son nom ; vide si l'en-tête ne l'écrit pas.</param>
/// <param name="Type">Son type, sans les alias.</param>
internal sealed record CParameter(string Name, string Type);

/// <summary>Une déclaration de l'en-tête que les liaisons ne savent pas encore convertir.</summary>
/// <param name="Kind">La sorte de déclaration (<c>RecordDecl</c>, <c>EnumDecl</c>, <c>VarDecl</c>…).</param>
/// <param name="Name">Son nom ; vide si elle n'en a pas.</param>
internal sealed record CSkipped(string Kind, string Name);

/// <summary>Un champ d'un <c>struct</c> C.</summary>
/// <param name="Name">Son nom.</param>
/// <param name="Type">Son type, sans les alias.</param>
/// <param name="IsBitfield">Vrai pour un champ de bits (<c>int f : 3</c>), qui n'a pas d'équivalent.</param>
internal sealed record CField(string Name, string Type, bool IsBitfield);

/// <summary>Un <c>struct</c> C défini dans l'en-tête.</summary>
/// <param name="Name">Son nom, ou celui du <c>typedef</c> qui le nomme quand il est anonyme ; vide s'il n'en a aucun.</param>
/// <param name="IsUnion">Vrai pour une <c>union</c>, qui n'a pas d'équivalent.</param>
/// <param name="Fields">Ses champs, dans l'ordre.</param>
internal sealed record CRecord(string Name, bool IsUnion, IReadOnlyList<CField> Fields);

/// <summary>Une <c>enum</c> C définie dans l'en-tête : ses constantes et leur valeur.</summary>
/// <param name="Name">Son nom, ou celui du <c>typedef</c> qui la nomme quand elle est anonyme ; vide si elle n'en a aucun.</param>
/// <param name="Constants">Ses constantes, dans l'ordre, avec leur valeur (celle de la précédente plus un si elle n'est pas écrite).</param>
internal sealed record CEnum(string Name, IReadOnlyList<(string Name, long Value)> Constants);

/// <summary>Ce qu'un membre d'une classe C++ est.</summary>
internal enum CppMemberKind
{
    /// <summary>Un constructeur (hors copie et déplacement).</summary>
    Constructor,

    /// <summary>Une méthode, d'instance ou statique.</summary>
    Method,

    /// <summary>Un champ public.</summary>
    Field,
}

/// <summary>Un membre public d'une classe C++, tel que Clang le déclare.</summary>
/// <param name="Kind">Constructeur, méthode ou champ.</param>
/// <param name="Name">Son nom (celui de la classe pour un constructeur).</param>
/// <param name="ReturnType">Le type de retour d'une méthode, ou le type d'un champ ; vide pour un constructeur.</param>
/// <param name="Parameters">Ses paramètres, dans l'ordre.</param>
/// <param name="IsStatic">Vrai pour une méthode statique.</param>
/// <param name="IsConst">Vrai pour une méthode <c>const</c> ou un champ <c>const</c>.</param>
/// <param name="IsVirtual">Vrai pour une méthode virtuelle.</param>
internal sealed record CppMember(CppMemberKind Kind, string Name, string ReturnType, IReadOnlyList<CParameter> Parameters, bool IsStatic, bool IsConst, bool IsVirtual);

/// <summary>Une classe C++ définie dans l'en-tête, avec ses membres publics.</summary>
/// <param name="QualifiedName">Son nom avec ses espaces de noms : <c>demo::Counter</c>.</param>
/// <param name="IsAbstract">Vrai si elle a une méthode virtuelle pure : on ne la construit pas.</param>
/// <param name="Bases">Les classes dont elle hérite, telles qu'écrites ; leurs membres ne sont pas repris.</param>
/// <param name="Members">Ses constructeurs, méthodes et champs publics, dans l'ordre.</param>
/// <param name="SkippedMembers">Ce que le squelette ne peut pas décrire (membres de patron, opérateurs…), avec la raison.</param>
internal sealed record CppClass(string QualifiedName, bool IsAbstract, IReadOnlyList<string> Bases, IReadOnlyList<CppMember> Members, IReadOnlyList<(string Name, string Reason)> SkippedMembers);

/// <summary>Ce qu'un en-tête déclare lui-même.</summary>
/// <param name="Functions">Les fonctions déclarées dans l'en-tête, dans l'ordre.</param>
/// <param name="Skipped">Les autres déclarations de l'en-tête (types, variables, énumérations), que cette version ne convertit pas.</param>
/// <param name="Aliases">Les <c>typedef</c> de tous les en-têtes lus, système compris (<c>size_t</c>, <c>int32_t</c>) : le type qu'ils désignent, tel qu'écrit.</param>
/// <param name="Records">Les <c>struct</c> et <c>union</c> définis dans l'en-tête.</param>
/// <param name="Enums">Les <c>enum</c> définies dans l'en-tête.</param>
/// <param name="Macros">Les macros de l'en-tête qui valent une constante (lues à part : l'arbre ne les voit pas).</param>
/// <param name="Classes">Les classes C++ définies dans l'en-tête (lu en C++), espaces de noms compris.</param>
/// <param name="ClassTemplates">Les noms qualifiés des patrons de classe de l'en-tête, que les descriptors ne décrivent pas encore.</param>
internal sealed record CHeader(IReadOnlyList<CFunction> Functions, IReadOnlyList<CSkipped> Skipped, IReadOnlyDictionary<string, string>? Aliases = null,
    IReadOnlyList<CRecord>? Records = null, IReadOnlyList<CEnum>? Enums = null, IReadOnlyList<CMacro>? Macros = null, IReadOnlyList<CppClass>? Classes = null,
    IReadOnlyList<string>? ClassTemplates = null);

/// <summary>
/// Lit l'arbre syntaxique que Clang écrit en JSON (<c>-ast-dump=json</c>) et n'en garde que ce que l'en-tête demandé déclare lui-même.
/// </summary>
/// <remarks>
/// Un en-tête qui en inclut d'autres donne des dizaines de Mo de JSON, presque tous pour les en-têtes du système : la lecture se fait au fil de
/// l'eau (<see cref="Utf8JsonReader"/>), et seules les déclarations de l'en-tête demandé sont gardées. Clang n'écrit le fichier d'un nœud que
/// lorsqu'il change : il faut donc suivre le dernier fichier vu pendant tout le parcours, y compris dans ce qu'on ignore, et ne pas confondre
/// le fichier courant avec celui d'un <c>includedFrom</c>, qui désigne le fichier qui inclut.
/// </remarks>
internal static class ClangAstReader
{
    /// <summary>Lit le flux jusqu'à sa fin.</summary>
    /// <param name="stream">Le JSON de Clang.</param>
    /// <param name="header">Le chemin complet de l'en-tête dont on veut les déclarations.</param>
    /// <returns>Les déclarations de l'en-tête ; <see langword="null"/> si le flux n'est pas un arbre de Clang.</returns>
    public static CHeader? Read(Stream stream, string header)
    {
        var target = _Normalize(header);
        List<CFunction> functions = [];
        List<CSkipped> skipped = [];
        Dictionary<string, string> aliases = new(StringComparer.Ordinal);
        var declarations = new Declarations();

        var buffer = new byte[1 << 20];
        var length = 0;
        var atEnd = false;
        // Les en-têtes C++ imbriquent bien plus que 64 niveaux (patrons de la bibliothèque standard) : la limite par défaut du lecteur les refuserait.
        var state = new JsonReaderState(new JsonReaderOptions { MaxDepth = 4096 });

        // Le nom de la propriété ouverte à chaque profondeur, pour savoir où l'on est.
        var property = new string?[4096];
        string? currentFile = null;

        // Le nœud de premier niveau en cours de copie : la racine est à la profondeur 0, sa propriété « inner » à 1, ses éléments à 2.
        var copy = new ArrayBufferWriter<byte>();
        var writer = new Utf8JsonWriter(copy);
        var copying = false;
        var decided = false;
        var sawRoot = false;
        string? childKind = null, childName = null, childType = null;

        while (true)
        {
            var read = atEnd ? 0 : stream.Read(buffer, length, buffer.Length - length);
            if (read == 0) atEnd = true;
            else length += read;

            var reader = new Utf8JsonReader(buffer.AsSpan(0, length), atEnd, state);
            try
            {
                while (reader.Read())
                {
                    var depth = reader.CurrentDepth;
                    var token = reader.TokenType;
                    if (depth >= property.Length) return null;

                    if (token == JsonTokenType.PropertyName) property[depth] = reader.GetString();
                    else if (token == JsonTokenType.StartObject && depth == 0) sawRoot = true;

                    // Le fichier courant : la valeur d'un « file », sauf sous « includedFrom » (le fichier qui inclut, pas celui qu'on lit).
                    if (token == JsonTokenType.String && property[depth] == "file" && !_Under(property, depth, "includedFrom"))
                        currentFile = reader.GetString();

                    if (token == JsonTokenType.StartObject && depth == 2 && property[1] == "inner")
                    {
                        copy.ResetWrittenCount();
                        writer.Reset(copy);
                        copying = true;
                        decided = false;
                        childKind = childName = childType = null;
                    }

                    // Un typedef est retenu où qu'il soit : size_t est écrit dans un en-tête du système, que l'en-tête demandé inclut.
                    if (token == JsonTokenType.String && depth == 3 && property[3] == "kind") childKind = reader.GetString();
                    else if (token == JsonTokenType.String && depth == 3 && property[3] == "name") childName = reader.GetString();
                    else if (token == JsonTokenType.String && depth == 4 && property[3] == "type" && property[4] == "qualType") childType = reader.GetString();
                    if (token == JsonTokenType.EndObject && depth == 2 && childKind == "TypedefDecl" && childName is not null && childType is not null) aliases[childName] = childType;

                    if (!copying) continue;

                    _Write(writer, ref reader);

                    // La déclaration est de l'en-tête demandé ou non dès que son « loc » est fermé : on cesse de copier les autres.
                    if (!decided && token == JsonTokenType.EndObject && depth == 3 && property[3] == "loc")
                    {
                        decided = true;
                        copying = currentFile is not null && _Normalize(currentFile) == target;
                    }

                    if (token == JsonTokenType.EndObject && depth == 2)
                    {
                        if (copying)
                        {
                            writer.Flush();
                            _Collect(copy.WrittenSpan, functions, skipped, declarations);
                        }
                        copying = false;
                    }
                }
            }
            catch (JsonException)
            {
                return null;
            }

            state = reader.CurrentState;
            var consumed = (int)reader.BytesConsumed;
            Buffer.BlockCopy(buffer, consumed, buffer, 0, length - consumed);
            length -= consumed;
            if (atEnd) break;
            if (length == buffer.Length) Array.Resize(ref buffer, buffer.Length * 2);
        }

        return sawRoot ? declarations.Finish(functions, skipped, aliases) : null;
    }

    private static bool _Under(string?[] property, int depth, string name)
    {
        for (var i = 0; i < depth; ++i)
            if (property[i] == name) return true;
        return false;
    }

    private static string _Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path).Replace('\\', '/').ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return path.Replace('\\', '/').ToLowerInvariant();
        }
    }

    private static void _Write(Utf8JsonWriter writer, ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject: writer.WriteStartObject(); break;
            case JsonTokenType.EndObject: writer.WriteEndObject(); break;
            case JsonTokenType.StartArray: writer.WriteStartArray(); break;
            case JsonTokenType.EndArray: writer.WriteEndArray(); break;
            case JsonTokenType.PropertyName: writer.WritePropertyName(reader.ValueSpan); break;
            case JsonTokenType.String: writer.WriteStringValue(reader.ValueSpan); break;
            case JsonTokenType.Number: writer.WriteRawValue(reader.ValueSpan); break;
            case JsonTokenType.True: writer.WriteBooleanValue(true); break;
            case JsonTokenType.False: writer.WriteBooleanValue(false); break;
            case JsonTokenType.Null: writer.WriteNullValue(); break;
        }
    }

    /// <summary>Range une déclaration de l'en-tête : une fonction, ou ce que les liaisons ne convertissent pas encore.</summary>
    /// <summary>Les <c>struct</c> et <c>enum</c> lus, dont certains n'ont pas de nom : un <c>typedef</c> écrit après eux le leur donne.</summary>
    private sealed class Declarations
    {
        private readonly List<(string Id, CRecord Record)> _records = [];
        private readonly List<(string Id, CEnum Enum)> _enums = [];
        private readonly Dictionary<string, string> _typedefNames = new(StringComparer.Ordinal);
        private readonly List<CppClass> _classes = [];
        private readonly List<string> _templates = [];

        public void Add(CppClass cppClass) => _classes.Add(cppClass);
        public void AddTemplate(string name) => _templates.Add(name);

        public void Add(string id, CRecord record) => _records.Add((id, record));
        public void Add(string id, CEnum enumeration) => _enums.Add((id, enumeration));
        public void Name(string declarationId, string typedefName) => _typedefNames.TryAdd(declarationId, typedefName);

        public CHeader Finish(List<CFunction> functions, List<CSkipped> skipped, Dictionary<string, string> aliases)
        {
            string Named(string id, string name) => name.Length > 0 ? name : _typedefNames.GetValueOrDefault(id, "");
            return new CHeader(functions, skipped, aliases,
                [.. _records.Select(r => r.Record with { Name = Named(r.Id, r.Record.Name) })],
                [.. _enums.Select(e => e.Enum with { Name = Named(e.Id, e.Enum.Name) })], null, [.. _classes], [.. _templates]);
        }
    }

    private static void _Collect(ReadOnlySpan<byte> json, List<CFunction> functions, List<CSkipped> skipped, Declarations declarations)
    {
        using var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 4096 });
        var node = document.RootElement;
        var kind = _String(node, "kind");
        var name = _String(node, "name");
        if (node.TryGetProperty("isImplicit", out var implicitNode) && implicitNode.ValueKind == JsonValueKind.True) return;

        switch (kind)
        {
            case "FunctionDecl":
                if (_Function(node, name) is { } function) functions.Add(function);
                break;
            // Un vide ou un bloc extern "C" ne dit rien à l'utilisateur.
            case "EmptyDecl" or "LinkageSpecDecl":
                break;
            // Un typedef peut donner son nom à la struct ou à l'enum anonyme qu'il déclare ; l'alias lui-même est lu à part.
            case "TypedefDecl":
                if (node.TryGetProperty("inner", out var typedefInner) && typedefInner.GetArrayLength() > 0
                    && typedefInner[0].TryGetProperty("decl", out var owned) && _String(owned, "id") is { Length: > 0 } ownedId)
                    declarations.Name(ownedId, name);
                break;
            // C++ : un espace de noms contient des classes ; une classe a ses constructeurs, méthodes et champs.
            case "NamespaceDecl" or "CXXRecordDecl" or "ClassTemplateDecl":
                CppReader.Read(node, "", declarations.Add, declarations.AddTemplate);
                break;
            case "RecordDecl":
                // Une déclaration sans définition (struct opaque) ne dit rien : un pointeur vers elle n'est pas converti de toute façon.
                if (!(node.TryGetProperty("completeDefinition", out var complete) && complete.ValueKind == JsonValueKind.True)) break;
                declarations.Add(_String(node, "id"), _Record(node, name));
                break;
            case "EnumDecl":
                if (_Enum(node, name) is { } enumeration) declarations.Add(_String(node, "id"), enumeration);
                else skipped.Add(new CSkipped(kind, name));
                break;
            default:
                skipped.Add(new CSkipped(kind, name));
                break;
        }
    }

    private static CRecord _Record(JsonElement node, string name)
    {
        List<CField> fields = [];
        if (node.TryGetProperty("inner", out var inner))
            foreach (var child in inner.EnumerateArray())
                if (_String(child, "kind") == "FieldDecl")
                    fields.Add(new CField(_String(child, "name"), child.TryGetProperty("type", out var type) ? _Type(type) : "",
                        child.TryGetProperty("isBitfield", out var bitfield) && bitfield.ValueKind == JsonValueKind.True));
        return new CRecord(name, _String(node, "tagUsed") == "union", fields);
    }

    /// <summary>L'enum et la valeur de chacune de ses constantes ; <see langword="null"/> si une valeur ne se lit pas.</summary>
    private static CEnum? _Enum(JsonElement node, string name)
    {
        List<(string, long)> constants = [];
        long next = 0;
        if (node.TryGetProperty("inner", out var inner))
            foreach (var child in inner.EnumerateArray())
            {
                if (_String(child, "kind") != "EnumConstantDecl") continue;

                // La valeur écrite est dans le ConstantExpr enfant ; sans elle, c'est celle de la précédente plus un.
                var value = next;
                if (child.TryGetProperty("inner", out var written) && written.GetArrayLength() > 0)
                {
                    if (!long.TryParse(_String(written[0], "value"), System.Globalization.CultureInfo.InvariantCulture, out value)) return null;
                }
                constants.Add((_String(child, "name"), value));
                next = value + 1;
            }
        return new CEnum(name, constants);
    }

    private static CFunction? _Function(JsonElement node, string name)
    {
        if (name.Length == 0 || !node.TryGetProperty("type", out var type)) return null;

        // « int (int, int) » : le type de retour est ce qui précède la parenthèse ouvrante qui commence les paramètres.
        var signature = _Type(type);
        var open = _SignatureStart(signature);
        if (open < 0) return null;

        List<CParameter> parameters = [];
        if (node.TryGetProperty("inner", out var inner))
            foreach (var child in inner.EnumerateArray())
                if (_String(child, "kind") == "ParmVarDecl")
                    parameters.Add(new CParameter(_String(child, "name"), child.TryGetProperty("type", out var parameterType) ? _Type(parameterType) : ""));

        return new CFunction(name, signature[..open].Trim(), parameters, signature.Contains("...", StringComparison.Ordinal));
    }

    private static int _SignatureStart(string signature)
    {
        // La parenthèse ouvrante des paramètres est celle dont la fermante termine le texte ; un type de retour peut contenir des parenthèses.
        if (!signature.EndsWith(')')) return -1;
        var depth = 0;
        for (var i = signature.Length - 1; i >= 0; --i)
        {
            if (signature[i] == ')') ++depth;
            else if (signature[i] == '(' && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>Le type d'un nœud : sans alias (<c>desugaredQualType</c>) quand Clang le donne, tel qu'écrit sinon.</summary>
    private static string _Type(JsonElement type) =>
        type.TryGetProperty("desugaredQualType", out var desugared) && desugared.ValueKind == JsonValueKind.String ? desugared.GetString()! : _String(type, "qualType");

    private static string _String(JsonElement node, string property) =>
        node.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
}
