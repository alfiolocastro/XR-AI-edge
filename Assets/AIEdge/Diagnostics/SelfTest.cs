using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using AIEdge.AskImage;
using AIEdge.Chat;
using AIEdge.Spatial;
using AIEdge.TinyGarden;
using AIEdge.WhatsThat;
using UnityEngine;

namespace AIEdge.Diagnostics
{
    /// <summary>
    /// On-device checks for the core layer: download resume and integrity, gated-model errors, generation
    /// cancel and conversation reset, and memory after unload. Runs at startup only when
    /// <c>persistentDataPath/selftest.json</c> exists, and logs <c>SELFTEST|{json}</c> lines (logcat tag Unity).
    /// Expects Gemma 4 E2B already downloaded (used as the reference for the byte comparison).
    /// </summary>
    public class SelfTest : MonoBehaviour
    {
        const long k_ChunkBytes = 32L * 1024 * 1024;
        const int k_CompareWindow = 512 * 1024;

        LlmService m_Service;

        string m_Config;

        async void Start()
        {
            var configPath = Path.Combine(Application.persistentDataPath, "selftest.json");
            if (!File.Exists(configPath))
                return;

            // {"tests":["chat_multiturn"]} runs only the named tests; anything else runs them all.
            m_Config = File.ReadAllText(configPath);

            m_Service = LlmService.Instance;
            await Awaitable.WaitForSecondsAsync(2f);
            Report("start", true, "");

            await Run("resume_download", TestResumeDownload);
            await Run("small_download", TestSmallDownload);
            await Run("gated_without_token", TestGatedWithoutToken);
            await Run("cancel_and_reset", TestCancelAndReset);
            await Run("unload_memory", TestUnloadMemory);
            await Run("chat_multiturn", TestChatMultiTurn);
            await Run("ask_image", TestAskImage);
            await Run("garden", TestGarden);
            await Run("garden_view", TestGardenView);
            await Run("depth", TestDepth);
            await Run("depth_ask", TestDepthAsk);
            await Run("speech", TestSpeech);
            await Run("speech_it", TestSpeechItalian);
            await Run("whats_that", TestWhatsThat);
            await Run("people", TestPeople);
            await Run("instructions", TestInstructions);
            await Run("instructions_it", TestInstructionsItalian);
            await Run("stt", TestSpeechInput);
            await Run("mic_gesture", TestMicGesture);
            await Run("depth_place", TestDepthPlace);
            await Run("depth_garden", TestDepthGarden);

            Report("finished", true, "");
        }

        async Awaitable Run(string name, Func<Awaitable<string>> test)
        {
            if (m_Config.Contains("\"tests\"") && !m_Config.Contains($"\"{name}\""))
                return;

            var savedLanguage = AppLanguage.Current;
            var savedText = UserInstructions.Text;
            var savedEnabled = UserInstructions.Enabled;
            AppLanguage.Set(SpeechLanguage.English);
            if (savedEnabled)
                UserInstructions.Set(savedText, false);
            try
            {
                var detail = await test();
                Report(name, true, detail);
            }
            catch (Exception e)
            {
                Report(name, false, e.GetType().Name + ": " + e.Message);
            }
            finally
            {
                AppLanguage.Set(savedLanguage);
                UserInstructions.Set(savedText, savedEnabled);
            }
        }

        static ModelInfo CloneAs(ModelInfo source, string fileName, long sizeBytes, bool gated)
        {
            return new ModelInfo
            {
                id = "selftest-" + fileName,
                displayName = "Self-test " + fileName,
                repoId = source.repoId,
                fileName = fileName,
                commitHash = source.commitHash,
                sizeBytes = sizeBytes,
                gated = gated,
            };
        }

        async Awaitable<string> TestResumeDownload()
        {
            var reference = ModelCatalog.Gemma4E2B;
            if (ModelStore.GetState(reference) != ModelState.Downloaded)
                throw new InvalidOperationException("Gemma 4 E2B must be downloaded to compare bytes.");

            // Same remote file as Gemma 4 E2B. It has the same local file name, so every file operation below
            // must happen inside the scratch root, never the real models folder.
            var referencePath = ModelStore.GetPath(reference);
            var test = CloneAs(reference, reference.fileName, reference.sizeBytes, false);
            var scratchRoot = Path.Combine(Application.temporaryCachePath, "selftest");
            Directory.CreateDirectory(scratchRoot);
            ModelStore.SetRootForTests(scratchRoot);
            try
            {
                ModelStore.Delete(test);
                var first = await DownloadUntil(test, k_ChunkBytes);
                var firstCode = m_Service.Downloader.LastResponseCode;
                if (ModelStore.GetState(test) != ModelState.Partial)
                    throw new Exception("Expected a partial file after cancelling.");

                var second = await DownloadUntil(test, first + k_ChunkBytes);
                var resumeCode = m_Service.Downloader.LastResponseCode;
                if (second <= first)
                    throw new Exception($"Partial file did not grow on resume ({first} -> {second}).");
                if (resumeCode != 206)
                    throw new Exception($"Resume answered HTTP {resumeCode}, expected 206.");

                // Bytes around the resume point must match the complete file.
                var start = first - k_CompareWindow;
                if (!RangesEqual(ModelStore.GetPartialPath(test), referencePath, start, 2 * k_CompareWindow))
                    throw new Exception("Bytes around the resume point differ from the reference file.");

                return $"first {first} B (HTTP {firstCode}), resumed to {second} B (HTTP {resumeCode}), {2 * k_CompareWindow} B around offset {first} match the reference";
            }
            finally
            {
                ModelStore.Delete(test);
                ModelStore.SetRootForTests(null);
            }
        }

        async Awaitable<long> DownloadUntil(ModelInfo model, long targetBytes)
        {
            using var cancel = new CancellationTokenSource();
            var progress = new Progress<DownloadProgress>(p =>
            {
                if (p.Bytes >= targetBytes)
                    cancel.Cancel();
            });

            try
            {
                await m_Service.Downloader.DownloadAsync(model, progress, cancel.Token);
                throw new Exception("Download finished before it could be cancelled.");
            }
            catch (OperationCanceledException)
            {
            }

            return ModelStore.GetPartialBytes(model);
        }

        static bool RangesEqual(string pathA, string pathB, long offset, int length)
        {
            var a = new byte[length];
            var b = new byte[length];
            using (var fa = File.OpenRead(pathA))
            {
                fa.Seek(offset, SeekOrigin.Begin);
                if (fa.Read(a, 0, length) != length)
                    return false;
            }

            using (var fb = File.OpenRead(pathB))
            {
                fb.Seek(offset, SeekOrigin.Begin);
                if (fb.Read(b, 0, length) != length)
                    return false;
            }

            for (var i = 0; i < length; i++)
            {
                if (a[i] != b[i])
                    return false;
            }

            return true;
        }

