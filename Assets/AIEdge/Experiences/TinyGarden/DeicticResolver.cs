using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AIEdge.TinyGarden
{
    /// <summary>
    /// Turns pointing words into plot numbers before the text reaches the model: with plots 4 and 5 pointed at,
    /// "water these" becomes "water plots 4 and 5" and "plant a rose here" becomes "plant a rose in plots 4 and 5".
    /// The 270M model then sees the explicit numbers it was trained on.
    /// </summary>
    public static class DeicticResolver
    {
        const RegexOptions k_Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        // "water here" / "harvest there": the verb takes the plots directly.
        static readonly Regex k_VerbPlace = new Regex(@"\b(water|harvest)\s+(right\s+)?(here|there)\b", k_Options);
        static readonly Regex k_Place = new Regex(@"\b(right\s+)?(here|there)\b", k_Options);
        static readonly Regex k_Things = new Regex(@"\b(these|those|this|that)(\s+(plots?|ones?|spots?|flowers?|plants?))?\b", k_Options);

        /// <summary>True if the text contains a pointing word.</summary>
        public static bool HasDeixis(string text) =>
            !string.IsNullOrEmpty(text) && (k_Place.IsMatch(text) || k_Things.IsMatch(text));

        /// <summary>Replaces pointing words with the plots; returns the text unchanged if nothing is pointed at.</summary>
        public static string Resolve(string text, IReadOnlyCollection<int> plots)
        {
            if (string.IsNullOrEmpty(text) || plots == null || plots.Count == 0 || !HasDeixis(text))
                return text;

            var list = FormatPlots(plots);
            text = k_VerbPlace.Replace(text, m => $"{m.Groups[1].Value} {list}");
            text = k_Place.Replace(text, $"in {list}");
            text = k_Things.Replace(text, list);
            return text;
        }

        static readonly Regex k_PlotNumbers = new Regex(@"\bplots?\s+((?:[1-9](?:\s*,\s*|\s+and\s+|\s+))*[1-9])\b", k_Options);
        static readonly Regex k_Digit = new Regex("[1-9]");

        /// <summary>
        /// Plot numbers written out in the text ("plot 9", "plots 1, 2 and 3"). Used when the model calls a tool with
        /// an empty plot list although the command named the plots.
        /// </summary>
        public static int[] ExplicitPlots(string text)
        {
            if (string.IsNullOrEmpty(text))
                return new int[0];
            return k_PlotNumbers.Matches(text).Cast<Match>()
                .SelectMany(m => k_Digit.Matches(m.Groups[1].Value).Cast<Match>().Select(d => int.Parse(d.Value)))
                .Distinct().OrderBy(n => n).ToArray();
        }

        /// <summary>"plot 5", or "plots 1, 2 and 3", the phrasing the model was tuned on.</summary>
        public static string FormatPlots(IEnumerable<int> plots)
        {
            var numbers = plots.Distinct().OrderBy(n => n).ToList();
            if (numbers.Count == 1)
                return $"plot {numbers[0]}";
            return $"plots {string.Join(", ", numbers.Take(numbers.Count - 1))} and {numbers[numbers.Count - 1]}";
        }
    }
}
