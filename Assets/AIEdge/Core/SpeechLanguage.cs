using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AIEdge
{
    /// <summary>
    /// Guesses whether text is English or Italian, so each reply is read by the matching voice. Counts common
    /// function words of each language, which is reliable from a short sentence on. Plain C# so it can be unit tested.
    /// </summary>
    public static class SpeechLanguage
    {
        public const string English = "en";
        public const string Italian = "it";

        static readonly HashSet<string> s_English = new HashSet<string>
        {
            "the", "and", "is", "are", "was", "of", "to", "it", "that", "this", "you", "your", "with", "for",
            "on", "be", "have", "has", "not", "what", "can", "will", "there", "they", "about", "which", "from", "or",
            "as", "at", "by", "an", "my", "we", "do", "does", "if", "but", "would", "should", "here", "how", "some",
        };

        static readonly HashSet<string> s_Italian = new HashSet<string>
        {
            "il", "lo", "la", "le", "gli", "un", "una", "uno", "di", "da", "del", "della", "dei", "delle", "dello",
            "che", "è", "e", "non", "per", "con", "sono", "ci", "si", "mi", "ti", "anche", "come", "questo", "questa",
            "nel", "nella", "al", "alla", "ai", "più", "ma", "se", "cosa", "perché", "puoi", "può", "hai", "ho",
            "sei", "molto", "ecco", "tuo", "tua", "suo", "sua", "loro", "quando", "dove", "fa", "essere", "sul", "sulla",
        };

        // Letters and digits together, so names like "E2B" stay one word instead of yielding a stray "e".
        static readonly Regex s_Word = new Regex(@"[\p{L}\p{N}']+", RegexOptions.Compiled);

        /// <summary>
        /// "en" or "it" when one language clearly wins, or null when the text gives no clear signal
        /// (very short, mixed, or another language).
        /// </summary>
        public static string Detect(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var english = 0;
            var italian = 0;
            foreach (Match match in s_Word.Matches(text.ToLowerInvariant()))
            {
                // Elisions such as "l'acqua", "dell'albero", "c'è" mark Italian.
                var word = match.Value;
                var apostrophe = word.IndexOf('\'');
                if (apostrophe > 0 && apostrophe < word.Length - 1)
                {
                    var head = word.Substring(0, apostrophe);
                    if (head == "l" || head == "dell" || head == "un" || head == "c" || head == "nell" || head == "all" || head == "d")
                    {
                        italian++;
                        word = word.Substring(apostrophe + 1);
                    }
                }

                if (s_English.Contains(word))
                    english++;
                if (s_Italian.Contains(word))
                    italian++;
            }

            if (english == italian)
                return null;
            return english > italian ? English : Italian;
        }
    }
}
