using Rgl.AST;
using Rgl.AST.Descriptors;
using Rgl.AST.Enums;
using Rgl.AST.Extensions;
using Rgl.AST.Files;
using Rgl.AST.Gears;
using Rgl.AST.Ingots;
using Rgl.AST.Interfaces;
using Rgl.AST.Molds;
using Rgl.AST.Precasts;
using Rgl.AST.Train;
using Rgl.AST.Variables;
using Rgl.Diagnostics;
using Rgl.Syntax;
using Rgl.Text;
using Rotomeca.Utils.Types;

namespace Rgl.Project;

/// <summary>
/// Ce que le genre d'un fichier demande de son contenu : l'en-tête <c>forge:</c> d'un fichier <c>.forge</c> (E016, W005,
/// E106), l'absence d'en-tête du <c>.rgl</c> d'entrée (E105), et l'ordre et la nature des déclarations (E104, E112).
/// </summary>
internal static class FileChecks
{
    /// <summary>Les genres de fichier pris en charge.</summary>
    private static readonly string[] SupportedKinds = ["train", "mold", "gears", "extend", "index", "precast", "native", "descriptor"];

    /// <summary>Les genres du langage que le compilateur ne prend pas encore en charge.</summary>
    private static readonly string[] PendingKinds = [];

    /// <summary>Vérifie l'en-tête et le contenu d'un fichier ; les problèmes sont signalés dans <paramref name="diagnostics"/>.</summary>
    /// <param name="unit">L'arbre du fichier, sans erreur de syntaxe.</param>
    /// <param name="isEntry">Vrai pour le fichier <c>.rgl</c> d'entrée, faux pour un <c>.forge</c>.</param>
    /// <param name="diagnostics">Reçoit les diagnostics.</param>
    public static void Check(CompilationUnit unit, bool isEntry, DiagnosticBag diagnostics)
    {
        foreach (var train in unit.Statements.OfType<TrainDeclaration>().Concat(unit.Statements.OfType<TrainBlock>().Select(b => b.Train).OfType<TrainDeclaration>()).Where(t => t.NativeReturnType is not null))
        {
            if (isEntry || unit.Header?.Kind?.Name != "train" || train.IsUnguarded)
                diagnostics.Error(train.Span, DiagnosticCodes.NativeComponent, "un train C++ se déclare dans forge: train, sans !train", "train natif", []);
            foreach (var parameter in (train.Parameters ?? []).Where(p => p.IsMutable || p.IsParams || p.IsThis || p.Default is not null))
                diagnostics.Error(parameter.Span, DiagnosticCodes.NativeComponent, "un train C++ reçoit ses paramètres sans mut, params, this ni valeur par défaut", "paramètre natif", []);
        }
        if (isEntry) _CheckEntry(unit, diagnostics);
        else _CheckForge(unit, diagnostics);
    }

    private static void _CheckEntry(CompilationUnit unit, DiagnosticBag diagnostics)
    {
        if (unit.Header is { } header)
            diagnostics.Error(header.Span, DiagnosticCodes.UnexpectedHeader, "le fichier .rgl d'entrée n'a pas d'en-tête", "en-tête en trop",
                ["le fichier .rgl est implicitement de genre train : retire l'en-tête forge:",
                 "l'en-tête est réservé aux fichiers .forge"]);

        foreach (var export in unit.Statements.OfType<ExportDeclaration>())
            diagnostics.Error(export.Span, DiagnosticCodes.MisplacedDeclaration, "le fichier .rgl n'exporte rien", "pas dans le fichier .rgl",
                ["personne n'importe le fichier .rgl d'entrée : retire l'export", "l'export s'écrit dans un fichier .forge"]);

        foreach (var extend in unit.Statements.OfType<ExtendDeclaration>())
            diagnostics.Error(extend.Span, DiagnosticCodes.MisplacedDeclaration, "une extension s'écrit dans un fichier forge: extend", "pas dans le fichier .rgl",
                ["les extensions ne servent que dans le fichier où elles sont écrites, et pour l'instant seulement dans un fichier forge: extend"]);

        foreach (var space in unit.Statements.OfType<NamespaceDeclaration>())
            diagnostics.Error(space.Span, DiagnosticCodes.MisplacedDeclaration, "un namespace s'écrit dans un fichier forge: extend", "pas dans le fichier .rgl",
                ["un namespace distingue les extensions d'un fichier : le .rgl d'entrée n'en écrit pas"]);

        foreach (var precast in unit.Statements.OfType<PrecastDeclaration>())
            diagnostics.Error(precast.Span, DiagnosticCodes.MisplacedDeclaration, "un precast s'écrit dans un fichier forge: precast", "pas dans le fichier .rgl",
                ["un precast est une instance de mold connue à la compilation : écris-le dans un fichier .forge de genre precast, puis importe-le"]);

        foreach (var descriptor in unit.Statements.OfType<DescriptorDeclaration>())
            diagnostics.Error(descriptor.Span, DiagnosticCodes.MisplacedDeclaration, "un descriptor s'écrit dans un fichier forge: descriptor", "pas dans le fichier .rgl",
                ["le .rgl d'entrée ne décrit pas de classe C++ : écris le descriptor dans un fichier .forge de genre descriptor, puis importe-le"]);

        foreach (var native in unit.Statements.OfType<GearDeclaration>().Where(g => g.IsNative))
            diagnostics.Error(native.Span, DiagnosticCodes.MisplacedDeclaration, "un composant écrit en C++ s'écrit dans un fichier forge: gears", "pas dans le fichier .rgl",
                ["le .rgl d'entrée ne contient que son train et ses gears écrits en RGL : déclare le guard dans un fichier .forge, à côté de son .rgl.cpp"]);

        foreach (var block in unit.Statements.OfType<TrainBlock>())
            diagnostics.Error(block.Span, DiagnosticCodes.MisplacedDeclaration, "le fichier .rgl n'a qu'un train : le train d'entrée", "train nommé",
                ["les trains nommés s'écrivent dans un fichier .forge de genre train (forge: train)"]);
    }

