using Rgl.IR.Symbols;
using Rgl.IR.Types;
using Rgl.Text;
using Rotomeca.Core.Optionals;

namespace Rgl.Binding;

/// <summary>
/// Une variable connue d'une portée. <see cref="Symbol"/> vaut <see langword="null"/> quand sa
/// déclaration contenait une erreur (déjà signalée) : le nom existe, pour ne pas provoquer
/// d'erreur « nom inconnu » en cascade, mais il n'a pas de symbole.
/// </summary>
/// <param name="Symbol">La variable, ou <see langword="null"/> après une erreur dans sa déclaration.</param>
/// <param name="Declaration">Le nom dans sa déclaration.</param>
internal readonly record struct ScopeEntry(VariableSymbol? Symbol, SourceSpan Declaration);

/// <summary>
/// Les variables d'un bloc. Un nom est cherché dans le bloc, puis dans les blocs englobants. Une
/// variable libérée par <c>undefine</c> disparaît de son bloc, qui retient où elle l'a été.
/// </summary>
/// <param name="parent">Le bloc englobant ; <see langword="null"/> pour le fichier.</param>
internal sealed class Scope(Scope? parent)
{
    private readonly Dictionary<string, ScopeEntry> _variables = new(StringComparer.Ordinal);

    /// <summary>Variables libérées par <c>undefine</c>, avec l'endroit du <c>undefine</c>.</summary>
    private readonly Dictionary<string, SourceSpan> _released = new(StringComparer.Ordinal);

    /// <summary>
    /// Les variables et paramètres nullables dont on sait, dans ce bloc, qu'ils ne sont pas
    /// <c>null</c> (après un test), avec leur type sans nullable.
    /// </summary>
    private readonly Dictionary<object, IrType> _narrowed = [];

    /// <summary>Les constantes importées d'un autre fichier (<c>import X only {MAX}</c>), visibles de la portée du fichier.</summary>
    private Dictionary<string, ScopeEntry>? _imports;

    /// <summary>Le bloc englobant ; <see langword="null"/> pour le fichier.</summary>
    public Scope? Parent { get; } = parent;

    /// <summary>Les noms déclarés dans ce bloc.</summary>
    public IEnumerable<string> Names => _variables.Keys;

    /// <summary>Rend visible ici, sous <paramref name="name"/>, une constante d'un autre fichier.</summary>
    public void Import(string name, ScopeEntry entry) => (_imports ??= new(StringComparer.Ordinal))[name] = entry;

    /// <summary>Le type sans nullable de <paramref name="symbol"/> s'il est sûr qu'il n'est pas <c>null</c> ici ; sinon <see langword="null"/>.</summary>
    /// <param name="symbol">Un <see cref="VariableSymbol"/> ou un <see cref="ParameterSymbol"/>.</param>
    public IrType? NarrowedType(object symbol) => _narrowed.TryGetValue(symbol, out var type) ? type : Parent?.NarrowedType(symbol);

    /// <summary>Note que <paramref name="symbol"/> n'est pas <c>null</c> dans ce bloc, jusqu'à sa fin.</summary>
    public void Narrow(object symbol, IrType type) => _narrowed[symbol] = type;

    /// <summary>Oublie, ici et dans les blocs englobants, que <paramref name="symbol"/> n'est pas <c>null</c> : il vient d'être modifié.</summary>
    public void Widen(object symbol)
    {
        _narrowed.Remove(symbol);
        Parent?.Widen(symbol);
    }

    /// <summary>La variable visible sous ce nom, ici ou dans un bloc englobant.</summary>
    public MayBe<ScopeEntry> this[string name] =>
        _variables.TryGetValue(name, out var entry) ? MayBe<ScopeEntry>.Some(entry)
        : _released.ContainsKey(name) ? MayBe<ScopeEntry>.Null
        : Parent is not null ? Parent[name]
        : _imports is not null && _imports.TryGetValue(name, out var imported) ? MayBe<ScopeEntry>.Some(imported)
        : MayBe<ScopeEntry>.Null;

    /// <summary>Indique si ce bloc même déclare <paramref name="name"/> (sans compter les blocs englobants).</summary>
    public bool DeclaresHere(string name) => _variables.ContainsKey(name);

    /// <summary>Endroit du <c>undefine</c> qui a libéré <paramref name="name"/>, ici ou dans un bloc englobant.</summary>
    public MayBe<SourceSpan> Released(string name) =>
        _released.TryGetValue(name, out var span) ? MayBe<SourceSpan>.Some(span) : Parent?.Released(name) ?? MayBe<SourceSpan>.Null;

    /// <summary>Déclare <paramref name="name"/> dans ce bloc ; un ancien <c>undefine</c> de ce nom ici est oublié.</summary>
    public void Declare(string name, ScopeEntry entry)
    {
        _variables[name] = entry;
        _released.Remove(name);
    }

    /// <summary>Masque une source consommée dans ce bloc, sans modifier le paramètre, la capture ou la constante extérieure.</summary>
    public void Hide(string name, SourceSpan at)
    {
        _variables.Remove(name);
        _released[name] = at;
    }

    /// <summary>Libère la variable visible sous <paramref name="name"/>, dans le bloc qui la déclare.</summary>
    /// <param name="name">Nom d'une variable visible.</param>
    /// <param name="at">Le <c>undefine</c>.</param>
    public void Release(string name, SourceSpan at)
    {
        if (_variables.Remove(name)) _released[name] = at;
        else Parent?.Release(name, at);
    }
}
