using AIEdge.AskImage;
using AIEdge.Chat;
using AIEdge.Spike;
using AIEdge.TinyGarden;
using AIEdge.UI;
using AIEdge.WhatsThat;
using UnityEngine;
using UnityEngine.XR.Templates.MR;

namespace AIEdge
{
    /// <summary>
    /// App root: connects the launcher to the experiences and tools it opens.
    /// </summary>
    public class AIEdgeApp : MonoBehaviour
    {
        [SerializeField, Tooltip("Home panel with experiences and the model manager.")]
        LauncherPanel m_Launcher;

        [SerializeField, Tooltip("Benchmark opened from the launcher.")]
        LlmSpikeController m_Benchmark;

        [SerializeField, Tooltip("Spatial AI Chat experience.")]
        ChatPanel m_Chat;

        [SerializeField, Tooltip("Point & Ask (Ask Image) experience.")]
        AskImagePanel m_AskImage;

        [SerializeField, Tooltip("Tabletop Garden (Tiny Garden) experience.")]
        GardenPanel m_Garden;

        [SerializeField, Tooltip("What's That? (point at an object) experience. Created at start if empty.")]
        WhatsThatPanel m_WhatsThat;

        [SerializeField, Tooltip("Editor for the user's standing instructions. Created at start if empty.")]
        InstructionsPanel m_Instructions;

        [SerializeField, Tooltip("The template's AR feature hub. Uses the one in the scene if empty.")]
        ARFeatureController m_ArFeatures;

        /// <summary>The AI Chat experience.</summary>
        public ChatPanel Chat => m_Chat;

        /// <summary>The Point &amp; Ask experience.</summary>
        public AskImagePanel AskImage => m_AskImage;

        /// <summary>The Tabletop Garden experience.</summary>
        public GardenPanel Garden => m_Garden;

        /// <summary>The What's That? experience.</summary>
        public WhatsThatPanel WhatsThat => m_WhatsThat;

        /// <summary>The instructions editor.</summary>
        public InstructionsPanel Instructions => m_Instructions;

        bool m_LauncherHiddenForInstructions;

        void Awake()
        {
            if (m_WhatsThat == null)
                m_WhatsThat = new GameObject("WhatsThat").AddComponent<WhatsThatPanel>();
            if (m_Instructions == null)
                m_Instructions = new GameObject("Instructions").AddComponent<InstructionsPanel>();
        }

        async void Start()
        {
            // Mixed reality: show the room. The template's onboarding (disabled here) normally turns passthrough on,
            // which also fades out its sky dome ("Environment").
            if (m_ArFeatures == null)
                m_ArFeatures = FindAnyObjectByType<ARFeatureController>();
            if (m_ArFeatures != null)
                m_ArFeatures.TogglePassthrough(true);

            // The launcher builds its tiles in its own Start.
            await Awaitable.NextFrameAsync();
            m_Launcher.SetExperience(0, "AI Chat", "Multi-turn chat with Gemma", OpenChat);
            m_Launcher.SetExperience(1, "Point & Ask", "Frame something, ask about it", OpenAskImage);
            m_Launcher.SetExperience(2, "Tabletop Garden", "Grow flowers by voice and hand", OpenGarden);
            m_Launcher.SetExperience(3, "What's That?", "Point at an object to name it", OpenWhatsThat);
        }

        void OnEnable()
        {
            m_Launcher.BenchmarkRequested += OnBenchmarkRequested;
            m_Benchmark.Closed += OnBenchmarkClosed;
            m_Chat.Closed += OnExperienceClosed;
            m_AskImage.Closed += OnExperienceClosed;
            m_Garden.Closed += OnExperienceClosed;
            m_WhatsThat.Closed += OnExperienceClosed;
            m_Launcher.InstructionsRequested += OnLauncherInstructions;
            m_WhatsThat.InstructionsRequested += OpenInstructions;
            m_Instructions.Closed += OnInstructionsClosed;
        }

        void OnDisable()
        {
            m_Launcher.BenchmarkRequested -= OnBenchmarkRequested;
            m_Benchmark.Closed -= OnBenchmarkClosed;
            m_Chat.Closed -= OnExperienceClosed;
            m_AskImage.Closed -= OnExperienceClosed;
            m_Garden.Closed -= OnExperienceClosed;
            m_WhatsThat.Closed -= OnExperienceClosed;
            m_Launcher.InstructionsRequested -= OnLauncherInstructions;
            m_WhatsThat.InstructionsRequested -= OpenInstructions;
            m_Instructions.Closed -= OnInstructionsClosed;
        }

        /// <summary>Hides the launcher and opens AI Chat.</summary>
        public void OpenChat()
        {
            m_Launcher.SetVisible(false);
            m_Chat.Open();
        }

        /// <summary>Hides the launcher and opens Point &amp; Ask.</summary>
        public void OpenAskImage()
        {
            m_Launcher.SetVisible(false);
            m_AskImage.Open();
        }

        /// <summary>Hides the launcher and opens Tabletop Garden.</summary>
        public void OpenGarden()
        {
            m_Launcher.SetVisible(false);
            m_Garden.Open();
        }

        /// <summary>Hides the launcher and opens What's That?.</summary>
        public void OpenWhatsThat()
        {
            m_Launcher.SetVisible(false);
            m_WhatsThat.Open();
        }

        /// <summary>Opens the instructions editor in front of the user (over the current experience).</summary>
        public void OpenInstructions() => m_Instructions.Open();

        void OnLauncherInstructions()
        {
            m_LauncherHiddenForInstructions = true;
            m_Launcher.SetVisible(false);
            OpenInstructions();
        }

        void OnInstructionsClosed()
        {
            if (!m_LauncherHiddenForInstructions)
                return;
            m_LauncherHiddenForInstructions = false;
            OnExperienceClosed();
        }

        void OnBenchmarkRequested()
        {
            m_Launcher.SetVisible(false);
            m_Benchmark.StartSpike("gpu");
        }

        void OnBenchmarkClosed() => OnExperienceClosed();

        void OnExperienceClosed()
        {
            m_Launcher.SetVisible(true);
            m_Launcher.Recenter();
        }
    }
}
