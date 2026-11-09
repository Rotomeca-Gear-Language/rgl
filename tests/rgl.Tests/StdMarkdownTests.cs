using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>Markdown vers HTML.</summary>
public class StdMarkdownTests
{
    [ZigFact]
    public void Le_markdown_devient_du_html_echappe()
    {
        var output = StdHarness.Main("""
            use <Markdown> only {markdownToHtml};

            gear show(string markdown): void { std.writeLine(markdownToHtml(markdown).replaceAll("\n", '|')); }

            gear Test(): void {
                show('# Titre');
                show("###### Petit titre  \n\nUn **gras**, un *italique* et du `code`.\nSuite de la ligne.");
                show("- un\n- deux\n\n1. premier\n2) second");
                show("> citation\n> suite\n\n---");
                show("```\nif (a < b) { x & y }\n```\napres");
                show('[RGL](https://rgl.dev) et ![logo](img/logo.png) et [autre](docs/page)');
                show('[piege](javascript:alert(1)) <script>alert(1)</script>');
                show('');
                show("a\r\nb");
            }
            emit Fin(): uint => 0;
            !train |> Test |> Fin;
            """);

        Assert.Equal(
            "<h1>Titre</h1>|\n"
            + "<h6>Petit titre</h6>|<p>Un <strong>gras</strong>, un <em>italique</em> et du <code>code</code>.|Suite de la ligne.</p>|\n"
            + "<ul>|<li>un</li>|<li>deux</li>|</ul>|<ol>|<li>premier</li>|<li>second</li>|</ol>|\n"
            + "<blockquote>|citation|suite|</blockquote>|<hr>|\n"
            + "<pre><code>if (a &lt; b) { x &amp; y }|</code></pre>|<p>apres</p>|\n"
            + "<p><a href=\"https://rgl.dev\">RGL</a> et <img src=\"img/logo.png\" alt=\"logo\"> et <a href=\"docs/page\">autre</a></p>|\n"
            + "<p>[piege](javascript:alert(1)) &lt;script&gt;alert(1)&lt;/script&gt;</p>|\n"
            + "\n"
            + "<p>a|b</p>|\n", output);
    }
}
