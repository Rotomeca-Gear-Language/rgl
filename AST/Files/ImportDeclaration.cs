using Rgl.AST.Interfaces;
using Rgl.AST.Train;
using Rgl.Text;

namespace Rgl.AST.Files;

/// <summary>D'où un import prend son fichier.</summary>
internal enum ImportKind
{
    /// <summary><c>import Commandes;</c> : un fichier cherché par son nom (dans la box du fichier qui importe, s'il est dans une box).</summary>
    Plain,

    /// <summary><c>import [..] Commandes;</c> : un fichier cherché par son nom hors de la box du fichier qui importe.</summary>
    Outside,

    /// <summary><c>import box Database;</c> : l'index de la box <c>Database.box</c>.</summary>
    Box,

    /// <summary><c>import tools Vectors;</c> (les noms de tout le dossier <c>Vectors.tools</c>) ou <c>import tools Vectors.Vec2;</c> (un fichier de ce dossier).</summary>
    Tools,

    /// <summary><c>use &lt;Math&gt;;</c> : un fichier ou une box de la bibliothèque standard, cherché par son nom ; l'utilisateur n'a pas à savoir lequel des deux c'est.</summary>
    Std,

    /// <summary><c>import json settings;</c> : le fichier <c>settings.json</c>, cherché par son nom, lu à la compilation et donné en constante <c>settings</c>.</summary>
    Json,

    /// <summary><c>import xml layout;</c> : le fichier <c>layout.xml</c>, cherché par son nom, lu à la compilation et donné en constante <c>layout</c> (un <c>XmlNode</c>).</summary>
    Xml,

    /// <summary><c>import file "page.html" as page;</c> : le premier fichier de ce nom dans la portée, lu comme une constante string.</summary>
    File,

    /// <summary><c>import [..] file "page.html" as page;</c> : le fichier est cherché hors de la box courante.</summary>
    FileOutside,
}

/// <summary>
/// Une constante qui vient d'un fichier de données importé (<c>import json</c>, <c>import xml</c>, <c>import file</c>) : le chargeur de fichiers remplace l'import par elle,
/// une déclaration <c>var</c> écrite à partir du contenu du fichier, que le binder lie comme un precast (donnée résolue à la compilation, écrite en
/// C++ seulement si le programme la lit).
/// </summary>
/// <param name="Span">L'import qu'elle remplace.</param>
/// <param name="Declaration">La constante, dans un texte source engendré dont le chemin est celui du fichier de données.</param>
internal sealed record DataConstant(SourceSpan Span, Variables.VarDeclaration Declaration) : IStatement;

/// <summary>
/// Un import :<c>import Commandes;</c> (le train par défaut, et tout le reste en accès qualifié) ou
/// <c>import Commandes only {ajouter as add, retirer};</c> (des noms choisis, en accès direct), avec ses variantes
/// <c>import [..] f;</c>, <c>import box B;</c>, <c>import tools T;</c> et <c>as</c> pour renommer le fichier.
/// </summary>
/// <param name="Span">De <c>import</c> au <c>;</c>.</param>
/// <param name="Module">Le nom du fichier importé, sans extension ; <c>Dossier.fichier</c> pour <c>import tools Dossier.fichier</c>.</param>
/// <param name="Only">Les noms choisis ; <see langword="null"/> sans <c>only</c>.</param>
/// <param name="Kind">D'où vient le fichier.</param>
/// <param name="Alias">Le nom sous lequel le fichier est visible ici ; <see langword="null"/> s'il garde le sien.</param>
internal sealed record ImportDeclaration(SourceSpan Span, Identifier Module, IReadOnlyList<ImportName>? Only, ImportKind Kind = ImportKind.Plain, Identifier? Alias = null) : IStatement
{
    /// <summary>Le nom sous lequel le fichier importé est visible dans le fichier qui l'importe.</summary>
    public string LocalModuleName => Alias?.Name ?? Module.Name;
}

/// <summary>Un nom choisi par <c>only</c> : <c>ajouter</c> ou <c>ajouter as add</c>.</summary>
/// <param name="Span">Du nom à son alias.</param>
/// <param name="Name">Le nom dans le fichier importé.</param>
/// <param name="Alias">Le nom sous lequel il est visible ici ; <see langword="null"/> s'il garde le sien.</param>
internal sealed record ImportName(SourceSpan Span, Identifier Name, Identifier? Alias) : IAstNode
{
    /// <summary>Le nom sous lequel l'import est visible dans le fichier qui importe.</summary>
    public Identifier LocalName => Alias ?? Name;
}

/// <summary>Ce qu'un nom exporté désigne.</summary>
internal enum ExportKind
{
    /// <summary>Un nom : une déclaration du fichier, ou un nom importé dans l'index d'une box.</summary>
    Name,

    /// <summary><c>...Fichier</c> dans l'index d'une box : tous les noms exportés d'un fichier importé, à plat.</summary>
    Spread,

    /// <summary><c>export Fichier;</c> dans l'index d'une box : le fichier importé lui-même, dont les noms restent qualifiés.</summary>
    Module,
}

/// <summary>Un nom d'un export.</summary>
/// <param name="Span">Le nom, avec ses <c>...</c> pour un spread.</param>
/// <param name="Name">Le nom exporté, ou le fichier importé pour un spread et un module.</param>
/// <param name="Kind">Ce qu'il désigne.</param>
internal sealed record ExportName(SourceSpan Span, Identifier Name, ExportKind Kind = ExportKind.Name) : IAstNode;

/// <summary>
/// Ce qu'un fichier rend visible à ceux qui l'importent : <c>export {a, b, c};</c>, <c>export gear f() { … }</c> (devant
/// un gear, un guard, un emit, un mold, une error, un enum, un ingot, une constante ou un train nommé), et, dans l'index
/// d'une box, <c>export {...Fichier, a};</c> et <c>export Fichier;</c>. Un fichier sans aucun export exporte tout.
/// C'est une déclaration, pas une valeur.
/// </summary>
/// <param name="Span">De <c>export</c> à la fin de ce qu'il exporte.</param>
/// <param name="Names">Les noms exportés.</param>
/// <param name="IsInline">Vrai pour <c>export</c> écrit devant une déclaration : l'export ne porte que son nom.</param>
internal sealed record ExportDeclaration(SourceSpan Span, IReadOnlyList<ExportName> Names, bool IsInline = false) : IStatement;
