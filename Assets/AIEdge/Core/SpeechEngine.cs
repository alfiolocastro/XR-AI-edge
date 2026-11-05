using System;
using System.Collections.Concurrent;
using UnityEngine;

namespace AIEdge
{
    /// <summary>
    /// Reads model replies aloud on device. Uses the downloaded neural voice (<see cref="VoiceCatalog.Default"/>,
    /// Piper through sherpa-onnx in <c>com.xraiedge.litertlm.NeuralSpeechBridge</c>) when present, and the system
    /// TextToSpeech engine (<c>SpeechBridge</c>) otherwise; on Quest the system engine is disabled, so the voice is
    /// needed there. Streamed replies are spoken sentence by sentence as they arrive. The on/off choice is shared by
    /// all experiences and remembered between sessions.
    /// </summary>
    public class SpeechEngine : MonoBehaviour
    {
        const string k_BridgeClass = "com.xraiedge.litertlm.SpeechBridge";
        const string k_NeuralBridgeClass = "com.xraiedge.litertlm.NeuralSpeechBridge";
        const string k_EnabledKey = "AIEdge.SpeakReplies";

        [SerializeField, Tooltip("Speech language as a BCP 47 tag. Replies are usually English.")]
        string m_Language = "en-US";

        [SerializeField, Tooltip("Speech rate: 1 is normal.")]
        float m_Rate = 1.05f;

        [SerializeField, Tooltip("CPU threads for the neural voice. Low, so rendering and the LLM keep their cores.")]
        int m_NeuralThreads = 2;

        /// <summary>The engine in the scene, or null.</summary>
        public static SpeechEngine Instance { get; private set; }

        /// <summary>Raised when replies are switched on or off.</summary>
        public event Action<bool> EnabledChanged;

        /// <summary>Raised when an utterance starts playing.</summary>
        public event Action UtteranceStarted;

        /// <summary>Downloads the neural voice.</summary>
        public VoiceDownloader Downloader { get; } = new VoiceDownloader();

        /// <summary>True when the neural voice is in use (rather than the system engine).</summary>
        public bool IsNeural { get; private set; }

        /// <summary>True once the speech engine is bound and can speak.</summary>
        public bool IsReady { get; private set; }

        /// <summary>Engine details reported at start (JSON), or the error.</summary>
        public string Info { get; private set; } = "";

        /// <summary>True while audio is playing or queued.</summary>
        public bool IsSpeaking => m_Queued > 0;

        /// <summary>Seconds from a reply's start (<see cref="BeginReply"/>) to its first audio, or -1.</summary>
        public float LastReplyAudioLatency { get; private set; } = -1f;

        /// <summary>Utterances queued since start.</summary>
        public int UtteranceCount { get; private set; }

        /// <summary>Whether replies are read aloud.</summary>
        public bool Enabled
        {
            get => PlayerPrefs.GetInt(k_EnabledKey, 1) == 1;
            set
            {
                if (value == Enabled)
                    return;
                PlayerPrefs.SetInt(k_EnabledKey, value ? 1 : 0);
                PlayerPrefs.Save();
                if (!value)
                    Stop();
                EnabledChanged?.Invoke(value);
            }
        }

        readonly ConcurrentQueue<Action> m_MainThreadQueue = new ConcurrentQueue<Action>();
        AndroidJavaObject m_Bridge;
        SpeechText m_Reply;
        int m_NextId;
        int m_Queued;
        float m_ReplyStarted = -1f;
        int m_Generation;

        void Awake()
        {
            Instance = this;
        }

        void Start() => SelectEngine();

        /// <summary>
        /// (Re)creates the engine: the neural voice if downloaded, else the system engine. Call after the voice is
        /// downloaded or deleted.
        /// </summary>
        public void SelectEngine()
        {
            ShutdownBridge();
            IsReady = false;
            m_Generation++;
            var voiceDir = VoiceStore.GetPath(VoiceCatalog.Default);
            IsNeural = VoiceStore.GetState(VoiceCatalog.Default) == ModelState.Downloaded;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (IsNeural)
            {
                Info = "Loading neural voice…";
                m_Bridge = new AndroidJavaObject(k_NeuralBridgeClass, voiceDir, m_NeuralThreads, new ListenerProxy(this, m_Generation));
            }
            else
            {
                Info = "Starting system speech engine…";
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                m_Bridge = new AndroidJavaObject(k_BridgeClass, activity, new ListenerProxy(this, m_Generation));
            }
#else
            Info = "Speech runs on Android devices only.";
#endif
        }

