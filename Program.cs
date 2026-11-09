using System.Text;
using Rgl.Build;
using Rgl.Build.Toolchain;
using Rgl.Cli;

// Sous Windows, la console utilise sinon la page de code OEM et casse les accents des messages.
Console.OutputEncoding = new UTF8Encoding(false);

// Ouvert d'un double-clic, sans rien à faire : on installe ce qu'il faut (rgl setup), puis on garde la fenêtre ouverte pour lire le résultat.
var launchedByClick = args.Length == 0 && SetupCommand.LaunchedByDoubleClick();
if (launchedByClick) args = ["setup"];

var cacheRoot = Cache.DefaultRoot();
var exitCode = CliRunner.Run(args, new CliContext(Console.Out, Console.Error, Environment.CurrentDirectory, cacheRoot, TerminalColors.Enable(), ZigToolchain.Locate(cacheRoot),
    Console.OpenStandardInput(), Console.OpenStandardOutput()));

if (launchedByClick)
{
    Console.WriteLine();
    Console.Write("Appuyez sur Entrée pour fermer cette fenêtre…");
    Console.ReadLine();
}
return exitCode;
