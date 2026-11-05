using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;

namespace AIEdge
{
    /// <summary>
    /// A downloadable Piper neural voice for <see cref="SpeechEngine"/>, packaged for sherpa-onnx: a VITS
    /// <c>.onnx</c> model, <c>tokens.txt</c>, and the <c>espeak-ng-data</c> folder used for phonemes.
    /// </summary>
    [Serializable]
    public class VoiceInfo
    {
        /// <summary>Stable identifier, also the folder name on device.</summary>
        public string id;

        /// <summary>Name shown to the user.</summary>
        public string displayName;

        /// <summary>Language the voice speaks, as a two-letter code ("en", "it").</summary>
        public string language;

        /// <summary>Hugging Face repository holding the sherpa-onnx packaging of the voice.</summary>
        public string repoId;

        /// <summary>Pinned commit, so the files never change under us.</summary>
        public string commitHash;

        /// <summary>Total size of the files used, in bytes (for display and the storage check).</summary>
        public long sizeBytes;

        /// <summary>License of the voice's training data.</summary>
        public string license;

        /// <summary>File list at the pinned commit (Hugging Face tree API).</summary>
        public string TreeUrl => $"https://huggingface.co/api/models/{repoId}/tree/{commitHash}?recursive=true";

        /// <summary>Download URL of one file at the pinned commit.</summary>
        public string FileUrl(string path) => $"https://huggingface.co/{repoId}/resolve/{commitHash}/{path}";

        /// <summary>True for the repository files the engine needs (skips scripts, README and the Piper JSON).</summary>
        public static bool IsNeeded(string path) =>
            path.EndsWith(".onnx", StringComparison.Ordinal) || path == "tokens.txt" || path == "MODEL_CARD" ||
            path.StartsWith("espeak-ng-data/", StringComparison.Ordinal);
    }

    /// <summary>The voices this app offers, one per reply language.</summary>
    public static class VoiceCatalog
    {
        /// <summary>
        /// Piper "alba" (medium), British English, 22 kHz. Chosen for its license: the training data is CC BY 4.0
        /// (the lessac and ryan US voices are non-commercial).
        /// </summary>
        public static readonly VoiceInfo Alba = new VoiceInfo
        {
            id = "piper-en_GB-alba-medium",
            displayName = "English voice · Alba (Piper)",
            language = "en",
            repoId = "csukuangfj/vits-piper-en_GB-alba-medium",
            commitHash = "7e469a54c7a4f5bd191cae788e197266e2c9c5ac",
            sizeBytes = 81194326,
            license = "CC BY 4.0",
        };

        /// <summary>
        /// Piper "paola" (medium), Italian, 22 kHz. The training data
        /// (paolapersico1/Voice-Dataset-Italian) is CC0.
        /// </summary>
        public static readonly VoiceInfo Paola = new VoiceInfo
        {
            id = "piper-it_IT-paola-medium",
            displayName = "Voce italiana · Paola (Piper)",
            language = "it",
            repoId = "csukuangfj/vits-piper-it_IT-paola-medium",
            commitHash = "b2d11ae8ce81120ba05f39fb43ecbff2d6d634ea",
            sizeBytes = 81504041,
            license = "CC0",
        };

        /// <summary>The English voice, the fallback when a reply's language is unclear.</summary>
        public static VoiceInfo Default => Alba;

        /// <summary>All voices.</summary>
        public static System.Collections.Generic.IReadOnlyList<VoiceInfo> All { get; } = new[] { Alba, Paola };

        /// <summary>The voice for a language code, or null.</summary>
        public static VoiceInfo ForLanguage(string language)
        {
            foreach (var voice in All)
            {
                if (voice.language == language)
                    return voice;
            }

            return null;
        }
    }

    /// <summary>
    /// Voice files on device, in <c>persistentDataPath/voices/&lt;id&gt;</c>. A download fills
    /// <c>&lt;id&gt;.part</c> and renames it when every file is complete, so a voice folder is always whole.
    /// </summary>
    public static class VoiceStore
    {
        /// <summary>Folder that holds voices.</summary>
        public static string VoicesDirectory => Path.Combine(Path.GetDirectoryName(ModelStore.ModelsDirectory), "voices");

