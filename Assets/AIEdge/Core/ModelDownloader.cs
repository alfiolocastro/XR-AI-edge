using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;

namespace AIEdge
{
    /// <summary>
    /// Progress of a model download.
    /// </summary>
    public readonly struct DownloadProgress
    {
        /// <summary>Bytes on disk so far, including bytes from earlier sessions.</summary>
        public readonly long Bytes;

        /// <summary>Total size in bytes, or 0 if unknown.</summary>
        public readonly long TotalBytes;

        /// <summary>Recent transfer rate.</summary>
        public readonly double BytesPerSecond;

        public DownloadProgress(long bytes, long totalBytes, double bytesPerSecond)
        {
            Bytes = bytes;
            TotalBytes = totalBytes;
            BytesPerSecond = bytesPerSecond;
        }

        /// <summary>Completed fraction 0..1, or 0 if the size is unknown.</summary>
        public float Fraction => TotalBytes > 0 ? Mathf.Clamp01((float)((double)Bytes / TotalBytes)) : 0f;
    }

    /// <summary>
    /// Thrown when Hugging Face refuses a download: missing token, or license not accepted.
    /// </summary>
    public class ModelAccessException : Exception
    {
        /// <summary>The model that couldn't be downloaded.</summary>
        public ModelInfo Model { get; }

        public ModelAccessException(ModelInfo model, string message) : base(message)
        {
            Model = model;
        }
    }

    /// <summary>
    /// Downloads models from Hugging Face into <see cref="ModelStore"/>, resuming interrupted downloads
    /// with HTTP Range requests. Files are written to a <c>.part</c> file and moved into place only after
    /// the size matches the catalog.
    /// </summary>
    public class ModelDownloader
    {
        const long k_FreeSpaceMarginBytes = 256L * 1024 * 1024;
        const double k_ProgressInterval = 0.25;

        readonly HashSet<string> m_Active = new HashSet<string>();

        /// <summary>HTTP status of the most recent request (206 when a download resumed).</summary>
        public long LastResponseCode { get; private set; }

        /// <summary>True while the model is downloading.</summary>
        public bool IsDownloading(ModelInfo model) => m_Active.Contains(model.id);

        /// <summary>
        /// Downloads a model, or finishes a partial download. Returns immediately if already downloaded.
        /// Cancelling keeps the partial file so the next call resumes.
        /// </summary>
        /// <exception cref="ModelAccessException">Token missing or license not accepted.</exception>
        /// <exception cref="OperationCanceledException">Cancelled through <paramref name="cancellationToken"/>.</exception>
        /// <exception cref="IOException">Network, storage, or size-check failure.</exception>
        public async Awaitable DownloadAsync(ModelInfo model, IProgress<DownloadProgress> progress = null,
            CancellationToken cancellationToken = default)
        {
            if (!m_Active.Add(model.id))
                throw new InvalidOperationException($"{model.displayName} is already downloading.");

            try
            {
                await DownloadInternalAsync(model, progress, cancellationToken, allowRestart: true);
            }
            finally
            {
                m_Active.Remove(model.id);
            }
        }

