// fetch-tools.cs — installe la chaîne de compilation C++ (Zig) dans tools/zig.
//
// Usage (depuis n'importe quel dossier du dépôt) :
//     dotnet run fetch-tools.cs            installe la version de tools/toolchain.json
//     dotnet run fetch-tools.cs --force    réinstalle même si la bonne version est présente
//
// tools/toolchain.json :
//     { "zig": "0.15.2", "openssl": { "version": "…", "url": "…", "sha256": "…" } }
// La clé "openssl" est facultative : sous Windows x86_64 seulement, elle installe dans tools/openssl
// les en-têtes, bibliothèques et DLL d'OpenSSL (paquet MSYS2 mingw64, vérifié par SHA-256) dont le
// composant HTTPS a besoin. Ailleurs, OpenSSL vient du système.
//
// Compatible Windows et Linux (x86_64, aarch64, x86). Nécessite .NET 10.
// Sous Linux, la commande `tar` du système décompresse les archives .tar.xz.

using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

const string IndexUrl = "https://ziglang.org/download/index.json";

try
{
    bool force = args.Contains("--force");
    string toolsDir = ToolsLocator.FindToolsDirectory(Directory.GetCurrentDirectory());
    ToolchainConfig config = ToolchainConfig.Load(Path.Combine(toolsDir, "toolchain.json"));
    var installer = new ZigInstaller(toolsDir, config.ZigVersion);

    using var http = new HttpClient();
    http.DefaultRequestHeaders.UserAgent.ParseAdd("rgl-fetch-tools/1.0");

    if (!force && await installer.IsInstalledAsync())
        Console.WriteLine($"Zig {config.ZigVersion} est déjà installé dans {installer.ZigDir}.");
    else
    {
        ZigPlatform platform = ZigPlatform.Current();
        Console.WriteLine($"Plateforme : {platform.Key} — version voulue : {config.ZigVersion}");
        ZigArchive archive = await new ZigIndex(http, IndexUrl).FindArchiveAsync(config.ZigVersion, platform);
        await installer.InstallAsync(http, archive);
        await installer.PrintVersionsAsync();
    }

    if (config.OpenSsl is { } openSsl && OperatingSystem.IsWindows() && RuntimeInformation.OSArchitecture == Architecture.X64)
        await new OpenSslInstaller(toolsDir, openSsl).InstallAsync(http, force);
    return 0;
}
catch (ToolchainException ex)
{
    Console.Error.WriteLine($"Erreur : {ex.Message}");
    return 1;
}
catch (HttpRequestException ex)
{
    Console.Error.WriteLine($"Erreur réseau : {ex.Message}. Vérifie ta connexion à ziglang.org.");
    return 1;
}

/// <summary>Erreur attendue, présentée telle quelle à l'utilisateur.</summary>
sealed class ToolchainException(string message) : Exception(message);

/// <summary>Version de Zig figée dans tools/toolchain.json.</summary>
sealed record ToolchainConfig(string ZigVersion, OpenSslConfig? OpenSsl)
{
    public static ToolchainConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new ToolchainException($"Fichier introuvable : {path}. Crée-le avec par exemple {{ \"zig\": \"0.15.2\" }}.");

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("zig", out JsonElement zig) || zig.GetString() is not { Length: > 0 } version)
            throw new ToolchainException($"{path} doit contenir une clé \"zig\" avec la version voulue.");

        OpenSslConfig? openSsl = null;
        if (doc.RootElement.TryGetProperty("openssl", out JsonElement node))
        {
            string? Text(string key) => node.TryGetProperty(key, out JsonElement e) ? e.GetString() : null;
            openSsl = new OpenSslConfig(
                Text("version") ?? throw new ToolchainException($"{path} : \"openssl\" doit avoir une clé \"version\"."),
                Text("url") ?? throw new ToolchainException($"{path} : \"openssl\" doit avoir une clé \"url\"."),
                Text("sha256") ?? throw new ToolchainException($"{path} : \"openssl\" doit avoir une clé \"sha256\"."));
        }
        return new ToolchainConfig(version, openSsl);
    }
}

/// <summary>Paquet OpenSSL figé dans tools/toolchain.json.</summary>
sealed record OpenSslConfig(string Version, string Url, string Sha256);

/// <summary>Installe OpenSSL (en-têtes, bibliothèques d'import, DLL) dans tools/openssl.</summary>
sealed class OpenSslInstaller(string toolsDir, OpenSslConfig config)
{
    private string Dir => Path.Combine(toolsDir, "openssl");
    private string VersionFile => Path.Combine(Dir, "version.txt");

