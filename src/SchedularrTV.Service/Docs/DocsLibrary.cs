using System.Reflection;
using System.Text;
using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.Extensions.Options;
using SchedularrTV.Service.Hosting;

namespace SchedularrTV.Service.Docs;

/// <summary>A heading inside a guide, for its "On this page" list.</summary>
public sealed record DocHeading(string Id, string Text, int Level);

/// <summary>One guide from docs/, rendered to HTML.</summary>
/// <param name="Slug">The file name without <c>.md</c>; the page lives at <c>docs/{Slug}</c>.</param>
/// <param name="Summary">The paragraph under the title, shown under the page header and on the index.</param>
/// <param name="Html">The body below the title and summary, with links rewritten to work inside the app.</param>
/// <param name="Listed">False for pages kept only so old links still work; they're left out of the index.</param>
public sealed record DocPage(
    string Slug,
    string Title,
    string Summary,
    string Section,
    string Html,
    string PlainText,
    IReadOnlyList<DocHeading> Headings,
    bool Listed);

/// <summary>A group of guides in the docs sidebar and index.</summary>
public sealed record DocSection(string Name, string Icon, IReadOnlyList<DocPage> Pages);

/// <summary>
/// The guides in the repository's docs/ folder, built into the app as resources and rendered once with Markdig.
/// Links between guides become links to the in-app pages, so the same Markdown reads well here and on GitHub.
/// </summary>
public sealed class DocsLibrary
{
    private const string ResourcePrefix = "docs/";

    /// <summary>The order guides are shown in. A guide not named here lands in "More" at the end.</summary>
    private static readonly (string Name, string Icon, string[] Slugs)[] Layout =
    [
        ("Web UI", "play", ["installation", "web-ui", "local-library", "import", "stats", "troubleshooting"]),
        ("Plugins", "puzzle", [
            "plugins", "generated-promos", "overlays", "local-ai", "radio", "background-music", "news", "iptv",
            "streamers", "web-channels", "tv-guide", "credit-squeeze", "auto-chapters", "archive-follow"
        ]),
        ("Advanced", "code", ["yaml", "configuration", "scheduling", "ersatztv", "rest-api", "schedule-performance", "plugin-development", "development"])
    ];

    /// <summary>Guides that only point somewhere else now. Still reachable, never listed.</summary>
    private static readonly HashSet<string> Unlisted = new(StringComparer.OrdinalIgnoreCase) { "weatherstar" };

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAutoIdentifiers(AutoIdentifierOptions.GitHub) // Same anchors as GitHub, so docs/x.md#y links agree.
        .UsePipeTables()
        .UseAlertBlocks()
        .UseTaskLists()
        .UseAutoLinks()
        .UseFootnotes()
        .UseEmphasisExtras(Markdig.Extensions.EmphasisExtras.EmphasisExtraOptions.Strikethrough)
        .Build();

    private readonly string _repository;
    private readonly Lazy<Catalog> _catalog;

    public DocsLibrary(IOptions<UpdateOptions> updates)
    {
        _repository = string.IsNullOrWhiteSpace(updates.Value.Repository) ? UpdateOptions.DefaultRepository : updates.Value.Repository;
        _catalog = new Lazy<Catalog>(Load, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The listed guides, grouped and in reading order.</summary>
    public IReadOnlyList<DocSection> Sections => _catalog.Value.Sections;

    /// <summary>The guide called <paramref name="slug"/> (case doesn't matter, a trailing <c>.md</c> is fine), or null.</summary>
    public DocPage? Find(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return null;
        }

        string key = slug.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? slug[..^3] : slug;
        return _catalog.Value.Pages.GetValueOrDefault(key);
    }

    /// <summary>The listed guides before and after <paramref name="page"/>, for the links at the bottom of a guide.</summary>
    public (DocPage? Previous, DocPage? Next) Neighbours(DocPage page)
    {
        List<DocPage> order = _catalog.Value.Sections.SelectMany(section => section.Pages).ToList();
        int index = order.FindIndex(candidate => candidate.Slug == page.Slug);
        return index < 0 ? (null, null) : (index > 0 ? order[index - 1] : null, index < order.Count - 1 ? order[index + 1] : null);
    }

    /// <summary>Listed guides matching every word of <paramref name="query"/>, best (title) matches first.</summary>
    public IReadOnlyList<DocPage> Search(string? query)
    {
        string[] words = (query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return [];
        }

        return _catalog.Value.Sections
            .SelectMany(section => section.Pages)
            .Select(page => (Page: page, Score: Score(page, words)))
            .Where(match => match.Score > 0)
            .OrderByDescending(match => match.Score)
            .Select(match => match.Page)
            .ToList();
    }

    /// <summary>The guide's source on GitHub.</summary>
    public string SourceUrl(DocPage page) => $"https://github.com/{_repository}/blob/main/docs/{page.Slug}.md";

    private static int Score(DocPage page, string[] words)
    {
        int score = 0;
        foreach (string word in words)
        {
            int best = page.Title.Contains(word, StringComparison.OrdinalIgnoreCase) ? 8
                : page.Headings.Any(heading => heading.Text.Contains(word, StringComparison.OrdinalIgnoreCase)) ? 4
                : page.Summary.Contains(word, StringComparison.OrdinalIgnoreCase) ? 3
                : page.PlainText.Contains(word, StringComparison.OrdinalIgnoreCase) ? 1
                : 0;
            if (best == 0)
            {
                return 0; // Every word has to be somewhere in the guide.
            }

            score += best;
        }

        return score;
    }

    private sealed record Catalog(IReadOnlyDictionary<string, DocPage> Pages, IReadOnlyList<DocSection> Sections);

    private Catalog Load()
    {
        Assembly assembly = typeof(DocsLibrary).Assembly;
        Dictionary<string, string> sources = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal) || !name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using Stream? stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                continue;
            }

