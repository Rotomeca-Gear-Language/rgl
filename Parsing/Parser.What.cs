using Rgl.AST.Expressions;
using Rgl.AST.Collections;
using Rgl.AST.Datasets;
using Rgl.AST.Enums;
using Rgl.AST.Extensions;
using Rgl.AST.Files;
using Rgl.AST.Gears;
using Rgl.AST.Generics;
using Rgl.AST.InputOutput;
using Rgl.AST.Ingots;
using Rgl.AST.Interfaces;
using Rgl.AST.Molds;
using Rgl.AST.Precasts;
using Rgl.AST.Statements;
using Rgl.AST.Steps;
using Rgl.AST.Train;
using Rgl.AST.Types;
using Rgl.AST.Variables;
using Rgl.AST;
using Rgl.Diagnostics;
using Rgl.Syntax;
using Rgl.Text;
using Rotomeca.Core.Collections;
using Rotomeca.Utils.Types;

namespace Rgl.Parsing;

internal sealed partial class Parser
{
    /// <summary>
    /// <c>Fichier := [ EnTête ] { Import | Instruction | Gear | BlocDeTrain } [ Train ] FinDeFichier</c>, avec un
    /// seul train par défaut (un guard ou un emit s'écrit comme un gear). Le parseur
    /// accepte toute instruction au niveau du fichier ; le binder n'y garde que les constantes de
    /// tête (les autres vont dans un gear). Un fichier sans instruction donne une unité vide (W006
    /// est déjà signalé par le lexer). Après le train par défaut, ce qui n'est ni une instruction ni un
    /// gear (un second train…) est signalé (E034) et sauté jusqu'au prochain <c>;</c>.
    /// </summary>
    private CompilationUnit _ParseProgram()
    {
        var file = new SourceSpan(_source, RUint.Zero, RUint.CreateChecked(_source.Content.Length));
        var header = _ParseHeader();
        RArray<IStatement> statements = new();
        var hasTrain = false;

        while (!_Expect(TokenKind.EndOfFile))
        {
            // @deprecated, [métadonnées] : des notes posées devant la déclaration qui suit.
            if (_AtNotes()) _ParseNotedDeclaration(statements, ref hasTrain);
            else _ParseTopLevelItem(statements, ref hasTrain);
        }

        return new CompilationUnit(file, [.. statements], header);
    }

    /// <summary>Un élément du premier niveau d'un fichier : un import, un export, une extension, un train, une déclaration ou une instruction.</summary>
    private void _ParseTopLevelItem(RArray<IStatement> statements, ref bool hasTrain)
    {
        if (_Expect(TokenKind.ImportKeyword) || _AtUse())
            statements.Push(_ParseImport());
        else if (_AtExport())
            _ParseExport(statements);
        else if (_AtDatasetExtension())
            statements.Push(_ParseDatasetExtension());
        else if (_AtExtend())
            statements.Push(_ParseExtend());
        else if (_AtNamespace())
            statements.Push(_ParseNamespace());
        else if (_AtAnnotationDeclaration())
            statements.Push(_ParseAnnotationDeclaration());
        // « train nom { … } » : un train nommé avec ses déclarations locales.
        else if (_Expect(TokenKind.TrainKeyword) && _Future().Kind == TokenKind.Identifier && _Future(2).Kind == TokenKind.OpenBrace)
            statements.Push(_ParseTrainBlock());
        // « { … train … ; } » : le bloc du train par défaut.
        else if (!hasTrain && _Expect(TokenKind.OpenBrace) && _BlockHoldsTrain())
        {
            statements.Push(_ParseTrainBlock());
            hasTrain = true;
        }
        else if (_TryParseDeclaration(statements)) { }
        else if (_Expect(TokenKind.CloseBrace))
        {
            _Error(_Current.Span, DiagnosticCodes.ExpectedToken, "« } » sans « { »", "ne ferme aucun bloc",
                "supprime cette accolade, ou vérifie celles des blocs au-dessus");
            _Consume();
        }
        else if (!hasTrain)
        {
            statements.Push(_ParseTrainDeclaration());
            hasTrain = true;
        }
        else _SkipUnexpectedAfterTrain();
    }

    /// <summary>
    /// Lit, si le token courant en commence une, une déclaration (gear, guard, emit, mold, error, enum, ingot)
    /// ou une instruction, et l'ajoute à <paramref name="statements"/>.
    /// </summary>
    /// <returns>Vrai si une déclaration ou une instruction a été lue.</returns>
    private bool _TryParseDeclaration(RArray<IStatement> statements)
    {
        if (_ExpectOneOf(TokenKind.GearKeyword, TokenKind.GuardKeyword, TokenKind.EmitKeyword, TokenKind.UnsafeKeyword))
            statements.Push(_ParseGear());
        else if (_Expect(TokenKind.MoldKeyword))
            statements.Push(_ParseMold());
        // « soa » devant « mold » déclare un layout en colonnes.
        else if (_Expect(TokenKind.SoaKeyword) && _Future().Kind == TokenKind.MoldKeyword)
        {
            var soa = _Consume();
            var layout = _ParseMold();
            statements.Push(layout with { Span = soa.Span.Union(layout.Span), IsSoa = true });
        }
        // « error » est contextuel : suivi d'un nom puis d'une accolade, il déclare un type d'erreur.
        else if (_Expect(TokenKind.Identifier, "error") && _Future().Kind == TokenKind.Identifier && _Future(2).Kind == TokenKind.OpenBrace)
            statements.Push(_ParseMold(isError: true));
        else if (_Expect(TokenKind.EnumKeyword))
            statements.Push(_ParseEnum());
        else if (_Expect(TokenKind.IngotKeyword))
            statements.Push(_ParseIngot());
        else if (_Expect(TokenKind.PrecastKeyword))
            statements.Push(_ParsePrecast());
        else if (_AtDataset())
            statements.Push(_ParseDataset());
        else if (_Expect(TokenKind.DescriptorKeyword))
            statements.Push(_ParseDescriptor());
        else if (_CanStartStatement())
            statements.Push(_ParseStatement());
        else return false;
        return true;
    }

    /// <summary>
    /// Vrai si l'accolade courante ouvre le bloc d'un train par défaut : un <c>train</c> (ou <c>!train</c>) se
    /// trouve directement dans ses accolades, pas dans un bloc imbriqué.
    /// </summary>
    private bool _BlockHoldsTrain()
    {
        var depth = 0;
        for (var i = 0; ; ++i)
        {
            var token = _Future(i);
            switch (token.Kind)
            {
                case TokenKind.EndOfFile:
                    return false;
                case TokenKind.OpenBrace:
                    ++depth;
                    break;
                case TokenKind.CloseBrace:
                    if (--depth == 0) return false;
                    break;
                case TokenKind.TrainKeyword when depth == 1:
                    return true;
            }
        }
    }

    /// <summary>
    /// <c>BlocDeTrain := [ "train" Identificateur ] "{" { Déclaration } Train "}"</c> : un train nommé, ou le train par
    /// défaut quand le nom manque, avec ses constantes, gears, molds… locaux. Le train est la dernière
    /// instruction du bloc ; un second train, ou une déclaration après lui, est signalé (E034).
    /// </summary>
    private TrainBlock _ParseTrainBlock()
    {
        var first = _Current;
        Identifier? name = null;
        if (_Expect(TokenKind.TrainKeyword))
        {
            _Consume();
            name = new Identifier(_Consume().Span);
        }

        _Consume();   // « { »
        RArray<IStatement> declarations = new();
        TrainDeclaration? train = null;

        while (!_ExpectOneOf(TokenKind.CloseBrace, TokenKind.EndOfFile))
        {
            if (_Expect(TokenKind.TrainKeyword) || (_Expect(TokenKind.Bang) && _Future().Kind == TokenKind.TrainKeyword))
            {
                if (train is null) train = _ParseTrainDeclaration();
                else
                {
                    _Error(_Current.Span, DiagnosticCodes.UnexpectedAfterTrain, "un bloc ne contient qu'un train", "second train",
                        "un train nommé, ou le train par défaut, a un seul train : écris-en un autre bloc");
                    while (_ExpectOneOf(TokenKind.TrainKeyword, TokenKind.Bang)) _Consume();
                    _SkipToSemicolon();
                }
            }
            else if (_TryParseDeclaration(declarations))
            {
                if (train is not null)
                    _Error(declarations[^1].Span, DiagnosticCodes.UnexpectedAfterTrain, "déclaration après le train du bloc", "après le train",
                        "le train est la dernière instruction du bloc : écris les déclarations locales au-dessus");
            }
            else
            {
                _Error(_Current.Span, DiagnosticCodes.ExpectedToken, "« } » attendu pour fermer le bloc du train", "ni une déclaration, ni « } »",
                    "un bloc de train contient des constantes, des gears, des molds…, puis son train");
                _SkipToSemicolon();
            }
        }

        _ExpectOrReport(TokenKind.CloseBrace, "pour fermer le bloc du train");
        return new TrainBlock(first.Span.Union(_previous!.Span), name, [.. declarations], train);
    }

    #region En-tête et imports

    /// <summary>Les clés d'un en-tête de fichier.</summary>
    private static readonly string[] HeaderKeys = ["forge", "description", "metadata", "header", "link", "library"];

    /// <summary>
    /// <c>EnTête := { ( "forge" | "description" | "metadata" ) ":" Valeur }</c> : le genre du fichier (un mot),
    /// sa description (un texte) et ses métadonnées (un objet JSON, lu sans être interprété) ; <see langword="null"/>
    /// sans en-tête. Une clé répétée est signalée (E030).
    /// </summary>
    private FileHeader? _ParseHeader()
    {
        if (!_AtHeaderKey()) return null;

        var first = _Current;
        Identifier? kind = null;
        SourceSpan? description = null, metadata = null;
        List<SourceSpan> headers = [];
        Identifier? link = null;
        List<LibraryEntry>? library = null;

        while (_AtHeaderKey())
        {
            var key = _Consume();
            _Consume();   // « : »
            var seen = key.Text switch { "forge" => kind is not null, "description" => description is not null, "metadata" => metadata is not null, "link" => link is not null, "library" => library is not null, _ => false };
            if (seen)
                _Error(key.Span, DiagnosticCodes.ExpectedToken, $"« {key.Text} » est déjà écrit dans l'en-tête", "en double", "chaque clé de l'en-tête s'écrit une seule fois");

            switch (key.Text)
            {
                case "forge":
                    if (_Expect(TokenKind.Identifier) || (_Current.Kind != TokenKind.EndOfFile && _Current.Text.Length > 0 && char.IsLetter(_Current.Text[0])))
                        kind = new Identifier(_Consume().Span);
                    else
                        _Error(_MissingAfterPrevious(), DiagnosticCodes.ExpectedToken, "genre du fichier attendu après « forge: »", "genre manquant ici",
                            "par exemple : forge: train, forge: mold, forge: gears");
                    break;

                case "description":
                    if (_ExpectOneOf(TokenKind.StringLiteral, TokenKind.RawStringLiteral)) description = _Consume().Span;
                    else
                        _Error(_MissingAfterPrevious(), DiagnosticCodes.ExpectedToken, "texte attendu après « description: »", "texte manquant ici",
                            "par exemple : description: \"Commandes de l'outil\"");
                    break;

                case "header":
                    if (_ExpectOneOf(TokenKind.StringLiteral, TokenKind.RawStringLiteral)) headers.Add(_Consume().Span);
                    else
                        _Error(_MissingAfterPrevious(), DiagnosticCodes.ExpectedToken, "chemin attendu après « header: »", "chemin manquant ici",
                            "par exemple : header: \"vendor/lib/include/lib.h\"");
                    break;

                case "link":
                    if (_Expect(TokenKind.Identifier)) link = new Identifier(_Consume().Span);
                    else
                        _Error(_MissingAfterPrevious(), DiagnosticCodes.ExpectedToken, "mode de liaison attendu après « link: »", "mode manquant ici",
                            "par exemple : link: static");
                    break;

                case "library":
                    library = _ParseLibrary();
                    break;

                default:
                    metadata = _ParseMetadata();
                    break;
            }
        }

        return new FileHeader(first.Span.Union(_previous!.Span), kind, description, metadata, headers, link, library);
    }

    /// <summary>Vrai si le token courant est une clé d'en-tête suivie de « : ».</summary>
    private bool _AtHeaderKey() => _Expect(TokenKind.Identifier) && HeaderKeys.Contains(_Current.Text) && _Future().Kind == TokenKind.Colon;

    /// <summary><c>library: { plateforme: "chemin", … }</c> : la bibliothèque statique de chaque plateforme.</summary>
    private List<LibraryEntry> _ParseLibrary()
    {
        List<LibraryEntry> entries = [];
        if (!_Expect(TokenKind.OpenBrace))
        {
            _Error(_MissingAfterPrevious(), DiagnosticCodes.ExpectedToken, "« { » attendu après « library: »", "« { » manquant ici",
                "par exemple : library: {windows: \"lib/x.lib\", linux: \"lib/libx.a\"}");
            return entries;
        }

        _Consume();
        while (!_Expect(TokenKind.CloseBrace) && !_Expect(TokenKind.EndOfFile))
        {
            if (!_Expect(TokenKind.Identifier) || _Future().Kind != TokenKind.Colon)
            {
                _Error(_Current.Span, DiagnosticCodes.ExpectedToken, "plateforme attendue, par exemple « windows: \"lib/x.lib\" »", "plateforme attendue ici");
                while (!_Expect(TokenKind.CloseBrace) && !_Expect(TokenKind.EndOfFile)) _Consume();
                break;
            }

            var platform = new Identifier(_Consume().Span);
            _Consume();   // « : »
            if (!_ExpectOneOf(TokenKind.StringLiteral, TokenKind.RawStringLiteral))
            {
                _Error(_MissingAfterPrevious(), DiagnosticCodes.ExpectedToken, $"chemin attendu après « {platform.Name}: »", "chemin manquant ici");
                break;
            }

            var path = _Consume().Span;
            entries.Add(new LibraryEntry(platform.Span.Union(path), platform, path));
            if (_Expect(TokenKind.Comma)) _Consume();
        }

        _ExpectOrReport(TokenKind.CloseBrace, "pour fermer library");
        return entries;
    }

