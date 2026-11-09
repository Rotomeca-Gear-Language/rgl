using System.Globalization;
using Rgl.Diagnostics;

namespace Rgl.Cli;

/// <summary>
/// <c>rgl cache --size</c> et <c>rgl cache clear</c> : la taille du cache de RGL, et son vidage. Le cache ne contient que ce qu'un build sait refaire
/// (C++ généré, objets du runtime, caches de Zig, liaisons des en-têtes C) : le vider ne coûte que le temps de tout recompiler.
/// </summary>
internal static class CacheCommand
{
    /// <summary>Part du disque à partir de laquelle le cache est signalé (W067).</summary>
    public const double WarningShare = 0.02;

    /// <summary>La taille, en octets, de tous les fichiers du cache ; 0 s'il n'existe pas.</summary>
    public static long Size(string cacheRoot)
    {
        if (!Directory.Exists(cacheRoot)) return 0;

        long total = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var tools = Path.Combine(Path.GetFullPath(cacheRoot), "tools") + Path.DirectorySeparatorChar;
        foreach (var file in new DirectoryInfo(cacheRoot).EnumerateFiles("*", options))
        {
            // Zig et OpenSSL (rgl setup) ne sont pas du cache : ni comptés ni vidés.
            if (file.FullName.StartsWith(tools, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                total += file.Length;
            }
            catch (IOException)
            {
                // Un fichier supprimé pendant le parcours ne compte plus.
            }
        }
        return total;
    }

    /// <summary>La taille écrite avec l'unité qui la rend la plus lisible : Ko, Mo ou Go (1 Ko = 1024 octets), un chiffre après la virgule.</summary>
    public static string Format(long bytes)
    {
        var french = CultureInfo.GetCultureInfo("fr-FR");
        return bytes switch
        {
            < 1024 => $"{bytes.ToString(french)} o",
            < 1024 * 1024 => $"{(bytes / 1024.0).ToString("0.#", french)} Ko",
            < 1024L * 1024 * 1024 => $"{(bytes / (1024.0 * 1024)).ToString("0.#", french)} Mo",
            _ => $"{(bytes / (1024.0 * 1024 * 1024)).ToString("0.#", french)} Go",
        };
    }

    /// <summary>La taille du disque qui porte le cache ; <see langword="null"/> si elle ne se lit pas.</summary>
    public static long? DiskSize(string cacheRoot)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(cacheRoot));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).TotalSize;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Signale (W067) un cache qui prend au moins 2 % du disque. <paramref name="diskSize"/> remplace la lecture du disque, pour les tests.
    /// </summary>
    /// <returns>La taille du cache.</returns>
    public static long Warn(string cacheRoot, long? diskSize, DiagnosticBag diagnostics)
    {
        var size = Size(cacheRoot);
        var disk = diskSize ?? DiskSize(cacheRoot) ?? 0;
        if (disk <= 0) return size;

        var share = (double)size / disk;
        if (share >= WarningShare)
            diagnostics.Warning($"le cache de RGL prend {Format(size)}, soit {(share * 100).ToString("0.#", CultureInfo.GetCultureInfo("fr-FR"))} % du disque", cacheRoot, DiagnosticCodes.CacheLarge);
        return size;
    }

    /// <summary><c>rgl cache --size</c> ou <c>rgl cache clear</c>.</summary>
    public static int Run(CommandLine commandLine, CliContext context, DiagnosticBag diagnostics)
    {
        if (commandLine.Cache == CacheAction.Clear) return Clear(context, diagnostics);

        var size = Warn(context.CacheRoot, context.DiskSize, diagnostics);
        context.Out.WriteLine($"Cache : {Format(size)} ({context.CacheRoot})");
        return CliRunner.Success;
    }

    private static int Clear(CliContext context, DiagnosticBag diagnostics)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(context.CacheRoot));
        var home = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        // Un cache réglé par erreur sur la racine d'un disque ou sur le dossier personnel ne doit jamais être vidé : tout ce qu'il contient disparaîtrait.
        if (root == Path.TrimEndingDirectorySeparator(Path.GetPathRoot(root) ?? root) || string.Equals(root, home, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Error($"le dossier du cache ne se vide pas : « {root} » n'est pas un dossier dédié à RGL", root);
            return CliRunner.Failure;
        }
        if (!Directory.Exists(root))
        {
            context.Out.WriteLine("Le cache est déjà vide.");
            return CliRunner.Success;
        }

        var before = Size(root);
        var failed = 0;
        foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos())
        {
            if (entry is DirectoryInfo && entry.Name == "tools") continue;
            try
            {
                if (entry is DirectoryInfo directory) directory.Delete(recursive: true);
                else entry.Delete();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Un fichier que Zig ou un programme lancé tient encore : le reste est vidé, et la commande le dit.
                diagnostics.Error($"« {entry.Name} » n'a pas pu être supprimé : {e.Message}", entry.FullName);
                ++failed;
            }
        }

        var freed = before - Size(root);
        context.Out.WriteLine($"Cache vidé : {Format(freed)} libérés ({root})");
        return failed == 0 ? CliRunner.Success : CliRunner.Failure;
    }
}
