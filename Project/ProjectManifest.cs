using System.Text.Json;
using Rgl.Build.Toolchain;
using Rgl.Diagnostics;
using Rgl.IR;

namespace Rgl.Project;

/// <summary>
/// Contenu de <c>rgl.json</c> : <c>name</c>, <c>version</c>, <c>icon</c> (facultatif), <c>rgl</c>,
/// la version du langage visée, et <c>overflow</c> (facultatif : <c>checked</c> par défaut, ou
/// <c>unchecked</c>). Toute autre clé avertit.
/// </summary>
/// <param name="Name">Nom du projet.</param>
/// <param name="Version">Version du projet.</param>
/// <param name="Icon">Chemin de l'icône, relatif au manifeste ; facultatif.</param>
/// <param name="LanguageVersion">Version du langage visée (clé <c>rgl</c>).</param>
/// <param name="Overflow">Ce que font les opérations entières qui débordent (clé <c>overflow</c>) ; en release aussi, sauf si <paramref name="ReleaseOverflow"/> le dit autrement.</param>
/// <param name="ReleaseOverflow">Le mode du profil release quand la clé <c>overflow</c> est un objet <c>{"debug": "checked", "release": "unchecked"}</c> ; <see langword="null"/> pour celui de <paramref name="Overflow"/>.</param>
/// <param name="Exclude">Dossiers du projet, relatifs au manifeste, où la recherche des fichiers <c>.forge</c> ne descend pas (clé <c>exclude</c> : un tableau de chemins, <c>["out"]</c> par défaut).</param>
/// <param name="RecursionLimit">Profondeur maximale des gears récursifs (clé <c>recursion</c> : un entier, 200 par défaut, ou <c>"infinite"</c>) ; <see langword="null"/> pour aucune limite.</param>
/// <param name="BoundedLoops">Boucles bornées (clé <c>loop</c> : <c>"free"</c> par défaut, ou <c>"safe"</c>) : une boucle de guard ou une reprise sans limite et <c>goto</c> sont refusés.</param>
/// <param name="Safe">Mode sûr (clé <c>safe</c> : un booléen, <see langword="false"/> par défaut) : un train doit traiter toutes les erreurs de ses étapes.</param>
/// <param name="NoTrust">Interdit les contrats @trusted utilisés hors std et cpp-trust.</param>
/// <param name="NoCpp">Interdit les blocs C++ et les liaisons natives hors std et cpp-trust.</param>
/// <param name="CppTrust">Chemins relatifs de fichiers forge explicitement autorisés.</param>
/// <param name="Assets">Fichiers et motifs relatifs copiés à côté de l'exécutable.</param>
internal sealed record ProjectManifest(string Name, string Version, string? Icon, string LanguageVersion, OverflowMode Overflow = OverflowMode.Checked, uint? RecursionLimit = ProjectManifest.DefaultRecursionLimit, string[]? Exclude = null, bool Safe = false, bool BoundedLoops = false, bool NoTrust = false, bool NoCpp = false, string[]? CppTrust = null, string[]? Assets = null, OverflowMode? ReleaseOverflow = null, IReadOnlyDictionary<string, BuildProfile>? Profiles = null)
{
    /// <summary>Dossiers exclus de la recherche des fichiers sans clé <c>exclude</c> : la sortie des builds.</summary>
    public static readonly string[] DefaultExclude = ["out"];

    /// <summary>Les dossiers exclus, <see cref="DefaultExclude"/> si le manifeste n'en dit rien.</summary>
    public string[] Exclude { get; init; } = Exclude ?? DefaultExclude;

    /// <summary>Nom du fichier manifeste, à la racine du projet.</summary>
    public string[] CppTrust { get; init; } = CppTrust ?? [];

    /// <summary>Fichiers et motifs copiés dans le dossier de sortie, en gardant leur chemin relatif.</summary>
    public string[] Assets { get; init; } = Assets ?? [];

    public const string FileName = "rgl.json";

    /// <summary>Profondeur maximale des gears récursifs sans clé <c>recursion</c>.</summary>
    public const uint DefaultRecursionLimit = 200;

    /// <summary>Version du langage que ce compilateur sait compiler.</summary>
    public const string SupportedLanguageVersion = "0";

    /// <summary>Clés reconnues ; les autres déclenchent un avertissement.</summary>
    private static readonly string[] KnownKeys = ["name", "version", "icon", "rgl", "overflow", "recursion", "exclude", "assets", "safe", "loop", "no-trust", "no-cpp", "cpp-trust", "profiles"];