    private static void _CheckForge(CompilationUnit unit, DiagnosticBag diagnostics)
    {
        var start = new SourceSpan(unit.Span.Source, unit.Span.Start, RUint.Zero);
        if (unit.Header?.Kind is not { } kind)
        {
            diagnostics.Error(unit.Header?.Span ?? start, DiagnosticCodes.MissingHeader, "le fichier .forge n'a pas d'en-tête « forge: »", "en-tête manquant",
                ["un fichier .forge commence par son genre, par exemple : forge: train",
                 $"genres : {string.Join(", ", SupportedKinds)}"]);
            return;
        }

        if (unit.Header.Description is null)
            diagnostics.Warning(unit.Header.Span, DiagnosticCodes.MissingDescription, "l'en-tête n'a pas de « description: »", "description manquante",
                ["décris le fichier, par exemple : description: \"Commandes de l'outil\""]);

        if (PendingKinds.Contains(kind.Name))
        {
            diagnostics.Add(new Diagnostic(DiagnosticLevel.Error, $"le genre « {kind.Name} » : pas encore pris en charge par le compilateur", Span: kind.Span, Label: "pas encore pris en charge"));
            return;
        }
        if (!SupportedKinds.Contains(kind.Name))
        {
            diagnostics.Error(kind.Span, DiagnosticCodes.UnknownFileKind, $"genre de fichier inconnu : « {kind.Name} »", "genre inconnu",
                [$"genres : {string.Join(", ", SupportedKinds.Concat(PendingKinds))}"]);
            return;
        }

        if (kind.Name is "train" or "gears" or "extend")
        {
            foreach (var precast in unit.Statements.OfType<PrecastDeclaration>())
                diagnostics.Error(precast.Span, DiagnosticCodes.MisplacedDeclaration, "un precast s'écrit dans un fichier forge: precast", "pas dans ce genre de fichier",
                    ["les precasts ont leur propre genre de fichier : forge: precast"]);
        }

        foreach (var extra in unit.Statements.OfType<NamespaceDeclaration>().Skip(1))
            diagnostics.Error(extra.Span, DiagnosticCodes.InvalidNamespace, "un fichier n'a qu'un namespace", "second namespace", ["retire-le : un seul namespace par fichier"]);

        if (kind.Name is not ("native" or "descriptor") && (unit.Header.Headers is { Count: > 0 } || unit.Header.Link is not null || unit.Header.Library is not null))
            diagnostics.Error(unit.Header.Span, DiagnosticCodes.MisplacedDeclaration, "header:, link: et library: s'écrivent dans un fichier forge: native ou descriptor", "pas dans ce genre de fichier",
                ["ces clés décrivent une bibliothèque C : elles n'ont de sens que dans un fichier forge: native"]);

        _CheckNativeComponents(unit, kind.Name, diagnostics);

        if (kind.Name != "descriptor")
            foreach (var descriptor in unit.Statements.OfType<DescriptorDeclaration>())
                diagnostics.Error(descriptor.Span, DiagnosticCodes.MisplacedDeclaration, "un descriptor s'écrit dans un fichier forge: descriptor", "pas dans ce genre de fichier",
                    ["un descriptor décrit une classe C++ : il a son propre genre de fichier, forge: descriptor, avec le header: de la classe"]);

        switch (kind.Name)
        {
            case "native":
                _CheckNativeFile(unit, diagnostics, descriptors: false);
                break;
            case "descriptor":
                _CheckNativeFile(unit, diagnostics, descriptors: true);
                _CheckDescriptors(unit, diagnostics);
                break;
            case "precast":
                _CheckPrecastFile(unit, diagnostics);
                break;
            case "train":
                foreach (var space in unit.Statements.OfType<NamespaceDeclaration>())
                    diagnostics.Error(space.Span, DiagnosticCodes.MisplacedDeclaration, "un namespace s'écrit dans un fichier forge: extend", "pas dans ce genre de fichier", []);
                foreach (var extend in unit.Statements.OfType<ExtendDeclaration>())
                    diagnostics.Error(extend.Span, DiagnosticCodes.MisplacedDeclaration, "une extension s'écrit dans un fichier forge: extend", "pas dans ce genre de fichier",
                        ["pour l'instant, les extensions ne s'écrivent que dans un fichier forge: extend"]);
                _CheckTrainFile(unit, start, diagnostics);
                break;
            case "index":
                _CheckOnly(unit, diagnostics, "un fichier forge: index ne contient que des imports et un export {…};",
                    s => s is ImportDeclaration or ExportDeclaration);
                break;
            case "mold":
                _CheckOnly(unit, diagnostics, "un fichier forge: mold ne contient que des molds, des enums, des ingots et des imports",
                    s => s is MoldDeclaration or EnumDeclaration or IngotDeclaration or ImportDeclaration or ExportDeclaration);
                break;
            default:
                _CheckOnly(unit, diagnostics, "un fichier forge: gears ou extend ne contient pas de train : il contient des gears, des guards, des emits, des molds, des constantes et des imports",
                    s => s is not (TrainDeclaration or TrainBlock));
                break;
        }
    }

