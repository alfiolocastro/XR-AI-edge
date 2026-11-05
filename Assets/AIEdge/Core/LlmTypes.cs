using System;
using System.Collections.Generic;
using UnityEngine;

namespace AIEdge
{
    /// <summary>
    /// LiteRT-LM compute backend.
    /// </summary>
    public enum LlmBackend
    {
        None,
        Cpu,
        Gpu,
    }

    /// <summary>
    /// Sampling parameters for a conversation. Defaults match Google AI Edge Gallery.
    /// </summary>
    [Serializable]
    public struct SamplerSettings
    {
        public int topK;
        public float topP;
        public float temperature;

        public static SamplerSettings Default => new SamplerSettings { topK = 64, topP = 0.95f, temperature = 1.0f };
    }

    /// <summary>
    /// Result of loading a model, reported by the Android bridge.
    /// </summary>
    [Serializable]
    public class LlmLoadInfo
    {
        public string backend;
        public string visionBackend;
        public double initSec;
        public long pssMb;
    }

    /// <summary>
    /// Per-turn inference statistics (time to first token, decode speed, memory).
    /// </summary>
    [Serializable]
    public class LlmStats
    {
        public double ttftSec;
        public int decodeChunks;
        public double decodeTokPerSec;
        public double totalSec;
        public int kvTokens;
        public int turnTokens;
        public long pssMb;
        public bool cancelled;

        public override string ToString() =>
            $"TTFT {ttftSec:0.00}s · {(decodeTokPerSec > 0 ? $"{decodeTokPerSec:0.0} tok/s · " : "")}{turnTokens} tok · KV {kvTokens} · {pssMb} MB{(cancelled ? " · stopped" : "")}";
    }

    /// <summary>
    /// Who said a chat turn.
    /// </summary>
    public enum ChatRole
    {
        User,
        Model,
    }

    /// <summary>
    /// One text turn of conversation history, passed to the engine to resume a chat.
    /// </summary>
    [Serializable]
    public struct ChatTurn
    {
        public ChatRole role;
        public string text;

        public ChatTurn(ChatRole role, string text)
        {
            this.role = role;
            this.text = text;
        }

        [Serializable]
        struct Wire
        {
            public string role;
            public string text;
        }

        [Serializable]
        struct WireList
        {
            public Wire[] turns;
        }

        /// <summary>Serializes turns as the bridge expects: <c>[{"role":"user"|"model","text":"..."}]</c>; empty for none.</summary>
        public static string ToJson(IReadOnlyList<ChatTurn> turns)
        {
            if (turns == null || turns.Count == 0)
                return "";

            var wire = new WireList { turns = new Wire[turns.Count] };
            for (var i = 0; i < turns.Count; i++)
                wire.turns[i] = new Wire { role = turns[i].role == ChatRole.Model ? "model" : "user", text = turns[i].text ?? "" };

            // JsonUtility can't write a bare array: serialize the wrapper and strip it.
            var json = JsonUtility.ToJson(wire);
            return json.Substring("{\"turns\":".Length, json.Length - "{\"turns\":".Length - 1);
        }
    }
}
