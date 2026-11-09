using Rgl.Text;

namespace Rgl.Diagnostics;

/// <summary>Gravité d'un diagnostic.</summary>
internal enum DiagnosticLevel
{
    /// <summary>Empêche la commande d'aboutir.</summary>
    Error,

    /// <summary>Signale un problème sans interrompre la commande.</summary>
    Warning,

    /// <summary>Information utile, sans problème à corriger.</summary>
    Info,
}

/// <summary>
/// Un message adressé à l'utilisateur : sa gravité, son code, l'endroit du source concerné, et
/// de quoi l'aider à corriger. <see cref="DiagnosticRenderer"/> l'affiche en détail dans le
/// terminal ; <see cref="Format"/> en donne une forme d'une ligne, stable, pour les tests.
/// </summary>
/// <param name="Level">Gravité du diagnostic.</param>
/// <param name="Message">Texte principal, en français.</param>
/// <param name="Path">Fichier concerné, s'il y en a un ; ignoré si <paramref name="Span"/> est fourni.</param>
/// <param name="Code">Code E/W du diagnostic, absent pour les erreurs d'outillage (CLI, manifeste).</param>
/// <param name="Span">Endroit du source concerné, s'il y en a un ; donne le fichier, la ligne et la colonne.</param>
/// <param name="Label">Court texte affiché sous le passage souligné (« « ; » manquant ici »).</param>
/// <param name="Help">
/// Suggestions pour corriger, une par ligne : ce qu'on peut faire à la place, ou la bonne
/// formulation (« la bonne formulation est : train |&gt; done; »).
/// </param>
internal sealed record Diagnostic(
    DiagnosticLevel Level,
    string Message,
    string? Path = null,
    string? Code = null,
    SourceSpan? Span = null,
    string? Label = null,
    IReadOnlyList<string>? Help = null)
{
    /// <summary>Nom de la gravité tel qu'affiché : <c>erreur</c>, <c>avertissement</c> ou <c>info</c>.</summary>
    public string LevelName => Level switch
    {
        DiagnosticLevel.Error => "erreur",
        DiagnosticLevel.Warning => "avertissement",
        _ => "info",
    };

    /// <summary>
    /// Forme d'une ligne : <c>chemin:ligne:colonne: niveau[code]: message</c>, ou
    /// <c>chemin: niveau[code]: message</c> sans position dans le source. Sert aux tests, qui
    /// ne dépendent ainsi pas de la mise en page du terminal.
    /// </summary>
    /// <param name="workingDirectory">Dossier courant, par rapport auquel le chemin est affiché.</param>
    public string Format(string workingDirectory)
    {
        var location = Location(workingDirectory);
        var code = Code is null ? "" : $"[{Code}]";
        return $"{(location is null ? "" : location + ": ")}{LevelName}{code}: {Message}";
    }

    /// <summary>
    /// Emplacement affiché : <c>chemin:ligne:colonne</c> avec un span, <c>chemin</c> avec
    /// seulement un fichier, <see langword="null"/> sinon. Le chemin est relatif au dossier courant.
    /// </summary>
    /// <param name="workingDirectory">Dossier courant, par rapport auquel le chemin est affiché.</param>
    public string? Location(string workingDirectory)
    {
        if (Span is { } span)
        {
            var position = span.StartPosition;
            return $"{System.IO.Path.GetRelativePath(workingDirectory, span.Source.Path)}:{position.Line}:{position.Column}";
        }

        return Path is null ? null : System.IO.Path.GetRelativePath(workingDirectory, Path);
    }
}

