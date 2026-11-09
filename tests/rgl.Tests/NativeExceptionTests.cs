using Rgl.Build.Toolchain;
using Rgl.Emit;
using Rgl.Native;
using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>
/// Le composant <c>Native</c> du runtime : l'appel protégé du C++ qui lance des exceptions. Le programme est compilé sans exceptions ni RTTI ; le
/// composant, lui, avec les exceptions, et le C++ « tiers » vient d'une archive compilée avec les exceptions.
/// </summary>
public class NativeExceptionTests
{
    private static readonly string SharedCache = Path.Combine(Path.GetTempPath(), "rgl-tests-cache");

    private const string Thrower = """
        #include <exception>
        #include <new>
        #include <stdexcept>
        extern "C" {
        void ok(void* context) { *static_cast<int*>(context) = 7; }
        void lance_std(void*) { throw std::runtime_error("boum std"); }
        void lance_entier(void*) { throw 42; }
        void lance_alloc(void*) { throw std::bad_alloc(); }
        void lance_imbrique(void*) {
            try { throw std::logic_error("interne"); }
            catch (...) { std::throw_with_nested(std::runtime_error("externe")); }
        }
        void lance_long(void*) { throw std::runtime_error(std::string(1000, 'x')); }
        }
        """;

    private const string Main = """
        #include <cstdio>
        #include <cstring>
        #include "rgl/standard/native.hpp"
        extern "C" {
        void ok(void*); void lance_std(void*); void lance_entier(void*); void lance_alloc(void*); void lance_imbrique(void*); void lance_long(void*);
        }
        static void essaie(const char* nom, void (*body)(void*)) {
            int resultat = 0;
            rgl_native_status status;
            if (rgl_native_guard(body, &resultat, &status) != 0) std::printf("%s : echec %d « %s » (%zu)\n", nom, status.failed, status.message, std::strlen(status.message));
            else std::printf("%s : ok %d\n", nom, resultat);
        }
        int main() {
            essaie("ok", ok); essaie("std", lance_std); essaie("entier", lance_entier); essaie("alloc", lance_alloc);
            essaie("imbrique", lance_imbrique); essaie("long", lance_long);
            std::puts("fin");
            return 0;
        }
        """;

    [ZigFact]
    public void NativeError_de_la_std_est_une_erreur_comme_les_autres()
    {
        using var project = new TempDirectory();
        File.WriteAllText(Path.Combine(project.Path, "main.rgl"), "guard g(): void {\n    fail new std.NativeError {callee: \"Lib::f\", message: \"boum\"};\n}\ntrain |> g !> [$ 3] |> done;\n");
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = Rgl.Cli.CliRunner.Run(["exec", "main.rgl"], new Rgl.Cli.CliContext(output, error, project.Path, SharedCache, Toolchain: ZigToolchain.Locate(SharedCache)));

        Assert.True(exit == 3, error.ToString());
    }

    [ZigTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Les_exceptions_du_C_plus_plus_d_une_bibliotheque_sont_attrapees_sans_arreter_le_programme(bool release)
    {
        var toolchain = ZigToolchain.Locate(SharedCache)!;
        using var directory = new TempDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "thrower.cpp"), "#include <string>\n" + Thrower);
        File.WriteAllText(Path.Combine(directory.Path, "main.cpp"), Main);

        // Le C++ tiers est compilé avec les exceptions, puis rangé dans une archive comme une bibliothèque de forge: native.
        var obj = Path.Combine(directory.Path, "thrower.o");
        var archive = Path.Combine(directory.Path, "libthrower.a");
        var compile = ZigBuild.Run(ZigBuild.Zig, "c++", "-std=c++23", "-fexceptions", "-O2", "-fno-sanitize=undefined", "-target", toolchain.Target, "-c", Path.Combine(directory.Path, "thrower.cpp"), "-o", obj);
        Assert.True(compile.Code == 0, compile.Output);
        var archived = ZigBuild.Run(ZigBuild.Zig, "ar", "rcs", archive, obj);
        Assert.True(archived.Code == 0, archived.Output);

        var executable = Path.Combine(directory.Path, OperatingSystem.IsWindows() ? "programme.exe" : "programme");
        var result = toolchain.BuildExecutable(Path.Combine(directory.Path, "main.cpp"), executable, release ? BuildProfile.Release : BuildProfile.Debug,
            new HashSet<RuntimeComponent> { RuntimeComponent.Native }, null, [new CLibrary([], [Path.Combine(ZigBuild.Runtime, "include")], archive)]);
        Assert.True(result.Success, result.Log);

        var run = ZigBuild.Run(executable);
        var lines = run.Output.Replace("\r\n", "\n").TrimEnd().Split('\n');

        Assert.Equal(0, run.Code);
        Assert.Equal("ok : ok 7", lines[0]);
        Assert.Equal("std : echec 1 « boum std » (8)", lines[1]);
        Assert.Equal("entier : echec 1 « exception C++ inconnue » (22)", lines[2]);
        Assert.StartsWith("alloc : echec 1 « ", lines[3]);
        Assert.Equal("imbrique : echec 1 « externe » (7)", lines[4]);
        Assert.Equal("long : echec 1 « " + new string('x', 255) + " » (255)", lines[5]);   // tronqué au tampon, toujours terminé par zéro
        Assert.Equal("fin", lines[6]);
    }
}
