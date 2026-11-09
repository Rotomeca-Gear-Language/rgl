using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Rgl.Diagnostics;

namespace Rgl.Project;

/// <summary><c>rgl init</c> : crée le squelette d'un projet dans un dossier.</summary>
internal static class ProjectInitializer
{
    /// <summary>Fichier d'entrée du projet.</summary>
    private const string EntryFile = "main.rgl";

    /// <summary>Premier fichier <c>.forge</c>. Séparateur « / » : accepté par tous les systèmes, et affiché tel quel.</summary>
    private const string FirstForgeFile = "src/first.forge";

    /// <summary>Première page du squelette web.</summary>
    private const string WebPageFile = "src/Home.paper";

    /// <summary>Première ressource du dossier statique conventionnel.</summary>
    private const string WebStyleFile = "static/site.css";

    /// <summary>Contenu de <c>main.rgl</c> : le plus petit train d'entrée valide, la démo de la V0.</summary>
    private const string EntrySource =
        """
        // Point d'entrée du projet. Le train d'entrée reçoit les arguments de la ligne
        // de commande et renvoie le code de sortie du programme : done donne 0.
        train |> done;

        """;

    /// <summary>Contenu de <c>src/first.forge</c> : une bibliothèque de gears vide, avec son en-tête.</summary>
    private const string FirstForgeSource =
        """
        forge: gears
        description: "Premiers gears du projet"

        """;

    /// <summary>Serveur HTTP minimal du squelette web.</summary>
    private const string WebEntrySource =
        """
        use <Web>;
        import Home;

        train
        |> Web.createServer
        |> Home.mount('/')
        |> Web.staticFiles
        |> Web.listen
        |> done;

        """;

    /// <summary>Page Paper minimale du squelette web.</summary>
    private const string WebPageSource =
        """
        @rgl {
            const string title: 'RGL Web';
            reactive int count: 0;

            gear increase(DomEvent event): void {
                count +<- 1;
            }
        }

        <!doctype html>
        <html lang="fr">
        <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>@@ title @@</title>
            <link rel="stylesheet" href="/site.css">
        </head>
        <body>
            <main>
                <h1>@@ title @@</h1>
                <p>Votre première page Paper fonctionne.</p>
                <button @click="increase">Compteur : @@ count @@</button>
            </main>
        </body>
        </html>

        """;

    private const string WebStyleSource =
        """
        :root { font-family: system-ui, sans-serif; color-scheme: light dark; }
        body { margin: 0; padding: 2rem; }
        button { padding: .5rem .75rem; }

        """;

    /// <summary>
    /// Crée <c>rgl.json</c>, <c>main.rgl</c> et le premier fichier source. En mode web, ce dernier
    /// est <c>src/Home.paper</c> et l'entrée lance un serveur HTTP. Le manifeste n'a pas de clé
    /// <c>icon</c> : le programme compilé reçoit alors l'icône par défaut de RGL, sans copie dans le
    /// projet (on pourrait la déplacer ou la modifier par mégarde). N'écrase
    /// rien : si l'un d'eux existe déjà, ou si le dossier a déjà un fichier <c>.rgl</c>, rien n'est créé.
    /// Renvoie les chemins créés, relatifs au dossier, ou <c>null</c> en cas d'erreur.
    /// </summary>
    /// <param name="directory">Dossier du projet ; créé s'il n'existe pas.</param>
    /// <param name="diagnostics">Reçoit l'erreur si le dossier contient déjà un projet.</param>
    /// <param name="web">Crée le squelette HTTP et Paper au lieu du projet minimal.</param>
    public static IReadOnlyList<string>? Init(string directory, DiagnosticBag diagnostics, bool web = false)
    {
        var files = web
            ? new (string Path, byte[] Content)[]
            {
                (ProjectManifest.FileName, ManifestContent(DefaultName(directory), web: true)),
                (EntryFile, Utf8(WebEntrySource)),
                (WebPageFile, Utf8(WebPageSource)),
                (WebStyleFile, Utf8(WebStyleSource)),
            }
            :
            [
                (ProjectManifest.FileName, ManifestContent(DefaultName(directory))),
                (EntryFile, Utf8(EntrySource)),
                (FirstForgeFile, Utf8(FirstForgeSource)),
            ];

        var existing = files.Select(f => f.Path)
            .Where(p => File.Exists(Path.Combine(directory, p)))
            .Concat(Directory.Exists(directory)
                ? Directory.GetFiles(directory, "*" + RglProject.EntryExtension)
                    .Where(f => Path.GetExtension(f).Equals(RglProject.EntryExtension, StringComparison.OrdinalIgnoreCase))
                    .Select(Path.GetFileName)
                    .OfType<string>()
                : [])
            .Distinct()
            .ToArray();
        if (existing.Length > 0)
        {
            diagnostics.Error($"ce dossier contient déjà un projet ({string.Join(", ", existing)}) : rien n'a été créé", directory);
            return null;
        }

        foreach (var (path, content) in files)
        {
            var fullPath = Path.Combine(directory, path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllBytes(fullPath, content);
        }

        return files.Select(f => f.Path).ToArray();
    }

    /// <summary>Nom par défaut du projet : celui de son dossier.</summary>
    private static string DefaultName(string directory) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)));

    /// <summary>Contenu de <c>rgl.json</c>, indenté sur 4 espaces.</summary>
    private static byte[] ManifestContent(string name, bool web = false)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            IndentSize = 4,
            NewLine = "\n",
            // Garde les accents lisibles dans le nom du projet.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            writer.WriteStartObject();
            writer.WriteString("name", name);
            writer.WriteString("version", "1.0.0");
            writer.WriteString("rgl", ProjectManifest.SupportedLanguageVersion);
            // Les dossiers où la recherche des fichiers .forge ne descend pas.
            writer.WriteStartArray("exclude");
            foreach (var folder in ProjectManifest.DefaultExclude) writer.WriteStringValue(folder);
            writer.WriteEndArray();
            if (web)
            {
                writer.WriteStartArray("assets");
                writer.WriteStringValue("static");
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    /// <summary>Encode un texte en UTF-8 sans BOM, fins de ligne LF quel que soit le système.</summary>
    private static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text.ReplaceLineEndings("\n"));
}
