using AIEdge.AskImage;
using AIEdge.Chat;
using AIEdge.Spike;
using AIEdge.TinyGarden;
using AIEdge.UI;
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

        [SerializeField, Tooltip("The template's AR feature hub. Uses the one in the scene if empty.")]
        ARFeatureController m_ArFeatures;

        /// <summary>The AI Chat experience.</summary>
        public ChatPanel Chat => m_Chat;

        /// <summary>The Point &amp; Ask experience.</summary>
        public AskImagePanel AskImage => m_AskImage;

        /// <summary>The Tabletop Garden experience.</summary>
        public GardenPanel Garden => m_Garden;

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
        }

        void OnEnable()
        {
            m_Launcher.BenchmarkRequested += OnBenchmarkRequested;
            m_Benchmark.Closed += OnBenchmarkClosed;
            m_Chat.Closed += OnExperienceClosed;
            m_AskImage.Closed += OnExperienceClosed;
            m_Garden.Closed += OnExperienceClosed;
        }

        void OnDisable()
        {
            m_Launcher.BenchmarkRequested -= OnBenchmarkRequested;
            m_Benchmark.Closed -= OnBenchmarkClosed;
            m_Chat.Closed -= OnExperienceClosed;
            m_AskImage.Closed -= OnExperienceClosed;
            m_Garden.Closed -= OnExperienceClosed;
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
