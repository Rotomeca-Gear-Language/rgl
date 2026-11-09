using Rgl.Diagnostics;
using Rgl.Native;

namespace Rgl.Cli;

/// <summary>
/// <c>rgl descriptor en-tête --class Classe</c> : lit l'en-tête C++ avec Clang et écrit le squelette d'un fichier <c>forge: descriptor</c> de la classe (voir
/// <see cref="DescriptorSkeleton"/>). Le squelette est un point de départ : il n'est jamais régénéré par-dessus un fichier existant.
/// </summary>
internal static class DescriptorCommand
{
    public static int Run(CommandLine commandLine, CliContext context, DiagnosticBag diagnostics)
    {
        var header = Path.GetFullPath(commandLine.File!, context.WorkingDirectory);
        if (!File.Exists(header))
        {
            diagnostics.Error($"en-tête « {commandLine.File} » introuvable");
            return CliRunner.Failure;
        }
        if (context.Toolchain is null)
        {
            diagnostics.Add(new Diagnostic(DiagnosticLevel.Error, "la chaîne de compilation est introuvable : l'en-tête C++ ne peut pas être lu", Code: DiagnosticCodes.UnreadableHeader,
                Help: ["lance : rgl setup"]));
            return CliRunner.Failure;
        }

        var rglName = commandLine.Name ?? commandLine.Class!.Split("::")[^1];
        if (!CBindings._IsName(rglName))
        {
            diagnostics.Error($"« {rglName} » ne peut pas être un nom de type RGL (mot réservé ou caractère invalide)", null, null);
            return CliRunner.Failure;
        }

        // Le dossier de l'en-tête d'abord, puis les -I : ce que l'en-tête inclut près de lui se trouve toujours.
        List<string> includes = [Path.GetDirectoryName(header)!, .. commandLine.Includes.Select(i => Path.GetFullPath(i, context.WorkingDirectory))];
        CHeader? read = null;
        var dumped = context.Toolchain.DumpHeaderAst(header, includes, stream => read = ClangAstReader.Read(stream, header), cpp: true);
        if (!dumped.Success || read is null)
        {
            // L'en-tête est celui de l'utilisateur : ce que Clang en dit est un diagnostic de son fichier, pas un bug du transpileur.
            var lines = dumped.Log.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => !l.StartsWith("$ ", StringComparison.Ordinal)).Take(8).Select(l => l.TrimEnd());
            diagnostics.Add(new Diagnostic(DiagnosticLevel.Error, $"l'en-tête « {Path.GetFileName(header)} » ne se lit pas", header, DiagnosticCodes.UnreadableHeader, Help: [.. lines]));
            return CliRunner.Failure;
        }

        var cppClass = read.Classes?.FirstOrDefault(c => c.QualifiedName == commandLine.Class);
        if (cppClass is null)
        {
            var found = read.Classes?.Select(c => c.QualifiedName).ToArray() ?? [];
            diagnostics.Add(new Diagnostic(DiagnosticLevel.Error, read.ClassTemplates?.Contains(commandLine.Class!) == true
                    ? $"« {commandLine.Class} » est un patron de classe : les descriptors génériques ne sont pas encore pris en charge"
                    : $"la classe « {commandLine.Class} » n'est pas définie dans {Path.GetFileName(header)}", header,
                Help: [found.Length == 0 ? "l'en-tête ne définit aucune classe" : $"classes définies : {string.Join(", ", found)}", "écris la classe avec ses espaces de noms : demo::Counter"]));
            return CliRunner.Failure;
        }

        string? outputPath = commandLine.Output is null ? null : Path.GetFullPath(commandLine.Output, context.WorkingDirectory);
        if (outputPath is not null && File.Exists(outputPath))
        {
            diagnostics.Error($"« {commandLine.Output} » existe déjà : le squelette ne l'écrase pas", outputPath);
            return CliRunner.Failure;
        }

        // Le chemin de header: est relatif au fichier engendré, ou au dossier courant sur la sortie standard.
        var baseDirectory = outputPath is null ? context.WorkingDirectory : Path.GetDirectoryName(outputPath)!;
        var text = DescriptorSkeleton.Generate(cppClass, rglName, Path.GetRelativePath(baseDirectory, header), context.Toolchain.Target, read.Aliases);
        if (outputPath is null)
        {
            context.Out.Write(text);
            return CliRunner.Success;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, text, new System.Text.UTF8Encoding(false));
        context.Out.WriteLine($"Squelette écrit dans {Path.GetRelativePath(context.WorkingDirectory, outputPath)} : relis-le et complète-le");
        return CliRunner.Success;
    }
}
