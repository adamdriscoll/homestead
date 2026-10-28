using Homestead.Features.Articles;
using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace Homestead.Features.Publishing;

public static class MermaidDiagrams
{
    private static readonly MarkdownPipeline pipeline = new MarkdownPipelineBuilder().DisableHtml().Build();

    public static bool Contains(string text) => Markdown.Parse(text, pipeline).Descendants<FencedCodeBlock>()
        .Any(block => string.Equals(block.Info, "mermaid", StringComparison.OrdinalIgnoreCase));

    public static bool Contains(ArticleSnapshot post) => Contains(post.BodyMarkdown) || post.Steps.Any(step => Contains(step.BodyMarkdown));

    public static IEnumerable<(string RelativePath, string SourcePath)> Files(string contentRoot)
    {
        var root = Path.Combine(contentRoot, "Diagrams");
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(file => Path.GetExtension(file) is ".mjs" or ".txt")
            .Select(file => ("assets/mermaid/" + Path.GetRelativePath(root, file).Replace('\\', '/'), file));
    }
}

internal sealed class MermaidCodeRenderer : HtmlObjectRenderer<CodeBlock>
{
    protected override void Write(HtmlRenderer renderer, CodeBlock block)
    {
        if (block is FencedCodeBlock fence && string.Equals(fence.Info, "mermaid", StringComparison.OrdinalIgnoreCase))
        {
            renderer.Write("<figure data-mermaid><details open><summary>Diagram source</summary><pre><code class=\"language-mermaid\">");
            renderer.WriteEscape(block.Lines.ToString());
            renderer.WriteLine("</code></pre></details></figure>");
        }
        else new CodeBlockRenderer().Write(renderer, block);
    }
}