    /// <summary>L'objet JSON de <c>metadata:</c>, jusqu'à son accolade fermante ; son contenu n'est pas interprété.</summary>
    private SourceSpan? _ParseMetadata()
    {
        if (!_Expect(TokenKind.OpenBrace))
        {
            _Error(_MissingAfterPrevious(), DiagnosticCodes.ExpectedToken, "objet JSON attendu après « metadata: »", "« { » manquant ici",
                "par exemple : metadata: {\"auteur\": \"Tommy\"}");
            return null;
        }

        var open = _Consume();
        var depth = 1;
        while (depth > 0 && !_Expect(TokenKind.EndOfFile))
        {
            var token = _Consume();
            if (token.Kind == TokenKind.OpenBrace) ++depth;
            else if (token.Kind == TokenKind.CloseBrace) --depth;
        }
        if (depth > 0) _Error(_MissingAfterPrevious(), DiagnosticCodes.ExpectedToken, "« } » attendu pour fermer les métadonnées", "« } » manquant ici");
        return open.Span.Union(_previous!.Span);
    }

    /// <summary>
    /// <c>Import := "import" [ "[..]" | "box" | "tools" ] Nom [ "as" Identificateur ] [ "only" "{" Nom { "," Nom } [ "," ] "}" ] ";"</c>,
    /// avec <c>Nom := Identificateur [ "as" Identificateur ]</c> ; pour <c>tools</c>, le fichier peut être <c>Dossier.fichier</c>.
    /// <c>import json nom;</c> et <c>import xml nom;</c> lisent des données à la compilation ;
    /// <c>import file "nom.ext" as contenu;</c> lit le premier fichier de ce nom comme une chaîne constante.
    /// </summary>
    /// <summary>Vrai si <c>use</c> (contextuel) commence un import de la std : <c>use &lt;Math&gt;;</c>.</summary>
    private bool _AtUse() => _Expect(TokenKind.Identifier, "use") && _Future().Kind == TokenKind.Less;

    private IStatement _ParseImport()
    {
        var keyword = _Consume();
        var kind = ImportKind.Plain;
        Identifier? stdName = null;
        if (keyword.Kind == TokenKind.Identifier)
        {
            // use <Nom> : le fichier ou la box de la std de ce nom, sans dire lequel.
            kind = ImportKind.Std;
            _Consume();
            stdName = _ParseName("nom de la std", "par exemple : use <Math>;");
            _ExpectOrReport(TokenKind.Greater, "pour fermer le nom de la std", "par exemple : use <Math>; ; seul l'index d'une box est visible, pas un de ses fichiers");
        }
        else if (_Expect(TokenKind.OpenBracket) && _Future().Kind == TokenKind.DotDot && _Future(2).Kind == TokenKind.CloseBracket)
        {
            _Consume();
            _Consume();
            _Consume();
            kind = ImportKind.Outside;
        }
        else if (_Expect(TokenKind.Identifier) && _Current.Text is "box" or "tools" && _Future().Kind == TokenKind.Identifier)
            kind = _Consume().Text == "box" ? ImportKind.Box : ImportKind.Tools;
        else if (_Expect(TokenKind.Identifier) && _Current.Text is "json" or "xml" && _Future().Kind == TokenKind.Identifier)
        {
            // import json settings; : le fichier settings.json, lu à la compilation ; import xml layout; : layout.xml.
            kind = _Consume().Text == "json" ? ImportKind.Json : ImportKind.Xml;
        }
        else if (_Expect(TokenKind.Identifier, "file"))
            return _ParseFileImport(keyword, ImportKind.File);
        else if (_Expect(TokenKind.Identifier) && _Current.Text is "json" or "xml" && _Future().Kind is TokenKind.Identifier or TokenKind.OpenParen)
        {
            var format = _Consume();
            _diagnosticBag.Add(new Diagnostic(DiagnosticLevel.Error, $"import {format.Text} : pas encore pris en charge par le compilateur",
                Span: keyword.Span.Union(format.Span), Label: "pas encore pris en charge"));
            _SkipToSemicolon();
            return new ImportDeclaration(keyword.Span.Union(_previous!.Span), new Identifier(format.Span), null);
        }

        // Le modificateur de portée précède le genre, comme pour un import classique :
        // import [..] file "commun.html" as page;
        if (kind == ImportKind.Outside && _Expect(TokenKind.Identifier, "file"))
            return _ParseFileImport(keyword, ImportKind.FileOutside);

        var module = stdName is not null ? stdName : kind == ImportKind.Tools
            ? _ParseQualifiedName("nom de fichier", "par exemple : import tools Vectors; ou import tools Vectors.Vec2;")
            : _ParseName("nom de fichier", "un import s'écrit par exemple : import Commandes;");
        var moduleAlias = _ExpectAndConsume(TokenKind.AsKeyword) ? _ParseName("nouveau nom", "par exemple : import Commandes as cmd;") : null;
        List<ImportName>? only = null;

        if (_Expect(TokenKind.Identifier, "only"))
        {
            _Consume();
            only = [];
            if (_ExpectOrReport(TokenKind.OpenBrace, "après « only »", "les noms choisis s'écrivent entre accolades : import Commandes only {ajouter, retirer as del};"))
            {
                while (!_ExpectOneOf(TokenKind.CloseBrace, TokenKind.Semicolon, TokenKind.EndOfFile))
                {
                    var name = _ParseName("nom importé", "par exemple : import Commandes only {ajouter as add};");
                    Identifier? alias = null;
                    if (_ExpectAndConsume(TokenKind.AsKeyword)) alias = _ParseName("nouveau nom", "par exemple : import Commandes only {ajouter as add};");
                    only.Add(new ImportName(alias is null ? name.Span : name.Span.Union(alias.Span), name, alias));
                    if (!_ExpectAndConsume(TokenKind.Comma)) break;
                }
                _ExpectOrReport(TokenKind.CloseBrace, "pour fermer les noms importés");
            }
        }

        if (!_ExpectOrReport(TokenKind.Semicolon, "à la fin de l'import")) _SkipToSemicolon();
        return new ImportDeclaration(keyword.Span.Union(_previous!.Span), module, only, kind, moduleAlias);
    }

    /// <summary><c>file "nom.ext" as constante;</c>, après que <c>import</c> et l'éventuel <c>[..]</c> ont été lus.</summary>
    private ImportDeclaration _ParseFileImport(Token keyword, ImportKind kind)
    {
        _Consume();
        if (!_ExpectOneOf(TokenKind.StringLiteral, TokenKind.RawStringLiteral))
        {
            var missing = _MissingAtCurrent();
            _Error(missing, DiagnosticCodes.ExpectedToken, "nom de fichier chaîne attendu après « import file »", "nom manquant ici",
                "par exemple : import file \"index.html\" as page;");
            _SkipToSemicolon();
            return new ImportDeclaration(keyword.Span.Union(_previous!.Span), new Identifier(missing), null, kind);
        }
        var file = _Consume();
        Identifier? alias = null;
        if (_ExpectAndConsume(TokenKind.AsKeyword)) alias = _ParseName("nom de la constante", "par exemple : import file \"index.html\" as page;");
        else _Error(_MissingAtCurrent(), DiagnosticCodes.ExpectedToken, "« as nom » attendu après le fichier", "alias manquant ici",
            "le contenu devient une constante : import file \"index.html\" as page;");
        if (!_ExpectOrReport(TokenKind.Semicolon, "à la fin de l'import")) _SkipToSemicolon();
        return new ImportDeclaration(keyword.Span.Union(_previous!.Span), new Identifier(file.Span), null, kind, alias);
    }

    /// <summary>
    /// Vrai si <c>export</c> (contextuel) commence un export : suivi de <c>{</c>, d'un nom et de <c>;</c>, ou d'une déclaration
    /// que l'export peut précéder.
    /// </summary>
    private bool _AtExport()
    {
        if (!_Expect(TokenKind.Identifier, "export")) return false;
        var next = _Future();
        return next.Kind is TokenKind.OpenBrace or TokenKind.GearKeyword or TokenKind.GuardKeyword or TokenKind.EmitKeyword or TokenKind.MoldKeyword
                or TokenKind.EnumKeyword or TokenKind.IngotKeyword or TokenKind.ConstKeyword or TokenKind.TrainKeyword
            || (next.Kind == TokenKind.Identifier && (_Future(2).Kind == TokenKind.Semicolon || next.Text is "error" or "dataset"));
    }

    /// <summary>
    /// <c>Export := "export" ( "{" Nom { "," Nom } [ "," ] "}" | Identificateur | Déclaration ) ";"</c>, avec
    /// <c>Nom := [ "..." ] Identificateur</c>. Devant une déclaration, l'export ne porte que son nom et n'a pas de <c>;</c> à lui.
    /// </summary>
    private void _ParseExport(RArray<IStatement> statements)
    {
        var keyword = _Consume();
        List<ExportName> names = [];

        if (_ExpectAndConsume(TokenKind.OpenBrace))
        {
            while (!_ExpectOneOf(TokenKind.CloseBrace, TokenKind.Semicolon, TokenKind.EndOfFile))
            {
                var spread = _Expect(TokenKind.DotDotDot) ? _Consume() : null;
                var name = _ParseName("nom exporté", "par exemple : export {query, Row};");
                names.Add(new ExportName(spread is null ? name.Span : spread.Span.Union(name.Span), name, spread is null ? ExportKind.Name : ExportKind.Spread));
                if (!_ExpectAndConsume(TokenKind.Comma)) break;
            }
            _ExpectOrReport(TokenKind.CloseBrace, "pour fermer les noms exportés");
        }
        else if (_Expect(TokenKind.Identifier) && _Future().Kind == TokenKind.Semicolon && _Current.Text != "error")
        {
            var module = _ParseName("nom exporté", "par exemple : export Vectors;");
            names.Add(new ExportName(module.Span, module, ExportKind.Module));
        }
        else
        {
            _ParseExportedDeclaration(keyword, statements);
            return;
        }

        if (!_ExpectOrReport(TokenKind.Semicolon, "à la fin de l'export")) _SkipToSemicolon();
        statements.Push(new ExportDeclaration(keyword.Span.Union(_previous!.Span), names));
    }

    /// <summary>La déclaration qui suit <c>export</c> ; son nom est exporté.</summary>
    private void _ParseExportedDeclaration(Token keyword, RArray<IStatement> statements)
    {
        RArray<IStatement> declared = new();
        if (_Expect(TokenKind.TrainKeyword) && _Future().Kind == TokenKind.Identifier && _Future(2).Kind == TokenKind.OpenBrace)
            declared.Push(_ParseTrainBlock());
        else if (!_TryParseDeclaration(declared) || declared.Count() == 0)
        {
            _Error(_Current.Span, DiagnosticCodes.ExpectedToken, "« export » doit précéder une déclaration", "pas une déclaration",
                "export s'écrit devant un gear, un guard, un emit, un mold, une error, un enum, un ingot, une constante ou un train nommé, ou avec des accolades : export {a, b};");
            _SkipToSemicolon();
            return;
        }

        foreach (var statement in declared)
        {
            var name = statement switch
            {
                GearDeclaration gear => gear.Name,
                MoldDeclaration mold => mold.Name,
                EnumDeclaration enumeration => enumeration.Name,
                IngotDeclaration ingot => ingot.Name,
                VarDeclaration { IsConst: true } constant => constant.Name,
                DatasetDeclaration dataset => dataset.Name,
                TrainBlock { Name: { } train } => train,
                _ => null,
            };
            if (name is null)
                _Error(statement.Span, DiagnosticCodes.ExpectedToken, "« export » ne s'applique pas à cette déclaration", "pas exportable",
                    "export s'écrit devant un gear, un guard, un emit, un mold, une error, un enum, un ingot, une constante ou un train nommé");
            else statements.Push(new ExportDeclaration(keyword.Span.Union(statement.Span), [new ExportName(name.Span, name)], IsInline: true));
            statements.Push(statement);
        }
    }

    #endregion

    /// <summary>Signale (E034) ce qui suit le train sans être une déclaration, puis le saute jusqu'au prochain <c>;</c>.</summary>
    private void _SkipUnexpectedAfterTrain()
    {
        if (_ExpectOneOf(TokenKind.TrainKeyword, TokenKind.Bang))
        {
            _Error(_Current.Span, DiagnosticCodes.UnexpectedAfterTrain, "contenu inattendu après le train", "second train",
                "un fichier ne contient qu'un seul train, terminé par un unique « ; »");
            // Le début du second train, que la resynchronisation laisserait en place.
            while (_ExpectOneOf(TokenKind.TrainKeyword, TokenKind.Bang)) _Consume();
        }
        else
            _Error(_Current.Span, DiagnosticCodes.UnexpectedAfterTrain, "contenu inattendu après le train", "ni un gear, ni le train",
                "à part son train, un fichier contient des constantes (const) et des gears");

        _SkipToSemicolon();
    }

    /// <summary>
    /// <c>Train := [ "!" ] "train" [ Paramètres ] [ ValeurDeDépart ] Pipeline { Rail } ";"</c>.
    /// Un élément manquant est signalé puis remplacé par un nœud manquant ; si le <c>;</c> final
    /// manque, le parseur se resynchronise sur le prochain <c>;</c>.
    /// </summary>
    private TrainDeclaration _ParseTrainDeclaration()
    {
        _inTrain = true;
        var first = _Current;
        var isUnguarded = _ExpectAndConsume(TokenKind.Bang);

        if (!_ExpectAndConsume(TokenKind.TrainKeyword))
            _Error(_MissingAtCurrent(), DiagnosticCodes.ExpectedToken, "« train » attendu", "« train » manquant ici",
                "un fichier .rgl contient un train, par exemple : train |> done;");

        var parameters = _Expect(TokenKind.OpenParen) ? _ParseParameters("du train") : null;
        if (_ExpectAndConsume(TokenKind.Colon))
        {
            var result = _ParseType();
            _ExpectOrReport(TokenKind.Semicolon, "après la déclaration du train C++");
            _inTrain = false;
            return new TrainDeclaration(first.Span.Union(_previous!.Span), isUnguarded, parameters, null,
                new Pipeline(first.Span, []), result);
        }
        // Lue au-dessus du pipeline : son « |> » commence les étapes du train.
        var startValue = _ExpectOneOf(TokenKind.Pipe, TokenKind.EachPipe, TokenKind.EachStrictPipe, TokenKind.FoldPipe, TokenKind.Semicolon, TokenKind.EndOfFile) ? null : _ParseExpression(PipelinePrecedence);
        var pipeline = _ParsePipeline(startValue);

        if (!_ExpectOrReport(TokenKind.Semicolon, "à la fin du train", "un train, rails compris, se termine par un unique « ; »"))
            _SkipToSemicolon();
        _inTrain = false;

        var last = _previous ?? first;
        return new TrainDeclaration(first.Span.Union(last.Span), isUnguarded, parameters, startValue, pipeline);
    }
    #region Paramètres et types

