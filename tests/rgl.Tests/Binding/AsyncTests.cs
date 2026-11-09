using Rgl.Binding;
using Rgl.Diagnostics;
using Rgl.Emit;
using Rgl.IR;
using Rgl.Project;
using Rgl.Text;
using Rgl.Tests.Emit;
using Rgl.Tests.Runtime;

namespace Rgl.Tests.Binding;

/// <summary>
/// Tests de l'asynchrone coopératif (slice 5.5) : <c>@async</c>, <c>@fire</c>, <c>await</c>, <c>Promise&lt;T ! E&gt;</c>, <c>sleep</c>, et leurs règles.
/// </summary>
public class AsyncTests
{
    /// <summary>Lie un programme avec la std (un <c>use &lt;Errors&gt;</c> en a besoin) ; une erreur de syntaxe fait échouer le test.</summary>
    private static (IrProgram? Program, IReadOnlyList<Diagnostic> Diagnostics) Bind(string content)
    {
        using var directory = new TempDirectory();
        var diagnostics = new DiagnosticBag();
        var modules = SourceGraph.Load(new SourceText(Path.Combine(directory.Path, "main.rgl"), content), directory.Path, diagnostics);
        Assert.False(modules is null || diagnostics.Items.Any(d => d.Level == DiagnosticLevel.Error && d.Code is not null && d.Code.StartsWith("E03", StringComparison.Ordinal)),
            "erreur de syntaxe dans le cas de test : " + string.Join(", ", diagnostics.Items.Select(d => d.Format("."))));
        return (new Binder(diagnostics).Bind(modules!), diagnostics.Items);
    }