    /// <summary>Lit et valide un manifeste ; renvoie <c>null</c> si une erreur a été signalée.</summary>
    /// <param name="path">Chemin de <c>rgl.json</c>.</param>
    /// <param name="diagnostics">Reçoit les erreurs et avertissements du manifeste.</param>
    public static ProjectManifest? Load(string path, DiagnosticBag diagnostics)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (JsonException e)
        {
            var line = e.LineNumber is { } n ? $" (ligne {n + 1})" : "";
            diagnostics.Error($"JSON invalide{line}", path);
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Error("le manifeste doit être un objet JSON", path);
                return null;
            }

            foreach (var property in root.EnumerateObject())
            {
                if (!KnownKeys.Contains(property.Name))
                    diagnostics.Warning($"clé inconnue « {property.Name} » ignorée (clés reconnues : {string.Join(", ", KnownKeys)})", path);
            }

            var name = ReadString(root, "name", required: true, path, diagnostics);
            var version = ReadString(root, "version", required: true, path, diagnostics);
            var icon = ReadString(root, "icon", required: false, path, diagnostics);
            var language = ReadString(root, "rgl", required: true, path, diagnostics);
            string? overflow;
            OverflowMode? releaseOverflow = null;
            var profileOverflowOk = true;
            if (root.TryGetProperty("overflow", out var overflowElement) && overflowElement.ValueKind == JsonValueKind.Object)
                (overflow, releaseOverflow, profileOverflowOk) = ReadProfileOverflow(overflowElement, path, diagnostics);
            else overflow = ReadString(root, "overflow", required: false, path, diagnostics);

            if (language is not null && language != SupportedLanguageVersion)
            {
                diagnostics.Error(
                    $"version du langage « {language} » non prise en charge : ce compilateur vise « {SupportedLanguageVersion} »",
                    path);
                return null;
            }

            OverflowMode? overflowMode = overflow switch
            {
                null or "checked" => OverflowMode.Checked,
                "unchecked" => OverflowMode.Unchecked,
                _ => null,
            };
            if (overflowMode is null)
                diagnostics.Error($"la clé « overflow » vaut « checked » (arrêt du programme) ou « unchecked » (roulement), pas « {overflow} »", path);

            var (recursionOk, recursion) = ReadRecursion(root, path, diagnostics);
            var (excludeOk, exclude) = ReadExclude(root, path, diagnostics);

            var safe = false;
            var safeOk = true;
            if (root.TryGetProperty("safe", out var safeValue))
            {
                if (safeValue.ValueKind is JsonValueKind.True or JsonValueKind.False) safe = safeValue.GetBoolean();
                else
                {
                    diagnostics.Error("la clé « safe » vaut true (mode sûr) ou false", path);
                    safeOk = false;
                }
            }

            var boundedLoops = false;
            var loopOk = true;
            if (root.TryGetProperty("loop", out var loopValue))
            {
                if (loopValue.ValueKind == JsonValueKind.String && loopValue.GetString() is "free" or "safe") boundedLoops = loopValue.GetString() == "safe";
                else
                {
                    diagnostics.Error("la clé « loop » vaut \"free\" (boucles libres) ou \"safe\" (boucles bornées, sans goto)", path);
                    loopOk = false;
                }
            }

            var (noTrustOk, noTrust) = ReadBoolean(root, "no-trust", path, diagnostics);
            var (noCppOk, noCpp) = ReadBoolean(root, "no-cpp", path, diagnostics);
            var (cppTrustOk, cppTrust) = ReadCppTrust(root, path, diagnostics);
            var (assetsOk, assets) = ReadAssets(root, path, diagnostics);
            var (profilesOk, profiles) = ReadProfiles(root, path, diagnostics);

            if (!profilesOk || !profileOverflowOk || !noTrustOk || !noCppOk || !cppTrustOk || !assetsOk || name is null || version is null || language is null || overflowMode is null || !recursionOk || !excludeOk || !safeOk || !loopOk)
                return null;

            if (icon is not null && !File.Exists(Path.Combine(Path.GetDirectoryName(path)!, icon)))
                diagnostics.Warning($"icône « {icon} » introuvable", path);

