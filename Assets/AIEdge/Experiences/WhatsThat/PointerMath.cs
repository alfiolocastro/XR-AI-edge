using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

namespace AIEdge.WhatsThat
{
    /// <summary>
    /// The plain logic of What's That?: the pointing pose, how much of the camera image to look at, the prompt, and
    /// how the answer and distance are shown and spoken. Plain C# so it can be unit tested.
    /// </summary>
    public static class PointerMath
    {
        /// <summary>Index fingertip at least this far from the palm: the finger is extended.</summary>
        public const float IndexExtended = 0.07f;

        /// <summary>Middle fingertip at most this far from the palm: the finger is curled.</summary>
        public const float MiddleCurled = 0.06f;

        /// <summary>True for a pointing hand: index finger out, middle finger curled.</summary>
        public static bool IsPointingPose(Vector3 indexTip, Vector3 middleTip, Vector3 palm) =>
            Vector3.Distance(indexTip, palm) >= IndexExtended && Vector3.Distance(middleTip, palm) <= MiddleCurled;

        /// <summary>
        /// Side, in meters, of the square around the pointed spot that is depth-sampled and cropped: large enough for
        /// a whole object nearby, not the whole room far away.
        /// </summary>
        public static float RegionSize(float distance) => Mathf.Clamp(distance * 0.45f, 0.3f, 1.4f);

        /// <summary>The model prompt: name in bold, then a short description, in the chosen language.</summary>
        public static string Prompt(string facts, string language)
        {
            var reply = language == SpeechLanguage.Italian ? " Reply in Italian." : "";
            var measured = string.IsNullOrEmpty(facts) ? "" : $" {facts}";
            return "The user is pointing at the object in the center of this image. " +
                   "Start with its name in bold, then describe it in one or two short sentences. " +
                   "Don't mention its distance." + measured + reply;
        }

        static readonly Regex s_Bold = new Regex(@"\*\*(.+?)\*\*", RegexOptions.Singleline);

        /// <summary>
        /// Splits a reply into the object's name (the first bold text, or the first sentence) and the rest.
        /// </summary>
        public static (string name, string description) ParseAnswer(string reply)
        {
            reply = (reply ?? "").Trim();
            if (reply.Length == 0)
                return ("", "");

            var bold = s_Bold.Match(reply);
            if (bold.Success)
            {
                var name = bold.Groups[1].Value.Trim().TrimEnd('.', ':');
                // "**Mug** — a white mug" leads with the name; "This is a **mug**. …" keeps the whole sentence.
                var rest = bold.Index == 0
                    ? reply.Substring(bold.Length).TrimStart(':', '-', '–', '—', ',', '.', ' ').Trim()
                    : reply.Replace("**", "").Trim();
                return (name, rest);
            }

            var end = reply.IndexOfAny(new[] { '.', '\n', ':' });
            return end > 0 ? (reply.Substring(0, end).Trim(), reply.Substring(end + 1).Trim()) : (reply, "");
        }

        /// <summary>The distance as a sentence to speak: "It's about 1.4 meters away." / "Si trova a circa 1,4 metri."</summary>
        public static string SpokenDistance(float meters, string language)
        {
            var italian = language == SpeechLanguage.Italian;
            string amount;
            if (meters < 1f)
            {
                var cm = Mathf.Max(1, Mathf.RoundToInt(meters * 100f / 5f) * 5);
                amount = $"{cm} {(italian ? "centimetri" : "centimeters")}";
            }
            else
            {
                var rounded = Mathf.Round(meters * 10f) / 10f;
                var culture = italian ? CultureInfo.GetCultureInfo("it-IT") : CultureInfo.InvariantCulture;
                var number = Mathf.Approximately(rounded, Mathf.Round(rounded))
                    ? Mathf.RoundToInt(rounded).ToString(culture)
                    : rounded.ToString("0.0", culture);
                var one = Mathf.Approximately(rounded, 1f);
                amount = italian ? $"{number} {(one ? "metro" : "metri")}" : $"{number} {(one ? "meter" : "meters")}";
            }

            return italian ? $"Si trova a circa {amount} da te." : $"It's about {amount} away from you.";
        }
    }
}
