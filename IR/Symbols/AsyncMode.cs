namespace Rgl.IR.Symbols;

/// <summary>Comment un gear s'exécute : tout de suite et jusqu'au bout, ou en coroutine.</summary>
internal enum AsyncMode
{
    /// <summary>Un gear ordinaire : son appel ne rend la main qu'une fois terminé.</summary>
    None,

    /// <summary><c>@async</c> : une coroutine ; son appel démarre tout de suite et rend une <c>Promise</c> que <c>await</c> attend.</summary>
    Async,

    /// <summary><c>@fire</c> : une coroutine détachée, dont l'appel rend la main sans rien rendre ; le programme l'attend avant de finir.</summary>
    Fire,

    /// <summary><c>@sync</c> : un gear synchrone qui bloque sur ce qui est asynchrone dans son corps : un appel de gear async, un <c>await</c>, un <c>sleep</c> y attendent le résultat.</summary>
    Sync,

    /// <summary>
    /// <c>@task</c> : un gear ordinaire dont l'appel s'exécute sur le pool de threads du runtime. L'appel rend tout de suite une <c>Promise</c> ; le corps tourne
    /// sur un autre thread, avec des copies de ses paramètres, et ne peut ni attendre, ni lancer, ni bloquer sur la boucle d'événements.
    /// </summary>
    Task,
}
