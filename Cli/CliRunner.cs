using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using Rgl.Build;
using Rgl.Build.Toolchain;
using Rgl.Diagnostics;
using Rgl.Lsp;
using Rgl.Project;

namespace Rgl.Cli;

/// <summary>Environnement d'une exécution de <c>rgl</c>, injecté pour les tests.</summary>
/// <param name="Out">Sortie standard : aide, version, comptes rendus.</param>
/// <param name="Error">Sortie d'erreur : diagnostics.</param>
/// <param name="WorkingDirectory">Dossier courant, base des chemins relatifs.</param>
/// <param name="CacheRoot">Racine du cache de RGL (voir <see cref="Cache"/>).</param>
/// <param name="Color">Vrai pour colorer les diagnostics (voir <see cref="TerminalColors"/>).</param>
/// <param name="Toolchain">Chaîne de compilation C++ ; <see langword="null"/> si elle est introuvable.</param>
/// <param name="Input">Entrée standard brute, lue par le serveur de langage ; <see langword="null"/> sans entrée.</param>
/// <param name="Output">Sortie standard brute, où écrit le serveur de langage ; <see langword="null"/> sans sortie.</param>
/// <param name="DiskSize">La taille du disque du cache, en octets, qui remplace celle que le système donne ; sert aux tests de l'avertissement du cache.</param>
internal sealed record CliContext(TextWriter Out, TextWriter Error, string WorkingDirectory, string CacheRoot, bool Color = false, ICompilerToolchain? Toolchain = null,
    Stream? Input = null, Stream? Output = null, long? DiskSize = null);

/// <summary>Exécute une commande <c>rgl</c> et renvoie le code de sortie du processus.</summary>
internal static class CliRunner
{
    /// <summary>Code de sortie : la commande a abouti.</summary>
    public const int Success = 0;

    /// <summary>Code de sortie : la commande a échoué (projet invalide, erreur de compilation…).</summary>
    public const int Failure = 1;

    /// <summary>Code de sortie : la ligne de commande est invalide.</summary>
    public const int UsageError = 2;

    /// <summary>Texte de <c>rgl --help</c>.</summary>
    private const string Usage =
        """
        Usage : rgl <commande> [options]

        Commandes :
          init [--web]              Crée un projet ; --web ajoute un serveur HTTP et une page Paper
          setup                     Installe Zig (et OpenSSL) à côté de rgl, puis propose d'ajouter rgl au PATH
          compile <fichier.rgl>     Compile le fichier .rgl indiqué
          exec<fichier.rgl>        Compile puis lance le fichier .rgl indiqué
          check [fichier.rgl]       Bilan de l'intégration C++ du projet : ce que les liaisons des
                                    bibliothèques C n'ont pas converti, et que le code utilise (E019)
          descriptor <en-tête> --class <Classe>
                                    Écrit le squelette d'un fichier forge: descriptor pour une classe C++
          cache --size              Taille du cache de RGL (Ko, Mo ou Go)
          cache clear               Vide le cache de RGL (tout se recalcule au build suivant)
          doc build <fichier.rgl>   Écrit la référence de l'API (out/docs/api.json et api.xml)
          doc check <fichier.rgl>   Vérifie les commentaires de documentation (///)
          lsp                       Serveur de langage pour les éditeurs (entrée et sortie standard)

        Options de compile et exec :
          -d <dossier>              Prend le fichier .rgl du dossier (sans ses sous-dossiers)
          --emit-cpp                S'arrête après la transpilation : le C++ est écrit dans le cache
          --emit-asm                S'arrête après l'assembleur du C++ généré, écrit dans le cache
                                    (avec les optimisations du profil : voir --release)
          --explain-optimizations   S'arrête après la transpilation et dit quelles optimisations ont été faites, ou refusées et pourquoi
          --count-allocations       Le programme écrit, en s'arrêtant, ses allocations et ses copies profondes (dossier out/<profil>-compteurs)
          --explain-vectorization   Compile en release et dit quelles boucles Clang vectorise, et pourquoi pas les autres
          --debug                   Build de debug (le profil par défaut)
          --release=size            Build de release le plus petit possible (-Oz), au prix de la vitesse
          --profile <nom>           Un profil écrit dans la clé « profiles » de rgl.json
          --release                 Build de release : le plus rapide, puis le plus léger
                                    (par défaut : build de debug, avec le plus d'informations pour déboguer)
          -- <arguments>            Avec exec : arguments transmis au programme

        Options de setup :
          --force                   Réinstalle même si la bonne version est déjà là
          --add-to-path             Ajoute rgl au PATH sans poser la question
          --no-path                 Ne propose pas d'ajouter rgl au PATH

        Options de doc (avec -d <dossier> comme compile) :
          --strict                  Avec doc check : un avertissement de documentation fait échouer

        Options de descriptor :
          --name <Nom>              Le nom du type côté RGL (par défaut, le dernier mot de la classe)
          -o <fichier>              Écrit le squelette dans ce fichier (jamais écrasé) au lieu de la sortie standard
          -I <dossier>              Un dossier où chercher ce que l'en-tête inclut (peut se répéter)

        Options de check (avec -d <dossier> comme compile) :
          --all                     Liste aussi les symboles non convertis que rien n'utilise
          --strict                  Un symbole utilisé et non converti fait échouer

        Autres :
          -h, --help                Affiche cette aide
          --version                 Affiche la version du compilateur
        """;

