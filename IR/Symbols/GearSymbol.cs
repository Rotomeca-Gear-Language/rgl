using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.IR.Symbols;

/// <summary>
/// Un gear déclaré : sa signature, connue avant la liaison des corps (un gear peut appeler un gear
/// écrit plus bas, ou lui-même). Comparé par identité, comme les autres symboles.
/// </summary>
/// <param name="name">Nom du gear.</param>
/// <param name="kind">Gear, guard ou emit.</param>
/// <param name="parameters">Ses paramètres, dans l'ordre.</param>
/// <param name="returnType">Son type de retour ; <c>void</c> s'il ne renvoie rien.</param>
/// <param name="isPure">Vrai pour un gear <c>pure</c>.</param>
/// <param name="declaration">Son nom dans sa déclaration.</param>
/// <param name="qualifiedName">
/// Son nom unique dans le programme : <c>Commandes.ajouter</c> pour un gear d'un fichier importé, celui du
/// gear lui-même dans le fichier d'entrée ; <see langword="null"/> pour le nom seul.
/// </param>
internal sealed class GearSymbol(string name, GearKind kind, IReadOnlyList<ParameterSymbol> parameters, IrType returnType, bool isPure, SourceSpan declaration, string? qualifiedName = null)
{
    /// <summary>Nom du gear.</summary>
    public string Name { get; } = name;

    /// <summary>Son nom unique dans le programme, d'où vient son nom C++.</summary>
    public string QualifiedName { get; } = qualifiedName ?? name;

    /// <summary>Son nom en C++ : son nom unique, sans les points d'un nom qualifié.</summary>
    public string CppName => CppNameOverride ?? QualifiedName.Replace(".", "_d_");

    /// <summary>Le nom C++ d'une instance de gear générique, dont le nom écrit (<c>clamp&lt;int&gt;</c>) ne s'écrit pas en C++ ; <see langword="null"/> sinon.</summary>
    public string? CppNameOverride { get; init; }

    /// <summary>
    /// Le chemin complet du fichier <c>.rgl.cpp</c> qui implémente le gear, pour un guard déclaré sans corps ; <see langword="null"/> pour un gear écrit en RGL.
    /// Le C++ s'exécute dans une unité compilée avec les exceptions : celles qu'il lance deviennent une <c>NativeError</c>.
    /// </summary>
    public string? NativeSource { get; init; }

    /// <summary>Vrai pour un gear dont le C++ est écrit à la main : dans un fichier <c>.rgl.cpp</c>, ou dans un descriptor.</summary>
    public bool IsNative => NativeSource is not null || NativeBody is not null || InlineCpp is not null;

    /// <summary>Vrai pour un guard dont le C++ est écrit à la main : il reçoit la position de l'appel, pour le <c>where</c> d'une <c>NativeError</c>.</summary>
    public bool IsNativeGuard => IsNative && Kind == GearKind.Guard;

    /// <summary>Le C++ d'un membre de descriptor, qui tient lieu de fichier <c>.rgl.cpp</c> ; <see langword="null"/> pour un gear écrit en RGL ou implémenté par un fichier.</summary>
    public NativeBody? NativeBody { get; init; }

    /// <summary>Le message de <c>@deprecated</c> (vide sans message) ; <see langword="null"/> pour un gear qui n'est pas obsolète.</summary>
    public string? Deprecation { get; init; }

    /// <summary>Vrai si le gear porte la métadonnée <c>[ignoreError]</c> : utilisé comme handler de rail, il avale l'erreur reçue exprès (le mode sûr n'avertit pas).</summary>
    public bool IgnoresError { get; init; }

    /// <summary>
    /// Vrai pour un train appelable (d'un fichier <c>forge: train</c>) : il se comporte comme un guard quand il
    /// peut échouer — son <see cref="Errors"/> —, mais appelé dans une expression il renvoie un
    /// résultat <c>T ! E</c>.
    /// </summary>
    public bool IsTrain { get; init; }

    public bool IsTrusted { get; init; }
    public SourceSpan? InlineCpp { get; init; }
    public bool DeclaredUnsafe { get; init; }
    public bool IsUnsafe { get; set; }

    /// <summary>Comment le gear s'exécute : ordinaire, coroutine (<c>@async</c>) ou coroutine détachée (<c>@fire</c>).</summary>
    public AsyncMode Async { get; init; }

    /// <summary>
    /// Pour un gear <c>@async</c> ou <c>@fire</c>, le type de la valeur de sa promesse (<c>void</c> sans valeur) ; <see langword="null"/> pour un gear
    /// ordinaire. Son <see cref="ReturnType"/> est alors la promesse <c>Promise&lt;T ! E&gt;</c> que l'appelant voit (<c>void</c> pour <c>@fire</c>).
    /// </summary>
    public PromiseType? Promise { get; init; }

    /// <summary>
    /// Le type que son corps renvoie : <see cref="ReturnType"/> pour un gear ordinaire ; pour un gear async, le résultat <c>T ! E</c> de sa promesse
    /// (<c>void</c> sans valeur), de sorte que <c>return 5;</c> y devient <c>ok(5)</c> et <c>return err(…);</c> reste permis.
    /// </summary>
    public IrType BodyType { get; init; } = returnType;

    /// <summary>Vrai pour un gear dont le corps est une coroutine (<c>@async</c> ou <c>@fire</c>).</summary>
    public bool IsCoroutine => Async is AsyncMode.Async or AsyncMode.Fire;

    /// <summary>Gear, guard ou emit.</summary>
    public GearKind Kind { get; } = kind;

    /// <summary>Ses paramètres, dans l'ordre.</summary>
    public IReadOnlyList<ParameterSymbol> Parameters { get; } = parameters;

    /// <summary>Son type de retour ; <c>void</c> s'il ne renvoie rien.</summary>
    public IrType ReturnType { get; } = returnType;

    /// <summary>Vrai pour un gear <c>pure</c> : aucun effet de bord.</summary>
    public bool IsPure { get; } = isPure;

    /// <summary>Son nom dans sa déclaration.</summary>
    public SourceSpan Declaration { get; } = declaration;

    /// <summary>Vrai s'il s'appelle lui-même, directement ou par d'autres gears : sa profondeur est alors limitée.</summary>
    public bool IsRecursive { get; set; }

    /// <summary>
    /// Pour un guard, le type des erreurs qu'il peut envoyer : un mold d'erreur, ou l'union de
    /// plusieurs ; <see langword="null"/> s'il ne peut pas échouer. Fixé une fois tous les corps liés,
    /// les erreurs des guards qu'il appelle comprises.
    /// </summary>
    public IrType? Errors { get; set; }

    /// <inheritdoc/>
    public override string ToString() => $"{Name}({string.Join(", ", Parameters.Select(p => p.Type))}): {ReturnType}";
}
