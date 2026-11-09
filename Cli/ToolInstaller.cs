using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Rgl.Cli;

/// <summary>Erreur attendue de l'installation des outils, présentée telle quelle à l'utilisateur.</summary>
internal sealed class SetupException(string message) : Exception(message);

/// <summary>
/// Installe la chaîne de compilation C++ (Zig) et, sous Windows x86_64, OpenSSL dans le dossier <c>tools/</c> à côté de l'exécutable.
/// Les versions viennent de <c>tools/toolchain.json</c>, embarqué dans le compilateur ; c'est la même installation que fait
/// <c>tools/fetch-tools.cs</c> dans le dépôt, mais disponible dans l'exécutable distribué.
/// </summary>
internal sealed class ToolInstaller(string toolsDirectory, TextWriter output)
{
    private const string IndexUrl = "https://ziglang.org/download/index.json";

    private string _ZigDirectory => Path.Combine(toolsDirectory, "zig");

    private string _ZigExecutable => Path.Combine(_ZigDirectory, OperatingSystem.IsWindows() ? "zig.exe" : "zig");

    /// <summary>Lit la configuration des outils embarquée dans le compilateur.</summary>
    public static ToolchainConfig LoadConfig()
    {
        using var stream = typeof(ToolInstaller).Assembly.GetManifestResourceStream("Rgl.Toolchain.json")
            ?? throw new SetupException("la configuration des outils (toolchain.json) n'est pas embarquée dans ce compilateur");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (!root.TryGetProperty("zig", out var zig) || zig.GetString() is not { Length: > 0 } version)
            throw new SetupException("toolchain.json doit contenir une clé « zig » avec la version voulue");

        OpenSslConfig? openSsl = null;
        if (root.TryGetProperty("openssl", out var node))
        {
            string Text(string key) => node.TryGetProperty(key, out var e) && e.GetString() is { } value
                ? value
                : throw new SetupException($"toolchain.json : « openssl » doit avoir une clé « {key} »");
            openSsl = new OpenSslConfig(Text("version"), Text("url"), Text("sha256"));
        }
        return new ToolchainConfig(version, openSsl);
    }

    /// <summary>Installe Zig puis OpenSSL ; ce qui est déjà à la bonne version est laissé tel quel sauf avec <paramref name="force"/>.</summary>
    public async Task InstallAsync(ToolchainConfig config, bool force)
    {
        Directory.CreateDirectory(toolsDirectory);
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("rgl-setup/1.0");

        if (!force && await _IsZigInstalledAsync(config.ZigVersion))
            output.WriteLine($"Zig {config.ZigVersion} est déjà installé dans {_ZigDirectory}.");
        else
        {
            var platform = ZigPlatform.Current();
            output.WriteLine($"Plateforme : {platform.Key} — version voulue : {config.ZigVersion}");
            var archive = await _FindArchiveAsync(http, config.ZigVersion, platform);
            await _InstallZigAsync(http, archive);
            var zig = await _RunAsync(_ZigExecutable, ["version"]) ?? "inconnue";
            var clang = (await _RunAsync(_ZigExecutable, ["c++", "--version"]) ?? "inconnue").Split('\n')[0];
            output.WriteLine($"Installé : Zig {zig.Trim()} — {clang.Trim()}");
        }

        if (config.OpenSsl is { } openSsl && OperatingSystem.IsWindows() && RuntimeInformation.OSArchitecture == Architecture.X64)
            await _InstallOpenSslAsync(http, openSsl, force);
    }

    private async Task<bool> _IsZigInstalledAsync(string version)
    {
        if (!File.Exists(_ZigExecutable)) return false;
        return (await _RunAsync(_ZigExecutable, ["version"]))?.Trim() == version;
    }

    private static async Task<ZigArchive> _FindArchiveAsync(HttpClient http, string version, ZigPlatform platform)
    {
        using var index = JsonDocument.Parse(await http.GetStringAsync(IndexUrl));
        if (!index.RootElement.TryGetProperty(version, out var release))
            throw new SetupException($"la version {version} de Zig n'existe pas dans {IndexUrl} (les versions de développement en disparaissent)");

        foreach (var key in platform.IndexKeys)
        {
            if (!release.TryGetProperty(key, out var entry)) continue;
            var url = entry.GetProperty("tarball").GetString() ?? throw new SetupException("adresse d'archive absente de l'index de Zig");
            var sha = entry.GetProperty("shasum").GetString() ?? throw new SetupException("empreinte absente de l'index de Zig");
            var size = long.TryParse(entry.GetProperty("size").GetString(), out var s) ? s : 0;
            return new ZigArchive(new Uri(url), sha, size);
        }
        throw new SetupException($"Zig {version} n'est pas publié pour {platform.Key}");
    }

