using System.Text.RegularExpressions;
using Rgl.AST;
using Rgl.AST.Enums;
using Rgl.AST.Extensions;
using Rgl.AST.Files;
using Rgl.AST.Gears;
using Rgl.AST.Ingots;
using Rgl.AST.Interfaces;
using Rgl.AST.Molds;
using Rgl.AST.Train;
using Rgl.AST.Variables;
using Rgl.Diagnostics;
using Rgl.Syntax;
using Rgl.Text;

namespace Rgl.Docs;

/// <summary>
/// Range la documentation d'un fichier : chaque déclaration documentable (gear, mold et ses champs, enum et ses variantes, ingot,
/// constante, train, extension et ses membres) avec son commentaire <c>///</c> lu, sa signature et son identifiant. La documentation
/// reste hors de l'IR : le binder n'en sait rien.
/// </summary>
internal static partial class DocCollector
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex _Spaces();

    /// <summary>Lit les déclarations de <paramref name="unit"/> en la découpant d'abord en tokens.</summary>
    public static IReadOnlyList<DocEntry> Collect(string moduleName, CompilationUnit unit, DiagnosticBag? diagnostics = null) =>
        Collect(moduleName, unit, new Lexer(unit.Span.Source, new DiagnosticBag()).Tokenize().Tokens, diagnostics);

    /// <summary>Lit les déclarations de <paramref name="unit"/>, dont les <paramref name="tokens"/> sont déjà connus.</summary>
    /// <param name="moduleName">Le nom du fichier sans extension.</param>
    /// <param name="unit">L'arbre du fichier.</param>
    /// <param name="tokens">Ses tokens, avec leurs commentaires.</param>
    /// <param name="diagnostics">Reçoit les problèmes de lecture des commentaires ; <see langword="null"/> pour ne rien signaler.</param>
    public static IReadOnlyList<DocEntry> Collect(string moduleName, CompilationUnit unit, IReadOnlyList<Token> tokens, DiagnosticBag? diagnostics = null)
    {
        var walker = new Walker(moduleName, unit, tokens, diagnostics);
        walker.Run();
        return walker.Entries;
    }

    private sealed class Walker
    {
        private readonly string _module;
        private readonly CompilationUnit _unit;
        private readonly SourceText _source;
        private readonly Dictionary<uint, Token> _tokenAt = [];
        private readonly DiagnosticBag? _diagnostics;
        private readonly bool _importable;
        private readonly bool _hasExports;
        private readonly HashSet<string> _exported = new(StringComparer.Ordinal);

        public List<DocEntry> Entries { get; } = [];

        public Walker(string module, CompilationUnit unit, IReadOnlyList<Token> tokens, DiagnosticBag? diagnostics)
        {
            _module = module;
            _unit = unit;
            _source = unit.Span.Source;
            _diagnostics = diagnostics;
            foreach (var token in tokens) _tokenAt.TryAdd(token.Span.Start.Value, token);

            // Un fichier .forge qui n'écrit aucun export expose tout son premier niveau, comme pour les imports.
            _importable = _source.Path.EndsWith(".forge", StringComparison.OrdinalIgnoreCase);
            var statements = unit.Statements;
            _hasExports = statements.OfType<ExportDeclaration>().Any();
            for (var i = 0; i < statements.Count; ++i)
            {
                if (statements[i] is not ExportDeclaration export) continue;
                foreach (var name in export.Names.Where(n => n.Kind == ExportKind.Name)) _exported.Add(name.Name.Name);
                if (export.IsInline && i + 1 < statements.Count && _NameOf(statements[i + 1]) is { } inline) _exported.Add(inline);
            }
        }

        public void Run()
        {
            var statements = _unit.Statements;
            for (var i = 0; i < statements.Count; ++i)
            {
                // Un commentaire écrit avant « export gear f() » précède l'export, pas le gear.
                var start = i > 0 && statements[i - 1] is ExportDeclaration { IsInline: true } export ? export.Span : _StartOf(statements[i]);
                _Statement(statements[i], start, container: null, topLevel: true);
            }
        }

        /// <summary>Où commence la déclaration, annotations (<c>@async</c>, <c>[métadonnées]</c>) comprises : c'est avant elles que s'écrit sa documentation.</summary>
        private static SourceSpan _StartOf(IStatement statement)
        {
            var notes = statement switch
            {
                GearDeclaration gear => gear.Notes,
                MoldDeclaration mold => mold.Notes,
                EnumDeclaration enumeration => enumeration.Notes,
                IngotDeclaration ingot => ingot.Notes,
                VarDeclaration declaration => declaration.Notes,
                _ => null,
            };
            return notes is not null && notes.Span.Start.Value < statement.Span.Start.Value ? notes.Span : statement.Span;
        }

        private static string? _NameOf(IStatement statement) => statement switch
        {
            GearDeclaration gear => gear.Name.Name,
            MoldDeclaration mold => mold.Name.Name,
            EnumDeclaration enumeration => enumeration.Name.Name,
            IngotDeclaration ingot => ingot.Name.Name,
            VarDeclaration { IsConst: true } constant => constant.Name.Name,
            TrainBlock { Name: { } name } => name.Name,
            _ => null,
        };

        private bool _IsPublic(string name, bool topLevel) => topLevel && _importable && (!_hasExports || _exported.Contains(name));

        private DocComment? _CommentAt(SourceSpan start) =>
            _tokenAt.TryGetValue(start.Start.Value, out var token) && DocExtractor.Before(token) is { } block ? DocParser.Parse(block, _diagnostics) : null;

        private string _Flat(uint from, uint to) =>
            _Spaces().Replace(_source.Content.Substring((int)from, (int)(Math.Max(from, to) - from)), " ").Trim();

        private void _Add(DocKind kind, string name, string? container, string signature, SourceSpan nameSpan, SourceSpan commentAt, DocTarget target, bool isPublic)
        {
            var qualified = container is null ? name : container + "." + name;
            Entries.Add(new DocEntry($"{_module}:{qualified}", kind, name, container, signature, nameSpan, _module, _CommentAt(commentAt), target, isPublic, commentAt));
        }

        private void _Statement(IStatement statement, SourceSpan start, string? container, bool topLevel, bool publicMember = false, IReadOnlyList<string>? inheritedTypeParameters = null)
        {
            switch (statement)
            {
                case GearDeclaration { Name.IsMissing: false } gear:
                {
                    var kind = gear.Keyword switch { TokenKind.GuardKeyword => DocKind.Guard, TokenKind.EmitKeyword => DocKind.Emit, _ => DocKind.Gear };
                    var target = new DocTarget([.. gear.Parameters.Select(p => p.Name.Name)], [.. (inheritedTypeParameters ?? []), .. (gear.TypeParameters ?? []).Select(t => t.Name.Name)], gear.ReturnType.Span.Text != "void");
                    _Add(kind, gear.Name.Name, container, _Flat(gear.Span.Start.Value, gear.ReturnType.Span.End.Value), gear.Name.Span, start, target,
                        publicMember || _IsPublic(gear.Name.Name, topLevel));
                    break;
                }

                case MoldDeclaration { Name.IsMissing: false } mold:
                {
                    var isPublic = _IsPublic(mold.Name.Name, topLevel);
                    var header = _Flat(mold.Span.Start.Value, _OpeningBrace(mold.Span));
                    _Add(mold.IsError ? DocKind.Error : DocKind.Mold, mold.Name.Name, container, header, mold.Name.Span, start,
                        new DocTarget([], [.. (mold.TypeParameters ?? []).Select(t => t.Name.Name)], false), isPublic);
                    foreach (var field in mold.Members.OfType<MoldField>().Where(f => !f.Name.IsMissing))
                        _Add(DocKind.Field, field.Name.Name, mold.Name.Name, _Flat(field.Type.Span.Start.Value, field.Name.Span.End.Value), field.Name.Span, field.Span, DocTarget.None, isPublic);
                    break;
                }

                case EnumDeclaration { Name.IsMissing: false } enumeration:
                {
                    var isPublic = _IsPublic(enumeration.Name.Name, topLevel);
                    _Add(DocKind.Enum, enumeration.Name.Name, container, _Flat(enumeration.Span.Start.Value, _OpeningBrace(enumeration.Span)), enumeration.Name.Span, start, DocTarget.None, isPublic);
                    foreach (var variant in enumeration.Variants.Where(v => !v.Name.IsMissing))
                        _Add(DocKind.Variant, variant.Name.Name, enumeration.Name.Name, variant.Name.Name, variant.Name.Span, variant.Span, DocTarget.None, isPublic);
                    break;
                }

                case IngotDeclaration { Name.IsMissing: false } ingot:
                    _Add(DocKind.Ingot, ingot.Name.Name, container, _Flat(ingot.Span.Start.Value, ingot.Span.End.Value), ingot.Name.Span, start, DocTarget.None, _IsPublic(ingot.Name.Name, topLevel));
                    break;

                case VarDeclaration { IsConst: true, Name.IsMissing: false } constant:
                    _Add(DocKind.Constant, constant.Name.Name, container, _Flat(constant.Span.Start.Value, constant.Name.Span.End.Value), constant.Name.Span, start, DocTarget.None,
                        publicMember || _IsPublic(constant.Name.Name, topLevel));
                    break;

                case TrainBlock block:
                {
                    var name = block.Name?.Name ?? _module;
                    var isPublic = block.Name is null ? _importable : _IsPublic(name, topLevel);
                    if (block.Train is { } train) _Train(name, train, block.Name?.Span ?? train.Span, start, isPublic);
                    foreach (var declaration in block.Declarations) _Statement(declaration, _StartOf(declaration), name, topLevel: false);
                    break;
                }

                case TrainDeclaration train:
                    _Train(_module, train, train.Span, start, _importable);
                    break;

                case ExtendDeclaration extend:
                {
                    var target = _Flat(extend.Target.Span.Start.Value, extend.Target.Span.End.Value);
                    var header = extend.TypeParameters is { Count: > 0 } generic
                        ? $"extend<{string.Join(", ", generic.Select(t => t.Span.Text))}> {target}"
                        : $"extend {target}";
                    Entries.Add(new DocEntry($"{_module}:extend {target}", DocKind.Extension, target, null, header, extend.Target.Span, _module, _CommentAt(start),
                        new DocTarget([], [.. (extend.TypeParameters ?? []).Select(t => t.Name.Name)], false), _importable, start));
                    var inherited = (extend.TypeParameters ?? []).Select(t => t.Name.Name).ToList();
                    foreach (var member in extend.Members) _Statement(member, _StartOf(member), target, topLevel: false, publicMember: _importable, inheritedTypeParameters: inherited);
                    break;
                }
            }
        }

        private void _Train(string name, TrainDeclaration train, SourceSpan nameSpan, SourceSpan commentAt, bool isPublic)
        {
            var parameters = train.Parameters ?? [];
            var signature = $"train({string.Join(", ", parameters.Select(p => _Flat(p.Span.Start.Value, p.Span.End.Value)))})";
            _Add(DocKind.Train, name, null, signature, nameSpan, commentAt, new DocTarget([.. parameters.Select(p => p.Name.Name)], [], true), isPublic);
        }

        /// <summary>L'indice de l'accolade ouvrante de la déclaration, ou sa fin s'il n'y en a pas.</summary>
        private uint _OpeningBrace(SourceSpan span)
        {
            var at = span.Text.IndexOf('{');
            return at < 0 ? span.End.Value : span.Start.Value + (uint)at;
        }
    }
}