    /// <summary>
    /// Analyse les arguments, exécute la commande, puis affiche les diagnostics sur la sortie d'erreur.
    /// </summary>
    /// <param name="args">Arguments de la ligne de commande, sans le nom du programme.</param>
    /// <param name="context">Sorties, dossier courant et racine du cache.</param>
    /// <returns>Code de sortie du processus ; avec <c>exec</c>, celui du programme lancé.</returns>
    public static int Run(IReadOnlyList<string> args, CliContext context)
    {
        var diagnostics = new DiagnosticBag();
        var commandLine = CommandLineParser.Parse(args, diagnostics);
        if (commandLine is null)
        {
            Report(diagnostics, context);
            context.Error.WriteLine("« rgl --help » pour l'aide.");
            return UsageError;
        }

        var exitCode = commandLine.Command switch
        {
            CommandKind.Help => PrintUsage(context),
            CommandKind.Version => PrintVersion(context),
            CommandKind.Init => Init(commandLine, context, diagnostics),
            CommandKind.Check => CheckCommand.Run(commandLine, context, diagnostics),
            CommandKind.Cache => CacheCommand.Run(commandLine, context, diagnostics),
            CommandKind.Descriptor => DescriptorCommand.Run(commandLine, context, diagnostics),
            CommandKind.Doc => DocCommand.Run(commandLine, context, diagnostics),
            CommandKind.Format => FormatCommand.Run(commandLine, context, diagnostics),
            CommandKind.Precast => PrecastCommand.Run(commandLine, context, diagnostics),
            CommandKind.Setup => SetupCommand.Run(commandLine, context, diagnostics),
            CommandKind.LanguageServer => LanguageServer.Run(context.Input ?? Stream.Null, context.Output ?? Stream.Null),
            CommandKind.Compile or CommandKind.Exec => Build(commandLine, context, diagnostics),
            _ => throw new UnreachableException(),
        };

        Report(diagnostics, context);
        return exitCode;
    }

    /// <summary><c>rgl --help</c> : affiche l'aide.</summary>
    private static int PrintUsage(CliContext context)
    {
        context.Out.WriteLine(Usage);
        return Success;
    }

    /// <summary><c>rgl --version</c> : affiche la version du compilateur et celle du langage visé.</summary>
    private static int PrintVersion(CliContext context)
    {
        var version = typeof(CliRunner).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        // Le SDK ajoute « +<commit> » à la version informative.
        context.Out.WriteLine($"rgl {version.Split('+')[0]} (langage RGL {ProjectManifest.SupportedLanguageVersion})");
        return Success;
    }

    /// <summary><c>rgl init</c> : crée le projet dans le dossier courant et liste les fichiers créés.</summary>
    private static int Init(CommandLine commandLine, CliContext context, DiagnosticBag diagnostics)
    {
        var created = ProjectInitializer.Init(context.WorkingDirectory, diagnostics, commandLine.Web);
        if (created is null)
            return Failure;

        context.Out.WriteLine("Projet créé :");
        foreach (var path in created)
            context.Out.WriteLine($"  {path}");
        return Success;
    }

