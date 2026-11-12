using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace AIEdge
{
    /// <summary>
    /// Reads model replies aloud on device, in English or Italian. Every downloaded voice in <see cref="VoiceCatalog"/>
    /// (Piper through sherpa-onnx in <c>com.xraiedge.litertlm.NeuralSpeechBridge</c>) is loaded, and each reply is
    /// read by the voice of its language (<see cref="SpeechLanguage"/>). With no voice downloaded the system
    /// TextToSpeech engine (<c>SpeechBridge</c>) is used; on Quest it is disabled, so a voice is needed there.
    /// Streamed replies are spoken sentence by sentence as they arrive. The on/off choice is shared by all experiences
    /// and remembered between sessions.
    /// </summary>
    public class SpeechEngine : MonoBehaviour
    {
        const string k_BridgeClass = "com.xraiedge.litertlm.SpeechBridge";
        const string k_NeuralBridgeClass = "com.xraiedge.litertlm.NeuralSpeechBridge";
        const string k_EnabledKey = "AIEdge.SpeakReplies";

        [SerializeField, Tooltip("System engine language as a BCP 47 tag, used only when no neural voice is downloaded.")]
        string m_Language = "en-US";

        [SerializeField, Tooltip("Speech rate: 1 is normal.")]
        float m_Rate = 1.05f;

        [SerializeField, Tooltip("CPU threads per neural voice. Low, so rendering and the LLM keep their cores.")]
        int m_NeuralThreads = 2;

        /// <summary>The engine in the scene, or null.</summary>
        public static SpeechEngine Instance { get; private set; }

        /// <summary>Raised when replies are switched on or off.</summary>
        public event Action<bool> EnabledChanged;

        /// <summary>Raised when an utterance starts playing.</summary>
        public event Action UtteranceStarted;

        /// <summary>Downloads the neural voices.</summary>
        public VoiceDownloader Downloader { get; } = new VoiceDownloader();

        /// <summary>True when neural voices are in use (rather than the system engine).</summary>
        public bool IsNeural => m_Engines.Any(e => e.Voice != null);

        /// <summary>True once at least one engine can speak.</summary>
        public bool IsReady => m_Engines.Any(e => e.Ready);

        /// <summary>True when the voice for this language is loaded and can speak.</summary>
        public bool IsVoiceReady(VoiceInfo voice) => m_Engines.Any(e => e.Ready && e.Voice == voice);

        /// <summary>Engine details reported at start (JSON), or errors, one line per engine.</summary>
        public string Info => m_Engines.Count == 0 ? m_Info : string.Join("\n", m_Engines.Select(e => e.Info));

        /// <summary>True while audio is playing or queued.</summary>
        public bool IsSpeaking => m_Queued > 0;

        /// <summary>Seconds from a reply's start (<see cref="BeginReply"/>) to its first audio, or -1.</summary>
        public float LastReplyAudioLatency { get; private set; } = -1f;

        /// <summary>Utterances queued since start.</summary>
        public int UtteranceCount { get; private set; }

        /// <summary>Language of the last utterance queued ("en", "it"), or null for the system engine.</summary>
        public string LastLanguage { get; private set; }

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

        /// <summary>One loaded speech engine: a neural voice, or the system engine (<see cref="Voice"/> null).</summary>
        class Engine
        {
            public VoiceInfo Voice;
            public AndroidJavaObject Bridge;
            public bool Ready;
            public string Info = "";
            public string Name => Voice != null ? Voice.language : "system";
        }

        readonly ConcurrentQueue<Action> m_MainThreadQueue = new ConcurrentQueue<Action>();
        readonly List<Engine> m_Engines = new List<Engine>();
        string m_Info = "";
        SpeechText m_Reply;
        string m_ReplyLanguage;
        string m_RecentLanguage = SpeechLanguage.English;
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
        /// (Re)creates the engines: every downloaded voice, or the system engine when there is none. Call after a
        /// voice is downloaded or deleted.
        /// </summary>
        public void SelectEngine()
        {
            ShutdownEngine();
#if UNITY_ANDROID && !UNITY_EDITOR
            foreach (var voice in VoiceCatalog.All)
            {
                if (VoiceStore.GetState(voice) != ModelState.Downloaded)
                    continue;
                var engine = new Engine { Voice = voice, Info = $"{voice.language}: loading…" };
                engine.Bridge = new AndroidJavaObject(k_NeuralBridgeClass, VoiceStore.GetPath(voice), m_NeuralThreads,
                    new ListenerProxy(this, engine, m_Generation));
                m_Engines.Add(engine);
            }

            if (m_Engines.Count == 0)
            {
                var engine = new Engine { Info = "system: starting…" };
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                engine.Bridge = new AndroidJavaObject(k_BridgeClass, activity, new ListenerProxy(this, engine, m_Generation));
                m_Engines.Add(engine);
            }
#else
            m_Info = "Speech runs on Android devices only.";
#endif
        }

        /// <summary>Stops and releases every engine (for example before deleting voice files).</summary>
        public void ShutdownEngine()
        {
            m_Generation++;
            m_Reply = null;
            m_Queued = 0;
            foreach (var engine in m_Engines)
            {
                engine.Bridge.Call("shutdown");
                engine.Bridge.Dispose();
            }

            m_Engines.Clear();
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
            ShutdownEngine();
        }

        /// <summary>
        /// Starts reading a new streamed reply, cutting off anything still being spoken. With
        /// <paramref name="language"/> ("en", "it") the whole reply is read by that voice; otherwise the voice follows
        /// the reply's detected language.
        /// </summary>
        public void BeginReply(string language = null)
        {
            Stop();
            m_Reply = new SpeechText();
            m_ReplyLanguage = language;
            m_ReplyStarted = Time.realtimeSinceStartup;
            LastReplyAudioLatency = -1f;
        }

        /// <summary>Feeds streamed reply text; complete sentences are spoken at once.</summary>
        public void AppendReply(string text)
        {
            if (m_Reply == null)
                return;
            foreach (var sentence in m_Reply.Append(text))
                SpeakReplySentence(sentence);
        }

        /// <summary>Speaks the rest of the reply.</summary>
        public void EndReply()
        {
            if (m_Reply == null)
                return;
            foreach (var sentence in m_Reply.Flush())
                SpeakReplySentence(sentence);
            m_Reply = null;
        }

        // A reply keeps one voice: its language is settled by the first sentence that gives a clear signal, so a
        // stray English word in an Italian reply doesn't switch voices mid-answer.
        void SpeakReplySentence(string sentence)
        {
            m_ReplyLanguage ??= SpeechLanguage.Detect(sentence);
            Speak(sentence, false, m_ReplyLanguage);
        }

        /// <summary>
        /// Queues plain text to speak (or replaces the queue when <paramref name="interrupt"/>), with the voice of
        /// <paramref name="language"/> ("en", "it"), or of the text's detected language when null.
        /// </summary>
        public void Speak(string text, bool interrupt = false, string language = null)
        {
            if (!Enabled || string.IsNullOrWhiteSpace(text))
                return;

            language ??= SpeechLanguage.Detect(text) ?? m_RecentLanguage;
            var engine = Choose(language);
            if (engine == null)
                return;

            if (interrupt)
                Stop();
            var id = (m_NextId++).ToString();
            if (engine.Bridge.Call<bool>("speak", text, id, false))
            {
                m_Queued++;
                UtteranceCount++;
                LastLanguage = engine.Voice?.language;
                if (engine.Voice != null)
                    m_RecentLanguage = engine.Voice.language;
            }
        }

        // The voice for the language if loaded, else any ready voice (better than silence), else the system engine.
        Engine Choose(string language) =>
            m_Engines.FirstOrDefault(e => e.Ready && e.Voice != null && e.Voice.language == language) ??
            m_Engines.FirstOrDefault(e => e.Ready);

        /// <summary>
        /// Synthesizes <paramref name="text"/> with the voice of <paramref name="language"/> without playing it (for
        /// tests). Returns null when that voice isn't loaded.
        /// </summary>
        public float[] Synthesize(string text, string language, out int sampleRate)
        {
            sampleRate = 0;
            var engine = m_Engines.FirstOrDefault(e => e.Ready && e.Voice != null && e.Voice.language == language);
            if (engine == null)
                return null;
            sampleRate = engine.Bridge.Call<int>("getSampleRate");
            return engine.Bridge.Call<float[]>("synthesize", text);
        }

        /// <summary>Stops speaking and drops the reply in progress.</summary>
        public void Stop()
        {
            m_Reply = null;
            m_Queued = 0;
            foreach (var engine in m_Engines)
                engine.Bridge.Call("stop");
        }

        void Post(Action action) => m_MainThreadQueue.Enqueue(action);

        void OnReady(Engine engine, string infoJson)
        {
            if (engine.Voice != null)
            {
                engine.Bridge.Call("setRate", m_Rate);
                engine.Info = $"{engine.Name}: {infoJson}";
                engine.Ready = true;
                Debug.Log($"[AIEdge] Speech ready: {engine.Info}");
                return;
            }

            var language = engine.Bridge.Call<int>("setLanguage", m_Language);
            engine.Bridge.Call("setRate", m_Rate);
            if (language < 0)
            {
                // The system engine bound but has no usable voice (Quest's is disabled): wait for a neural voice.
                engine.Info = $"System speech engine has no voice ({language}). Download a voice in the launcher. {infoJson}";
                Debug.LogWarning($"[AIEdge] {engine.Info}");
                return;
            }

            engine.Info = $"system: {infoJson} setLanguage({m_Language})={language}";
            engine.Ready = true;
            Debug.Log($"[AIEdge] Speech ready: {engine.Info}");
        }

        /// <summary>Receives one engine's callbacks on Android threads and forwards them to the main thread.</summary>
        class ListenerProxy : AndroidJavaProxy
        {
            readonly SpeechEngine m_Owner;
            readonly Engine m_Engine;
            readonly int m_Generation;

            public ListenerProxy(SpeechEngine owner, Engine engine, int generation) : base(k_BridgeClass + "$Listener")
            {
                m_Owner = owner;
                m_Engine = engine;
                m_Generation = generation;
            }

            // Callbacks from engines replaced by SelectEngine are dropped.
            bool IsCurrent => m_Generation == m_Owner.m_Generation;

            public void onReady(string infoJson) => m_Owner.Post(() =>
            {
                if (IsCurrent)
                    m_Owner.OnReady(m_Engine, infoJson);
            });

            public void onError(string message) => m_Owner.Post(() =>
            {
                if (!IsCurrent)
                    return;
                m_Engine.Info = $"{m_Engine.Name}: {message}";
                Debug.LogWarning($"[AIEdge] Speech unavailable: {m_Engine.Info}");
            });

            public void onStart(string utteranceId) => m_Owner.Post(() =>
            {
                if (m_Owner.m_ReplyStarted >= 0f && m_Owner.LastReplyAudioLatency < 0f)
                    m_Owner.LastReplyAudioLatency = Time.realtimeSinceStartup - m_Owner.m_ReplyStarted;
                m_Owner.UtteranceStarted?.Invoke();
            });

            public void onDone(string utteranceId) => m_Owner.Post(() =>
            {
                if (IsCurrent)
                    m_Owner.m_Queued = Mathf.Max(0, m_Owner.m_Queued - 1);
            });
        }
    }
}
