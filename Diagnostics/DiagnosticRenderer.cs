using System.Text;
using Rgl.Text;

namespace Rgl.Diagnostics;

/// <summary>
/// Affiche les diagnostics dans le terminal, avec un extrait du source et le passage fautif
/// souligné, en couleur si le terminal le permet :
/// <code>
/// erreur[E030]: « ; » attendu à la fin du train
///  --> main.rgl:2:14
///   |
/// 2 | train |> done
///   |              ^ « ; » manquant ici
///   |
///   = aide : un train se termine toujours par un unique « ; »
/// </code>
/// Un diagnostic sans span affiche seulement son message et, s'il en a un, son fichier.
/// </summary>
/// <param name="workingDirectory">Dossier courant, par rapport auquel les chemins sont affichés.</param>
/// <param name="useColor">Vrai pour colorer la sortie avec des séquences ANSI.</param>
internal sealed class DiagnosticRenderer(string workingDirectory, bool useColor)
{
    /// <summary>Largeur d'une tabulation dans l'extrait, pour que le soulignement reste aligné.</summary>
    private const int TabWidth = 4;

    private const string Reset = "\e[0m";
    private const string Bold = "\e[1m";
    private const string Red = "\e[1;31m";
    private const string Yellow = "\e[1;33m";
    private const string Cyan = "\e[1;36m";
    private const string Blue = "\e[1;34m";
    private const string Green = "\e[1;32m";

    private readonly string _workingDirectory = workingDirectory;
    private readonly bool _useColor = useColor;

    /// <summary>Écrit les diagnostics les uns après les autres, séparés par une ligne vide.</summary>
    /// <param name="writer">Sortie, en général la sortie d'erreur.</param>
    /// <param name="diagnostics">Diagnostics à afficher, dans l'ordre.</param>
    public void Write(TextWriter writer, IEnumerable<Diagnostic> diagnostics)
    {
        var first = true;
        foreach (var diagnostic in diagnostics)
        {
            if (!first) writer.Write('\n');
            writer.Write(Render(diagnostic));
            first = false;
        }
    }

    /// <summary>Met un diagnostic en forme, chaque ligne terminée par <c>\n</c>.</summary>
    /// <param name="diagnostic">Diagnostic à afficher.</param>
    /// <returns>Le texte à écrire dans le terminal.</returns>
    public string Render(Diagnostic diagnostic)
    {
        var builder = new StringBuilder();
        var levelColor = _LevelColor(diagnostic.Level);

        // En-tête : niveau[code]: message
        var code = diagnostic.Code is null ? "" : $"[{diagnostic.Code}]";
        builder.Append(_Paint(levelColor, diagnostic.LevelName + code)).Append(_Paint(Bold, ": " + diagnostic.Message)).Append('\n');

        if (diagnostic.Span is { } span)
        {
            _RenderSnippet(builder, diagnostic, span, levelColor);
            return builder.ToString();
        }

        var location = diagnostic.Location(_workingDirectory);
        if (location is not null) builder.Append(_Paint(Blue, " --> ")).Append(location).Append('\n');
        foreach (var help in diagnostic.Help ?? []) builder.Append(_Paint(Green, " = aide")).Append(" : ").Append(help).Append('\n');

        return builder.ToString();
    }

    /// <summary>Emplacement, extrait de la ligne, soulignement, étiquette et aides.</summary>
    private void _RenderSnippet(StringBuilder builder, Diagnostic diagnostic, SourceSpan span, string levelColor)
    {
        var start = span.StartPosition;
        var lineNumber = start.Line.ToString();
        var gutter = new string(' ', lineNumber.Length);
        var bar = _Paint(Blue, "|");

        var (line, lineStart) = _Line(span.Source, (int)start.Line.Value);
        var offset = (int)span.Start.Value - lineStart;
        var (displayLine, column) = _ExpandTabs(line, offset);

        // Soulignement : la longueur du span sur sa première ligne, au moins un caractère.
        var length = Math.Min((int)span.Length.Value, line.Length - offset);
        var underline = _ExpandTabs(line[offset..(offset + Math.Max(length, 0))], length).Column;
        var marker = new string(diagnostic.Level == DiagnosticLevel.Error ? '^' : '~', Math.Max(underline, 1));

        builder.Append(gutter).Append(_Paint(Blue, "--> ")).Append(diagnostic.Location(_workingDirectory)).Append('\n');
        builder.Append(gutter).Append(' ').Append(bar).Append('\n');
        builder.Append(_Paint(Blue, lineNumber)).Append(' ').Append(bar).Append(' ').Append(displayLine).Append('\n');
        builder.Append(gutter).Append(' ').Append(bar).Append(' ').Append(new string(' ', column)).Append(_Paint(levelColor, marker));
        if (diagnostic.Label is not null) builder.Append(' ').Append(_Paint(levelColor, diagnostic.Label));
        builder.Append('\n');

        if (diagnostic.Help is not { Count: > 0 } helps) return;

        builder.Append(gutter).Append(' ').Append(bar).Append('\n');
        foreach (var help in helps)
            builder.Append(gutter).Append(' ').Append(_Paint(Green, "= aide")).Append(" : ").Append(help).Append('\n');
    }

    /// <summary>Texte de la ligne <paramref name="lineNumber"/> (à partir de 1), sans sa fin de ligne, et l'indice de son début.</summary>
    private static (string Line, int Start) _Line(SourceText source, int lineNumber)
    {
        var start = (int)source.LineStart[lineNumber - 1].Value;
        var end = lineNumber < source.LineStart.Count ? (int)source.LineStart[lineNumber].Value : source.Content.Length;
        var line = source.Content[start..end].TrimEnd('\n', '\r');
        return (line, start);
    }

    /// <summary>
    /// Remplace les tabulations par des espaces, et donne la colonne d'affichage du caractère
    /// d'indice <paramref name="offset"/> dans la ligne.
    /// </summary>
    private static (string Line, int Column) _ExpandTabs(string line, int offset)
    {
        var builder = new StringBuilder();
        var column = 0;

        for (var i = 0; i < line.Length; ++i)
        {
            if (i == offset) column = builder.Length;
            if (line[i] == '\t') builder.Append(' ', TabWidth - builder.Length % TabWidth);
            else builder.Append(line[i]);
        }

        if (offset >= line.Length) column = builder.Length + (offset - line.Length);
        return (builder.ToString(), column);
    }

    private static string _LevelColor(DiagnosticLevel level) => level switch
    {
        DiagnosticLevel.Error => Red,
        DiagnosticLevel.Warning => Yellow,
        _ => Cyan,
    };

    /// <summary>Entoure <paramref name="text"/> de la couleur demandée, si les couleurs sont activées.</summary>
    private string _Paint(string color, string text) => _useColor ? color + text + Reset : text;
}
