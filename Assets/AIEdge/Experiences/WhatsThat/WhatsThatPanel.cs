using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AIEdge.AskImage;
using AIEdge.Spatial;
using AIEdge.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AIEdge.WhatsThat
{
    /// <summary>The result of one What's That? question.</summary>
    public class WhatsThatResult
    {
        public PointerTarget Target;

        /// <summary>Distance from the user's eyes to the pointed spot, in meters.</summary>
        public float Distance;

        /// <summary>Depth measurements of the object, or null.</summary>
        public DepthBlob Depth;

        public string Name = "";
        public string Description = "";
        public string Reply = "";
        public string ImagePath;
        public LlmStats Stats;
        public string Error;

        /// <summary>Where the object's highlight came from: "depth", "segmentation", or "" for none.</summary>
        public string HighlightSource = "";

        /// <summary>The segmentation of the pointed object (set when it finishes, which may be after the answer).</summary>
        public SegmentationResult Segmentation;

        /// <summary>True once segmentation finished or was skipped.</summary>
        public bool SegmentationDone;
    }

    /// <summary>
    /// What's That?: point at something in the room with a hand or a controller. The depth sensor finds the spot and
    /// measures the object, the passthrough camera image around it goes to Gemma, and the answer, the object's name
    /// and a short description, appears on a card pinned beside it, with its distance from you. The answer is read
    /// aloud, followed by the distance. The object is highlighted: by its depth outline at once, then by an image
    /// cut-out from interactive segmentation (<see cref="ObjectSegmenter"/>).
    /// </summary>
    public class WhatsThatPanel : MonoBehaviour
    {
        const float k_Width = 640f;
        const float k_Height = 330f;
        const float k_CropPadding = 0.1f;
        const int k_MinCropPixels = 224;
        const float k_RenderInterval = 0.06f;
        const int k_MaxCards = 5;
        const string k_LanguageKey = "AIEdge.WhatsThat.Language";

        [SerializeField, Tooltip("Model service. Uses LlmService.Instance if empty.")]
        LlmService m_Service;

        [SerializeField, Tooltip("Passthrough camera frame source. Found in the scene if empty.")]
        PassthroughFrameCapture m_Camera;

        [SerializeField, Tooltip("Room depth. Uses DepthService.Instance if empty.")]
        DepthService m_Depth;

        [SerializeField, Tooltip("Rays per side of the depth grid cast around the pointed spot.")]
        int m_DepthGrid = 11;

        [SerializeField, Tooltip("Distance from the user's head when opened, in meters.")]
        float m_Distance = 0.8f;

        /// <summary>Raised when the user closes What's That?.</summary>
        public event Action Closed;

        /// <summary>Raised when an answer finishes or fails.</summary>
        public event Action<WhatsThatResult> AnswerFinished;

        /// <summary>True when the vision model is loaded, the camera streams and no question is running.</summary>
        public bool IsReady => m_ModelReady && m_Camera != null && m_Camera.IsReady && m_Pending == null && !m_Preparing;

        /// <summary>True while the panel is open.</summary>
        public bool IsOpen => m_Panel != null && m_Panel.gameObject.activeSelf;

        /// <summary>Language of the answers: "en" or "it". Remembered between sessions.</summary>
        public string Language
        {
            get => PlayerPrefs.GetString(k_LanguageKey, SpeechLanguage.English);
            set
            {
                PlayerPrefs.SetString(k_LanguageKey, value);
                PlayerPrefs.Save();
                RefreshControls();
            }
        }

        ModelInfo Model => ModelCatalog.Gemma4E2B;

        ObjectPointer m_Pointer;
        ObjectSegmenter m_Segmenter;
        readonly Dictionary<AnswerCard, ObjectHighlight> m_Highlights = new Dictionary<AnswerCard, ObjectHighlight>();
        RectTransform m_Panel;
        TextMeshProUGUI m_Status;
        Button m_English;
        Button m_Italian;
        Transform m_CardRoot;
        readonly List<AnswerCard> m_Cards = new List<AnswerCard>();
        readonly StringBuilder m_Stream = new StringBuilder();
        WhatsThatResult m_Pending;
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
            m_Pointer = new GameObject("WhatsThatPointer").AddComponent<ObjectPointer>();
            m_Pointer.transform.SetParent(transform, false);
            m_Pointer.enabled = false;
            m_Segmenter = gameObject.AddComponent<ObjectSegmenter>();
        }

        void OnEnable() => m_Pointer.Selected += OnSelected;

        void OnDisable() => m_Pointer.Selected -= OnSelected;

        void OnDestroy()
        {
            Unsubscribe();
            if (m_CardRoot != null)
                Destroy(m_CardRoot.gameObject);
        }

        /// <summary>Opens What's That? in front of the user, starts the camera and loads the vision model.</summary>
        public void Open()
        {
            if (m_Panel == null)
                Build();
            if (m_Service == null)
                m_Service = LlmService.Instance;
            if (m_Camera == null)
                m_Camera = FindAnyObjectByType<PassthroughFrameCapture>();

            m_Panel.gameObject.SetActive(true);
            m_CardRoot.gameObject.SetActive(true);
            // Low and tilted up, out of the way of what the user points at.
            UiFactory.PlaceInFront(transform, m_Distance, -0.35f);
            transform.rotation *= Quaternion.Euler(25f, 0f, 0f);
            m_Camera.StartStreaming();
            m_Pointer.enabled = true;
            Subscribe();
            _ = PrepareAsync();
        }

        /// <summary>Closes What's That? and stops the camera. Answer cards are hidden, not deleted.</summary>
        public void Close()
        {
            if (m_Pending != null)
                m_Service.Engine.Cancel();
            FinishPending("Closed");
            SpeechEngine.Instance?.Stop();

            Unsubscribe();
            m_Pointer.enabled = false;
            m_Camera.StopStreaming();
            m_Panel.gameObject.SetActive(false);
            m_CardRoot.gameObject.SetActive(false);
            Closed?.Invoke();
        }

        /// <summary>
        /// Asks about whatever a ray points at (the pointer calls this on selection; tests call it with a gaze ray).
        /// Returns false if the depth sensor finds nothing along the ray; otherwise the answer arrives through
        /// <see cref="AnswerFinished"/> (or the status line says why it couldn't ask).
        /// </summary>
        public bool AskAlong(Ray ray, string source = "test")
        {
            if (m_Depth == null)
                m_Depth = DepthService.Instance;
            if (m_Depth == null || !m_Depth.IsReady || !m_Depth.Raycast(ray, out var hit, 6f))
            {
                SetStatus("Nothing found there. Point at a nearby object.");
                return false;
            }

            Ask(new PointerTarget { Ray = ray, Point = hit.point, Normal = hit.normal, Source = source });
            return true;
        }

        void OnSelected(PointerTarget target) => Ask(target);

        async void Ask(PointerTarget target)
        {
            if (m_Pending != null || m_Preparing)
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

            if (m_Depth == null)
                m_Depth = DepthService.Instance;
            if (m_Depth == null || !m_Depth.IsReady)
            {
                SetStatus("Depth is still starting. Try again in a moment.");
                return;
            }

            var eye = Camera.main.transform.position;
            var distance = Vector3.Distance(eye, target.Point);

            // Project a square around the pointed spot into the camera image, facing the user.
            var cameraPose = m_Camera.GetWorldCameraPose();
            var size = PointerMath.RegionSize(distance);
            var facing = Quaternion.LookRotation(target.Point - eye, Vector3.up);
            var region = new FramedRegion
            {
                Center = target.Point,
                Rotation = facing,
                Size = new Vector2(size, size),
                Corners = new[]
                {
                    // FramedRegion order: bottom-left, top-left, top-right, bottom-right.
                    target.Point + facing * new Vector3(-size / 2f, -size / 2f, 0f),
                    target.Point + facing * new Vector3(-size / 2f, size / 2f, 0f),
                    target.Point + facing * new Vector3(size / 2f, size / 2f, 0f),
                    target.Point + facing * new Vector3(size / 2f, -size / 2f, 0f),
                },
            };

            if (!ImageRegion.AllInFront(cameraPose, region.Corners))
            {
                SetStatus("Point at something in front of you.");
                return;
            }

            var viewportPoints = new Vector2[region.Corners.Length];
            for (var i = 0; i < region.Corners.Length; i++)
                viewportPoints[i] = m_Camera.CameraAccess.WorldToViewportPoint(region.Corners[i], cameraPose);
            var viewport = ImageRegion.ViewportBounds(viewportPoints);
            if (viewport.width <= 0f)
            {
                SetStatus("That's outside the camera's view. Look toward what you point at.");
                return;
            }

            // Depth: find the object under the pointed spot, measure it, and tighten the crop to it.
            var grid = DepthPlacement.SampleGrid(m_Depth, m_Camera.CameraAccess, cameraPose, viewport, m_DepthGrid);
            var blob = DepthRegion.Analyze(grid, viewport, cameraPose);
            if (blob != null && !blob.FillsFrame)
                viewport = blob.Viewport;

            var crop = ImageRegion.ToPixels(viewport, m_Camera.Resolution, k_CropPadding, k_MinCropPixels);
            var path = Path.Combine(Application.temporaryCachePath, $"whatsthat_{m_ShotCount++ % k_MaxCards}.png");
            var image = m_Camera.CaptureCrop(crop, path);
            if (image == null)
            {
                SetStatus("Couldn't read the camera image.");
                return;
            }

            if (m_Cards.Count >= k_MaxCards)
                m_Cards[0].Close();
            var card = AnswerCard.Create(m_CardRoot, image, Language == SpeechLanguage.Italian ? "Cos'è?" : "What's that?", region);
            var label = blob != null && !blob.FillsFrame
                ? $"{DepthRegion.FormatLength(distance)} away · about {DepthRegion.FormatLength(blob.Width)} x {DepthRegion.FormatLength(blob.Height)}"
                : $"{DepthRegion.FormatLength(distance)} away";
            card.SetMeasurement(label);
            card.PinBeside(blob != null && !blob.FillsFrame ? blob.Center : target.Point, blob != null && !blob.FillsFrame ? blob.Width : 0.1f);
            card.Closed += OnCardClosed;
            m_Cards.Add(card);

            var facts = blob != null && !blob.FillsFrame
                ? $"The headset's depth sensor measured it as about {DepthRegion.FormatLength(blob.Width)} wide and {DepthRegion.FormatLength(blob.Height)} tall."
                : "";
            m_Pending = new WhatsThatResult { Target = target, Distance = distance, Depth = blob, ImagePath = path };
            HighlightObject(m_Pending, card, target, eye, cameraPose, crop);
            m_PendingCard = card;
            m_Pointer.CanSelect = false;
            m_Stream.Clear();
            SetStatus($"Looking at what you pointed at, {DepthRegion.FormatLength(distance)} away…");

            m_Service.NewConversation();
            SpeechEngine.Instance?.BeginReply();
            m_Service.Engine.Send(PointerMath.Prompt(facts, Language), path);
        }

        // The selected object's highlight: the pointer's live depth outline (or a fresh one) right away, then the
        // segmentation cut-out of the camera crop once it is ready. It lives as long as the card.
        void HighlightObject(WhatsThatResult result, AnswerCard card, PointerTarget target, Vector3 eye, Pose cameraPose, RectInt crop)
        {
            var highlight = m_Pointer.TakeHighlight();
            if (highlight == null)
            {
                highlight = ObjectHighlight.Create("ObjectHighlight");
                if (!ObjectPointer.TryDepthOutline(m_Depth, eye, target.Point, 24, highlight))
                    highlight.SetVisible(false);
            }

            highlight.transform.SetParent(m_CardRoot, true);
            highlight.SetStyle(ObjectHighlight.SelectedColor, true);
            m_Highlights[card] = highlight;
            result.HighlightSource = highlight.gameObject.activeSelf ? "depth" : "";

            // Where the pointed spot falls in the crop, normalized with the origin at the top-left (PNG order).
            var viewport = m_Camera.CameraAccess.WorldToViewportPoint(target.Point, cameraPose);
            var resolution = m_Camera.Resolution;
            var keypoint = new Vector2(
                (viewport.x * resolution.x - crop.x) / crop.width,
                1f - (viewport.y * resolution.y - crop.y) / crop.height);
            _ = SegmentAsync(result, card, highlight, target.Point, cameraPose, crop, keypoint);
        }

        async Awaitable SegmentAsync(WhatsThatResult result, AnswerCard card, ObjectHighlight highlight, Vector3 point,
            Pose cameraPose, RectInt crop, Vector2 keypoint)
        {
            var segmentation = await m_Segmenter.SegmentAsync(result.ImagePath, keypoint);
            result.Segmentation = segmentation;
            result.SegmentationDone = true;
            Debug.Log($"[AIEdge] What's That? segmentation: {segmentation.Error ?? segmentation.Info}");
            if (highlight == null || !m_Highlights.ContainsKey(card) || !segmentation.IsUsable)
                return;

            // The crop's corners, cast from the camera onto the plane through the object facing the camera.
            var forward = cameraPose.rotation * Vector3.forward;
            var depth = Vector3.Dot(point - cameraPose.position, forward);
            var resolution = (Vector2)m_Camera.Resolution;
            Vector3 Corner(float x, float y)
            {
                var ray = m_Camera.CameraAccess.ViewportPointToRay(new Vector2(x / resolution.x, y / resolution.y), cameraPose);
                return ray.origin + ray.direction * (depth / Mathf.Max(0.1f, Vector3.Dot(ray.direction, forward)));
            }

            var bottomLeft = Corner(crop.xMin, crop.yMin);
            var bottomRight = Corner(crop.xMax, crop.yMin);
            var topLeft = Corner(crop.xMin, crop.yMax);
            var topRight = Corner(crop.xMax, crop.yMax);
            var center = (bottomLeft + bottomRight + topLeft + topRight) / 4f;
            var size = new Vector2(Vector3.Distance(bottomLeft, bottomRight), Vector3.Distance(bottomLeft, topLeft));
            highlight.SetPlane(center, Quaternion.LookRotation(forward, cameraPose.rotation * Vector3.up), size);
            highlight.ShowSegmentation(segmentation.Mask, segmentation.Size, segmentation.Coverage);
            result.HighlightSource = "segmentation";
        }

        void OnCardClosed(AnswerCard card)
        {
            m_Cards.Remove(card);
            if (m_Highlights.Remove(card, out var highlight) && highlight != null)
                Destroy(highlight.gameObject);
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
                await m_Service.LoadAsync(Model, withVision: true);
                m_ModelReady = true;
                SetStatus(Instructions);
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
            }
        }

        const string Instructions = "Point at something with your index finger and hold still, or aim a controller and pull the trigger.";

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

            var speech = SpeechEngine.Instance;
            speech?.EndReply();
            // The distance is measured, not guessed by the model: say it after the description, in the reply's language.
            speech?.Speak(PointerMath.SpokenDistance(m_Pending.Distance, Language), language: Language);

            m_Pending.Stats = stats;
            m_PendingCard.SetAnswer(MarkdownToTmp.Convert(m_Stream.ToString()));
            m_PendingCard.SetStats(stats.ToString());
            SetStatus(Instructions);
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
            result.Reply = m_Stream.ToString();
            (result.Name, result.Description) = PointerMath.ParseAnswer(result.Reply);
            result.Error = error;
            m_Pending = null;
            m_PendingCard = null;
            m_StreamDirty = false;
            m_Pointer.CanSelect = true;
            if (error == null)
                Debug.Log($"[AIEdge] What's That?: {result.Name} ({DepthRegion.FormatLength(result.Distance)}, {result.Target.Source})");
            AnswerFinished?.Invoke(result);
        }

        void RefreshControls()
        {
            if (m_English == null)
                return;
            var italian = Language == SpeechLanguage.Italian;
            m_English.GetComponent<Image>().color = italian ? UiFactory.CardColor : UiFactory.AccentColor;
            m_Italian.GetComponent<Image>().color = italian ? UiFactory.AccentColor : UiFactory.CardColor;
        }

        void SetStatus(string text)
        {
            if (m_Status != null)
                m_Status.text = text;
            Debug.Log($"[AIEdge] What's That?: {text}");
        }

        void Build()
        {
            m_Panel = UiFactory.CreatePanel(transform, "WhatsThatPanel", new Vector2(k_Width, k_Height));

            var title = UiFactory.CreateText(m_Panel, "Title", "What's That?", 30);
            title.fontStyle = FontStyles.Bold;
            UiFactory.Place(title.rectTransform, 24, 16, 380, 42);
            var close = UiFactory.CreateButton(m_Panel, "Close", UiFactory.CardColor, Close, 22);
            UiFactory.Place((RectTransform)close.transform, k_Width - 116, 18, 92, 50);
            var voice = VoiceToggle.Create(m_Panel, 18);
            UiFactory.Place((RectTransform)voice.transform, k_Width - 226, 18, 102, 50);

            var how = UiFactory.CreateText(m_Panel, "HowTo",
                "Point at an object in the room. The headset measures where it is with depth, and Gemma names it, " +
                "describes it and tells you how far away it is.", 19);
            UiFactory.Place(how.rectTransform, 24, 72, k_Width - 48, 80);

            m_Status = UiFactory.CreateText(m_Panel, "Status", "", 18);
            m_Status.color = UiFactory.MutedTextColor;
            UiFactory.Place(m_Status.rectTransform, 24, 156, k_Width - 48, 56);

            var language = UiFactory.CreateText(m_Panel, "LanguageLabel", "Answer in", 18);
            language.color = UiFactory.MutedTextColor;
            UiFactory.Place(language.rectTransform, 24, 236, 120, 50);
            m_English = UiFactory.CreateButton(m_Panel, "English", UiFactory.CardColor, () => Language = SpeechLanguage.English, 20);
            UiFactory.Place((RectTransform)m_English.transform, 128, 226, 140, 50);
            m_Italian = UiFactory.CreateButton(m_Panel, "Italiano", UiFactory.CardColor, () => Language = SpeechLanguage.Italian, 20);
            UiFactory.Place((RectTransform)m_Italian.transform, 276, 226, 140, 50);
            var clear = UiFactory.CreateButton(m_Panel, "Clear", UiFactory.CardColor, ClearCards, 20);
            UiFactory.Place((RectTransform)clear.transform, k_Width - 184, 226, 160, 50);

            var hint = UiFactory.CreateText(m_Panel, "Hint", "Hand: point and hold · Controller: trigger", 16);
            hint.color = UiFactory.MutedTextColor;
            UiFactory.Place(hint.rectTransform, 24, 288, k_Width - 48, 30);

            m_CardRoot = new GameObject("WhatsThatCards").transform;
            RefreshControls();
        }

        void ClearCards()
        {
            foreach (var card in m_Cards.ToArray())
                if (card != m_PendingCard)
                    card.Close();
        }
    }
}
