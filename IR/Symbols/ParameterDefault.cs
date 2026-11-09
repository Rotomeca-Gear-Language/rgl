namespace Rgl.IR.Symbols;

/// <summary>Ce qu'un paramètre de gear reçoit quand l'appel ne le donne pas.</summary>
internal enum ParameterDefault
{
    /// <summary>Rien : l'argument est obligatoire.</summary>
    None,

    /// <summary>Une expression, évaluée dans le gear appelé, qui peut lire les paramètres qui la précèdent.</summary>
    Expression,

    /// <summary><c>__CALLER__</c> : le nom du gear, du train ou de la lambda qui contient l'appel.</summary>
    Caller,

    /// <summary><c>__WHERE__</c> : <c>fichier:ligne:colonne</c> de l'appel.</summary>
    Where,

    /// <summary><c>__FILE__</c> : le nom du fichier de l'appel.</summary>
    File,

    /// <summary><c>__LINE__</c> : la ligne de l'appel.</summary>
    Line,

    /// <summary><c>params [T] reste</c> : sans argument, le tableau est vide ; les arguments en trop, ou un tableau, le remplissent.</summary>
    Params,
}