        /// <summary>Stops and releases the current engine (for example before deleting the voice files).</summary>
        public void ShutdownEngine()
        {
            ShutdownBridge();
            IsReady = false;
            m_Generation++;
        }

        void ShutdownBridge()
        {
            m_Reply = null;
            m_Queued = 0;
            if (m_Bridge == null)
                return;
            m_Bridge.Call("shutdown");
            m_Bridge.Dispose();
            m_Bridge = null;
        }

        void Update()
        {
            while (m_MainThreadQueue.TryDequeue(out var action))
                action();
        }

        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
            ShutdownBridge();
        }

        /// <summary>Starts reading a new streamed reply, cutting off anything still being spoken.</summary>
        public void BeginReply()
        {
            Stop();
            m_Reply = new SpeechText();
            m_ReplyStarted = Time.realtimeSinceStartup;
            LastReplyAudioLatency = -1f;
        }

        /// <summary>Feeds streamed reply text; complete sentences are spoken at once.</summary>
        public void AppendReply(string text)
        {
            if (m_Reply == null)
                return;
            foreach (var sentence in m_Reply.Append(text))
                Speak(sentence);
        }

        /// <summary>Speaks the rest of the reply.</summary>
        public void EndReply()
        {
            if (m_Reply == null)
                return;
            foreach (var sentence in m_Reply.Flush())
                Speak(sentence);
            m_Reply = null;
        }

        /// <summary>Queues plain text to speak (or replaces the queue when <paramref name="interrupt"/>).</summary>
        public void Speak(string text, bool interrupt = false)
        {
            if (!Enabled || !IsReady || m_Bridge == null || string.IsNullOrWhiteSpace(text))
                return;

            var id = (m_NextId++).ToString();
            if (m_Bridge.Call<bool>("speak", text, id, interrupt))
            {
                m_Queued = interrupt ? 1 : m_Queued + 1;
                UtteranceCount++;
            }
        }

        /// <summary>Stops speaking and drops the reply in progress.</summary>
        public void Stop()
        {
            m_Reply = null;
            m_Queued = 0;
            m_Bridge?.Call("stop");
        }

        void Post(Action action) => m_MainThreadQueue.Enqueue(action);

        void OnReady(string infoJson)
        {
            var language = m_Bridge.Call<int>("setLanguage", m_Language);
            m_Bridge.Call("setRate", m_Rate);
            Info = $"{infoJson} setLanguage({m_Language})={language}";
            if (language < 0)
            {
                // The system engine bound but has no usable voice (Quest's is disabled): wait for the neural voice.
                Info = $"System speech engine has no voice ({language}). Download the neural voice in the launcher. {infoJson}";
                Debug.LogWarning($"[AIEdge] {Info}");
                return;
            }

            IsReady = true;
            Debug.Log($"[AIEdge] Speech ready: {Info}");
        }

        /// <summary>Receives bridge callbacks on Android threads and forwards them to the main thread.</summary>
        class ListenerProxy : AndroidJavaProxy
        {
            readonly SpeechEngine m_Engine;
            readonly int m_Generation;

            public ListenerProxy(SpeechEngine engine, int generation) : base(k_BridgeClass + "$Listener")
            {
                m_Engine = engine;
                m_Generation = generation;
            }

            // Callbacks from an engine replaced by SelectEngine are dropped.
            bool IsCurrent => m_Generation == m_Engine.m_Generation;

            public void onReady(string infoJson) => m_Engine.Post(() =>
            {
                if (IsCurrent)
                    m_Engine.OnReady(infoJson);
            });

            public void onError(string message) => m_Engine.Post(() =>
            {
                if (!IsCurrent)
                    return;
                m_Engine.Info = message;
                Debug.LogWarning($"[AIEdge] Speech unavailable: {message}");
            });

            public void onStart(string utteranceId) => m_Engine.Post(() =>
            {
                if (m_Engine.m_ReplyStarted >= 0f && m_Engine.LastReplyAudioLatency < 0f)
                    m_Engine.LastReplyAudioLatency = Time.realtimeSinceStartup - m_Engine.m_ReplyStarted;
                m_Engine.UtteranceStarted?.Invoke();
            });

            public void onDone(string utteranceId) => m_Engine.Post(() => m_Engine.m_Queued = Mathf.Max(0, m_Engine.m_Queued - 1));
        }
    }
}
