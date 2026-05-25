using System.Text;

namespace DropNSpawn;

internal static class CommentedYamlTemplateSupport
{
    internal static void AppendTemplateComment(StringBuilder builder, string text)
    {
        builder.Append("# ");
        builder.AppendLine(text);
    }

    internal static void AppendTemplateLine(StringBuilder builder, int indent, string text)
    {
        builder.Append("# ");
        builder.Append(' ', indent * 2);
        builder.AppendLine(text);
    }

    internal static void AppendTemplateNestedLine(StringBuilder builder, int indent, string text)
    {
        builder.Append("# ");
        builder.Append(' ', indent * 2);
        builder.Append("# ");
        builder.AppendLine(text);
    }

    internal static void AppendActiveTemplateLine(StringBuilder builder, int indent, string text)
    {
        builder.Append(' ', indent * 2);
        builder.AppendLine(text);
    }

    internal static void AppendActiveTemplateBlankLine(StringBuilder builder)
    {
        builder.AppendLine();
    }

    internal static void AppendTemplateBlankLine(StringBuilder builder)
    {
        builder.AppendLine("#");
    }
}
