using System.Text;
using System.Text.RegularExpressions;

namespace AIEdge.UI
{
    /// <summary>
    /// Converts the Markdown that Gemma writes (bold, italics, inline code, headings, bullet and numbered lists,
    /// code fences) into TextMeshPro rich text. Everything else is shown literally: '&lt;' is escaped so model or
    /// user text can't inject rich-text tags.
    /// </summary>
    public static class MarkdownToTmp
    {
        static readonly Regex k_Bold = new Regex(@"\*\*(.+?)\*\*|__(.+?)__", RegexOptions.Compiled);
        static readonly Regex k_Italic = new Regex(@"(?<![\*\w])\*(?!\s)(.+?)(?<!\s)\*(?!\*)|(?<![_\w])_(?!\s)(.+?)(?<!\s)_(?![_\w])", RegexOptions.Compiled);
        static readonly Regex k_InlineCode = new Regex(@"`([^`]+)`", RegexOptions.Compiled);
        static readonly Regex k_Heading = new Regex(@"^(#{1,6})\s+(.*)$", RegexOptions.Compiled);
        static readonly Regex k_Bullet = new Regex(@"^(\s*)[-*+]\s+(.*)$", RegexOptions.Compiled);
        static readonly Regex k_Numbered = new Regex(@"^(\s*)(\d+)[.)]\s+(.*)$", RegexOptions.Compiled);

        const string k_CodeColor = "#9FD3FF";

        /// <summary>Escapes text for TextMeshPro rich text without any formatting.</summary>
        public static string Escape(string text) => (text ?? "").Replace("<", "<noparse><</noparse>");

        /// <summary>Converts Markdown to TextMeshPro rich text.</summary>
        public static string Convert(string markdown)
        {
            if (string.IsNullOrEmpty(markdown))
                return "";

            var output = new StringBuilder(markdown.Length + 32);
            var inFence = false;
            var lines = markdown.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith("```"))
                {
                    inFence = !inFence;
                    output.Append(inFence ? $"<color={k_CodeColor}>" : "</color>");
                    continue;
                }

                if (i > 0 && output.Length > 0)
                    output.Append('\n');

                if (inFence)
                {
                    output.Append(Escape(line));
                    continue;
                }

                output.Append(ConvertLine(line));
            }

            // An unclosed fence (still streaming) must not leave the color open.
            if (inFence)
                output.Append("</color>");

            return output.ToString().TrimStart('\n');
        }

        static string ConvertLine(string line)
        {
            var heading = k_Heading.Match(line);
            if (heading.Success)
                return $"<b>{Inline(heading.Groups[2].Value)}</b>";

            var bullet = k_Bullet.Match(line);
            if (bullet.Success)
                return $"{Indent(bullet.Groups[1].Value)}• {Inline(bullet.Groups[2].Value)}";

            var numbered = k_Numbered.Match(line);
            if (numbered.Success)
                return $"{Indent(numbered.Groups[1].Value)}{numbered.Groups[2].Value}. {Inline(numbered.Groups[3].Value)}";

            return Inline(line);
        }

        static string Indent(string leading) => new string(' ', leading.Replace("\t", "  ").Length);

        static string Inline(string text)
        {
            text = Escape(text);
            // Code spans first so their contents aren't styled.
            var codeSpans = new System.Collections.Generic.List<string>();
            text = k_InlineCode.Replace(text, m =>
            {
                codeSpans.Add($"<color={k_CodeColor}>{m.Groups[1].Value}</color>");
                return $"\u0001{codeSpans.Count - 1}\u0001";
            });
            text = k_Bold.Replace(text, m => $"<b>{(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)}</b>");
            text = k_Italic.Replace(text, m => $"<i>{(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)}</i>");
            for (var i = 0; i < codeSpans.Count; i++)
                text = text.Replace($"\u0001{i}\u0001", codeSpans[i]);
            return text;
        }
    }
}