    /// <summary>
    /// Un gear sans corps est implémenté en C++ (un fichier <c>.rgl.cpp</c> du même nom). Il n'est permis que dans un fichier <c>forge: gears</c>, et c'est
    /// un guard, sans option que le C++ ne prend pas encore en charge : le C++ peut lancer une exception, qui devient une <c>NativeError</c> sur le rail.
    /// </summary>
    private static void _CheckNativeComponents(CompilationUnit unit, string kind, DiagnosticBag diagnostics)
    {
        foreach (var gear in unit.Statements.OfType<GearDeclaration>().Where(g => g.IsNative))
        {
            if (kind != "gears")
            {
                diagnostics.Error(gear.Span, DiagnosticCodes.MisplacedDeclaration, "un composant écrit en C++ s'écrit dans un fichier forge: gears", "pas dans ce genre de fichier",
                    ["un guard sans corps est implémenté par un fichier .rgl.cpp : déclare-le dans un fichier forge: gears"]);
                continue;
            }

            if (gear.Keyword is not (TokenKind.GuardKeyword or TokenKind.UnsafeKeyword))
                diagnostics.Error(gear.Name.Span, DiagnosticCodes.NativeComponent, $"« {gear.Name.Name} » est implémenté en C++ : c'est un guard", "pas un guard",
                    ["le C++ peut lancer une exception, que le compilateur envoie sur le rail comme une NativeError : déclare « guard » au lieu de « " + gear.Keyword.ToString().Replace("Keyword", "").ToLowerInvariant() + " »"]);
            if (gear.TypeParameters is not null)
                diagnostics.Error(gear.Name.Span, DiagnosticCodes.NativeComponent, $"« {gear.Name.Name} » : un composant en C++ n'est pas générique", "paramètres génériques", ["les génériques ne sont pas encore pris en charge pour un composant en C++"]);
            foreach (var parameter in gear.Parameters.Where(p => p.IsMutable || p.IsParams || p.IsThis || p.Default is not null))
                diagnostics.Error(parameter.Span, DiagnosticCodes.NativeComponent, $"le paramètre « {parameter.Name.Name} » de « {gear.Name.Name} » : un composant en C++ n'a ni mut, ni params, ni this, ni valeur par défaut", "pas encore pris en charge",
                    ["un composant en C++ reçoit ses arguments par valeur ou par référence constante : écris-les sans option"]);
        }
    }

