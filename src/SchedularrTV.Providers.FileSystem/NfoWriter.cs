using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using SchedularrTV.Core.Media;

namespace SchedularrTV.Providers.FileSystem;

/// <summary>
/// Changes a Kodi-style .nfo file's metadata in place: only the edited fields' elements, leaving everything else in the
/// file (ids, artwork, stream details, other elements, a scraper link after the XML) as it was.
/// </summary>
public static partial class NfoWriter
{
    private static readonly XmlReaderSettings ReadSettings = new()
    {
        ConformanceLevel = ConformanceLevel.Fragment,
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
        IgnoreWhitespace = true,
        IgnoreProcessingInstructions = true,
        CheckCharacters = false
    };

    /// <summary>
    /// The file's new text: <paramref name="existing"/> (null or blank for a new file) with the
    /// <paramref name="root"/> element's (<c>tvshow</c>, <c>movie</c>) <paramref name="fields"/> set from
    /// <paramref name="metadata"/>. A file that can't be read as XML is replaced by a new one.
    /// </summary>
    public static string Update(string? existing, string root, EditableMetadata metadata, MetadataFields fields)
    {
        List<XNode> nodes = Parse(existing);
        XElement? element = nodes.OfType<XElement>().FirstOrDefault(node => string.Equals(node.Name.LocalName, root, StringComparison.OrdinalIgnoreCase));
        if (element is null)
        {
            element = new XElement(root);
            nodes.Insert(0, element);
        }

        EditableMetadata values = metadata.Normalized();
        if (fields.HasFlag(MetadataFields.Title) && values.Title is { } title) SetOne(element, "title", title);
        if (fields.HasFlag(MetadataFields.SortTitle)) SetOne(element, "sorttitle", values.SortTitle);
        if (fields.HasFlag(MetadataFields.Year)) SetOne(element, "year", values.Year?.ToString(CultureInfo.InvariantCulture));
        if (fields.HasFlag(MetadataFields.Overview)) SetOne(element, "plot", values.Overview);
        if (fields.HasFlag(MetadataFields.Rating)) SetOne(element, "mpaa", values.Rating);
        if (fields.HasFlag(MetadataFields.Genres)) SetMany(element, "genre", values.Genres);
        if (fields.HasFlag(MetadataFields.Studios)) SetMany(element, "studio", values.Studios);
        if (fields.HasFlag(MetadataFields.Tags)) SetMany(element, "tag", values.Tags);

        var text = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>");
        foreach (XNode node in nodes)
        {
            text.Append('\n').Append(node is XElement xml ? Indented(xml) : node.ToString().Trim());
        }

        return text.Append('\n').ToString();
    }

    /// <summary>The file's top-level nodes: its elements, and any loose text (a scraper's link after the XML).</summary>
    private static List<XNode> Parse(string? existing)
    {
        List<XNode> nodes = [];
        if (string.IsNullOrWhiteSpace(existing))
        {
            return nodes;
        }

        string text = Declaration().Replace(existing, string.Empty);
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), ReadSettings);
            reader.MoveToContent();
            while (!reader.EOF)
            {
                if (reader.NodeType is XmlNodeType.Element or XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Comment)
                {
                    nodes.Add(XNode.ReadFrom(reader));
                }
                else if (!reader.Read())
                {
                    break;
                }
            }
        }
        catch (XmlException)
        {
            // Unreadable from here on: what was read is kept, and the rest can't be.
        }

        return nodes;
    }

    /// <summary>One element with the value; none when the value is null.</summary>
    private static void SetOne(XElement parent, string name, string? value)
    {
        List<XElement> existing = [.. parent.Elements(name)];
        if (value is null)
        {
            existing.ForEach(element => element.Remove());
            return;
        }

        if (existing.Count == 0)
        {
            parent.Add(new XElement(name, value));
            return;
        }

        // Its attributes (a language, say) stay.
        existing[0].Value = value;
        existing.Skip(1).ToList().ForEach(element => element.Remove());
    }

    /// <summary>One element per value, where the first of the old ones was (so the file keeps its order).</summary>
    private static void SetMany(XElement parent, string name, IReadOnlyList<string> values)
    {
        List<XElement> existing = [.. parent.Elements(name)];
        XNode? before = existing.Count > 0 ? existing[0].PreviousNode : null;
        bool atStart = existing.Count > 0 && before is null;
        existing.ForEach(element => element.Remove());

        XElement[] added = [.. values.Select(value => new XElement(name, value))];
        if (added.Length == 0)
        {
            return;
        }

        if (before is not null)
        {
            before.AddAfterSelf(added);
        }
        else if (atStart)
        {
            parent.AddFirst(added);
        }
        else
        {
            parent.Add(added);
        }
    }

    private static string Indented(XElement element)
    {
        var settings = new XmlWriterSettings { Indent = true, IndentChars = "  ", OmitXmlDeclaration = true, NewLineChars = "\n" };
        var builder = new StringBuilder();
        using (XmlWriter writer = XmlWriter.Create(builder, settings))
        {
            element.WriteTo(writer);
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"^﻿?\s*<\?xml[^>]*\?>")]
    private static partial Regex Declaration();
}
