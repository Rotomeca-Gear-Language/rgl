using System.Net.Http;
using Rgl.Diagnostics;

namespace Rgl.Cli;

/// <summary>
/// <c>rgl setup [--force] [--add-to-path | --no-path]</c> : installe Zig (et OpenSSL sous Windows) dans <c>tools/</c> du cache de RGL (que <c>rgl cache clear</c> laisse en place),
/// puis propose d'ajouter le dossier de cet exécutable au PATH de l'utilisateur pour taper <c>rgl</c> de partout.
/// </summary>
internal static class SetupCommand
{
    public static int Run(CommandLine commandLine, CliContext context, DiagnosticBag diagnostics)
    {
        try
        {
            var installer = new ToolInstaller(Build.Toolchain.ZigToolchain.ToolsDirectory(context.CacheRoot), context.Out);
            installer.InstallAsync(ToolInstaller.LoadConfig(), commandLine.Force).GetAwaiter().GetResult();
        }
        catch (SetupException e)
        {
            diagnostics.Error(e.Message);
            return CliRunner.Failure;
        }
        catch (HttpRequestException e)
        {
            diagnostics.Error($"erreur réseau : {e.Message} (vérifie ta connexion à ziglang.org)");
            return CliRunner.Failure;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            diagnostics.Error($"installation impossible dans « {Build.Toolchain.ZigToolchain.ToolsDirectory(context.CacheRoot)} » : {e.Message}");
            return CliRunner.Failure;
        }

        if (!commandLine.NoPath)
            _OfferPath(ExecutableDirectory(), commandLine.AddToPath, context, diagnostics);
        return CliRunner.Success;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetConsoleProcessList(uint[] processList, uint count);

    /// <summary>
    /// Vrai quand <c>rgl</c> a été ouvert d'un double-clic : sous Windows, la console n'appartient qu'à ce processus (un terminal
    /// où l'on tape la commande en partage une avec son interpréteur) et rien n'est redirigé.
    /// </summary>
    internal static bool LaunchedByDoubleClick()
    {
        if (!OperatingSystem.IsWindows() || Console.IsInputRedirected || Console.IsOutputRedirected) return false;
        try
        {
            return GetConsoleProcessList(new uint[2], 2) == 1;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Le dossier de l'exécutable <c>rgl</c> en cours ; celui du compilateur quand il est lancé par <c>dotnet rgl.dll</c>.</summary>
    internal static string ExecutableDirectory()
    {
        var process = Environment.ProcessPath;
        var name = Path.GetFileNameWithoutExtension(process);
        return Path.TrimEndingDirectorySeparator(
            process is not null && string.Equals(name, "rgl", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(process)!
                : AppContext.BaseDirectory);
    }

    /// <summary>Le dossier est-il déjà dans le PATH (processus ou utilisateur) ?</summary>
    internal static bool IsOnPath(string directory)
    {
        var current = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (OperatingSystem.IsWindows())
            current += ";" + (Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return current.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(p => string.Equals(Path.TrimEndingDirectorySeparator(p), directory, comparison));
    }

    private static void _OfferPath(string directory, bool assumeYes, CliContext context, DiagnosticBag diagnostics)
    {
        if (IsOnPath(directory))
        {
            context.Out.WriteLine($"PATH : {directory} y est déjà, « rgl » est utilisable partout.");
            return;
        }

        if (!assumeYes)
        {
            if (Console.IsInputRedirected)
            {
                context.Out.WriteLine($"PATH : pour utiliser « rgl » partout, ajoute {directory} au PATH (ou relance « rgl setup --add-to-path »).");
                return;
            }
            context.Out.Write($"Ajouter {directory} au PATH pour pouvoir taper « rgl » depuis n'importe où ? [O/n] ");
            var answer = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
            if (answer is not ("" or "o" or "oui" or "y" or "yes"))
            {
                context.Out.WriteLine("PATH : inchangé.");
                return;
            }
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                var current = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
                Environment.SetEnvironmentVariable("Path", current.Length == 0 ? directory : current.TrimEnd(';') + ";" + directory, EnvironmentVariableTarget.User);
                context.Out.WriteLine($"PATH : {directory} ajouté au PATH de l'utilisateur. Ouvre un nouveau terminal pour en profiter.");
            }
            else
            {
                var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".profile");
                File.AppendAllText(profile, $"{Environment.NewLine}# RGL{Environment.NewLine}export PATH=\"$PATH:{directory}\"{Environment.NewLine}");
                context.Out.WriteLine($"PATH : ligne ajoutée à {profile}. Ouvre un nouveau terminal (ou « source ~/.profile ») pour en profiter.");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            diagnostics.Warning($"le PATH n'a pas pu être modifié ({e.Message}) : ajoute {directory} à la main");
        }
    }
}
