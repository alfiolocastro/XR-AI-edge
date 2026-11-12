using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace AIEdge.UI
{
    /// <summary>
    /// Push-to-talk button for <see cref="SpeechInput"/>: press to start talking, press again to stop. The words heard,
    /// in the app language, go to the panel's callback. Shows how long it has been listening and the voice level.
    /// The mic gesture (<see cref="AIEdge.Interaction.MicGesture"/>) drives the button of the open panel the same way.
    /// </summary>
    public class MicButton : MonoBehaviour
    {
        // Buttons on open panels, most recently shown last.
        static readonly List<MicButton> s_Visible = new List<MicButton>();

        /// <summary>The Talk button of the most recently opened panel that has one, or null.</summary>
        public static MicButton Current => s_Visible.Count > 0 ? s_Visible[s_Visible.Count - 1] : null;

        /// <summary>True while this button is listening.</summary>
        public bool IsListening => m_Listening;

        /// <summary>True while this button is starting or transcribing.</summary>
        public bool IsBusy => m_Busy;

        Button m_Button;
        Action<string> m_OnText;
        Action<string> m_OnStatus;
        bool m_Listening;
        bool m_Busy;

        /// <summary>Creates the button inside a panel.</summary>
        /// <param name="onText">Receives the words heard (never empty).</param>
        /// <param name="onStatus">Receives short status lines ("Listening…", errors) for the panel, or null.</param>
        public static Button Create(Transform parent, Action<string> onText, Action<string> onStatus = null, float fontSize = 20)
        {
            MicButton mic = null;
            var button = UiFactory.CreateButton(parent, "Talk", UiFactory.CardColor, () => mic.OnPressed(), fontSize);
            mic = button.gameObject.AddComponent<MicButton>();
            mic.m_Button = button;
            mic.m_OnText = onText;
            mic.m_OnStatus = onStatus;
            return button;
        }

        void OnEnable()
        {
            s_Visible.Remove(this);
            s_Visible.Add(this);
        }

        void OnDisable()
        {
            s_Visible.Remove(this);
            if (m_Listening)
                SpeechInput.Instance?.Cancel();
            m_Listening = false;
        }

        /// <summary>Starts listening, from the mic gesture. Does nothing if already listening or busy.</summary>
        public void BeginFromGesture()
        {
            if (!m_Listening && !m_Busy)
                StartListening();
        }

        /// <summary>Stops listening and uses what was said, from the mic gesture.</summary>
        public void EndFromGesture()
        {
            if (m_Listening)
                Finish();
        }

        void Update()
        {
            var input = SpeechInput.Instance;
            if (m_Listening && input != null && !input.IsRecording && !m_Busy)
                Finish(); // stopped by its length limit

            string label;
            Color color;
            if (m_Busy && !m_Listening)
            {
                label = "…";
                color = UiFactory.CardColor;
            }
            else if (m_Listening && input != null)
            {
                label = $"Stop {input.RecordedSeconds:0}s";
                color = Color.Lerp(UiFactory.DangerColor * 0.7f, UiFactory.DangerColor, input.Level);
                color.a = 1f;
            }
            else
            {
                label = "Talk";
                color = input != null && input.IsReady ? Color.Lerp(UiFactory.CardColor, UiFactory.AccentColor, 0.35f) : UiFactory.CardColor;
            }

            UiFactory.SetLabel(m_Button, label);
            m_Button.image.color = color;
            m_Button.interactable = !(m_Busy && !m_Listening);
        }

        void OnPressed()
        {
            if (m_Listening)
                Finish();
            else
                StartListening();
        }

        async void StartListening()
        {
            var input = SpeechInput.Instance;
            if (input == null || !input.IsReady)
            {
                var hint = input != null && input.State == SpeechInput.Status.Loading
                    ? (AppLanguage.IsItalian ? "L'input vocale si sta caricando…" : "Speech input is loading…")
                    : (AppLanguage.IsItalian
                        ? "Per parlare, scarica \"Speech input\" nel menu principale."
                        : "To talk, download \"Speech input\" in the main menu.");
                m_OnStatus?.Invoke(hint);
                MicFeedback.ShowHint(hint);
                return;
            }

            m_Busy = true;
            var started = await input.StartListeningAsync();
            m_Busy = false;
            if (!started)
            {
                m_OnStatus?.Invoke(input.Info);
                MicFeedback.ShowHint(input.Info);
                return;
            }

            m_Listening = true;
            m_OnStatus?.Invoke(AppLanguage.IsItalian ? "Ti ascolto… premi Stop quando hai finito." : "Listening… press Stop when you're done.");
        }

        async void Finish()
        {
            var input = SpeechInput.Instance;
            m_Listening = false;
            if (input == null)
                return;
            m_Busy = true;
            m_OnStatus?.Invoke(AppLanguage.IsItalian ? "Trascrivo…" : "Transcribing…");
            var result = await input.StopAndTranscribeAsync();
            m_Busy = false;
            if (result.Error != null)
            {
                m_OnStatus?.Invoke(result.Error);
                return;
            }

            if (string.IsNullOrWhiteSpace(result.Text))
            {
                var nothing = AppLanguage.IsItalian ? "Non ho sentito niente." : "I didn't hear anything.";
                m_OnStatus?.Invoke(nothing);
                MicFeedback.ShowHint(nothing);
                return;
            }

            m_OnStatus?.Invoke("");
            m_OnText?.Invoke(result.Text.Trim());
        }
    }
}
