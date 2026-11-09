using Rgl.Diagnostics;
using Rgl.Lsp;
using Rgl.Tests.Runtime;

namespace Rgl.Tests.Lsp;

public class NativeEditorTests
{
    [ZigFact]
    public void Les_liaisons_C_offrent_diagnostics_signatures_et_actualisation_des_headers()
    {
        using var directory = new TempDirectory();
        directory.File("rgl.json", "{\"name\":\"p\",\"version\":\"1.0.0\",\"rgl\":\"0\"}");
        var header = directory.File("api.h", "int twice(int n);\n");
        var forge = directory.File("Api.forge", "forge: native\ndescription: \"API\"\nheader: \"api.h\"\nlink: static\n");
        const string text = "import Api;\nguard f(): int => Api.twice(3);\n!train |> done;";
        var path = directory.File("main.rgl", text);
        var report = DocumentAnalysis.Analyze(path, text);
        Assert.DoesNotContain(report.Diagnostics, d => d.Level == DiagnosticLevel.Error);
        var index = new ProjectIndex();
        var project = index.Open(path, _ => null);
        var module = project.Find("Api")!;
        var symbol = module.Exported["twice"];
        Assert.Equal("gear twice(int n): int", Signatures.Of(symbol));
        Assert.Equal(File.ReadAllText(forge), symbol.NameSpan.Source.Content);
        var document = SemanticDocument.Create(path, text, project);
        Assert.Equal("twice(int n): int", SignatureHelpProvider.At(document, text.IndexOf("twice(3)", StringComparison.Ordinal) + 7)!.Label);
        Assert.Contains(CompletionProvider.Complete(path, text, 1, (uint)(text.Split('\n')[1].IndexOf("twice", StringComparison.Ordinal) + 3), project), c => c.Label == "twice");
        File.WriteAllText(header, "int twice(int n);\nint triple(int n);\n");
        Assert.Contains("triple", index.Open(path, _ => null).Find("Api")!.Exported.Keys);
        File.WriteAllText(header, "int twice(!!!);\n");
        Assert.Contains(DocumentAnalysis.Analyze(forge, File.ReadAllText(forge)).Diagnostics, d => d.Code == DiagnosticCodes.UnreadableHeader);
    }

    [Theory]
    [InlineData("unsafe")]
    [InlineData("ptr")]
    [InlineData("uptr")]
    [InlineData("char")]
    [InlineData("delete")]
    [InlineData("pack")]
    [InlineData("descriptor")]
    public void Les_nouveaux_mots_ont_une_aide(string word) => Assert.NotNull(KeywordDocumentation.For(word));
}