        /// <summary>Folder of a downloaded voice.</summary>
        public static string GetPath(VoiceInfo voice) => Path.Combine(VoicesDirectory, voice.id);

        /// <summary>Folder of an in-progress download.</summary>
        public static string GetPartialPath(VoiceInfo voice) => GetPath(voice) + ".part";

        /// <summary>Where the voice stands on this device.</summary>
        public static ModelState GetState(VoiceInfo voice)
        {
            if (Directory.Exists(GetPath(voice)))
                return ModelState.Downloaded;
            return Directory.Exists(GetPartialPath(voice)) ? ModelState.Partial : ModelState.NotDownloaded;
        }

        /// <summary>Bytes on disk so far, complete or partial.</summary>
        public static long GetBytes(VoiceInfo voice)
        {
            var dir = Directory.Exists(GetPath(voice)) ? GetPath(voice) : GetPartialPath(voice);
            return Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
        }

        /// <summary>Deletes the voice and any partial download.</summary>
        public static void Delete(VoiceInfo voice)
        {
            if (Directory.Exists(GetPath(voice)))
                Directory.Delete(GetPath(voice), true);
            if (Directory.Exists(GetPartialPath(voice)))
                Directory.Delete(GetPartialPath(voice), true);
        }
    }

    /// <summary>
    /// Downloads a voice's files (a few hundred, most of them small espeak-ng data files) with several requests
    /// at once. Files already complete are kept, so a cancelled download resumes file by file.
    /// </summary>
    public class VoiceDownloader
    {
        const int k_Parallel = 6;
        const long k_FreeSpaceMarginBytes = 64L * 1024 * 1024;
        const double k_ProgressInterval = 0.25;

        [Serializable]
        class TreeEntry
        {
            public string type;
            public string path;
            public long size;
        }

        [Serializable]
        class TreeList
        {
            public TreeEntry[] items;
        }

        readonly HashSet<string> m_Active = new HashSet<string>();

        /// <summary>True while the voice is downloading.</summary>
        public bool IsDownloading(VoiceInfo voice) => m_Active.Contains(voice.id);

        /// <summary>Downloads the voice, or finishes a partial download. Returns at once if already downloaded.</summary>
        /// <exception cref="OperationCanceledException">Cancelled through <paramref name="cancellationToken"/>.</exception>
        /// <exception cref="IOException">Network, storage, or size-check failure.</exception>
        public async Awaitable DownloadAsync(VoiceInfo voice, IProgress<DownloadProgress> progress = null,
            CancellationToken cancellationToken = default)
        {
            if (VoiceStore.GetState(voice) == ModelState.Downloaded)
                return;
            if (!m_Active.Add(voice.id))
                throw new InvalidOperationException($"{voice.displayName} is already downloading.");

            try
            {
                await DownloadInternalAsync(voice, progress, cancellationToken);
            }
            finally
            {
                m_Active.Remove(voice.id);
            }
        }