    public async Task InstallAsync(HttpClient http, bool force)
    {
        if (!force && File.Exists(VersionFile) && File.ReadAllText(VersionFile).Trim() == config.Version)
        {
            Console.WriteLine($"OpenSSL {config.Version} est déjà installé dans {Dir}.");
            return;
        }

        string work = Path.Combine(toolsDir, $".openssl-install-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        try
        {
            string archive = Path.Combine(work, Path.GetFileName(new Uri(config.Url).LocalPath));
            Console.WriteLine($"Téléchargement de {config.Url}");
            await using (Stream source = await http.GetStreamAsync(config.Url))
            await using (FileStream target = File.Create(archive))
                await source.CopyToAsync(target);

            string actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(archive)));
            if (!string.Equals(actual, config.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new ToolchainException($"Empreinte SHA-256 d'OpenSSL incorrecte : attendue {config.Sha256}, obtenue {actual}. Archive rejetée.");
            Console.WriteLine("Empreinte SHA-256 vérifiée.");

            // Le tar de Windows (bsdtar) sait lire .zst ; celui de Git non.
            string extract = Path.Combine(work, "extract");
            Directory.CreateDirectory(extract);
            string tar = Path.Combine(Environment.SystemDirectory, "tar.exe");
            if (await ProcessRunner.TryRunAsync(tar, ["-xf", archive, "-C", extract]) is null)
                throw new ToolchainException("La décompression d'OpenSSL a échoué (tar.exe de Windows requis pour le format zstd).");

            string root = Path.Combine(extract, "mingw64");
            if (!Directory.Exists(Path.Combine(root, "include", "openssl")))
                throw new ToolchainException("Paquet OpenSSL inattendu : dossier include/openssl absent.");

            if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true);
            Directory.CreateDirectory(Dir);
            foreach (string part in new[] { "include", "lib", "bin" })
                Directory.Move(Path.Combine(root, part), Path.Combine(Dir, part));
            // Les bibliothèques statiques passeraient avant celles d'import (-lssl) et lieraient la CRT msvcrt de MSYS2.
            foreach (string name in new[] { "libssl.a", "libcrypto.a" })
                File.Delete(Path.Combine(Dir, "lib", name));
            File.WriteAllText(VersionFile, config.Version);
            Console.WriteLine($"Installé : OpenSSL {config.Version} dans {Dir}");
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

/// <summary>Trouve le dossier tools/ en remontant depuis le dossier courant.</summary>
static class ToolsLocator
{
    public static string FindToolsDirectory(string startDir)
    {
        for (DirectoryInfo? dir = new(startDir); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "tools");
            if (File.Exists(Path.Combine(candidate, "toolchain.json")))
                return candidate;
        }
        throw new ToolchainException("Aucun dossier tools/ contenant toolchain.json trouvé depuis le dossier courant.");
    }
}

/// <summary>Système et architecture courants, traduits dans les noms utilisés par Zig.</summary>
sealed record ZigPlatform(string Arch, string Os)
{
    public string Key => $"{Arch}-{Os}";

    /// <summary>Clés possibles dans index.json : les anciennes versions de Zig nomment « i386 » l'architecture x86.</summary>
    public IEnumerable<string> IndexKeys => Arch == "x86" ? [Key, $"i386-{Os}"] : [Key];

    public bool IsWindows => Os == "windows";

    public static ZigPlatform Current()
    {
        string os = OperatingSystem.IsWindows() ? "windows"
                  : OperatingSystem.IsLinux() ? "linux"
                  : throw new ToolchainException("Système non pris en charge : seuls Windows et Linux le sont.");

        string arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x86_64",
            Architecture.Arm64 => "aarch64",
            Architecture.X86 => "x86",
            var other => throw new ToolchainException($"Architecture non prise en charge : {other}.")
        };
        return new ZigPlatform(arch, os);
    }
}

/// <summary>Archive à télécharger, telle que décrite dans index.json.</summary>
sealed record ZigArchive(Uri Url, string Sha256, long Size);

/// <summary>Lecture de l'index officiel des versions de Zig.</summary>
sealed class ZigIndex(HttpClient http, string indexUrl)
{
    public async Task<ZigArchive> FindArchiveAsync(string version, ZigPlatform platform)
    {
        using JsonDocument index = JsonDocument.Parse(await http.GetStringAsync(indexUrl));

        if (!index.RootElement.TryGetProperty(version, out JsonElement release))
            throw new ToolchainException(
                $"La version {version} n'existe pas dans {indexUrl}. Les versions de développement en disparaissent : utilise une version stable.");

        foreach (string key in platform.IndexKeys)
        {
            if (!release.TryGetProperty(key, out JsonElement entry))
                continue;

            string url = entry.GetProperty("tarball").GetString() ?? throw new ToolchainException("Adresse d'archive absente de l'index.");
            string sha = entry.GetProperty("shasum").GetString() ?? throw new ToolchainException("Empreinte absente de l'index.");
            long size = long.TryParse(entry.GetProperty("size").GetString(), out long s) ? s : 0;
            return new ZigArchive(new Uri(url), sha, size);
        }
        throw new ToolchainException($"Zig {version} n'est pas publié pour {platform.Key}.");
    }
}

/// <summary>Téléchargement, vérification, décompression et remplacement de tools/zig.</summary>
sealed class ZigInstaller(string toolsDir, string version)
{
    public string ZigDir { get; } = Path.Combine(toolsDir, "zig");

