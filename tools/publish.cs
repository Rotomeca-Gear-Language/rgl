// publish.cs — construit l'exécutable distribuable de rgl (alias de dotnet publish).
//
// Usage (depuis le dossier tools/, comme fetch-tools.cs) :
//     dotnet run publish.cs                    exécutable pour ce système, dans dist/
//     dotnet run publish.cs -- --rid linux-x64 l'exécutable d'un autre système (win-x64, linux-x64, linux-arm64…)
//     dotnet run publish.cs -- -o dossier      un autre dossier de sortie
//
// Équivaut à : dotnet publish rgl.csproj -c Release -r <rid> --self-contained -p:PublishSingleFile=true -o dist
// Compatible Windows et Linux. Nécessite .NET 10.

using System.Diagnostics;
using System.Runtime.InteropServices;

string? rid = null;
string? output = null;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--rid" when i + 1 < args.Length: rid = args[++i]; break;
        case "-o" when i + 1 < args.Length: output = args[++i]; break;
        default:
            Console.Error.WriteLine($"Erreur : argument inconnu « {args[i]} » (options : --rid <rid>, -o <dossier>).");
            return 2;
    }
}

rid ??= HostRid();

string? root = FindRoot(Directory.GetCurrentDirectory()) ?? FindRoot(AppContext.BaseDirectory);
if (root is null)
{
    Console.Error.WriteLine("Erreur : rgl.csproj introuvable depuis le dossier courant.");
    return 1;
}

output = output is null ? Path.Combine(root, "dist") : Path.GetFullPath(output);
var info = new ProcessStartInfo("dotnet") { WorkingDirectory = root, UseShellExecute = false };
foreach (string a in new[] { "publish", "rgl.csproj", "-c", "Release", "-r", rid, "--self-contained", "-p:PublishSingleFile=true", "-o", output })
    info.ArgumentList.Add(a);

Console.WriteLine($"Publication de rgl pour {rid} dans {output}");
using Process process = Process.Start(info)!;
process.WaitForExit();
return process.ExitCode;

static string HostRid()
{
    string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "osx" : throw new PlatformNotSupportedException();
    string arch = RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        var other => throw new PlatformNotSupportedException($"Architecture {other}"),
    };
    return $"{os}-{arch}";
}

static string? FindRoot(string start)
{
    for (DirectoryInfo? dir = new(start); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "rgl.csproj"))) return dir.FullName;
    return null;
}