    /// <summary><c>Paramètres := "(" [ Paramètre { "," Paramètre } ] ")"</c>.</summary>
    /// <param name="owner">Ce qui les déclare, pour les messages : « du train », « de twice ».</param>
    private IReadOnlyList<Parameter> _ParseParameters(string owner)
    {
        _Consume();
        RArray<Parameter> parameters = new();

        if (!_Expect(TokenKind.CloseParen))
        {
            do parameters.Push(_ParseParameter(_allowThis && parameters.Count() == 0));
            while (_ExpectAndConsume(TokenKind.Comma));
        }

        _ExpectOrReport(TokenKind.CloseParen, $"pour fermer les paramètres {owner}");
        return [.. parameters];
    }

    #region Enums

    /// <summary>
    /// <c>Enum := "enum" Identificateur [ ":" Type ] "{" Variante { "," Variante } [ "," ] "}"</c>, avec
    /// <c>Variante := Identificateur [ ":" Expression ]</c>.
    /// </summary>
    private EnumDeclaration _ParseEnum()
    {
        var keyword = _Consume();
        var name = _ParseName("nom d'enum", "un enum s'écrit par exemple : enum Direction { North, South }");
        var underlying = _ExpectAndConsume(TokenKind.Colon) ? _ParseType() : null;
        RArray<EnumVariant> variants = new();

        if (_ExpectOrReport(TokenKind.OpenBrace, "pour ouvrir les variantes de l'enum", "un enum s'écrit par exemple : enum Direction { North, South }"))
        {
            while (!_ExpectOneOf(TokenKind.CloseBrace, TokenKind.EndOfFile) && !_AtDeclarationStart())
            {
                var variant = _ParseName("nom de variante", "les variantes se séparent par « , » : enum Direction { North, South }");
                var value = _ExpectAndConsume(TokenKind.Colon) ? _ParseExpression() : null;
                variants.Push(new EnumVariant(variant.Span.Union(_previous!.Span), variant, value));
                if (!_ExpectAndConsume(TokenKind.Comma)) break;
            }
            if (!_ExpectOrReport(TokenKind.CloseBrace, "pour fermer l'enum", "les variantes se séparent par « , »")) _SkipToCloseBrace();
        }

        return new EnumDeclaration(keyword.Span.Union(_previous!.Span), name, underlying, [.. variants]);
    }

    #endregion

    #region Datasets

    /// <summary>Vrai si <c>dataset</c> (contextuel) commence un dataset : un nom, puis <c>of</c>, <c>as</c> ou <c>from</c>.</summary>
    private bool _AtDataset() =>
        _Expect(TokenKind.Identifier, "dataset") && _Future().Kind == TokenKind.Identifier
        && (_Future(2).Kind == TokenKind.AsKeyword || (_Future(2).Kind == TokenKind.Identifier && _Future(2).Text is "of" or "from"));

    /// <summary>
    /// <c>Dataset := "dataset" Nom [ "from" Chaîne ] ( "of" | "as" ) Mold ( "{" { Nom ":" Valeur [ "," ] } "}" | ";" )</c> : <c>of</c> lit les
    /// entrées comme un mold déclaré, <c>as</c> crée ce mold depuis elles. Avec <c>from</c>, les entrées sont dans le fichier de données
    /// et la déclaration finit par <c>;</c>.
    /// </summary>
    private DatasetDeclaration _ParseDataset()
    {
        var keyword = _Consume();
        const string example = "un dataset s'écrit par exemple : dataset Moves of Move { attaquer: {name: 'Attaquer', power: 5} }";
        var name = _ParseName("nom de dataset", example);

        LiteralExpression? source = null;
        if (_Expect(TokenKind.Identifier, "from"))
        {
            _Consume();
            if (_Expect(TokenKind.StringLiteral) || _Expect(TokenKind.RawStringLiteral)) source = new LiteralExpression(_Current.Span, _Consume().Kind);
            else _Error(_Current.Span, DiagnosticCodes.ExpectedToken, "le nom du fichier de données est attendu après « from »", "chaîne attendue",
                "par exemple : dataset Moves from \"moves.json\" of Move;");
        }

        var createsMold = _Expect(TokenKind.AsKeyword);
        Identifier mold;
        if (createsMold || _Expect(TokenKind.Identifier, "of"))
        {
            _Consume();
            mold = _ParseName("nom de mold", "« of Move » lit les entrées comme un mold déclaré, « as Move » crée ce mold depuis elles");
        }
        else
        {
            _Error(_Current.Span, DiagnosticCodes.ExpectedToken, "« of » ou « as » est attendu avant le mold du dataset", "mold manquant",
                "« of Move » lit les entrées comme un mold déclaré, « as Move » crée ce mold depuis elles");
            mold = new Identifier(new SourceSpan(_Current.Span.Source, _Current.Span.Start, 0u));
        }

        RArray<DatasetEntry> entries = new();
        if (source is not null) _ExpectOrReport(TokenKind.Semicolon, "à la fin du dataset", example);
        else if (_ExpectOrReport(TokenKind.OpenBrace, "pour ouvrir les entrées du dataset", example))
        {
            _ParseDatasetEntries(entries);
            _ExpectOrReport(TokenKind.CloseBrace, "pour fermer les entrées du dataset");
        }

        return new DatasetDeclaration(keyword.Span.Union(_previous!.Span), name, mold, createsMold, source, [.. entries]);
    }

    /// <summary>Les entrées <c>nom: valeur</c> d'un dataset ou de son extension, jusqu'à l'accolade fermante (non consommée).</summary>
    private void _ParseDatasetEntries(RArray<DatasetEntry> entries)
    {
        while (!_ExpectOneOf(TokenKind.CloseBrace, TokenKind.EndOfFile) && !_AtDeclarationStart())
        {
            var entryName = _ParseName("nom d'entrée", "chaque entrée s'écrit : nom: valeur");
            if (!_ExpectOrReport(TokenKind.Colon, "après le nom de l'entrée", "chaque entrée s'écrit : nom: {champ: valeur}")) { _SkipToSemicolon(); break; }
            var value = _ParseExpression();
            entries.Push(new DatasetEntry(entryName.Span.Union(value.Span), entryName, value));
            if (!_ExpectAndConsume(TokenKind.Comma)) break;
        }
    }

    /// <summary>Vrai si <c>extend dataset Nom {</c> commence : les entrées qu'un autre fichier ajoute à un dataset.</summary>
    private bool _AtDatasetExtension() =>
        _Expect(TokenKind.Identifier, "extend") && _Future().Kind == TokenKind.Identifier && _Future().Text == "dataset"
        && _Future(2).Kind == TokenKind.Identifier && _Future(3).Kind == TokenKind.OpenBrace;

    /// <summary><c>"extend" "dataset" Nom "{" { Nom ":" Valeur [ "," ] } "}"</c>.</summary>
    private DatasetExtension _ParseDatasetExtension()
    {
        var keyword = _Consume();
        _Consume();
        var name = _ParseName("nom de dataset", "une extension s'écrit par exemple : extend dataset Moves { braise: {name: 'Braise', power: 9} }");
        RArray<DatasetEntry> entries = new();
        if (_ExpectOrReport(TokenKind.OpenBrace, "pour ouvrir les entrées ajoutées", "une extension s'écrit par exemple : extend dataset Moves { braise: {name: 'Braise', power: 9} }"))
        {
            _ParseDatasetEntries(entries);
            _ExpectOrReport(TokenKind.CloseBrace, "pour fermer les entrées ajoutées");
        }
        return new DatasetExtension(keyword.Span.Union(_previous!.Span), name, [.. entries]);
    }

    #endregion

    #region Precasts

    /// <summary>
    /// <c>Precast := "precast" [ Type ] Nom "{" { Champ ( ";" | "," ) } "}"</c>. Avec un type avant le nom (importé, ou
    /// anonyme <c>{…}</c>), le corps donne les valeurs <c>champ: valeur</c> ; sans type, c'est le corps qui déclare le mold,
    /// par ses champs <c>Type champ: valeur</c>.
    /// </summary>
    private PrecastDeclaration _ParsePrecast()
    {
        var keyword = _Consume();
        const string example = "un precast s'écrit par exemple : precast Vec2 Zero { x: 0.0, y: 0.0 }";
        // « precast Nom { » : le mold est déclaré par les champs ; sinon un type, puis le nom.
        var declaresMold = _Expect(TokenKind.Identifier) && _Future().Kind == TokenKind.OpenBrace;
        var type = declaresMold ? null : _ParseType();
        var name = _ParseName("nom de precast", example);
        RArray<MoldFieldValue> values = new();
        RArray<MoldField> fields = new();

        if (_ExpectOrReport(TokenKind.OpenBrace, "pour ouvrir le corps du precast", example))
        {
            while (!_ExpectOneOf(TokenKind.CloseBrace, TokenKind.EndOfFile) && !_AtDeclarationStart())
            {
                if (declaresMold)
                {
                    if (_ParseMoldMember(withDefault: true) is MoldField field) fields.Push(field);
                }
                else
                {
                    var fieldName = _ParseName("nom de champ", "chaque valeur s'écrit : champ: valeur");
                    _ExpectOrReport(TokenKind.Colon, "entre le champ et sa valeur", "chaque valeur s'écrit : champ: valeur");
                    var value = _ParseExpression();
                    values.Push(new MoldFieldValue(fieldName.Span.Union(value.Span), fieldName, value, false));
                }
                if (_ExpectAndConsume(TokenKind.Semicolon) || _ExpectAndConsume(TokenKind.Comma)) continue;
                if (!_Expect(TokenKind.CloseBrace) && !_ExpectOrReport(TokenKind.Semicolon, "après le champ", "les champs d'un precast se terminent par « ; » (ou se séparent par « , »)")) _SkipToSemicolon();
            }
            _ExpectOrReport(TokenKind.CloseBrace, "pour fermer le precast");
        }

        return new PrecastDeclaration(keyword.Span.Union(_previous!.Span), type, name, [.. values], [.. fields]);
    }

    #endregion

    #region Ingots

    /// <summary><c>Ingot := "ingot" Identificateur ":" Type ";"</c>.</summary>
    private IngotDeclaration _ParseIngot()
    {
        var keyword = _Consume();
        const string example = "un ingot s'écrit par exemple : ingot UserId: int;";
        var name = _ParseName("nom d'ingot", example);
        _ExpectOrReport(TokenKind.Colon, "avant le type de base de l'ingot", example);
        var type = _ParseType();
        if (!_ExpectOrReport(TokenKind.Semicolon, "après l'ingot", example)) _SkipToSemicolon();

        return new IngotDeclaration(keyword.Span.Union(_previous!.Span), name, type);
    }

    #endregion

    #region Génériques

    /// <summary>
    /// <c>Paramètres := "&lt;" Paramètre { "," Paramètre } "&gt;"</c>, avec <c>Paramètre := Identificateur [ ":" Type ] | TypePrimitif Identificateur</c> :
    /// un type sans contrainte, avec une contrainte (<c>number</c>, <c>error</c>, <c>{int x}</c>), ou une valeur connue à la compilation.
    /// </summary>
    private IReadOnlyList<TypeParameter> _ParseTypeParameters()
    {
        _Consume();
        RArray<TypeParameter> parameters = new();
        do
        {
            var first = _Current;
            if (SyntaxFacts.IsPrimitiveType(first.Kind))
            {
                var valueType = _ParseType();
                var valueName = _ParseName("nom du paramètre générique", "un paramètre de valeur s'écrit par exemple : gear borne<int max>(int v): int => …");
                parameters.Push(new TypeParameter(first.Span.Union(valueName.Span), valueName, null, valueType));
                continue;
            }

            var name = _ParseName("nom du paramètre générique", "un paramètre générique s'écrit par exemple : gear identity<T>(T val): T => val;");
            ITypeNode? constraint = _ExpectAndConsume(TokenKind.Colon) ? _ParseType() : null;
            parameters.Push(new TypeParameter(first.Span.Union(_previous!.Span), name, constraint, null));
        }
        while (_ExpectAndConsume(TokenKind.Comma));

        _ExpectOrReport(TokenKind.Greater, "pour fermer les paramètres génériques", "par exemple : gear identity<T>(T val): T => val;");
        return [.. parameters];
    }

    /// <summary>
    /// <c>Arguments := "&lt;" Argument { "," Argument } "&gt;"</c> : des types, ou des valeurs pour les paramètres de valeur.
    /// </summary>
    /// <returns>Les arguments, et le <c>&gt;</c> qui les ferme.</returns>
    private (IReadOnlyList<ITypeNode> Arguments, SourceSpan End) _ParseTypeArguments()
    {
        _Consume();
        RArray<ITypeNode> arguments = new();
        do
        {
            // Au-dessus des comparaisons : le « > » final n'est pas lu comme un opérateur.
            if (_Current.Kind is TokenKind.IntegerLiteral or TokenKind.FloatLiteral or TokenKind.TrueKeyword or TokenKind.FalseKeyword or TokenKind.StringLiteral or TokenKind.Minus)
                arguments.Push(new ValueTypeArgument(_ParseExpression(SyntaxFacts.PrefixPrecedence - 1)));
            else arguments.Push(_ParseType());
        }
        while (_ExpectAndConsume(TokenKind.Comma));

        var end = _Current.Span;
        _ExpectOrReport(TokenKind.Greater, "pour fermer les arguments génériques", "par exemple : Pair<int, string>");
        return ([.. arguments], end);
    }

    #endregion

    #region Molds

    /// <summary>
    /// <c>Mold := "mold" Identificateur [ "-&gt;" Identificateur ] "{" { Membre } "}"</c> (le nom après la flèche est le gear de fin), avec
    /// <c>Membre := Type Identificateur [ ":" Expression ] ";" | "..." Type ";"</c>.
    /// </summary>
    /// <param name="isError">Vrai pour <c>error Nom { … }</c>, un type d'erreur.</param>
    private MoldDeclaration _ParseMold(bool isError = false)
    {
        var keyword = _Consume();
        var name = _ParseName("nom de mold", "un mold s'écrit par exemple : mold Vec2 { float x; float y; }");
        var typeParameters = _Expect(TokenKind.Less) ? _ParseTypeParameters() : null;
        var destructor = _ExpectAndConsume(TokenKind.Arrow)
            ? _ParseQualifiedName("nom de gear de fin", "le gear de fin d'un mold s'écrit après la flèche, par exemple : mold Handle -> fermer { int id; }")
            : null;
        RArray<IMoldMember> members = new();

        if (_ExpectOrReport(TokenKind.OpenBrace, "pour ouvrir le corps du mold", "un mold s'écrit par exemple : mold Vec2 { float x; float y; }"))
        {
            while (!_ExpectOneOf(TokenKind.CloseBrace, TokenKind.EndOfFile) && !_AtDeclarationStart())
            {
                var member = _ParseMoldMember(withDefault: true);
                members.Push(member);
                if (member is MoldOperator) continue;
                if (!_ExpectOrReport(TokenKind.Semicolon, "après le champ", "chaque champ d'un mold se termine par « ; »")) _SkipToSemicolon();
            }
            _ExpectOrReport(TokenKind.CloseBrace, "pour fermer le mold");
        }

        return new MoldDeclaration(keyword.Span.Union(_previous!.Span), name, [.. members], isError, typeParameters, Destructor: destructor);
    }