        async Awaitable<string> TestSmallDownload()
        {
            // Small file with unknown size: exercises the complete-and-move path.
            var readme = CloneAs(ModelCatalog.Gemma4E2B, "README.md", 0, false);
            ModelStore.SetRootForTests(Path.Combine(Application.temporaryCachePath, "selftest"));
            try
            {
                ModelStore.Delete(readme);
                await m_Service.Downloader.DownloadAsync(readme);
                if (ModelStore.GetState(readme) != ModelState.Downloaded)
                    throw new Exception("README.md not marked as downloaded.");
                if (File.Exists(ModelStore.GetPartialPath(readme)))
                    throw new Exception("Partial file left behind.");
                return $"{new FileInfo(ModelStore.GetPath(readme)).Length} B, HTTP {m_Service.Downloader.LastResponseCode}";
            }
            finally
            {
                ModelStore.Delete(readme);
                ModelStore.SetRootForTests(null);
            }
        }

        async Awaitable<string> TestGatedWithoutToken()
        {
            if (ModelStore.HuggingFaceToken != null)
                return "skipped: a token is installed";

            // Marked not gated so the request reaches Hugging Face and the 401 path runs.
            var gated = CloneAs(ModelCatalog.TinyGarden, ModelCatalog.TinyGarden.fileName, ModelCatalog.TinyGarden.sizeBytes, false);
            gated.repoId = ModelCatalog.TinyGarden.repoId;
            var scratchRoot = Path.Combine(Application.temporaryCachePath, "selftest");
            ModelStore.SetRootForTests(scratchRoot);
            try
            {
                ModelStore.Delete(gated);
                try
                {
                    await m_Service.Downloader.DownloadAsync(gated);
                }
                catch (ModelAccessException e)
                {
                    if (File.Exists(ModelStore.GetPartialPath(gated)))
                        throw new Exception("Error body left in the partial file.");
                    return $"HTTP {m_Service.Downloader.LastResponseCode}: {e.Message}";
                }

                throw new Exception("Gated download succeeded without a token.");
            }
            finally
            {
                ModelStore.Delete(gated);
                ModelStore.SetRootForTests(null);
            }
        }

        async Awaitable<string> TestCancelAndReset()
        {
            await m_Service.LoadAsync(ModelCatalog.Gemma4E2B);
            var engine = m_Service.Engine;
            m_Service.NewConversation();

            // 1. Cancel a long answer shortly after it starts streaming.
            var turn = new Turn(engine);
            engine.Send("Count from 1 to 300, separated by commas.");
            while (turn.Text.Length == 0 && !turn.Done)
                await Awaitable.NextFrameAsync();
            await Awaitable.WaitForSecondsAsync(1.5f);
            var cancelAt = Time.realtimeSinceStartup;
            engine.Cancel();
            var cancelled = await turn.WaitAsync(10f);
            var stopLatency = Time.realtimeSinceStartup - cancelAt;
            if (!cancelled.cancelled)
                throw new Exception("Turn finished without the cancelled flag.");

            // 2. The conversation keeps working after a stop.
            turn = new Turn(engine);
            engine.Send("Reply with only the word: hello");
            var after = await turn.WaitAsync(30f);
            if (after.cancelled || turn.Text.ToString().Trim().Length == 0)
                throw new Exception("No answer after cancel.");
            var kvBeforeReset = after.kvTokens;
            var answerAfterStop = turn.Text.ToString().Trim();

            // 3. Reset drops the history.
            m_Service.NewConversation();
            turn = new Turn(engine);
            engine.Send("Reply with only the word: ok");
            var reset = await turn.WaitAsync(30f);
            if (reset.kvTokens >= kvBeforeReset)
                throw new Exception($"KV cache not cleared by reset ({kvBeforeReset} -> {reset.kvTokens}).");

            return $"stopped in {stopLatency:0.00}s after {cancelled.decodeChunks} chunks; answer after stop \"{answerAfterStop}\"; KV {kvBeforeReset} -> {reset.kvTokens} after reset; decode {after.decodeTokPerSec:0.0} tok/s";
        }

        async Awaitable<string> TestUnloadMemory()
        {
            var gemma = await m_Service.LoadAsync(ModelCatalog.Gemma4E2B);
            var gemmaPss = gemma.pssMb;
            m_Service.Unload();
            await Awaitable.WaitForSecondsAsync(2f);

            if (ModelStore.GetState(ModelCatalog.TinyGarden) != ModelState.Downloaded)
                return $"Gemma loaded {gemmaPss} MB; Tiny Garden not downloaded, skipped reload";

            var garden = await m_Service.LoadAsync(ModelCatalog.TinyGarden);
            m_Service.Unload();
            if (garden.pssMb >= gemmaPss)
                throw new Exception($"Memory not released: Gemma {gemmaPss} MB, Tiny Garden after unload {garden.pssMb} MB.");
            return $"Gemma loaded {gemmaPss} MB -> unload -> Tiny Garden loaded {garden.pssMb} MB";
        }

        // Drives the AI Chat panel like a user: two dependent turns, then resume the saved thread from disk.
        // Leaves the chat open on the test thread for a screenshot.
        async Awaitable<string> TestChatMultiTurn()
        {
            var app = FindAnyObjectByType<AIEdgeApp>();
            var chat = app.Chat;
            app.OpenChat();
            chat.NewChat();

            var first = await ChatTurnAsync(chat, "My name is Ada and my favorite color is teal. Reply with one short sentence.");
            var second = await ChatTurnAsync(chat, "What is my favorite color? Answer with one word.");
            if (!second.text.ToLowerInvariant().Contains("teal"))
                throw new Exception($"Second turn lost context: \"{second.text}\"");

            var id = chat.CurrentThread.id;
            chat.NewChat();
            var saved = ChatThreadStore.Load(id);
            if (saved == null || saved.messages.Count != 4)
                throw new Exception($"Thread not saved with 4 messages ({saved?.messages.Count}).");

            chat.OpenThread(saved);
            var resumed = await ChatTurnAsync(chat, "What is my name? Answer with one word.");
            if (!resumed.text.Contains("Ada"))
                throw new Exception($"Resumed thread lost context: \"{resumed.text}\"");

            return $"turn1 TTFT {first.stats.ttftSec:0.00}s {first.stats.decodeTokPerSec:0.0} tok/s; turn2 \"{second.text.Trim()}\" KV {second.stats.kvTokens}; " +
                $"resumed \"{resumed.text.Trim()}\" TTFT {resumed.stats.ttftSec:0.00}s KV {resumed.stats.kvTokens}";
        }

        // Opens Point & Ask, captures the region straight ahead and asks about it. Copies the crop and the full frame
        // to persistentDataPath (selftest_crop.png, selftest_frame.png) to check orientation. Leaves the card open.
        async Awaitable<string> TestAskImage()
        {
            var app = FindAnyObjectByType<AIEdgeApp>();
            var ask = app.AskImage;
            if (app.Chat.IsOpen)
                app.Chat.Close();
            app.OpenAskImage();

            var deadline = Time.realtimeSinceStartup + 120f;
            while (!ask.IsReady && Time.realtimeSinceStartup < deadline)
                await Awaitable.NextFrameAsync();
            if (!ask.IsReady)
                throw new TimeoutException("Point & Ask never became ready (model or camera).");

            AskResult result = null;
            void OnFinished(AskResult r) => result = r;
            ask.AnswerFinished += OnFinished;
            try
            {
                ask.AskInFront("What is in this image? Answer in one sentence.");
                while (result == null && Time.realtimeSinceStartup < deadline)
                    await Awaitable.NextFrameAsync();
            }
            finally
            {
                ask.AnswerFinished -= OnFinished;
            }

            if (result == null)
                throw new TimeoutException("No answer.");
            if (result.Error != null)
                throw new LlmException(result.Error);

            File.Copy(result.ImagePath, Path.Combine(Application.persistentDataPath, "selftest_crop.png"), true);
            var camera = FindAnyObjectByType<PassthroughFrameCapture>();
            camera.CaptureToFile(Path.Combine(Application.persistentDataPath, "selftest_frame.png"));
            return $"crop {result.Crop.x},{result.Crop.y} {result.Crop.width}x{result.Crop.height} of {camera.Resolution.x}x{camera.Resolution.y}; " +
                $"TTFT {result.Stats.ttftSec:0.00}s {result.Stats.decodeTokPerSec:0.0} tok/s; answer \"{result.Answer.Trim()}\"";
        }