    private async Task _InstallZigAsync(HttpClient http, ZigArchive archive)
    {
        var work = Path.Combine(toolsDirectory, $".zig-install-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        try
        {
            var archivePath = Path.Combine(work, Path.GetFileName(archive.Url.LocalPath));
            await _DownloadAsync(http, archive.Url, archive.Sha256, archivePath, archive.Size);

            var extract = Path.Combine(work, "extract");
            Directory.CreateDirectory(extract);
            output.WriteLine("Décompression…");
            if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                ZipFile.ExtractToDirectory(archivePath, extract);
            else if (await _RunAsync("tar", ["-xJf", archivePath, "-C", extract]) is null)
                throw new SetupException("la décompression a échoué : vérifie que « tar » et la prise en charge de xz sont installés (paquet xz-utils)");

            var tops = Directory.GetDirectories(extract);
            if (tops.Length != 1) throw new SetupException("archive de Zig inattendue : elle devrait contenir un seul dossier racine");
            _ReplaceZig(tops[0]);
        }
        finally
        {
            _DeleteQuietly(work);
        }
    }

    /// <summary>Remplace tools/zig sans jamais laisser d'installation à moitié faite.</summary>
    private void _ReplaceZig(string newDirectory)
    {
        var old = _ZigDirectory + ".old";
        _DeleteQuietly(old);
        if (Directory.Exists(_ZigDirectory)) Directory.Move(_ZigDirectory, old);
        try
        {
            Directory.Move(newDirectory, _ZigDirectory);
        }
        catch
        {
            if (Directory.Exists(old)) Directory.Move(old, _ZigDirectory);
            throw;
        }
        _DeleteQuietly(old);
        _DeleteQuietly(Path.Combine(_ZigDirectory, "doc"));
    }

    private async Task _InstallOpenSslAsync(HttpClient http, OpenSslConfig config, bool force)
    {
        var directory = Path.Combine(toolsDirectory, "openssl");
        var versionFile = Path.Combine(directory, "version.txt");
        if (!force && File.Exists(versionFile) && File.ReadAllText(versionFile).Trim() == config.Version)
        {
            output.WriteLine($"OpenSSL {config.Version} est déjà installé dans {directory}.");
            return;
        }

        var work = Path.Combine(toolsDirectory, $".openssl-install-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        try
        {
            var url = new Uri(config.Url);
            var archive = Path.Combine(work, Path.GetFileName(url.LocalPath));
            await _DownloadAsync(http, url, config.Sha256, archive, 0);

            // Le tar de Windows (bsdtar) sait lire .zst ; celui de Git non.
            var extract = Path.Combine(work, "extract");
            Directory.CreateDirectory(extract);
            var tar = Path.Combine(Environment.SystemDirectory, "tar.exe");
            if (await _RunAsync(tar, ["-xf", archive, "-C", extract]) is null)
                throw new SetupException("la décompression d'OpenSSL a échoué (le tar.exe de Windows est requis pour le format zstd)");

            var root = Path.Combine(extract, "mingw64");
            if (!Directory.Exists(Path.Combine(root, "include", "openssl")))
                throw new SetupException("paquet OpenSSL inattendu : dossier include/openssl absent");

            _DeleteQuietly(directory);
            Directory.CreateDirectory(directory);
            foreach (var part in new[] { "include", "lib", "bin" })
                Directory.Move(Path.Combine(root, part), Path.Combine(directory, part));
            // Les bibliothèques statiques passeraient avant celles d'import (-lssl) et lieraient la CRT msvcrt de MSYS2.
            foreach (var name in new[] { "libssl.a", "libcrypto.a" })
                File.Delete(Path.Combine(directory, "lib", name));
            File.WriteAllText(versionFile, config.Version);
            output.WriteLine($"Installé : OpenSSL {config.Version} dans {directory}");
        }
        finally
        {
            _DeleteQuietly(work);
        }
    }

    private async Task _DownloadAsync(HttpClient http, Uri url, string expectedSha256, string destination, long expectedSize)
    {
        output.WriteLine($"Téléchargement de {url}");
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? expectedSize;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var source = await response.Content.ReadAsStreamAsync())
        await using (var target = File.Create(destination))
        {
            var buffer = new byte[1 << 16];
            long received = 0;
            var lastStep = -1;
            int read;
            while ((read = await source.ReadAsync(buffer)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read));
                hash.AppendData(buffer, 0, read);
                received += read;
                if (total > 0 && (int)(received * 100 / total) / 10 != lastStep)
                {
                    lastStep = (int)(received * 100 / total) / 10;
                    output.WriteLine($"  {lastStep * 10,3} %  ({received / 1_048_576} / {total / 1_048_576} Mio)");
                }
            }
        }

        var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new SetupException($"empreinte SHA-256 incorrecte : attendue {expectedSha256}, obtenue {actual} (archive rejetée)");
        output.WriteLine("Empreinte SHA-256 vérifiée.");
    }

    /// <summary>Lance un programme et rend sa sortie standard ; <see langword="null"/> s'il est introuvable ou échoue.</summary>
    private static async Task<string?> _RunAsync(string fileName, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(fileName) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(info) ?? throw new InvalidOperationException();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            return process.ExitCode == 0 ? stdout.Result : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static void _DeleteQuietly(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Nettoyage au mieux : un résidu n'empêche pas l'installation.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Versions des outils figées dans <c>tools/toolchain.json</c>.</summary>
internal sealed record ToolchainConfig(string ZigVersion, OpenSslConfig? OpenSsl);

/// <summary>Paquet OpenSSL figé dans <c>tools/toolchain.json</c>.</summary>
internal sealed record OpenSslConfig(string Version, string Url, string Sha256);

/// <summary>Archive de Zig à télécharger, telle que décrite par l'index officiel.</summary>
internal sealed record ZigArchive(Uri Url, string Sha256, long Size);

/// <summary>Système et architecture courants, dans les noms qu'utilise Zig.</summary>
internal sealed record ZigPlatform(string Arch, string Os)
{
    public string Key => $"{Arch}-{Os}";

    /// <summary>Clés possibles dans l'index : les anciennes versions de Zig nomment « i386 » l'architecture x86.</summary>
    public IEnumerable<string> IndexKeys => Arch == "x86" ? [Key, $"i386-{Os}"] : [Key];

    public static ZigPlatform Current()
    {
        var os = OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsLinux() ? "linux"
            : throw new SetupException("système non pris en charge : seuls Windows et Linux le sont");

        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x86_64",
            Architecture.Arm64 => "aarch64",
            Architecture.X86 => "x86",
            var other => throw new SetupException($"architecture non prise en charge : {other}"),
        };
        return new ZigPlatform(arch, os);
    }
}
