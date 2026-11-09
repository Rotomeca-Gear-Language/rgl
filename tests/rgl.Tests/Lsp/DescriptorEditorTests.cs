using Rgl.Lsp;

namespace Rgl.Tests.Lsp;

public class DescriptorEditorTests
{
    [Fact]
    public void Les_descriptors_exposent_types_signatures_et_positions_originales()
    {
        const string text = """
            forge: descriptor
            description: "Test"
            header: "counter.hpp"
            link: static
            descriptor Counter -> Compteur {
                constructor(int n) => Counter(n);
                field total: int => total;
                const field capacity: int => capacity;
                add(int n): int => add;
                add(int n, int m): int => add;
            }
            """;
        var module = ModuleInfo.Build("Cpp.forge", text);
        var type = module.Symbols["Compteur"];
        Assert.Equal("descriptor Counter -> Compteur", Signatures.Of(type));
        var constructors = DescriptorEditorModel.Members(type, true).ToArray();
        Assert.Equal("guard create(int n): Compteur", Signatures.Of(Assert.Single(constructors)));
        var methods = DescriptorEditorModel.Members(type, false).ToArray();
        Assert.Equal(2, methods.Count(m => m.Name == "add"));
        Assert.Contains(methods, m => m.Name == "set_total" && Signatures.Of(m) == "guard set_total(int value): void");
        Assert.DoesNotContain(methods, m => m.Name == "set_capacity");
        Assert.All(methods, m => Assert.Equal(text, m.NameSpan.Source.Content));
    }
    [Fact]
    public void Une_methode_importee_offre_survol_signature_et_navigation()
    {
        using var directory = new TempDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "Cpp.forge"), """
            forge: descriptor
            description: "Counter"
            header: "counter.hpp"
            link: static
            descriptor Counter -> Compteur { add(int n): int => add; }
            """);
        const string text = "import Cpp;\ngear g(Cpp.Compteur c): void { int n: c.add(3); }\n!train |> done;";
        var path = Path.Combine(directory.Path, "main.rgl");
        File.WriteAllText(path, text);
        var project = new ProjectIndex().Open(path, _ => null);
        var document = SemanticDocument.Create(path, text, project);
        var offset = text.IndexOf("add(3)", StringComparison.Ordinal);
        var symbol = document.TargetAt(offset)!.Symbol;
        Assert.NotNull(symbol);
        Assert.Equal("guard add(int n): int", Signatures.Of(symbol));
        Assert.Equal("Cpp.forge", Path.GetFileName(symbol.Module!.Path));
        Assert.Equal("add", symbol.NameSpan.Text);
        Assert.Equal("add(int n): int", SignatureHelpProvider.At(document, offset + 5)!.Label);
    }
    [Fact]
    public void Les_membres_generiques_substituent_parametres_retours_et_champs()
    {
        const string descriptor = "forge: descriptor\ndescription: \"Box\"\nheader: \"box.hpp\"\nlink: static\ndescriptor Box<T> -> Boite<T> { constructor(T n) => Box<T>(n); field value: T => value; add(T n): T => add; }";
        var type = ModuleInfo.Build("Cpp.forge", descriptor).Symbols["Boite"];
        var members = DescriptorEditorModel.Members(type, false, ["[int]"]).ToArray();
        Assert.Equal("guard add([int] n): [int]", Signatures.Of(members.Single(m => m.Name == "add")));
        Assert.Equal("guard set_value([int] value): void", Signatures.Of(members.Single(m => m.Name == "set_value")));
        Assert.Equal("guard create(int n): Boite<int>", Signatures.Of(DescriptorEditorModel.Members(type, true, ["int"]).Single()));
        using var directory = new TempDirectory();
        directory.File("Cpp.forge", descriptor);
        const string text = "import Cpp;\ngear g(Cpp.Boite<int> b): void { var n: b.add(3); n -> console; }\n!train |> done;";
        var path = directory.File("main.rgl", text);
        var document = SemanticDocument.Create(path, text, new ProjectIndex().Open(path, _ => null));
        var offset = text.IndexOf("add(3)", StringComparison.Ordinal);
        Assert.Equal("add(int n): int", SignatureHelpProvider.At(document, offset + 5)!.Label);
        var local = document.LocalsAt(text.IndexOf("n ->", StringComparison.Ordinal)).Single(l => l.Name == "n");
        Assert.Equal("int", document.TypeOfSymbol(local));
    }
}