    /// <summary>
    /// Un membre de mold : <c>Type nom [: défaut]</c>, <c>...Type</c>, ou, dans un mold déclaré,
    /// <c>[pure] Type opérateur(paramètres) { … }</c> (<c>implicit</c> et <c>explicit</c> sont contextuels).
    /// </summary>
    /// <param name="withDefault">Vrai dans un mold déclaré, où un champ peut avoir une valeur par défaut, un opérateur ou une conversion.</param>
    private IMoldMember _ParseMoldMember(bool withDefault)
    {
        var (isPure, isMutable) = (false, false);
        SourceSpan? start = null;
        if (withDefault && _Expect(TokenKind.PureKeyword) && _Future().Kind != TokenKind.OpenParen)
        {
            start = _Consume().Span;
            isPure = true;
        }
        else if (withDefault && _Expect(TokenKind.MutKeyword))
        {
            start = _Consume().Span;
            isMutable = true;
        }

        if (_Expect(TokenKind.DotDotDot))
        {
            var dots = _Consume();
            var source = _ParseType();
            return new MoldSpread(dots.Span.Union(source.Span), source);
        }

        var type = _ParseType();
        if (withDefault && _AtMoldOperator()) return _ParseMoldOperator(start ?? type.Span, isPure, type, isMutable);

        if (isMutable) _Error(start!.Value, DiagnosticCodes.ExpectedToken, "« mut » ne précède qu'un opérateur d'index dans un mold", "mut inattendu", "l'écriture d'un index s'écrit : mut T [int i] { … }");
        var name = _ParseName("nom de champ", "un champ s'écrit avec son type puis son nom, par exemple : float x;");
        IExpression? value = withDefault && _ExpectAndConsume(TokenKind.Colon) ? _ParseExpression() : null;
        return new MoldField(type.Span.Union(_previous!.Span), type, name, value);
    }

    /// <summary>
    /// Vrai si le token courant, après le type d'un membre, est un opérateur surchargeable ou
    /// <c>implicit</c> / <c>explicit</c> suivi de <c>(</c> : le membre est un opérateur, pas un champ.
    /// </summary>
    private bool _AtMoldOperator() =>
        _Expect(TokenKind.OpenBracket)
        || _Future().Kind == TokenKind.OpenParen
        && (_ExpectOneOf(TokenKind.Plus, TokenKind.Minus, TokenKind.Star, TokenKind.Slash, TokenKind.Percent, TokenKind.Less, TokenKind.Greater, TokenKind.EqKeyword,
                TokenKind.BitAndKeyword, TokenKind.BitOrKeyword, TokenKind.BitXorKeyword, TokenKind.ShlKeyword, TokenKind.ShrKeyword)
            || _Expect(TokenKind.Identifier, "implicit") || _Expect(TokenKind.Identifier, "explicit"));

    /// <summary><c>Opérateur := [ "pure" ] Type Opérateur Paramètres Bloc</c>, le type et <c>pure</c> déjà lus.</summary>
    private MoldOperator _ParseMoldOperator(SourceSpan start, bool isPure, ITypeNode returnType, bool isMutable)
    {
        // L'opérateur d'index : [Type nom] au lieu de (paramètres).
        if (_Expect(TokenKind.OpenBracket))
        {
            var open = _Consume();
            var index = _ParseParameter(false);
            _ExpectOrReport(TokenKind.CloseBracket, "pour fermer le paramètre de l'opérateur d'index", "par exemple : pure int [int i] { … }");
            var indexBody = _ParseBlock();
            return new MoldOperator(start.Union(_previous!.Span), isPure, returnType, "[]", open.Span, [index], indexBody, isMutable);
        }

        var token = _Consume();
        var parameters = _ParseParameters($"de « {token.Text} »");
        var body = _ParseBlock();
        return new MoldOperator(start.Union(_previous!.Span), isPure, returnType, token.Text, token.Span, parameters, body);
    }

    /// <summary><c>TypeAnonyme := "{" [ Membre { "," Membre } ] "}"</c>, sans valeur par défaut.</summary>
    private AnonymousMoldType _ParseAnonymousMoldType()
    {
        var open = _Consume();
        RArray<IMoldMember> members = new();
        if (!_Expect(TokenKind.CloseBrace))
        {
            do members.Push(_ParseMoldMember(withDefault: false));
            while (_ExpectAndConsume(TokenKind.Comma) && !_Expect(TokenKind.CloseBrace));
        }
        _ExpectOrReport(TokenKind.CloseBrace, "pour fermer le type mold", "un type mold anonyme s'écrit par exemple : {int x, int y}");
        return new AnonymousMoldType(open.Span.Union(_previous!.Span), [.. members]);
    }

    /// <summary>
    /// Indique si l'instruction courante affecte un champ : <c>nom.champ { .champ } &lt;- …</c>.
    /// </summary>
    private bool _IsFieldAssignment()
    {
        // nom suivi d'une suite de « .champ » et de « [index] », puis d'une affectation.
        var offset = 1;
        while (true)
        {
            var kind = _Future(offset).Kind;
            if (kind == TokenKind.Dot && _Future(offset + 1).Kind == TokenKind.Identifier) offset += 2;
            else if (kind == TokenKind.OpenBracket)
            {
                var depth = 0;
                do
                {
                    var current = _Future(offset).Kind;
                    if (current is TokenKind.EndOfFile or TokenKind.Semicolon) return false;
                    if (current == TokenKind.OpenBracket) ++depth;
                    else if (current == TokenKind.CloseBracket) --depth;
                    ++offset;
                }
                while (depth > 0);
            }
            else break;
        }
        return offset > 1 && _IsAssignOperator(_Future(offset).Kind);
    }

    /// <summary>
    /// <c>nom { .champ | [index] } (« &lt;- » | « +&lt;- »…) Expression ";"</c> : modifier un champ ou un
    /// élément en place, ce que seule une variable <c>mut</c> permet (le binder le vérifie).
    /// </summary>
    private FieldAssignmentStatement _ParseFieldAssignment()
    {
        var target = _ParseExpression(_AssignPrecedence);
        var op = _Consume();
        var value = _ParseExpression();
        if (!_ExpectOrReport(TokenKind.Semicolon, "à la fin de l'affectation")) _SkipToSemicolon();
        return new FieldAssignmentStatement(target.Span.Union(_previous!.Span), target, op.Kind, value);
    }

    #endregion

    /// <summary><c>Paramètre := [ "mut" ] Type Identificateur</c>, par exemple <c>[string] args</c>.</summary>
    /// <param name="allowThis">Vrai pour le premier paramètre d'un gear d'extension, que <c>this</c> peut précéder.</param>
    private Parameter _ParseParameter(bool allowThis = false)
    {
        // « this » est contextuel : un nom de type le suit, jamais « , » ni « ) ».
        var self = allowThis && _Expect(TokenKind.Identifier, "this") && !_ExpectOneOf(TokenKind.Comma, TokenKind.CloseParen) && _Future().Kind != TokenKind.Comma && _Future().Kind != TokenKind.CloseParen
            ? _Consume() : null;
        var mut = _Expect(TokenKind.MutKeyword) ? _Consume() : null;
        // « params » est contextuel : suivi d'un « [ », il annonce un paramètre qui reçoit tous les arguments restants.
        var variadic = _Expect(TokenKind.Identifier, "params") && _Future().Kind == TokenKind.OpenBracket ? _Consume() : null;
        var type = _ParseType();
        var name = _ParseName("nom de paramètre", "un paramètre s'écrit avec son type puis son nom, par exemple : [string] args");
        // Valeur par défaut : « string b: "x" », n'importe quelle expression.
        var @default = _ExpectAndConsume(TokenKind.Colon) ? _ParseExpression() : null;

        return new Parameter((self?.Span ?? mut?.Span ?? variadic?.Span ?? type.Span).Union(@default?.Span ?? name.Span), type, name, mut is not null, self is not null, @default, variadic is not null);
    }

    /// <summary>Vrai pendant la lecture d'un gear d'extension : son premier paramètre peut être <c>this</c>.</summary>
    private bool _allowThis;

    /// <summary>Vrai si <c>namespace</c> (contextuel) commence une déclaration : un nom le suit.</summary>
    private bool _AtNamespace() =>
        _Expect(TokenKind.Identifier, "namespace") && _Future().Kind is not (TokenKind.Colon or TokenKind.ColonColon or TokenKind.Semicolon or TokenKind.Assign or TokenKind.MoveAssign or TokenKind.EndOfFile);

    /// <summary><c>Namespace := "namespace" Nom { "/" Nom } ";"</c>, les caractères permis étant lettres, chiffres, <c>_</c> et <c>-</c>.</summary>
    private NamespaceDeclaration _ParseNamespace()
    {
        var keyword = _Consume();
        var first = _Current;
        var any = false;
        while (!_ExpectOneOf(TokenKind.Semicolon, TokenKind.EndOfFile))
        {
            _Consume();
            any = true;
        }
        var text = any ? first.Span.Union(_previous!.Span).Text : "";
        var span = keyword.Span.Union(_previous!.Span);
        if (!_IsNamespaceName(text))
            _Error(span, DiagnosticCodes.InvalidNamespace, "un namespace s'écrit avec des lettres, des chiffres, _ et -, séparés par /", "namespace invalide",
                "par exemple : namespace Mon/Namespace;");
        _ExpectOrReport(TokenKind.Semicolon, "pour terminer le namespace");
        return new NamespaceDeclaration(span, text);
    }

    /// <summary>Vrai si <c>extend</c> (contextuel) commence une extension : un type, puis <c>{</c>.</summary>
    private bool _AtExtend() =>
        _Expect(TokenKind.Identifier, "extend") && _SkipExtendHeader() is var after && after >= 0 && _Future(after).Kind == TokenKind.OpenBrace;

    /// <summary>La position qui suit <c>extend [&lt;T, …&gt;] Type</c>, -1 si ce n'est pas un en-tête d'extension.</summary>
    private int _SkipExtendHeader()
    {
        var at = 1;
        if (_Future(at).Kind == TokenKind.Less && (at = _SkipGroup(at, TokenKind.Less, TokenKind.Greater)) < 0) return -1;
        return _SkipType(at);
    }

    /// <summary>
    /// <c>Extension := "extend" Type "{" { Gear | Constante } "}"</c> : ce qu'elle contient s'ajoute au type, pour le
    /// compilateur seulement.
    /// </summary>
    private ExtendDeclaration _ParseExtend()
    {
        var keyword = _Consume();
        var typeParameters = _Expect(TokenKind.Less) ? _ParseTypeParameters() : null;
        var target = _ParseType();
        _Consume();   // « { »
        RArray<IStatement> members = new();

        while (!_ExpectOneOf(TokenKind.CloseBrace, TokenKind.EndOfFile))
        {
            if (_ExpectOneOf(TokenKind.GearKeyword, TokenKind.GuardKeyword, TokenKind.EmitKeyword, TokenKind.UnsafeKeyword))
            {
                _allowThis = true;
                members.Push(_ParseGear());
                _allowThis = false;
            }
            else if (_Expect(TokenKind.ConstKeyword)) members.Push(_ParseStatement());
            else
            {
                _Error(_Current.Span, DiagnosticCodes.ExpectedToken, "une extension ne contient que des gears et des constantes", "ni gear ni constante",
                    "par exemple : extend Vec2 { const Vec2 Zero: {x: 0.0, y: 0.0}; gear length(this Vec2 v): float => v.x; }");
                _SkipToSemicolon();
            }
        }

        _ExpectOrReport(TokenKind.CloseBrace, "pour fermer l'extension");
        return new ExtendDeclaration(keyword.Span.Union(_previous!.Span), typeParameters, target, [.. members]);
    }

    /// <summary><c>Type := TypeUnion [ "!" TypeUnion ]</c> : un type, ou un résultat <c>T ! E</c>.</summary>
    private ITypeNode _ParseType()
    {
        var value = _ParseUnionType();
        if (!_ExpectAndConsume(TokenKind.Bang)) return value;

        var error = _ParseUnionType();
        return new ResultType(value.Span.Union(error.Span), value, error);
    }

    /// <summary><c>TypeUnion := TypeNullable { "|" TypeNullable }</c>.</summary>
    private ITypeNode _ParseUnionType()
    {
        var first = _ParseNullableType();
        if (!_Expect(TokenKind.Bar)) return first;

        RArray<ITypeNode> members = new();
        members.Push(first);
        while (_ExpectAndConsume(TokenKind.Bar)) members.Push(_ParseNullableType());
        return new UnionType(first.Span.Union(_previous!.Span), [.. members]);
    }

    /// <summary>
    /// <c>TypeNullable := TypeSimple [ "?" ]</c> : un type, éventuellement nullable. <c>T??</c> est
    /// signalé (E077) : un type n'est nullable qu'une fois.
    /// </summary>
    private ITypeNode _ParseNullableType()
    {
        var type = _ParseSimpleType();
        if (_ExpectAndConsume(TokenKind.Question)) return new NullableType(type.Span.Union(_previous!.Span), type);

        if (_Expect(TokenKind.QuestionQuestion))
        {
            var marks = _Consume();
            _Error(marks.Span, DiagnosticCodes.InvalidNullable, "un type n'est nullable qu'une fois", "« ? » en trop",
                $"écris {type.Span.Text}?");
            return new NullableType(type.Span.Union(marks.Span), type);
        }

        return type;
    }