    private string ZigExe => Path.Combine(ZigDir, OperatingSystem.IsWindows() ? "zig.exe" : "zig");

    public async Task<bool> IsInstalledAsync()
    {
        if (!File.Exists(ZigExe))
            return false;
        string? installed = await ProcessRunner.TryRunAsync(ZigExe, ["version"]);
        return installed?.Trim() == version;
    }

    public async Task InstallAsync(HttpClient http, ZigArchive archive)
    {
        string workDir = Path.Combine(toolsDir, $".zig-install-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            string archivePath = Path.Combine(workDir, Path.GetFileName(archive.Url.LocalPath));
            await DownloadAndVerifyAsync(http, archive, archivePath);

            string extractDir = Path.Combine(workDir, "extract");
            await ExtractAsync(archivePath, extractDir);
            ReplaceInstalledZig(SingleTopDirectory(extractDir));
        }
        finally
        {
            DeleteDirectoryQuietly(workDir);
        }
    }

    public async Task PrintVersionsAsync()
    {
        string zig = await ProcessRunner.TryRunAsync(ZigExe, ["version"]) ?? "inconnue";
        string clang = (await ProcessRunner.TryRunAsync(ZigExe, ["c++", "--version"]) ?? "inconnue").Split('\n')[0];
        Console.WriteLine($"Installé : Zig {zig.Trim()} — {clang.Trim()}");
    }

    private static async Task DownloadAndVerifyAsync(HttpClient http, ZigArchive archive, string destination)
    {
        Console.WriteLine($"Téléchargement de {archive.Url}");
        using HttpResponseMessage response = await http.GetAsync(archive.Url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        long total = response.Content.Headers.ContentLength ?? archive.Size;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (Stream source = await response.Content.ReadAsStreamAsync())
        await using (FileStream target = File.Create(destination))
        {
            var buffer = new byte[1 << 16];
            long received = 0;
            int lastPercent = -1;
            int read;
            while ((read = await source.ReadAsync(buffer)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read));
                hash.AppendData(buffer, 0, read);
                received += read;
                lastPercent = ReportProgress(received, total, lastPercent);
            }
        }
        Console.WriteLine();

        string actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (!string.Equals(actual, archive.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new ToolchainException($"Empreinte SHA-256 incorrecte : attendue {archive.Sha256}, obtenue {actual}. Archive rejetée.");
        Console.WriteLine("Empreinte SHA-256 vérifiée.");
    }

    private static int ReportProgress(long received, long total, int lastPercent)
    {
        if (total <= 0)
            return lastPercent;
        int percent = (int)(received * 100 / total);
        if (percent / 5 != lastPercent / 5)
            Console.Write($"\r  {percent,3} %  ({received / 1_048_576} / {total / 1_048_576} Mio)");
        return percent;
    }

    private static async Task ExtractAsync(string archivePath, string destination)
    {
        Console.WriteLine("Décompression…");
        Directory.CreateDirectory(destination);
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archivePath, destination);
            return;
        }
        // .NET ne sait pas décompresser le format xz : on passe par la commande tar du système.
        string? output = await ProcessRunner.TryRunAsync("tar", ["-xJf", archivePath, "-C", destination]);
        if (output is null)
            throw new ToolchainException("La décompression a échoué. Vérifie que `tar` et la prise en charge de xz sont installés (paquet xz-utils).");
    }

    private static string SingleTopDirectory(string extractDir)
    {
        string[] dirs = Directory.GetDirectories(extractDir);
        return dirs.Length == 1
            ? dirs[0]
            : throw new ToolchainException("Archive inattendue : elle devrait contenir un seul dossier racine.");
    }

    /// <summary>Remplace tools/zig sans jamais laisser d'installation à moitié faite.</summary>
    private void ReplaceInstalledZig(string newZigDir)
    {
        string oldDir = ZigDir + ".old";
        DeleteDirectoryQuietly(oldDir);
        if (Directory.Exists(ZigDir))
            Directory.Move(ZigDir, oldDir);
        try
        {
            Directory.Move(newZigDir, ZigDir);
        }
        catch
        {
            if (Directory.Exists(oldDir))
                Directory.Move(oldDir, ZigDir);
            throw;
        }
        DeleteDirectoryQuietly(oldDir);
        DeleteDirectoryQuietly(Path.Combine(ZigDir, "doc"));
    }

    private static void DeleteDirectoryQuietly(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException) { /* nettoyage au mieux : un résidu n'empêche pas l'installation */ }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Lancement d'un programme externe, avec capture de sa sortie.</summary>
static class ProcessRunner
{
    /// <summary>Renvoie la sortie standard, ou null si le programme est introuvable ou échoue.</summary>
    public static async Task<string?> TryRunAsync(string fileName, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string arg in arguments)
            info.ArgumentList.Add(arg);

        try
        {
            using Process process = Process.Start(info) ?? throw new InvalidOperationException();
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            return process.ExitCode == 0 ? stdout.Result : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}