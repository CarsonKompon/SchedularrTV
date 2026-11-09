using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.EventEmitters;

namespace SchedularrTV.Core.Configuration;

/// <summary>
/// Writes text with line breaks in it (an overlay's or promo's multi-line text) as a "|" block, one line of text per
/// line of the file, rather than YAMLDotNet's quoted form, where each line break becomes a blank line that hand edits
/// (and comment-keeping tools) easily lose. Text a block can't hold exactly is left to the usual quoting.
/// </summary>
internal sealed class MultilineLiteralEmitter(IEventEmitter next) : ChainedEventEmitter(next)
{
    public override void Emit(ScalarEventInfo eventInfo, IEmitter emitter)
    {
        if (eventInfo.Source.Value is string text && text.Contains('\n') && FitsBlock(text))
        {
            eventInfo.Style = ScalarStyle.Literal;
        }

        base.Emit(eventInfo, emitter);
    }

    // A block keeps every character except trailing spaces at line ends and a first line starting with a space, which
    // it can't tell from indentation; those stay quoted.
    private static bool FitsBlock(string text) =>
        !text.StartsWith(' ') && !text.Contains('\r') && !text.Contains('\t')
        && text.Split('\n').All(line => line.Length == 0 || !char.IsWhiteSpace(line[^1]));
}