    /// <summary>
    /// <c>TypeSimple := TypePrimitif | Identificateur | "[" Type "]" | TypeAnonyme | "(" Type ")" | TypeLambda</c>,
    /// avec <c>TypeLambda := [ "pure" ] "(" [ Type { "," Type } ] ")" "=&gt;" Type</c>.
    /// </summary>
    private ITypeNode _ParseSimpleType()
    {
        if (_ExpectOneOf(TokenKind.PtrKeyword, TokenKind.UptrKeyword))
        {
            var first = _Consume();
            _ExpectOrReport(TokenKind.Less, "après ptr");
            var element = _ParseType();
            _ExpectOrReport(TokenKind.Greater, "après le type pointé");
            return new AST.Types.PointerType(first.Span.Union(_previous!.Span), element, first.Kind == TokenKind.PtrKeyword);
        }
        if (_Expect(TokenKind.OpenParen)) return _ParseParenthesizedType(null);
        if (_Expect(TokenKind.PureKeyword) && _Future().Kind == TokenKind.OpenParen) return _ParseParenthesizedType(_Consume());

        if (SyntaxFacts.IsPrimitiveType(_Current.Kind))
        {
            var keyword = _Consume();
            var primitive = new PrimitiveType(keyword.Span, keyword.Kind);
            return _AtBounds() ? _ParseBounds(primitive) : primitive;
        }

        if (_Expect(TokenKind.Identifier))
        {
            var named = _ParseQualifiedName("nom de type", "un type est un nom de mold, d'enum ou d'ingot");
            if (!_Expect(TokenKind.Less) || _SkipGroup(0, TokenKind.Less, TokenKind.Greater) < 0) return new NamedType(named);

            var (arguments, end) = _ParseTypeArguments();
            return new NamedType(named, arguments, end);
        }

        if (_Expect(TokenKind.OpenBrace)) return _ParseAnonymousMoldType();

        if (_Expect(TokenKind.OpenBracket)) return _ParseBracketType();

        var missing = new MissingType(_MissingAtCurrent());
        _Error(missing.Span, DiagnosticCodes.ExpectedType, "type attendu", "type manquant ici",
            "vous pouvez écrire un type primitif (int, string…), un nom de type (Profile), un tableau ([string]) ou un mold anonyme ({int x, int y})");
        return missing;
    }

    /// <summary>
    /// <c>"[" Type ( "]" | ";" Taille [ ".." Taille ] "]" | "+" "]" | ":" Type "]" )</c> : un tableau
    /// <c>[T]</c>, <c>[T;N]</c>, <c>[T;N..M]</c>, <c>[T+]</c>, ou un dictionnaire <c>[K:V]</c>.
    /// </summary>
    private ITypeNode _ParseBracketType()
    {
        var open = _Consume();
        var element = _ParseType();
        const string example = "un tableau s'écrit [int], [int;3], [int;1..5] ou [int+], un dictionnaire [string:int]";

        if (_ExpectAndConsume(TokenKind.Colon))
        {
            var value = _ParseType();
            _ExpectOrReport(TokenKind.CloseBracket, "pour fermer le type dictionnaire", example);
            return new DictionaryType(open.Span.Union(_previous!.Span), element, value);
        }

        var kind = ArrayTypeKind.Dynamic;
        IExpression? size = null, max = null;
        if (_ExpectAndConsume(TokenKind.Semicolon))
        {
            // Au-dessus des opérateurs : « .. » et « ] » terminent la taille.
            size = _ParseExpression(SyntaxFacts.PrefixPrecedence - 1);
            kind = ArrayTypeKind.Fixed;
            if (_ExpectAndConsume(TokenKind.DotDot))
            {
                max = _ParseExpression(SyntaxFacts.PrefixPrecedence - 1);
                kind = ArrayTypeKind.Bounded;
            }
        }
        else if (_ExpectAndConsume(TokenKind.Plus)) kind = ArrayTypeKind.NonEmpty;

        _ExpectOrReport(TokenKind.CloseBracket, "pour fermer le type tableau", example);
        return new ArrayType(open.Span.Union(_previous!.Span), element, kind, size, max);
    }

    /// <summary>
    /// <c>"(" Type ")"</c> regroupe un type ; <c>"(" Type [ nom ] "," Type [ nom ] { … } ")"</c> est un
    /// tuple, au moins deux éléments, chacun avec un nom facultatif ; suivies de <c>=&gt;</c> et d'un
    /// type de retour, les parenthèses donnent les paramètres d'une lambda : <c>(int, int) =&gt; int</c>,
    /// <c>() =&gt; void</c>, <c>pure (int) =&gt; bool</c>.
    /// </summary>
    /// <param name="pure">Le <c>pure</c> déjà lu devant les parenthèses : c'est alors forcément un type de lambda.</param>
    private ITypeNode _ParseParenthesizedType(Token? pure)
    {
        var open = _Consume();
        var start = pure?.Span ?? open.Span;
        // Entre les parenthèses, un type de lambda se lit partout.
        var returnTypeLevel = _inGearReturnType;
        _inGearReturnType = false;
        RArray<TupleTypeElement> elements = new();
        if (!_Expect(TokenKind.CloseParen))
        {
            do
            {
                var type = _ParseType();
                // Un nom d'élément ; sur le premier, seulement devant une virgule ou à la fin des paramètres
                // d'une lambda : « (int | string w) » sans sa « ) » reste un type entre parenthèses, mal fermé.
                var named = _Expect(TokenKind.Identifier)
                    && (elements.Length > 0 || _Future().Kind == TokenKind.Comma || (_Future().Kind == TokenKind.CloseParen && _Future(2).Kind == TokenKind.FatArrow));
                var name = named ? new Identifier(_Consume().Span) : null;
                elements.Push(new TupleTypeElement(name is null ? type.Span : type.Span.Union(name.Span), type, name));
            }
            while (_ExpectAndConsume(TokenKind.Comma));
        }

        _ExpectOrReport(TokenKind.CloseParen, "pour fermer le type", "des parenthèses groupent un type, (int | string)?, forment un tuple, (int, string), ou donnent les paramètres d'une lambda, (int) => bool");
        _inGearReturnType = returnTypeLevel;
        IReadOnlyList<TupleTypeElement> items = [.. elements];

        if (_Expect(TokenKind.FatArrow) && (pure is not null || !returnTypeLevel || _ArrowStartsReturnType()))
        {
            _Consume();
            var returnType = _ParseType();
            return new FunctionType(start.Union(returnType.Span), [.. items.Select(e => e.Type)], returnType, pure is not null);
        }
        if (pure is not null)
        {
            _ExpectOrReport(TokenKind.FatArrow, "après les paramètres du type de lambda", "par exemple : pure (int) => bool");
            return new FunctionType(start.Union(_previous!.Span), [.. items.Select(e => e.Type)], new MissingType(_MissingAtCurrent()), true);
        }

        if (items.Count == 0)
        {
            var empty = open.Span.Union(_previous!.Span);
            _Error(empty, DiagnosticCodes.ExpectedType, "type attendu entre les parenthèses", "parenthèses vides",
                "() n'est un type que pour une lambda sans paramètre : () => void");
            return new MissingType(empty);
        }
        if (items is [{ Name: null } single]) return single.Type;

        var span = open.Span.Union(_previous!.Span);
        if (items.Count == 1)
            _Error(span, DiagnosticCodes.ExpectedToken, "un tuple a au moins deux éléments", "un seul élément",
                "par exemple : (int x, int y)");
        return new TupleType(span, items);
    }

    /// <summary>
    /// Dans le type de retour d'un gear, indique si le <c>=&gt;</c> courant introduit le type de retour
    /// d'une lambda, et non le corps expression du gear : le corps en bloc <c>{</c> suit alors ce type.
    /// <c>gear f(): (int) =&gt; int { … }</c> renvoie une lambda, <c>gear f(): (int, string) =&gt; (1, "a");</c>
    /// un tuple ; un gear à corps expression qui renvoie une lambda met son type entre parenthèses :
    /// <c>gear f(): ((int) =&gt; int) =&gt; (x) =&gt; x;</c>. Un <c>{ … }</c> juste après la flèche est sauté
    /// comme une valeur (ou un mold anonyme).
    /// </summary>
    private bool _ArrowStartsReturnType()
    {
        var offset = 1;
        var first = true;
        while (true)
        {
            var kind = _Future(offset).Kind;
            switch (kind)
            {
                case TokenKind.EndOfFile or TokenKind.Semicolon or TokenKind.CloseParen or TokenKind.CloseBracket or TokenKind.CloseBrace:
                    return false;
                case TokenKind.OpenBrace when !first:
                    return true;
                case TokenKind.OpenParen or TokenKind.OpenBracket or TokenKind.OpenBrace:
                    offset = _AfterGroup(offset);
                    break;
                default:
                    ++offset;
                    break;
            }
            first = false;
        }
    }

    /// <summary>
    /// La position, comptée depuis le token courant, qui suit le groupe <c>( … )</c>, <c>[ … ]</c> ou
    /// <c>{ … }</c> ouvert à <paramref name="offset"/> ; la fin du fichier s'il n'est pas fermé.
    /// </summary>
    private int _AfterGroup(int offset)
    {
        var depth = 0;
        while (true)
        {
            switch (_Future(offset).Kind)
            {
                case TokenKind.EndOfFile:
                    return offset;
                case TokenKind.OpenParen or TokenKind.OpenBracket or TokenKind.OpenBrace:
                    ++depth;
                    break;
                case TokenKind.CloseParen or TokenKind.CloseBracket or TokenKind.CloseBrace:
                    if (--depth == 0) return offset + 1;
                    break;
            }
            ++offset;
        }
    }

    /// <summary>
    /// Indique si un <c>&lt;</c> commence des bornes : suivi d'un nombre (signé ou non) puis de
    /// <c>..</c>. Ailleurs, comme dans <c>x as int &lt; y</c>, c'est une comparaison. Dans
    /// <c>int&lt;-5..5&gt;</c>, le lexer voit <c>&lt;-</c> : c'est aussi le début de bornes.
    /// </summary>
    private bool _AtBounds()
    {
        var offset = _Expect(TokenKind.Assign) ? 1 : _Expect(TokenKind.Less) ? _Future().Kind == TokenKind.Minus ? 2 : 1 : 0;
        return offset > 0
            && _Future(offset).Kind is TokenKind.IntegerLiteral or TokenKind.FloatLiteral or TokenKind.Identifier
            && _Future(offset + 1).Kind == TokenKind.DotDot;
    }

    /// <summary><c>TypeBorné := TypePrimitif "&lt;" Borne ".." Borne "&gt;"</c>, le type déjà lu.</summary>
    private BoundedType _ParseBounds(PrimitiveType primitive)
    {
        var open = _Consume();
        // Au-dessus des comparaisons : le « > » final n'est pas lu comme un opérateur.
        var min = _ParseExpression(SyntaxFacts.PrefixPrecedence - 1);
        if (open.Kind == TokenKind.Assign)
        {
            // « <- » : le « - » est le signe de la borne basse.
            var minus = new SourceSpan(_source, open.Span.Start + RUint.One, RUint.One);
            min = new UnaryExpression(minus.Union(min.Span), TokenKind.Minus, min);
        }
        _ExpectOrReport(TokenKind.DotDot, "entre les deux bornes", $"par exemple : {primitive.Span.Text}<0..100>");
        var max = _ParseExpression(SyntaxFacts.PrefixPrecedence - 1);
        _ExpectOrReport(TokenKind.Greater, "pour fermer les bornes", $"par exemple : {primitive.Span.Text}<0..100>");
        return new BoundedType(primitive.Span.Union(_previous!.Span), primitive, min, max);
    }

    #endregion

    #region Pipeline, rails et étapes

    /// <summary>
    /// <c>Pipeline := Séparateur Étape { Séparateur Étape }</c>, le séparateur étant <c>|&gt;</c>, ou
    /// <c>||&gt;</c> et <c>|!|&gt;</c> devant une étape de parcours. Si le <c>|&gt;</c> manque et que la
    /// valeur de départ est en fait une étape (<c>train done;</c>), l'aide donne la bonne formulation.
    /// </summary>
    private Pipeline _ParsePipeline(IExpression? startValue)
    {
        if (!_ExpectOneOf(TokenKind.Pipe, TokenKind.EachPipe, TokenKind.EachStrictPipe, TokenKind.FoldPipe))
        {
            var missing = _MissingAfterPrevious();
            var help = startValue is NameExpression { Name.Name: "done" }
                ? "la bonne formulation est : train |> done;"
                : "un train contient au moins une étape, par exemple : train |> done;";
            _Error(missing, DiagnosticCodes.ExpectedToken, "« |> » attendu avant la première étape", "« |> » manquant ici", help);
            return new Pipeline(missing, [new MissingStep(missing)]);
        }

        var first = _Current;
        RArray<IStep> steps = new();

        while (_ParseSeparatedStep() is { } step) steps.Push(step);

        return new Pipeline(first.Span.Union(_previous!.Span), [.. steps]);
    }

    /// <summary>
    /// Le séparateur courant et l'étape qui le suit, avec son rail : une étape ordinaire après
    /// <c>|&gt;</c>, une étape de parcours après <c>||&gt;</c> ou <c>|!|&gt;</c> ; <see langword="null"/> sans séparateur.
    /// </summary>
    private IStep? _ParseSeparatedStep()
    {
        if (_ExpectAndConsume(TokenKind.Pipe)) return _WithRail(_ParseStep());
        if (_Expect(TokenKind.FoldPipe))
        {
            var fold = _Consume();
            var (seed, step) = _ParseFold();
            return _WithRail(new FoldStep(fold.Span.Union(step.Span), seed, step));
        }
        if (!_ExpectOneOf(TokenKind.EachPipe, TokenKind.EachStrictPipe)) return null;

        var each = _Consume();
        var target = _ParseEachTarget();
        return _WithRail(new EachStep(each.Span.Union(target.Span), each.Kind == TokenKind.EachStrictPipe, target));
    }

    /// <summary>
    /// Une étape et le rail qui la suit, s'il y en a un : <c>!&gt; [ … ]</c>, seulement après un appel
    /// (E090 sinon). Un rail sans crochets (<c>!&gt; a |&gt; b</c>) est E090 : ses étapes sont lues puis
    /// ignorées.
    /// </summary>
    private IStep _WithRail(IStep step)
    {
        if (!_Expect(TokenKind.Rail)) return step;

        if (_Future().Kind != TokenKind.OpenBracket)
        {
            _Error(_Current.Span, DiagnosticCodes.MisplacedRail, "un rail s'écrit entre crochets, juste après son guard", "crochets manquants",
                "par exemple : train |> lire !> [reparer |> $ 3] |> sauver |> done;");
            _Consume();
            _WithRail(_ParseStep());
            while (_ParseSeparatedStep() is not null) { }
            return step;
        }

        var rail = _ParseRail();
        if (step is CallStep call) return call with { Rail = rail };
        if (step is EachStep each) return each with { Rail = rail };
        if (step is FoldStep fold) return fold with { Rail = rail };

        _Error(rail.Span, DiagnosticCodes.MisplacedRail, "un rail suit un guard, pas une fin de chemin", "après done, fail ou $",
            "place le rail juste après le guard dont il reçoit l'échec");
        return step;
    }