        // Plays Tabletop Garden through the model: all three tools, pointing ("these", "here") through a simulated
        // selection, growth through Tick, and the special-seed unlock. Leaves the garden open for a screenshot.
        async Awaitable<string> TestGarden()
        {
            var app = FindAnyObjectByType<AIEdgeApp>();
            if (app.AskImage.IsOpen)
                app.AskImage.Close();
            if (app.Chat.IsOpen)
                app.Chat.Close();
            app.OpenGarden();

            var panel = app.Garden;
            var state = panel.State;
            var log = new StringBuilder();
            var deadline = Time.realtimeSinceStartup + 60f;
            while (!panel.IsReady && Time.realtimeSinceStartup < deadline)
                await Awaitable.NextFrameAsync();
            if (!panel.IsReady)
                throw new TimeoutException("Garden model never became ready.");
            panel.ResetGarden();

            async Awaitable Step(string text, int[] pointAt, Func<bool> check)
            {
                if (pointAt != null)
                    panel.Garden.SetSelection(pointAt);

                GardenCommandResult result = null;
                void OnFinished(GardenCommandResult r) => result = r;
                panel.CommandFinished += OnFinished;
                try
                {
                    panel.Submit(text);
                    var stepDeadline = Time.realtimeSinceStartup + 30f;
                    while (result == null && Time.realtimeSinceStartup < stepDeadline)
                        await Awaitable.NextFrameAsync();
                }
                finally
                {
                    panel.CommandFinished -= OnFinished;
                }

                if (result == null)
                    throw new TimeoutException($"No result for \"{text}\".");
                var calls = string.Join(" ", result.Calls);
                log.Append($"[{text}{(result.ResolvedText != text ? " => " + result.ResolvedText : "")}] {calls} {result.Stats?.totalSec:0.0}s; ");
                if (result.Error != null || !check())
                    throw new Exception($"\"{text}\" -> {calls}; events: {string.Join(", ", result.Events)}. Log so far: {log}");
            }

            bool All(PlotStage stage, Seed seed, params int[] plots) => plots.All(p => state[p].stage == stage && state[p].seed == seed);

            await Step("plant sunflowers in the top row", null, () => All(PlotStage.Seed, Seed.Sunflower, 1, 2, 3));
            await Step("water the top row", null, () => All(PlotStage.Sprout, Seed.Sunflower, 1, 2, 3));
            state.Tick(GardenState.GrowSeconds);
            await Step("water these", new[] { 1, 2, 3 }, () => All(PlotStage.Bud, Seed.Sunflower, 1, 2, 3));
            state.Tick(GardenState.GrowSeconds);
            await Step("plant a rose here", new[] { 5 }, () => All(PlotStage.Seed, Seed.Rose, 5));
            await Step("harvest the top row", null, () => state.HarvestedCount(Seed.Sunflower) == 3 && state.SpecialUnlocked);
            await Step("plant the special seed in plot 9", null, () => All(PlotStage.Seed, Seed.Special, 9));
            await Step("water plots 5 and 9", null, () => All(PlotStage.Sprout, Seed.Rose, 5) && All(PlotStage.Sprout, Seed.Special, 9));
            return log.ToString();
        }

        // Screenshot staging, not a model test: puts every growth stage on the planter and lifts it into view of a
        // headset lying on a desk.
        async Awaitable<string> TestGardenView()
        {
            var app = FindAnyObjectByType<AIEdgeApp>();
            if (!app.Garden.IsOpen)
                app.OpenGarden();

            var state = app.Garden.State;
            state.Reset();
            state.Plant(Seed.Sunflower, new[] { 1, 4 });
            state.Plant(Seed.Daisy, new[] { 2, 5 });
            state.Plant(Seed.Rose, new[] { 3, 6 });
            state.Plant(Seed.Daisy, new[] { 7 });
            state.Plant(Seed.Rose, new[] { 8 });
            state.Water(new[] { 1, 2, 3, 4, 5, 6 });
            state.Tick(GardenState.GrowSeconds);
            state.Water(new[] { 1, 2, 3 });
            state.Tick(GardenState.GrowSeconds);
            state.Water(new[] { 4 });
            state.Water(new[] { 8 });
            app.Garden.Garden.SetSelection(new[] { 5 });

            var head = Camera.main.transform;
            var forward = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            app.Garden.Garden.Place(head.position + forward * 0.65f + Vector3.down * 0.3f, -forward);
            await Awaitable.WaitForSecondsAsync(1f);
            return "row 1 flowers, plot 4 bud, plots 5-6 thirsty, plot 7 seed, plot 8 sprout, plot 9 empty, plot 5 selected";
        }

        // Depth is ready and answers: a ray straight ahead and a 7x7 grid over the view.
        async Awaitable<string> TestDepth()
        {
            var depth = DepthService.Instance;
            if (depth == null)
                throw new Exception("No DepthService in the scene.");
            if (!depth.IsSupported)
                throw new Exception("Environment raycast not supported.");
            var started = Time.realtimeSinceStartup;
            if (!await depth.WaitUntilReadyAsync(20f))
                throw new TimeoutException("Depth never became ready (scene permission?).");
            var readySec = Time.realtimeSinceStartup - started;

            var head = Camera.main.transform;
            depth.Raycast(new Ray(head.position, head.forward), out var hit);
            var hits = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (var x = -3; x <= 3; x++)
            for (var y = -3; y <= 3; y++)
                if (depth.Raycast(new Ray(head.position, Quaternion.Euler(y * 5f, x * 5f, 0f) * head.forward), out _))
                    hits++;
            var gridMs = sw.Elapsed.TotalMilliseconds;
            return $"ready after {readySec:0.0}s; ahead {hit.status} at {Vector3.Distance(head.position, hit.point):0.00} m, normal {hit.normal}, confidence {hit.normalConfidence:0.00}; 49-ray grid {hits} hits in {gridMs:0.0} ms";
        }