        async Awaitable DownloadInternalAsync(VoiceInfo voice, IProgress<DownloadProgress> progress, CancellationToken cancellationToken)
        {
            var files = await ListFilesAsync(voice, cancellationToken);
            var total = files.Sum(f => f.size);
            var root = VoiceStore.GetPartialPath(voice);
            Directory.CreateDirectory(root);

            var todo = new Queue<TreeEntry>();
            long done = 0;
            foreach (var file in files)
            {
                var path = Path.Combine(root, file.path);
                if (File.Exists(path) && new FileInfo(path).Length == file.size)
                    done += file.size;
                else
                    todo.Enqueue(file);
            }

            var freeBytes = ModelStore.GetFreeBytes();
            if (freeBytes >= 0 && freeBytes < total - done + k_FreeSpaceMarginBytes)
                throw new IOException($"Not enough storage for {voice.displayName}: {ModelDownloader.FormatBytes(total - done)} needed, {ModelDownloader.FormatBytes(freeBytes)} free.");

            // Several requests at once: per-request latency dominates for the many small files.
            var inFlight = new HashSet<UnityWebRequest>();
            Exception failure = null;
            var workers = new List<Awaitable>();
            for (var i = 0; i < k_Parallel; i++)
                workers.Add(Worker());

            var lastTime = Time.realtimeSinceStartupAsDouble;
            var lastBytes = done;
            var rate = 0.0;
            progress?.Report(new DownloadProgress(done, total, 0));
            while (workers.Any(w => !w.IsCompleted))
            {
                var now = Time.realtimeSinceStartupAsDouble;
                if (now - lastTime >= k_ProgressInterval)
                {
                    var bytes = done + inFlight.Sum(r => (long)r.downloadedBytes);
                    var instant = (bytes - lastBytes) / (now - lastTime);
                    rate = rate <= 0 ? instant : rate * 0.8 + instant * 0.2;
                    lastTime = now;
                    lastBytes = bytes;
                    progress?.Report(new DownloadProgress(bytes, total, rate));
                }

                await Awaitable.NextFrameAsync();
            }

            foreach (var worker in workers)
                await worker;

            cancellationToken.ThrowIfCancellationRequested();
            if (failure != null)
                throw failure is IOException ? failure : new IOException(failure.Message, failure);

            Directory.Move(root, VoiceStore.GetPath(voice));
            progress?.Report(new DownloadProgress(total, total, 0));

            async Awaitable Worker()
            {
                while (failure == null && !cancellationToken.IsCancellationRequested && todo.Count > 0)
                {
                    var file = todo.Dequeue();
                    var path = Path.Combine(root, file.path);
                    var temp = path + ".tmp";
                    Directory.CreateDirectory(Path.GetDirectoryName(path));

                    using var request = new UnityWebRequest(voice.FileUrl(file.path), UnityWebRequest.kHttpVerbGET);
                    request.downloadHandler = new DownloadHandlerFile(temp) { removeFileOnAbort = true };
                    inFlight.Add(request);
                    var operation = request.SendWebRequest();
                    while (!operation.isDone)
                    {
                        if (cancellationToken.IsCancellationRequested || failure != null)
                        {
                            request.Abort();
                            break;
                        }

                        await Awaitable.NextFrameAsync();
                    }

                    inFlight.Remove(request);
                    if (cancellationToken.IsCancellationRequested || failure != null)
                        return;

                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        failure = new IOException($"Downloading {file.path} failed (HTTP {request.responseCode}): {request.error}");
                        File.Delete(temp);
                        return;
                    }

                    var size = new FileInfo(temp).Length;
                    if (size != file.size)
                    {
                        failure = new IOException($"{file.path} size mismatch: got {size} bytes, expected {file.size}.");
                        File.Delete(temp);
                        return;
                    }

                    File.Delete(path);
                    File.Move(temp, path);
                    done += file.size;
                }
            }
        }

        static async Awaitable<List<TreeEntry>> ListFilesAsync(VoiceInfo voice, CancellationToken cancellationToken)
        {
            using var request = UnityWebRequest.Get(voice.TreeUrl);
            var operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    request.Abort();
                    throw new OperationCanceledException(cancellationToken);
                }

                await Awaitable.NextFrameAsync();
            }

            if (request.result != UnityWebRequest.Result.Success)
                throw new IOException($"Listing {voice.displayName} files failed (HTTP {request.responseCode}): {request.error}");

            var list = JsonUtility.FromJson<TreeList>("{\"items\":" + request.downloadHandler.text + "}");
            var files = (list?.items ?? Array.Empty<TreeEntry>())
                .Where(e => e.type == "file" && VoiceInfo.IsNeeded(e.path) && !e.path.Contains(".."))
                .ToList();
            if (!files.Any(f => f.path.EndsWith(".onnx", StringComparison.Ordinal)) || !files.Any(f => f.path == "tokens.txt"))
                throw new IOException($"{voice.displayName} file list is incomplete.");
            return files;
        }
    }
}
