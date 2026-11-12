using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AIEdge.Spatial;
using AIEdge.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AIEdge.TinyGarden
{
    /// <summary>
    /// The result of one typed or spoken garden command.
    /// </summary>
    public class GardenCommandResult
    {
        public string Text;
        public string ResolvedText;
        public readonly List<GardenToolCall> Calls = new List<GardenToolCall>();
        public readonly List<GardenEvent> Events = new List<GardenEvent>();
        public LlmStats Stats;
        public string Error;
    }

    /// <summary>
    /// Tabletop Garden (the Gallery's Tiny Garden): the FunctionGemma 270M fine-tune turns commands into
    /// <c>plant_seed</c> / <c>water_plots</c> / <c>harvest_plots</c> tool calls that drive a 3D planter on the table.
    /// Pointing words ("here", "these") are resolved to the plots the user points at before the model sees the text.
    /// </summary>
    public class GardenPanel : MonoBehaviour
    {
        const float k_Width = 640f;
        const float k_Height = 640f;
        const int k_LogLines = 9;
        const float k_PlanterFootprint = 0.42f;
        const float k_MaxSurfaceDistance = 1.2f;
        const float k_DepthWait = 2f;
        const string k_ToolResult = "{\"result\":\"success\"}";

        static readonly string[] k_Suggestions =
        {
            "plant sunflowers in the top row",
            "water these",
            "plant a rose here",
            "harvest the bottom row",
        };

        [SerializeField, Tooltip("Model service. Uses LlmService.Instance if empty.")]
        LlmService m_Service;

        [SerializeField, Tooltip("Planter view. Created as a sibling object if empty.")]
        GardenView m_Garden;

        [SerializeField, Tooltip("Lit material for the planter and plants (tinted per piece).")]
        Material m_GardenMaterial;

        [SerializeField, Tooltip("Distance of the planter from the user's head, in meters, when placed.")]
        float m_GardenDistance = 0.55f;

        [SerializeField, Tooltip("Height of the planter below the user's eyes, in meters (a table, without depth).")]
        float m_GardenDrop = 0.55f;

        /// <summary>Raised when the user closes the garden.</summary>
        public event Action Closed;

        /// <summary>Raised when a command finishes or fails.</summary>
        public event Action<GardenCommandResult> CommandFinished;

        /// <summary>How the planter was last placed, for the log and tests.</summary>
        public string PlacementNote { get; private set; } = "";

        /// <summary>True if the planter was last placed on a real surface found with depth.</summary>
        public bool PlacedOnSurface { get; private set; }

        /// <summary>The planter.</summary>
        public GardenView Garden => m_Garden;

        /// <summary>The game state.</summary>
        public GardenState State { get; } = new GardenState();

        /// <summary>True when the model is loaded and no command is running.</summary>
        public bool IsReady => m_ModelReady && m_Pending == null && !m_Preparing;

        /// <summary>True while the panel is open.</summary>
        public bool IsOpen => m_Panel != null && m_Panel.gameObject.activeSelf;

        ModelInfo Model => ModelCatalog.TinyGarden;

        RectTransform m_Panel;
        TextMeshProUGUI m_Status;
        TextMeshProUGUI m_Harvest;
        TextMeshProUGUI m_Log;
        TextMeshProUGUI m_SelectionLabel;
        TMP_InputField m_Input;
        Button m_SendButton;
        readonly List<string> m_LogLines = new List<string>();
        readonly ConcurrentQueue<(string Name, string Args)> m_ToolCalls = new ConcurrentQueue<(string, string)>();
        readonly StringBuilder m_Reply = new StringBuilder();
        GardenCommandResult m_Pending;
        bool m_ModelReady;
        bool m_Preparing;

        void Awake()
        {
            if (m_Service == null)
                m_Service = LlmService.Instance;
        }

        void OnDestroy()
        {
            Unsubscribe();
            if (m_Garden != null)
                Destroy(m_Garden.gameObject);
        }

        /// <summary>Opens the garden: places the planter in front of the user and loads the model.</summary>
        public void Open()
        {
            if (m_Panel == null)
                Build();
            if (m_Service == null)
                m_Service = LlmService.Instance;

            m_Panel.gameObject.SetActive(true);
            m_Garden.gameObject.SetActive(true);
            PlaceGarden();
            Subscribe();
            _ = PrepareAsync();
        }

        /// <summary>Closes the garden. The game state is kept for next time.</summary>
        public void Close()
        {
            if (m_Pending != null)
                m_Service.Engine.Cancel();
            Finish("Closed");
            SpeechEngine.Instance?.Stop();

            Unsubscribe();
            m_Panel.gameObject.SetActive(false);
            m_Garden.gameObject.SetActive(false);
            Closed?.Invoke();
        }

        /// <summary>Moves the planter to a table-height spot in front of the user, with the panel beside it.</summary>
        public void PlaceGarden() => _ = PlaceGardenAsync();

        /// <summary>
        /// Puts the planter on a free, flat real surface ahead (a table, else the floor) using depth
        /// (PortPlan §4.3), or at table height in front of the user without depth. The panel goes beside it.
        /// </summary>
        public async Awaitable PlaceGardenAsync()
        {
            var head = Camera.main.transform;
            var forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            forward = forward.sqrMagnitude > 1e-4f ? forward.normalized : Vector3.forward;

            var depth = DepthService.Instance;
            var started = Time.realtimeSinceStartup;
            Pose pose = default;
            var report = "";
            PlacedOnSurface = depth != null && await depth.WaitUntilReadyAsync(k_DepthWait) &&
                DepthPlacement.TryPlaceOnSurface(depth, new Vector2(k_PlanterFootprint, k_PlanterFootprint), k_MaxSurfaceDistance, out pose, out report);
            Debug.Log($"[AIEdge] Garden placement rays: {report}");
            var surface = PlacedOnSurface ? pose.position : head.position + forward * m_GardenDistance + Vector3.down * m_GardenDrop;
            m_Garden.Place(surface, -forward);
            var milliseconds = (Time.realtimeSinceStartup - started) * 1000f;
            PlacementNote = PlacedOnSurface
                ? $"Placed on a real surface {DepthRegion.FormatLength(Vector3.Distance(head.position, surface))} away, {DepthRegion.FormatLength(head.position.y - surface.y)} below your eyes ({milliseconds:0} ms)."
                : depth == null || !depth.IsReady ? "No depth: placed at table height." : "No free flat surface ahead: placed at table height.";
            Debug.Log($"[AIEdge] Garden: {PlacementNote}");
            AddLog($"<color=#8A90A0>{PlacementNote}</color>");

            var right = Vector3.Cross(Vector3.up, forward);
            var panelPosition = surface + right * 0.5f + forward * 0.1f;
            panelPosition.y = head.position.y - 0.05f;
            var toPanel = Vector3.ProjectOnPlane(panelPosition - head.position, Vector3.up);
            transform.SetPositionAndRotation(panelPosition, Quaternion.LookRotation(toPanel, Vector3.up));
            DepthPlacement.PushOutOfGeometry(depth, transform, new Vector2(k_Width, k_Height) * 0.001f);
        }

        /// <summary>Sends a command to the model, resolving "here" and "these" to the pointed-at plots.</summary>
        public async void Submit(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0 || m_Pending != null)
                return;
            if (!await PrepareAsync())
                return;

            var resolved = DeicticResolver.Resolve(text, m_Garden.Selection);
            if (DeicticResolver.HasDeixis(text) && m_Garden.Selection.Count == 0)
                AddLog("<color=#E0B050>Point at plots first, then say \"here\" or \"these\".</color>");

            m_Input.text = "";
            m_Pending = new GardenCommandResult { Text = text, ResolvedText = resolved };
            m_Reply.Clear();
            while (m_ToolCalls.TryDequeue(out _)) { }
            AddLog(resolved == text ? $"<b>› {Escape(text)}</b>" : $"<b>› {Escape(text)}</b>  <color=#9FD3FF>→ {Escape(resolved)}</color>");
            RefreshControls();

            // One fresh conversation per command, as in Phase 0: the model only needs the prompt and the command.
            m_Service.NewConversation(TinyGardenModel.SystemPrompt, TinyGardenModel.ToolsJson, constrainedDecoding: true);
            m_Service.Engine.Send(resolved);
        }

        /// <summary>Empties the garden.</summary>
        public void ResetGarden()
        {
            State.Reset();
            m_Garden.ClearSelection();
            AddLog("Garden reset.");
        }

        void Update()
        {
            ApplyToolCalls();
            if (m_Harvest != null && IsOpen)
                RefreshHarvest();
        }

        void ApplyToolCalls()
        {
            while (m_ToolCalls.TryDequeue(out var raw))
            {
                var call = GardenToolCall.Parse(raw.Name, raw.Args);
                var note = "";
                if (call.plots.Length == 0 && m_Pending != null)
                {
                    // The 270M model sometimes drops the plot list ("plant the special seed in plot 9" -> plots []).
                    call.plots = DeicticResolver.ExplicitPlots(m_Pending.ResolvedText);
                    if (call.plots.Length > 0)
                        note = " <color=#8A90A0>(plots taken from the command)</color>";
                }

                var events = State.Apply(call);
                m_Pending?.Calls.Add(call);
                m_Pending?.Events.AddRange(events);
                AddLog($"<color=#7FD8A0>{Escape(call.ToString())}</color>{note}  {Summarize(events)}");
            }
        }

        async Awaitable<bool> PrepareAsync()
        {
            if (m_ModelReady && m_Service.LoadedModel == Model)
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
                    SetStatus($"Download {Model.displayName} from the launcher first (it needs a Hugging Face token).");
                    return false;
                }

                while (m_Service.IsLoading)
                    await Awaitable.NextFrameAsync();

                SetStatus($"Loading {Model.displayName}…");
                var info = await m_Service.LoadAsync(Model);
                m_ModelReady = true;
                SetStatus($"{Model.displayName} · {info.backend}. Point at plots, then say \"water these\".");
                return true;
            }
            catch (Exception e)
            {
                SetStatus($"<color=#E07070>Couldn't load {Model.displayName}: {Escape(e.Message)}</color>");
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
            m_Service.Engine.ToolCallHandler = OnToolCall;
            m_Service.LoadedModelChanged += OnLoadedModelChanged;
            m_Garden.HandAction += OnHandAction;
            m_Garden.SelectionChanged += RefreshSelection;
        }

        void Unsubscribe()
        {
            if (m_Service == null)
                return;

            m_Service.Engine.TokenReceived -= OnToken;
            m_Service.Engine.ResponseDone -= OnDone;
            m_Service.Engine.Failed -= OnFailed;
            if (m_Service.Engine.ToolCallHandler == (Func<string, string, string>)OnToolCall)
                m_Service.Engine.ToolCallHandler = null;
            m_Service.LoadedModelChanged -= OnLoadedModelChanged;
            if (m_Garden != null)
            {
                m_Garden.HandAction -= OnHandAction;
                m_Garden.SelectionChanged -= RefreshSelection;
            }
        }

        void OnLoadedModelChanged(ModelInfo model) => m_ModelReady = false;

        // Called on the inference thread: queue for the main thread.
        string OnToolCall(string name, string argsJson)
        {
            m_ToolCalls.Enqueue((name, argsJson));
            return k_ToolResult;
        }

        void OnToken(string text)
        {
            if (m_Pending != null)
                m_Reply.Append(text);
        }

        void OnDone(LlmStats stats)
        {
            if (m_Pending == null)
                return;

            ApplyToolCalls();
            m_Pending.Stats = stats;
            if (m_Pending.Calls.Count == 0)
                AddLog("<color=#E0B050>No garden action in that. Try \"water plots 1 and 2\".</color>");
            var reply = m_Reply.ToString().Trim();
            if (reply.Length > 0)
                AddLog($"<color=#B3BACC>{Escape(reply)}</color>");
            AddLog($"<size=14><color=#8A90A0>{stats.totalSec:0.0}s · {stats.turnTokens} tok</color></size>");
            SpeechEngine.Instance?.Speak(m_Pending.Calls.Count > 0 ? GardenState.Summarize(m_Pending.Events) : "I didn't catch a garden action.", interrupt: true);
            m_Garden.ClearSelection();
            Finish(null);
        }

        void OnFailed(string error)
        {
            if (m_Pending == null)
                return;

            AddLog($"<color=#E07070>Error: {Escape(error)}</color>");
            Finish(error);
        }

        void Finish(string error)
        {
            if (m_Pending == null)
                return;

            var result = m_Pending;
            result.Error = error;
            m_Pending = null;
            RefreshControls();
            CommandFinished?.Invoke(result);
        }

        void OnHandAction(string description, List<GardenEvent> events) =>
            AddLog($"<color=#C8A0FF>Hands: {Escape(description)}</color>  {Summarize(events)}");

        static string Summarize(List<GardenEvent> events) =>
            string.Join("; ", events.Select(e => e.ok ? Escape(e.ToString()) : $"<color=#E0B050>{Escape(e.ToString())}</color>"));

        static string Escape(string text) => MarkdownToTmp.Escape(text);

        void AddLog(string line)
        {
            m_LogLines.Add(line);
            while (m_LogLines.Count > k_LogLines)
                m_LogLines.RemoveAt(0);
            if (m_Log != null)
                m_Log.text = string.Join("\n", m_LogLines);
        }

        void RefreshSelection()
        {
            if (m_SelectionLabel == null)
                return;
            var selection = m_Garden.Selection;
            m_SelectionLabel.text = selection.Count == 0
                ? "Point at plots to pick them for \"here\" / \"these\""
                : $"Pointing at {DeicticResolver.FormatPlots(selection)}";
        }

        void RefreshHarvest()
        {
            var special = State.SpecialUnlocked ? $"special {State.HarvestedCount(Seed.Special)}" : $"special seed unlocks at {GardenState.SpecialUnlockHarvests} harvests";
            m_Harvest.text = $"Harvest: sunflower {State.HarvestedCount(Seed.Sunflower)} · daisy {State.HarvestedCount(Seed.Daisy)} · rose {State.HarvestedCount(Seed.Rose)} · {special}";
        }

        void RefreshControls()
        {
            if (m_SendButton == null)
                return;
            m_SendButton.interactable = m_Pending == null && !m_Preparing;
            UiFactory.SetLabel(m_SendButton, m_Pending != null ? "…" : "Send");
        }

        void SetStatus(string text)
        {
            if (m_Status != null)
                m_Status.text = text;
        }

        void Build()
        {
            if (m_Garden == null)
                m_Garden = new GameObject("TabletopGarden").AddComponent<GardenView>();
            if (m_Garden.State == null)
                m_Garden.Initialize(State, m_GardenMaterial);

            m_Panel = UiFactory.CreatePanel(transform, "GardenPanel", new Vector2(k_Width, k_Height));

            var title = UiFactory.CreateText(m_Panel, "Title", "Tabletop Garden", 30);
            title.fontStyle = FontStyles.Bold;
            UiFactory.Place(title.rectTransform, 22, 14, 420, 42);
            var close = UiFactory.CreateButton(m_Panel, "Close", UiFactory.CardColor, Close, 20);
            UiFactory.Place((RectTransform)close.transform, k_Width - 104, 16, 84, 46);
            var voice = VoiceToggle.Create(m_Panel, 17);
            UiFactory.Place((RectTransform)voice.transform, k_Width - 206, 16, 94, 46);

            m_Status = UiFactory.CreateText(m_Panel, "Status", "", 16);
            m_Status.color = UiFactory.MutedTextColor;
            UiFactory.Place(m_Status.rectTransform, 22, 60, k_Width - 44, 44);
            m_Harvest = UiFactory.CreateText(m_Panel, "Harvest", "", 16);
            UiFactory.Place(m_Harvest.rectTransform, 22, 104, k_Width - 44, 26);

            var logBackground = UiFactory.CreateImage(m_Panel, "LogBackground", UiFactory.CardColor);
            UiFactory.Place(logBackground.rectTransform, 22, 136, k_Width - 44, 250);
            m_Log = UiFactory.CreateText(logBackground.transform, "Log", "", 17);
            m_Log.overflowMode = TextOverflowModes.Truncate;
            m_Log.alignment = TextAlignmentOptions.BottomLeft;
            UiFactory.Stretch(m_Log.rectTransform, 12, 12, 8, 8);

            m_SelectionLabel = UiFactory.CreateText(m_Panel, "Selection", "", 17);
            m_SelectionLabel.color = new Color(0.55f, 0.75f, 1f);
            UiFactory.Place(m_SelectionLabel.rectTransform, 22, 394, k_Width - 150, 30);
            var clear = UiFactory.CreateButton(m_Panel, "Clear", UiFactory.CardColor, () => m_Garden.ClearSelection(), 16);
            UiFactory.Place((RectTransform)clear.transform, k_Width - 112, 392, 90, 34);

            m_Input = CreateInputField(m_Panel);
            UiFactory.Place((RectTransform)m_Input.transform, 22, 434, k_Width - 280, 60);
            var talk = MicButton.Create(m_Panel, Submit, SetStatus, 20);
            UiFactory.Place((RectTransform)talk.transform, k_Width - 250, 434, 106, 60);
            m_Input.onSubmit.AddListener(_ => Submit(m_Input.text));
            m_SendButton = UiFactory.CreateButton(m_Panel, "Send", UiFactory.AccentColor, () => Submit(m_Input.text), 22);
            UiFactory.Place((RectTransform)m_SendButton.transform, k_Width - 134, 434, 112, 60);

            for (var i = 0; i < k_Suggestions.Length; i++)
            {
                var suggestion = k_Suggestions[i];
                var chip = UiFactory.CreateButton(m_Panel, suggestion, UiFactory.CardColor, () => Submit(suggestion), 16);
                UiFactory.Place((RectTransform)chip.transform, 22 + (i % 2) * 302, 504 + (i / 2) * 46, 294, 40);
            }

            var reset = UiFactory.CreateButton(m_Panel, "Reset garden", UiFactory.CardColor, ResetGarden, 17);
            UiFactory.Place((RectTransform)reset.transform, 22, 600, 294, 30);
            var move = UiFactory.CreateButton(m_Panel, "Move garden here", UiFactory.CardColor, PlaceGarden, 17);
            UiFactory.Place((RectTransform)move.transform, 324, 600, 294, 30);

            AddLog("Tell the garden what to do, or use your hands: pour the can, drop a seed packet on a plot, pull a flower to pick it.");
            RefreshSelection();
            RefreshControls();
        }

        static TMP_InputField CreateInputField(Transform parent)
        {
            var background = UiFactory.CreateImage(parent, "Command", UiFactory.CardColor);
            var area = new GameObject("Text Area", typeof(RectTransform), typeof(RectMask2D));
            area.transform.SetParent(background.transform, false);
            var areaRect = (RectTransform)area.transform;
            UiFactory.Stretch(areaRect, 14, 14, 10, 10);

            var hint = UiFactory.CreateText(areaRect, "Placeholder", "Type or dictate: \"water these\"", 20);
            hint.color = UiFactory.MutedTextColor;
            hint.fontStyle = FontStyles.Italic;
            UiFactory.Stretch(hint.rectTransform);
            var text = UiFactory.CreateText(areaRect, "Text", "", 20);
            UiFactory.Stretch(text.rectTransform);

            var field = background.gameObject.AddComponent<TMP_InputField>();
            field.targetGraphic = background;
            field.textViewport = areaRect;
            field.textComponent = text;
            field.placeholder = hint;
            field.fontAsset = text.font;
            field.pointSize = 20;
            field.lineType = TMP_InputField.LineType.SingleLine;
            field.richText = false;
            return field;
        }
    }
}
