using SchedularrTV.Core.Configuration;
using Xunit;

namespace SchedularrTV.Core.Tests;

/// <summary>
/// Text with line breaks in it (an overlay's credits, say) survives saving: written as a "|" block, and the comment-keeping
/// merge neither loses its line breaks nor copies its lines in again as if they were comments.
/// </summary>
public sealed class MultilineYamlTests
{
    private const string Credits = "{tag.artist}\n\"{title}\"\n{tag.album}\n{year}";

    private static Dictionary<string, object?> Section(string text) => new()
    {
        ["overlays"] = new List<object?>
        {
            new Dictionary<string, object?> { ["name"] = "Music Video Info", ["layers"] = new List<object?> { new Dictionary<string, object?> { ["kind"] = "text", ["text"] = text } } }
        },
        ["enabled"] = true
    };

    private static string TextOf(string yaml)
    {
        var document = new YamlDotNet.Serialization.DeserializerBuilder().Build().Deserialize<Dictionary<string, object>>(yaml);
        var overlay = (Dictionary<object, object>)((List<object>)document["overlays"])[0];
        var layer = (Dictionary<object, object>)((List<object>)overlay["layers"])[0];
        return (string)layer["text"];
    }

    [Fact]
    public void Text_with_line_breaks_is_written_as_a_block()
    {
        string yaml = SchedulerConfigLoader.SerializeSection(Section(Credits));

        Assert.Contains("text: |-", yaml, StringComparison.Ordinal);
        Assert.Equal(Credits, TextOf(yaml));
    }

    [Fact]
    public void Saving_again_and_again_keeps_the_text_exactly()
    {
        // # comments in the hand-kept file are what the merge is for.
        string file = "# My overlays\n" + SchedulerConfigLoader.SerializeSection(Section("{tag.artist} - {title}"));

        foreach (string text in new[] { Credits, Credits, Credits + "\n\n{channel}", "Just one line", Credits })
        {
            file = YamlCommentMerger.Merge(file, SchedulerConfigLoader.SerializeSection(Section(text)));
            Assert.Equal(text, TextOf(file));
            Assert.StartsWith("# My overlays", file, StringComparison.Ordinal);
        }
    }

    [Theory]
    // How the text was saved before this fix: folded with blank lines, and single-quoted with blank lines.
    [InlineData("enabled: true\noverlays:\n- name: Music Video Info\n  layers:\n  - kind: text\n    text: >-\n      {tag.artist}\n\n      {title}\n# kept\n")]
    [InlineData("enabled: true\noverlays:\n- name: Music Video Info\n  layers:\n  - kind: text\n    text: '{tag.artist}\n\n      {title}'\n# kept\n")]
    public void Earlier_multi_line_text_in_the_file_isnt_copied_in_again(string original)
    {
        string merged = YamlCommentMerger.Merge(original, SchedulerConfigLoader.SerializeSection(Section(Credits)));

        Assert.Equal(Credits, TextOf(merged));
        Assert.Equal(1, merged.Split('\n').Count(line => line.Trim() == "{tag.artist}"));
        Assert.Contains("# kept", merged, StringComparison.Ordinal);
    }
}
