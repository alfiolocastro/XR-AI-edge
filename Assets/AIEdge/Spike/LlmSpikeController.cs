using System;
using System.Collections;
using System.IO;
using System.Text;
using AIEdge.TinyGarden;
using AIEdge.UI;
using TMPro;
using UnityEngine;

namespace AIEdge.Spike
{
    /// <summary>
    /// Benchmark from Phase 0: loads models through <see cref="LlmService"/>, measures speed and memory,
    /// captures a passthrough frame for an image prompt, and checks Tiny Garden tool calls. Results are logged as
    /// <c>SPIKE|{json}</c> lines (logcat tag Unity) and shown on its own panel.
    /// Runs from the launcher's Benchmark button, or automatically when <c>persistentDataPath/spike.json</c> exists.
    /// </summary>
    public class LlmSpikeController : MonoBehaviour
    {
        /// <summary>Benchmark settings, overridable with spike.json on device.</summary>
        [Serializable]
        public class SpikeConfig
        {
            public bool autoRun = true;
            public string backend = "gpu";
            public string visionBackend = "gpu";
            public string chatPrompt = "In three short sentences, explain what mixed reality is.";
            public bool runImage = true;
            public string imagePrompt = "Describe what you see in this image in two sentences.";
            public bool runGarden = true;
            public string[] gardenPrompts =
            {
                "plant sunflowers in the top row",
                "water plots 1, 5 and 9",
                "harvest the left column",
                "plant a rose in the middle",
            };
        }

        [SerializeField, Tooltip("Passthrough frame source for the image test. Optional.")]
        PassthroughFrameCapture m_FrameCapture;

        [SerializeField, Tooltip("Benchmark settings used when no spike.json exists on device.")]
        SpikeConfig m_Config = new SpikeConfig();

        /// <summary>Raised when the user closes the benchmark panel.</summary>
        public event Action Closed;

        LlmEngine m_Engine;
        RectTransform m_Panel;
        TextMeshProUGUI m_StatusText;
        TextMeshProUGUI m_OutputText;
        readonly StringBuilder m_Output = new StringBuilder();
        Coroutine m_Run;

        // Per-step results written by engine events.
        bool m_StepDone;
        string m_StepError;
        LlmStats m_StepStats;
        readonly StringBuilder m_StepText = new StringBuilder();
        readonly StringBuilder m_ToolCalls = new StringBuilder();

        IEnumerator Start()
        {
            var configPath = Path.Combine(Application.persistentDataPath, "spike.json");
            if (!File.Exists(configPath))
                yield break;

            JsonUtility.FromJsonOverwrite(File.ReadAllText(configPath), m_Config);
            if (!m_Config.autoRun)
                yield break;

            // Let head tracking settle before placing the panel.
            yield return new WaitForSeconds(1f);
            StartSpike(m_Config.backend);
        }

        /// <summary>Runs the benchmark with the given language-model backend ("cpu" or "gpu").</summary>
        public void StartSpike(string backend)
        {
            if (m_Panel == null)
                BuildPanel();
            m_Panel.gameObject.SetActive(true);
            UiFactory.PlaceInFront(m_Panel, 1.2f);

            if (m_Run != null)
            {
                StopCoroutine(m_Run);
                Unsubscribe();
            }

            m_Config.backend = backend;
            m_Run = StartCoroutine(RunSpike());
        }

        void Close()
        {
            if (m_Run != null)
            {
                m_Engine?.Cancel();
                StopCoroutine(m_Run);
                Unsubscribe();
                m_Run = null;
            }

            m_Panel.gameObject.SetActive(false);
            Closed?.Invoke();
        }