    /// <summary><c>Rail := "!&gt;" "[" Étape { "|&gt;" Étape } "]"</c>, chaque étape suivie de son propre rail.</summary>
    private Rail _ParseRail()
    {
        var arrow = _Consume();
        _Consume();
        RArray<IStep> steps = new();

        steps.Push(_WithRail(_ParseStep()));
        while (_ParseSeparatedStep() is { } step) steps.Push(step);

        if (!_ExpectOrReport(TokenKind.CloseBracket, "pour fermer le rail", "un rail s'écrit entre crochets : !> [reparer |> $ 3]"))
        {
            while (!_ExpectOneOf(TokenKind.CloseBracket, TokenKind.Semicolon, TokenKind.EndOfFile)) _Consume();
            _ExpectAndConsume(TokenKind.CloseBracket);
        }
        return new Rail(arrow.Span.Union(_previous!.Span), [.. steps]);
    }

    /// <summary>
    /// <c>Étape := Appel | Lambda | "done" | "fail" [ Expression ] | "$" Expression | Reprise</c>. <c>done</c> est un
    /// mot contextuel : le lexer le produit comme identificateur. Les valeurs d'une étape sont lues
    /// au-dessus du pipeline : un <c>|&gt;</c> qui suit appartient au train.
    /// </summary>
    private IStep _ParseStep()
    {
        if (_Expect(TokenKind.Identifier, "done")) return new DoneStep(_Consume().Span);
        if (_Expect(TokenKind.Caret)) return _ParseCheckpointStep();
        if (_Expect(TokenKind.OpenParen) && _Future(_AfterGroup(0)).Kind == TokenKind.FatArrow)
        {
            var lambda = _ParseLambda(PipelinePrecedence);
            return new LambdaStep(lambda.Span, lambda);
        }

        if (_Expect(TokenKind.FailKeyword))
        {
            var fail = _Consume();
            var value = _CanStartExpression() ? _ParseExpression(PipelinePrecedence) : null;
            return new FailStep(value is null ? fail.Span : fail.Span.Union(value.Span), value);
        }

        if (_Expect(TokenKind.Dollar))
        {
            var dollar = _Consume();
            var code = _ParseExpression(PipelinePrecedence);
            return new ExitCodeStep(dollar.Span.Union(code.Span), code);
        }

        if (_Expect(TokenKind.Identifier)) return _ParseCallStep();

        var missing = new MissingStep(_MissingAtCurrent());
        _Error(missing.Span, DiagnosticCodes.ExpectedStep, "étape attendue", "étape manquante ici",
            "vous pouvez écrire un gear (positive, add(b)), done, fail ou $ valeur");
        return missing;
    }

    /// <summary>
    /// Un <c>^N</c> : point de reprise devant un appel (<c>^1 lire</c>), ou reprise
    /// (<c>Reprise := "^" EntierLittéral [ Limite ]</c>), une étape à part entière.
    /// </summary>
    private IStep _ParseCheckpointStep()
    {
        var caret = _Consume();
        if (!_Expect(TokenKind.IntegerLiteral))
        {
            var missing = new MissingStep(_MissingAtCurrent());
            _Error(missing.Span, DiagnosticCodes.ExpectedToken, "numéro attendu après « ^ »", "numéro manquant ici",
                "un point de reprise se pose devant une étape (^1 lire), et une reprise y revient (^1, ^1<3>, ^1<!3>)");
            return missing;
        }

        var token = _Consume();
        var number = new LiteralExpression(token.Span, token.Kind);
        if (_Expect(TokenKind.Identifier))
        {
            var call = _ParseCallStep();
            return call with { Span = caret.Span.Union(call.Span), Checkpoint = new Checkpoint(caret.Span.Union(number.Span), number) };
        }

        var limit = _Expect(TokenKind.Less) ? _ParseRepeatLimit("reprises") : null;
        return new ResumeStep(caret.Span.Union(_previous!.Span), number, limit);
    }

    /// <summary>
    /// <c>Limite := "&lt;" [ "!" ] EntierLittéral "&gt;"</c>, d'une boucle de guard ou d'une reprise ;
    /// <see langword="null"/> si le nombre manque (l'erreur est signalée).
    /// </summary>
    /// <param name="what">Ce qui est compté, pour le message : « tours », « reprises ».</param>
    private RepeatLimit? _ParseRepeatLimit(string what)
    {
        var less = _Consume();
        var fails = _ExpectAndConsume(TokenKind.Bang);
        if (!_Expect(TokenKind.IntegerLiteral))
        {
            // « <!> » : le lexer y lit « < » puis « !> ».
            var glued = _Expect(TokenKind.Rail);
            _Error(_MissingAtCurrent(), DiagnosticCodes.ExpectedToken, "nombre entier attendu entre les chevrons", "nombre manquant ici",
                $"la limite s'écrit avec un entier littéral : <3> passe à la suite après 3 {what}, <!3> échoue");
            if (glued) _Consume();
            else _ExpectAndConsume(TokenKind.Greater);
            return null;
        }

        var token = _Consume();
        _ExpectOrReport(TokenKind.Greater, "pour fermer la limite");
        return new RepeatLimit(less.Span.Union(_previous!.Span), new LiteralExpression(token.Span, token.Kind), fails);
    }

    /// <summary>
    /// <c>Appel := Identificateur [ "(" [ Expression { "," Expression } ] ")" ] [ "*" [ Limite ] ]</c>,
    /// l'étoile faisant boucler un guard.
    /// </summary>
    private CallStep _ParseCallStep()
    {
        var gear = _ParseQualifiedName("nom de gear", "une étape appelle un gear, par exemple : |> twice");
        RArray<IExpression> arguments = new();

        if (_ExpectAndConsume(TokenKind.OpenParen))
        {
            if (!_Expect(TokenKind.CloseParen))
            {
                do arguments.Push(_ParseExpression(PipelinePrecedence));
                while (_ExpectAndConsume(TokenKind.Comma));
            }

            _ExpectOrReport(TokenKind.CloseParen, $"pour fermer les arguments de « {gear.Name} »");
        }

        Repeat? repeat = null;
        if (_Expect(TokenKind.Star))
        {
            var star = _Consume();
            var limit = _Expect(TokenKind.Less) ? _ParseRepeatLimit("tours") : null;
            repeat = new Repeat(star.Span.Union(_previous!.Span), limit);
        }

        return new CallStep(gear.Span.Union(_previous!.Span), gear, [.. arguments], Repeat: repeat);
    }

    #endregion

    #region Instructions

    /// <summary>
    /// Indique si le token courant commence une instruction : un mot-clé d'instruction
    /// (déclaration, <c>undefine</c>, <c>if</c>, <c>fail</c>…), <c>{</c>, ou le début d'une
    /// expression — une affectation, un appel, <c>increment v</c>, une écriture <c>v -&gt; console</c>,
    /// <c>done</c>.
    /// </summary>
    private bool _CanStartStatement() =>
        _ExpectOneOf(TokenKind.VarKeyword, TokenKind.MutKeyword, TokenKind.ConstKeyword, TokenKind.UndefineKeyword, TokenKind.OpenBrace,
            TokenKind.IfKeyword, TokenKind.SwitchKeyword, TokenKind.BreakKeyword, TokenKind.GotoKeyword, TokenKind.ReturnKeyword, TokenKind.FailKeyword,
            TokenKind.ReservedKeyword)
        || _CanStartExpression()
        || _LooksLikeTypedDeclaration();

    /// <summary><c>&lt;-</c> ou une affectation composée : <c>+&lt;-</c>, <c>-&lt;-</c>, <c>*&lt;-</c>, <c>/&lt;-</c>, <c>°&lt;-</c>.</summary>
    private static bool _IsAssignOperator(TokenKind kind) =>
        kind is TokenKind.Assign or TokenKind.MoveAssign or TokenKind.PlusAssign or TokenKind.MinusAssign or TokenKind.StarAssign or TokenKind.SlashAssign or TokenKind.DegreeAssign;

    /// <summary>
    /// <c>Instruction := Déclaration | Affectation | Bloc | Undefine | If | Switch | Break | Goto | Return
    /// | Fail | Done | InstructionExpression</c>. Le token courant en commence une (<see cref="_CanStartStatement"/>).
    /// </summary>
    private IStatement _ParseStatement() => _Current.Kind switch
    {
        TokenKind.Star => _ParseRawStore(),
        TokenKind.VarKeyword or TokenKind.MutKeyword or TokenKind.ConstKeyword => _ParseVariableDeclaration(),
        TokenKind.UndefineKeyword => _ParseUndefine(),
        TokenKind.OpenBrace => _ParseBlock(),
        TokenKind.IfKeyword => _ParseIf(),
        TokenKind.SwitchKeyword => _ParseSwitch(),
        TokenKind.BreakKeyword => _ParseBreak(),
        TokenKind.GotoKeyword => _ParseGoto(),
        TokenKind.ReturnKeyword => _ParseReturn(),
        TokenKind.FailKeyword => _ParseFail(),
        TokenKind.ReservedKeyword => _ParseReservedStatement(),
        // « done » est un mot contextuel : seul devant « ; », il termine un guard ; devant une valeur, il arrête un |+>.
        // « panic » est contextuel aussi : seul devant « ; » ou devant une valeur, il arrête le programme.
        TokenKind.Identifier when _Current.Text == "panic" && (_Future().Kind is TokenKind.Semicolon or TokenKind.NewKeyword || _StartsDoneValue(_Future().Kind)) => _ParsePanic(),
        TokenKind.Identifier when _Current.Text == "done" && (_Future().Kind == TokenKind.Semicolon || _StartsDoneValue(_Future().Kind)) => _ParseDone(),
        // « arena » est contextuel : suivi d'un bloc (ou de sa taille entre parenthèses puis d'un bloc), il ouvre une arène.
        TokenKind.Identifier when _Current.Text == "arena" && _AtArena() => _ParseArena(),
        // « cancel » est contextuel : suivi d'un nom, il annule la tâche d'une promesse.
        TokenKind.Identifier when _Current.Text == "cancel" && _Future().Kind == TokenKind.Identifier => _ParseCancel(),
        // « Type nom: valeur; » : une déclaration typée, sans mot-clé.
        _ when _LooksLikeTypedDeclaration() => _ParseVariableDeclaration(),
        TokenKind.Identifier when _Future().Kind is TokenKind.Colon || _IsAssignOperator(_Future().Kind) => _ParseAssignment(),
        TokenKind.Identifier when _IsFieldAssignment() => _ParseFieldAssignment(),
        _ => _ParseExpressionStatement(),
    };

    #region Contrôle de flux

    /// <summary>« ( » Expression « ) » : la condition d'un if ou la valeur d'un switch.</summary>
    private IExpression _ParseParenthesized(string what)
    {
        _ExpectOrReport(TokenKind.OpenParen, $"avant {what}", $"{what} s'écrit entre parenthèses");
        var expression = _ParseExpression();
        _ExpectOrReport(TokenKind.CloseParen, $"après {what}");
        return expression;
    }

    /// <summary>Un bloc obligatoire après une condition ; manquant, il est signalé et vaut un bloc vide.</summary>
    private BlockStatement _ParseRequiredBlock(string what)
    {
        if (_Expect(TokenKind.OpenBrace)) return _ParseBlock();

        var missing = _MissingAfterPrevious();
        _Error(missing, DiagnosticCodes.ExpectedToken, $"« {{ » attendu pour ouvrir {what}", "« { » manquant ici",
            "les blocs s'écrivent toujours entre accolades : if (n < 0) { … }, gear f(): void { … }");
        return new BlockStatement(missing, []);
    }

    /// <summary><c>If := "if" "(" Expression ")" Bloc [ "else" ( If | Bloc ) ]</c>.</summary>
    private IfStatement _ParseIf()
    {
        var keyword = _Consume();
        var condition = _ParseParenthesized("la condition");
        var then = _ParseRequiredBlock("la branche du if");

        IStatement? otherwise = null;
        if (_ExpectAndConsume(TokenKind.ElseKeyword))
            otherwise = _Expect(TokenKind.IfKeyword) ? _ParseIf() : _ParseRequiredBlock("la branche else");

        return new IfStatement(keyword.Span.Union(_previous!.Span), condition, then, otherwise);
    }

    /// <summary>
    /// <c>Switch := "switch" "(" Expression ")" "{" { Cas } "}"</c>, avec
    /// <c>Cas := ( "case" Expression { "," Expression } | "case" "eval" Expression | "default" ) ":" { Instruction }</c>.
    /// </summary>
    private SwitchStatement _ParseSwitch()
    {
        var keyword = _Consume();
        var subject = _ParseParenthesized("la valeur du switch");
        RArray<SwitchSection> sections = new();

        if (!_ExpectOrReport(TokenKind.OpenBrace, "pour ouvrir les cas du switch"))
            return new SwitchStatement(keyword.Span.Union(_previous!.Span), subject, []);

        while (!_ExpectOneOf(TokenKind.CloseBrace, TokenKind.EndOfFile) && !_AtDeclarationStart())
        {
            if (_ExpectOneOf(TokenKind.CaseKeyword, TokenKind.DefaultKeyword))
            {
                sections.Push(_ParseSwitchSection());
                continue;
            }

            _Error(_Current.Span, DiagnosticCodes.ExpectedToken, "« case » ou « default » attendu", "hors de tout cas",
                "les instructions d'un switch se placent après un « case v: » ou « default: »");
            _SkipToSemicolon();
        }

        _ExpectOrReport(TokenKind.CloseBrace, "pour fermer le switch");
        return new SwitchStatement(keyword.Span.Union(_previous!.Span), subject, [.. sections]);
    }

