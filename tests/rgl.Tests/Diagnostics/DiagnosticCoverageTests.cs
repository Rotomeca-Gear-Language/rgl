using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using Rgl.Diagnostics;
using Rgl.Tests.Golden;
using Rgl.Tests.Binding;
using Rgl.Tests.Syntax;
using Rgl.Tests.Text;
using Xunit.Abstractions;

namespace Rgl.Tests.Diagnostics;

/// <summary>
/// Chaque code E/W déclaré doit être déclenché et vérifié par une régression exécutable.
/// Les régressions existantes sont rejouées avec leurs assertions : une simple mention du code dans un fichier ne suffit pas.
/// </summary>
public class DiagnosticCoverageTests(ITestOutputHelper output)
{
    private sealed record Regression(string Name, IReadOnlyList<string> Codes, Action Verify);

    private static readonly Lazy<IReadOnlyList<Regression>> Regressions = new(BuildRegressions);

    private static string[] DeclaredCodes() => typeof(DiagnosticCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.IsLiteral && field.FieldType == typeof(string))
        .Select(field => (string)field.GetRawConstantValue()!)
        .Where(code => Regex.IsMatch(code, "^[EW][0-9]{3}$"))
        .Order(StringComparer.Ordinal).ToArray();

    public static TheoryData<string> Codes() => new(DeclaredCodes());

    [Fact]
    public void Chaque_code_declare_possede_une_regression_executable()
    {
        foreach (var code in DeclaredCodes())
        {
            var regression = Regressions.Value.FirstOrDefault(candidate => candidate.Codes.Contains(code));
            output.WriteLine($"{code} → {regression?.Name ?? "MANQUANT"}");
        }
        var covered = Regressions.Value.SelectMany(regression => regression.Codes).ToHashSet(StringComparer.Ordinal);
        var missing = DeclaredCodes().Where(code => !covered.Contains(code)).ToArray();
        Assert.True(missing.Length == 0,
            "Codes sans régression exécutable : " + string.Join(", ", missing) +
            ". Ajouter un test qui vérifie le diagnostic produit, puis le relier au catalogue.");
    }

    [Theory]
    [MemberData(nameof(Codes))]
    public void Le_code_est_declenche_et_verifie(string code)
    {
        var regression = Regressions.Value.FirstOrDefault(candidate => candidate.Codes.Contains(code));
        Assert.True(regression is not null, $"{code} : aucune régression exécutable enregistrée.");
        output.WriteLine($"{code} → {regression.Name}");
        try { regression.Verify(); }
        catch (Exception error)
        {
            throw new Xunit.Sdk.XunitException($"{code} — {regression.Name}\n{error}");
        }
    }

    private static IReadOnlyList<Regression> BuildRegressions()
    {
        var regressions = new List<Regression>();
        AddGoldenRegressions(regressions);
        AddDiagnosticTheories(regressions);
        AddSpecificRegressions(regressions);
        return regressions;
    }