        // Exit criterion: measurements reach the answer. Captures straight ahead with a size question.
        async Awaitable<string> TestDepthAsk()
        {
            var app = FindAnyObjectByType<AIEdgeApp>();
            if (app.Garden.IsOpen)
                app.Garden.Close();
            if (app.Chat.IsOpen)
                app.Chat.Close();
            if (!app.AskImage.IsOpen)
                app.OpenAskImage();
            var ask = app.AskImage;

            var deadline = Time.realtimeSinceStartup + 120f;
            while (!ask.IsReady && Time.realtimeSinceStartup < deadline)
                await Awaitable.NextFrameAsync();
            if (!ask.IsReady)
                throw new TimeoutException("Point & Ask never became ready.");

            AskResult result = null;
            void OnFinished(AskResult r) => result = r;
            ask.AnswerFinished += OnFinished;
            try
            {
                ask.AskInFront("What is the main object here, and roughly how big is it?");
                while (result == null && Time.realtimeSinceStartup < deadline)
                    await Awaitable.NextFrameAsync();
            }
            finally
            {
                ask.AnswerFinished -= OnFinished;
            }

            if (result == null)
                throw new TimeoutException("No answer.");
            if (result.Error != null)
                throw new LlmException(result.Error);
            File.Copy(result.ImagePath, Path.Combine(Application.persistentDataPath, "selftest_depth_crop.png"), true);
            if (result.Depth == null)
                throw new Exception("No depth measurement for the capture.");
            var metric = System.Text.RegularExpressions.Regex.IsMatch(result.Answer, @"\d+(\.\d+)?\s*(cm|centimet|m\b|meter|metre|inch)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var detail = $"crop {result.Crop.width}x{result.Crop.height} ({result.Depth.Cells} cells{(result.Depth.FillsFrame ? ", fills frame" : "")}); facts \"{result.Facts}\"; answer \"{result.Answer.Trim()}\"";
            if (!metric)
                throw new Exception("Answer has no measurement. " + detail);
            return detail;
        }

        // PlaceBox sweep toward the floor, for a headset lying on a desk (it can't look down at a table like a
        // user). Reports every angle; passes if any footprint fits somewhere.
        async Awaitable<string> TestDepthPlace()
        {
            var depth = DepthService.Instance;
            if (depth == null || !await depth.WaitUntilReadyAsync(20f))
                throw new Exception("Depth not ready.");

            var head = Camera.main.transform;
            var forward = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            var right = Vector3.Cross(Vector3.up, forward);
            var log = new StringBuilder();
            var placed = 0;
            foreach (var size in new[] { 0.46f, 0.3f })
            for (var pitch = 10; pitch <= 40; pitch += 3)
            {
                var ray = new Ray(head.position, Quaternion.AngleAxis(pitch, right) * forward);
                if (depth.PlaceBox(ray, new Vector3(size, size, 0.02f), forward, out var hit))
                {
                    placed++;
                    log.Append($"{size * 100:0}cm@{pitch}°: fit n.y {hit.normal.y:0.00} at {Vector3.Distance(head.position, hit.point):0.00} m; ");
                }
                else
                {
                    depth.Raycast(ray, out var probe);
                    log.Append($"{size * 100:0}cm@{pitch}°: {probe.status} n.y {probe.normal.y:0.00}; ");
                }
            }

            if (placed == 0)
                throw new Exception("No PlaceBox fit at any angle. " + log);
            return $"{placed} fits. {log}";
        }

        // On-device text to speech: the engine binds, speaks a sentence, and reads a streamed chat reply sentence by
        // sentence, starting before the reply finishes.
        async Awaitable<string> TestSpeech()
        {
            var speech = SpeechEngine.Instance;
            if (speech == null)
                throw new Exception("No SpeechEngine in the scene.");
            speech.Enabled = true;

            // The neural voice: download it if missing (Quest's system engine is disabled).
            var download = "";
            var voice = VoiceCatalog.Default;
            if (VoiceStore.GetState(voice) != ModelState.Downloaded)
            {
                var downloadStart = Time.realtimeSinceStartup;
                await speech.Downloader.DownloadAsync(voice);
                download = $"voice downloaded in {Time.realtimeSinceStartup - downloadStart:0}s; ";
            }

            if (!speech.IsNeural)
                speech.SelectEngine();
            var deadline = Time.realtimeSinceStartup + 30f;
            while (!speech.IsReady && Time.realtimeSinceStartup < deadline)
                await Awaitable.NextFrameAsync();
            if (!speech.IsReady)
                throw new TimeoutException("Speech engine never became ready: " + speech.Info);

            var started = false;
            void OnStarted() => started = true;
            speech.UtteranceStarted += OnStarted;
            var spokenAt = Time.realtimeSinceStartup;
            var frameStart = Time.frameCount;
            speech.Speak("Hello from X R A I Edge. This sentence checks the frame rate while the voice is synthesized.", interrupt: true);
            while (!started && Time.realtimeSinceStartup < spokenAt + 5f)
                await Awaitable.NextFrameAsync();
            speech.UtteranceStarted -= OnStarted;
            if (!started)
                throw new Exception("No audio started. " + speech.Info);
            var startLatency = Time.realtimeSinceStartup - spokenAt;
            while (speech.IsSpeaking && Time.realtimeSinceStartup < spokenAt + 20f)
                await Awaitable.NextFrameAsync();
            var sentenceSec = Time.realtimeSinceStartup - spokenAt;
            var fps = (Time.frameCount - frameStart) / Mathf.Max(0.01f, sentenceSec);

            // A chat reply: speech should start while the reply is still streaming.
            var app = FindAnyObjectByType<AIEdgeApp>();
            if (!app.Chat.IsOpen)
                app.OpenChat();
            app.Chat.NewChat();
            var before = speech.UtteranceCount;
            var reply = await ChatTurnAsync(app.Chat, "Name three planets and one fact about each, in three short sentences.");
            var utterances = speech.UtteranceCount - before;
            var latency = speech.LastReplyAudioLatency;
            if (utterances == 0 || latency < 0f)
                throw new Exception($"Reply was not spoken ({utterances} utterances, latency {latency}).");
            var streamed = latency < reply.stats.totalSec;
            return $"{download}{speech.Info}; first sentence audio after {startLatency:0.00}s, done in {sentenceSec:0.0}s at {fps:0} fps; " +
                $"chat reply ({reply.stats.totalSec:0.0}s to generate) spoken as {utterances} utterances, audio after {latency:0.00}s" +
                (streamed ? " (before the reply finished)" : "");
        }

        // Italian replies: the Italian voice is loaded next to the English one, and a reply in Italian is read by it.
        async Awaitable<string> TestSpeechItalian()
        {
            var speech = SpeechEngine.Instance;
            if (speech == null)
                throw new Exception("No SpeechEngine in the scene.");
            speech.Enabled = true;

            var download = "";
            var voice = VoiceCatalog.Paola;
            if (VoiceStore.GetState(voice) != ModelState.Downloaded)
            {
                var downloadStart = Time.realtimeSinceStartup;
                await speech.Downloader.DownloadAsync(voice);
                download = $"voice downloaded in {Time.realtimeSinceStartup - downloadStart:0}s; ";
            }

            if (!speech.IsVoiceReady(voice))
                speech.SelectEngine();
            var deadline = Time.realtimeSinceStartup + 30f;
            while (!speech.IsVoiceReady(voice) && Time.realtimeSinceStartup < deadline)
                await Awaitable.NextFrameAsync();
            if (!speech.IsVoiceReady(voice))
                throw new TimeoutException("Italian voice never became ready: " + speech.Info);

            // With Italiano selected in the launcher and an English personal instruction on, the whole chat reply is
            // in Italian and read by the Italian voice.
            AppLanguage.Set(SpeechLanguage.Italian);
            UserInstructions.Set("Talk like a pirate.", true);
            var app = FindAnyObjectByType<AIEdgeApp>();
            if (!app.Chat.IsOpen)
                app.OpenChat();
            app.Chat.NewChat();
            var before = speech.UtteranceCount;
            var voices = new HashSet<string>();
            void OnUtterance() => voices.Add(speech.LastLanguage);
            speech.UtteranceStarted += OnUtterance;
            ChatMessage reply;
            try
            {
                reply = await ChatTurnAsync(app.Chat, "Dimmi due curiosità sulla Luna, in due frasi brevi.");
                var spokenDeadline = Time.realtimeSinceStartup + 20f;
                while (speech.IsSpeaking && Time.realtimeSinceStartup < spokenDeadline)
                    await Awaitable.NextFrameAsync();
            }
            finally
            {
                speech.UtteranceStarted -= OnUtterance;
            }

            var utterances = speech.UtteranceCount - before;
            if (utterances == 0)
                throw new Exception("Reply was not spoken.");
            if (speech.LastLanguage != SpeechLanguage.Italian || voices.Contains(SpeechLanguage.English))
                throw new Exception($"Reply spoken in '{string.Join(",", voices)}', expected Italian only. Reply: {reply.text}");
            var englishSentences = Regex.Split(reply.text, @"(?<=[.!?])\s+").Where(x => SpeechLanguage.Detect(x) == SpeechLanguage.English).ToList();
            if (englishSentences.Count > 0)
                throw new Exception($"English sentence(s) in an Italian reply: \"{string.Join("\" \"", englishSentences)}\". Reply: {reply.text}");
            var latency = speech.LastReplyAudioLatency;
            app.Chat.Close();