    private SwitchSection _ParseSwitchSection()
    {
        var keyword = _Consume();
        var kind = SwitchLabelKind.Default;
        RArray<IExpression> values = new();
        IExpression? condition = null;

        if (keyword.Kind == TokenKind.CaseKeyword)
        {
            // « eval » est un mot contextuel : suivi d'une condition, il en fait un case eval.
            if (_Expect(TokenKind.Identifier, "eval") && _Future().Kind is not (TokenKind.Colon or TokenKind.Comma))
            {
                _Consume();
                kind = SwitchLabelKind.Eval;
                condition = _ParseExpression();
            }
            else
            {
                kind = SwitchLabelKind.Values;
                do values.Push(_ParseExpression());
                while (_ExpectAndConsume(TokenKind.Comma));
            }
        }

        _ExpectOrReport(TokenKind.Colon, "après le cas", "un cas s'écrit : case 1: … ou default: …");
        var label = keyword.Span.Union(_previous!.Span);

        RArray<IStatement> body = new();
        while (!_ExpectOneOf(TokenKind.CaseKeyword, TokenKind.DefaultKeyword, TokenKind.CloseBrace, TokenKind.EndOfFile) && !_AtDeclarationStart())
        {
            if (_CanStartStatement())
            {
                body.Push(_ParseStatement());
                continue;
            }

            _Error(_Current.Span, DiagnosticCodes.ExpectedToken, "instruction attendue dans le cas", "ni une instruction, ni un cas",
                "un cas contient des déclarations, des affectations, des if, des switch, des break et des goto");
            _SkipToSemicolon();
        }

        return new SwitchSection(label.Union(_previous!.Span), label, kind, [.. values], condition, [.. body]);
    }

    /// <summary><c>Break := "break" [ EntierLittéral | "all" ] ";"</c>.</summary>
    private BreakStatement _ParseBreak()
    {
        var keyword = _Consume();
        LiteralExpression? depth = null;
        var all = false;

        if (_Expect(TokenKind.IntegerLiteral))
        {
            var literal = _Consume();
            depth = new LiteralExpression(literal.Span, literal.Kind);
        }
        else if (_Expect(TokenKind.Identifier, "all"))
        {
            _Consume();
            all = true;
        }

        if (!_ExpectOrReport(TokenKind.Semicolon, "à la fin du break")) _SkipToSemicolon();
        return new BreakStatement(keyword.Span.Union(_previous!.Span), depth, all);
    }

    /// <summary><c>Goto := "goto" Expression ";"</c>.</summary>
    private GotoStatement _ParseGoto()
    {
        var keyword = _Consume();
        var target = _ParseExpression();
        if (!_ExpectOrReport(TokenKind.Semicolon, "à la fin du goto")) _SkipToSemicolon();
        return new GotoStatement(keyword.Span.Union(_previous!.Span), target);
    }

    #endregion

    /// <summary>
    /// <c>InstructionExpression := Expression [ "-&gt;" Cible ] ";"</c> : une écriture
    /// (<c>total -&gt; console;</c>), ou une expression écrite pour son effet — un appel (<c>log(x);</c>,
    /// <c>x |&gt; log;</c>) ou <c>increment v;</c>. Une autre valeur seule ne ferait rien : elle est
    /// signalée (E030, « -&gt; » attendu).
    /// </summary>
    private IStatement _ParseRawStore()
    {
        var target = _ParseExpression(SyntaxFacts.PrefixPrecedence - 1);
        if (!_ExpectAndConsume(TokenKind.Assign))
        {
            _ExpectOrReport(TokenKind.Semicolon, "après la lecture du pointeur");
            return new ExpressionStatement(target.Span, target);
        }
        var value = _ParseExpression();
        _ExpectOrReport(TokenKind.Semicolon, "après l'écriture du pointeur");
        return new RawStoreStatement(target.Span.Union(_previous!.Span), target, value);
    }

    private IStatement _ParseExpressionStatement()
    {
        var errorsBefore = _diagnosticBag.Items.Count;
        var expression = _ParseExpression();

        // Un match en instruction se termine à son accolade ; un « ; » après est permis.
        if (expression is MemberAccessExpression { Member.Name: "free" })
        {
            _ExpectOrReport(TokenKind.Semicolon, "après free");
            return new ExpressionStatement(expression.Span, expression);
        }
        if (expression is MatchExpression)
        {
            _ExpectAndConsume(TokenKind.Semicolon);
            return new ExpressionStatement(expression.Span, expression);
        }

        if (_ExpectAndConsume(TokenKind.Arrow))
        {
            var target = _ParseName("nom de cible", "une valeur s'écrit vers une cible, par exemple : total -> console;");
            var custom = target.Name is "custom" or "json" or "xml" && _Expect(TokenKind.Less) ? _ParseCustomFormat(target.Name == "custom") : null;
            // Un format de fichier est suivi du chemin : texte -> raw "a.txt";
            var path = _CanStartExpression() ? _ParseExpression() : null;
            if (!_ExpectOrReport(TokenKind.Semicolon, "à la fin de l'écriture")) _SkipToSemicolon();
            return new WriteStatement(expression.Span.Union(_previous!.Span), expression, target, path, custom);
        }

        // Une valeur seule ne fait rien ; après une erreur dans l'expression, inutile d'en rajouter.
        if (expression is not (CallExpression or PipeExpression or EachExpression or SoaEachExpression or IncrementExpression or SpawnExpression or AwaitExpression or TaskCallExpression or NativeCallExpression or CppExpression or MissingExpression or UnaryExpression { Operator: TokenKind.DeleteKeyword }) && _diagnosticBag.Items.Count == errorsBefore)
        {
            _Error(_MissingAfterPrevious(), DiagnosticCodes.ExpectedToken, "« -> » attendu après la valeur", "valeur sans effet",
                "une valeur seule ne fait rien : écris-la, par exemple : total -> console;",
                "une instruction peut aussi être un appel (log(x);) ou une affectation (total <- 0;)");
            _SkipToSemicolon();
            return new ExpressionStatement(expression.Span.Union(_previous!.Span), expression);
        }

        if (!_ExpectOrReport(TokenKind.Semicolon, "à la fin de l'instruction")) _SkipToSemicolon();
        return new ExpressionStatement(expression.Span.Union(_previous!.Span), expression);
    }

    /// <summary>
    /// Une instruction qui commence par un mot réservé (<c>continue;</c>, <c>for (…)</c>) : signalée
    /// (E070), puis sautée jusqu'au prochain <c>;</c>.
    /// </summary>
    private ExpressionStatement _ParseReservedStatement()
    {
        var word = _Consume();
        _ReportReserved(word);
        _SkipToSemicolon();
        return new ExpressionStatement(word.Span, new MissingExpression(word.Span));
    }

    /// <summary><c>Fail := "fail" [ Expression ] ";"</c>.</summary>
    private FailStatement _ParseFail()
    {
        var keyword = _Consume();
        var message = _Expect(TokenKind.Semicolon) ? null : _ParseExpression();
        if (!_ExpectOrReport(TokenKind.Semicolon, "à la fin du fail")) _SkipToSemicolon();
        return new FailStatement(keyword.Span.Union(_previous!.Span), message);
    }

    /// <summary>Vrai si un jeton qui suit <c>done</c> commence sa valeur : un nom, un littéral, <c>true</c>, <c>false</c> ou <c>null</c>.</summary>
    private static bool _StartsDoneValue(TokenKind kind) => kind is TokenKind.Identifier or TokenKind.IntegerLiteral or TokenKind.FloatLiteral or TokenKind.StringLiteral
        or TokenKind.RawStringLiteral or TokenKind.TemplateStringLiteral or TokenKind.TrueKeyword or TokenKind.FalseKeyword or TokenKind.NullKeyword;

    /// <summary><c>Panic := "panic" [ Expression ] ";"</c>.</summary>
    private PanicStatement _ParsePanic()
    {
        var keyword = _Consume();
        var message = _Expect(TokenKind.Semicolon) ? null : _ParseExpression();
        if (!_ExpectOrReport(TokenKind.Semicolon, "après panic")) _SkipToSemicolon();
        return new PanicStatement(keyword.Span.Union(_previous!.Span), message);
    }

    /// <summary>Indique si le mot « arena » commence un bloc <c>arena { … }</c> ou <c>arena(taille) { … }</c>, et non l'appel d'un gear de ce nom.</summary>
    private bool _AtArena()
    {
        if (_Future().Kind == TokenKind.OpenBrace) return true;
        if (_Future().Kind != TokenKind.OpenParen) return false;
        var depth = 0;
        for (var i = 1; ; i++)
        {
            switch (_Future(i).Kind)
            {
                case TokenKind.EndOfFile:
                    return false;
                case TokenKind.OpenParen:
                    ++depth;
                    break;
                case TokenKind.CloseParen:
                    if (--depth == 0) return _Future(i + 1).Kind == TokenKind.OpenBrace;
                    break;
            }
        }
    }

    /// <summary><c>Arena := "arena" [ "(" Expression ")" ] Bloc</c> : la taille est celle du premier segment de mémoire, en octets.</summary>
    private BlockStatement _ParseArena()
    {
        var keyword = _Consume();
        IExpression? size = null;
        if (_ExpectAndConsume(TokenKind.OpenParen))
        {
            size = _ParseExpression();
            _ExpectOrReport(TokenKind.CloseParen, "pour fermer la taille de l'arène");
        }
        var block = _ParseRequiredBlock("l'arène");
        return block with { Span = keyword.Span.Union(block.Span), IsArena = true, ArenaSize = size };
    }

    /// <summary><c>Cancel := "cancel" Expression ";"</c>.</summary>
    private CancelStatement _ParseCancel()
    {
        var keyword = _Consume();
        var operand = _ParseExpression();
        if (!_ExpectOrReport(TokenKind.Semicolon, "après cancel")) _SkipToSemicolon();
        return new CancelStatement(keyword.Span.Union(_previous!.Span), operand);
    }

    /// <summary><c>Done := "done" [ Expression ] ";"</c>.</summary>
    private DoneStatement _ParseDone()
    {
        var keyword = _Consume();
        var value = _Expect(TokenKind.Semicolon) ? null : _ParseExpression();
        if (!_ExpectOrReport(TokenKind.Semicolon, "après done", "done valeur; arrête l'accumulation d'un |+>")) _SkipToSemicolon();
        return new DoneStatement(keyword.Span.Union(_previous!.Span), value);
    }

    /// <summary><c>Return := "return" [ Expression ] ";"</c>.</summary>
    private ReturnStatement _ParseReturn()
    {
        var keyword = _Consume();
        var value = _Expect(TokenKind.Semicolon) ? null : _ParseExpression();
        if (!_ExpectOrReport(TokenKind.Semicolon, "à la fin du return")) _SkipToSemicolon();
        return new ReturnStatement(keyword.Span.Union(_previous!.Span), value);
    }

    /// <summary>
    /// <c>Gear := ( "gear" [ "pure" ] | "guard" | "emit" ) Identificateur Paramètres ":" Type ( Bloc | "=&gt;" Expression ";" )</c>.
    /// Seul un gear peut être <c>pure</c> (E068 sinon).
    /// </summary>
    private GearDeclaration _ParseGear()
    {
        var keyword = _Consume();
        var isPure = false;
        if (_Expect(TokenKind.PureKeyword))
        {
            var pure = _Consume();
            if (keyword.Kind is TokenKind.GearKeyword or TokenKind.UnsafeKeyword) isPure = true;
            else
                _Error(pure.Span, DiagnosticCodes.PureGuardOrEmit, $"un {keyword.Text} ne peut pas être pure", "seul un gear peut l'être",
                    keyword.Kind == TokenKind.GuardKeyword
                        ? "un guard peut échouer et envoyer son échec sur le rail : retire « pure »"
                        : "un emit fait des entrées et sorties : retire « pure »");
        }

        var name = _ParseName($"nom de {keyword.Text}", $"un {keyword.Text} s'écrit par exemple : {keyword.Text} twice(int n): int => n * 2;");
        var typeParameters = _Expect(TokenKind.Less) ? _ParseTypeParameters() : null;

        IReadOnlyList<Parameter> parameters = [];
        if (_Expect(TokenKind.OpenParen)) parameters = _ParseParameters($"de « {name.Name} »");
        else _ExpectOrReport(TokenKind.OpenParen, "pour ouvrir les paramètres du gear", "un gear sans paramètre s'écrit avec des parenthèses vides : gear zero(): int => 0;");

        ITypeNode returnType;
        if (_ExpectAndConsume(TokenKind.Colon))
        {
            _inGearReturnType = true;
            returnType = _ParseType();
            _inGearReturnType = false;
        }
        else
        {
            returnType = new MissingType(_MissingAfterPrevious());
            _Error(returnType.Span, DiagnosticCodes.ExpectedToken, "« : » et le type de retour attendus", "type de retour manquant ici",
                "un gear déclare toujours son type de retour, void s'il ne renvoie rien : gear log(string m): void { … }");
        }

        if (_ExpectAndConsume(TokenKind.FatArrow))
        {
            var expression = _ParseExpression();
            if (!_ExpectOrReport(TokenKind.Semicolon, "à la fin du gear")) _SkipToSemicolon();
            return new GearDeclaration(keyword.Span.Union(_previous!.Span), keyword.Kind, isPure, name, parameters, returnType, null, expression, typeParameters);
        }

        // Sans corps (« guard f(string s): string; ») : l'implémentation est le C++ d'un fichier .rgl.cpp. Les fichiers qui n'en veulent pas le refusent (FileChecks).
        if (_Expect(TokenKind.Semicolon))
        {
            _Consume();
            return new GearDeclaration(keyword.Span.Union(_previous!.Span), keyword.Kind, isPure, name, parameters, returnType, null, null, typeParameters);
        }

        var body = _ParseRequiredBlock("le corps du gear");
        // Un gear sans corps (« gear f(): int; ») : son « ; » ne commence rien d'autre.
        if (body.Statements.Count == 0 && body.Span.Length.Value == 0) _ExpectAndConsume(TokenKind.Semicolon);
        return new GearDeclaration(keyword.Span.Union(_previous!.Span), keyword.Kind, isPure, name, parameters, returnType, body, null, typeParameters);
    }

    /// <summary>
    /// <c>Bloc := "{" { Instruction } "}"</c>. Ce qui n'est pas une instruction est signalé, puis
    /// sauté jusqu'au prochain <c>;</c>. Une déclaration de niveau fichier (<c>gear</c>, <c>train</c>…)
    /// termine le bloc : sa <c>}</c> manquait.
    /// </summary>
    private BlockStatement _ParseBlock()
    {
        var open = _Consume();
        RArray<IStatement> statements = new();

        while (!_ExpectOneOf(TokenKind.CloseBrace, TokenKind.EndOfFile) && !_AtDeclarationStart())
        {
            if (_CanStartStatement())
            {
                statements.Push(_ParseStatement());
                continue;
            }

            _Error(_Current.Span, DiagnosticCodes.ExpectedToken, "« } » attendu pour fermer le bloc", "ni une instruction, ni « } »",
                "un bloc contient des déclarations (var, Type nom, const), des affectations (nom <- valeur), des blocs et des undefine");
            _SkipToSemicolon();
        }

        _ExpectOrReport(TokenKind.CloseBrace, "pour fermer le bloc");
        return new BlockStatement(open.Span.Union(_previous!.Span), [.. statements]);
    }

