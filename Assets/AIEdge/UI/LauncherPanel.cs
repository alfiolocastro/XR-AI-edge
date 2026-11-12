using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AIEdge.UI
{
    /// <summary>
    /// Home panel: the experiences (AI Chat, Point &amp; Ask, Tabletop Garden, What's That?), the model manager
    /// (download with resume, cancel, delete, load/unload, memory), the neural voices for spoken replies, speech input,
    /// and the app language (<see cref="AppLanguage"/>) used by replies, voices and speech input. Built in code with
    /// <see cref="UiFactory"/>.
    /// </summary>
    public class LauncherPanel : MonoBehaviour
    {
        const float k_Width = 900f;
        const float k_Height = 1156f;
        const float k_VoiceRowHeight = 100f;
        const int k_ExperienceCount = 4;
        const float k_RefreshInterval = 0.25f;

        [SerializeField, Tooltip("Model service. Uses LlmService.Instance if empty.")]
        LlmService m_Service;

        [SerializeField, Tooltip("Distance from the user's head when placed, in meters.")]
        float m_Distance = 1.1f;

        /// <summary>Raised when the Benchmark button is pressed.</summary>
        public event Action BenchmarkRequested;

        /// <summary>Raised when the My instructions button is pressed.</summary>
        public event Action InstructionsRequested;

        class ModelRow
        {
            public ModelInfo Model;
            public TextMeshProUGUI Status;
            public Image Progress;
            public Button Primary;
            public Button Delete;
            public CancellationTokenSource Download;
            public DownloadProgress LastProgress;
            public string Error;
        }

        class ExperienceTile
        {
            public Button Button;
            public TextMeshProUGUI Label;
            public Action Open;
        }

        class VoiceRow
        {
            public VoiceInfo Voice;
            public TextMeshProUGUI Status;
            public Image Progress;
            public Button Primary;
            public Button Delete;
            public CancellationTokenSource Download;
            public DownloadProgress LastProgress;
            public string Error;
        }

        readonly List<ModelRow> m_Rows = new List<ModelRow>();
        readonly List<VoiceRow> m_VoiceRows = new List<VoiceRow>();
        readonly List<ExperienceTile> m_Tiles = new List<ExperienceTile>();
        readonly Dictionary<string, Button> m_LanguageButtons = new Dictionary<string, Button>();
        VoiceRow m_SpeechInputRow;
        string m_Heard;
        bool m_Trying;
        RectTransform m_Panel;
        TextMeshProUGUI m_Footer;
        float m_NextRefresh;

        void Start()
        {
            if (m_Service == null)
                m_Service = LlmService.Instance;

            Build();
            SetExperience(0, "AI Chat", "Coming in Phase 2", null);
            SetExperience(1, "Ask Image", "Coming in Phase 3", null);
            SetExperience(2, "Tiny Garden", "Coming in Phase 4", null);
            SetExperience(3, "What's That?", "Coming soon", null);
            Invoke(nameof(Recenter), 1f); // let head tracking settle
        }

        void OnDestroy()
        {
            foreach (var row in m_Rows)
                row.Download?.Cancel();
            foreach (var row in m_VoiceRows)
                row.Download?.Cancel();
        }

        void Update()
        {
            if (Time.unscaledTime < m_NextRefresh)
                return;

            m_NextRefresh = Time.unscaledTime + k_RefreshInterval;
            Refresh();
        }

        /// <summary>Shows or hides the panel.</summary>
        public void SetVisible(bool visible) => m_Panel.gameObject.SetActive(visible);

        /// <summary>Moves the panel in front of the user.</summary>
        public void Recenter() => UiFactory.PlaceInFront(transform, m_Distance);

        /// <summary>Sets an experience tile. A null <paramref name="open"/> shows it as unavailable.</summary>
        /// <param name="index">0 = AI Chat, 1 = Point &amp; Ask, 2 = Tabletop Garden, 3 = What's That?.</param>
        public void SetExperience(int index, string title, string subtitle, Action open)
        {
            var tile = m_Tiles[index];
            tile.Label.text = $"{title}\n<size=16><color=#B3BACC>{subtitle}</color></size>";
            tile.Open = open;
            tile.Button.interactable = open != null;
        }

        void Build()
        {
            m_Panel = UiFactory.CreatePanel(transform, "LauncherPanel", new Vector2(k_Width, k_Height));

            var title = UiFactory.CreateText(m_Panel, "Title", "XR AI Edge", 44);
            UiFactory.Place(title.rectTransform, 32, 22, 420, 56);
            var subtitle = UiFactory.CreateText(m_Panel, "Subtitle", "Google AI Edge Gallery experiences, running on this headset", 22);
            subtitle.color = UiFactory.MutedTextColor;
            UiFactory.Place(subtitle.rectTransform, 32, 78, 560, 32);

            // The language of replies, voices and speech input, for every experience.
            for (var i = 0; i < AppLanguage.All.Length; i++)
            {
                var language = AppLanguage.All[i];
                var button = UiFactory.CreateButton(m_Panel, AppLanguage.DisplayName(language), UiFactory.CardColor, () => AppLanguage.Set(language), 22);
                UiFactory.Place((RectTransform)button.transform, 616 + i * 130, 24, 122, 52);
                m_LanguageButtons[language] = button;
            }

            var languageHint = UiFactory.CreateText(m_Panel, "LanguageHint", "Replies · voice · speech input", 16, TextAlignmentOptions.Center);
            languageHint.color = UiFactory.MutedTextColor;
            UiFactory.Place(languageHint.rectTransform, 616, 80, 252, 26);

            var experiences = UiFactory.CreateText(m_Panel, "ExperiencesHeader", "Experiences", 26);
            UiFactory.Place(experiences.rectTransform, 32, 124, 400, 34);
            for (var i = 0; i < k_ExperienceCount; i++)
            {
                var tile = new ExperienceTile();
                tile.Button = UiFactory.CreateButton(m_Panel, $"Experience{i}", UiFactory.CardColor, () => tile.Open?.Invoke(), 25);
                UiFactory.Place((RectTransform)tile.Button.transform, 32 + i * 212, 164, 200, 110);
                tile.Label = tile.Button.GetComponentInChildren<TextMeshProUGUI>();
                m_Tiles.Add(tile);
            }

            var models = UiFactory.CreateText(m_Panel, "ModelsHeader", "Models", 26);
            UiFactory.Place(models.rectTransform, 32, 300, 400, 34);
            var y = 340f;
            foreach (var model in ModelCatalog.All)
            {
                m_Rows.Add(BuildModelRow(model, y));
                y += 150f;
            }

            foreach (var voice in VoiceCatalog.All)
            {
                m_VoiceRows.Add(BuildVoiceRow(voice, y));
                y += k_VoiceRowHeight + 10f;
            }

            m_SpeechInputRow = BuildVoiceRow(SpeechInput.Model, y);
            m_VoiceRows.Add(m_SpeechInputRow);
            y += k_VoiceRowHeight + 10f;

            m_Footer = UiFactory.CreateText(m_Panel, "Footer", "", 20);
            m_Footer.color = UiFactory.MutedTextColor;
            UiFactory.Place(m_Footer.rectTransform, 32, y + 4, 836, 84);

            var instructions = UiFactory.CreateButton(m_Panel, "My instructions", UiFactory.AccentColor, () => InstructionsRequested?.Invoke(), 22);
            UiFactory.Place((RectTransform)instructions.transform, 32, y + 96, 200, 60);
            var benchmark = UiFactory.CreateButton(m_Panel, "Benchmark", UiFactory.CardColor, () => BenchmarkRequested?.Invoke(), 22);
            UiFactory.Place((RectTransform)benchmark.transform, 244, y + 96, 200, 60);
            var unload = UiFactory.CreateButton(m_Panel, "Unload model", UiFactory.CardColor, () => m_Service.Unload(), 22);
            UiFactory.Place((RectTransform)unload.transform, 456, y + 96, 200, 60);
            var recenter = UiFactory.CreateButton(m_Panel, "Recenter", UiFactory.CardColor, Recenter, 22);
            UiFactory.Place((RectTransform)recenter.transform, 668, y + 96, 200, 60);
        }

        ModelRow BuildModelRow(ModelInfo model, float y)
        {
            var row = new ModelRow { Model = model };
            var card = UiFactory.CreateImage(m_Panel, model.id, UiFactory.CardColor);
            UiFactory.Place(card.rectTransform, 32, y, 836, 136);

            var name = UiFactory.CreateText(card.transform, "Name", model.displayName, 28);
            UiFactory.Place(name.rectTransform, 20, 12, 560, 36);
            var details = $"{ModelDownloader.FormatBytes(model.sizeBytes)} · {(model.supportsImage ? "text + image" : "function calling")} · {model.backend.ToString().ToUpperInvariant()}{(model.gated ? " · gated" : "")}";
            var info = UiFactory.CreateText(card.transform, "Details", details, 20);
            info.color = UiFactory.MutedTextColor;
            UiFactory.Place(info.rectTransform, 20, 50, 560, 28);
            row.Status = UiFactory.CreateText(card.transform, "Status", "", 20);
            UiFactory.Place(row.Status.rectTransform, 20, 80, 560, 30);

            var track = UiFactory.CreateProgressBar(card.transform, "Progress");
            row.Progress = track;
            UiFactory.Place((RectTransform)track.transform.parent, 20, 116, 560, 8);

            row.Primary = UiFactory.CreateButton(card.transform, "Download", UiFactory.AccentColor, () => OnPrimary(row));
            UiFactory.Place((RectTransform)row.Primary.transform, 600, 16, 216, 50);
            row.Delete = UiFactory.CreateButton(card.transform, "Delete", UiFactory.DangerColor, () => OnDelete(row), 22);
            UiFactory.Place((RectTransform)row.Delete.transform, 600, 74, 216, 46);
            return row;
        }

        VoiceRow BuildVoiceRow(VoiceInfo voice, float y)
        {
            var row = new VoiceRow { Voice = voice };
            var card = UiFactory.CreateImage(m_Panel, voice.id, UiFactory.CardColor);
            UiFactory.Place(card.rectTransform, 32, y, 836, k_VoiceRowHeight);

            var name = UiFactory.CreateText(card.transform, "Name", voice.displayName, 24);
            UiFactory.Place(name.rectTransform, 20, 8, 560, 32);
            row.Status = UiFactory.CreateText(card.transform, "Status", "", 19);
            UiFactory.Place(row.Status.rectTransform, 20, 42, 560, 28);

            row.Progress = UiFactory.CreateProgressBar(card.transform, "Progress");
            UiFactory.Place((RectTransform)row.Progress.transform.parent, 20, 80, 560, 8);

            row.Primary = UiFactory.CreateButton(card.transform, "Download", UiFactory.AccentColor, () => OnVoicePrimary(row), 22);
            UiFactory.Place((RectTransform)row.Primary.transform, 600, 8, 216, 40);
            row.Delete = UiFactory.CreateButton(card.transform, "Delete", UiFactory.DangerColor, () => OnVoiceDelete(row), 20);
            UiFactory.Place((RectTransform)row.Delete.transform, 600, 54, 216, 36);
            return row;
        }

        void OnVoicePrimary(VoiceRow row)
        {
            if (row == m_SpeechInputRow)
            {
                OnSpeechInputPrimary(row);
                return;
            }

            var speech = SpeechEngine.Instance;
            if (speech == null)
                return;

            var voice = row.Voice;
            if (speech.Downloader.IsDownloading(voice))
                row.Download?.Cancel();
            else if (VoiceStore.GetState(voice) == ModelState.Downloaded)
                speech.Speak(voice.language == SpeechLanguage.Italian
                    ? "Ciao! Ecco come suoneranno le risposte in italiano."
                    : "Hello! This is how replies will sound.", interrupt: true, language: voice.language);
            else
                DownloadVoice(speech, row);
            Refresh();
        }

        async void DownloadVoice(SpeechEngine speech, VoiceRow row)
        {
            row.Error = null;
            row.Download = new CancellationTokenSource();
            var progress = new Progress<DownloadProgress>(p => row.LastProgress = p);
            try
            {
                await speech.Downloader.DownloadAsync(row.Voice, progress, row.Download.Token);
                speech.SelectEngine();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e) when (e is IOException || e is InvalidOperationException)
            {
                row.Error = e.Message;
                Debug.LogWarning($"[AIEdge] {e.Message}");
            }
            finally
            {
                row.Download.Dispose();
                row.Download = null;
                Refresh();
            }
        }

        void OnVoiceDelete(VoiceRow row)
        {
            if (row == m_SpeechInputRow)
            {
                var input = SpeechInput.Instance;
                input?.Shutdown();
                try
                {
                    VoiceStore.Delete(row.Voice);
                    row.Error = null;
                }
                catch (IOException e)
                {
                    row.Error = e.Message;
                }

                m_Heard = null;
                input?.Reload();
                Refresh();
                return;
            }

            var speech = SpeechEngine.Instance;
            // Release the voice files before deleting them, then reload the voices that are left.
            speech?.ShutdownEngine();
            try
            {
                VoiceStore.Delete(row.Voice);
                row.Error = null;
            }
            catch (IOException e)
            {
                row.Error = e.Message;
            }

            speech?.SelectEngine();
            Refresh();
        }

        // Speech input: download Whisper, then "Try it" records a few words and shows what was heard.
        async void OnSpeechInputPrimary(VoiceRow row)
        {
            var input = SpeechInput.Instance;
            if (input == null)
                return;

            if (input.Downloader.IsDownloading(row.Voice))
            {
                row.Download?.Cancel();
            }
            else if (VoiceStore.GetState(row.Voice) != ModelState.Downloaded)
            {
                row.Error = null;
                row.Download = new CancellationTokenSource();
                var progress = new Progress<DownloadProgress>(p => row.LastProgress = p);
                try
                {
                    await input.Downloader.DownloadAsync(row.Voice, progress, row.Download.Token);
                    input.Reload();
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception e) when (e is IOException || e is InvalidOperationException)
                {
                    row.Error = e.Message;
                    Debug.LogWarning($"[AIEdge] {e.Message}");
                }
                finally
                {
                    row.Download.Dispose();
                    row.Download = null;
                }
            }
            else if (input.IsRecording && m_Trying)
            {
                m_Trying = false;
                m_Heard = "…";
                Refresh();
                var result = await input.StopAndTranscribeAsync();
                m_Heard = result.Error ?? (string.IsNullOrWhiteSpace(result.Text) ? "(nothing)" : $"\"{result.Text}\"");
            }
            else if (input.IsReady)
            {
                m_Heard = null;
                m_Trying = await input.StartListeningAsync();
                if (!m_Trying)
                    m_Heard = input.Info;
            }

            Refresh();
        }

        void RefreshSpeechInput(VoiceRow row)
        {
            var input = SpeechInput.Instance;
            var voice = row.Voice;
            var downloading = input != null && input.Downloader.IsDownloading(voice);
            var state = VoiceStore.GetState(voice);
            var details = $"{ModelDownloader.FormatBytes(voice.sizeBytes)} · offline · {voice.license}";

            string status;
            float fraction;
            var primary = "Download";
            if (downloading)
            {
                var p = row.LastProgress;
                fraction = p.Fraction;
                var total = p.TotalBytes > 0 ? p.TotalBytes : voice.sizeBytes;
                status = $"Downloading {fraction * 100f:0}% · {ModelDownloader.FormatBytes(p.Bytes)} of {ModelDownloader.FormatBytes(total)} · {ModelDownloader.FormatBytes((long)p.BytesPerSecond)}/s";
                primary = "Cancel";
            }
            else if (state == ModelState.Downloaded)
            {
                fraction = 1f;
                var inputState = input != null ? input.State : SpeechInput.Status.Unavailable;
                status = inputState switch
                {
                    SpeechInput.Status.Recording => $"<color=#FF9090>Listening {input.RecordedSeconds:0}s: say something in {AppLanguage.DisplayName(AppLanguage.Current)}</color>",
                    SpeechInput.Status.Transcribing => "Transcribing…",
                    SpeechInput.Status.Ready => $"{details} · <color=#7FE0A0>In use</color>",
                    SpeechInput.Status.Failed => $"<color=#FF8080>{input.Info}</color>",
                    SpeechInput.Status.Unavailable => $"{details} · Android only",
                    _ => $"{details} · Loading…",
                };
                if (m_Heard != null && inputState != SpeechInput.Status.Recording)
                    status = $"Heard: {MarkdownToTmp.Escape(m_Heard)}";
                primary = inputState == SpeechInput.Status.Recording && m_Trying ? "Stop" : "Try it";
            }
            else if (state == ModelState.Partial)
            {
                fraction = Mathf.Clamp01((float)((double)VoiceStore.GetBytes(voice) / voice.sizeBytes));
                status = $"Paused at {fraction * 100f:0}%";
                primary = "Resume";
            }
            else
            {
                fraction = 0f;
                status = $"{details} · not downloaded · talk instead of typing";
            }

            if (row.Error != null)
                status = $"<color=#FF8080>{row.Error}</color>";

            row.Status.text = status;
            UiFactory.SetProgress(row.Progress, fraction);
            UiFactory.SetLabel(row.Primary, primary);
            row.Primary.interactable = input != null && (state != ModelState.Downloaded || input.IsReady || input.IsRecording && m_Trying);
            row.Delete.interactable = !downloading && state != ModelState.NotDownloaded && (input == null || !input.IsRecording);
        }

        void RefreshVoice(VoiceRow row)
        {
            if (row == m_SpeechInputRow)
            {
                RefreshSpeechInput(row);
                return;
            }

            var voice = row.Voice;
            var speech = SpeechEngine.Instance;
            var downloading = speech != null && speech.Downloader.IsDownloading(voice);
            var state = VoiceStore.GetState(voice);
            var details = $"{ModelDownloader.FormatBytes(voice.sizeBytes)} · offline · {voice.license}";

            string status;
            float fraction;
            string primary;
            if (downloading)
            {
                var p = row.LastProgress;
                fraction = p.Fraction;
                var total = p.TotalBytes > 0 ? p.TotalBytes : voice.sizeBytes;
                status = $"Downloading {fraction * 100f:0}% · {ModelDownloader.FormatBytes(p.Bytes)} of {ModelDownloader.FormatBytes(total)} · {ModelDownloader.FormatBytes((long)p.BytesPerSecond)}/s";
                primary = "Cancel";
            }
            else if (state == ModelState.Downloaded)
            {
                fraction = 1f;
                var ready = speech != null && speech.IsVoiceReady(voice);
                status = $"{details} · {(ready ? "<color=#7FE0A0>In use</color>" : "Loading…")}";
                primary = "Test voice";
            }
            else if (state == ModelState.Partial)
            {
                fraction = Mathf.Clamp01((float)((double)VoiceStore.GetBytes(voice) / voice.sizeBytes));
                status = $"Paused at {fraction * 100f:0}%";
                primary = "Resume";
            }
            else
            {
                fraction = 0f;
                status = $"{details} · not downloaded";
                primary = "Download";
            }

            if (row.Error != null)
                status = $"<color=#FF8080>{row.Error}</color>";

            row.Status.text = status;
            UiFactory.SetProgress(row.Progress, fraction);
            UiFactory.SetLabel(row.Primary, primary);
            row.Primary.interactable = speech != null && (state != ModelState.Downloaded || speech.IsVoiceReady(voice));
            row.Delete.interactable = !downloading && state != ModelState.NotDownloaded;
        }

        void OnPrimary(ModelRow row)
        {
            var model = row.Model;
            if (m_Service.Downloader.IsDownloading(model))
            {
                row.Download?.Cancel();
                return;
            }

            switch (ModelStore.GetState(model))
            {
                case ModelState.Downloaded when m_Service.LoadedModel == model:
                    m_Service.Unload();
                    break;
                case ModelState.Downloaded:
                    Load(row);
                    break;
                default:
                    Download(row);
                    break;
            }

            Refresh();
        }

        async void Download(ModelRow row)
        {
            row.Error = null;
            row.Download = new CancellationTokenSource();
            var progress = new Progress<DownloadProgress>(p => row.LastProgress = p);
            try
            {
                await m_Service.Downloader.DownloadAsync(row.Model, progress, row.Download.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e) when (e is ModelAccessException || e is IOException || e is InvalidOperationException)
            {
                row.Error = e.Message;
                Debug.LogWarning($"[AIEdge] {e.Message}");
            }
            finally
            {
                row.Download.Dispose();
                row.Download = null;
                Refresh();
            }
        }

        async void Load(ModelRow row)
        {
            row.Error = null;
            try
            {
                await m_Service.LoadAsync(row.Model, row.Model.supportsImage);
            }
            catch (Exception e) when (e is LlmException || e is InvalidOperationException)
            {
                row.Error = e.Message;
                Debug.LogWarning($"[AIEdge] {e.Message}");
            }

            Refresh();
        }

        void OnDelete(ModelRow row)
        {
            if (m_Service.LoadedModel == row.Model)
                m_Service.Unload();
            ModelStore.Delete(row.Model);
            row.Error = null;
            Refresh();
        }

        void Refresh()
        {
            if (m_Service == null)
                return;

            foreach (var row in m_Rows)
                RefreshRow(row);
            foreach (var row in m_VoiceRows)
                RefreshVoice(row);
            foreach (var (language, button) in m_LanguageButtons)
                button.image.color = language == AppLanguage.Current ? UiFactory.AccentColor : UiFactory.CardColor;

            var loaded = m_Service.LoadedModel;
            var memory = loaded != null && m_Service.LoadInfo != null ? $"{loaded.displayName} loaded ({m_Service.LoadInfo.backend}, {m_Service.LoadInfo.pssMb} MB)" : "No model loaded";
            var token = ModelStore.HuggingFaceToken != null ? "found" : "missing (add hf_token.txt to the app folder for gated models)";
            m_Footer.text = $"{memory}\nStorage free: {ModelDownloader.FormatBytes(ModelStore.GetFreeBytes())} · Hugging Face token: {token}\n" +
                $"My instructions: {MarkdownToTmp.Escape(UserInstructions.Summary(70))}";
        }

        void RefreshRow(ModelRow row)
        {
            var model = row.Model;
            var state = ModelStore.GetState(model);
            var downloading = m_Service.Downloader.IsDownloading(model);
            var loaded = m_Service.LoadedModel == model;
            var loading = m_Service.IsLoading;

            string status;
            float fraction;
            string primary;
            if (downloading)
            {
                var p = row.LastProgress;
                fraction = p.Fraction;
                status = $"Downloading {fraction * 100f:0}% · {ModelDownloader.FormatBytes(p.Bytes)} of {ModelDownloader.FormatBytes(model.sizeBytes)} · {ModelDownloader.FormatBytes((long)p.BytesPerSecond)}/s";
                primary = "Cancel";
            }
            else if (state == ModelState.Downloaded)
            {
                fraction = 1f;
                status = loaded ? "<color=#7FE0A0>Loaded</color>" : loading ? "Loading…" : "Ready";
                primary = loaded ? "Unload" : "Load";
            }
            else if (state == ModelState.Partial)
            {
                fraction = model.sizeBytes > 0 ? (float)((double)ModelStore.GetPartialBytes(model) / model.sizeBytes) : 0f;
                status = $"Paused at {fraction * 100f:0}%";
                primary = "Resume";
            }
            else
            {
                fraction = 0f;
                status = model.gated && ModelStore.HuggingFaceToken == null ? "Not downloaded · needs Hugging Face token" : "Not downloaded";
                primary = "Download";
            }

            if (row.Error != null)
                status = $"<color=#FF8080>{row.Error}</color>";

            row.Status.text = status;
            UiFactory.SetProgress(row.Progress, fraction);
            UiFactory.SetLabel(row.Primary, primary);
            row.Primary.interactable = !(loading && state == ModelState.Downloaded);
            row.Delete.interactable = !downloading && !loading && state != ModelState.NotDownloaded;
        }
    }
}