        async Awaitable DownloadInternalAsync(ModelInfo model, IProgress<DownloadProgress> progress,
            CancellationToken cancellationToken, bool allowRestart)
        {
            if (ModelStore.GetState(model) == ModelState.Downloaded)
                return;

            Directory.CreateDirectory(ModelStore.ModelsDirectory);
            var partialPath = ModelStore.GetPartialPath(model);
            var existing = ModelStore.GetPartialBytes(model);

            if (model.sizeBytes > 0 && existing > model.sizeBytes)
            {
                File.Delete(partialPath);
                existing = 0;
            }

            if (model.sizeBytes > 0 && existing == model.sizeBytes)
            {
                CompleteDownload(model);
                return;
            }

            var freeBytes = ModelStore.GetFreeBytes();
            var needed = model.sizeBytes > 0 ? model.sizeBytes - existing + k_FreeSpaceMarginBytes : k_FreeSpaceMarginBytes;
            if (freeBytes >= 0 && freeBytes < needed)
                throw new IOException($"Not enough storage for {model.displayName}: {FormatBytes(needed)} needed, {FormatBytes(freeBytes)} free.");

            var token = ModelStore.HuggingFaceToken;
            if (model.gated && token == null)
                throw new ModelAccessException(model,
                    $"{model.displayName} needs a Hugging Face token. Accept the license at {model.PageUrl}, then add your token.");

            using var request = new UnityWebRequest(model.DownloadUrl, UnityWebRequest.kHttpVerbGET);
            request.downloadHandler = new DownloadHandlerFile(partialPath, existing > 0) { removeFileOnAbort = false };
            if (existing > 0)
                request.SetRequestHeader("Range", $"bytes={existing}-");
            if (token != null)
                request.SetRequestHeader("Authorization", "Bearer " + token);

            var operation = request.SendWebRequest();
            var lastReportTime = Time.realtimeSinceStartupAsDouble;
            var lastReportBytes = existing;
            var bytesPerSecond = 0.0;
            progress?.Report(new DownloadProgress(existing, model.sizeBytes, 0));

            LastResponseCode = 0;
            while (!operation.isDone)
            {
                if (request.responseCode > 0)
                    LastResponseCode = request.responseCode;

                if (cancellationToken.IsCancellationRequested)
                {
                    request.Abort();
                    throw new OperationCanceledException(cancellationToken);
                }

                var now = Time.realtimeSinceStartupAsDouble;
                if (now - lastReportTime >= k_ProgressInterval)
                {
                    var bytes = existing + (long)request.downloadedBytes;
                    var instant = (bytes - lastReportBytes) / (now - lastReportTime);
                    bytesPerSecond = bytesPerSecond <= 0 ? instant : bytesPerSecond * 0.8 + instant * 0.2;
                    lastReportTime = now;
                    lastReportBytes = bytes;
                    progress?.Report(new DownloadProgress(bytes, model.sizeBytes, bytesPerSecond));
                }

                await Awaitable.NextFrameAsync();
            }

            LastResponseCode = request.responseCode;
            if (request.result != UnityWebRequest.Result.Success)
            {
                // The error body went into the partial file: cut it back to the resumable bytes.
                TruncatePartial(partialPath, existing);

                if (request.responseCode == 401 || request.responseCode == 403)
                {
                    throw new ModelAccessException(model, token == null
                        ? $"{model.displayName} needs a Hugging Face token (HTTP {request.responseCode})."
                        : $"Access to {model.displayName} was refused (HTTP {request.responseCode}). Accept the license at {model.PageUrl} and check the token.");
                }

                if (request.responseCode == 416 && model.sizeBytes > 0 && existing == model.sizeBytes)
                {
                    CompleteDownload(model);
                    return;
                }

                throw new IOException($"Downloading {model.displayName} failed (HTTP {request.responseCode}): {request.error}");
            }

            if (existing > 0 && request.responseCode == 200)
            {
                // The server ignored Range and sent the whole file after our partial bytes.
                File.Delete(partialPath);
                if (!allowRestart)
                    throw new IOException($"Downloading {model.displayName} failed: the server does not support resuming.");

                await DownloadInternalAsync(model, progress, cancellationToken, allowRestart: false);
                return;
            }

            CompleteDownload(model);
            var finalBytes = new FileInfo(ModelStore.GetPath(model)).Length;
            progress?.Report(new DownloadProgress(finalBytes, model.sizeBytes, 0));
        }

        static void CompleteDownload(ModelInfo model)
        {
            var partialPath = ModelStore.GetPartialPath(model);
            var size = new FileInfo(partialPath).Length;
            if (model.sizeBytes > 0 && size != model.sizeBytes)
            {
                File.Delete(partialPath);
                throw new IOException($"{model.displayName} size mismatch: got {size} bytes, expected {model.sizeBytes}. Download it again.");
            }

            var finalPath = ModelStore.GetPath(model);
            File.Delete(finalPath);
            File.Move(partialPath, finalPath);
        }

        static void TruncatePartial(string partialPath, long length)
        {
            if (!File.Exists(partialPath))
                return;

            if (length <= 0)
            {
                File.Delete(partialPath);
                return;
            }

            using var stream = new FileStream(partialPath, FileMode.Open, FileAccess.Write);
            stream.SetLength(length);
        }

        /// <summary>Formats a byte count as "1.2 GB".</summary>
        public static string FormatBytes(long bytes)
        {
            if (bytes < 0)
                return "?";
            var culture = CultureInfo.InvariantCulture;
            if (bytes >= 1L << 30)
                return (bytes / (double)(1L << 30)).ToString("0.0", culture) + " GB";
            if (bytes >= 1L << 20)
                return (bytes / (double)(1L << 20)).ToString("0", culture) + " MB";
            return (bytes / 1024.0).ToString("0", culture) + " KB";
        }
    }
}