    /// <summary>
    /// Les membres d'un descriptor : un nom par membre (les champs et les méthodes partagent leurs noms), au plus un constructeur, et pas d'option que le C++ ne
    /// prend pas encore en charge (<c>mut</c>, <c>params</c>, <c>this</c>, valeur par défaut, générique).
    /// </summary>
    private static void _CheckDescriptors(CompilationUnit unit, DiagnosticBag diagnostics)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var descriptor in unit.Statements.OfType<DescriptorDeclaration>())
        {
            if (!names.Add(descriptor.Name.Name))
                diagnostics.Error(descriptor.Name.Span, DiagnosticCodes.NativeComponent, $"le descriptor « {descriptor.Name.Name} » est déjà écrit", "en double", ["chaque type décrit a un seul descriptor"]);

            var members = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in descriptor.Members)
            {
                if (!members.Add((member.Kind == DescriptorMemberKind.Constructor ? "create" : member.Name.Name) + "(" + string.Join(",", member.Parameters.Select(p => string.Concat(p.Type.Span.Text.Where(c => !char.IsWhiteSpace(c))))) + ")"))
                    diagnostics.Error(member.Name.Span, DiagnosticCodes.NativeComponent, $"« {member.Name.Name} » est déjà un membre de {descriptor.Name.Name}", "en double",
                        ["deux surcharges doivent avoir des signatures différentes"]);
                foreach (var parameter in member.Parameters.Where(p => p.IsMutable || p.IsParams || p.IsThis || p.Default is not null))
                    diagnostics.Error(parameter.Span, DiagnosticCodes.NativeComponent, $"le paramètre « {parameter.Name.Name} » de « {member.Name.Name} » : un membre de descriptor n'a ni mut, ni params, ni this, ni valeur par défaut", "pas encore pris en charge",
                        ["un membre reçoit ses arguments par valeur ou par référence constante : écris-les sans option"]);
            }
        }
    }

    /// <summary>
    /// Un fichier <c>forge: native</c> ne contient que son en-tête : au moins un <c>header:</c>, <c>link: static</c> (<c>dynamic</c> est E020)
    /// et, si la bibliothèque n'est pas dans le runtime, <c>library:</c>.
    /// </summary>
    private static void _CheckNativeFile(CompilationUnit unit, DiagnosticBag diagnostics, bool descriptors)
    {
        var header = unit.Header!;
        if (descriptors) _CheckOnly(unit, diagnostics, "un fichier forge: descriptor ne contient que son en-tête (header:, link:, library:) et des descriptors", s => s is DescriptorDeclaration);
        else _CheckOnly(unit, diagnostics, "un fichier forge: native ne contient que son en-tête : header:, link: et library:", _ => false);

        if (header.Headers is not { Count: > 0 })
            diagnostics.Error(header.Span, DiagnosticCodes.MisplacedDeclaration, $"un fichier forge: {(descriptors ? "descriptor" : "native")} a au moins un « header: »", "header manquant",
                ["décris l'en-tête à lire, par exemple : header: \"vendor/lib/include/lib.h\""]);

        if (header.Link is not { } link)
            diagnostics.Error(header.Span, DiagnosticCodes.MisplacedDeclaration, $"un fichier forge: {(descriptors ? "descriptor" : "native")} dit comment il se lie : « link: static »", "link manquant",
                ["seule la liaison statique existe pour l'instant : link: static"]);
        else if (link.Name == "dynamic")
            diagnostics.Error(link.Span, DiagnosticCodes.DynamicLinkUnavailable, "link: dynamic n'est pas disponible avant la V2", "liaison dynamique",
                ["la V2 ajoutera la liaison dynamique", "utilise link: static avec une bibliothèque statique (.a, .lib)"]);
        else if (link.Name != "static")
            diagnostics.Error(link.Span, DiagnosticCodes.MisplacedDeclaration, $"mode de liaison inconnu : « {link.Name} »", "mode inconnu", ["modes : static"]);

        foreach (var entry in header.Library ?? [])
            if (entry.Platform.Name is not ("windows" or "linux"))
                diagnostics.Error(entry.Platform.Span, DiagnosticCodes.MisplacedDeclaration, $"plateforme inconnue : « {entry.Platform.Name} »", "plateforme inconnue", ["plateformes : windows, linux"]);
    }

    /// <summary>
    /// Un fichier <c>forge: precast</c> : au plus un import (le mold des precasts) et des precasts. Un precast qui déclare son
    /// mold (champs typés, ou type anonyme écrit) n'a pas d'import et reste le seul du fichier.
    /// </summary>
    private static void _CheckPrecastFile(CompilationUnit unit, DiagnosticBag diagnostics)
    {
        _CheckOnly(unit, diagnostics, "un fichier forge: precast ne contient qu'un import de mold et des precasts", s => s is PrecastDeclaration or ImportDeclaration);

        var imports = unit.Statements.OfType<ImportDeclaration>().ToArray();
        foreach (var extra in imports.Skip(1))
            diagnostics.Error(extra.Span, DiagnosticCodes.MisplacedDeclaration, "un fichier forge: precast importe au plus un mold", "second import",
                ["les precasts d'un fichier sont tous de ce mold : sépare-les en plusieurs fichiers"]);

        var precasts = unit.Statements.OfType<PrecastDeclaration>().ToArray();
        var declaring = precasts.Where(p => p.Type is null or AST.Types.AnonymousMoldType).ToArray();
        foreach (var precast in declaring)
        {
            if (imports.Length > 0)
                diagnostics.Error(precast.Span, DiagnosticCodes.MisplacedDeclaration, $"« {precast.Name.Name} » déclare son mold, mais le fichier en importe un", "mold déclaré",
                    ["un precast sans import déclare son mold ; avec un import, écris le mold avant le nom : precast Vec2 Zero { x: 0.0, y: 0.0 }"]);
        }
        foreach (var extra in declaring.Skip(1))
            diagnostics.Error(extra.Span, DiagnosticCodes.MisplacedDeclaration, $"« {extra.Name.Name} » est un second precast qui déclare son mold", "un seul par fichier",
                ["un fichier ne contient qu'un precast qui déclare son mold : importe ce mold pour en écrire d'autres"]);
    }

    /// <summary>
    /// Un fichier <c>forge: train</c> : ses déclarations communes en tête (E104 après un train), ses trains nommés, puis son
    /// train par défaut, obligatoire (E112) et toujours le dernier (E104).
    /// </summary>
    private static void _CheckTrainFile(CompilationUnit unit, SourceSpan start, DiagnosticBag diagnostics)
    {
        var seenTrain = false;
        var seenDefault = false;
        foreach (var statement in unit.Statements)
        {
            switch (statement)
            {
                case ImportDeclaration or ExportDeclaration:
                    break;

                case TrainBlock { Name: not null } named:
                    if (seenDefault)
                        diagnostics.Error(named.Span, DiagnosticCodes.MisplacedDeclaration, "un train nommé vient avant le train par défaut", "après le train par défaut",
                            ["le train par défaut est toujours le dernier du fichier"]);
                    seenTrain = true;
                    break;

                case TrainBlock or TrainDeclaration:
                    if (seenDefault)
                        diagnostics.Error(statement.Span, DiagnosticCodes.MisplacedDeclaration, "un seul train par défaut", "second train par défaut", []);
                    seenTrain = seenDefault = true;
                    break;

                default:
                    if (seenTrain)
                        diagnostics.Error(statement.Span, DiagnosticCodes.MisplacedDeclaration, "une déclaration commune s'écrit avant les trains", "après un train",
                            ["les déclarations communes (constantes, gears, molds…) sont en tête du fichier ; celles d'un train s'écrivent dans son bloc : train nom { … }"]);
                    break;
            }
        }

        if (!seenDefault)
            diagnostics.Error(start, DiagnosticCodes.MissingDefaultTrain, "le fichier n'a pas de train par défaut", "train par défaut manquant",
                ["un fichier forge: train se termine par son train par défaut : train(params) étape |> … ;",
                 "ou par un bloc sans nom : { … train(params) … ; }"]);
    }

    private static void _CheckOnly(CompilationUnit unit, DiagnosticBag diagnostics, string message, Func<IStatement, bool> allowed)
    {
        foreach (var statement in unit.Statements.Where(s => !allowed(s)))
        {
            var what = statement switch
            {
                GearDeclaration gear => $"un {gear.Keyword.ToString().Replace("Keyword", "").ToLowerInvariant()}",
                TrainDeclaration or TrainBlock => "un train",
                VarDeclaration => "une variable",
                _ => "cette déclaration",
            };
            diagnostics.Error(statement.Span, DiagnosticCodes.MisplacedDeclaration, $"{what} n'a pas sa place ici", "pas dans ce genre de fichier", [message]);
        }
    }
}