        IEnumerator RunSpike()
        {
            var service = LlmService.Instance;
            m_Engine = service.Engine;
            Subscribe();
            m_Output.Clear();
            Report("gpu_probe", $"{{\"result\":\"{Escape(LlmEngine.ProbeGpu())}\"}}");

            // The benchmark may override the catalog backend.
            var chatModel = ModelCatalog.Gemma4E2B;
            var savedBackend = chatModel.backend;
            var savedVision = chatModel.visionBackend;
            chatModel.backend = ParseBackend(m_Config.backend);
            chatModel.visionBackend = ParseBackend(m_Config.visionBackend);
            var withVision = m_Config.runImage && chatModel.visionBackend != LlmBackend.None;

            // 1. Chat model load.
            SetStatus($"Loading {chatModel.displayName} ({m_Config.backend}, vision {(withVision ? m_Config.visionBackend : "off")})…");
            service.Unload();
            yield return Await(service.LoadAsync(chatModel, withVision), out var load);
            chatModel.backend = savedBackend;
            chatModel.visionBackend = savedVision;
            Report("load", load.Error == null
                ? $"{{\"model\":\"{chatModel.fileName}\",\"info\":{JsonUtility.ToJson(service.LoadInfo)}}}"
                : ErrorJson(load.Error));
            if (load.Error != null)
            {
                Finish();
                yield break;
            }

            // 2. Text chat turn.
            service.NewConversation();
            SetStatus("Chat test…");
            yield return RunStep(() => m_Engine.Send(m_Config.chatPrompt));
            ReportTurn("chat", m_Config.chatPrompt);

            // 3. Ask Image with a passthrough frame.
            if (withVision)
                yield return RunImageTest(service);

            // 4. Tiny Garden function calling.
            if (m_Config.runGarden)
                yield return RunGardenTest(service);

            Finish();
        }

        void Finish()
        {
            SetStatus("Benchmark finished.");
            Report("finished", "{}");
            Unsubscribe();
            m_Run = null;
        }

        IEnumerator RunImageTest(LlmService service)
        {
            if (m_FrameCapture == null)
            {
                Report("image", ErrorJson("No PassthroughFrameCapture assigned"));
                yield break;
            }

            SetStatus("Waiting for passthrough camera…");
            m_FrameCapture.StartStreaming();
            var timeout = Time.time + 15f;
            while (!m_FrameCapture.IsReady && Time.time < timeout)
                yield return null;
            if (!m_FrameCapture.IsReady)
            {
                m_FrameCapture.StopStreaming();
                Report("image", ErrorJson("Passthrough camera not ready (permission or support)"));
                yield break;
            }

            var imagePath = Path.Combine(Application.temporaryCachePath, "spike_frame.png");
            var captured = m_FrameCapture.CaptureToFile(imagePath);
            m_FrameCapture.StopStreaming();
            if (!captured)
            {
                Report("image", ErrorJson("Capture failed"));
                yield break;
            }
            Report("camera", $"{{\"resolution\":\"{m_FrameCapture.Resolution.x}x{m_FrameCapture.Resolution.y}\",\"bytes\":{new FileInfo(imagePath).Length}}}");

            service.NewConversation();
            SetStatus("Ask Image test…");
            yield return RunStep(() => m_Engine.Send(m_Config.imagePrompt, imagePath));
            ReportTurn("image", m_Config.imagePrompt);
        }

        IEnumerator RunGardenTest(LlmService service)
        {
            var garden = ModelCatalog.TinyGarden;
            if (ModelStore.GetState(garden) != ModelState.Downloaded)
            {
                Report("garden", ErrorJson("Model not downloaded: " + ModelStore.GetPath(garden)));
                yield break;
            }

            SetStatus($"Loading {garden.displayName}…");
            yield return Await(service.LoadAsync(garden), out var load);
            Report("garden_load", load.Error == null ? JsonUtility.ToJson(service.LoadInfo) : ErrorJson(load.Error));
            if (load.Error != null)
                yield break;

            foreach (var prompt in m_Config.gardenPrompts)
            {
                // One command per conversation, like the Gallery's periodic reset.
                service.NewConversation(TinyGardenModel.SystemPrompt, TinyGardenModel.ToolsJson, constrainedDecoding: true);
                SetStatus($"Garden: {prompt}");
                yield return RunStep(() => m_Engine.Send(prompt));
                ReportTurn("garden", prompt);
            }
        }

        /// <summary>Result holder so coroutines can wait on an Awaitable.</summary>
        class AwaitResult
        {
            public bool Done;
            public string Error;
        }

        static IEnumerator Await<T>(Awaitable<T> awaitable, out AwaitResult result)
        {
            var r = result = new AwaitResult();
            Observe(awaitable, r);
            return new WaitUntil(() => r.Done);
        }

        static async void Observe<T>(Awaitable<T> awaitable, AwaitResult result)
        {
            try
            {
                await awaitable;
            }
            catch (Exception e)
            {
                result.Error = e.Message;
            }

            result.Done = true;
        }

        IEnumerator RunStep(Action start)
        {
            m_StepDone = false;
            m_StepError = null;
            m_StepStats = null;
            m_StepText.Clear();
            lock (m_ToolCalls)
                m_ToolCalls.Clear();

            start();
            while (!m_StepDone)
                yield return null;
        }

