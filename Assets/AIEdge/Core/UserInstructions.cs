using System;
using System.IO;
using UnityEngine;

namespace AIEdge
{
    /// <summary>
    /// The user's standing instructions for Gemma's replies, in their own words: facts about them and how replies
    /// should treat what they ask about or point at ("I'm on a strict diet: if I look at a cake, make fun of me").
    /// Added to the system prompt of AI Chat, Point &amp; Ask and What's That? (not Tiny Garden, whose function-calling
    /// model needs its exact prompt). Stored in <c>persistentDataPath/user_instructions.json</c>.
    /// </summary>
    public static class UserInstructions
    {
        const string k_FileName = "user_instructions.json";

        /// <summary>Longest instructions kept, in characters, so they never crowd out the conversation.</summary>
        public const int MaxLength = 600;

        /// <summary>Ready-made instructions to start from.</summary>
        public static readonly (string Name, string Text)[] Presets =
        {
            ("Strict diet",
                "I'm on a strict diet. Whenever I ask about or point at sweets, cake, pastries, fried food or other junk " +
                "food, make fun of me for it in a playful, teasing way and remind me of my diet. Praise me for healthy food."),
            ("Kid-friendly",
                "I'm 8 years old. Use simple words, short sentences and a fun fact when you can. Keep everything safe for kids."),
            ("Plant lover",
                "I love plants. When something is a plant or flower, tell me how to care for it. Otherwise, suggest a plant " +
                "that would look good next to it."),
            ("Safety first",
                "I'm clumsy. If something I ask about or point at could be hot, sharp, fragile or dangerous, warn me first."),
            ("Pirate",
                "Talk like a friendly pirate in every reply, but keep the facts correct."),
        };

        [Serializable]
        class Data
        {
            public bool enabled = true;
            public string text = "";
        }

        static Data s_Data;
        static string s_PathOverride;

        /// <summary>Raised when the instructions change.</summary>
        public static event Action Changed;

        /// <summary>The instructions as written.</summary>
        public static string Text => Load().text;

        /// <summary>Whether the instructions are applied.</summary>
        public static bool Enabled => Load().enabled;

        /// <summary>True when there are instructions and they are on.</summary>
        public static bool IsActive => Enabled && !string.IsNullOrWhiteSpace(Text);

        static string FilePath => s_PathOverride ?? Path.Combine(Path.GetDirectoryName(ModelStore.ModelsDirectory), k_FileName);

        /// <summary>Redirects storage to a file (tests). Pass null to restore.</summary>
        public static void SetPathForTests(string path)
        {
            s_PathOverride = path;
            s_Data = null;
        }

        /// <summary>Saves new instructions (trimmed to <see cref="MaxLength"/>) and whether they are on.</summary>
        public static void Set(string text, bool enabled)
        {
            text = (text ?? "").Trim();
            if (text.Length > MaxLength)
                text = text.Substring(0, MaxLength);
            s_Data = new Data { text = text, enabled = enabled };
            try
            {
                File.WriteAllText(FilePath, JsonUtility.ToJson(s_Data));
            }
            catch (IOException e)
            {
                Debug.LogWarning($"[AIEdge] Couldn't save instructions: {e.Message}");
            }

            Changed?.Invoke();
        }

        /// <summary>
        /// A system prompt with the user's instructions appended to <paramref name="basePrompt"/> (a chat persona, or
        /// empty). Returns <paramref name="basePrompt"/> unchanged when the instructions are off or empty.
        /// </summary>
        public static string Compose(string basePrompt) => Compose(basePrompt, IsActive ? Text : null);

        /// <summary><see cref="Compose(string)"/> with explicit instructions (null or empty for none).</summary>
        public static string Compose(string basePrompt, string instructions)
        {
            basePrompt = (basePrompt ?? "").Trim();
            if (string.IsNullOrWhiteSpace(instructions))
                return basePrompt;

            var block = "The user gave you these personal instructions. Follow them in every reply whenever they apply " +
                        "to what the user asks about, shows or points at, while keeping your answer accurate. Write the " +
                        "whole reply in the reply language, even when these instructions are written in another:\n" +
                        instructions.Trim();
            return basePrompt.Length == 0 ? block : $"{basePrompt}\n\n{block}";
        }

        /// <summary>A one-line summary for status lines: "On · I'm on a strict diet. Whenever I…".</summary>
        public static string Summary(int maxChars = 60)
        {
            if (string.IsNullOrWhiteSpace(Text))
                return "None";
            var text = Text.Replace('\n', ' ');
            if (text.Length > maxChars)
                text = text.Substring(0, maxChars - 1).TrimEnd() + "…";
            return $"{(Enabled ? "On" : "Off")} · {text}";
        }

        static Data Load()
        {
            if (s_Data != null)
                return s_Data;
            s_Data = new Data();
            try
            {
                if (File.Exists(FilePath))
                    s_Data = JsonUtility.FromJson<Data>(File.ReadAllText(FilePath)) ?? new Data();
            }
            catch (Exception e) when (e is IOException || e is ArgumentException)
            {
                Debug.LogWarning($"[AIEdge] Couldn't read instructions: {e.Message}");
            }

            s_Data.text ??= "";
            return s_Data;
        }
    }
}
