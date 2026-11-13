using System.Diagnostics;

namespace Rgl.Cli;

/// <summary>
/// Le seul accès de <c>rgl</c> à <c>git</c> : <c>rgl publish</c> en a besoin pour lire l'état du dépôt, créer le tag d'une version et le pousser.
/// Injecté par <see cref="CliContext"/> pour que les tests ne touchent à aucun dépôt.
/// </summary>
internal interface IGitRunner
{
    /// <summary>Lance <c>git</c> avec ces arguments dans ce dossier.</summary>
    /// <returns>Son code de sortie, ce qu'il a écrit sur la sortie standard, puis sur la sortie d'erreur ; <see langword="null"/> si <c>git</c> n'a pas pu être lancé.</returns>
    (int Exit, string Output, string Error)? Run(string workingDirectory, params string[] arguments);
}

/// <summary>Lance le <c>git</c> du système.</summary>
internal sealed class ProcessGitRunner : IGitRunner
{
    /// <inheritdoc/>
    public (int Exit, string Output, string Error)? Run(string workingDirectory, params string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // Aucune question : une identification demandée ferait attendre la commande indéfiniment.
            Environment = { ["GIT_TERMINAL_PROMPT"] = "0" },
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(info);
            if (process is null) return null;
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, output.Trim(), error.Result.Trim());
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
