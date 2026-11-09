using Rgl.Diagnostics;

namespace Rgl.Cli;

/// <summary>Commande demandée à <c>rgl</c>.</summary>
internal enum CommandKind
{
    /// <summary><c>rgl</c>, <c>rgl help</c>, <c>-h</c> ou <c>--help</c> : affiche l'aide.</summary>
    Help,

    /// <summary><c>rgl --version</c> : affiche la version du compilateur.</summary>
    Version,

    /// <summary><c>rgl init</c> : crée le squelette d'un projet.</summary>
    Init,

    /// <summary><c>rgl compile</c> : compile le fichier <c>.rgl</c> d'entrée.</summary>
    Compile,

    /// <summary><c>rgl exec</c> : compile puis lance le programme.</summary>
    Exec,

    /// <summary><c>rgl check</c> : bilan de l'intégration C++.</summary>
    Check,

    /// <summary><c>rgl doc build</c> ou <c>rgl doc check</c> : la documentation <c>///</c> du projet.</summary>
    Doc,

    /// <summary><c>rgl lsp</c> : serveur de langage pour les éditeurs, sur l'entrée et la sortie standard.</summary>
    LanguageServer,

    /// <summary><c>rgl cache --size</c> ou <c>rgl cache clear</c> : la taille du cache de RGL, ou son vidage.</summary>
    Cache,

    /// <summary><c>rgl descriptor en-tête.hpp --class ns::Classe</c> : écrit le squelette d'un fichier forge: descriptor.</summary>
    Descriptor,

    /// <summary><c>rgl format [chemin…] [--check]</c> : met en forme les fichiers <c>.rgl</c> et <c>.forge</c>.</summary>
    Format,

    /// <summary><c>rgl precast export [fichier|-d dossier] [-o fichier]</c> : écrit les precasts en JSON pour les outils.</summary>
    Precast,

    /// <summary><c>rgl setup</c> : installe Zig (et OpenSSL) à côté de l'exécutable et propose de l'ajouter au PATH.</summary>
    Setup,
}

/// <summary>Ce que demande <c>rgl cache</c>.</summary>
internal enum CacheAction
{
    /// <summary><c>rgl cache --size</c> : la taille du cache.</summary>
    Size,

    /// <summary><c>rgl cache clear</c> : vide le cache.</summary>
    Clear,
}

/// <summary>Ce que demande <c>rgl doc</c>.</summary>
internal enum DocAction
{
    /// <summary><c>rgl doc build</c> : écrit la référence de l'API (<c>api.json</c>, <c>api.xml</c>).</summary>
    Build,

    /// <summary><c>rgl doc check</c> : vérifie les commentaires de documentation.</summary>
    Check,
}