            return new ProjectManifest(name, version, icon, language, overflowMode.Value, recursion, exclude, safe, boundedLoops, noTrust, noCpp, cppTrust, assets, releaseOverflow, profiles);
        }
    }

    /// <summary>Le mode de débordement d'un profil, ou <see langword="null"/> s'il n'est ni <c>checked</c> ni <c>unchecked</c>.</summary>
    private static OverflowMode? ParseOverflow(string? text) => text switch
    {
        "checked" => OverflowMode.Checked,
        "unchecked" => OverflowMode.Unchecked,
        _ => null,
    };

    /// <summary>
    /// La clé <c>overflow</c> écrite en objet, un mode par profil : <c>{"debug": "checked", "release": "unchecked"}</c>. Un profil absent
    /// vaut <c>checked</c>. Rend le texte du mode de debug (le mode par défaut), celui de release et si tout est valide.
    /// </summary>
    private static (string Debug, OverflowMode? Release, bool Ok) ReadProfileOverflow(JsonElement element, string path, DiagnosticBag diagnostics)
    {
        var ok = true;
        string debug = "checked";
        OverflowMode? release = null;
        foreach (var property in element.EnumerateObject())
        {
            var text = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
            if (property.Name is not ("debug" or "release"))
            {
                diagnostics.Error($"la clé « overflow » ne règle que les profils « debug » et « release », pas « {property.Name} »", path);
                ok = false;
            }
            else if (ParseOverflow(text) is not { } mode)
            {
                diagnostics.Error($"« overflow.{property.Name} » vaut « checked » (arrêt du programme) ou « unchecked » (roulement)", path);
                ok = false;
            }
            else if (property.Name == "debug") debug = text!;
            else release = mode;
        }
        // Sans profil release écrit, il suit celui de debug.
        if (ok && release is null && element.TryGetProperty("release", out _) == false) release = ParseOverflow(debug);
        return (debug, release, ok);
    }

    /// <summary>
    /// Les profils de build écrits dans la clé <c>profiles</c> : <c>{"rapide": {"extends": "release", "flags": ["-march=native"], "link": [], "overflow": "unchecked"}}</c>.
    /// <c>extends</c> est la famille dont le profil part (<c>debug</c>, <c>release</c> ou <c>size</c>, <c>release</c> par défaut), <c>flags</c> et <c>link</c> des options en plus
    /// pour le compilateur et pour l'édition de liens. Les noms <c>debug</c>, <c>release</c> et <c>size</c> sont ceux des profils fournis.
    /// </summary>
    private static (bool Ok, IReadOnlyDictionary<string, BuildProfile>? Profiles) ReadProfiles(JsonElement root, string path, DiagnosticBag diagnostics)
    {
        if (!root.TryGetProperty("profiles", out var element)) return (true, null);
        if (element.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Error("la clé « profiles » est un objet : un profil par nom", path);
            return (false, null);
        }

        var ok = true;
        var profiles = new Dictionary<string, BuildProfile>(StringComparer.Ordinal);
        foreach (var profile in element.EnumerateObject())
        {
            var name = profile.Name;
            if (BuildProfile.BuiltIn(name) is not null)
            {
                diagnostics.Error($"« profiles.{name} » : « {name} » est un profil fourni, choisis un autre nom", path);
                ok = false;
                continue;
            }
            if (name.Length == 0 || !name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_'))
            {
                diagnostics.Error($"« profiles.{name} » : un nom de profil s'écrit en minuscules, chiffres, « - » et « _ »", path);
                ok = false;
                continue;
            }
            if (profile.Value.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Error($"« profiles.{name} » est un objet : extends, flags, link, overflow", path);
                ok = false;
                continue;
            }

            var kind = ProfileKind.Release;
            var compile = new List<string>();
            var link = new List<string>();
            OverflowMode? overflow = null;
            foreach (var key in profile.Value.EnumerateObject())
            {
                switch (key.Name)
                {
                    case "extends":
                        if (key.Value.ValueKind == JsonValueKind.String && BuildProfile.BuiltIn(key.Value.GetString()!) is { } parent) kind = parent.Kind;
                        else
                        {
                            diagnostics.Error($"« profiles.{name}.extends » vaut « debug », « release » ou « size »", path);
                            ok = false;
                        }
                        break;
                    case "flags" or "link":
                        var list = key.Name == "flags" ? compile : link;
                        if (key.Value.ValueKind == JsonValueKind.Array && key.Value.EnumerateArray().All(v => v.ValueKind == JsonValueKind.String)) list.AddRange(key.Value.EnumerateArray().Select(v => v.GetString()!));
                        else
                        {
                            diagnostics.Error($"« profiles.{name}.{key.Name} » est un tableau de chaînes", path);
                            ok = false;
                        }
                        break;
                    case "overflow":
                        if (key.Value.ValueKind == JsonValueKind.String && ParseOverflow(key.Value.GetString()) is { } mode) overflow = mode;
                        else
                        {
                            diagnostics.Error($"« profiles.{name}.overflow » vaut « checked » (arrêt du programme) ou « unchecked » (roulement)", path);
                            ok = false;
                        }
                        break;
                    default:
                        diagnostics.Error($"« profiles.{name}.{key.Name} » : clé inconnue (extends, flags, link, overflow)", path);
                        ok = false;
                        break;
                }
            }
            profiles[name] = new BuildProfile(name, kind, compile, link, overflow);
        }
        return (ok, profiles);
    }

    /// <summary>Le mode de débordement du profil de build : le sien s'il en écrit un, sinon celui du manifeste (celui de release pour un profil qui optimise).</summary>
    public OverflowMode OverflowFor(BuildProfile profile) => profile.Overflow ?? OverflowFor(profile.IsOptimized);

    /// <summary>Le mode de débordement du profil demandé.</summary>
    /// <param name="release">Vrai pour le profil release.</param>
    public OverflowMode OverflowFor(bool release) => release ? ReleaseOverflow ?? Overflow : Overflow;

    private static (bool Ok, bool Value) ReadBoolean(JsonElement root, string key, string path, DiagnosticBag diagnostics)
    {
        if (!root.TryGetProperty(key, out var value)) return (true, false);
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return (true, value.GetBoolean());
        diagnostics.Error($"la clé « {key} » vaut true ou false", path);
        return (false, false);
    }

    private static (bool Ok, string[] Paths) ReadCppTrust(JsonElement root, string path, DiagnosticBag diagnostics)
    {
        if (!root.TryGetProperty("cpp-trust", out var value)) return (true, []);
        if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String))
        {
            diagnostics.Error("la clé « cpp-trust » est un tableau de chemins relatifs de fichiers .forge", path);
            return (false, []);
        }
        var paths = value.EnumerateArray().Select(e => e.GetString()!).ToArray();
        if (paths.Any(p => !NativePolicy.ValidTrustPath(p)))
        {
            diagnostics.Error("cpp-trust exige des chemins .forge relatifs à la racine, sans remontée hors du projet", path);
            return (false, []);
        }
        return (true, paths);
    }

    private static (bool Ok, string[] Paths) ReadAssets(JsonElement root, string path, DiagnosticBag diagnostics)
    {
        if (!root.TryGetProperty("assets", out var value)) return (true, []);
        if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String || e.GetString() is not { Length: > 0 }))
        {
            diagnostics.Error("la clé « assets » est un tableau de chemins ou motifs relatifs, par exemple : [\"front/html/**\"]", path);
            return (false, []);
        }
        var paths = value.EnumerateArray().Select(e => e.GetString()!.Replace('\\', '/')).ToArray();
        if (paths.Any(p => Path.IsPathFullyQualified(p) || p.StartsWith('/') || (p.Length >= 2 && char.IsAsciiLetter(p[0]) && p[1] == ':')
            || p.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(part => part == "..")))
        {
            diagnostics.Error("assets exige des chemins relatifs à la racine, sans remontée hors du projet", path);
            return (false, []);
        }
        return (true, paths);
    }

    /// <summary>
    /// La clé <c>exclude</c> : un tableau de chemins de dossiers, relatifs au manifeste ; absente, <see cref="DefaultExclude"/>.
    /// </summary>
    private static (bool Ok, string[]? Folders) ReadExclude(JsonElement root, string path, DiagnosticBag diagnostics)
    {
        if (!root.TryGetProperty("exclude", out var value)) return (true, null);
        if (value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 }))
            return (true, [.. value.EnumerateArray().Select(e => e.GetString()!)]);

        diagnostics.Error("la clé « exclude » vaut un tableau de chemins de dossiers, par exemple : \"exclude\": [\"out\", \"vendor\"]", path);
        return (false, null);
    }

    /// <summary>
    /// La clé <c>recursion</c> : un entier au moins égal à 1, ou <c>"infinite"</c> ; absente, la
    /// limite par défaut.
    /// </summary>
    /// <returns>Si la valeur est valide, et la limite (<see langword="null"/> pour aucune).</returns>
    private static (bool Ok, uint? Limit) ReadRecursion(JsonElement root, string path, DiagnosticBag diagnostics)
    {
        if (!root.TryGetProperty("recursion", out var value)) return (true, DefaultRecursionLimit);
        if (value.ValueKind == JsonValueKind.String && value.GetString() == "infinite") return (true, null);
        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out var limit) && limit >= 1) return (true, limit);

        diagnostics.Error("la clé « recursion » vaut un entier au moins égal à 1 (profondeur maximale) ou « infinite », par exemple : \"recursion\": 500", path);
        return (false, null);
    }

    /// <summary>
    /// Lit une clé dont la valeur doit être une chaîne non vide. Signale une erreur si elle est
    /// absente alors qu'obligatoire, ou si elle n'est pas une chaîne non vide.
    /// </summary>
    /// <returns>La valeur, ou <c>null</c> si elle est absente ou invalide.</returns>
    private static string? ReadString(JsonElement root, string key, bool required, string path, DiagnosticBag diagnostics)
    {
        if (!root.TryGetProperty(key, out var value))
        {
            if (required)
                diagnostics.Error($"clé « {key} » absente", path);
            return null;
        }

        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 } text)
        {
            diagnostics.Error($"la clé « {key} » doit être une chaîne non vide", path);
            return null;
        }

        return text;
    }
}
