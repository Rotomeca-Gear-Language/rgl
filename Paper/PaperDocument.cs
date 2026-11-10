using Rgl.Text;

namespace Rgl.Paper;

/// <summary>Résultat de la compilation d'un fichier <c>.paper</c> vers un module RGL.</summary>
/// <param name="Source">Source RGL engendré.</param>
/// <param name="Fragments">Fragments dynamiques indépendants, dans l'ordre du document.</param>
internal sealed record PaperDocument(SourceText Source, IReadOnlyList<PaperFragment> Fragments, IReadOnlyList<PaperReactive> Reactives, IReadOnlyList<PaperHandler> Handlers);

/// <summary>Une interpolation Paper compilée séparément pour pouvoir être recalculée seule.</summary>
/// <param name="Id">Identifiant stable dans le fichier.</param>
/// <param name="Expression">Expression écrite entre <c>@@</c>.</param>
/// <param name="RawHtml">Vrai pour <c>html(...)</c>, donc sans échappement.</param>
/// <param name="ImportedFile">Fichier incorporé par une expression <c>import file</c>, le cas échéant.</param>
/// <param name="ImportedContent">Contenu UTF-8 strict du fichier incorporé.</param>
/// <param name="Trackable">Vrai si le navigateur peut entourer le fragment de bornes HTML invisibles.</param>
internal sealed record PaperFragment(int Id, string Expression, bool RawHtml, string? ImportedFile = null, string? ImportedContent = null, IReadOnlyList<string>? Dependencies = null, bool Trackable = true)
{
    public IReadOnlyList<string> Dependencies { get; init; } = Dependencies ?? [];
}

/// <summary>État réactif privé à une instance de page.</summary>
/// <param name="Initializer">Valeur initiale ; vide pour un paramètre <c>param</c> sans valeur par défaut.</param>
/// <param name="IsParam">Vrai pour un <c>param</c> : donné par l'appelant de <c>mount</c>, en lecture seule dans la page.</param>
internal sealed record PaperReactive(string Type, string Name, string Initializer, bool Mutable, bool IsParam = false);

/// <summary>Liaison d'un événement HTML vers un gear Paper vérifié.</summary>
internal sealed record PaperHandler(string Event, string Gear, bool ReceivesEvent, IReadOnlyList<int>? Invalidates = null)
{
    public IReadOnlyList<int> Invalidates { get; init; } = Invalidates ?? [];
}
