using System.Runtime.InteropServices;

namespace Rgl.Cli;

/// <summary>Décide si la sortie d'erreur peut être colorée avec des séquences ANSI.</summary>
internal static class TerminalColors
{
    /// <summary>
    /// Vrai si la sortie d'erreur est un vrai terminal qui comprend les couleurs : pas de
    /// redirection vers un fichier, pas de <c>NO_COLOR</c> (convention <c>no-color.org</c>), pas
    /// de <c>TERM=dumb</c>. Sous Windows, active aussi le mode de la console qui interprète les
    /// séquences ANSI ; si la console le refuse, pas de couleur.
    /// </summary>
    public static bool Enable()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"))) return false;
        if (Environment.GetEnvironmentVariable("TERM") == "dumb") return false;
        if (Console.IsErrorRedirected) return false;

        return !OperatingSystem.IsWindows() || _EnableWindowsVirtualTerminal();
    }

    private const int StdErrorHandle = -12;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    /// <summary>Active l'interprétation des séquences ANSI sur la sortie d'erreur de la console Windows.</summary>
    private static bool _EnableWindowsVirtualTerminal()
    {
        var handle = _GetStdHandle(StdErrorHandle);
        if (!_GetConsoleMode(handle, out var mode)) return false;

        return (mode & EnableVirtualTerminalProcessing) != 0
            || _SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
    }

    [DllImport("kernel32.dll", EntryPoint = "GetStdHandle")]
    private static extern nint _GetStdHandle(int handle);

    [DllImport("kernel32.dll", EntryPoint = "GetConsoleMode")]
    private static extern bool _GetConsoleMode(nint handle, out uint mode);

    [DllImport("kernel32.dll", EntryPoint = "SetConsoleMode")]
    private static extern bool _SetConsoleMode(nint handle, uint mode);
}