/// <summary>Ligne de commande analysée.</summary>
/// <param name="Command">Commande demandée.</param>
/// <param name="File">Fichier <c>.rgl</c> donné en argument (<c>compile</c> / <c>exec</c>).</param>
/// <param name="Directory">Dossier donné par <c>-d</c>, dont on prend le fichier <c>.rgl</c>.</param>
/// <param name="EmitCpp">Arrêt après la transpilation (anciennement <c>--debug</c>).</param>
/// <param name="ProgramArgs">Arguments transmis au programme par <c>exec</c>, après <c>--</c>.</param>
/// <param name="Release">Build de release (<c>--release</c>) : le plus rapide, puis le plus léger. Sinon, build de debug.</param>
/// <param name="EmitAsm">Arrêt après la production de l'assembleur du C++ généré (<c>--emit-asm</c>).</param>
/// <param name="Doc">L'action de <c>rgl doc</c>.</param>
/// <param name="Strict">Avec <c>rgl doc check --strict</c> : un avertissement de documentation fait échouer la commande ; avec <c>rgl check --strict</c> : un symbole natif utilisé et non converti.</param>
/// <param name="Cache">L'action de <c>rgl cache</c>.</param>
/// <param name="All">Avec <c>rgl check --all</c> : liste aussi les symboles non convertis que rien n'utilise.</param>
/// <param name="Class">Avec <c>rgl descriptor</c> : la classe C++ à décrire, avec ses espaces de noms.</param>
/// <param name="Name">Avec <c>rgl descriptor</c> : le nom du type côté RGL ; le dernier mot de la classe par défaut.</param>
/// <param name="Output">Avec <c>rgl descriptor</c> : le fichier à écrire ; la sortie standard par défaut.</param>
/// <param name="Includes">Avec <c>rgl descriptor</c> : des dossiers où chercher ce que l'en-tête inclut (<c>-I</c>).</param>
/// <param name="ExplainOptimizations">Avec <c>--explain-optimizations</c> : s'arrête après la transpilation et dit quelles optimisations ont été faites (parcours fondus, accumulateur déplacé, récursion terminale en boucle…) et lesquelles ont été refusées, avec la raison.</param>
/// <param name="CountAllocations">Avec <c>--count-allocations</c> : le programme compté écrit sur la sortie d'erreur, en s'arrêtant, le nombre de ses blocs de mémoire et de ses copies profondes (profil <c>&lt;nom&gt;-compteurs</c>).</param>
/// <param name="ExplainVectorization">Avec <c>compile --explain-vectorization</c> : s'arrête après avoir compilé avec les optimisations de release et dit quelles boucles Clang vectorise, et pourquoi pas les autres.</param>
/// <param name="Profile">Le profil demandé par <c>--debug</c>, <c>--release</c>, <c>--release=size</c> ou <c>--profile nom</c> ; <see langword="null"/> pour le profil par défaut (<c>debug</c>).</param>
/// <param name="Web">Avec <c>rgl init --web</c> : crée un serveur HTTP et une page Paper.</param>
internal sealed record CommandLine(
    CommandKind Command,
    string? File = null,
    string? Directory = null,
    bool EmitCpp = false,
    IReadOnlyList<string>? ProgramArgs = null,
    bool Release = false,
    bool EmitAsm = false,
    DocAction Doc = DocAction.Check,
    bool Strict = false,
    CacheAction Cache = CacheAction.Size,
    bool All = false,
    string? Class = null,
    string? Name = null,
    string? Output = null,
    IReadOnlyList<string>? Includes = null,
    bool Web = false,
    bool ExplainVectorization = false,
    bool CountAllocations = false,
    bool ExplainOptimizations = false,
    string? Profile = null,
    IReadOnlyList<string>? Paths = null,
    bool CheckOnly = false,
    bool Force = false,
    bool AddToPath = false,
    bool NoPath = false)
{
    /// <summary>Avec <c>rgl format</c> : les fichiers et dossiers à mettre en forme ; vide pour le dossier courant.</summary>
    public IReadOnlyList<string> Paths { get; init; } = Paths ?? [];

    /// <summary>Les dossiers d'<c>-I</c> de <c>rgl descriptor</c> ; vide s'il n'y en a pas.</summary>
    public IReadOnlyList<string> Includes { get; init; } = Includes ?? [];

    /// <summary>Arguments transmis au programme par <c>exec</c> ; vide s'il n'y en a pas.</summary>
    public IReadOnlyList<string> ProgramArgs { get; init; } = ProgramArgs ?? [];
}

/// <summary>Analyse les arguments de <c>rgl</c> en une <see cref="CommandLine"/>.</summary>
internal static class CommandLineParser
{
    /// <summary>Analyse les arguments ; renvoie <c>null</c> si une erreur a été signalée.</summary>
    /// <param name="args">Arguments de la ligne de commande, sans le nom du programme.</param>
    /// <param name="diagnostics">Reçoit les erreurs de syntaxe de la ligne de commande.</param>
    public static CommandLine? Parse(IReadOnlyList<string> args, DiagnosticBag diagnostics)
    {
        if (args.Count == 0)
            return new CommandLine(CommandKind.Help);

        switch (args[0])
        {
            case "-h" or "--help" or "help":
                return new CommandLine(CommandKind.Help);
            case "--version":
                return new CommandLine(CommandKind.Version);
            case "init":
                return ParseInit(args, diagnostics);
            case "check":
                return ParseCheck(args, diagnostics);
            case "cache":
                return ParseCache(args, diagnostics);
            case "descriptor":
                return ParseDescriptor(args, diagnostics);
            case "compile":
                return ParseBuild(CommandKind.Compile, args, diagnostics);
            case "exec":
                return ParseBuild(CommandKind.Exec, args, diagnostics);
            case "doc":
                return ParseDoc(args, diagnostics);
            case "format":
                return ParseFormat(args, diagnostics);
            case "precast":
                return ParsePrecast(args, diagnostics);
            case "setup":
                return ParseSetup(args, diagnostics);
            case "lsp":
                return ParseNoArguments(CommandKind.LanguageServer, args, diagnostics);
            default:
                diagnostics.Error($"commande inconnue « {args[0]} » (commandes : init, setup, compile, exec, check, cache, descriptor, doc, format, precast, lsp)");
                return null;
        }
    }

