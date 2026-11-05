using System;
using System.Collections.Generic;

namespace AIEdge.Chat
{
    /// <summary>
    /// One message in a chat thread.
    /// </summary>
    [Serializable]
    public class ChatMessage
    {
        public ChatRole role;
        public string text = "";

        /// <summary>Inference stats for model messages; null for user messages.</summary>
        public LlmStats stats;

        /// <summary>True if the user stopped this response before it finished.</summary>
        public bool stopped;
    }

    /// <summary>
    /// A saved conversation: messages, system prompt and timestamps.
    /// </summary>
    [Serializable]
    public class ChatThread
    {
        const int k_TitleLength = 40;

        public string id;
        public string title = "New chat";
        public string systemPrompt = "";
        public long createdUnixMs;
        public long updatedUnixMs;
        public List<ChatMessage> messages = new List<ChatMessage>();

        /// <summary>Creates an empty thread with a new id.</summary>
        public static ChatThread Create(string systemPrompt = "")
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return new ChatThread
            {
                id = Guid.NewGuid().ToString("N"),
                systemPrompt = systemPrompt ?? "",
                createdUnixMs = now,
                updatedUnixMs = now,
            };
        }

        /// <summary>True if the thread has no messages.</summary>
        public bool IsEmpty => messages.Count == 0;

        /// <summary>Text of the last message, for previews.</summary>
        public string LastMessageText => messages.Count > 0 ? messages[messages.Count - 1].text : "";

        /// <summary>Adds a message and refreshes the title and timestamp.</summary>
        public ChatMessage Add(ChatRole role, string text)
        {
            var message = new ChatMessage { role = role, text = text ?? "" };
            messages.Add(message);
            if (role == ChatRole.User && title == "New chat")
                title = MakeTitle(text);
            Touch();
            return message;
        }

        /// <summary>Marks the thread as just updated.</summary>
        public void Touch() => updatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        /// <summary>Builds a one-line title from the first user message.</summary>
        public static string MakeTitle(string text)
        {
            var line = (text ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
            while (line.Contains("  "))
                line = line.Replace("  ", " ");
            if (line.Length == 0)
                return "New chat";
            return line.Length <= k_TitleLength ? line : line.Substring(0, k_TitleLength - 1).TrimEnd() + "…";
        }
    }
}
