using Rgl.Binding;
using Rgl.Diagnostics;
using Rgl.Emit;
using Rgl.IR;
using Rgl.Project;
using Rgl.Text;

namespace Rgl.Tests.Binding;

/// <summary>Un fichier <c>forge: extend</c> s'applique à tout le programme sans être importé, et ses gears inutilisés ne sont pas émis.</summary>
public class ExtensionFileTests
{
    private const string Extensions = """
        forge: extend
        description: "test"

        namespace Test/Ext;

        extend<T> [T] {
            gear pure second(this [T] v): T => v[1];
            gear pure third(this [T] v): T => v[2];
        }

        extend string {
            gear pure shout(this string s): string => `{s}!`;
        }
        """;

    private static (IrProgram? Program, DiagnosticBag Diagnostics) Bind(string main)
    {
        using var directory = new TempDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "ext.forge"), Extensions);
        var diagnostics = new DiagnosticBag();
        var modules = SourceGraph.Load(new SourceText(Path.Combine(directory.Path, "main.rgl"), main), directory.Path, diagnostics);
        return (modules is null ? null : new Binder(diagnostics).Bind(modules), diagnostics);
    }

    [Fact]
    public void Une_extension_s_applique_sans_import()
    {
        var (program, diagnostics) = Bind("""
            gear f([int] xs): int => xs.second();
            gear g(string s): string => s.shout();
            !train |> done;
            """);

        Assert.True(program is not null, string.Join(", ", diagnostics.Items.Select(d => d.Format("."))));
    }

    [Fact]
    public void Seuls_les_gears_d_extension_appeles_sont_emis()
    {
        var (program, _) = Bind("""
            gear f([int] xs): int => xs.second();
            gear g(): void { f([1, 2, 3]); }
            !train |> done;
            """);

        var cpp = CppEmitter.Emit(program!, "main.rgl").Code;
        Assert.Contains("second", cpp);
        Assert.DoesNotContain("third", cpp);
        Assert.DoesNotContain("shout", cpp);
    }
}