            // An English sentence right after should switch back to the English voice.
            speech.Speak("And this sentence is in English again.", interrupt: true);
            var english = speech.LastLanguage;
            var fps = 1f / Mathf.Max(0.001f, Time.smoothDeltaTime);
            if (english != SpeechLanguage.English)
                throw new Exception($"English sentence spoken in '{english}'.");
            return $"{download}{speech.Info}; Italian reply (pirate instruction on) spoken as {utterances} utterances by the Italian voice, " +
                $"audio after {latency:0.00}s: {reply.text.Replace("\n", " ")}; English sentence switched back to English; {fps:0} fps";
        }

        // What's That?: point straight ahead (the gaze ray stands in for a hand), and Gemma names what depth found
        // there; the card and the spoken reply carry the measured distance. Repeated in Italian.
        async Awaitable<string> TestWhatsThat()
        {
            var app = FindAnyObjectByType<AIEdgeApp>();
            foreach (var open in new Action[] { () => { if (app.Chat.IsOpen) app.Chat.Close(); }, () => { if (app.AskImage.IsOpen) app.AskImage.Close(); }, () => { if (app.Garden.IsOpen) app.Garden.Close(); } })
                open();
            var panel = app.WhatsThat;
            if (!panel.IsOpen)
                app.OpenWhatsThat();

            var deadline = Time.realtimeSinceStartup + 120f;
            while (!panel.IsReady && Time.realtimeSinceStartup < deadline)
                await Awaitable.NextFrameAsync();
            if (!panel.IsReady)
                throw new TimeoutException("What's That? never became ready.");

            var report = new StringBuilder();
            var speech = SpeechEngine.Instance;
            foreach (var language in new[] { SpeechLanguage.English, SpeechLanguage.Italian })
            {
                panel.Language = language;
                WhatsThatResult result = null;
                void OnFinished(WhatsThatResult r) => result = r;
                panel.AnswerFinished += OnFinished;
                var head = Camera.main.transform;
                // Slightly below eye level, where objects usually are.
                var ray = new Ray(head.position, Quaternion.AngleAxis(15f, head.right) * head.forward);
                var before = speech != null ? speech.UtteranceCount : 0;
                if (!panel.AskAlong(ray))
                {
                    panel.AnswerFinished -= OnFinished;
                    throw new Exception("Depth found nothing straight ahead.");
                }

                deadline = Time.realtimeSinceStartup + 90f;
                while (result == null && Time.realtimeSinceStartup < deadline)
                    await Awaitable.NextFrameAsync();
                panel.AnswerFinished -= OnFinished;
                if (result == null)
                    throw new TimeoutException("No answer.");
                if (result.Error != null)
                    throw new Exception(result.Error);
                if (string.IsNullOrWhiteSpace(result.Name))
                    throw new Exception("The answer has no name: " + result.Reply);
                if (result.Distance <= 0.1f || result.Distance > 6f)
                    throw new Exception($"Implausible distance {result.Distance} m.");

                // Let the reply and the distance sentence be queued and spoken.
                var spokenDeadline = Time.realtimeSinceStartup + 15f;
                while (speech != null && speech.IsSpeaking && Time.realtimeSinceStartup < spokenDeadline)
                    await Awaitable.NextFrameAsync();
                var utterances = speech != null ? speech.UtteranceCount - before : 0;

                // The highlight: depth outline first, then the segmentation cut-out.
                var segmentDeadline = Time.realtimeSinceStartup + 15f;
                while (!result.SegmentationDone && Time.realtimeSinceStartup < segmentDeadline)
                    await Awaitable.NextFrameAsync();
                var segmentation = result.Segmentation;
                if (segmentation == null)
                    throw new TimeoutException("Segmentation never finished.");
                if (segmentation.Error != null)
                    throw new Exception("Segmentation failed: " + segmentation.Error);
                if (segmentation.Mask != null)
                    SaveMaskOverlay(result.ImagePath, segmentation, Path.Combine(Application.persistentDataPath, $"selftest_segmentation_{language}.png"));

                var size = result.Depth != null && !result.Depth.FillsFrame ? $", {DepthRegion.FormatLength(result.Depth.Width)} wide" : "";
                report.Append($"[{language}] \"{result.Name}\" at {DepthRegion.FormatLength(result.Distance)}{size}: " +
                    $"{result.Description} ({result.Stats.totalSec:0.0}s, {utterances} utterances, last in '{speech?.LastLanguage}', " +
                    $"distance said as \"{PointerMath.SpokenDistance(result.Distance, language)}\"); " +
                    $"highlight {result.HighlightSource}, segmentation {segmentation.InferMs} ms, coverage {segmentation.Coverage:P0}, " +
                    $"at point {segmentation.AtKeypoint:0.00}, usable {segmentation.IsUsable} ({segmentation.Info}). ");
            }

            panel.Language = SpeechLanguage.English;
            return report.ToString();
        }

