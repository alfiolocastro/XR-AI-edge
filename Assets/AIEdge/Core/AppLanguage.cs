using System;
using UnityEngine;

namespace AIEdge
{
    /// <summary>
    /// The language the app talks in, chosen in the launcher: every model reply, the voice that reads it and the
    /// speech input. Remembered between sessions. Pinning one language keeps a reply from mixing languages (personal
    /// instructions and personas are usually written in English, and would otherwise pull an Italian reply into
    /// English) and from switching voices mid-answer.
    /// </summary>
    public static class AppLanguage
    {
        const string k_Key = "AIEdge.Language";
        // What's That? had its own language choice before this setting; it seeds the setting once.
        const string k_LegacyWhatsThatKey = "AIEdge.WhatsThat.Language";

        static string s_Current;

        /// <summary>Raised after the language changes.</summary>
        public static event Action Changed;

        /// <summary>The supported languages, as two-letter codes.</summary>
        public static readonly string[] All = { SpeechLanguage.English, SpeechLanguage.Italian };

        /// <summary>The current language: "en" or "it".</summary>
        public static string Current
        {
            get
            {
                if (s_Current == null)
                {
                    var stored = PlayerPrefs.GetString(k_Key, PlayerPrefs.GetString(k_LegacyWhatsThatKey, SpeechLanguage.English));
                    s_Current = Array.IndexOf(All, stored) >= 0 ? stored : SpeechLanguage.English;
                }

                return s_Current;
            }
        }

        /// <summary>True when the app talks in Italian.</summary>
        public static bool IsItalian => Current == SpeechLanguage.Italian;

        /// <summary>Sets and saves the language. Unknown codes are ignored.</summary>
        public static void Set(string language)
        {
            if (Array.IndexOf(All, language) < 0 || language == Current)
                return;
            s_Current = language;
            PlayerPrefs.SetString(k_Key, language);
            PlayerPrefs.Save();
            Changed?.Invoke();
        }

        /// <summary>Name of a language in that language, for buttons.</summary>
        public static string DisplayName(string language) => language == SpeechLanguage.Italian ? "Italiano" : "English";

        /// <summary>Rule that keeps every sentence of a reply in <paramref name="language"/>.</summary>
        public static string ReplyRule(string language) => language == SpeechLanguage.Italian
            ? "Rispondi sempre e solo in italiano, in ogni frase, anche quando la persona, le istruzioni o la domanda sono scritte in un'altra lingua. " +
              "Se ti viene chiesto uno stile o un personaggio (per esempio un pirata), interpretalo in italiano: niente saluti, esclamazioni o battute in inglese."
            : "Always reply in English, in every sentence, even when the persona, the instructions or the question are written in another language. " +
              "If you are asked for a style or a character, play it in English.";

        /// <summary>
        /// A system prompt for a conversation: <paramref name="basePrompt"/> (a chat persona, or empty), the user's
        /// personal instructions when on, and the reply-language rule last, so it wins over both.
        /// </summary>
        public static string SystemPrompt(string basePrompt) => SystemPrompt(basePrompt, Current);

        /// <summary><see cref="SystemPrompt(string)"/> for an explicit language.</summary>
        public static string SystemPrompt(string basePrompt, string language)
        {
            var composed = UserInstructions.Compose(basePrompt);
            var rule = ReplyRule(language);
            return composed.Length == 0 ? rule : $"{composed}\n\n{rule}";
        }

        /// <summary>
        /// A user message as sent to the model: for Italian, a short reminder is appended (small models drift back to
        /// English otherwise). The conversation shown and saved keeps the original text.
        /// </summary>
        public static string ForModel(string message) => ForModel(message, Current);

        /// <summary><see cref="ForModel(string)"/> for an explicit language.</summary>
        public static string ForModel(string message, string language) =>
            language == SpeechLanguage.Italian ? message + "\n\n(Rispondi solo in italiano, anche nello stile richiesto: nessuna parola in inglese.)" : message;

        /// <summary>Resets the cached value so the next read comes from PlayerPrefs (tests).</summary>
        public static void ResetForTests() => s_Current = null;
    }
}
