using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;

namespace AIEdge
{
    /// <summary>
    /// Runs a LiteRT-LM model on device through the <c>com.xraiedge.litertlm.LlmBridge</c> Android plugin.
    /// All inference happens on a background thread; events are raised on the Unity main thread.
    /// </summary>
    public class LlmEngine : MonoBehaviour
    {
        const string k_BridgeClass = "com.xraiedge.litertlm.LlmBridge";

        /// <summary>Raised when a model finished loading.</summary>
        public event Action<LlmLoadInfo> Loaded;

        /// <summary>Raised for each streamed piece of the response.</summary>
        public event Action<string> TokenReceived;

        /// <summary>Raised when a response is complete.</summary>
        public event Action<LlmStats> ResponseDone;

        /// <summary>Raised when loading or inference fails.</summary>
        public event Action<string> Failed;

        /// <summary>
        /// Handles a tool call from the model. Invoked on the inference thread, not the main thread:
        /// enqueue Unity work and return the tool result JSON right away.
        /// </summary>
        public Func<string, string, string> ToolCallHandler { get; set; }

        /// <summary>True while a model is loading or a response is being generated.</summary>
        public bool IsBusy { get; private set; }

        /// <summary>True once a model is loaded.</summary>
        public bool IsLoaded { get; private set; }

        readonly ConcurrentQueue<Action> m_MainThreadQueue = new ConcurrentQueue<Action>();
        AndroidJavaObject m_Bridge;

        /// <summary>Returns which OpenCL driver files exist and whether one loads (GPU backend prerequisite).</summary>
        public static string ProbeGpu()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using var bridgeClass = new AndroidJavaClass(k_BridgeClass);
            return bridgeClass.CallStatic<string>("probeGpu");
#else
            return "n/a (not running on Android)";
#endif
        }

        /// <summary>Loads a .litertlm model. Replaces any loaded model.</summary>
        /// <param name="modelPath">Absolute path to the model file on device.</param>
        /// <param name="backend">Backend for the language model.</param>
        /// <param name="visionBackend">Backend for the image encoder, or <see cref="LlmBackend.None"/> for text only.</param>
        /// <param name="maxTokens">KV-cache size in tokens, or 0 for the model default.</param>
        public void Load(string modelPath, LlmBackend backend, LlmBackend visionBackend, int maxTokens)
        {
            if (!EnsureBridge())
                return;

            IsBusy = true;
            IsLoaded = false;
            m_Bridge.Call("load", modelPath, ToBridgeName(backend), ToBridgeName(visionBackend), maxTokens, Application.temporaryCachePath);
        }

        /// <summary>Starts a new conversation, clearing history.</summary>
        /// <param name="systemPrompt">System instruction, or empty.</param>
        /// <param name="sampler">Sampling parameters.</param>
        /// <param name="toolsJson">JSON array of OpenAPI tool descriptions, or empty.</param>
        /// <param name="constrainedDecoding">Constrain tool calls to the tool schema.</param>
        /// <param name="history">Earlier turns to resume a chat, or null to start fresh.</param>
        public void NewConversation(string systemPrompt, SamplerSettings sampler, string toolsJson = "", bool constrainedDecoding = false,
            IReadOnlyList<ChatTurn> history = null)
        {
            if (!EnsureBridge())
                return;

            m_Bridge.Call("newConversation", systemPrompt ?? "", sampler.topK, (double)sampler.topP, (double)sampler.temperature,
                toolsJson ?? "", constrainedDecoding, ChatTurn.ToJson(history));
        }

        /// <summary>Sends a user message. Responses stream through <see cref="TokenReceived"/>.</summary>
        /// <param name="text">User text.</param>
        /// <param name="imagePath">Path to a PNG/JPEG file to attach, or empty.</param>
        public void Send(string text, string imagePath = "")
        {
            if (!EnsureBridge())
                return;

            IsBusy = true;
            m_Bridge.Call("send", text, imagePath ?? "");
        }

        /// <summary>Stops the current response. It still completes through <see cref="ResponseDone"/>, flagged as cancelled.</summary>
        public void Cancel()
        {
            m_Bridge?.Call("cancel");
        }

        /// <summary>Releases the loaded model and its memory.</summary>
        public void Unload()
        {
            IsLoaded = false;
            IsBusy = false;
            m_Bridge?.Call("unload");
        }

        void Update()
        {
            while (m_MainThreadQueue.TryDequeue(out var action))
                action();
        }

        void OnDestroy()
        {
            if (m_Bridge == null)
                return;

            m_Bridge.Call("close");
            m_Bridge.Dispose();
            m_Bridge = null;
        }

        bool EnsureBridge()
        {
            if (m_Bridge != null)
                return true;

#if UNITY_ANDROID && !UNITY_EDITOR
            m_Bridge = new AndroidJavaObject(k_BridgeClass, new ListenerProxy(this));
            return true;
#else
            Failed?.Invoke("LiteRT-LM runs on Android devices only.");
            return false;
#endif
        }

        static string ToBridgeName(LlmBackend backend) => backend switch
        {
            LlmBackend.Cpu => "cpu",
            LlmBackend.Gpu => "gpu",
            _ => "none",
        };

        void Post(Action action) => m_MainThreadQueue.Enqueue(action);

        /// <summary>Receives bridge callbacks on the inference thread and forwards them to the main thread.</summary>
        class ListenerProxy : AndroidJavaProxy
        {
            readonly LlmEngine m_Engine;

            public ListenerProxy(LlmEngine engine) : base(k_BridgeClass + "$Listener")
            {
                m_Engine = engine;
            }

            public void onLoaded(string infoJson)
            {
                var info = JsonUtility.FromJson<LlmLoadInfo>(infoJson);
                m_Engine.Post(() =>
                {
                    m_Engine.IsBusy = false;
                    m_Engine.IsLoaded = true;
                    m_Engine.Loaded?.Invoke(info);
                });
            }

            public void onToken(string text)
            {
                m_Engine.Post(() => m_Engine.TokenReceived?.Invoke(text));
            }

            public void onDone(string statsJson)
            {
                var stats = JsonUtility.FromJson<LlmStats>(statsJson);
                m_Engine.Post(() =>
                {
                    m_Engine.IsBusy = false;
                    m_Engine.ResponseDone?.Invoke(stats);
                });
            }

            public void onError(string message)
            {
                m_Engine.Post(() =>
                {
                    m_Engine.IsBusy = false;
                    m_Engine.Failed?.Invoke(message);
                });
            }

            public string onToolCall(string name, string argsJson)
            {
                var handler = m_Engine.ToolCallHandler;
                return handler != null ? handler(name, argsJson) : "{\"result\":\"success\"}";
            }
        }
    }
}