        // People: the detector finds the two people at the bottom left of a test photo (people_test.jpg, pushed next
        // to selftest.json), from the file and through the live GPU path (which checks the image isn't upside down),
        // then runs on the live camera for a few seconds at a few frames a second.
        async Awaitable<string> TestPeople()
        {
            var app = FindAnyObjectByType<AIEdgeApp>();
            if (app.Chat.IsOpen) app.Chat.Close();
            if (app.AskImage.IsOpen) app.AskImage.Close();
            if (app.Garden.IsOpen) app.Garden.Close();
            var panel = app.WhatsThat;
            if (!panel.IsOpen)
                app.OpenWhatsThat();
            var detector = panel.People;
            var savedEnabled = PersonDetector.Enabled;
            PersonDetector.Enabled = true;
            try
            {
                var path = Path.Combine(Application.persistentDataPath, "people_test.jpg");
                if (!File.Exists(path))
                    throw new FileNotFoundException("Push people_test.jpg next to selftest.json.");

                static PersonBox Largest(List<PersonBox> boxes, string from)
                {
                    if (boxes.Count == 0)
                        throw new Exception($"No people found in the photo ({from}).");
                    var largest = boxes.OrderByDescending(b => b.Viewport.width * b.Viewport.height).First();
                    var c = largest.Viewport.center;
                    if (c.x > 0.35f || c.y > 0.45f)
                        throw new Exception($"The largest person ({from}) is at {c}, not at the bottom left: the image is flipped.");
                    return largest;
                }

                var fromFile = await detector.DetectFileAsync(path);
                var fileResult = detector.LastResultJson;
                var fileBox = Largest(fromFile, "file");

                var texture = new Texture2D(2, 2);
                texture.LoadImage(File.ReadAllBytes(path));
                detector.DumpNextFrame = Path.Combine(Application.persistentDataPath, "selftest_people_input.png");
                var fromTexture = await detector.DetectTextureAsync(texture);
                var textureBox = Largest(fromTexture, $"GPU path: {detector.LastResultJson}; file: {fileResult}");
                Destroy(texture);
                var overlap = PersonMath.Overlap(fileBox.Viewport, textureBox.Viewport);
                if (overlap < 0.6f)
                    throw new Exception($"File and GPU boxes differ (overlap {overlap:0.00}).");

                // Live: the camera must be streaming, then frames keep coming.
                var deadline = Time.realtimeSinceStartup + 30f;
                var camera = FindAnyObjectByType<PassthroughFrameCapture>();
                while ((camera == null || !camera.IsReady) && Time.realtimeSinceStartup < deadline)
                    await Awaitable.NextFrameAsync();
                if (camera == null || !camera.IsReady)
                    throw new TimeoutException("The camera never streamed.");
                await Awaitable.WaitForSecondsAsync(1f);
                var frames = detector.FrameCount;
                const float seconds = 6f;
                await Awaitable.WaitForSecondsAsync(seconds);
                var rate = (detector.FrameCount - frames) / seconds;
                // A live frame as the detector sees it, to check it's upright.
                detector.DumpNextFrame = Path.Combine(Application.persistentDataPath, "selftest_people_live.png");
                await Awaitable.WaitForSecondsAsync(1f);
                if (detector.Error != null)
                    throw new Exception(detector.Error);
                if (rate < 2f)
                    throw new Exception($"Only {rate:0.0} frames a second.");

                var distances = detector.People.Select(p => p.Distance).ToList();
                var summary = PersonMath.Summary(distances, SpeechLanguage.English);

                // Targeting: a steady high-confidence person straight ahead (injected, as the room may be empty) gets
                // the red display with a threat level.
                TrackedPerson target = null;
                var injectUntil = Time.realtimeSinceStartup + 8f; // long enough for a screenshot
                while (Time.realtimeSinceStartup < injectUntil)
                {
                    detector.InjectForTests(new PersonBox { Viewport = new Rect(0.4f, 0.25f, 0.2f, 0.5f), Score = 0.9f });
                    target = detector.People.FirstOrDefault(p => p.IsTargeted);
                    await Awaitable.WaitForSecondsAsync(0.2f);
                }

                if (target == null)
                    throw new Exception("A 90% person never became a target.");
                if (target.Threat == ThreatLevel.Unknown)
                    throw new Exception("The target has no distance, so no threat level.");
                var hud = PersonMath.ThreatHud(target.SmoothedScore, target.Distance, target.ApproachSpeed, SpeechLanguage.English)
                    .Replace("\n", " / ");
                return $"photo: {fromFile.Count} people (file), {fromTexture.Count} (GPU), largest at " +
                    $"{fileBox.Viewport.center} score {fileBox.Score:0.00}, overlap {overlap:0.00}; live {rate:0.0} fps, " +
                    $"infer {detector.LastInferMs} ms, latency {detector.LastLatency * 1000f:0} ms, " +
                    $"\"{summary}\"; injected target: \"{hud}\"";
            }
            finally
            {
                PersonDetector.Enabled = savedEnabled;
            }
        }

        // Speech input: each voice says a known sentence, and Whisper (downloaded if missing) transcribes it in that
        // language; most words must come back. Then a short real recording checks the microphone path.
        async Awaitable<string> TestSpeechInput()
        {
            var input = SpeechInput.Instance;
            var speech = SpeechEngine.Instance;
            if (input == null || speech == null)
                throw new Exception("No SpeechInput or SpeechEngine in the scene.");

            var report = new StringBuilder();
            if (VoiceStore.GetState(SpeechInput.Model) != ModelState.Downloaded)
            {
                var downloadStart = Time.realtimeSinceStartup;
                await input.Downloader.DownloadAsync(SpeechInput.Model);
                report.Append($"Whisper downloaded in {Time.realtimeSinceStartup - downloadStart:0}s; ");
                input.Reload();
            }

            var deadline = Time.realtimeSinceStartup + 60f;
            while (!input.IsReady && input.State != SpeechInput.Status.Failed && Time.realtimeSinceStartup < deadline)
                await Awaitable.NextFrameAsync();
            if (!input.IsReady)
                throw new TimeoutException($"Speech input never became ready ({input.State}): {input.Info}");
            report.Append($"{input.Info}; ");

            var sentences = new[]
            {
                (SpeechLanguage.English, "The cat is sleeping on the kitchen sofa next to the window."),
                (SpeechLanguage.Italian, "Il gatto dorme sul divano della cucina vicino alla finestra."),
            };
            var failures = new List<string>();
            foreach (var (language, sentence) in sentences)
            {
                deadline = Time.realtimeSinceStartup + 30f;
                var voice = VoiceCatalog.ForLanguage(language);
                while (!speech.IsVoiceReady(voice) && Time.realtimeSinceStartup < deadline)
                    await Awaitable.NextFrameAsync();
                var samples = speech.Synthesize(sentence, language, out var sampleRate);
                if (samples == null || samples.Length == 0)
                    throw new Exception($"The {language} voice isn't loaded, so there is nothing to transcribe.");

                var started = Time.realtimeSinceStartup;
                var result = await input.TranscribeAsync(samples, sampleRate, language);
                var seconds = Time.realtimeSinceStartup - started;
                if (result.Error != null)
                    throw new Exception(result.Error);
                var expected = Words(sentence);
                var heard = Words(result.Text);
                var matched = expected.Count(heard.Contains) / (float)expected.Count;
                report.Append($"[{language}] heard \"{result.Text}\" ({matched:P0} of words, {seconds:0.00}s, {result.Info}); ");
                if (matched < 0.7f)
                    failures.Add($"{language}: only {matched:P0} of the words");
            }

            // The microphone path: 1.5 s of whatever the room sounds like. Silence comes back empty, not invented.
            if (await input.StartListeningAsync())
            {
                await Awaitable.WaitForSecondsAsync(1.5f);
                var level = input.Level;
                var recorded = input.RecordedSeconds;
                var result = await input.StopAndTranscribeAsync(SpeechLanguage.English);
                report.Append($"microphone: {recorded:0.0}s recorded, level {level:0.00}, heard \"{result.Text}\" {result.Info}");
                if (recorded < 1f)
                    failures.Add($"microphone recorded only {recorded:0.0}s");
            }
            else
            {
                failures.Add("microphone: " + input.Info);
            }

            if (failures.Count > 0)
                throw new Exception(string.Join("; ", failures) + ". " + report);
            return report.ToString();

            static HashSet<string> Words(string text) => new HashSet<string>(
                Regex.Matches(text.ToLowerInvariant(), @"[\p{L}']+").Cast<Match>().Select(m => m.Value));
        }

