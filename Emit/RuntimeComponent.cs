namespace Rgl.Emit;

/// <summary>
/// Une partie du runtime C++ (<c>runtime/</c>). L'émetteur note celles que le programme utilise : le
/// C++ généré n'inclut que leurs en-têtes, et la chaîne de compilation ne compile et ne lie qu'elles,
/// avec leurs dépendances. Un programme qui n'utilise ni chaîne ni argument n'embarque rien du runtime.
/// </summary>
internal enum RuntimeComponent
{
    /// <summary>Allocateur et arrêt sur mémoire insuffisante (<c>memory.hpp</c>, <c>memory.cpp</c>).</summary>
    Memory,

    /// <summary><c>string</c> et <c>mstring</c> (<c>string.hpp</c>, <c>string.cpp</c>) ; dépend de <see cref="Memory"/>.</summary>
    String,

    /// <summary>
    /// Lecture des arguments de la ligne de commande en UTF-8 (<c>arguments.hpp</c>,
    /// <c>arguments.cpp</c>), pour un train d'entrée qui déclare <c>[string] args</c> ; dépend de <see cref="String"/>.
    /// </summary>
    Arguments,

    /// <summary><c>symbol.cpp</c> : la table des symboles, pour <c>symbol::intern</c>. Les littéraux <c>§nom</c> n'en ont pas besoin.</summary>
    Symbol,

    /// <summary>Lecture et écriture de fichiers entiers, chemins en UTF-8 (<c>file.hpp</c>, <c>file.cpp</c>) ; dépend de <see cref="String"/>.</summary>
    File,

    /// <summary>L'analyseur JSON (<c>json.hpp</c>, <c>json.cpp</c>) ; dépend de <see cref="String"/> et de <see cref="File"/>.</summary>
    Json,

    /// <summary>L'analyseur et l'écrivain XML (<c>xml.hpp</c>, <c>xml.cpp</c>) ; dépend de <see cref="String"/> et de <see cref="File"/>.</summary>
    Xml,

    /// <summary>Les processus enfants (<c>process.hpp</c>, <c>process.cpp</c>) ; dépend de <see cref="String"/>.</summary>
    Process,

    /// <summary>Les sockets TCP et UDP bloquants avec délai (<c>net.hpp</c>, <c>net.cpp</c>) ; dépend de <see cref="String"/>.</summary>
    Net,

    /// <summary>Serveur/client HTTP/1.1 concurrent et WebSocket (<c>http.hpp</c>, <c>http.cpp</c>) ; dépend des chaînes et fonctions RGL.</summary>
    Http,

    /// <summary>Transport HTTPS OpenSSL (<c>http_tls.hpp</c>, <c>http_tls.cpp</c>) ; dépend du transport HTTP.</summary>
    HttpTls,

    /// <summary>
    /// L'appel protégé de code C++ qui peut lancer une exception (<c>native.hpp</c>, <c>native.cpp</c>). Le seul fichier du runtime compilé <b>avec</b> les
    /// exceptions : il attrape ce que lance le C++ d'une liaison et le rend en statut, pour que le reste du programme n'en voie jamais.
    /// </summary>
    Native,

    /// <summary>Les processus enfants qui restent ouverts : entrée écrite, sorties lues au fil de l'eau (<c>child.hpp</c>, <c>child.cpp</c>) ; dépend de <see cref="String"/>.</summary>
    Child,

    /// <summary>La normalisation Unicode, NFC, NFD, NFKC et NFKD (<c>unicode.hpp</c>, <c>unicode.cpp</c>, tables générées dans <c>unicode_tables.hpp</c>) ; dépend de <see cref="String"/>.</summary>
    Unicode,

    /// <summary>Le terminal (saisie sans écho, touches), les signaux et l'observation de fichiers (<c>system.hpp</c>, <c>system.cpp</c>) ; dépend de <see cref="String"/> et de <see cref="Console"/>.</summary>
    System,

    /// <summary>Écriture et lecture de la console en UTF-8 (<c>console.hpp</c>, <c>console.cpp</c>) ; dépend de <see cref="String"/>.</summary>
    Console,
}

/// <summary>Ce que l'émetteur produit : le C++ du programme et les parties du runtime qu'il utilise.</summary>
/// <param name="Code">Le C++ généré, avec son <c>main()</c>.</param>
/// <param name="Components">
/// Les parties du runtime que le code inclut directement ; leurs dépendances (par exemple
/// <see cref="RuntimeComponent.Memory"/> pour <see cref="RuntimeComponent.String"/>) sont ajoutées par la chaîne de compilation.
/// </param>
/// <param name="Natives">Les bibliothèques C que le code inclut : leurs dossiers d'en-têtes et leurs archives sont donnés à la chaîne de compilation.</param>
/// <param name="NativeUnits">Les unités C++ des composants écrits en C++ : chacune se compile à part, avec les exceptions.</param>
internal sealed record CppProgram(string Code, IReadOnlySet<RuntimeComponent> Components, IReadOnlyList<Native.CLibrary>? Natives = null, IReadOnlyList<NativeUnit>? NativeUnits = null,
    IReadOnlyList<OptimizationNote>? Notes = null);

/// <summary>
/// Ce que le compilateur a fait, ou n'a pas pu faire, pour rendre le programme plus rapide : une optimisation appliquée (deux parcours en une boucle,
/// un accumulateur déplacé, une récursion terminale écrite en boucle…) ou refusée, avec ses raisons. Rendu en diagnostics par <c>--explain-optimizations</c>.
/// </summary>
/// <param name="Span">L'endroit du source.</param>
/// <param name="Refused">Vrai quand l'optimisation a été écartée.</param>
/// <param name="Message">Ce qui a été fait ou refusé.</param>
/// <param name="Reasons">Pourquoi, pour une optimisation refusée.</param>
internal sealed record OptimizationNote(Rgl.Text.SourceSpan Span, bool Refused, string Message, IReadOnlyList<string>? Reasons = null);

/// <summary>L'unité C++ d'un composant écrit en C++ (<c>nom.rgl.cpp</c>) : à compiler avec les exceptions, puis à lier au programme.</summary>
/// <param name="Name">Le nom du composant, celui du guard et du fichier <c>.rgl.cpp</c>.</param>
/// <param name="Code">Le C++ de l'unité : le contexte, la déclaration de la fonction à définir, le fichier de l'utilisateur inclus tel quel, l'appel.</param>
/// <param name="SourcePath">Le chemin complet du fichier <c>.rgl.cpp</c> de l'utilisateur (pour un descriptor : de son en-tête).</param>
/// <param name="IncludeDirectories">Les dossiers où l'unité cherche ce que ses en-têtes incluent, en plus de celui du fichier de l'utilisateur.</param>
internal sealed record NativeUnit(string Name, string Code, string SourcePath, IReadOnlyList<string>? IncludeDirectories = null);
