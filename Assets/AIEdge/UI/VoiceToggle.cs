using UnityEngine;
using UnityEngine.UI;

namespace AIEdge.UI
{
    /// <summary>
    /// A "Voice on / Voice off" button for reading replies aloud. All experiences share the setting through
    /// <see cref="SpeechEngine"/>, so every button's label follows a change made from any panel.
    /// </summary>
    public class VoiceToggle : MonoBehaviour
    {
        Button m_Button;

        /// <summary>Creates the button inside a panel.</summary>
        public static Button Create(Transform parent, float fontSize = 20)
        {
            var button = UiFactory.CreateButton(parent, "Voice", UiFactory.CardColor, Toggle, fontSize);
            var toggle = button.gameObject.AddComponent<VoiceToggle>();
            toggle.m_Button = button;
            toggle.Refresh(SpeechEngine.Instance != null && SpeechEngine.Instance.Enabled);
            return button;
        }

        static void Toggle()
        {
            var speech = SpeechEngine.Instance;
            if (speech != null)
                speech.Enabled = !speech.Enabled;
        }

        void OnEnable()
        {
            if (SpeechEngine.Instance == null)
                return;
            SpeechEngine.Instance.EnabledChanged += Refresh;
            if (m_Button != null)
                Refresh(SpeechEngine.Instance.Enabled);
        }

        void OnDisable()
        {
            if (SpeechEngine.Instance != null)
                SpeechEngine.Instance.EnabledChanged -= Refresh;
        }

        void Refresh(bool enabled)
        {
            UiFactory.SetLabel(m_Button, enabled ? "Voice on" : "Voice off");
            m_Button.image.color = enabled ? Color.Lerp(UiFactory.CardColor, UiFactory.AccentColor, 0.5f) : UiFactory.CardColor;
        }
    }
}