    private static string[] Errors(string content)
    {
        var (program, diagnostics) = Bind(content);
        Assert.Null(program);
        return [.. diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Code ?? "")];
    }

    private static string[] Warnings(string content)
    {
        var (program, diagnostics) = Bind(content);
        Assert.True(program is not null, string.Join(", ", diagnostics.Select(d => d.Format("."))));
        return [.. diagnostics.Where(d => d.Level == DiagnosticLevel.Warning).Select(d => d.Code ?? "")];
    }

    /// <summary>Un programme dont l'emit async termine le train d'entrée ; <paramref name="declarations"/> précède, <paramref name="body"/> est son corps.</summary>
    private static string Program(string declarations, string body) => $"{declarations}\n@async\nemit principal(): uint {{\n{body}\nreturn 0;\n}}\n!train |> principal;";

    /// <summary>Les types d'erreur de la std que les déclarations des tests nomment.</summary>
    private const string Imports = "use <Errors> only {AsyncException, AsyncKind, IoError};\n";

    private const string Sleeper = "@async\ngear dormir(duration d): int {\n    await sleep(d);\n    'fini' -> console;\n    return 1;\n}\n";

    private const string Slow = "@async\ngear lent(int a): int {\n    await sleep(10ms);\n    return a + 1;\n}\n";

    #region Liaison

    [Theory]
    [InlineData("var p: lent(1);\nvar x: await p;")]                                       // la promesse se garde, puis s'attend
    [InlineData("Promise<int> p: lent(1);\nint x: await p ?? 0;")]                          // le type s'écrit ; await donne T ! E, que ?? déballe
    [InlineData("int a: await lent(1) ?? 0;\nint b: (await lent(a) ?? 0) + (await lent(a) ?? 0);")]
    [InlineData("var r: await lent(1);\nint n: match (r) { ok v |> v; err e |> 0; };")]     // le résultat se lit comme tout résultat
    [InlineData("await sleep(5ms);\nvar t: `{await lent(1) ?? 0}`;")]
    [InlineData("var d: sleep(1s);\nawait d;\nawait d;")]                                    // une promesse terminée se relit
    public void Un_gear_async_et_ses_await_se_lient(string body)
    {
        var (program, diagnostics) = Bind(Program(Slow, body));
        Assert.True(program is not null, string.Join(", ", diagnostics.Select(d => d.Format("."))));
    }

    [Theory]
    [InlineData("@async gear f(): int { await sleep(1ms); return 1; }")]                                                    // T
    [InlineData("@async gear f(): Promise<int> { await sleep(1ms); return 1; }")]                                           // Promise<T>
    [InlineData("@async gear f(): Promise<int ! AsyncException> { await sleep(1ms); return 1; }")]                          // Promise<T ! E>
    [InlineData("@async gear f(): void { await sleep(1ms); }")]                                                             // sans valeur
    [InlineData("@async gear f(): Promise<void> { await sleep(1ms); return; }")]
    [InlineData("error Mienne { ...AsyncException; string detail; }\n@async gear f(): Promise<int ! Mienne> { await sleep(1ms); return 1; }")]
    [InlineData("@fire gear f(): void { await sleep(1ms); }")]
    [InlineData("@async gear f(string s: \"a\", uint n: 2): int { await sleep(1ms); return 1; }")]                           // paramètres par défaut
    public void Les_trois_ecritures_du_type_de_retour_et_le_fire_se_lient(string declaration)
    {
        var (program, diagnostics) = Bind(Imports + declaration + "\n@async emit principal(): uint { return 0; }\n!train |> principal;");
        Assert.True(program is not null, string.Join(", ", diagnostics.Select(d => d.Format("."))));
    }

    [Theory]
    [InlineData("await sleep(1ms);", "gear sync(): void {\nawait sleep(1ms);\n}\n", "E128")]                                // await dans un gear qui n'est pas async
    [InlineData("var x: await lent(1);", "gear sync(): void {\nvar x: await lent(1);\n}\n", "E128")]
    [InlineData("var x: await 5;", "", "E130")]                                                                              // pas une promesse
    [InlineData("var s: await \"a\";", "", "E130")]
    [InlineData("lent(1);", "", "E129")]                                                                                     // promesse jetée
    [InlineData("sleep(10ms);", "", "E129")]
    [InlineData("sleep(10ms, 5);", "", "E058")]                                                                              // sleep attend une durée
    [InlineData("sleep(5);", "", "E003")]
    [InlineData("int x: lent(1);", "", "E003")]                                                                              // une promesse n'est pas un int
    [InlineData("var x: await lent(1);\nint n: x + 1;", "", "E040")]                                                         // await donne T ! E, pas T
    public void Les_regles_de_await_et_des_promesses_sont_verifiees(string body, string extra, string code) =>
        Assert.Contains(code, Errors(Program(Slow + extra, body)));

    [Theory]
    [InlineData("@async gear pure f(): int { return 1; }", "E018")]
    [InlineData("@fire gear pure f(): void { }", "E018")]
    [InlineData("@fire gear f(): int { return 1; }", "E017")]
    [InlineData("@async @fire gear f(): void { }", "E125")]
    [InlineData("@async gear f(): Promise<int, int> { return 1; }", "E131")]
    [InlineData("@async gear f(): Promise { return 1; }", "E131")]
    [InlineData("@async gear f(): Promise<int ! IoError> { return 1; }", "E131")]                                             // IoError n'a pas les champs d'AsyncException
    [InlineData("@async gear f(): Promise<int?> { return 1; }", "E131")]
    [InlineData("@async gear f(): int { return \"a\"; }", "E003")]                                                           // le corps renvoie un int
    [InlineData("@async gear f(): int { }", "E056")]                                                                         // un chemin ne renvoie rien
    [InlineData("mold M { int x; }\n@async mold N { int x; }", "")]                                                          // pas sur un mold (pas encore pris en charge)
    public void Les_declarations_async_invalides_sont_refusees(string declaration, string code)
    {
        var errors = Errors(Imports + declaration + "\n@async emit principal(): uint { return 0; }\n!train |> principal;");
        if (code.Length == 0) Assert.NotEmpty(errors);
        else Assert.Contains(code, errors);
    }

    [Theory]
    [InlineData("@async guard f(int a): int { await sleep(1ms); return a; }")]                                                 // un guard async
    [InlineData("@async gear f(mut [int] a): void { await sleep(1ms); }")]                                                     // un paramètre mut
    [InlineData("@async gear f(): void { var g: (int x) => await sleep(1ms); }")]                                              // await dans une lambda
    public void Ce_qui_n_est_pas_encore_pris_en_charge_est_dit(string declaration)
    {
        var (program, diagnostics) = Bind(declaration + "\n@async emit principal(): uint { return 0; }\n!train |> principal;");
        Assert.Null(program);
        Assert.Contains(diagnostics, d => d.Level == DiagnosticLevel.Error && d.Message.Contains("pas encore pris en charge"));
    }

    [Fact]
    public void Un_gear_async_ne_s_appelle_pas_comme_etape_de_train()
    {
        var (program, diagnostics) = Bind(Slow + "gear sync(): uint {\nreturn 0;\n}\n!train 4 |> lent |> sync;");
        Assert.Null(program);
        Assert.Contains(diagnostics, d => d.Message.Contains("comme étape de train"));
    }

    [Fact]
    public void Un_gear_async_qui_n_attend_jamais_avertit()
    {
        Assert.Contains("W053", Warnings("@async gear paresseux(): int { return 1; }\n@async emit principal(): uint { return 0; }\n!train |> principal;"));
        Assert.DoesNotContain("W053", Warnings(Program(Slow, "var x: await lent(1);")));
    }

    [Fact]
    public void Ignorer_le_resultat_d_un_await_avertit_sauf_pour_un_sleep()
    {
        Assert.Contains("W063", Warnings(Program(Slow, "await lent(1);")));
        Assert.DoesNotContain("W063", Warnings(Program(Slow, "await sleep(1ms);")));
    }

    [Fact]
    public void Un_gear_nomme_sleep_prend_le_pas_sur_le_sleep_du_langage()
    {
        var (program, diagnostics) = Bind("gear sleep(int n): int => n;\n@async emit principal(): uint { int x: sleep(3); return 0; }\n!train |> principal;");
        Assert.True(program is not null, string.Join(", ", diagnostics.Select(d => d.Format("."))));
    }

    [Theory]
    [InlineData("var p: lent(1);\ncancel p;")]
    [InlineData("Promise<int> p: lent(1);\ncancel p;\nvar s: p.state;")]          // state se lit, en lecture seule
    [InlineData("var d: sleep(1s);\ncancel d;")]
    public void Cancel_et_state_se_lient(string body)
    {
        var (program, diagnostics) = Bind(Program(Slow, body));
        Assert.True(program is not null, string.Join(", ", diagnostics.Select(d => d.Format("."))));
    }

    [Theory]
    [InlineData("int n: 1;\ncancel n;", "E130")]                               // cancel annule une promesse
    [InlineData("var p: lent(1);\nvar s: p.inconnu;", "E074")]                  // state est le seul membre d'une promesse
    [InlineData("var p: lent(1);\np.state <- 1;", "E013")]                      // state ne s'écrit pas
    public void Les_regles_de_cancel_et_de_state_sont_verifiees(string body, string code) =>
        Assert.Contains(code, Errors(Program(Slow, body)));

    #endregion

    #region Exécution

    private static (int Code, string Output) Run(string declarations, string body) => CppEmitterTests.CompileAndRun(Program(declarations, body));

    /// <summary>Compile et exécute un programme qui importe la std (<c>use &lt;Errors&gt;</c>).</summary>
    private static (int Code, string Output) RunWithStd(string main)
    {
        using var directory = new TempDirectory();
        var diagnostics = new DiagnosticBag();
        var modules = SourceGraph.Load(new SourceText(Path.Combine(directory.Path, "main.rgl"), main), directory.Path, diagnostics);
        var program = modules is null ? null : new Binder(diagnostics).Bind(modules);
        Assert.True(program is not null, "programme invalide : " + string.Join(", ", diagnostics.Items.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Format("."))));

        var cpp = Path.Combine(directory.Path, "main.cpp");
        File.WriteAllText(cpp, CppEmitter.Emit(program, "main.rgl").Code);
        var executable = ZigBuild.Compile(directory.Path, "programme", warningsAsErrors: true, Path.Combine(ZigBuild.Runtime, "src", "console.cpp"), cpp);
        var result = ZigBuild.RunWithInput(executable, null);
        return (result.Code, result.Output.Replace("\r\n", "\n"));
    }

    /// <summary>Compile et exécute un programme qui lit ou écrit des fichiers ; <c>%P%</c> y est le chemin d'un fichier du dossier de travail.</summary>
    private static (int Code, string Output) RunWithFiles(string main)
    {
        using var directory = new TempDirectory();
        main = main.Replace("%P%", directory.Path.Replace('\\', '/') + "/f.txt").Replace("%ZIG%", ZigBuild.Zig.Replace('\\', '/'));
        var diagnostics = new DiagnosticBag();
        var modules = SourceGraph.Load(new SourceText(Path.Combine(directory.Path, "main.rgl"), main), directory.Path, diagnostics);
        var program = modules is null ? null : new Binder(diagnostics).Bind(modules);
        Assert.True(program is not null, "programme invalide : " + string.Join(", ", diagnostics.Items.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Format("."))));

        var cpp = Path.Combine(directory.Path, "main.cpp");
        File.WriteAllText(cpp, CppEmitter.Emit(program, "main.rgl").Code);
        var executable = ZigBuild.Compile(directory.Path, "programme", warningsAsErrors: true, Path.Combine(ZigBuild.Runtime, "src", "console.cpp"), Path.Combine(ZigBuild.Runtime, "src", "file.cpp"),
            Path.Combine(ZigBuild.Runtime, "src", "process.cpp"), cpp);
        var result = ZigBuild.RunWithInput(executable, null);
        return (result.Code, result.Output.Replace("\r\n", "\n"));
    }

    [ZigFact]
    public void Un_gear_async_rend_son_resultat_a_l_await()
    {
        var (code, output) = Run(Slow, "var a: await lent(1);\nint b: await lent(10) ?? 0;\n`{a} {b}` -> console;");

        Assert.Equal(0, code);
        Assert.Equal("ok(2) 11", output);
    }

    [ZigFact]
    public void Deux_gears_async_qui_attendent_en_meme_temps_durent_le_temps_du_plus_long()
    {
        var (code, output) = Run(
            "@async\ngear lent(int a, duration d): int {\n    await sleep(d);\n    return a + 1;\n}\n",
            "var debut <- clock;\nvar a: lent(1, 150ms);\nvar b: lent(10, 150ms);\nint x: await a ?? 0;\nint y: await b ?? 0;\nvar fin <- clock;\n"
            + "duration ecoule: fin - debut;\n`{x} {y} {ecoule >= 140ms} {ecoule < 260ms}` -> console;");

        Assert.Equal(0, code);
        Assert.Equal("2 11 true true", output);
    }

    [ZigFact]
    public void Les_taches_reprennent_dans_l_ordre_de_leurs_echeances()
    {
        var (code, output) = Run(
            "@async\ngear bavard(string nom, duration d): string {\n    await sleep(d);\n    `{nom}` -> console;\n    return nom;\n}\n",
            "var a: bavard('A', 90ms);\nvar b: bavard('B', 30ms);\nvar c: bavard('C', 60ms);\nawait a;\nawait b;\nawait c;");

        Assert.Equal(0, code);
        Assert.Equal("BCA", output);
    }

    [ZigFact]
    public void Un_fire_est_attendu_avant_la_fin_du_programme()
    {
        var (code, output) = Run(
            "@fire\ngear signal(duration d): void {\n    await sleep(d);\n    'fin' -> console;\n}\n",
            "signal(40ms);\n'debut ' -> console;");

        Assert.Equal(0, code);
        Assert.Equal("debut fin", output);
    }

    [ZigFact]
    public void Une_promesse_que_personne_n_attend_est_attendue_avant_la_fin_du_programme()
    {
        var (code, output) = Run(
            "@async\ngear lent(int a): int {\n    await sleep(30ms);\n    'tard' -> console;\n    return a;\n}\n",
            "var p: lent(1);\n'tot ' -> console;");

        Assert.Equal(0, code);
        Assert.Equal("tot tard", output);
    }

    [ZigFact]
    public void Un_await_dans_une_expression_compose_les_resultats_dans_l_ordre()
    {
        var (code, output) = Run(Slow, "int total: (await lent(1) ?? 0) * 10 + (await lent(2) ?? 0);\nvar t: `{await lent(5) ?? 0}/{await lent(6) ?? 0}`;\n`{total} {t}` -> console;");

        Assert.Equal(0, code);
        Assert.Equal("23 6/7", output);
    }

    [ZigFact]
    public void Un_gear_async_peut_renvoyer_une_erreur_que_l_appelant_lit()
    {
        var (code, output) = RunWithStd(
            "use <Errors> only {AsyncException, AsyncKind};\n"
            + "@async\ngear racine(int n): Promise<int ! AsyncException> {\n    await sleep(1ms);\n"
            + "    if (n < 0) {\n        return err({message: 'négatif', kind: AsyncKind.Aborted});\n    }\n    return n * 2;\n}\n"
            + "@async\nemit principal(): uint {\n"
            + "    var bon: await racine(21);\n    var mauvais: await racine(-1);\n"
            + "    string a: match (bon) { ok v |> `ok {v}`; err e |> `err {e.message}`; };\n"
            + "    string b: match (mauvais) { ok v |> `ok {v}`; err e |> `err {e.message} {e.kind}`; };\n"
            + "    `{a} | {b}` -> console;\n    return 5;\n}\n!train |> principal;");

        Assert.Equal(5, code);
        Assert.Equal("ok 42 | err négatif Aborted", output);
    }

    [ZigFact]
    public void Un_gear_async_sans_valeur_rend_null_quand_tout_s_est_bien_passe()
    {
        var (code, output) = RunWithStd(Program(
            "use <Errors> only {AsyncException};\n@async\ngear attendre(duration d): void {\n    await sleep(d);\n}\n",
            "AsyncException? e: await attendre(5ms);\n`{e eq null}` -> console;"));

        Assert.Equal(0, code);
        Assert.Equal("true", output);
    }

    [ZigFact]
    public void Un_gear_async_peut_s_appeler_lui_meme()
    {
        var (code, output) = Run(
            "@async\ngear factorielle(int n): int {\n    if (n <= 1) {\n        return 1;\n    }\n    int dessous: await factorielle(n - 1) ?? 1;\n    return n * dessous;\n}\n",
            "`{await factorielle(6) ?? 0}` -> console;");

        Assert.Equal(0, code);
        Assert.Equal("720", output);
    }

    [ZigFact]
    public void La_valeur_de_l_emit_async_est_le_code_de_sortie()
    {
        var (code, output) = CppEmitterTests.CompileAndRun("@async\nemit principal(): uint {\n    await sleep(5ms);\n    'ok' -> console;\n    return 3;\n}\n!train |> principal;");

        Assert.Equal(3, code);
        Assert.Equal("ok", output);
    }

    [ZigFact]
    public void Les_parametres_d_une_coroutine_sont_copies_dans_son_cadre()
    {
        // Le texte temporaire est détruit à la fin de l'instruction : la coroutine en garde sa propre copie.
        var (code, output) = Run(
            "@async\ngear dire(string texte): string {\n    await sleep(20ms);\n    return `{texte}!`;\n}\n",
            "var p: dire(`{1 + 2} fois`);\nstring s: await p ?? '';\n`{s}` -> console;");

        Assert.Equal(0, code);
        Assert.Equal("3 fois!", output);
    }


    [ZigFact]
    public void Annuler_une_tache_qui_attend_l_arrete_tout_de_suite()
    {
        var (code, output) = Run(Sleeper,
            "var debut <- clock;\nvar p: dormir(5s);\nvar avant: p.state;\ncancel p;\nvar r: await p;\n"
            + "string genre: match (r) { ok v |> 'ok'; err e |> `{e.kind}`; };\nvar fin <- clock;\n"
            + "`{avant} {p.state} {genre} {fin - debut < 2s}` -> console;");

        Assert.Equal(0, code);
        Assert.Equal("Suspended Cancelled Cancelled true", output);
    }

    [ZigFact]
    public void L_annulation_suit_ce_que_la_tache_attend()
    {
        var (code, output) = Run(
            Sleeper + "@async\ngear dessus(duration d): int {\n    var r: await dormir(d);\n    'jamais' -> console;\n    return 7;\n}\n",
            "var debut <- clock;\nvar p: dessus(5s);\ncancel p;\nvar r: await p;\n"
            + "string genre: match (r) { ok v |> 'ok'; err e |> `{e.kind}`; };\nvar fin <- clock;\n"
            + "`{p.state} {genre} {fin - debut < 2s}` -> console;");

        Assert.Equal(0, code);
        Assert.Equal("Cancelled Cancelled true", output);
    }

    [ZigFact]
    public void Une_tache_en_annule_une_autre_et_celui_qui_l_attend_recoit_l_erreur()
    {
        var (code, output) = Run(
            Sleeper
            + "@async\ngear annuleur(Promise<int> cible, duration d): void {\n    await sleep(d);\n    cancel cible;\n}\n"
            + "@async\ngear surveillant(Promise<int> cible): string {\n    var r: await cible;\n"
            + "    string genre: match (r) { ok v |> 'ok'; err e |> `{e.kind}`; };\n    return genre;\n}\n",
            "var p: dormir(5s);\nvar s: surveillant(p);\nvar a: annuleur(p, 20ms);\nstring verdict: await s ?? '';\nawait a;\n`{verdict} {p.state}` -> console;");

        Assert.Equal(0, code);
        Assert.Equal("Cancelled Cancelled", output);
    }

    [ZigFact]
    public void Annuler_une_tache_finie_ne_change_rien()
    {
        var (code, output) = Run(Slow, "var p: lent(1);\nvar r: await p;\ncancel p;\nint n: await p ?? 0;\n`{p.state} {n}` -> console;");

        Assert.Equal(0, code);
        Assert.Equal("Completed 2", output);
    }

    [ZigFact]
    public void Readasync_et_writeasync_passent_par_le_pool_et_rendent_leur_resultat()
    {
        var (code, output) = RunWithFiles(Program("",
            "var ecrit: await writeAsync('%P%', 'bonjour');\nvar lu: await readAsync('%P%');\n"
            + "string texte: match (lu) { ok v |> v; err e |> e.message; };\n`{ecrit eq null} {texte}` -> console;"));

        Assert.Equal(0, code);
        Assert.Equal("true bonjour", output);
    }

    [ZigFact]
    public void Un_fichier_absent_est_une_asyncexception_de_genre_ioerror()
    {
        var (code, output) = RunWithFiles(Program("",
            "var lu: await readAsync('%P%');\nstring genre: match (lu) { ok v |> 'ok'; err e |> `{e.kind}`; };\n`{genre}` -> console;"));

        Assert.Equal(0, code);
        Assert.Equal("IoError", output);
    }

    [ZigFact]
    public void Une_lecture_asynchrone_ne_bloque_pas_les_autres_taches()
    {
        var (code, output) = RunWithFiles(Program(
            "@async\ngear bavard(duration d): void {\n    await sleep(d);\n    'tic ' -> console;\n}\n",
            "var a: bavard(10ms);\nvar e: await writeAsync('%P%', 'x');\nvar l: await readAsync('%P%');\nawait a;\n'fin' -> console;"));

        Assert.Equal(0, code);
        Assert.EndsWith("fin", output);
    }

    [Theory]
    [InlineData("@sync gear f(): int { return 1; }")]
    [InlineData("@sync guard f(int a): int { return a; }")]
    [InlineData("@async gear f<T>(T a): T { await sleep(1ms); return a; }\ngear g(): int { return 1; }")]                // un gear async générique
    [InlineData("@fire gear f<T>(T a): void { await sleep(1ms); }")]
    public void Sync_et_les_gears_async_generiques_se_lient(string declaration)
    {
        var (program, diagnostics) = Bind(Imports + declaration + "\n@async emit principal(): uint { return 0; }\n!train |> principal;");
        Assert.True(program is not null, string.Join(", ", diagnostics.Select(d => d.Format("."))));
    }

    [Theory]
    [InlineData("@sync gear pure f(): int { return 1; }", "E018")]
    [InlineData("@sync @async gear f(): int { return 1; }", "E125")]
    public void Les_declarations_sync_invalides_sont_refusees(string declaration, string code) =>
        Assert.Contains(code, Errors(Imports + declaration + "\n@async emit principal(): uint { return 0; }\n!train |> principal;"));

    [Fact]
    public void Appeler_un_gear_sync_depuis_une_coroutine_avertit()
    {
        var warnings = Warnings(Slow + "@sync\ngear bloque(): int { return await lent(1) ?? 0; }\n" + Program("", "int n: bloque();"));
        Assert.Contains("W064", warnings);
    }

    [ZigFact]
    public void Un_gear_sync_attend_sur_place_ce_qui_est_asynchrone_a_l_interieur()
    {
        var (code, output) = Run(
            Slow + "@sync\ngear bloque(int n): int {\n    var r: lent(n);\n    sleep(5ms);\n    int s: await lent(r ?? 0) ?? 0;\n    return s;\n}\n"
            + "gear pasasync(): void {\n    `{bloque(1)}` -> console;\n}\n",
            "pasasync();");

        Assert.Equal(0, code);
        Assert.Equal("3", output);
    }

    [ZigFact]
    public void Un_gear_async_generique_s_instancie_et_se_suspend()
    {
        var (code, output) = RunWithStd(Program(
            "use <Errors> only {AsyncException};\n@async\ngear apres<T>(T valeur, duration d): T {\n    await sleep(d);\n    return valeur;\n}\n",
            "var a: apres(7, 5ms);\nvar b: apres('x', 5ms);\nint n: await a ?? 0;\nstring s: await b ?? '';\n`{n}{s}` -> console;"));

        Assert.Equal(0, code);
        Assert.Equal("7x", output);
    }

    [ZigFact]
    public void Un_gear_generique_recoit_une_promesse_et_en_deduit_son_type()
    {
        var (code, output) = RunWithStd(Program(
            "use <Errors> only {AsyncException};\n@async\ngear attendre<T>(Promise<T> p): Promise<T> {\n    return await p;\n}\n" + Slow,
            "var r: await attendre(lent(4));\nint n: match (r) { ok v |> v; err e |> -1; };\n`{n}` -> console;"));

        Assert.Equal(0, code);
        Assert.Equal("5", output);
    }

    [ZigFact]
    public void Whenall_attend_toutes_les_promesses_et_rend_leurs_resultats_dans_l_ordre()
    {
        var (code, output) = RunWithStd(Program(
            "use <Async> only {whenAll};\nuse <Errors> only {AsyncException};\n" + Slow.Replace("10ms", "30ms") + "@async\ngear court(int a): int {\n    await sleep(5ms);\n    return a * 10;\n}\n",
            "var debut <- clock;\nvar a: lent(1);\nvar b: court(2);\nvar c: lent(3);\n[int ! AsyncException] vide: [];\nvar tous: await whenAll([a, b, c]) ?? vide;\nvar fin <- clock;\n"
            + "string s: tous |+> '' with (string acc, r) => match (r) { ok v |> `{acc}{v} `; err e |> `{acc}! `; };\n`{s}{fin - debut < 200ms}` -> console;"));

        Assert.Equal(0, code);
        Assert.Equal("2 20 4 true", output);
    }

    [ZigFact]
    public void Whenany_rend_le_resultat_de_la_premiere_promesse_finie_et_laisse_les_autres_continuer()
    {
        var (code, output) = Run(
            "@async\ngear apres(int valeur, duration d): int {\n    await sleep(d);\n    `{valeur} ` -> console;\n    return valeur;\n}\n",
            "var lent: apres(1, 120ms);\nvar vite: apres(2, 10ms);\nvar premier: await whenAny([lent, vite]);\nint n: match (premier) { ok v |> v; err e |> -1; };\n"
            + "`premier={n} ` -> console;\nawait lent;\n'fin' -> console;");

        Assert.Equal(0, code);
        Assert.Equal("2 premier=2 1 fin", output);
    }

    [ZigFact]
    public void Plusieurs_taches_peuvent_attendre_la_meme_promesse()
    {
        var (code, output) = Run(
            "@async\ngear apres(int valeur, duration d): int {\n    await sleep(d);\n    return valeur;\n}\n"
            + "@async\ngear lecteur(Promise<int> p, string nom): void {\n    var r: await p;\n    `{nom} ` -> console;\n}\n",
            "var p: apres(5, 20ms);\nvar a: lecteur(p, 'a');\nvar b: lecteur(p, 'b');\nvar c: lecteur(p, 'c');\nawait a;\nawait b;\nawait c;\n'fin' -> console;");

        Assert.Equal(0, code);
        Assert.Equal("a b c fin", output);
    }

    [Theory]
    [InlineData("var x: whenAny(5);", "E130")]
    [InlineData("var x: whenAny();", "E058")]
    [InlineData("var x: whenAny([1, 2]);", "E130")]
    public void Les_regles_de_whenany_sont_verifiees(string body, string code) => Assert.Contains(code, Errors(Program(Slow, body)));

    private const string Squares = "@task\ngear carre(int n): int {\n    return n * n;\n}\n";

    [Theory]
    [InlineData("@task gear f(int a): int { return a + 1; }")]
    [InlineData("@task gear f<T>(T a): T { return a; }\ngear g(): int { return 1; }")]
    [InlineData("@task gear f(string s): void { s -> console; }")]
    [InlineData("@task gear f([int] xs): Promise<int ! AsyncException> { return err({message: 'non', kind: AsyncKind.Aborted}); }")]
    public void Un_gear_task_se_lie(string declaration)
    {
        var (program, diagnostics) = Bind(Imports + declaration + "\n@async emit principal(): uint { return 0; }\n!train |> principal;");
        Assert.True(program is not null, string.Join(", ", diagnostics.Select(d => d.Format("."))));
    }

    [Theory]
    [InlineData("@task gear pure f(): int { return 1; }", "E018")]
    [InlineData("@task @async gear f(): int { return 1; }", "E125")]
    [InlineData("@task gear f(Promise<int> p): int { return 1; }", "E132")]                                                    // une promesse ne se copie pas pour de bon
    [InlineData("@task gear f(): int { var p: sleep(1ms); return 1; }", "E132")]                                               // il ne lance rien sur la boucle
    [InlineData("@task gear f(): int { return await sleep(1ms) ?? 0; }", "E128")]                                              // ni n'attend
    [InlineData("@async gear a(): int { await sleep(1ms); return 1; }\ngear plain(): int { var p: a(); return 1; }\n@task gear f(): int { return plain(); }", "E132")]   // même par un gear ordinaire
    [InlineData("@sync gear s(): int { return 1; }\ngear plain(): int { return s(); }\n@task gear f(): int { return plain(); }", "E132")]
    [InlineData("@task gear g(): int { return 1; }\n@task gear f(): int { var p: g(); return 1; }", "E132")]                    // pas de tâche dans une tâche
    public void Les_regles_de_task_sont_verifiees(string declaration, string code) =>
        Assert.Contains(code, Errors(Imports + declaration + "\n@async emit principal(): uint { return 0; }\n!train |> principal;"));

    [Theory]
    [InlineData("@task gear f(() => int g): int { return g(); }", "")]                                                          // une fonction qui peut tout faire : refusée
    [InlineData("@task gear f(([int]) => int g): int { return 1; }", "")]
    public void Un_task_ne_recoit_pas_une_fonction_qui_n_est_pas_pure(string declaration, string unused)
    {
        _ = unused;
        Assert.Contains("E132", Errors(Imports + declaration + "\n@async emit principal(): uint { return 0; }\n!train |> principal;"));
    }

    [Fact]
    public void Un_task_recoit_une_fonction_pure()
    {
        var (program, diagnostics) = Bind(Imports + "@task gear f(pure (int) => int g): int { return g(2); }\n@async emit principal(): uint { var p: f((x) => x + 1);\nawait p;\nreturn 0; }\n!train |> principal;");
        Assert.True(program is not null, string.Join(", ", diagnostics.Select(d => d.Format("."))));
    }

    [Fact]
    public void Une_lambda_qui_lance_une_promesse_ne_passe_pas_a_un_task_a_l_appel()
    {
        // La lambda crée une promesse sur la boucle : elle ne peut pas s'exécuter sur un thread du pool.
        Assert.Contains("E132", Errors(Program("gear lance(() => int g): int { return g(); }", "var p: task lance(() => { var s: sleep(1ms);\nreturn 1; });\nawait p;")));
    }

    [Theory]
    [InlineData("@fire gear signal(): void {\n    await lent(1);\n}\n", "E133")]                                                  // l'erreur d'un await ignoré est perdue
    [InlineData("@async gear vide(): void { await sleep(1ms); }\n@fire gear signal(): void {\n    await vide();\n}\n", "E133")]    // un void donne E? : perdu aussi
    public void Un_fire_ne_peut_pas_ignorer_l_erreur_d_un_await(string declarations, string code) =>
        Assert.Contains(code, Errors(Imports + Slow + declarations + "@async emit principal(): uint { return 0; }\n!train |> principal;"));

    [Theory]
    [InlineData("@fire gear signal(): void {\n    await sleep(1ms);\n}\n")]                                                       // un sleep ne peut pas échouer
    [InlineData("@fire gear signal(): void {\n    var r: await lent(1);\n    int n: r ?? 0;\n    `{n}` -> console;\n}\n")]          // l'erreur est traitée dans le corps
    [InlineData("@async gear normal(): void {\n    await lent(1);\n}\n")]                                                         // un @async ordinaire : un simple avertissement
    public void Un_fire_qui_gere_ses_erreurs_se_lie(string declarations)
    {
        var (program, diagnostics) = Bind(Imports + Slow + declarations + "@async emit principal(): uint { return 0; }\n!train |> principal;");
        Assert.True(program is not null, string.Join(", ", diagnostics.Select(d => d.Format("."))));
    }

    [Fact]
    public void Async_a_l_appel_est_refuse_avec_ce_qu_il_faut_ecrire()
    {
        using var directory = new TempDirectory();
        var diagnostics = new DiagnosticBag();
        SourceGraph.Load(new SourceText(Path.Combine(directory.Path, "main.rgl"), Program(Slow, "var p: async lent(1);\nawait p;")), directory.Path, diagnostics);
        Assert.Contains(diagnostics.Items, d => d.Code == "E070" && d.Help.Any(h => h.Contains("@async")));
    }

    [Fact]
    public void Un_gear_task_ne_s_appelle_pas_comme_etape_de_train()
    {
        var (program, diagnostics) = Bind(Squares + "gear sync(): uint {\nreturn 0;\n}\n!train 4 |> carre |> sync;");
        Assert.Null(program);
        Assert.Contains(diagnostics, d => d.Message.Contains("comme étape de train"));
    }

    [ZigFact]
    public void Un_gear_task_rend_une_promesse_que_l_on_attend()
    {
        var (code, output) = Run(Squares, "var a: carre(3);\nvar b: carre(4);\nint x: await a ?? 0;\nint y: await b ?? 0;\n`{x} {y}` -> console;");

        Assert.Equal(0, code);
        Assert.Equal("9 16", output);
    }

    [ZigFact]
    public void Beaucoup_de_taches_donnent_chacune_leur_resultat()
    {
        // Des durées inégales : le vol de travail répartit, le résultat de chacune reste le sien.
        var (code, output) = Run(
            "@task\ngear somme(int n): int {\n    int total: 0;\n    int i: 0;\n    switch (0) {\n        default:\n    }\n    return n * (n + 1) / 2;\n}\n",
            "var a: somme(10);\nvar b: somme(100);\nvar c: somme(1000);\nvar d: somme(5);\nvar e: somme(50);\n"
            + "int s: (await a ?? 0) + (await b ?? 0) + (await c ?? 0) + (await d ?? 0) + (await e ?? 0);\n`{s}` -> console;");

        Assert.Equal(0, code);
        Assert.Equal("506895", output);   // 55 + 5050 + 500500 + 15 + 1275
    }

    [ZigFact]
    public void Un_gear_task_peut_ecrire_sur_la_console_et_etre_annule()
    {
        var (code, output) = Run(
            "@task\ngear dire(string t): void {\n    t -> console;\n}\n",
            "var a: dire('un ');\nawait a;\nvar b: dire('deux');\ncancel b;\nawait b;\n`{b.state eq b.state}` -> console;");

        Assert.Equal(0, code);
        Assert.StartsWith("un ", output);
    }

    [ZigFact]
    public void Des_taches_qui_ecrivent_en_meme_temps_ne_melangent_pas_leurs_ecritures()
    {
        var (code, output) = Run(
            "@task\ngear dire(string t): void {\n    t -> console;\n}\n",
            "var a: dire('AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA');\nvar b: dire('BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB');\n"
            + "var c: dire('CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC');\nvar d: dire('DDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD');\nawait a;\nawait b;\nawait c;\nawait d;");

        Assert.Equal(0, code);
        Assert.Equal(4 * 48, output.Length);
        Assert.All(new[] { 'A', 'B', 'C', 'D' }, letter => Assert.Equal(new string(letter, 48), output.Substring(output.IndexOf(letter), 48)));
    }

    private const string Plain = "gear carre(int n): int {\n    return n * n;\n}\ngear dis(string t): void {\n    t -> console;\n}\n";

    [Theory]
    [InlineData("var p: task carre(3);\nawait p;")]
    [InlineData("var p: task carre(3);\nint x: await p ?? 0;\nvar q: task dis('a');\nawait q;")]
    [InlineData("int x: await task carre(2) ?? 0;")]
    public void Task_devant_un_appel_se_lie(string body)
    {
        var (program, diagnostics) = Bind(Program(Plain, body));
        Assert.True(program is not null, string.Join(", ", diagnostics.Select(d => d.Format("."))));
    }

    [Theory]
    [InlineData("task carre(3);", "E129")]                                                                                  // une promesse jetée
    [InlineData("var p: task lent(1);", "E132")]                                                                            // déjà async
    [InlineData("var p: task carre(1) + 1;", "E040")]
    public void Les_regles_de_task_a_l_appel_sont_verifiees(string body, string code) => Assert.Contains(code, Errors(Program(Slow + Plain, body)));

    [Theory]
    [InlineData("gear brut(mut [int] xs): void { }", "mut [int] t: [1];\nvar p: task brut(t);")]                         // un paramètre mut
    [InlineData("@sync gear s(): int { return 1; }", "var p: task s();")]                                                    // @sync
    [InlineData("@async gear a(): int { await sleep(1ms); return 1; }\ngear plain(): int { var p: a(); return 1; }", "var p: task plain();")]   // atteint de l'async
    [InlineData("gear garde(Promise<int> p): int { return 1; }", "var q: lent(1);\nvar p: task garde(q);")]                // une promesse en argument
    public void Task_a_l_appel_refuse_ce_qui_partage_un_etat_ou_touche_a_la_boucle(string declarations, string body) =>
        Assert.NotEmpty(Errors(Program(Slow + declarations, body)));

    [ZigFact]
    public void Task_devant_un_appel_execute_un_gear_ordinaire_sur_le_pool()
    {
        var (code, output) = Run(Plain, "var a: task carre(6);\nvar b: task carre(7);\nvar c: task dis('ok ');\nint x: await a ?? 0;\nint y: await b ?? 0;\nawait c;\n`{x} {y}` -> console;");

        Assert.Equal(0, code);
        Assert.Equal("ok 36 49", output);
    }

    [ZigFact]
    public void Un_gear_sync_attend_un_task_a_l_appel()
    {
        var (code, output) = Run(
            Plain + "@sync\ngear calcul(int n): int {\n    return task carre(n) ?? 0;\n}\ngear affiche(): void {\n    `{calcul(9)}` -> console;\n}\n",
            "affiche();");

        Assert.Equal(0, code);
        Assert.Equal("81", output);
    }

    [ZigFact]
    public void Un_gear_sync_appelle_un_task_et_attend_son_resultat()
    {
        var (code, output) = Run(
            Squares + "@sync\ngear calcul(int n): int {\n    return carre(n) ?? 0;\n}\ngear affiche(): void {\n    `{calcul(7)}` -> console;\n}\n",
            "affiche();");

        Assert.Equal(0, code);
        Assert.Equal("49", output);
    }

    private const string ProcessImports = "use <Process> only {ProcessResult};\n";

    [ZigFact]
    public void Spawnasync_lance_un_processus_sans_bloquer_les_autres_taches()
    {
        var (code, output) = RunWithFiles(Program(
            ProcessImports + "@async\ngear bavard(duration d): void {\n    await sleep(d);\n    'tic ' -> console;\n}\n",
            "var a: bavard(10ms);\nvar r: await spawnAsync('%ZIG%', 'version');\nawait a;\n"
            + "string s: match (r) { ok p |> `ok {p.exitCode} {p.stdout.length > 0}`; err e |> `err {e.kind}`; };\n`{s}` -> console;"));

        Assert.Equal(0, code);
        Assert.Equal("tic ok 0 true", output);
    }

    [ZigFact]
    public void Un_executable_introuvable_est_une_asyncexception_de_genre_ioerror()
    {
        var (code, output) = RunWithFiles(Program(ProcessImports,
            "var r: await spawnAsync('%P%-inexistant');\nstring s: match (r) { ok p |> 'ok'; err e |> `{e.kind}`; };\n`{s}` -> console;"));

        Assert.Equal(0, code);
        Assert.Equal("IoError", output);
    }

    [Theory]
    [InlineData("var r: spawnAsync();", "E058")]
    [InlineData("var r: spawnAsync(5);", "E003")]
    [InlineData("var r: spawnAsync('rgl', 'compile');", "E124")]
    public void Les_regles_de_spawnasync_sont_verifiees(string body, string code) =>
        Assert.Contains(code, Errors(Program(ProcessImports, body)));

    [ZigFact]
    public void Writeasync_et_readbytesasync_passent_des_octets()
    {
        var (code, output) = RunWithFiles(Program("",
            "var texte: 'héé' as bytes;\nvar ecrit: await writeAsync('%P%', texte);\nvar lu: await readBytesAsync('%P%');\n"
            + "string s: match (lu) { ok v |> `{v.length}`; err e |> e.message; };\n`{s}` -> console;"));

        Assert.Equal(0, code);
        Assert.Equal("5", output);   // h, é, é : cinq octets
    }

    [ZigFact]
    public void Les_operations_sur_le_meme_fichier_gardent_leur_ordre()
    {
        // Lancées sans attendre : la lecture, lancée après les écritures, voit la dernière.
        var (code, output) = RunWithFiles(Program("",
            "var a: writeAsync('%P%', 'premier');\nvar b: writeAsync('%P%', 'second');\nvar c: readAsync('%P%');\nawait a;\nawait b;\nvar lu: await c;\n"
            + "string s: match (lu) { ok v |> v; err e |> e.message; };\n`{s}` -> console;"));

        Assert.Equal(0, code);
        Assert.Equal("second", output);
    }

    [ZigFact]
    public void Spawnasyncwith_regle_les_options_du_processus()
    {
        var (code, output) = RunWithFiles(Program(ProcessImports,
            "var r: await spawnAsyncWith('timeout=60000', '%ZIG%', 'version');\nstring s: match (r) { ok p |> `ok {p.exitCode}`; err e |> `{e.kind}`; };\n"
            + "var lent: await spawnAsyncWith('timeout=1', '%ZIG%', 'zen');\nstring t: match (lent) { ok p |> `ok`; err e |> `{e.kind}`; };\n`{s} {t.length > 0}` -> console;"));

        Assert.Equal(0, code);
        Assert.Equal("ok 0 true", output);
    }

    [Theory]
    [InlineData("var r: spawnAsyncWith('timeout=5', 'make');")]
    [InlineData("var b: readBytesAsync('a.bin');\nawait b;")]
    [InlineData("var e: await writeAsync('a.bin', 'x' as bytes);")]
    public void Les_octets_et_les_options_se_lient(string body)
    {
        var (program, diagnostics) = Bind(Program(ProcessImports + Slow, body));
        Assert.True(program is not null, string.Join(", ", diagnostics.Select(d => d.Format("."))));
    }

    [Theory]
    [InlineData("var r: spawnAsyncWith('pas=une-option', 'make');", "E124")]
    [InlineData("var r: spawnAsyncWith('timeout=5');", "E058")]
    [InlineData("var r: spawnAsyncWith('timeout=5', 'rgl');", "E124")]
    [InlineData("var b: readBytesAsync();", "E058")]
    public void Les_regles_des_octets_et_des_options_sont_verifiees(string body, string code) => Assert.Contains(code, Errors(Program(ProcessImports + Slow, body)));

    [Theory]
    [InlineData("var t: readAsync('a.txt');\nawait t;")]
    [InlineData("await writeAsync('a.txt', 'x');")]
    public void Readasync_et_writeasync_se_lient(string body)
    {
        var (program, diagnostics) = Bind(Program(Slow, body));
        Assert.True(program is not null, string.Join(", ", diagnostics.Select(d => d.Format("."))));
    }

    [Theory]
    [InlineData("var t: readAsync();", "E058")]
    [InlineData("var t: readAsync(5);", "E003")]
    [InlineData("var t: writeAsync('a');", "E058")]
    public void Les_regles_de_readasync_et_writeasync_sont_verifiees(string body, string code) =>
        Assert.Contains(code, Errors(Program(Slow, body)));

    #endregion
}