    /// <summary>
    /// <c>rgl compile</c> et <c>rgl exec</c> : charge le projet, lance le build, puis avec <c>exec</c>
    /// exécute le binaire. Avec <c>--emit-cpp</c>, s'arrête après l'écriture du C++. Les diagnostics
    /// du build (erreurs, avertissements, notices) s'affichent dès sa fin : avant le message final,
    /// et avant ce qu'écrit le programme lancé par <c>exec</c>.
    /// </summary>
    private static int Build(CommandLine commandLine, CliContext context, DiagnosticBag diagnostics)
    {
        var build = new DiagnosticBag();
        var project = RglProject.Load(commandLine.File, commandLine.Directory, context.WorkingDirectory, build);
        var profile = _ResolveProfile(commandLine, project?.Manifest, build);
        if (commandLine.CountAllocations) profile = profile.WithCounters();
        var output = commandLine.ExplainOptimizations ? BuildOutput.Optimizations : commandLine.ExplainVectorization ? BuildOutput.Vectorization : commandLine.EmitAsm ? BuildOutput.Assembly : commandLine.EmitCpp ? BuildOutput.Cpp : BuildOutput.Executable;
        var result = project is null ? null : BuildPipeline.Run(project, context.CacheRoot, output, profile, context.Toolchain, build);

        // Un build écrit dans le cache : s'il prend 2 % du disque ou plus, l'utilisateur en est prévenu (W067).
        CacheCommand.Warn(context.CacheRoot, context.DiskSize, build);
        Report(build, context);
        if (result is null)
            return Failure;

        if (output == BuildOutput.Optimizations)
        {
            context.Out.WriteLine("Analyse des optimisations terminée");
            return Success;
        }

        if (output == BuildOutput.Vectorization)
        {
            context.Out.WriteLine("Analyse de la vectorisation terminée");
            return Success;
        }

        if (result.Assembly is not null)
        {
            context.Out.WriteLine($"Assembleur écrit dans {result.Assembly}");
            return Success;
        }

        if (result.Executable is null)
        {
            context.Out.WriteLine($"C++ écrit dans {result.CppDirectory}");
            return Success;
        }

        if (commandLine.Command == CommandKind.Exec)
            return Execute(result.Executable, commandLine.ProgramArgs, context, diagnostics);

        context.Out.WriteLine($"Exécutable écrit dans {Path.GetRelativePath(context.WorkingDirectory, result.Executable)}");
        return Success;
    }

    /// <summary>
    /// Le profil demandé : celui d'<c>--debug</c>, <c>--release</c>, <c>--release=size</c> ou <c>--profile nom</c> (un profil fourni, sinon un profil du
    /// manifeste ; un nom inconnu est une erreur, le build est alors annulé avec le profil par défaut). Sans option : <c>debug</c>, et <c>release</c>
    /// pour <c>--explain-vectorization</c>, qui n'a de sens qu'optimisé.
    /// </summary>
    private static BuildProfile _ResolveProfile(CommandLine commandLine, ProjectManifest? manifest, DiagnosticBag diagnostics)
    {
        if (commandLine.Profile is not { } name) return commandLine.ExplainVectorization ? BuildProfile.Release : BuildProfile.Debug;
        if (BuildProfile.BuiltIn(name) is { } builtIn) return builtIn;
        if (manifest?.Profiles is { } profiles && profiles.TryGetValue(name, out var custom)) return custom;

        var known = manifest?.Profiles is { Count: > 0 } declared ? string.Join(", ", declared.Keys.Prepend("size").Prepend("release").Prepend("debug")) : "debug, release, size";
        diagnostics.Error($"profil « {name} » inconnu (profils : {known})");
        return BuildProfile.Debug;
    }

    /// <summary>Lance le programme compilé sur la console courante et propage son code de sortie.</summary>
    private static int Execute(string executable, IReadOnlyList<string> args, CliContext context, DiagnosticBag diagnostics)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = context.WorkingDirectory,
        };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(startInfo)!;
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Win32Exception e)
        {
            diagnostics.Error($"impossible de lancer le programme : {e.Message}", executable);
            return Failure;
        }
    }

    /// <summary>Écrit les diagnostics accumulés sur la sortie d'erreur, avec extrait du source et couleurs.</summary>
    private static void Report(DiagnosticBag diagnostics, CliContext context) =>
        new DiagnosticRenderer(context.WorkingDirectory, context.Color).Write(context.Error, diagnostics.Items);
}
