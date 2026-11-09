using System.Reflection;
using Rgl.Build;

namespace Rgl.Project;

/// <summary>
/// La bibliothèque standard : les <c>.forge</c> du dossier <c>std/</c> du dépôt, embarqués dans l'assembly. Le chargeur
/// de fichiers les lit comme un dossier ordinaire, rangé dans le cache sous l'identité de l'assembly : elle est écrite une
/// fois par version du compilateur.
/// </summary>
internal static class StdLibrary
{
    /// <summary>Préfixe du nom des ressources embarquées ; le chemin relatif du fichier suit, avec des « / ».</summary>
    public const string ResourcePrefix = "Rgl.Std/";

    /// <summary>Combien de temps une vérification complète de la std sur disque reste valable : la revérifier à chaque accès coûtait plus que tout le reste d'une analyse.</summary>
    private const long VerificationLifetime = 1500;

    private static readonly object _lock = new();
    private static string? _directory;
    private static long _verifiedAt = long.MinValue;
    private static string? _prefix;

    /// <summary>Le dossier qui contient la std sur disque, créé au premier appel ; <see langword="null"/> si elle ne peut pas être écrite.</summary>
    public static string? Directory
    {
        get
        {
            lock (_lock)
            {
                if (_directory is not null && Environment.TickCount64 - _verifiedAt < VerificationLifetime) return _directory;

                // Revérifiée au plus toutes les VerificationLifetime ms : une std vidée pendant la session est retrouvée, sans relire ses ~100 fichiers à chaque accès.
                if (_directory is null || !IsAvailable(_directory)) _directory = _Extract();
                _verifiedAt = Environment.TickCount64;
                _prefix = null;
                return _directory;
            }
        }
    }

    /// <summary>Comme <see cref="IsAvailable"/> pour la std du cache, mais une vérification récente vaut pour les suivantes (le serveur de langage la demande à chaque message).</summary>
    public static bool IsAvailableCached()
    {
        lock (_lock)
        {
            if (_directory is not null && Environment.TickCount64 - _verifiedAt < VerificationLifetime) return true;
        }
        var available = IsAvailable();
        if (available)
        {
            lock (_lock)
            {
                _directory ??= CacheDirectory;
                _verifiedAt = Environment.TickCount64;
            }
        }
        return available;
    }

    /// <summary>Vérifie le cache sans l'extraire, y compris après un vidage pendant une session.</summary>
    public static bool IsAvailable(string? directory = null)
    {
        var target = directory ?? CacheDirectory;
        return File.Exists(Path.Combine(target, ".complete"))
            && typeof(StdLibrary).Assembly.GetManifestResourceNames()
                .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
                .All(n => File.Exists(Path.Combine(target, n[ResourcePrefix.Length..].Replace('/', Path.DirectorySeparatorChar))));
    }

    public static string CacheDirectory => Path.Combine(Cache.DefaultRoot(), "std",
        typeof(StdLibrary).Assembly.ManifestModule.ModuleVersionId.ToString("N"));

    /// <summary>Restaure les fichiers embarqués ; aucun téléchargement n'est nécessaire.</summary>
    public static bool Restore()
    {
        lock (_lock)
        {
            _directory = _Extract();
            _verifiedAt = Environment.TickCount64;
            _prefix = null;
            return _directory is not null && IsAvailable(_directory);
        }
    }

    /// <summary>
    /// Le fichier <c>Errors.forge</c> de la std, lu à part : le compilateur y prend ses types d'erreur (<c>Exception</c>, <c>CastError</c>,
    /// <c>LoopError</c>), donc tout programme le lie, qu'il charge la std ou non. <see langword="null"/> si la std n'est pas disponible.
    /// </summary>
    public static Binding.SourceModule? ErrorsModule(Diagnostics.DiagnosticBag diagnostics)
    {
        if (Directory is not { } std || Text.SourceText.FromPath(Path.Combine(std, "Errors.forge"), diagnostics) is not { } source) return null;

        var before = diagnostics.Items.Count(d => d.Level == Diagnostics.DiagnosticLevel.Error);
        var tokens = new Syntax.Lexer(source, diagnostics).Tokenize();
        var unit = new Parsing.Parser(tokens, diagnostics).Parse();
        FileChecks.Check(unit, false, diagnostics);
        return diagnostics.Items.Count(d => d.Level == Diagnostics.DiagnosticLevel.Error) > before ? null : new Binding.SourceModule("std.Errors", unit, false);
    }

    /// <summary>Vrai si <paramref name="path"/> est un fichier de la std embarquée.</summary>
    public static bool Contains(string path)
    {
        if (!Path.IsPathFullyQualified(path) || Directory is not { } std) return false;
        try
        {
            string prefix;
            lock (_lock) prefix = _prefix ??= Path.TrimEndingDirectorySeparator(Path.GetFullPath(std)) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static string? _Extract()
    {
        var assembly = typeof(StdLibrary).Assembly;
        var names = assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)).ToArray();
        if (names.Length == 0) return null;

        try
        {
            var target = CacheDirectory;
            var done = Path.Combine(target, ".complete");
            if (IsAvailable(target)) return target;

            foreach (var name in names)
            {
                var path = Path.Combine(target, name[ResourcePrefix.Length..].Replace('/', Path.DirectorySeparatorChar));
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var input = assembly.GetManifestResourceStream(name)!;
                using var output = File.Create(path);
                input.CopyTo(output);
            }
            File.WriteAllText(done, "");
            return target;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