    /// <summary>Analyse <c>rgl setup [--force] [--add-to-path | --no-path]</c>.</summary>
    private static CommandLine? ParseSetup(IReadOnlyList<string> args, DiagnosticBag diagnostics)
    {
        bool force = false, addToPath = false, noPath = false;
        for (var i = 1; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--force": force = true; break;
                case "--add-to-path": addToPath = true; break;
                case "--no-path": noPath = true; break;
                default:
                    diagnostics.Error($"option inconnue « {args[i]} » pour « rgl setup » (options : --force, --add-to-path, --no-path)");
                    return null;
            }
        }
        if (addToPath && noPath)
        {
            diagnostics.Error("« --add-to-path » et « --no-path » s'excluent");
            return null;
        }
        return new CommandLine(CommandKind.Setup, Force: force, AddToPath: addToPath, NoPath: noPath);
    }

    /// <summary>Analyse <c>rgl precast export [fichier.rgl | -d dossier] [-o fichier]</c>.</summary>
    private static CommandLine? ParsePrecast(IReadOnlyList<string> args, DiagnosticBag diagnostics)
    {
        if (args.Count < 2 || args[1] != "export")
        {
            diagnostics.Error("« rgl precast » attend « export »");
            return null;
        }
        string? file = null, directory = null, output = null;
        for (var i = 2; i < args.Count; i++)
        {
            if (args[i] is "-d" or "-o")
            {
                if (i + 1 >= args.Count)
                {
                    diagnostics.Error($"l'option « {args[i]} » attend une valeur");
                    return null;
                }
                if (args[i] == "-d") directory = args[++i];
                else output = args[++i];
            }
            else if (args[i].StartsWith('-'))
            {
                diagnostics.Error($"option inconnue « {args[i]} » pour « rgl precast export »");
                return null;
            }
            else if (file is not null)
            {
                diagnostics.Error($"un seul fichier d'entrée est attendu (reçu « {file} » puis « {args[i]} »)");
                return null;
            }
            else file = args[i];
        }
        if (file is not null && directory is not null)
        {
            diagnostics.Error("donner un fichier ou « -d <dossier> », pas les deux");
            return null;
        }
        return new CommandLine(CommandKind.Precast, file, directory ?? (file is null ? "." : null), Output: output);
    }

    /// <summary>Analyse <c>rgl format [chemin…] [--check]</c>.</summary>
    private static CommandLine? ParseFormat(IReadOnlyList<string> args, DiagnosticBag diagnostics)
    {
        List<string> paths = [];
        var check = false;
        for (var i = 1; i < args.Count; i++)
        {
            if (args[i] == "--check") check = true;
            else if (args[i].StartsWith('-'))
            {
                diagnostics.Error($"option inconnue « {args[i]} » pour « rgl format »");
                return null;
            }
            else paths.Add(args[i]);
        }
        return new CommandLine(CommandKind.Format, Paths: paths, CheckOnly: check);
    }

    /// <summary>
    /// Analyse <c>rgl doc &lt;build|check&gt;</c> : un fichier ou <c>-d &lt;dossier&gt;</c> (exactement l'un des deux) et, pour <c>check</c>, <c>--strict</c>.
    /// </summary>
    private static CommandLine? ParseDoc(IReadOnlyList<string> args, DiagnosticBag diagnostics)
    {
        if (args.Count < 2 || args[1] is not ("build" or "check"))
        {
            diagnostics.Error("« rgl doc » attend « build » ou « check »");
            return null;
        }

        var action = args[1] == "build" ? DocAction.Build : DocAction.Check;
        string? file = null, directory = null;
        var strict = false;
        for (var i = 2; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "-d":
                    if (i + 1 >= args.Count)
                    {
                        diagnostics.Error("l'option « -d » attend un dossier");
                        return null;
                    }
                    directory = args[++i];
                    break;
                case "--strict" when action == DocAction.Check:
                    strict = true;
                    break;
                case var option when option.StartsWith('-'):
                    diagnostics.Error($"option inconnue « {option} » pour « rgl doc {args[1]} »");
                    return null;
                default:
                    if (file is not null)
                    {
                        diagnostics.Error($"un seul fichier d'entrée est attendu (reçu « {file} » puis « {args[i]} »)");
                        return null;
                    }
                    file = args[i];
                    break;
            }
        }

        if (file is not null && directory is not null)
        {
            diagnostics.Error("donner un fichier ou « -d <dossier> », pas les deux");
            return null;
        }
        if (file is null && directory is null)
        {
            diagnostics.Error($"« rgl doc {args[1]} » attend un fichier .rgl ou « -d <dossier> »");
            return null;
        }

        return new CommandLine(CommandKind.Doc, file, directory, Doc: action, Strict: strict);
    }

    /// <summary>Analyse <c>rgl check [fichier.rgl | -d dossier] [--all] [--strict]</c> : sans fichier ni dossier, le projet du dossier courant.</summary>
    private static CommandLine? ParseCheck(IReadOnlyList<string> args, DiagnosticBag diagnostics)
    {
        string? file = null, directory = null;
        bool all = false, strict = false;
        for (var i = 1; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "-d":
                    if (i + 1 >= args.Count)
                    {
                        diagnostics.Error("l'option « -d » attend un dossier");
                        return null;
                    }
                    directory = args[++i];
                    break;
                case "--all":
                    all = true;
                    break;
                case "--strict":
                    strict = true;
                    break;
                case var option when option.StartsWith('-'):
                    diagnostics.Error($"option inconnue « {option} » pour « rgl check »");
                    return null;
                default:
                    if (file is not null)
                    {
                        diagnostics.Error($"un seul fichier d'entrée est attendu (reçu « {file} » puis « {args[i]} »)");
                        return null;
                    }
                    file = args[i];
                    break;
            }
        }

        if (file is not null && directory is not null)
        {
            diagnostics.Error("donner un fichier ou « -d <dossier> », pas les deux");
            return null;
        }
        return new CommandLine(CommandKind.Check, file, directory, Strict: strict, All: all);
    }

    /// <summary>
    /// Analyse <c>rgl descriptor en-tête --class Classe [--name Nom] [-o fichier] [-I dossier]…</c> : un en-tête et une classe, le reste est facultatif.
    /// </summary>
    private static CommandLine? ParseDescriptor(IReadOnlyList<string> args, DiagnosticBag diagnostics)
    {
        string? header = null, cppClass = null, name = null, output = null;
        List<string> includes = [];
        for (var i = 1; i < args.Count; i++)
        {
            string? Value(string option)
            {
                if (i + 1 < args.Count) return args[++i];
                diagnostics.Error($"l'option « {option} » attend une valeur");
                return null;
            }

            switch (args[i])
            {
                case "--class":
                    if ((cppClass = Value("--class")) is null) return null;
                    break;
                case "--name":
                    if ((name = Value("--name")) is null) return null;
                    break;
                case "-o":
                    if ((output = Value("-o")) is null) return null;
                    break;
                case "-I":
                    if (Value("-I") is not { } include) return null;
                    includes.Add(include);
                    break;
                case var option when option.StartsWith('-'):
                    diagnostics.Error($"option inconnue « {option} » pour « rgl descriptor »");
                    return null;
                default:
                    if (header is not null)
                    {
                        diagnostics.Error($"un seul en-tête est attendu (reçu « {header} » puis « {args[i]} »)");
                        return null;
                    }
                    header = args[i];
                    break;
            }
        }

        if (header is null || cppClass is null)
        {
            diagnostics.Error("« rgl descriptor » attend un en-tête et « --class Classe » : rgl descriptor lib/counter.hpp --class demo::Counter");
            return null;
        }
        return new CommandLine(CommandKind.Descriptor, header, Class: cppClass, Name: name, Output: output, Includes: includes);
    }

    /// <summary>Analyse <c>rgl cache --size</c> ou <c>rgl cache clear</c> : exactement l'une des deux.</summary>
    private static CommandLine? ParseCache(IReadOnlyList<string> args, DiagnosticBag diagnostics)
    {
        if (args.Count != 2 || args[1] is not ("--size" or "clear"))
        {
            diagnostics.Error("« rgl cache » attend « --size » (la taille du cache) ou « clear » (le vide)");
            return null;
        }
        return new CommandLine(CommandKind.Cache, Cache: args[1] == "clear" ? CacheAction.Clear : CacheAction.Size);
    }

    /// <summary>Analyse une commande qui n'accepte aucun argument (<c>rgl init</c>).</summary>
    private static CommandLine? ParseNoArguments(CommandKind command, IReadOnlyList<string> args, DiagnosticBag diagnostics)
    {
        if (args.Count > 1)
        {
            diagnostics.Error($"« rgl {args[0]} » ne prend pas d'argument (reçu « {args[1]} »)");
            return null;
        }
        return new CommandLine(command);
    }

    /// <summary>Analyse <c>rgl init</c> et son squelette web optionnel.</summary>
    private static CommandLine? ParseInit(IReadOnlyList<string> args, DiagnosticBag diagnostics)
    {
        if (args.Count == 1) return new CommandLine(CommandKind.Init);
        if (args.Count == 2 && args[1] == "--web") return new CommandLine(CommandKind.Init, Web: true);
        diagnostics.Error($"option inconnue « {args[1]} » pour « rgl init » (option disponible : --web)");
        return null;
    }



    /// <summary>Retient le profil demandé ; un seul profil par commande.</summary>
    private static bool _ChooseProfile(ref string? chosen, string name, DiagnosticBag diagnostics)
    {
        if (chosen is null)
        {
            chosen = name;
            return true;
        }
        diagnostics.Error($"un seul profil par commande : « {chosen} » et « {name} » sont demandés");
        return false;
    }

    /// <summary>
    /// Analyse les arguments de <c>compile</c> et <c>exec</c> : un fichier ou <c>-d &lt;dossier&gt;</c>
    /// (exactement l'un des deux), <c>--emit-cpp</c>, et pour <c>exec</c> les arguments du programme
    /// après <c>--</c>. Toutes les erreurs sont signalées avant d'abandonner, sauf celles qui
    /// empêchent de lire la suite.
    /// </summary>
    private static CommandLine? ParseBuild(CommandKind command, IReadOnlyList<string> args, DiagnosticBag diagnostics)
    {
        var name = args[0];
        string? file = null;
        string? directory = null;
        var emitCpp = false;
        var emitAsm = false;
        var explainVectorization = false;
        var countAllocations = false;
        var explainOptimizations = false;
        var release = false;
        string? profileName = null;
        IReadOnlyList<string> programArgs = [];
        var ok = true;

        for (var i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--":
                    if (command != CommandKind.Exec)
                    {
                        diagnostics.Error("« -- » n'a de sens qu'avec « rgl exec » : il précède les arguments du programme");
                        return null;
                    }
                    programArgs = [.. args.Skip(i + 1)];
                    i = args.Count;
                    break;
                case "-d":
                    if (i + 1 >= args.Count)
                    {
                        diagnostics.Error("l'option « -d » attend un dossier");
                        return null;
                    }
                    if (directory is not null)
                    {
                        diagnostics.Error("l'option « -d » est donnée plusieurs fois");
                        ok = false;
                    }
                    directory = args[++i];
                    break;
                case "--emit-cpp":
                    emitCpp = true;
                    break;
                case "--emit-asm":
                    emitAsm = true;
                    break;
                case "--explain-optimizations":
                    explainOptimizations = true;
                    break;
                case "--count-allocations":
                    countAllocations = true;
                    break;
                case "--explain-vectorization":
                    explainVectorization = true;
                    break;
                case "--release":
                    release = true;
                    if (!_ChooseProfile(ref profileName, "release", diagnostics)) ok = false;
                    break;
                case "--release=size":
                    if (!_ChooseProfile(ref profileName, "size", diagnostics)) ok = false;
                    break;
                case "--debug":
                    if (!_ChooseProfile(ref profileName, "debug", diagnostics)) ok = false;
                    break;
                case "--profile":
                    if (i + 1 >= args.Count)
                    {
                        diagnostics.Error("l'option « --profile » attend le nom d'un profil");
                        return null;
                    }
                    if (!_ChooseProfile(ref profileName, args[++i], diagnostics)) ok = false;
                    break;
                default:
                    if (arg.StartsWith('-'))
                    {
                        diagnostics.Error($"option inconnue « {arg} » pour « rgl {name} »");
                        ok = false;
                    }
                    else if (file is not null)
                    {
                        diagnostics.Error($"un seul fichier d'entrée est attendu (reçu « {file} » puis « {arg} »)");
                        ok = false;
                    }
                    else
                    {
                        file = arg;
                    }
                    break;
            }
        }

        if (!ok)
            return null;

        if (file is not null && directory is not null)
        {
            diagnostics.Error("donner un fichier ou « -d <dossier> », pas les deux");
            return null;
        }
        if (file is null && directory is null)
        {
            diagnostics.Error($"« rgl {name} » attend un fichier .rgl ou « -d <dossier> »");
            return null;
        }

        return new CommandLine(command, file, directory, emitCpp, programArgs, release, emitAsm, ExplainVectorization: explainVectorization, Profile: profileName, CountAllocations: countAllocations, ExplainOptimizations: explainOptimizations);
    }
}
