using System;
using System.Collections.Generic;
using System.Text;
using AIEdge.Spatial;
using AIEdge.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace AIEdge.Chat
{
    /// <summary>
    /// Spatial AI Chat: a world-locked chat panel for multi-turn conversations with Gemma, with streaming answers,
    /// per-answer stats and the system keyboard. Saved conversations float beside the panel as cards you grab to
    /// resume and throw away to delete. Grab the bar under the panel to move it.
    /// </summary>
    public class ChatPanel : MonoBehaviour
    {
        const float k_Width = 960f;
        const float k_Height = 780f;
        const float k_RenderInterval = 0.06f;
        const float k_WallMinDistance = 0.7f;
        const float k_WallMaxDistance = 1.8f;
        const int k_MaxCards = 8;
        const float k_CardColumnSpacing = 0.2f;
        const float k_CardRowSpacing = 0.13f;

        static readonly string[] k_Suggestions =
        {
            "What can you help me with?",
            "Explain mixed reality in one paragraph.",
            "Write a haiku about the room I'm in.",
            "Give me a 5-minute stretching routine.",
        };

        static readonly (string Name, string Prompt)[] k_Personas =
        {
            ("None", ""),
            ("Concise", "Answer in at most three short sentences."),
            ("Tutor", "You are a patient tutor. Explain step by step with simple examples, then check understanding with one question."),
            ("Spatial guide", "You are a helpful assistant running on a mixed reality headset. The user sees their real room around them. Keep answers short and practical."),
        };

        [SerializeField, Tooltip("Model service. Uses LlmService.Instance if empty.")]
        LlmService m_Service;

        [SerializeField, Tooltip("Distance from the user's head when opened, in meters.")]
        float m_Distance = 1.0f;

        /// <summary>Raised when the user closes the chat.</summary>
        public event Action Closed;

        /// <summary>Raised when an answer finishes, is stopped or fails.</summary>
        public event Action<ChatMessage> ResponseFinished;

        /// <summary>The thread shown in the panel.</summary>
        public ChatThread CurrentThread => m_Thread;

        /// <summary>True while an answer is streaming.</summary>
        public bool IsGenerating => m_Reply != null;

        /// <summary>True when the model is loaded and the conversation matches the current thread.</summary>
        public bool IsReady => m_ConversationReady && !m_Preparing;

        /// <summary>True while the panel is open.</summary>
        public bool IsOpen => m_Panel != null && m_Panel.gameObject.activeSelf;

        class Bubble
        {
            public Image Background;
            public TextMeshProUGUI Text;
            public TextMeshProUGUI Stats;
        }

        ModelInfo Model => ModelCatalog.Gemma4E2B;

        RectTransform m_Panel;
        TextMeshProUGUI m_Title;
        TextMeshProUGUI m_Status;
        TextMeshProUGUI m_Footer;
        ScrollRect m_Scroll;
        RectTransform m_Content;
        GameObject m_Suggestions;
        TMP_InputField m_Input;
        Button m_SendButton;
        Button m_NewButton;
        Button m_PersonaButton;
        GameObject m_PersonaEditor;
        TMP_InputField m_PersonaInput;
        Transform m_CardRoot;
        XRGrabInteractable m_Handle;

        readonly List<Bubble> m_Bubbles = new List<Bubble>();
        readonly List<ThreadCard> m_Cards = new List<ThreadCard>();
        readonly StringBuilder m_Stream = new StringBuilder();
        ChatThread m_Thread;
        ChatMessage m_Reply;
        Bubble m_ReplyBubble;
        bool m_ConversationReady;
        bool m_Preparing;
        bool m_StreamDirty;
        float m_NextRender;
        bool m_ScrollToBottom;
        int m_LastKvTokens;

        void Awake()
        {
            if (m_Service == null)
                m_Service = LlmService.Instance;
            UserInstructions.Changed += OnInstructionsChanged;
            AppLanguage.Changed += OnInstructionsChanged;
        }

        // New instructions reach the system prompt: rebuild the conversation (from history) before the next message.
        void OnInstructionsChanged()
        {
            if (IsGenerating)
                return;
            m_ConversationReady = false;
            if (IsOpen)
                _ = PrepareAsync();
        }

        void OnDestroy()
        {
            UserInstructions.Changed -= OnInstructionsChanged;
            AppLanguage.Changed -= OnInstructionsChanged;
            Unsubscribe();
            if (m_CardRoot != null)
                Destroy(m_CardRoot.gameObject);
        }

        /// <summary>Opens the chat in front of the user on the most recent thread.</summary>
        public void Open()
        {
            if (m_Panel == null)
                Build();

            if (m_Service == null)
                m_Service = LlmService.Instance;

            m_Panel.gameObject.SetActive(true);
            m_CardRoot.gameObject.SetActive(true);
            m_Handle.enabled = true;
            UiFactory.PlaceInFront(transform, m_Distance);
            PlaceOnRoom();

            Subscribe();
            m_ConversationReady = false;

            var threads = ChatThreadStore.LoadAll();
            ShowThread(threads.Count > 0 ? threads[0] : ChatThread.Create());
            RebuildCards(threads);
            _ = PrepareAsync();
        }

        /// <summary>How the panel was last placed, for tests.</summary>
        public string PlacementNote { get; private set; } = "";

        // Pins the panel to the wall ahead when one is at reading distance, and keeps it out of furniture
        // (PortPlan §4.4). Cards are laid out afterwards, so they follow the panel.
        void PlaceOnRoom()
        {
            var depth = DepthService.Instance;
            var pinned = DepthPlacement.TrySnapToWall(depth, transform, k_WallMinDistance, k_WallMaxDistance, -0.1f);
            var pushed = DepthPlacement.PushOutOfGeometry(depth, transform, new Vector2(k_Width, k_Height) * 0.001f);
            PlacementNote = depth == null || !depth.IsReady ? "no depth"
                : (pinned ? "pinned to the wall" : "floating") + (pushed > 0f ? $", moved {DepthRegion.FormatLength(pushed)} out of geometry" : "");
            Debug.Log($"[AIEdge] Chat panel: {PlacementNote}");
        }

        /// <summary>Closes the chat, stopping any answer in progress.</summary>
        public void Close()
        {
            AbandonReply();
            SpeechEngine.Instance?.Stop();
            Unsubscribe();
            m_Panel.gameObject.SetActive(false);
            m_CardRoot.gameObject.SetActive(false);
            m_Handle.enabled = false;
            Closed?.Invoke();
        }

        /// <summary>Starts an empty thread.</summary>
        public void NewChat()
        {
            if (IsGenerating)
                return;

            ShowThread(ChatThread.Create(m_Thread?.systemPrompt));
            m_ConversationReady = false;
            RebuildCards(ChatThreadStore.LoadAll());
            _ = PrepareAsync();
        }

        /// <summary>Resumes a saved thread.</summary>
        public void OpenThread(ChatThread thread)
        {
            if (IsGenerating)
            {
                SetStatus("Stop the answer first.");
                return;
            }

            ShowThread(thread);
            m_ConversationReady = false;
            foreach (var card in m_Cards)
                card.SetCurrent(card.Thread.id == thread.id);
            _ = PrepareAsync();
        }

        /// <summary>Sends a user message and streams the answer.</summary>
        public async void Submit(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0 || IsGenerating)
                return;
            if (m_Preparing)
            {
                SetStatus($"Still loading {Model.displayName}…");
                return;
            }

            if (!await PrepareAsync())
                return;

            m_Input.text = "";
            m_Thread.Add(ChatRole.User, text);
            AddBubble(m_Thread.messages[m_Thread.messages.Count - 1]);
            ChatThreadStore.Save(m_Thread);

            m_Reply = new ChatMessage { role = ChatRole.Model };
            m_ReplyBubble = AddBubble(m_Reply);
            m_ReplyBubble.Text.text = "<color=#B3BACC>…</color>";
            m_Stream.Clear();
            m_ScrollToBottom = true;
            RefreshControls();

            SpeechEngine.Instance?.BeginReply(AppLanguage.Current);
            m_Service.Engine.Send(AppLanguage.ForModel(text));
        }

        /// <summary>Stops the answer in progress.</summary>
        public void Stop()
        {
            if (IsGenerating)
                m_Service.Engine.Cancel();
        }

        void Update()
        {
            if (m_StreamDirty && m_ReplyBubble != null && Time.unscaledTime >= m_NextRender)
            {
                m_StreamDirty = false;
                m_NextRender = Time.unscaledTime + k_RenderInterval;
                m_ReplyBubble.Text.text = MarkdownToTmp.Convert(m_Stream.ToString());
                m_ScrollToBottom = true;
            }

            if (m_Handle != null && m_Handle.isSelected)
                FaceUser();
        }

        void LateUpdate()
        {
            if (!m_ScrollToBottom || m_Scroll == null)
                return;

            m_ScrollToBottom = IsGenerating;
            Canvas.ForceUpdateCanvases();
            m_Scroll.verticalNormalizedPosition = 0f;
        }

        // Loads Gemma if needed and makes the engine's conversation match the current thread.
        async Awaitable<bool> PrepareAsync()
        {
            if (m_ConversationReady && m_Service.LoadedModel == Model && m_Service.Engine.IsLoaded)
                return true;
            if (m_Preparing)
                return false;

            m_Preparing = true;
            RefreshControls();
            try
            {
                if (ModelStore.GetState(Model) != ModelState.Downloaded)
                {
                    SetStatus($"Download {Model.displayName} from the launcher first.");
                    return false;
                }

                if (m_Service.LoadedModel != Model)
                {
                    if (m_Service.IsLoading)
                    {
                        SetStatus("Waiting for another model to finish loading…");
                        while (m_Service.IsLoading)
                            await Awaitable.NextFrameAsync();
                    }

                    if (m_Service.LoadedModel != Model)
                    {
                        SetStatus($"Loading {Model.displayName}…");
                        await m_Service.LoadAsync(Model);
                    }
                }

                var history = ChatContext.SelectHistory(m_Thread.messages, ChatContext.HistoryBudgetChars(Model.maxTokens));
                m_Service.NewConversation(AppLanguage.SystemPrompt(m_Thread.systemPrompt), history: history);
                m_ConversationReady = true;
                var info = m_Service.LoadInfo;
                SetStatus($"{Model.displayName} · {info?.backend ?? "?"}" +
                    (history.Count < CountReplayable(m_Thread) ? $" · resumed with last {history.Count} messages" : ""));
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

        static int CountReplayable(ChatThread thread)
        {
            var count = 0;
            foreach (var message in thread.messages)
                if (message.role == ChatRole.User || !string.IsNullOrWhiteSpace(message.text))
                    count++;
            return count;
        }

        void Subscribe()
        {
            Unsubscribe();
            var engine = m_Service.Engine;
            engine.TokenReceived += OnToken;
            engine.ResponseDone += OnDone;
            engine.Failed += OnFailed;
            m_Service.LoadedModelChanged += OnLoadedModelChanged;
        }

        void Unsubscribe()
        {
            if (m_Service == null)
                return;

            var engine = m_Service.Engine;
            engine.TokenReceived -= OnToken;
            engine.ResponseDone -= OnDone;
            engine.Failed -= OnFailed;
            m_Service.LoadedModelChanged -= OnLoadedModelChanged;
        }

        void OnLoadedModelChanged(ModelInfo model) => m_ConversationReady = false;

        void OnToken(string text)
        {
            if (!IsGenerating)
                return;

            m_Stream.Append(text);
            m_StreamDirty = true;
            SpeechEngine.Instance?.AppendReply(text);
        }

        void OnDone(LlmStats stats)
        {
            if (!IsGenerating)
                return;

            if (stats.cancelled)
                SpeechEngine.Instance?.Stop();
            else
                SpeechEngine.Instance?.EndReply();

            var reply = m_Reply;
            reply.text = m_Stream.ToString();
            reply.stats = stats;
            reply.stopped = stats.cancelled;
            m_Thread.messages.Add(reply);
            m_Thread.Touch();
            ChatThreadStore.Save(m_Thread);
            Render(m_ReplyBubble, reply);
            FinishReply(reply);

            m_LastKvTokens = stats.kvTokens;
            if (ChatContext.ShouldTrim(stats.kvTokens, Model.maxTokens))
            {
                var history = ChatContext.SelectHistory(m_Thread.messages, ChatContext.HistoryBudgetChars(Model.maxTokens));
                m_Service.NewConversation(AppLanguage.SystemPrompt(m_Thread.systemPrompt), history: history);
                SetStatus($"{Model.displayName} · context trimmed to the last {history.Count} messages");
            }
        }

        void OnFailed(string error)
        {
            if (!IsGenerating)
                return;

            SpeechEngine.Instance?.Stop();

            var reply = m_Reply;
            reply.text = m_Stream.ToString();
            m_ReplyBubble.Text.text = MarkdownToTmp.Convert(reply.text) +
                $"\n<color=#E07070>Error: {MarkdownToTmp.Escape(error)}</color>";
            if (reply.text.Length > 0)
            {
                m_Thread.messages.Add(reply);
                ChatThreadStore.Save(m_Thread);
            }

            m_ConversationReady = false;
            FinishReply(reply);
        }

        // Stops the answer in progress without waiting for the engine; its late events are ignored.
        void AbandonReply(bool save = true)
        {
            if (!IsGenerating)
                return;

            m_Service.Engine.Cancel();
            m_Reply.text = m_Stream.ToString();
            m_Reply.stopped = true;
            if (save && m_Reply.text.Length > 0)
            {
                m_Thread.messages.Add(m_Reply);
                ChatThreadStore.Save(m_Thread);
            }

            m_Reply = null;
            m_ReplyBubble = null;
            m_StreamDirty = false;
            RefreshControls();
        }

        void FinishReply(ChatMessage reply)
        {
            m_Reply = null;
            m_ReplyBubble = null;
            m_StreamDirty = false;
            m_ScrollToBottom = true;
            RefreshControls();
            RefreshCurrentCard();
            ResponseFinished?.Invoke(reply);
        }

        void ShowThread(ChatThread thread)
        {
            SpeechEngine.Instance?.Stop();
            m_Thread = thread;
            foreach (var bubble in m_Bubbles)
                Destroy(bubble.Background.transform.parent.gameObject);
            m_Bubbles.Clear();

            foreach (var message in thread.messages)
                AddBubble(message);

            m_Title.text = MarkdownToTmp.Escape(thread.title);
            m_LastKvTokens = 0;
            m_ScrollToBottom = true;
            ShowPersonaEditor(false);
            RefreshControls();
        }

        Bubble AddBubble(ChatMessage message)
        {
            var isUser = message.role == ChatRole.User;
            var row = new GameObject(isUser ? "UserRow" : "ModelRow", typeof(RectTransform), typeof(HorizontalLayoutGroup));
            row.transform.SetParent(m_Content, false);
            var rowLayout = row.GetComponent<HorizontalLayoutGroup>();
            rowLayout.childAlignment = isUser ? TextAnchor.UpperRight : TextAnchor.UpperLeft;
            rowLayout.childControlWidth = rowLayout.childControlHeight = true;
            rowLayout.childForceExpandWidth = rowLayout.childForceExpandHeight = false;

            var bubble = new Bubble();
            bubble.Background = UiFactory.CreateImage(row.transform, "Bubble",
                isUser ? Color.Lerp(UiFactory.CardColor, UiFactory.AccentColor, 0.55f) : UiFactory.CardColor);
            bubble.Background.raycastTarget = false;
            var layout = bubble.Background.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(18, 18, 12, 12);
            layout.spacing = 6;
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            var element = bubble.Background.gameObject.AddComponent<LayoutElement>();
            // The inner layout's force-expand reports flexible width; without this every bubble fills the row.
            element.flexibleWidth = 0f;

            bubble.Text = UiFactory.CreateText(bubble.Background.transform, "Text", "", 24);
            bubble.Stats = UiFactory.CreateText(bubble.Background.transform, "Stats", "", 15);
            bubble.Stats.color = UiFactory.MutedTextColor;
            bubble.Stats.gameObject.SetActive(false);

            if (isUser)
            {
                // Size user bubbles to their text, up to a maximum width.
                const float maxText = 560f;
                var text = MarkdownToTmp.Escape(message.text);
                var width = Mathf.Min(maxText, bubble.Text.GetPreferredValues(text, maxText, 0f).x);
                element.preferredWidth = width + layout.padding.horizontal + 2f;
            }
            else
            {
                element.preferredWidth = 860f;
            }

            Render(bubble, message);
            m_Bubbles.Add(bubble);
            return bubble;
        }

        static void Render(Bubble bubble, ChatMessage message)
        {
            bubble.Text.text = message.role == ChatRole.User ? MarkdownToTmp.Escape(message.text) : MarkdownToTmp.Convert(message.text);
            var hasStats = message.stats != null && message.role == ChatRole.Model && message.stats.totalSec > 0;
            bubble.Stats.gameObject.SetActive(hasStats);
            if (hasStats)
                bubble.Stats.text = message.stats.ToString();
        }

        void RefreshControls()
        {
            if (m_Panel == null)
                return;

            var generating = IsGenerating;
            UiFactory.SetLabel(m_SendButton, generating ? "Stop" : "Send");
            m_SendButton.image.color = generating ? UiFactory.DangerColor : UiFactory.AccentColor;
            m_SendButton.interactable = generating || !m_Preparing;
            m_NewButton.interactable = !generating;
            m_PersonaButton.interactable = !generating && !m_Preparing;
            m_Suggestions.SetActive(m_Thread != null && m_Thread.IsEmpty && !generating);
            m_Title.text = m_Thread != null ? MarkdownToTmp.Escape(m_Thread.title) : "";

            var context = m_LastKvTokens > 0 ? $"Context {m_LastKvTokens}/{Model.maxTokens} tokens · " : "";
            var persona = string.IsNullOrEmpty(m_Thread?.systemPrompt) ? "No persona" : "Persona on";
            m_Footer.text = $"{context}{persona} · Drag the bar below to move · Tap a card to resume, throw it to delete";
        }

        void SetStatus(string text)
        {
            if (m_Status != null)
                m_Status.text = text;
        }

        void Build()
        {
            m_Panel = UiFactory.CreatePanel(transform, "ChatPanel", new Vector2(k_Width, k_Height));

            m_Title = UiFactory.CreateText(m_Panel, "Title", "", 30);
            m_Title.fontStyle = FontStyles.Bold;
            m_Title.overflowMode = TextOverflowModes.Ellipsis;
            m_Title.textWrappingMode = TextWrappingModes.NoWrap;
            UiFactory.Place(m_Title.rectTransform, 28, 16, 450, 42);
            m_Status = UiFactory.CreateText(m_Panel, "Status", "", 18);
            m_Status.color = UiFactory.MutedTextColor;
            UiFactory.Place(m_Status.rectTransform, 28, 58, 560, 26);

            m_PersonaButton = UiFactory.CreateButton(m_Panel, "Persona", UiFactory.CardColor, () => ShowPersonaEditor(!m_PersonaEditor.activeSelf), 22);
            UiFactory.Place((RectTransform)m_PersonaButton.transform, 600, 20, 110, 56);
            m_NewButton = UiFactory.CreateButton(m_Panel, "New chat", UiFactory.CardColor, NewChat, 22);
            UiFactory.Place((RectTransform)m_NewButton.transform, 720, 20, 110, 56);
            var close = UiFactory.CreateButton(m_Panel, "Close", UiFactory.CardColor, Close, 22);
            UiFactory.Place((RectTransform)close.transform, 840, 20, 92, 56);
            var voice = VoiceToggle.Create(m_Panel, 20);
            UiFactory.Place((RectTransform)voice.transform, 490, 20, 100, 56);

            BuildMessages();
            BuildSuggestions();
            BuildInput();
            BuildPersonaEditor();

            m_Footer = UiFactory.CreateText(m_Panel, "Footer", "", 16);
            m_Footer.color = UiFactory.MutedTextColor;
            UiFactory.Place(m_Footer.rectTransform, 28, 738, 904, 30);

            BuildHandle();

            m_CardRoot = new GameObject("ChatThreadCards").transform;
        }

        void BuildMessages()
        {
            var viewport = UiFactory.CreateImage(m_Panel, "Messages", new Color(0f, 0f, 0f, 0.001f));
            viewport.gameObject.AddComponent<RectMask2D>();
            UiFactory.Place(viewport.rectTransform, 20, 96, 920, 520);

            var content = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            content.transform.SetParent(viewport.transform, false);
            m_Content = (RectTransform)content.transform;
            m_Content.anchorMin = new Vector2(0f, 1f);
            m_Content.anchorMax = Vector2.one;
            m_Content.pivot = new Vector2(0.5f, 1f);
            m_Content.offsetMin = m_Content.offsetMax = Vector2.zero;
            var layout = content.GetComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(8, 8, 8, 8);
            layout.spacing = 14;
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            content.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            m_Scroll = viewport.gameObject.AddComponent<ScrollRect>();
            m_Scroll.viewport = viewport.rectTransform;
            m_Scroll.content = m_Content;
            m_Scroll.horizontal = false;
            m_Scroll.movementType = ScrollRect.MovementType.Clamped;
            m_Scroll.scrollSensitivity = 30f;
        }

        void BuildSuggestions()
        {
            var root = new GameObject("Suggestions", typeof(RectTransform));
            root.transform.SetParent(m_Panel, false);
            var rect = (RectTransform)root.transform;
            UiFactory.Place(rect, 120, 250, 720, 260);
            m_Suggestions = root;

            var hint = UiFactory.CreateText(rect, "Hint", "Ask anything — runs on this headset, offline", 22, TextAlignmentOptions.Center);
            hint.color = UiFactory.MutedTextColor;
            UiFactory.Place(hint.rectTransform, 0, 0, 720, 40);
            for (var i = 0; i < k_Suggestions.Length; i++)
            {
                var suggestion = k_Suggestions[i];
                var chip = UiFactory.CreateButton(rect, suggestion, UiFactory.CardColor, () => Submit(suggestion), 20);
                UiFactory.Place((RectTransform)chip.transform, (i % 2) * 366, 60 + (i / 2) * 96, 354, 84);
            }
        }

        void BuildInput()
        {
            m_Input = CreateInputField(m_Panel, "Input", "Type a message, or press Talk…", TMP_InputField.LineType.MultiLineSubmit);
            UiFactory.Place((RectTransform)m_Input.transform, 20, 632, 622, 92);
            var talk = MicButton.Create(m_Panel, Submit, SetStatus, 22);
            UiFactory.Place((RectTransform)talk.transform, 652, 632, 110, 92);
            m_Input.onSubmit.AddListener(_ => Submit(m_Input.text));

            m_SendButton = UiFactory.CreateButton(m_Panel, "Send", UiFactory.AccentColor, () =>
            {
                if (IsGenerating)
                    Stop();
                else
                    Submit(m_Input.text);
            }, 26);
            UiFactory.Place((RectTransform)m_SendButton.transform, 772, 632, 168, 92);
        }

        void BuildPersonaEditor()
        {
            var editor = UiFactory.CreateImage(m_Panel, "PersonaEditor", UiFactory.PanelColor);
            UiFactory.Place(editor.rectTransform, 20, 96, 920, 520);
            m_PersonaEditor = editor.gameObject;

            var label = UiFactory.CreateText(editor.transform, "Label", "Persona (system prompt) for this chat", 24);
            UiFactory.Place(label.rectTransform, 16, 12, 880, 36);

            for (var i = 0; i < k_Personas.Length; i++)
            {
                var persona = k_Personas[i];
                var preset = UiFactory.CreateButton(editor.transform, persona.Name, UiFactory.CardColor, () => m_PersonaInput.text = persona.Prompt, 20);
                UiFactory.Place((RectTransform)preset.transform, 16 + i * 224, 58, 212, 52);
            }

            m_PersonaInput = CreateInputField(editor.transform, "PromptInput", "No persona: Gemma's default behavior", TMP_InputField.LineType.MultiLineNewline);
            UiFactory.Place((RectTransform)m_PersonaInput.transform, 16, 124, 888, 300);

            var apply = UiFactory.CreateButton(editor.transform, "Apply", UiFactory.AccentColor, ApplyPersona, 22);
            UiFactory.Place((RectTransform)apply.transform, 664, 444, 240, 60);
            var cancel = UiFactory.CreateButton(editor.transform, "Cancel", UiFactory.CardColor, () => ShowPersonaEditor(false), 22);
            UiFactory.Place((RectTransform)cancel.transform, 412, 444, 240, 60);

            m_PersonaEditor.SetActive(false);
        }

        void ShowPersonaEditor(bool show)
        {
            if (m_PersonaEditor == null)
                return;

            if (show)
                m_PersonaInput.text = m_Thread.systemPrompt;
            m_PersonaEditor.SetActive(show);
        }

        void ApplyPersona()
        {
            m_Thread.systemPrompt = m_PersonaInput.text.Trim();
            if (!m_Thread.IsEmpty)
                ChatThreadStore.Save(m_Thread);
            ShowPersonaEditor(false);
            m_ConversationReady = false;
            RefreshControls();
            _ = PrepareAsync();
        }

        // A bar under the panel; grabbing it moves the whole chat.
        void BuildHandle()
        {
            const float barWidth = 220f;
            const float barHeight = 22f;
            var bar = UiFactory.CreateImage(m_Panel, "MoveHandle", new Color(1f, 1f, 1f, 0.55f));
            bar.raycastTarget = false;
            UiFactory.Place(bar.rectTransform, (k_Width - barWidth) / 2f, k_Height + 14f, barWidth, barHeight);

            var collider = gameObject.AddComponent<BoxCollider>();
            collider.center = new Vector3(0f, -(k_Height / 2f + 14f + barHeight / 2f), 0f) * 0.001f;
            collider.size = new Vector3(barWidth + 60f, barHeight + 50f, 30f) * 0.001f;

            var body = gameObject.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;

            m_Handle = gameObject.AddComponent<XRGrabInteractable>();
            m_Handle.movementType = XRBaseInteractable.MovementType.Instantaneous;
            m_Handle.useDynamicAttach = true;
            m_Handle.trackRotation = false;
            m_Handle.throwOnDetach = false;
            m_Handle.selectExited.AddListener(_ => LayoutCards(false));
        }

        void FaceUser()
        {
            var head = Camera.main != null ? Camera.main.transform : null;
            if (head == null)
                return;

            var forward = Vector3.ProjectOnPlane(transform.position - head.position, Vector3.up);
            if (forward.sqrMagnitude > 0.001f)
                transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
        }

        static TMP_InputField CreateInputField(Transform parent, string name, string placeholder, TMP_InputField.LineType lineType)
        {
            var background = UiFactory.CreateImage(parent, name, UiFactory.CardColor);
            var area = new GameObject("Text Area", typeof(RectTransform), typeof(RectMask2D));
            area.transform.SetParent(background.transform, false);
            var areaRect = (RectTransform)area.transform;
            UiFactory.Stretch(areaRect, 18, 18, 12, 12);

            var hint = UiFactory.CreateText(areaRect, "Placeholder", placeholder, 24);
            hint.color = UiFactory.MutedTextColor;
            hint.fontStyle = FontStyles.Italic;
            UiFactory.Stretch(hint.rectTransform);
            var text = UiFactory.CreateText(areaRect, "Text", "", 24);
            UiFactory.Stretch(text.rectTransform);

            var field = background.gameObject.AddComponent<TMP_InputField>();
            field.targetGraphic = background;
            field.textViewport = areaRect;
            field.textComponent = text;
            field.placeholder = hint;
            field.fontAsset = text.font;
            field.pointSize = 24;
            field.lineType = lineType;
            field.richText = false;
            return field;
        }

        void RebuildCards(List<ChatThread> threads)
        {
            foreach (var card in m_Cards)
                if (card != null)
                    Destroy(card.gameObject);
            m_Cards.Clear();

            foreach (var thread in threads)
            {
                if (thread.IsEmpty)
                    continue;
                if (m_Cards.Count == k_MaxCards)
                    break;

                var card = ThreadCard.Create(m_CardRoot, thread);
                card.DropTarget = transform;
                card.OpenRequested += OnCardOpenRequested;
                card.Thrown += OnCardThrown;
                card.SetCurrent(m_Thread != null && thread.id == m_Thread.id);
                m_Cards.Add(card);
            }

            LayoutCards(true);
        }

        // Stacks cards in two columns left of the panel. Cards the user placed stay put unless resetting.
        void LayoutCards(bool resetAll)
        {
            var panelHalfWidth = k_Width / 2f * 0.001f;
            var top = k_Height / 2f * 0.001f - 0.07f;
            for (var i = 0; i < m_Cards.Count; i++)
            {
                var card = m_Cards[i];
                if (card == null || (card.UserPlaced && !resetAll))
                    continue;

                var column = i % 2;
                var row = i / 2;
                var local = new Vector3(-panelHalfWidth - 0.12f - column * k_CardColumnSpacing, top - row * k_CardRowSpacing, -0.03f);
                card.transform.SetPositionAndRotation(transform.TransformPoint(local), transform.rotation * Quaternion.Euler(0f, -12f, 0f));
            }
        }

        void RefreshCurrentCard()
        {
            foreach (var card in m_Cards)
            {
                if (card != null && card.Thread.id == m_Thread.id)
                {
                    card.SetThread(m_Thread);
                    return;
                }
            }

            // First answer in a new thread: give it a card.
            if (!m_Thread.IsEmpty && m_Cards.Count < k_MaxCards)
            {
                var card = ThreadCard.Create(m_CardRoot, m_Thread);
                card.DropTarget = transform;
                card.OpenRequested += OnCardOpenRequested;
                card.Thrown += OnCardThrown;
                m_Cards.Insert(0, card);
                foreach (var other in m_Cards)
                    other.SetCurrent(other == card);
                LayoutCards(false);
            }
        }

        void OnCardOpenRequested(ThreadCard card)
        {
            if (m_Thread != null && card.Thread.id == m_Thread.id)
                return;

            var thread = ChatThreadStore.Load(card.Thread.id) ?? card.Thread;
            OpenThread(thread);
        }

        void OnCardThrown(ThreadCard card)
        {
            m_Cards.Remove(card);
            ChatThreadStore.Delete(card.Thread.id);
            if (m_Thread != null && card.Thread.id == m_Thread.id)
            {
                AbandonReply(save: false);
                // The thread was deleted; keep the persona for the next chat.
                ShowThread(ChatThread.Create(m_Thread.systemPrompt));
                m_ConversationReady = false;
            }

            SetStatus($"Deleted \"{MarkdownToTmp.Escape(card.Thread.title)}\"");
        }
    }
}
