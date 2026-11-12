using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace AIEdge
{
    /// <summary>What a transcription produced.</summary>
    public class SpeechInputResult
    {
        /// <summary>The words heard, or empty when nothing was said.</summary>
        public string Text = "";

        /// <summary>Timings and audio length (JSON) from the recognizer.</summary>
        public string Info = "";

        /// <summary>Why it failed, or null.</summary>
        public string Error;
    }

    /// <summary>
    /// On-device speech to text, push to talk: <see cref="StartListeningAsync"/> records the microphone and
    /// <see cref="StopAndTranscribeAsync"/> turns it into text in the app language (<see cref="AppLanguage"/>). Runs
    /// Whisper base through sherpa-onnx (<c>com.xraiedge.litertlm.RecognizerBridge</c>) once
    /// <see cref="VoiceCatalog.Whisper"/> is downloaded from the launcher. The audio never leaves the headset.
    /// </summary>
    public class SpeechInput : MonoBehaviour
    {
        const string k_BridgeClass = "com.xraiedge.litertlm.RecognizerBridge";

        /// <summary>Where speech input stands.</summary>
        public enum Status
        {
            /// <summary>Not on an Android device.</summary>
            Unavailable,
            /// <summary>The Whisper model isn't downloaded.</summary>
            NotDownloaded,
            Loading,
            Ready,
            Recording,
            Transcribing,
            /// <summary>The model failed to load; see <see cref="Info"/>.</summary>
            Failed,
        }

        [SerializeField, Tooltip("CPU threads for transcription. Nothing else heavy runs on the CPU while it decodes.")]
        int m_Threads = 4;

        /// <summary>The speech input in the scene, or null.</summary>
        public static SpeechInput Instance { get; private set; }

        /// <summary>Downloads the Whisper model.</summary>
        public VoiceDownloader Downloader { get; } = new VoiceDownloader();

        /// <summary>The model this uses.</summary>
        public static VoiceInfo Model => VoiceCatalog.Whisper;

        /// <summary>Current status.</summary>
        public Status State { get; private set; } = Status.Unavailable;

        /// <summary>True when a recording can start.</summary>
        public bool IsReady => State == Status.Ready;

        /// <summary>True while the microphone is recording.</summary>
        public bool IsRecording => State == Status.Recording;

        /// <summary>Recognizer details (JSON) once loaded, or the load error.</summary>
        public string Info { get; private set; } = "";

        /// <summary>Microphone loudness while recording, 0 to 1.</summary>
        public float Level => State == Status.Recording && m_Bridge != null ? m_Bridge.Call<float>("getLevel") : 0f;

        /// <summary>Seconds recorded so far.</summary>
        public float RecordedSeconds => State == Status.Recording && m_Bridge != null ? m_Bridge.Call<float>("getRecordedSeconds") : 0f;

        /// <summary>Raised when <see cref="State"/> changes.</summary>
        public event Action StateChanged;

        readonly ConcurrentQueue<Action> m_MainThreadQueue = new ConcurrentQueue<Action>();
        readonly Dictionary<string, AwaitableCompletionSource<SpeechInputResult>> m_Pending =
            new Dictionary<string, AwaitableCompletionSource<SpeechInputResult>>();
        AndroidJavaObject m_Bridge;
        int m_Generation;
        int m_NextId;
        // The recording hit its length limit; it can still be transcribed.
        bool m_StoppedByItself;

        void Awake()
        {
            Instance = this;
        }

        void Start() => Reload();

        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
            Shutdown();
        }

        void Update()
        {
            while (m_MainThreadQueue.TryDequeue(out var action))
                action();

            // The bridge stops by itself at its length limit (or if the microphone fails).
            if (State == Status.Recording && m_Bridge != null && !m_Bridge.Call<bool>("isRecording"))
            {
                m_StoppedByItself = true;
                SetState(Status.Ready);
            }
        }

        /// <summary>(Re)loads the recognizer when the model is downloaded. Call after downloading or deleting it.</summary>
        public void Reload()
        {
            Shutdown();
#if UNITY_ANDROID && !UNITY_EDITOR
            if (VoiceStore.GetState(Model) != ModelState.Downloaded)
            {
                SetState(Status.NotDownloaded);
                return;
            }

            Info = "loading…";
            SetState(Status.Loading);
            m_Bridge = new AndroidJavaObject(k_BridgeClass, VoiceStore.GetPath(Model), m_Threads, new ListenerProxy(this, m_Generation));
#else
            Info = "Speech input runs on Android devices only.";
            SetState(VoiceStore.GetState(Model) == ModelState.Downloaded ? Status.Unavailable : Status.NotDownloaded);
#endif
        }

        /// <summary>Stops recording and releases the recognizer (for example before deleting its files).</summary>
        public void Shutdown()
        {
            m_Generation++;
            if (m_Bridge != null)
            {
                m_Bridge.Call("shutdown");
                m_Bridge.Dispose();
                m_Bridge = null;
            }

            foreach (var pending in m_Pending.Values)
                pending.TrySetResult(new SpeechInputResult { Error = "Speech input was stopped." });
            m_Pending.Clear();
            if (State != Status.Unavailable && State != Status.NotDownloaded)
                SetState(Status.NotDownloaded);
        }

        /// <summary>
        /// Starts recording, asking for the microphone permission the first time. Stops any speech being read aloud,
        /// so the reply isn't recorded. Returns false when not ready, the permission is denied or the microphone fails.
        /// </summary>
        public async Awaitable<bool> StartListeningAsync()
        {
            if (State != Status.Ready)
                return false;
            if (!await EnsurePermissionAsync())
            {
                Info = "Microphone permission denied.";
                return false;
            }

            if (State != Status.Ready || m_Bridge == null)
                return false;
            SpeechEngine.Instance?.Stop();
            if (!m_Bridge.Call<bool>("startRecording"))
            {
                Info = "The microphone couldn't be opened.";
                return false;
            }

            m_StoppedByItself = false;
            SetState(Status.Recording);
            return true;
        }

        /// <summary>Stops recording and transcribes it in <paramref name="language"/> (the app language when null).</summary>
        public Awaitable<SpeechInputResult> StopAndTranscribeAsync(string language = null)
        {
            if (m_Bridge == null || !(State == Status.Recording || State == Status.Ready && m_StoppedByItself))
                return Completed(new SpeechInputResult { Error = "Not recording." });
            m_StoppedByItself = false;
            var (id, source) = NewJob();
            SetState(Status.Transcribing);
            m_Bridge.Call("stopAndTranscribe", id, language ?? AppLanguage.Current);
            return source.Awaitable;
        }

        /// <summary>Stops recording and drops it.</summary>
        public void Cancel()
        {
            if (State != Status.Recording || m_Bridge == null)
                return;
            m_Bridge.Call("cancel");
            m_StoppedByItself = false;
            SetState(Status.Ready);
        }

        /// <summary>Transcribes audio given directly (mono, -1 to 1), for tests.</summary>
        public Awaitable<SpeechInputResult> TranscribeAsync(float[] samples, int sampleRate, string language)
        {
            if (State != Status.Ready || m_Bridge == null)
                return Completed(new SpeechInputResult { Error = $"Speech input isn't ready ({State})." });
            var (id, source) = NewJob();
            SetState(Status.Transcribing);
            m_Bridge.Call("transcribe", id, samples, sampleRate, language);
            return source.Awaitable;
        }

        (string, AwaitableCompletionSource<SpeechInputResult>) NewJob()
        {
            var id = (m_NextId++).ToString();
            var source = new AwaitableCompletionSource<SpeechInputResult>();
            m_Pending[id] = source;
            return (id, source);
        }

        static Awaitable<SpeechInputResult> Completed(SpeechInputResult result)
        {
            var source = new AwaitableCompletionSource<SpeechInputResult>();
            source.SetResult(result);
            return source.Awaitable;
        }

        static async Awaitable<bool> EnsurePermissionAsync()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (Permission.HasUserAuthorizedPermission(Permission.Microphone))
                return true;
            var answer = new AwaitableCompletionSource<bool>();
            var callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += _ => answer.TrySetResult(true);
            callbacks.PermissionDenied += _ => answer.TrySetResult(false);
            Permission.RequestUserPermission(Permission.Microphone, callbacks);
            return await answer.Awaitable;
#else
            await Awaitable.NextFrameAsync();
            return false;
#endif
        }

        void SetState(Status state)
        {
            if (State == state)
                return;
            State = state;
            StateChanged?.Invoke();
        }

        void Post(Action action) => m_MainThreadQueue.Enqueue(action);

        void Finish(string id, SpeechInputResult result)
        {
            if (State == Status.Transcribing)
                SetState(Status.Ready);
            if (!m_Pending.Remove(id, out var source))
                return;
            if (result.Error == null)
                Debug.Log($"[AIEdge] Heard: \"{result.Text}\" {result.Info}");
            source.TrySetResult(result);
        }

        /// <summary>Receives the bridge's callbacks on its worker thread and forwards them to the main thread.</summary>
        class ListenerProxy : AndroidJavaProxy
        {
            readonly SpeechInput m_Owner;
            readonly int m_Generation;

            public ListenerProxy(SpeechInput owner, int generation) : base(k_BridgeClass + "$Listener")
            {
                m_Owner = owner;
                m_Generation = generation;
            }

            bool IsCurrent => m_Generation == m_Owner.m_Generation;

            public void onReady(string infoJson) => m_Owner.Post(() =>
            {
                if (!IsCurrent)
                    return;
                m_Owner.Info = infoJson;
                Debug.Log($"[AIEdge] Speech input ready: {infoJson}");
                m_Owner.SetState(Status.Ready);
            });

            public void onResult(string id, string text, string infoJson) => m_Owner.Post(() =>
            {
                if (IsCurrent)
                    m_Owner.Finish(id, new SpeechInputResult { Text = text ?? "", Info = infoJson });
            });

            public void onError(string id, string message) => m_Owner.Post(() =>
            {
                if (!IsCurrent)
                    return;
                Debug.LogWarning($"[AIEdge] Speech input: {message}");
                if (string.IsNullOrEmpty(id))
                {
                    m_Owner.Info = message;
                    m_Owner.SetState(Status.Failed);
                    return;
                }

                m_Owner.Finish(id, new SpeechInputResult { Error = message });
            });
        }
    }
}