        // The mic gesture, with a simulated fist: in Chat, holding it at the mouth starts listening with the start tone and
        // symbol, moving it away stops with the stop tone and uses what was heard; with no panel that takes speech it
        // only shows a hint. Also reports what the real hands and controllers read right now.
        async Awaitable<string> TestMicGesture()
        {
            var gesture = AIEdge.Interaction.MicGesture.Instance;
            var feedback = AIEdge.UI.MicFeedback.Instance;
            var input = SpeechInput.Instance;
            if (gesture == null || feedback == null || input == null)
                throw new Exception("MicGesture, MicFeedback or SpeechInput is missing.");
            var report = new StringBuilder();
            report.Append($"live tracking: {gesture.Source ?? "nothing"} at {gesture.Distance:0.00} m; ");

            var deadline = Time.realtimeSinceStartup + 60f;
            while (!input.IsReady && Time.realtimeSinceStartup < deadline)
                await Awaitable.NextFrameAsync();
            if (!input.IsReady)
                throw new TimeoutException($"Speech input isn't ready ({input.State}): {input.Info}");

            var app = FindAnyObjectByType<AIEdgeApp>();
            try
            {
                // No panel that takes speech: a hint, no recording.
                foreach (var close in new Action[] { () => { if (app.Chat.IsOpen) app.Chat.Close(); }, () => { if (app.AskImage.IsOpen) app.AskImage.Close(); }, () => { if (app.Garden.IsOpen) app.Garden.Close(); } })
                    close();
                await Awaitable.NextFrameAsync();
                gesture.Simulate(true, 0.05f);
                await Awaitable.WaitForSecondsAsync(0.8f);
                var hintShown = feedback.IsShown;
                var recordedWithoutPanel = input.IsRecording;
                gesture.Simulate(true, 0.6f);
                await Awaitable.WaitForSecondsAsync(1.2f);
                report.Append($"no panel: hint shown {hintShown}, recording {recordedWithoutPanel}; ");
                if (recordedWithoutPanel || !hintShown)
                    throw new Exception("Without a panel that takes speech the gesture should only show a hint. " + report);

                // In Chat: start, listen, stop.
                app.OpenChat();
                await Awaitable.NextFrameAsync();
                var starts = feedback.StartCount;
                var stops = feedback.StopCount;
                var startedAt = Time.realtimeSinceStartup;
                gesture.Simulate(true, 0.05f);
                deadline = Time.realtimeSinceStartup + 3f;
                while (!input.IsRecording && Time.realtimeSinceStartup < deadline)
                    await Awaitable.NextFrameAsync();
                var startDelay = Time.realtimeSinceStartup - startedAt;
                if (!input.IsRecording)
                    throw new Exception("Holding the fist at the mouth didn't start listening. " + report);
                await Awaitable.NextFrameAsync();
                var signalledStart = feedback.StartCount == starts + 1 && feedback.IsShown;

                // A loosened fist at the mouth keeps listening.
                gesture.Simulate(false, 0.08f);
                await Awaitable.WaitForSecondsAsync(3f);
                var stillListening = input.IsRecording;

                gesture.Simulate(true, 0.6f);
                deadline = Time.realtimeSinceStartup + 3f;
                while (input.IsRecording && Time.realtimeSinceStartup < deadline)
                    await Awaitable.NextFrameAsync();
                await Awaitable.NextFrameAsync();
                var signalledStop = feedback.StopCount == stops + 1;
                deadline = Time.realtimeSinceStartup + 20f;
                while (input.State == SpeechInput.Status.Transcribing && Time.realtimeSinceStartup < deadline)
                    await Awaitable.NextFrameAsync();

                report.Append($"chat: listening after {startDelay:0.00}s, start signalled {signalledStart}, kept listening with a loose fist {stillListening}, " +
                    $"stopped {!input.IsRecording}, stop signalled {signalledStop}, then {input.State}");
                if (!signalledStart || !stillListening || input.IsRecording || !signalledStop)
                    throw new Exception("The gesture didn't run start → listen → stop with its signals. " + report);
                return report.ToString();
            }
            finally
            {
                gesture.Simulate(null);
                if (app.Chat.IsOpen)
                    app.Chat.Close();
            }
        }

