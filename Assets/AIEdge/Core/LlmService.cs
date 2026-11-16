using System;
using System.Collections.Generic;
using UnityEngine;

namespace AIEdge
{
    /// <summary>
    /// Thrown when the LiteRT-LM bridge reports a failure.
    /// </summary>
    public class LlmException : Exception
    {
        public LlmException(string message) : base(message) { }
    }

    /// <summary>
    /// App-wide entry point for on-device models: owns the <see cref="LlmEngine"/> and the
    /// <see cref="ModelDownloader"/>, and keeps at most one model in memory (Quest 3 has room for one
    /// E2B-class model next to the MR scene). Experiences load what they need through <see cref="LoadAsync"/>.
    /// </summary>
    public class LlmService : MonoBehaviour
    {
        [SerializeField, Tooltip("Engine that runs the models. Added automatically if empty.")]
        LlmEngine m_Engine;

        LlmLoadInfo m_LoadInfo;
        bool m_Loading;

        /// <summary>The service in the scene.</summary>
        public static LlmService Instance { get; private set; }

        /// <summary>The engine; subscribe to its events to stream responses.</summary>
        public LlmEngine Engine => m_Engine;

        /// <summary>Downloads and resumes model files.</summary>
        public ModelDownloader Downloader { get; } = new ModelDownloader();

        /// <summary>The model in memory, or null.</summary>
        public ModelInfo LoadedModel { get; private set; }

        /// <summary>True if the loaded model has its image encoder enabled.</summary>
        public bool LoadedWithVision { get; private set; }

        /// <summary>Load details of the model in memory (backend, load time, memory).</summary>
        public LlmLoadInfo LoadInfo => m_LoadInfo;

        /// <summary>True while a model is loading.</summary>
        public bool IsLoading => m_Loading;

        /// <summary>Raised on the main thread when the loaded model changes (null after unload).</summary>
        public event Action<ModelInfo> LoadedModelChanged;

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning($"Duplicate {nameof(LlmService)} on {name}; disabling it.", this);
                enabled = false;
                return;
            }

            Instance = this;
            if (m_Engine == null)
                m_Engine = gameObject.AddComponent<LlmEngine>();
        }

        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        /// <summary>
        /// Loads a downloaded model, replacing the one in memory. Returns at once if it's already loaded (with its image
        /// encoder, if asked for).
        /// </summary>
        /// <remarks>
        /// A model that reads images loads with its image encoder unless told otherwise, so chat and the camera
        /// experiences share one load: loading Gemma on the GPU stops the app rendering for over a second, inside
        /// LiteRT-LM.
        /// </remarks>
        /// <param name="model">Catalog model.</param>
        /// <param name="withVision">
        /// Enable the image encoder (only for models that support images), or null for whenever the model supports them.
        /// </param>
        /// <exception cref="InvalidOperationException">Model not downloaded, or another load in progress.</exception>
        /// <exception cref="LlmException">The engine failed to load the model.</exception>
        public async Awaitable<LlmLoadInfo> LoadAsync(ModelInfo model, bool? withVision = null)
        {
            var vision = (withVision ?? true) && model.supportsImage && model.visionBackend != LlmBackend.None;
            if (LoadedModel == model && (LoadedWithVision || !vision) && m_Engine.IsLoaded)
                return m_LoadInfo;
            if (m_Loading)
                throw new InvalidOperationException("Another model is loading.");
            if (ModelStore.GetState(model) != ModelState.Downloaded)
                throw new InvalidOperationException($"{model.displayName} is not downloaded.");

            m_Loading = true;
            var completion = new AwaitableCompletionSource<LlmLoadInfo>();
            void OnLoaded(LlmLoadInfo info) => completion.TrySetResult(info);
            void OnFailed(string message) => completion.TrySetException(new LlmException(message));
            m_Engine.Loaded += OnLoaded;
            m_Engine.Failed += OnFailed;

            try
            {
                if (LoadedModel != null)
                    SetLoaded(null, false, null);

                LlmEngine.SetVisualTokenBudget(vision ? model.visualTokenBudget : 0);
                m_Engine.Load(ModelStore.GetPath(model), model.backend, vision ? model.visionBackend : LlmBackend.None, model.maxTokens);
                var info = await completion.Awaitable;
                SetLoaded(model, vision, info);
                return info;
            }
            finally
            {
                m_Engine.Loaded -= OnLoaded;
                m_Engine.Failed -= OnFailed;
                m_Loading = false;
            }
        }

        /// <summary>Frees the loaded model.</summary>
        public void Unload()
        {
            if (LoadedModel == null)
                return;

            m_Engine.Unload();
            SetLoaded(null, false, null);
        }

        /// <summary>Starts a new conversation on the loaded model with its default sampling.</summary>
        /// <param name="systemPrompt">System instruction, or empty.</param>
        /// <param name="toolsJson">JSON array of OpenAPI tool descriptions, or empty.</param>
        /// <param name="constrainedDecoding">Constrain tool calls to the schema.</param>
        /// <param name="history">Earlier turns to resume a chat, or null to start fresh.</param>
        public void NewConversation(string systemPrompt = "", string toolsJson = "", bool constrainedDecoding = false,
            IReadOnlyList<ChatTurn> history = null)
        {
            if (LoadedModel == null)
                throw new InvalidOperationException("No model loaded.");

            m_Engine.NewConversation(systemPrompt, LoadedModel.sampler, toolsJson, constrainedDecoding, history);
        }

        void SetLoaded(ModelInfo model, bool withVision, LlmLoadInfo info)
        {
            LoadedModel = model;
            LoadedWithVision = withVision;
            m_LoadInfo = info;
            LoadedModelChanged?.Invoke(model);
        }
    }
}