        void Subscribe()
        {
            m_Engine.TokenReceived += OnToken;
            m_Engine.ResponseDone += OnDone;
            m_Engine.Failed += OnFailed;
            m_Engine.ToolCallHandler = OnToolCall;
        }

        void Unsubscribe()
        {
            if (m_Engine == null)
                return;

            m_Engine.TokenReceived -= OnToken;
            m_Engine.ResponseDone -= OnDone;
            m_Engine.Failed -= OnFailed;
            m_Engine.ToolCallHandler = null;
        }

        void OnToken(string text)
        {
            m_StepText.Append(text);
            m_OutputText.text = m_Output + m_StepText.ToString();
        }

        void OnDone(LlmStats stats)
        {
            m_StepStats = stats;
            m_StepDone = true;
        }

        void OnFailed(string message)
        {
            m_StepError = message;
            m_StepDone = true;
            SetStatus("Error: " + message);
        }

        // Called on the inference thread.
        string OnToolCall(string name, string argsJson)
        {
            lock (m_ToolCalls)
                m_ToolCalls.Append(m_ToolCalls.Length > 0 ? "," : "").Append($"{{\"name\":\"{name}\",\"args\":{argsJson}}}");
            return "{\"result\":\"success\"}";
        }

        void ReportTurn(string step, string prompt)
        {
            string toolCalls;
            lock (m_ToolCalls)
                toolCalls = m_ToolCalls.ToString();

            var json = m_StepError != null
                ? ErrorJson(m_StepError)
                : $"{{\"prompt\":\"{Escape(prompt)}\",\"stats\":{JsonUtility.ToJson(m_StepStats)},\"toolCalls\":[{toolCalls}],\"response\":\"{Escape(m_StepText.ToString())}\"}}";
            Report(step, json);

            m_Output.Append($"<b>{step}</b> {prompt}\n");
            if (toolCalls.Length > 0)
                m_Output.Append($"<color=#7fd>tools: {toolCalls}</color>\n");
            m_Output.Append(m_StepText).Append('\n');
            if (m_StepStats != null)
                m_Output.Append($"<color=#aaa>{m_StepStats}</color>\n\n");
            m_OutputText.text = m_Output.ToString();
        }

        static void Report(string step, string json)
        {
            Debug.Log($"SPIKE|{{\"step\":\"{step}\",\"data\":{json}}}");
        }

        static string ErrorJson(string message) => $"{{\"error\":\"{Escape(message)}\"}}";

        static string Escape(string s) =>
            s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "").Replace("\t", " ");

        static LlmBackend ParseBackend(string name) => name?.ToLowerInvariant() switch
        {
            "gpu" => LlmBackend.Gpu,
            "cpu" => LlmBackend.Cpu,
            _ => LlmBackend.None,
        };

        void SetStatus(string status)
        {
            if (m_StatusText != null)
                m_StatusText.text = status;
        }

        void BuildPanel()
        {
            m_Panel = UiFactory.CreatePanel(transform, "BenchmarkPanel", new Vector2(900, 700));

            m_StatusText = UiFactory.CreateText(m_Panel, "Status", "", 28);
            UiFactory.Stretch(m_StatusText.rectTransform, 24, 24, 24, 700 - 80);

            m_OutputText = UiFactory.CreateText(m_Panel, "Output", "", 22);
            UiFactory.Stretch(m_OutputText.rectTransform, 24, 24, 90, 110);
            m_OutputText.overflowMode = TextOverflowModes.Truncate;

            var cpu = UiFactory.CreateButton(m_Panel, "Run CPU", UiFactory.AccentColor, () => StartSpike("cpu"));
            UiFactory.Place((RectTransform)cpu.transform, 24, 606, 200, 70);
            var gpu = UiFactory.CreateButton(m_Panel, "Run GPU", UiFactory.AccentColor, () => StartSpike("gpu"));
            UiFactory.Place((RectTransform)gpu.transform, 240, 606, 200, 70);
            var stop = UiFactory.CreateButton(m_Panel, "Stop", UiFactory.CardColor, () => m_Engine?.Cancel());
            UiFactory.Place((RectTransform)stop.transform, 456, 606, 200, 70);
            var close = UiFactory.CreateButton(m_Panel, "Close", UiFactory.CardColor, Close);
            UiFactory.Place((RectTransform)close.transform, 672, 606, 204, 70);
        }
    }
}
