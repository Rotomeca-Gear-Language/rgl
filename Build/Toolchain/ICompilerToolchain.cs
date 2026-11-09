using Rgl.Emit;
using Rgl.Native;

namespace Rgl.Build.Toolchain;

/// <summary>
/// Chaîne de compilation C++ : transforme le C++ généré, avec le runtime, en exécutable. Le reste
/// du compilateur ne connaît qu'elle, jamais Zig ni Clang directement : on peut ainsi la remplacer
/// (autre compilateur, autre cible) ou la simuler dans les tests.
/// </summary>
internal interface ICompilerToolchain
{
    /// <summary>Nom affiché dans les messages, par exemple <c>zig c++ 0.17.0</c>.</summary>
    string Name { get; }

    /// <summary>La cible dans la syntaxe de Zig, par exemple <c>x86_64-windows-gnu</c> : la taille de <c>long</c> en dépend.</summary>
    string Target { get; }

    /// <summary>
    /// Compile <paramref name="mainCpp"/> et le runtime en <paramref name="executable"/>. Ne lève
    /// pas d'exception pour un échec de compilation : il est décrit dans le résultat.
    /// </summary>
    /// <param name="mainCpp">Le C++ généré par l'émetteur.</param>
    /// <param name="executable">Chemin de l'exécutable à produire.</param>
    /// <param name="profile">Debug (informations pour déboguer) ou release (vitesse, puis taille).</param>
    /// <param name="components">
    /// Parties du runtime utilisées par le programme : seules elles, et celles dont elles dépendent,
    /// sont compilées et liées.
    /// </param>
    /// <param name="icon">Icône Windows (<c>.ico</c>) à lier à l'exécutable ; ignorée pour les autres systèmes.</param>
    /// <param name="natives">Les bibliothèques C que le programme appelle : leurs dossiers d'en-têtes et leurs archives statiques.</param>
    /// <returns>Le succès ou l'échec, avec la sortie complète du compilateur.</returns>
    ToolchainResult BuildExecutable(string mainCpp, string executable, BuildProfile profile, IReadOnlySet<RuntimeComponent> components, string? icon, IReadOnlyList<CLibrary>? natives = null);

    /// <summary>
    /// Compile <paramref name="mainCpp"/> en assembleur, avec les optimisations de
    /// <paramref name="profile"/>, pour voir le code que le processeur exécutera. Rien n'est lié :
    /// le code du runtime défini hors des en-têtes n'y apparaît que par ses appels.
    /// </summary>
    /// <param name="mainCpp">Le C++ généré par l'émetteur.</param>
    /// <param name="assembly">Chemin du fichier assembleur à produire.</param>
    /// <param name="profile">Le profil dont on veut voir le code.</param>
    /// <param name="natives">Les bibliothèques C que le programme appelle : leurs dossiers d'en-têtes.</param>
    /// <returns>Le succès ou l'échec, avec la sortie complète du compilateur.</returns>
    ToolchainResult EmitAssembly(string mainCpp, string assembly, BuildProfile profile, IReadOnlyList<CLibrary>? natives = null);

    /// <summary>
    /// Compile <paramref name="mainCpp"/> avec les optimisations de <paramref name="profile"/> en demandant à Clang ses remarques sur la
    /// vectorisation des boucles (<c>-Rpass=loop-vectorize</c>), sans rien lier. Les remarques portent sur les positions du fichier RGL
    /// (les directives <c>#line</c>) : <see cref="Rgl.Build.VectorizationReport"/> les relit.
    /// </summary>
    /// <returns>Le succès ou l'échec, avec la sortie complète du compilateur.</returns>
    ToolchainResult ExplainVectorization(string mainCpp, BuildProfile profile, IReadOnlyList<CLibrary>? natives = null) =>
        new(false, "cette chaîne de compilation ne sait pas expliquer la vectorisation");

    /// <summary>
    /// Lit un en-tête C avec Clang et envoie son arbre syntaxique en JSON à <paramref name="read"/>, au fil de l'eau : pour un en-tête
    /// qui en inclut d'autres, l'arbre fait des dizaines de Mo, et rien ne doit le garder en mémoire.
    /// </summary>
    /// <param name="header">Le chemin complet de l'en-tête.</param>
    /// <param name="includeDirectories">Les dossiers où chercher les en-têtes qu'il inclut.</param>
    /// <param name="read">Lit le flux, jusqu'à sa fin ou non ; le reste est jeté.</param>
    /// <returns>Le succès ou l'échec ; la sortie d'erreur de Clang, dans le journal du résultat, dit pourquoi l'en-tête ne se lit pas.</returns>
    /// <param name="cpp">Vrai pour lire l'en-tête en C++ (les classes d'un descriptor) ; faux pour le lire en C.</param>
    ToolchainResult DumpHeaderAst(string header, IReadOnlyList<string> includeDirectories, Action<Stream> read, bool cpp = false);

    /// <summary>
    /// Préprocesse un en-tête C en gardant ses <c>#define</c> (<c>-E -dD</c>) et envoie le texte à <paramref name="read"/> : l'arbre syntaxique ne voit pas les macros.
    /// </summary>
    /// <param name="header">Le chemin complet de l'en-tête.</param>
    /// <param name="includeDirectories">Les dossiers où chercher les en-têtes qu'il inclut.</param>
    /// <param name="read">Lit le flux, jusqu'à sa fin ou non ; le reste est jeté.</param>
    ToolchainResult DumpHeaderMacros(string header, IReadOnlyList<string> includeDirectories, Action<Stream> read);

    /// <summary>
    /// Liste les fichiers dont dépend un en-tête C : lui-même et ce qu'il inclut, hors en-têtes du système (<c>-MM</c>), au format d'un fichier Makefile.
    /// Sert à reconnaître qu'un en-tête n'a pas changé : un en-tête inclus qui change change les liaisons.
    /// </summary>
    /// <param name="header">Le chemin complet de l'en-tête.</param>
    /// <param name="includeDirectories">Les dossiers où chercher les en-têtes qu'il inclut.</param>
    /// <param name="read">Lit le flux.</param>
    ToolchainResult ListHeaderDependencies(string header, IReadOnlyList<string> includeDirectories, Action<Stream> read);

    /// <summary>
    /// Compile l'unité C++ d'un composant écrit en C++ (<c>nom.rgl.cpp</c>) en un objet, <b>avec les exceptions</b> et le RTTI : c'est du code écrit à la main, qui peut
    /// en lancer et s'en servir. Le programme, lui, reste compilé sans. L'échec de cette compilation est une erreur du fichier de l'utilisateur, pas un bug du transpileur.
    /// </summary>
    /// <param name="source">Le chemin de l'unité C++ engendrée.</param>
    /// <param name="objectFile">Le chemin de l'objet à produire.</param>
    /// <param name="profile">Debug ou release.</param>
    /// <param name="includeDirectories">Les dossiers où le fichier de l'utilisateur cherche ce qu'il inclut : le sien d'abord.</param>
    ToolchainResult CompileNativeUnit(string source, string objectFile, BuildProfile profile, IReadOnlyList<string> includeDirectories);
}

/// <summary>Résultat d'un appel à la chaîne de compilation.</summary>
/// <param name="Success">Vrai si l'exécutable a été produit.</param>
/// <param name="Log">Commandes lancées et sortie complète du compilateur, pour le diagnostic d'un échec.</param>
internal sealed record ToolchainResult(bool Success, string Log);
