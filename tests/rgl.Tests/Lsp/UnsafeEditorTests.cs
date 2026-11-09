using Rgl.Lsp;

namespace Rgl.Tests.Lsp;

public class UnsafeEditorTests
{
    [Fact]
    public void Le_survol_et_la_signature_de_free_suivent_les_pointeurs()
    {
        using var directory = new TempDirectory();
        const string text = "unsafe f(): int { uptr<int> p: 5; p.free(); ptr<int> q: 6; var n: *q; return n; }\n!train |> $ 0;";
        var path = Path.Combine(directory.Path, "main.rgl");
        File.WriteAllText(path, text);
        var project = new ProjectIndex().Open(path, _ => null);
        var document = SemanticDocument.Create(path, text, project);
        Assert.Equal("unsafe f(): int", Signatures.Of(document.Self.Symbols["f"]));
        var free = text.IndexOf("free", StringComparison.Ordinal);
        Assert.Equal("free", document.TargetAt(free)!.Symbol!.Name);
        Assert.Equal("free(): void", SignatureHelpProvider.At(document, free + 5)!.Label);
        var variable = document.LocalsAt(text.IndexOf("return n", StringComparison.Ordinal)).Single(s => s.Name == "n");
        Assert.Equal("int", document.TypeOfSymbol(variable));
        Assert.Contains(CompletionProvider.Complete(path, text, 0, (uint)free, project), item => item.Label == "free");
    }

    [Fact]
    public void Pack_est_propose_et_son_type_de_retour_est_connu()
    {
        using var directory = new TempDirectory();
        const string text = "gear f(): int { var valeurs: pack([1, 2]); return 0; }\n!train |> $ 0;";
        var path = Path.Combine(directory.Path, "main.rgl");
        File.WriteAllText(path, text);
        var project = new ProjectIndex().Open(path, _ => null);
        var document = SemanticDocument.Create(path, text, project);
        var variable = document.LocalsAt(text.IndexOf("return 0", StringComparison.Ordinal)).Single(s => s.Name == "valeurs");
        Assert.Equal("[int]", document.TypeOfSymbol(variable));
        Assert.Contains(CompletionProvider.Complete(path, text, 0, 28, project), item => item.Label == "pack");
    }
}
