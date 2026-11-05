using System.Collections.Generic;

namespace AIEdge.Chat
{
    /// <summary>
    /// Chooses which earlier messages to replay into a new LiteRT-LM conversation. The KV cache is small
    /// (4000 tokens for Gemma 4 E2B), so long threads keep only their most recent turns.
    /// </summary>
    public static class ChatContext
    {
        /// <summary>Rough characters per token for budgeting (English text with Gemma's tokenizer).</summary>
        public const int CharsPerToken = 4;

        /// <summary>
        /// Returns the most recent messages whose text fits in <paramref name="maxChars"/>, starting at a user
        /// message and skipping empty model messages. The newest messages always win.
        /// </summary>
        public static List<ChatTurn> SelectHistory(IReadOnlyList<ChatMessage> messages, int maxChars)
        {
            var selected = new List<ChatTurn>();
            var used = 0;
            for (var i = messages.Count - 1; i >= 0; i--)
            {
                var message = messages[i];
                if (message.role == ChatRole.Model && string.IsNullOrWhiteSpace(message.text))
                    continue;

                var length = message.text.Length;
                if (used + length > maxChars)
                    break;

                used += length;
                selected.Add(new ChatTurn(message.role, message.text));
            }

            selected.Reverse();

            // Conversations replay from a user turn.
            while (selected.Count > 0 && selected[0].role != ChatRole.User)
                selected.RemoveAt(0);

            return selected;
        }

        /// <summary>Character budget for replayed history given the model's KV-cache size, leaving room for the next answer.</summary>
        public static int HistoryBudgetChars(int maxTokens) => maxTokens / 2 * CharsPerToken;

        /// <summary>True once the KV cache is full enough that the conversation should be rebuilt with trimmed history.</summary>
        public static bool ShouldTrim(int kvTokens, int maxTokens) => kvTokens > maxTokens * 3 / 4;
    }
}
