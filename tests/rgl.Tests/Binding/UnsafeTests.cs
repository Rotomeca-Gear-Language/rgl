using Rgl.Binding;
using Rgl.Diagnostics;
using Rgl.Emit;
using Rgl.IR;
using Rgl.Parsing;
using Rgl.Syntax;
using Rgl.Text;
using Rgl.Tests.Runtime;

namespace Rgl.Tests.Binding;

public class UnsafeTests
{
    private static (IrProgram? Program, DiagnosticBag Diagnostics) Bind(string text, bool safe = false)
    {
        var bag = new DiagnosticBag();
        var unit = new Parser(new Lexer(new SourceText("main.rgl", text), bag).Tokenize(), bag).Parse();
        return (bag.HasErrors ? null : new Binder(bag, safe: safe).Bind(unit), bag);
    }

    [Theory]
    [InlineData("E147", "gear g(): int { int x: 1; return *(&x); } !train |> g |> $ 0;")]
    [InlineData("E147", "gear g(ptr<int> p): int => 0; !train |> $ 0;")]
    [InlineData("E147", "gear g(): int { char c: 65 as char; return 0; } !train |> $ 0;")]
    [InlineData("E147", "unsafe g(): int { const int x: 1; ptr<int> p: &x; return 0; } !train |> $ 0;")]
    public void La_memoire_brute_est_limitee_a_unsafe(string expectedCode, string source)
    {
        var result = Bind(source);
        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics.Items, d => d.Code == expectedCode);
    }

    [Fact]
    public void Le_mode_safe_refuse_un_appel_indirect_a_unsafe()
    {
        var result = Bind("unsafe brut(): int => 3; gear appel(): int => brut(); !train |> appel |> $ 0;", safe: true);
        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics.Items, d => d.Code == "E147");
    }

    [Theory]
    [InlineData("E147", "unsafe brut(): ptr<int> => 3; gear g(): int { var p: brut(); return 0; } !train |> g |> $ 0;")]
    [InlineData("E147", "unsafe brut(): int { int n: 3; delete &n; return n; } !train |> $ 0;")]
    [InlineData("E147", "unsafe g(): void { int x: 5; uptr<int> p: &x; p.free; } !train |> g |> $ 0;")]
    [InlineData("E147", "unsafe g(): void { int x: 5; uptr<int> p: &x; undefine p; } !train |> g |> $ 0;")]
    [InlineData("E003", "unsafe g(): void { int x: 5; uptr<int> p: &x; ptr<int> q:: p; } !train |> g |> $ 0;")]
    public void Les_adresses_non_gerables_sont_refusees(string expectedCode, string source)
    {
        var result = Bind(source);
        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics.Items, d => d.Code == expectedCode);
    }

    [Fact]
    public void Unsafe_se_propage_au_gear_et_au_train_appelants()
    {
        var result = Bind("unsafe brut(): int => 3; gear appel(): int => brut(); !train |> appel |> $ 0;");
        Assert.NotNull(result.Program);
        Assert.True(result.Program.Entry.IsUnsafe);
        Assert.True(result.Program.Gears.Single(g => g.Symbol.Name == "appel").Symbol.IsUnsafe);
    }

    [Theory]
    [InlineData("W068", "unsafe g(): void { uptr<int> p: 3; } !train |> g |> $ 0;")]
    [InlineData("W068", "unsafe g(): void { [uptr<int>] p: [3, 4]; } !train |> g |> $ 0;")]
    [InlineData("W068", "unsafe g(): void { int x: 4; uptr<int> p: 3; p <- &x; } !train |> g |> $ 0;")]
    [InlineData("W068", "unsafe g(): void { uptr<int> p: 3; p <- null; } !train |> g |> $ 0;")]
    [InlineData("W068", "unsafe g(bool b): void { uptr<int> p: 3; if (b) { p.free; } } !train true |> g |> $ 0;")]
    public void Un_uptr_possiblement_non_libere_avertit(string expectedCode, string source)
    {
        var result = Bind(source);
        Assert.NotNull(result.Program);
        Assert.Contains(result.Diagnostics.Items, d => d.Code == expectedCode);
    }

    [Theory]
    [InlineData("unsafe g(): void { uptr<int> p: 3; p.free; } !train |> g |> $ 0;")]
    [InlineData("unsafe g(): void { uptr<int> p: null; p <- null; } !train |> g |> $ 0;")]
    [InlineData("unsafe g(): void { uptr<int> p: 3; p <- p; p.free; } !train |> g |> $ 0;")]
    [InlineData("unsafe g(): void { uptr<int> p: 3; undefine p; } !train |> g |> $ 0;")]
    [InlineData("unsafe g(): void { ptr<int> p: 3; } !train |> g |> $ 0;")]
    [InlineData("unsafe g(): void { ptr<int> p: 3; uptr<int> q:: p; ptr<int> r:: q; } !train |> g |> $ 0;")]
    [InlineData("unsafe g(): void { ptr<int> p: 3; uptr<int> q: null; q <-| p; ptr<int> r: null; r <-| q; } !train |> g |> $ 0;")]
    public void Les_liberations_et_transferts_sont_reconnus(string source)
    {
        var result = Bind(source);
        Assert.True(result.Program is not null, string.Join("\n", result.Diagnostics.Items.Select(d => d.Format("."))));
        Assert.DoesNotContain(result.Diagnostics.Items, d => d.Code == "W068");
    }

    [Theory]
    [InlineData("E136", "unsafe g(): void { ptr<int> p: 3; ptr<int> q: p; } !train |> g |> $ 0;")]
    [InlineData("E136", "@async unsafe lire(ptr<int> p): int => *p; unsafe g(): void { ptr<int> p: 5; var attente: lire(p); } !train |> g |> $ 0;")]
    [InlineData("E006", "unsafe g(): int { ptr<int> p: 3; uptr<int> q:: p; return *p; } !train |> g |> $ 0;")]
    [InlineData("E006", "unsafe g(): int { uptr<int> p: 3; p.free; return *p; } !train |> g |> $ 0;")]
    public void Un_ptr_ne_se_copie_pas_et_une_source_liberee_ne_se_lit_plus(string expectedCode, string source)
    {
        var result = Bind(source);
        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics.Items, d => d.Code == expectedCode);
    }

    [ZigFact]
    public void Le_runtime_detruit_les_ptr_et_garde_les_uptr_jusqua_free()
    {
        using var directory = new TempDirectory();
        var source = Path.Combine(directory.Path, "pointer.cpp");
        File.WriteAllText(source, """
            #include "rgl/standard/pointer.hpp"
            #include <type_traits>
            struct Counted {
                static inline int destroyed = 0;
                ~Counted() { ++destroyed; }
            };
            using Owned = rgl::standard::pointer<Counted, true>;
            using Raw = rgl::standard::pointer<Counted, false>;
            static_assert(!std::is_copy_constructible_v<Owned>);
            static_assert(std::is_move_constructible_v<Owned>);
            int main() {
                {
                    Owned a(new Counted);
                    Owned b(std::move(a));
                    if (a.get() != nullptr) return 1;
                }
                if (Counted::destroyed != 1) return 2;
                Counted* saved;
                { Raw a(new Counted); saved = a.get(); }
                if (Counted::destroyed != 1) return 3;
                { Raw a(saved); a.free(); }
                if (Counted::destroyed != 2) return 4;
                {
                    Owned a(new Counted);
                    Raw b(a.release());
                    Owned c(b.release());
                }
                if (Counted::destroyed != 3) return 5;
                {
                    const Owned a(new Counted);
                    a.free();
                }
                return Counted::destroyed == 4 ? 0 : 6;
            }
            """);
        var executable = ZigBuild.Compile(directory.Path, "pointer", true, source);
        Assert.Equal(0, ZigBuild.Run(executable).Code);
    }

    [ZigFact]
    public void Allocation_lecture_ecriture_adresse_char_et_liberation()
    {
        var result = Bind("""
            unsafe calcul(): int {
                ptr<int> initial: 4;
                uptr<int> manuel:: initial;
                ptr<int> p:: manuel;
                *p <- *p + 3;
                int n: *(p + 0);
                delete p;
                uptr<int> local: &n;
                *local <- *local + 2;
                char c: 65 as char;
                int code: c as int;
                return n + code;
            }
            emit sortie(int n): uint { if (n eq 74) { return 0; } return 1; }
            !train |> calcul |> sortie;
            """);
        Assert.True(result.Program is not null, string.Join("\n", result.Diagnostics.Items.Select(d => d.Format("."))));
        using var directory = new TempDirectory();
        var source = Path.Combine(directory.Path, "main.cpp");
        File.WriteAllText(source, CppEmitter.Emit(result.Program!, "main.rgl").Code);
        var executable = ZigBuild.Compile(directory.Path, "unsafe", true, source);
        Assert.Equal(0, ZigBuild.Run(executable).Code);
    }
}