    /// <summary>
    /// <c>Affectation := Identificateur [ ":" Type ] ( "&lt;-" | "+&lt;-" | "-&lt;-" | "*&lt;-" | "/&lt;-" | "°&lt;-" ) Expression ";"</c>.
    /// </summary>
    private AssignmentStatement _ParseAssignment()
    {
        var target = new Identifier(_Consume().Span);
        var typeHint = _ExpectAndConsume(TokenKind.Colon) ? _ParseType() : null;

        var op = TokenKind.Assign;
        var opSpan = _MissingAtCurrent();
        if (_IsAssignOperator(_Current.Kind))
        {
            var token = _Consume();
            (op, opSpan) = (token.Kind, token.Span);
        }
        else
            _ExpectOrReport(TokenKind.Assign, $"pour affecter une valeur à « {target.Name} »",
                $"une affectation s'écrit par exemple : {target.Name} <- 5;");
        var value = op == TokenKind.Assign && _IsReadSource() ? _ParseRead() : _ParseExpression();

        if (!_ExpectOrReport(TokenKind.Semicolon, "à la fin de l'affectation")) _SkipToSemicolon();
        return new AssignmentStatement(target.Span.Union(_previous!.Span), target, typeHint, op, value, opSpan);
    }

    /// <summary><c>Undefine := "undefine" Identificateur ";"</c>.</summary>
    private UndefineStatement _ParseUndefine()
    {
        var keyword = _Consume();

        var name = _ParseName("nom de variable", "undefine libère une variable, par exemple : undefine compteur;");

        if (!_ExpectOrReport(TokenKind.Semicolon, "à la fin de l'undefine")) _SkipToSemicolon();
        return new UndefineStatement(keyword.Span.Union(_previous!.Span), name);
    }

    #endregion

    #region Variables

    /// <summary>Le format de la console, mot contextuel : <c>x -&gt; console</c>, <c>var l &lt;- console</c>.</summary>
    private const string ConsoleFormat = "console";

    /// <summary>L'horloge, mot contextuel comme la console : <c>var t &lt;- clock</c> donne l'instant présent.</summary>
    private const string ClockFormat = "clock";

    /// <summary>Les formats de fichier, mots contextuels : un chemin les suit, <c>var t &lt;- raw "a.txt"</c>.</summary>
    private static readonly HashSet<string> FileFormats = new(["file", "raw", "json", "xml", "custom"], StringComparer.Ordinal);

    /// <summary>
    /// Indique si le token à <paramref name="offset"/> du courant est un format de lecture, juste après
    /// <c>&lt;-</c> : <c>console</c>, ou un format de fichier suivi d'un chemin (un nom ou un littéral :
    /// <c>raw</c> seul reste une variable).
    /// </summary>
    private bool _IsReadSourceAt(int offset)
    {
        var token = offset == 0 ? _Current : _Future(offset);
        if (token.Kind != TokenKind.Identifier) return false;
        if (token.Text is ConsoleFormat or ClockFormat) return true;
        if (!FileFormats.Contains(token.Text)) return false;
        // custom<M, train> : les chevrons suivent le format.
        if (token.Text is "custom" or "json" or "xml" or "file" && _Future(offset + 1).Kind == TokenKind.Less) return true;
        var next = _Future(offset + 1).Kind;
        return _IsLiteral(next) || next == TokenKind.Identifier;
    }

    /// <summary>Indique si le token courant, juste après <c>&lt;-</c>, est un format de lecture.</summary>
    private bool _IsReadSource() => _IsReadSourceAt(0);

    /// <summary><c>Lecture := "console" | Format Chemin</c>, juste après <c>&lt;-</c> : <c>console</c>, <c>raw "a.txt"</c>.</summary>
    private ReadExpression _ParseRead()
    {
        var source = new Identifier(_Consume().Span);
        if (source.Name is ConsoleFormat or ClockFormat) return new ReadExpression(source.Span, source);

        var custom = source.Name is "custom" or "json" or "xml" or "file" && _Expect(TokenKind.Less) ? _ParseCustomFormat(source.Name == "custom") : null;
        var path = _ParseExpression();
        return new ReadExpression(source.Span.Union(path.Span), source, path, custom);
    }

    /// <summary>
    /// <c>Chevrons := "&lt;" Type [ "," Nom ] "&gt;"</c>, juste après le format : <c>custom&lt;Config, lireToml&gt;</c> (avec le train), <c>json&lt;Config&gt;</c>.
    /// </summary>
    private CustomFormat _ParseCustomFormat(bool withTrain)
    {
        var open = _Consume();
        var type = _ParseType();
        Identifier? train = null;
        if (withTrain)
        {
            _ExpectOrReport(TokenKind.Comma, "entre le type et le train de custom", "par exemple : custom<Config, lireToml> \"a.toml\"");
            train = _ParseQualifiedName("nom du train", "le train de custom est le gear ou le guard qui convertit les bytes : custom<Config, lireToml>");
        }
        _ExpectOrReport(TokenKind.Greater, withTrain ? "pour fermer les arguments de custom" : "pour fermer le type de json");
        return new CustomFormat(open.Span.Union(_previous!.Span), type, train);
    }

    /// <summary>
    /// Indique si l'instruction qui commence ici est une déclaration typée : <c>Type nom:</c>, où le
    /// type est tout type qui ne commence pas par <c>{</c> (un bloc) : primitif borné ou non, nom,
    /// tableau, tuple, lambda, nullable, union ou résultat (<see cref="_SkipType"/>). <c>Type nom;</c>,
    /// <c>Type nom &lt;- v</c> et <c>Type nom = v</c> en sont aussi, mal écrites : la déclaration les signale,
    /// comme un mot réservé à la place du nom. Une instruction qui commence par un type primitif en est
    /// toujours une : aucune expression ne commence ainsi.
    /// </summary>
    private bool _LooksLikeTypedDeclaration()
    {
        if (SyntaxFacts.IsPrimitiveType(_Current.Kind)) return true;
        if (_Expect(TokenKind.OpenBrace)) return false;
        var next = _SkipType(0);
        return next >= 0 && _Future(next).Kind is TokenKind.Identifier or TokenKind.ReservedKeyword
            && _Future(next + 1).Kind is TokenKind.Colon or TokenKind.ColonColon or TokenKind.Semicolon or TokenKind.Assign or TokenKind.Equals;
    }

    /// <summary>
    /// La position, comptée depuis le token courant, qui suit le type écrit à <paramref name="at"/>,
    /// sans rien lire ni signaler ; -1 si aucun type ne commence là. Suit la grammaire de
    /// <see cref="_ParseType"/> : <c>TypeUnion [ "!" TypeUnion ]</c>.
    /// </summary>
    private int _SkipType(int at)
    {
        var next = _SkipUnionType(at);
        return next >= 0 && _Future(next).Kind == TokenKind.Bang ? _SkipUnionType(next + 1) : next;
    }

    /// <summary>Saute <c>TypeNullable { "|" TypeNullable }</c> ; -1 si aucun type ne commence là.</summary>
    private int _SkipUnionType(int at)
    {
        var next = _SkipNullableType(at);
        while (next >= 0 && _Future(next).Kind == TokenKind.Bar) next = _SkipNullableType(next + 1);
        return next;
    }

    /// <summary>Saute <c>TypeSimple [ "?" ]</c> ; -1 si aucun type ne commence là.</summary>
    private int _SkipNullableType(int at)
    {
        var next = _SkipSimpleType(at);
        return next >= 0 && _Future(next).Kind is TokenKind.Question or TokenKind.QuestionQuestion ? next + 1 : next;
    }

    /// <summary>
    /// Saute un type simple : primitif (et ses bornes <c>&lt;…&gt;</c>), nom, groupe <c>[…]</c>, <c>{…}</c>
    /// ou <c>(…)</c> — suivi de <c>=&gt;</c> et d'un type pour une lambda, <c>pure</c> compris ; -1 sinon.
    /// </summary>
    private int _SkipSimpleType(int at)
    {
        var kind = _Future(at).Kind;
        if (kind is TokenKind.PtrKeyword or TokenKind.UptrKeyword && _Future(at + 1).Kind == TokenKind.Less) return _SkipGroup(at + 1, TokenKind.Less, TokenKind.Greater);
        if (kind == TokenKind.PureKeyword && _Future(at + 1).Kind == TokenKind.OpenParen) return _SkipSimpleType(at + 1);
        if (kind == TokenKind.Identifier)
        {
            // Un nom qualifié : Geometry.Vec2.
            var end = at + 1;
            while (_Future(end).Kind == TokenKind.Dot && _Future(end + 1).Kind == TokenKind.Identifier) end += 2;
            // Pair<int, string> : les arguments d'un type générique.
            return _Future(end).Kind == TokenKind.Less && _SkipGroup(end, TokenKind.Less, TokenKind.Greater) is >= 0 and var after ? after : end;
        }
        if (SyntaxFacts.IsPrimitiveType(kind))
            return _Future(at + 1).Kind is TokenKind.Less or TokenKind.Assign ? _SkipGroup(at + 1, _Future(at + 1).Kind, TokenKind.Greater) : at + 1;
        if (kind == TokenKind.OpenBracket) return _SkipGroup(at, TokenKind.OpenBracket, TokenKind.CloseBracket);
        if (kind == TokenKind.OpenBrace) return _SkipGroup(at, TokenKind.OpenBrace, TokenKind.CloseBrace);
        if (kind != TokenKind.OpenParen) return -1;

        var next = _SkipGroup(at, TokenKind.OpenParen, TokenKind.CloseParen);
        return next >= 0 && _Future(next).Kind == TokenKind.FatArrow ? _SkipType(next + 1) : next;
    }

    /// <summary>
    /// La position qui suit le groupe équilibré ouvert par <paramref name="open"/> à <paramref name="at"/> ;
    /// -1 s'il n'est pas fermé avant la fin du fichier, ou avant un <c>;</c> hors des crochets d'un
    /// tableau (<c>[int;3]</c>).
    /// </summary>
    private int _SkipGroup(int at, TokenKind open, TokenKind close)
    {
        var depth = 0;
        for (var i = at; ; ++i)
        {
            var kind = _Future(i).Kind;
            if (kind == TokenKind.EndOfFile || (kind == TokenKind.Semicolon && open != TokenKind.OpenBracket)) return -1;

            if (kind == open) ++depth;
            else if (kind == close && --depth == 0) return i + 1;
        }
    }

    /// <summary>
    /// <c>Déclaration := [ "mut" ] ( "var" Identificateur | "const" Type Identificateur | Type Identificateur )
    /// ( ":" | "::" ) Expression ";"</c>, <c>::</c> déplaçant la valeur quand c'est possible. Une déclaration sans valeur (<c>int a;</c>) est signalée (E006), comme
    /// une constante <c>mut</c> (E101) ; si le <c>;</c> final manque, le parseur se resynchronise sur
    /// le prochain <c>;</c>.
    /// </summary>
    private VarDeclaration _ParseVariableDeclaration()
    {
        var first = _Current;
        var mut = _Expect(TokenKind.MutKeyword) ? _Consume() : null;
        var isIntef = _ExpectAndConsume(TokenKind.VarKeyword);
        var isConst = !isIntef && _Expect(TokenKind.ConstKeyword);
        if (isConst)
        {
            var keyword = _Consume();
            if (mut is not null)
                _Error(mut.Span.Union(keyword.Span), DiagnosticCodes.InvalidMut, "une constante n'est jamais mut", "mut const",
                    "une constante ne change jamais : retire mut, ou déclare une variable, par exemple : mut var liste: [1, 2];");
        }

        // Exemple de la bonne forme, donné dans les aides.
        var example = isIntef ? "var compteur: 0;" : isConst ? "const int MAX: 5;" : "int compteur: 0;";

        var type = isIntef ? null : _ParseType();

        var name = _ParseName("nom de variable", $"une déclaration s'écrit par exemple : {example}");

        // « nom:: valeur » : la valeur est déplacée quand c'est possible.
        var isMove = _ExpectAndConsume(TokenKind.ColonColon);
        var value = isMove ? _ParseExpression() : _ParseVariableValue(example);

        if (!_ExpectOrReport(TokenKind.Semicolon, "à la fin de la déclaration")) _SkipToSemicolon();

        var last = _previous ?? first;
        return new VarDeclaration(first.Span.Union(last.Span), isConst, isIntef, type, name, value, mut is not null, isMove);
    }

    /// <summary>
    /// Valeur initiale d'une déclaration : <c>":" Expression</c>. Sans valeur, signale E006 et
    /// rend une expression manquante. Un <c>:</c> oublié, ou remplacé par <c>&lt;-</c> ou <c>=</c>,
    /// est signalé, puis la valeur qui suit est lue quand même.
    /// </summary>
    /// <param name="example">Déclaration correcte du même genre, montrée dans l'aide.</param>
    private IExpression _ParseVariableValue(string example)
    {
        if (_ExpectAndConsume(TokenKind.Colon)) return _ParseExpression();

        // « <- console » : une lecture, la seule déclaration qui s'initialise avec « <- ».
        if (_Expect(TokenKind.Assign) && _IsReadSourceAt(1))
        {
            _Consume();
            return _ParseRead();
        }

        if (_ExpectOneOf(TokenKind.Semicolon, TokenKind.EndOfFile))
        {
            var missing = new MissingExpression(_MissingAfterPrevious());
            _Error(missing.Span, DiagnosticCodes.VariableWithoutValue, "variable déclarée sans valeur", "valeur manquante ici",
                $"une variable reçoit toujours une valeur initiale, par exemple : {example}");
            return missing;
        }

        if (_ExpectOneOf(TokenKind.Assign, TokenKind.Equals))
        {
            var wrong = _Consume();
            _Error(wrong.Span, DiagnosticCodes.ExpectedToken, $"« : » attendu à la place de « {wrong.Text} »", "« : » attendu ici",
                $"la valeur initiale suit « : », par exemple : {example}", "« <- » sert à réassigner une variable déjà déclarée");
            return _ParseExpression();
        }

        var absent = new MissingExpression(_MissingAfterPrevious());
        _Error(absent.Span, DiagnosticCodes.ExpectedToken, "« : » attendu avant la valeur initiale", "« : » manquant ici",
            $"une déclaration s'écrit par exemple : {example}");
        return _CanStartExpression() ? _ParseExpression() : absent;
    }

    #endregion
}
