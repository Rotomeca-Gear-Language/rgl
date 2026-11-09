using Rgl.Cli;
using Rgl.Diagnostics;

namespace Rgl.Tests;

/// <summary>Tests de l'analyse de la ligne de commande (<see cref="CommandLineParser"/>).</summary>
public class CommandLineParserTests
{
    /// <summary>Analyse les arguments et renvoie le résultat avec les diagnostics produits.</summary>
    private static (CommandLine? Result, DiagnosticBag Diagnostics) Parse(params string[] args)
    {
        var diagnostics = new DiagnosticBag();
        return (CommandLineParser.Parse(args, diagnostics), diagnostics);
    }

    [Theory]
    [InlineData]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("help")]
    public void Sans_commande_ou_avec_help_affiche_l_aide(params string[] args)
    {
        var (result, _) = Parse(args);
        Assert.Equal(CommandKind.Help, result?.Command);
    }

    [Fact]
    public void Compile_avec_un_fichier()
    {
        var (result, diagnostics) = Parse("compile", "main.rgl");
        Assert.Empty(diagnostics.Items);
        Assert.Equal(new CommandLine(CommandKind.Compile, File: "main.rgl"), result);
    }

    [Fact]
    public void Init_web_selectionne_le_squelette_web()
    {
        var (result, diagnostics) = Parse("init", "--web");
        Assert.Empty(diagnostics.Items);
        Assert.True(result?.Web);
    }

    [Fact]
    public void Exec_avec_dossier_emit_cpp_et_arguments_du_programme()
    {
        var (result, _) = Parse("exec", "-d", "src", "--emit-cpp", "--", "a", "--emit-cpp");
        Assert.NotNull(result);
        Assert.Equal(CommandKind.Exec, result.Command);
        Assert.Equal("src", result.Directory);
        Assert.True(result.EmitCpp);
        Assert.Equal(["a", "--emit-cpp"], result.ProgramArgs);
    }

    [Fact]
    public void Emit_asm_demande_l_assembleur()
    {
        var (result, _) = Parse("compile", "main.rgl", "--emit-asm");
        Assert.NotNull(result);
        Assert.True(result.EmitAsm);
        Assert.False(result.EmitCpp);
    }

    [Fact]
    public void Release_choisit_le_build_de_release_et_le_debug_est_le_defaut()
    {
        var (release, _) = Parse("exec", "main.rgl", "--release", "--", "--release");
        Assert.NotNull(release);
        Assert.True(release.Release);
        Assert.Equal(["--release"], release.ProgramArgs);

        var (debug, _) = Parse("compile", "main.rgl");
        Assert.NotNull(debug);
        Assert.False(debug.Release);
    }

    [Theory]
    [InlineData("compile")]
    [InlineData("compile", "a.rgl", "-d", "src")]
    [InlineData("compile", "a.rgl", "b.rgl")]
    [InlineData("compile", "-d")]
    [InlineData("compile", "a.rgl", "--", "x")]
    [InlineData("compile", "a.rgl", "--inconnue")]
    [InlineData("init", "dossier")]
    [InlineData("init", "--web", "autre")]
    [InlineData("build", "a.rgl")]
    public void Lignes_de_commande_invalides(params string[] args)
    {
        var (result, diagnostics) = Parse(args);
        Assert.Null(result);
        Assert.True(diagnostics.HasErrors);
    }
}