        // Personal instructions written in English, with What's That? set to Italiano: every sentence of the reply is in
        // Italian and the whole reply is read by the Italian voice.
        async Awaitable<string> TestInstructionsItalian()
        {
            var savedText = UserInstructions.Text;
            var savedEnabled = UserInstructions.Enabled;
            var app = FindAnyObjectByType<AIEdgeApp>();
            var panel = app.WhatsThat;
            var speech = SpeechEngine.Instance;
            var report = new StringBuilder();
            try
            {
                UserInstructions.Set("I'm on a strict diet. After describing any object, always add one short joke " +
                                     "teasing me about my diet.", true);
                if (app.Chat.IsOpen)
                    app.Chat.Close();
                if (!panel.IsOpen)
                    app.OpenWhatsThat();
                var deadline = Time.realtimeSinceStartup + 120f;
                while (!panel.IsReady && Time.realtimeSinceStartup < deadline)
                    await Awaitable.NextFrameAsync();
                if (!panel.IsReady)
                    throw new TimeoutException("What's That? never became ready.");

                panel.Language = SpeechLanguage.Italian;
                var english = 0;
                var languages = new HashSet<string>();
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    WhatsThatResult result = null;
                    void OnFinished(WhatsThatResult r) => result = r;
                    panel.AnswerFinished += OnFinished;
                    var head = Camera.main.transform;
                    var ray = new Ray(head.position, Quaternion.AngleAxis(15f, head.right) * head.forward);
                    if (!panel.AskAlong(ray))
                    {
                        panel.AnswerFinished -= OnFinished;
                        throw new Exception("Depth found nothing straight ahead.");
                    }

                    // Sample the voice of every utterance while the reply streams in.
                    deadline = Time.realtimeSinceStartup + 90f;
                    while ((result == null || speech != null && speech.IsSpeaking) && Time.realtimeSinceStartup < deadline)
                    {
                        if (speech?.LastLanguage != null && speech.IsSpeaking)
                            languages.Add(speech.LastLanguage);
                        await Awaitable.NextFrameAsync();
                    }
                    panel.AnswerFinished -= OnFinished;
                    if (result == null)
                        throw new TimeoutException("No answer.");
                    if (result.Error != null)
                        throw new Exception(result.Error);

                    foreach (var sentence in Regex.Split(result.Reply, @"(?<=[.!?])\s+"))
                        if (SpeechLanguage.Detect(sentence) == SpeechLanguage.English)
                        {
                            english++;
                            report.Append($"ENGLISH SENTENCE: \"{sentence}\" ");
                        }
                    report.Append($"[{attempt}] {result.Reply.Replace("\n", " ")} | ");
                }

                report.Append($"voices used: {string.Join(",", languages)}");
                if (english > 0)
                    throw new Exception($"{english} English sentence(s) in Italian replies. " + report);
                if (languages.Contains(SpeechLanguage.English))
                    throw new Exception("The English voice read part of an Italian reply. " + report);
                return report.ToString();
            }
            finally
            {
                panel.Language = SpeechLanguage.English;
                UserInstructions.Set(savedText, savedEnabled);
            }
        }

        // Personal instructions: with the "Strict diet" preset on, a chat about a cake gets teased about the diet, and
        // What's That? still answers in its format. The user's own instructions are restored afterwards.
        async Awaitable<string> TestInstructions()
        {
            var savedText = UserInstructions.Text;
            var savedEnabled = UserInstructions.Enabled;
            var diet = Array.Find(UserInstructions.Presets, preset => preset.Name == "Strict diet").Text;
            var report = new StringBuilder();
            try
            {
                UserInstructions.Set(diet, true);
                var app = FindAnyObjectByType<AIEdgeApp>();
                if (app.WhatsThat.IsOpen)
                    app.WhatsThat.Close();
                if (!app.Chat.IsOpen)
                    app.OpenChat();
                app.Chat.NewChat();
                var cake = await ChatTurnAsync(app.Chat, "I'm looking at a big slice of chocolate cake right now. What is it?");
                var teased = cake.text.IndexOf("diet", StringComparison.OrdinalIgnoreCase) >= 0;
                report.Append($"cake reply ({(teased ? "mentions the diet" : "NO diet mention")}): {cake.text.Replace("\n", " ")} | ");

                app.Chat.NewChat();
                var salad = await ChatTurnAsync(app.Chat, "I'm looking at a bowl of green salad. What is it?");
                report.Append($"salad reply: {salad.text.Replace("\n", " ")} | ");

                UserInstructions.Set(diet, false);
                app.Chat.NewChat();
                var plain = await ChatTurnAsync(app.Chat, "I'm looking at a big slice of chocolate cake right now. What is it?");
                var plainTeased = plain.text.IndexOf("diet", StringComparison.OrdinalIgnoreCase) >= 0;
                report.Append($"instructions off ({(plainTeased ? "still mentions the diet" : "no diet mention")}): {plain.text.Replace("\n", " ")}");
                app.Chat.Close();
                if (!teased)
                    throw new Exception("The diet instructions were ignored. " + report);
                return report.ToString();
            }
            finally
            {
                UserInstructions.Set(savedText, savedEnabled);
            }
        }

        // The crop the model saw, tinted green where the segmentation mask is, to check the cut-out by eye.
        static void SaveMaskOverlay(string cropPath, SegmentationResult segmentation, string path)
        {
            var crop = new Texture2D(2, 2);
            if (!crop.LoadImage(File.ReadAllBytes(cropPath)))
                return;
            var pixels = crop.GetPixels32();
            for (var y = 0; y < crop.height; y++)
            for (var x = 0; x < crop.width; x++)
            {
                var mx = Mathf.Min(segmentation.Size - 1, x * segmentation.Size / crop.width);
                var my = Mathf.Min(segmentation.Size - 1, y * segmentation.Size / crop.height);
                if (segmentation.Mask[my * segmentation.Size + mx] <= 127)
                    continue;
                var i = y * crop.width + x;
                pixels[i] = Color32.Lerp(pixels[i], new Color32(60, 255, 120, 255), 0.5f);
            }

            crop.SetPixels32(pixels);
            File.WriteAllBytes(path, crop.EncodeToPNG());
            Destroy(crop);
        }

        // Exit criterion: the garden lands on a real surface at once.
        async Awaitable<string> TestDepthGarden()
        {
            var app = FindAnyObjectByType<AIEdgeApp>();
            if (app.AskImage.IsOpen)
                app.AskImage.Close();
            if (!app.Garden.IsOpen)
                app.OpenGarden();
            await app.Garden.PlaceGardenAsync();
            if (!app.Garden.PlacedOnSurface)
                throw new Exception(app.Garden.PlacementNote);
            return app.Garden.PlacementNote;
        }

        static async Awaitable<ChatMessage> ChatTurnAsync(ChatPanel chat, string text)
        {
            var deadline = Time.realtimeSinceStartup + 90f;
            while (!chat.IsReady && Time.realtimeSinceStartup < deadline)
                await Awaitable.NextFrameAsync();
            if (!chat.IsReady)
                throw new TimeoutException("Chat never became ready.");

            ChatMessage reply = null;
            void OnFinished(ChatMessage message) => reply = message;
            chat.ResponseFinished += OnFinished;
            try
            {
                chat.Submit(text);
                while (reply == null && Time.realtimeSinceStartup < deadline)
                    await Awaitable.NextFrameAsync();
            }
            finally
            {
                chat.ResponseFinished -= OnFinished;
            }

            if (reply == null || reply.stats == null)
                throw new Exception($"No answer to \"{text}\".");
            return reply;
        }

        /// <summary>Collects one streamed turn from the engine.</summary>
        class Turn
        {
            readonly LlmEngine m_Engine;
            public readonly StringBuilder Text = new StringBuilder();
            public bool Done;
            LlmStats m_Stats;
            string m_Error;

            public Turn(LlmEngine engine)
            {
                m_Engine = engine;
                m_Engine.TokenReceived += OnToken;
                m_Engine.ResponseDone += OnDone;
                m_Engine.Failed += OnFailed;
            }

            void OnToken(string text) => Text.Append(text);

            void OnDone(LlmStats stats)
            {
                m_Stats = stats;
                Finish();
            }

            void OnFailed(string error)
            {
                m_Error = error;
                Finish();
            }

            void Finish()
            {
                Done = true;
                m_Engine.TokenReceived -= OnToken;
                m_Engine.ResponseDone -= OnDone;
                m_Engine.Failed -= OnFailed;
            }

            public async Awaitable<LlmStats> WaitAsync(float timeoutSec)
            {
                var deadline = Time.realtimeSinceStartup + timeoutSec;
                while (!Done && Time.realtimeSinceStartup < deadline)
                    await Awaitable.NextFrameAsync();

                if (!Done)
                    throw new TimeoutException($"No response within {timeoutSec}s.");
                if (m_Error != null)
                    throw new LlmException(m_Error);
                return m_Stats;
            }
        }

        static void Report(string test, bool pass, string detail)
        {
            var line = $"SELFTEST|{{\"test\":\"{test}\",\"pass\":{(pass ? "true" : "false")},\"detail\":\"{detail.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ")}\"}}";
            Debug.Log(line);

            // Also kept in a file: the headset's log buffer can rotate before long tests finish.
            var resultsPath = Path.Combine(Application.persistentDataPath, "selftest_results.txt");
            if (test == "start")
                File.Delete(resultsPath);
            File.AppendAllText(resultsPath, line + "\n");
        }
    }
}