            using StreamReader reader = new(stream, Encoding.UTF8);
            sources[name[ResourcePrefix.Length..^3].ToLowerInvariant()] = reader.ReadToEnd();
        }

        Dictionary<string, DocPage> pages = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string slug, string markdown) in sources)
        {
            string section = Layout.Where(group => group.Slugs.Contains(slug)).Select(group => group.Name).FirstOrDefault() ?? "More";
            pages[slug] = Render(slug, markdown, section, sources.Keys);
        }

        List<DocSection> sections = [];
        foreach ((string name, string icon, string[] slugs) in Layout)
        {
            List<DocPage> listed = slugs.Select(slug => pages.GetValueOrDefault(slug)).OfType<DocPage>().Where(page => page.Listed).ToList();
            if (listed.Count > 0)
            {
                sections.Add(new DocSection(name, icon, listed));
            }
        }

        List<DocPage> more = pages.Values.Where(page => page.Listed && page.Section == "More").OrderBy(page => page.Title, StringComparer.OrdinalIgnoreCase).ToList();
        if (more.Count > 0)
        {
            sections.Add(new DocSection("More", "list", more));
        }

        return new Catalog(pages, sections);
    }

    private DocPage Render(string slug, string markdown, string section, ICollection<string> known)
    {
        MarkdownDocument document = Markdown.Parse(markdown, Pipeline);

        // The title and the paragraph under it go in the page header, so they're taken out of the body.
        string title = slug;
        string summary = string.Empty;
        if (document.FirstOrDefault() is HeadingBlock { Level: 1 } h1)
        {
            title = InlineText(h1.Inline);
            document.Remove(h1);
            if (document.FirstOrDefault() is ParagraphBlock intro)
            {
                summary = InlineText(intro.Inline);
                document.Remove(intro);
            }
        }

        foreach (LinkInline link in document.Descendants<LinkInline>())
        {
            Rewrite(link, slug, known);
        }

        foreach (AutolinkInline link in document.Descendants<AutolinkInline>())
        {
            if (!link.IsEmail)
            {
                OpenOutside(link);
            }
        }

        List<DocHeading> headings = document.Descendants<HeadingBlock>()
            .Where(heading => heading.Level is 2 or 3 && heading.GetAttributes().Id is { Length: > 0 })
            .Select(heading => new DocHeading(heading.GetAttributes().Id!, InlineText(heading.Inline), heading.Level))
            .ToList();

        using StringWriter writer = new();
        HtmlRenderer renderer = new(writer);
        Pipeline.Setup(renderer);
        renderer.Render(document);
        writer.Flush();

        string plain = Markdown.ToPlainText(markdown, Pipeline);
        return new DocPage(slug, title, summary, section, writer.ToString(), plain, headings, !Unlisted.Contains(slug));
    }

    /// <summary>
    /// Points a link written for GitHub (<c>other.md#part</c>, <c>#part</c>) at the in-app page, sends anything else
    /// in the repository to GitHub, and opens other sites in a new tab.
    /// </summary>
    private void Rewrite(LinkInline link, string slug, ICollection<string> known)
    {
        if (link.IsImage || string.IsNullOrWhiteSpace(link.Url))
        {
            return;
        }

        string url = link.Url.Trim();
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? absolute))
        {
            if (absolute.Scheme is "http" or "https")
            {
                OpenOutside(link);
            }

            return;
        }

        if (url.StartsWith('#'))
        {
            link.Url = $"docs/{slug}{url}";
            return;
        }

        int hash = url.IndexOf('#');
        string path = hash < 0 ? url : url[..hash];
        string fragment = hash < 0 ? string.Empty : url[hash..];
        while (path.StartsWith("./", StringComparison.Ordinal))
        {
            path = path[2..];
        }

        if (path.StartsWith("docs/", StringComparison.OrdinalIgnoreCase))
        {
            path = path[5..];
        }

        if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && !path.Contains('/'))
        {
            string target = path[..^3].ToLowerInvariant();
            if (known.Contains(target))
            {
                link.Url = $"docs/{target}{fragment}";
                return;
            }

            if (target == "readme")
            {
                link.Url = "docs";
                return;
            }
        }

        // Something else in the repository (a source file, a sample): show it on GitHub.
        string repoPath = path.StartsWith("../", StringComparison.Ordinal) ? path[3..] : $"docs/{path}";
        link.Url = $"https://github.com/{_repository}/blob/main/{repoPath}{fragment}";
        OpenOutside(link);
    }

    private static void OpenOutside(MarkdownObject link)
    {
        HtmlAttributes attributes = link.GetAttributes();
        attributes.AddPropertyIfNotExist("target", "_blank");
        attributes.AddPropertyIfNotExist("rel", "noopener noreferrer");
        attributes.AddClass("external");
    }

    /// <summary>The text of some inlines with the Markdown taken out (code keeps its text, links their label).</summary>
    private static string InlineText(ContainerInline? container)
    {
        if (container is null)
        {
            return string.Empty;
        }

        StringBuilder text = new();
        Append(container, text);
        return string.Join(' ', text.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        static void Append(Inline inline, StringBuilder text)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    text.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    text.Append(code.Content);
                    break;
                case HtmlEntityInline entity:
                    text.Append(entity.Transcoded.ToString());
                    break;
                case LineBreakInline:
                    text.Append(' ');
                    break;
                case AutolinkInline autolink:
                    text.Append(autolink.Url);
                    break;
                case ContainerInline children:
                    foreach (Inline child in children)
                    {
                        Append(child, text);
                    }

                    break;
            }
        }
    }
}