    private static void AddGoldenRegressions(List<Regression> regressions, [CallerFilePath] string here = "")
    {
        var directory = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "Golden", "parser"));
        foreach (var path in Directory.EnumerateFiles(directory, "*.diagnostics").Order(StringComparer.Ordinal))
        {
            // Ces attendus ne comptent qu'après exécution du lexer et du parseur et comparaison complète du résultat.
            var codes = Regex.Matches(File.ReadAllText(path), @"\[(?<code>[EW][0-9]{3})\]")
                .Select(match => match.Groups["code"].Value).Distinct().ToArray();
            if (codes.Length == 0) continue;
            var file = Path.GetFileNameWithoutExtension(path) + ".rgl";
            regressions.Add(new Regression("Golden/parser/" + file, codes,
                () => new GoldenTests().Le_parseur_produit_l_arbre_et_les_diagnostics_attendus(file)));
        }
    }

    private static void AddDiagnosticTheories(List<Regression> regressions)
    {
        foreach (var type in typeof(DiagnosticCoverageTests).Assembly.GetTypes().OrderBy(type => type.FullName))
        {
            if (!type.IsClass || type.IsAbstract || type.GetConstructor(Type.EmptyTypes) is null) continue;
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).OrderBy(method => method.Name))
            {
                // Uniquement les théories ordinaires ; jamais les tests natifs conditionnels (ZigTheory).
                var theory = method.GetCustomAttributes<TheoryAttribute>().SingleOrDefault();
                if (theory is null || theory.GetType() != typeof(TheoryAttribute) || theory.Skip is not null) continue;
                var expected = method.GetParameters().Select((parameter, index) => (parameter.Name, Index: index))
                    .Where(parameter => parameter.Name is "expected" or "code" or "expectedCode").Select(parameter => parameter.Index).ToArray();
                if (expected.Length == 0) continue;
                var row = 0;
                foreach (var data in method.GetCustomAttributes<InlineDataAttribute>().SelectMany(attribute => attribute.GetData(method)))
                {
                    ++row;
                    var codes = expected.Where(index => index < data.Length).Select(index => data[index]).OfType<string>()
                        .Select(value => Regex.Match(value, @"^(?<code>[EW][0-9]{3})(?:\s|$)"))
                        .Where(match => match.Success).Select(match => match.Groups["code"].Value).Distinct().ToArray();
                    if (codes.Length == 0) continue;
                    regressions.Add(new Regression($"{type.Name}.{method.Name} [InlineData {row}]", codes, () => Invoke(type, method, data)));
                }
            }
        }
    }

    private static void Invoke(Type type, MethodInfo method, object?[] arguments)
    {
        var instance = Activator.CreateInstance(type)!;
        try
        {
            if (method.Invoke(instance, arguments) is Task task) task.GetAwaiter().GetResult();
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
        }
        finally { (instance as IDisposable)?.Dispose(); }
    }

    private static void AddSpecificRegressions(List<Regression> regressions)
    {
        // Ces tests ont des codes attendus fixes dans leurs assertions, plutôt qu'un paramètre expected/code.
        void Add(string code, Action verify, [CallerArgumentExpression(nameof(verify))] string name = "") =>
            regressions.Add(new Regression(name, [code], verify));

        Add("E009", new OverloadTests().Deux_moules_qui_surchargent_le_meme_operateur_pour_le_meme_ordre_sont_ambigus);
        Add("E010", () => new OverloadTests().Inferieur_et_superieur_vont_ensemble("bool <(P a, P b) { return a.x < b.x; }"));
        Add("E014", new BinderTests().Un_parametre_ne_recoit_pas_de_push);
        Add("E016", new ModuleTests().Un_forge_sans_en_tete_est_E016);
        Add("E019", new CheckAndCacheTests().Un_symbole_utilise_et_non_converti_est_E019_mais_ne_fait_pas_echouer_par_defaut);
        Add("E020", new NativeTests().La_liaison_dynamique_n_est_pas_disponible);
        Add("E022", new LexerTests().Un_commentaire_de_bloc_non_ferme_va_jusqu_a_la_fin_et_est_signale);
        Add("E023", new LexerTests().Un_caractere_invalide_donne_un_token_Bad_et_le_lexer_continue);
        Add("E026", new LexerTests().Un_identificateur_de_plus_de_255_caracteres_est_signale);
        Add("E028", new SourceTextTests().FromPath_signale_un_octet_UTF8_invalide_a_sa_position);
        Add("E029", () => new SourceTextTests().FromPath_refuse_l_UTF16_et_l_UTF32_en_nommant_l_encodage([0xFF, 0xFE, (byte)'f', 0], "UTF-16 LE"));
        Add("E036", new BinderTests().Un_fichier_sans_train_n_a_pas_de_train_d_entree);
        Add("E038", new BinderTests().Une_reprise_limitee_sans_suite_doit_terminer_son_chemin);
        Add("E105", new ModuleTests().Le_fichier_rgl_n_a_pas_d_en_tete_E105);
        Add("E106", new ModuleTests().Un_genre_inconnu_est_E106);
        Add("E108", new ModulesTests().Des_imports_circulaires_sont_E108);
        Add("E110", new ModuleTests().Un_train_n_appelle_que_les_trains_ecrits_au_dessus_de_lui);
        Add("E111", new ModuleTests().Des_fins_sans_type_commun_sont_E111);
        Add("E112", new ModuleTests().Un_fichier_train_sans_train_par_defaut_est_E112);
        Add("E114", new ExtensionTests().Une_contrainte_non_respectee_est_signalee);
        Add("E118", () => new OverloadTests().Un_operateur_mal_declare_est_signale("int +(P a) { return a.x; }"));
        Add("W069", new VectorizationReportTests().Une_boucle_non_vectorisee_est_un_avertissement_a_la_ligne_de_son_instruction_avec_sa_raison);
        Add("W070", new OptimizationReportTests().Le_rapport_dit_ce_qui_est_refuse_et_pourquoi);
        Add("W067", new CheckAndCacheTests().Un_cache_a_2_pour_cent_du_disque_est_signale);
        Add("E145", new NativeComponentTests().Un_guard_sans_corps_demande_son_fichier_rgl_cpp);
        Add("E146", new NativeComponentTests().Le_C_plus_plus_que_Clang_refuse_est_E146_avec_ce_que_Clang_en_dit);
        Add("E144", new NativeTests().Sans_chaine_de_compilation_un_en_tete_ne_se_lit_pas);
        Add("E119", () => new ExtensionTests().Une_extension_invalide_est_signalee("extend int { const int N: 3; }"));
        Add("E122", new ExtensionTests().Un_gear_pure_n_a_pas_de_parametre_mut);
        Add("E123", new ExtensionTests().Un_namespace_est_unique_dans_le_projet);
        Add("E135", new LifecycleTests().Le_gear_de_fin_doit_exister);
        Add("E136", new LifecycleTests().Copier_une_variable_est_une_erreur);
        Add("E137", new LifecycleTests().Une_constante_a_cycle_de_vie_est_refusee);
        Add("E138", new DatasetTests().Une_entree_en_double_est_une_erreur);
        Add("E139", new SafeModeTests().Une_erreur_sans_rail_est_refusee);
        Add("E140", new SafeModeTests().Une_reprise_bornee_dans_un_rail_doit_echouer_a_l_epuisement);
        Add("E141", new SafeModeTests().Le_resultat_ignore_d_un_await_est_une_erreur_en_mode_sur_et_un_avertissement_en_mode_libre);
        Add("E142", () => new SafeModeTests().Une_boucle_sans_limite_et_goto_sont_refuses_avec_loop_safe("train 1 |> charger* |> done;"));
        Add("E143", new SafeModeTests().Un_pas_qui_atteint_un_fire_est_refuse);
        Add("W005", new ModuleTests().Une_description_manquante_avertit_W005_sans_empecher_la_liaison);
        Add("W012", new BinderTests().Un_switch_incomplet_sur_un_enum_sans_default_avertit);
        Add("W013", new BinderTests().Un_cast_vers_le_meme_type_est_signale_inutile);
        Add("W015", () => new StdMaskingTests().Un_type_d_erreur_standard_peut_etre_masque_avec_un_avertissement("error CastError { string detail: 'x'; }"));
        Add("W016", new FileStreamTests().Un_flux_const_avertit_et_close_se_fait_ailleurs_que_dans_un_guard);
        Add("W041", new BinderTests().Un_rail_sur_une_etape_qui_ne_peut_pas_echouer_et_un_tableau_de_tableaux_sont_signales);
        Add("W053", new AsyncTests().Un_gear_async_qui_n_attend_jamais_avertit);
        Add("W060", new SpawnTests().Ignorer_le_resultat_d_un_spawn_avertit);
        Add("W061", new AnnotationTests().Un_gear_obsolete_avertit_a_chaque_usage_sauf_depuis_un_gear_obsolete);
        Add("W062", new AnnotationTests().Un_tap_before_apres_un_tap_after_avertit);
        Add("W063", new AsyncTests().Ignorer_le_resultat_d_un_await_avertit_sauf_pour_un_sleep);
        Add("W064", new AsyncTests().Appeler_un_gear_sync_depuis_une_coroutine_avertit);
        Add("W065", new SafeModeTests().Un_handler_qui_ne_lit_pas_l_erreur_et_finit_en_succes_avertit);
        Add("W066", new SafeModeTests().Un_fail_seul_envoie_l_erreur_recue_et_un_fail_avec_texte_la_perd);
    }
}
