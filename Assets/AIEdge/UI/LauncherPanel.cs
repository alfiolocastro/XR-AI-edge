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
    /// Home panel: the experiences (AI Chat, Ask Image, Tiny Garden), the model manager
    /// (download with resume, cancel, delete, load/unload, memory) and the neural voice for spoken replies. Built in code with <see cref="UiFactory"/>.
    /// </summary>
    public class LauncherPanel : MonoBehaviour
    {
        const float k_Width = 900f;
        const float k_Height = 980f;
        const float k_RefreshInterval = 0.25f;

        [SerializeField, Tooltip("Model service. Uses LlmService.Instance if empty.")]
        LlmService m_Service;

        [SerializeField, Tooltip("Distance from the user's head when placed, in meters.")]
        float m_Distance = 1.1f;

        /// <summary>Raised when the Benchmark button is pressed.</summary>
        public event Action BenchmarkRequested;

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
            public TextMeshProUGUI Status;
            public Image Progress;
            public Button Primary;
            public Button Delete;
            public CancellationTokenSource Download;
            public DownloadProgress LastProgress;
            public string Error;
        }

        readonly List<ModelRow> m_Rows = new List<ModelRow>();
        VoiceRow m_Voice;
        readonly List<ExperienceTile> m_Tiles = new List<ExperienceTile>();
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
            Invoke(nameof(Recenter), 1f); // let head tracking settle
        }

        void OnDestroy()
        {
            foreach (var row in m_Rows)
                row.Download?.Cancel();
            m_Voice?.Download?.Cancel();
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
        /// <param name="index">0 = AI Chat, 1 = Ask Image, 2 = Tiny Garden.</param>
        public void SetExperience(int index, string title, string subtitle, Action open)
        {
            var tile = m_Tiles[index];
            tile.Label.text = $"{title}\n<size=18><color=#B3BACC>{subtitle}</color></size>";
            tile.Open = open;
            tile.Button.interactable = open != null;
        }

        void Build()
        {
            m_Panel = UiFactory.CreatePanel(transform, "LauncherPanel", new Vector2(k_Width, k_Height));

            var title = UiFactory.CreateText(m_Panel, "Title", "XR AI Edge", 44);
            UiFactory.Place(title.rectTransform, 32, 22, 600, 56);
            var subtitle = UiFactory.CreateText(m_Panel, "Subtitle", "Google AI Edge Gallery experiences, running on this headset", 22);
            subtitle.color = UiFactory.MutedTextColor;
            UiFactory.Place(subtitle.rectTransform, 32, 78, 836, 32);

            var experiences = UiFactory.CreateText(m_Panel, "ExperiencesHeader", "Experiences", 26);
            UiFactory.Place(experiences.rectTransform, 32, 124, 400, 34);
            for (var i = 0; i < 3; i++)
            {
                var tile = new ExperienceTile();
                tile.Button = UiFactory.CreateButton(m_Panel, $"Experience{i}", UiFactory.CardColor, () => tile.Open?.Invoke(), 28);
                UiFactory.Place((RectTransform)tile.Button.transform, 32 + i * 284, 164, 268, 110);
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

            m_Voice = BuildVoiceRow(y);

            m_Footer = UiFactory.CreateText(m_Panel, "Footer", "", 20);
            m_Footer.color = UiFactory.MutedTextColor;
            UiFactory.Place(m_Footer.rectTransform, 32, 800, 836, 60);

            var benchmark = UiFactory.CreateButton(m_Panel, "Benchmark", UiFactory.CardColor, () => BenchmarkRequested?.Invoke());
            UiFactory.Place((RectTransform)benchmark.transform, 32, 894, 268, 60);
            var unload = UiFactory.CreateButton(m_Panel, "Unload model", UiFactory.CardColor, () => m_Service.Unload());
            UiFactory.Place((RectTransform)unload.transform, 316, 894, 268, 60);
            var recenter = UiFactory.CreateButton(m_Panel, "Recenter", UiFactory.CardColor, Recenter);
            UiFactory.Place((RectTransform)recenter.transform, 600, 894, 268, 60);
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

        VoiceRow BuildVoiceRow(float y)
        {
            var voice = VoiceCatalog.Default;
            var row = new VoiceRow();
            var card = UiFactory.CreateImage(m_Panel, voice.id, UiFactory.CardColor);
            UiFactory.Place(card.rectTransform, 32, y, 836, 136);

            var name = UiFactory.CreateText(card.transform, "Name", voice.displayName, 28);
            UiFactory.Place(name.rectTransform, 20, 12, 560, 36);
            var details = $"{ModelDownloader.FormatBytes(voice.sizeBytes)} · spoken replies, offline · CPU · {voice.license}";
            var info = UiFactory.CreateText(card.transform, "Details", details, 20);
            info.color = UiFactory.MutedTextColor;
            UiFactory.Place(info.rectTransform, 20, 50, 560, 28);
            row.Status = UiFactory.CreateText(card.transform, "Status", "", 20);
            UiFactory.Place(row.Status.rectTransform, 20, 80, 560, 30);

            row.Progress = UiFactory.CreateProgressBar(card.transform, "Progress");
            UiFactory.Place((RectTransform)row.Progress.transform.parent, 20, 116, 560, 8);

            row.Primary = UiFactory.CreateButton(card.transform, "Download", UiFactory.AccentColor, OnVoicePrimary);
            UiFactory.Place((RectTransform)row.Primary.transform, 600, 16, 216, 50);
            row.Delete = UiFactory.CreateButton(card.transform, "Delete", UiFactory.DangerColor, OnVoiceDelete, 22);
            UiFactory.Place((RectTransform)row.Delete.transform, 600, 74, 216, 46);
            return row;
        }

        void OnVoicePrimary()
        {
            var speech = SpeechEngine.Instance;
            if (speech == null)
                return;

            var voice = VoiceCatalog.Default;
            if (speech.Downloader.IsDownloading(voice))
                m_Voice.Download?.Cancel();
            else if (VoiceStore.GetState(voice) == ModelState.Downloaded)
                speech.Speak("Hello! This is how replies will sound.", interrupt: true);
            else
                DownloadVoice(speech);
            Refresh();
        }

        async void DownloadVoice(SpeechEngine speech)
        {
            var row = m_Voice;
            row.Error = null;
            row.Download = new CancellationTokenSource();
            var progress = new Progress<DownloadProgress>(p => row.LastProgress = p);
            try
            {
                await speech.Downloader.DownloadAsync(VoiceCatalog.Default, progress, row.Download.Token);
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

        void OnVoiceDelete()
        {
            var speech = SpeechEngine.Instance;
            // Release the voice files before deleting them, then fall back to the system engine.
            speech?.ShutdownEngine();
            try
            {
                VoiceStore.Delete(VoiceCatalog.Default);
                m_Voice.Error = null;
            }
            catch (IOException e)
            {
                m_Voice.Error = e.Message;
            }

            speech?.SelectEngine();
            Refresh();
        }

        void RefreshVoice()
        {
            var row = m_Voice;
            var voice = VoiceCatalog.Default;
            var speech = SpeechEngine.Instance;
            var downloading = speech != null && speech.Downloader.IsDownloading(voice);
            var state = VoiceStore.GetState(voice);

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
                status = speech == null || !speech.IsNeural ? "Ready" : speech.IsReady ? "<color=#7FE0A0>In use</color>" : "Loading…";
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
                status = speech != null && speech.IsReady ? "Not downloaded · using the system voice" : "Not downloaded · needed for spoken replies";
                primary = "Download";
            }

            if (row.Error != null)
                status = $"<color=#FF8080>{row.Error}</color>";

            row.Status.text = status;
            UiFactory.SetProgress(row.Progress, fraction);
            UiFactory.SetLabel(row.Primary, primary);
            row.Primary.interactable = speech != null && (state != ModelState.Downloaded || speech.IsReady);
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
            RefreshVoice();

            var loaded = m_Service.LoadedModel;
            var memory = loaded != null && m_Service.LoadInfo != null ? $"{loaded.displayName} loaded ({m_Service.LoadInfo.backend}, {m_Service.LoadInfo.pssMb} MB)" : "No model loaded";
            var token = ModelStore.HuggingFaceToken != null ? "found" : "missing (add hf_token.txt to the app folder for gated models)";
            m_Footer.text = $"{memory}\nStorage free: {ModelDownloader.FormatBytes(ModelStore.GetFreeBytes())} · Hugging Face token: {token}";
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
