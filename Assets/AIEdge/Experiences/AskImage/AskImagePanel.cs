using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AIEdge.Spatial;
using AIEdge.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AIEdge.AskImage
{
    /// <summary>
    /// The result of one Point &amp; Ask question.
    /// </summary>
    public class AskResult
    {
        public string Question;
        public string Answer;
        public RectInt Crop;
        public string ImagePath;
        public LlmStats Stats;
        public string Error;

        /// <summary>Depth measurements of the framed object, or null without depth.</summary>
        public DepthBlob Depth;

        /// <summary>The measurement sentence added to the prompt, or empty.</summary>
        public string Facts = "";
    }

    /// <summary>
    /// Point &amp; Ask (the Gallery's Ask Image): frame something in the room with both hands, or press Capture, and
    /// Gemma answers about the cropped passthrough camera image on a card pinned where you framed it.
    /// </summary>
    public class AskImagePanel : MonoBehaviour
    {
        const float k_Width = 760f;
        const float k_Height = 470f;
        const float k_MinProjectionDistance = 1.0f;
        const float k_CropPadding = 0.08f;
        const int k_MinCropPixels = 224;
        const float k_RenderInterval = 0.06f;
        const int k_MaxCards = 6;
        static readonly Vector2 k_CaptureSize = new Vector2(0.6f, 0.45f);
        const float k_CaptureDistance = 1.2f;

        static readonly string[] k_Questions =
        {
            "What is this?",
            "Read the text in this image.",
            "How do I use this?",
            "Describe this scene in detail.",
        };

        [SerializeField, Tooltip("Model service. Uses LlmService.Instance if empty.")]
        LlmService m_Service;

        [SerializeField, Tooltip("Passthrough camera frame source.")]
        PassthroughFrameCapture m_Camera;

        [SerializeField, Tooltip("Room depth for the smart crop and measurements. Uses DepthService.Instance if empty.")]
        DepthService m_Depth;

        [SerializeField, Tooltip("Rays per side of the depth grid cast over the framed region.")]
        int m_DepthGrid = 11;

        [SerializeField, Tooltip("Two-hand framing gesture. Created on this object if empty.")]
        FrameGesture m_Gesture;

        [SerializeField, Tooltip("Distance from the user's head when opened, in meters.")]
        float m_Distance = 0.9f;

        /// <summary>Raised when the user closes Point &amp; Ask.</summary>
        public event Action Closed;

        /// <summary>Raised when an answer finishes or fails.</summary>
        public event Action<AskResult> AnswerFinished;

        /// <summary>True when the vision model is loaded, the camera streams and no question is running.</summary>
        public bool IsReady => m_ModelReady && m_Camera.IsReady && m_Pending == null && !m_Preparing;

        /// <summary>True while the panel is open.</summary>
        public bool IsOpen => m_Panel != null && m_Panel.gameObject.activeSelf;

        ModelInfo Model => ModelCatalog.Gemma4E2B;

        RectTransform m_Panel;
        TextMeshProUGUI m_Status;
        TMP_InputField m_Question;
        Button m_CaptureButton;
        Transform m_CardRoot;
        readonly List<AnswerCard> m_Cards = new List<AnswerCard>();
        readonly StringBuilder m_Stream = new StringBuilder();
        AskResult m_Pending;
        AnswerCard m_PendingCard;
        bool m_ModelReady;
        bool m_Preparing;
        bool m_StreamDirty;
        float m_NextRender;
        int m_ShotCount;

        void Awake()
        {
            if (m_Service == null)
                m_Service = LlmService.Instance;
            if (m_Gesture == null)
                m_Gesture = gameObject.AddComponent<FrameGesture>();
            m_Gesture.enabled = false;
        }

        void OnEnable() => m_Gesture.Framed += OnFramed;

        void OnDisable() => m_Gesture.Framed -= OnFramed;

        void OnDestroy()
        {
            Unsubscribe();
            if (m_CardRoot != null)
                Destroy(m_CardRoot.gameObject);
        }

        /// <summary>Opens Point &amp; Ask in front of the user, starts the camera and loads the vision model.</summary>
        public void Open()
        {
            if (m_Panel == null)
                Build();
            if (m_Service == null)
                m_Service = LlmService.Instance;

            m_Panel.gameObject.SetActive(true);
            m_CardRoot.gameObject.SetActive(true);
            UiFactory.PlaceInFront(transform, m_Distance, -0.3f);
            transform.rotation *= Quaternion.Euler(20f, 0f, 0f); // tilt up toward the eyes
            m_Camera.StartStreaming();
            m_Gesture.enabled = true;
            Subscribe();
            _ = PrepareAsync();
        }

        /// <summary>Closes Point &amp; Ask and stops the camera. Answer cards are hidden, not deleted.</summary>
        public void Close()
        {
            if (m_Pending != null)
                m_Service.Engine.Cancel();
            FinishPending("Closed");
            SpeechEngine.Instance?.Stop();

            Unsubscribe();
            m_Gesture.enabled = false;
            m_Camera.StopStreaming();
            m_Panel.gameObject.SetActive(false);
            m_CardRoot.gameObject.SetActive(false);
            Closed?.Invoke();
        }

        /// <summary>Asks about the region straight ahead of the user (the Capture button).</summary>
        public void AskInFront(string question = null)
        {
            var head = Camera.main.transform;
            var region = FramedRegion.InFront(new Pose(head.position, head.rotation), k_CaptureDistance, k_CaptureSize);
            Ask(region, question ?? m_Question.text);
        }

        /// <summary>Crops the framed region from the camera image and asks Gemma about it.</summary>
        public async void Ask(FramedRegion region, string question)
        {
            question = string.IsNullOrWhiteSpace(question) ? k_Questions[0] : question.Trim();
            if (m_Pending != null)
            {
                SetStatus("Wait for the current answer first.");
                return;
            }

            if (!await PrepareAsync())
                return;

            if (!m_Camera.IsReady)
            {
                SetStatus("The camera is still starting. Try again in a moment.");
                return;
            }

            // Project the frame into the camera image, using the camera pose of the latest frame.
            var cameraPose = m_Camera.GetWorldCameraPose();
            var eye = Camera.main.transform.position;
            var corners = ImageRegion.ExtendFromEye(eye, region.Corners, k_MinProjectionDistance);
            if (!ImageRegion.AllInFront(cameraPose, corners))
            {
                SetStatus("Frame something in front of you.");
                return;
            }

            var viewportPoints = new Vector2[corners.Length];
            for (var i = 0; i < corners.Length; i++)
                viewportPoints[i] = m_Camera.CameraAccess.WorldToViewportPoint(corners[i], cameraPose);
            var viewport = ImageRegion.ViewportBounds(viewportPoints);
            if (viewport.width <= 0f)
            {
                SetStatus("That's outside the camera's view.");
                return;
            }

            // Depth: tighten the crop to the object under the frame's center and measure it (PortPlan §4.1, §4.2).
            if (m_Depth == null)
                m_Depth = DepthService.Instance;
            DepthBlob blob = null;
            if (m_Depth != null && m_Depth.IsReady)
            {
                var grid = DepthPlacement.SampleGrid(m_Depth, m_Camera.CameraAccess, cameraPose, viewport, m_DepthGrid);
                blob = DepthRegion.Analyze(grid, viewport, cameraPose);
                if (blob != null && !blob.FillsFrame)
                    viewport = blob.Viewport;
            }

            var crop = ImageRegion.ToPixels(viewport, m_Camera.Resolution, k_CropPadding, k_MinCropPixels);
            var path = Path.Combine(Application.temporaryCachePath, $"ask_{m_ShotCount++ % k_MaxCards}.png");
            var image = m_Camera.CaptureCrop(crop, path);
            if (image == null)
            {
                SetStatus("Couldn't read the camera image.");
                return;
            }

            if (m_Cards.Count >= k_MaxCards)
                m_Cards[0].Close();
            var card = AnswerCard.Create(m_CardRoot, image, question, region);
            if (blob != null)
            {
                card.SetMeasurement(DepthRegion.Label(blob));
                card.PinBeside(blob.Center, blob.Width);
            }
            card.Closed += c => m_Cards.Remove(c);
            m_Cards.Add(card);

            var facts = blob != null ? DepthRegion.Describe(blob) : "";
            m_Pending = new AskResult { Question = question, Crop = crop, ImagePath = path, Depth = blob, Facts = facts };
            m_PendingCard = card;
            m_Stream.Clear();
            RefreshControls();
            SetStatus($"Asking about a {crop.width}×{crop.height} crop…");

            // Each question is its own single-turn conversation, as in the Gallery's Ask Image.
            m_Service.NewConversation();
            var prompt = facts.Length > 0 ? $"{facts} Use these measurements if the question is about size or distance.\n\n{question}" : question;
            SpeechEngine.Instance?.BeginReply();
            m_Service.Engine.Send(prompt, path);
        }

        void Update()
        {
            if (m_StreamDirty && m_PendingCard != null && Time.unscaledTime >= m_NextRender)
            {
                m_StreamDirty = false;
                m_NextRender = Time.unscaledTime + k_RenderInterval;
                m_PendingCard.SetAnswer(MarkdownToTmp.Convert(m_Stream.ToString()));
            }
        }

        async Awaitable<bool> PrepareAsync()
        {
            if (m_ModelReady && m_Service.LoadedModel == Model && m_Service.LoadedWithVision)
                return true;
            if (m_Preparing)
                return false;

            m_Preparing = true;
            m_ModelReady = false;
            RefreshControls();
            try
            {
                if (ModelStore.GetState(Model) != ModelState.Downloaded)
                {
                    SetStatus($"Download {Model.displayName} from the launcher first.");
                    return false;
                }

                while (m_Service.IsLoading)
                    await Awaitable.NextFrameAsync();

                SetStatus($"Loading {Model.displayName} with vision…");
                var info = await m_Service.LoadAsync(Model, withVision: true);
                m_ModelReady = true;
                SetStatus($"{Model.displayName} · {info.backend} · vision {info.visionBackend}. Frame something with both hands.");
                return true;
            }
            catch (Exception e)
            {
                SetStatus($"<color=#E07070>Couldn't load {Model.displayName}: {MarkdownToTmp.Escape(e.Message)}</color>");
                return false;
            }
            finally
            {
                m_Preparing = false;
                RefreshControls();
            }
        }

        void Subscribe()
        {
            Unsubscribe();
            m_Service.Engine.TokenReceived += OnToken;
            m_Service.Engine.ResponseDone += OnDone;
            m_Service.Engine.Failed += OnFailed;
            m_Service.LoadedModelChanged += OnLoadedModelChanged;
        }

        void Unsubscribe()
        {
            if (m_Service == null)
                return;

            m_Service.Engine.TokenReceived -= OnToken;
            m_Service.Engine.ResponseDone -= OnDone;
            m_Service.Engine.Failed -= OnFailed;
            m_Service.LoadedModelChanged -= OnLoadedModelChanged;
        }

        void OnLoadedModelChanged(ModelInfo model) => m_ModelReady = false;

        void OnToken(string text)
        {
            if (m_Pending == null)
                return;

            m_Stream.Append(text);
            m_StreamDirty = true;
            SpeechEngine.Instance?.AppendReply(text);
        }

        void OnDone(LlmStats stats)
        {
            if (m_Pending == null)
                return;

            m_Pending.Stats = stats;
            SpeechEngine.Instance?.EndReply();
            m_PendingCard.SetAnswer(MarkdownToTmp.Convert(m_Stream.ToString()));
            m_PendingCard.SetStats(stats.ToString());
            SetStatus("Frame something else, or press Capture.");
            FinishPending(null);
        }

        void OnFailed(string error)
        {
            if (m_Pending == null)
                return;

            SpeechEngine.Instance?.Stop();
            m_PendingCard.SetAnswer($"<color=#C04040>Error: {MarkdownToTmp.Escape(error)}</color>");
            SetStatus("The question failed.");
            FinishPending(error);
        }

        void FinishPending(string error)
        {
            if (m_Pending == null)
                return;

            var result = m_Pending;
            result.Answer = m_Stream.ToString();
            result.Error = error;
            m_Pending = null;
            m_PendingCard = null;
            m_StreamDirty = false;
            RefreshControls();
            AnswerFinished?.Invoke(result);
        }

        void OnFramed(FramedRegion region) => Ask(region, m_Question.text);

        void RefreshControls()
        {
            if (m_CaptureButton == null)
                return;

            m_CaptureButton.interactable = m_Pending == null && !m_Preparing;
            UiFactory.SetLabel(m_CaptureButton, m_Pending != null ? "Answering…" : "Capture");
        }

        void SetStatus(string text)
        {
            if (m_Status != null)
                m_Status.text = text;
            Debug.Log($"[AIEdge] Point & Ask: {text}");
        }

        void Build()
        {
            m_Panel = UiFactory.CreatePanel(transform, "AskImagePanel", new Vector2(k_Width, k_Height));

            var title = UiFactory.CreateText(m_Panel, "Title", "Point & Ask", 30);
            title.fontStyle = FontStyles.Bold;
            UiFactory.Place(title.rectTransform, 24, 16, 500, 42);
            var close = UiFactory.CreateButton(m_Panel, "Close", UiFactory.CardColor, Close, 22);
            UiFactory.Place((RectTransform)close.transform, k_Width - 116, 18, 92, 50);
            var voice = VoiceToggle.Create(m_Panel, 18);
            UiFactory.Place((RectTransform)voice.transform, k_Width - 226, 18, 102, 50);

            m_Status = UiFactory.CreateText(m_Panel, "Status", "", 18);
            m_Status.color = UiFactory.MutedTextColor;
            UiFactory.Place(m_Status.rectTransform, 24, 64, k_Width - 48, 50);

            var how = UiFactory.CreateText(m_Panel, "HowTo",
                "Pinch with both hands and pull them apart to frame something, then let go. " +
                "Or press Capture to ask about what's straight ahead.", 18);
            UiFactory.Place(how.rectTransform, 24, 116, k_Width - 48, 52);

            var label = UiFactory.CreateText(m_Panel, "QuestionLabel", "Question", 18);
            label.color = UiFactory.MutedTextColor;
            UiFactory.Place(label.rectTransform, 24, 176, 300, 26);
            m_Question = CreateInputField(m_Panel);
            m_Question.text = k_Questions[0];
            UiFactory.Place((RectTransform)m_Question.transform, 24, 204, k_Width - 48, 64);

            for (var i = 0; i < k_Questions.Length; i++)
            {
                var question = k_Questions[i];
                var chip = UiFactory.CreateButton(m_Panel, question, UiFactory.CardColor, () => m_Question.text = question, 17);
                UiFactory.Place((RectTransform)chip.transform, 24 + (i % 2) * 360, 282 + (i / 2) * 60, 352, 52);
            }

            m_CaptureButton = UiFactory.CreateButton(m_Panel, "Capture", UiFactory.AccentColor, () => AskInFront(), 24);
            UiFactory.Place((RectTransform)m_CaptureButton.transform, 24, 404, 352, 54);
            var clear = UiFactory.CreateButton(m_Panel, "Clear answers", UiFactory.CardColor, ClearCards, 22);
            UiFactory.Place((RectTransform)clear.transform, 384, 404, 352, 54);

            m_CardRoot = new GameObject("AskImageCards").transform;
            RefreshControls();
        }

        void ClearCards()
        {
            foreach (var card in m_Cards.ToArray())
                if (card != m_PendingCard)
                    card.Close();
        }

        static TMP_InputField CreateInputField(Transform parent)
        {
            var background = UiFactory.CreateImage(parent, "Question", UiFactory.CardColor);
            var area = new GameObject("Text Area", typeof(RectTransform), typeof(RectMask2D));
            area.transform.SetParent(background.transform, false);
            var areaRect = (RectTransform)area.transform;
            UiFactory.Stretch(areaRect, 16, 16, 10, 10);

            var hint = UiFactory.CreateText(areaRect, "Placeholder", "What is this?", 22);
            hint.color = UiFactory.MutedTextColor;
            hint.fontStyle = FontStyles.Italic;
            UiFactory.Stretch(hint.rectTransform);
            var text = UiFactory.CreateText(areaRect, "Text", "", 22);
            UiFactory.Stretch(text.rectTransform);

            var field = background.gameObject.AddComponent<TMP_InputField>();
            field.targetGraphic = background;
            field.textViewport = areaRect;
            field.textComponent = text;
            field.placeholder = hint;
            field.fontAsset = text.font;
            field.pointSize = 22;
            field.lineType = TMP_InputField.LineType.SingleLine;
            field.richText = false;
            return field;
        }
    }
}
