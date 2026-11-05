using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace AIEdge
{
    /// <summary>
    /// Turns streamed Markdown replies into speakable sentences: strips formatting, skips code blocks, and splits at
    /// sentence and line ends so speech can start while the model is still writing.
    /// </summary>
    public class SpeechText
    {
        const int k_MaxChunk = 280;

        static readonly Regex k_Link = new Regex(@"\[([^\]]+)\]\([^)]*\)", RegexOptions.Compiled);
        static readonly Regex k_LinePrefix = new Regex(@"^\s*(#{1,6}\s+|[-*+]\s+|>\s*|\d+[.)]\s+)", RegexOptions.Compiled | RegexOptions.Multiline);
        static readonly Regex k_Emphasis = new Regex(@"(?<![\w])(\*\*|__|\*|_|~~)|(\*\*|__|\*|_|~~)(?![\w])", RegexOptions.Compiled);
        static readonly Regex k_Whitespace = new Regex(@"\s+", RegexOptions.Compiled);
        static readonly Regex k_Boundary = new Regex(@"(?<=[.!?…])\s+|\n", RegexOptions.Compiled);

        readonly StringBuilder m_Buffer = new StringBuilder();
        bool m_InCode;

        /// <summary>Removes Markdown so a speech engine reads only the words. Returns empty for nothing speakable.</summary>
        public static string Clean(string markdown)
        {
            if (string.IsNullOrEmpty(markdown))
                return "";

            var text = k_Link.Replace(markdown, "$1");
            text = k_LinePrefix.Replace(text, "");
            // Keep underscores inside words (snake_case) by only stripping emphasis markers at word edges.
            text = k_Emphasis.Replace(text, "");
            text = text.Replace("`", "");
            text = k_Whitespace.Replace(text, " ").Trim();
            return HasWords(text) ? text : "";
        }

        /// <summary>Adds streamed text; returns the sentences completed by it, cleaned and ready to speak.</summary>
        public List<string> Append(string text)
        {
            var ready = new List<string>();
            m_Buffer.Append(text);
            while (true)
            {
                var buffered = m_Buffer.ToString();
                var match = k_Boundary.Match(buffered);
                int end;
                if (match.Success)
                {
                    end = match.Index + match.Length;
                }
                else if (buffered.Length > k_MaxChunk && !m_InCode)
                {
                    // A long run without punctuation: break at the last comma or space.
                    var cut = buffered.LastIndexOfAny(new[] { ',', ';', ' ' }, k_MaxChunk);
                    end = cut > 0 ? cut + 1 : k_MaxChunk;
                }
                else
                {
                    break;
                }

                var piece = buffered.Substring(0, end);
                m_Buffer.Remove(0, end);
                AddPiece(piece, ready);
            }

            return ready;
        }

        /// <summary>Returns whatever is left once the reply is complete.</summary>
        public List<string> Flush()
        {
            var ready = new List<string>();
            AddPiece(m_Buffer.ToString(), ready);
            m_Buffer.Clear();
            m_InCode = false;
            return ready;
        }

        void AddPiece(string piece, List<string> ready)
        {
            if (piece.TrimStart().StartsWith("```"))
            {
                m_InCode = !m_InCode;
                return;
            }

            if (m_InCode)
                return;

            var cleaned = Clean(piece);
            if (cleaned.Length > 0)
                ready.Add(cleaned);
        }

        static bool HasWords(string text)
        {
            foreach (var c in text)
                if (char.IsLetterOrDigit(c))
                    return true;
            return false;
        }
    }
}