/// <summary>Accumule les diagnostics d'une commande.</summary>
internal sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _items = [];

    /// <summary>Diagnostics signalés, dans l'ordre.</summary>
    public IReadOnlyList<Diagnostic> Items => _items;

    internal int Count => _items.Count;

    /// <summary>Retire les diagnostics exploratoires ajoutés depuis un point de reprise.</summary>
    internal void Rollback(int count)
    {
        if (count < _items.Count) _items.RemoveRange(count, _items.Count - count);
    }

    /// <summary>Vrai si au moins une erreur a été signalée.</summary>
    public bool HasErrors => _items.Any(d => d.Level == DiagnosticLevel.Error);

    /// <summary>Ajoute un diagnostic déjà construit.</summary>
    /// <param name="diagnostic">Diagnostic à ajouter.</param>
    public void Add(Diagnostic diagnostic)
    {
        // La std n'est pas le code de l'utilisateur : il n'a rien à y corriger, seules ses erreurs comptent.
        if (diagnostic.Level != DiagnosticLevel.Error && diagnostic.Span?.Source.Path is { } path && Project.StdLibrary.Contains(path)) return;
        _items.Add(diagnostic);
    }

    /// <summary>Signale une erreur.</summary>
    /// <param name="message">Texte de l'erreur.</param>
    /// <param name="path">Fichier concerné, s'il y en a un.</param>
    /// <param name="code">Code E de l'erreur, s'il y en a un.</param>
    public void Error(string message, string? path = null, string? code = null) =>
        _items.Add(new Diagnostic(DiagnosticLevel.Error, message, path, code));

    /// <summary>Signale un avertissement.</summary>
    /// <param name="message">Texte de l'avertissement.</param>
    /// <param name="path">Fichier concerné, s'il y en a un.</param>
    /// <param name="code">Code W de l'avertissement, s'il y en a un.</param>
    public void Warning(string message, string? path = null, string? code = null) =>
        _items.Add(new Diagnostic(DiagnosticLevel.Warning, message, path, code));

    /// <summary>Signale une information.</summary>
    /// <param name="message">Texte de l'information.</param>
    /// <param name="path">Fichier concerné, s'il y en a un.</param>
    public void Info(string message, string? path = null) =>
        _items.Add(new Diagnostic(DiagnosticLevel.Info, message, path));

    /// <summary>Signale une erreur à un endroit du source.</summary>
    /// <param name="span">Endroit concerné ; le diagnostic affiche son fichier, sa ligne et sa colonne.</param>
    /// <param name="code">Code E de l'erreur (voir <see cref="DiagnosticCodes"/>).</param>
    /// <param name="message">Texte de l'erreur.</param>
    /// <param name="label">Court texte sous le passage souligné, s'il y en a un.</param>
    /// <param name="help">Suggestions pour corriger, une par ligne.</param>
    public void Error(SourceSpan span, string code, string message, string? label = null, IReadOnlyList<string>? help = null) =>
        _items.Add(new Diagnostic(DiagnosticLevel.Error, message, Code: code, Span: span, Label: label, Help: help));

    /// <summary>Signale un avertissement à un endroit du source.</summary>
    /// <param name="span">Endroit concerné ; le diagnostic affiche son fichier, sa ligne et sa colonne.</param>
    /// <param name="code">Code W de l'avertissement (voir <see cref="DiagnosticCodes"/>).</param>
    /// <param name="message">Texte de l'avertissement.</param>
    /// <param name="label">Court texte sous le passage souligné, s'il y en a un.</param>
    /// <param name="help">Suggestions pour corriger, une par ligne.</param>
    public void Warning(SourceSpan span, string code, string message, string? label = null, IReadOnlyList<string>? help = null) =>
        Add(new Diagnostic(DiagnosticLevel.Warning, message, Code: code, Span: span, Label: label, Help: help));

    /// <summary>Signale une information (notice) à un endroit du source : rien à corriger.</summary>
    /// <param name="span">Endroit concerné ; le diagnostic affiche son fichier, sa ligne et sa colonne.</param>
    /// <param name="code">Code I de l'information (voir <see cref="DiagnosticCodes"/>).</param>
    /// <param name="message">Texte de l'information.</param>
    /// <param name="label">Court texte sous le passage souligné, s'il y en a un.</param>
    /// <param name="help">Précisions, une par ligne.</param>
    public void Info(SourceSpan span, string code, string message, string? label = null, IReadOnlyList<string>? help = null) =>
        Add(new Diagnostic(DiagnosticLevel.Info, message, Code: code, Span: span, Label: label, Help: help));
}
