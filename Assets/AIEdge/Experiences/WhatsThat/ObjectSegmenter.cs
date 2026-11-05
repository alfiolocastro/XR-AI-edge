using System;
using System.Collections.Concurrent;
using System.IO;
using UnityEngine;

namespace AIEdge.WhatsThat
{
    /// <summary>The cut-out of a pointed object in a camera crop.</summary>
    public class SegmentationResult
    {
        /// <summary>Mask probabilities 0-255, <see cref="Size"/>², row 0 at the bottom (texture order).</summary>
        public byte[] Mask;

        public int Size;

        /// <summary>Fraction of the image inside the mask.</summary>
        public float Coverage;

        /// <summary>Mask value at the pointed spot, 0-1.</summary>
        public float AtKeypoint;

        public long InferMs;
        public string Info = "";
        public string Error;

        /// <summary>True when the mask is a plausible object: it contains the pointed spot and isn't the whole image.</summary>
        public bool IsUsable => Error == null && AtKeypoint > 0.5f && Coverage > 0.002f && Coverage < 0.9f;
    }

    /// <summary>
    /// Interactive segmentation of the pointed object in a camera crop, through
    /// <c>com.xraiedge.litertlm.SegmenterBridge</c> (MediaPipe's magic touch model run with LiteRT). One request at a
    /// time; results come back on the main thread.
    /// </summary>
    public class ObjectSegmenter : MonoBehaviour
    {
        const string k_BridgeClass = "com.xraiedge.litertlm.SegmenterBridge";

        readonly ConcurrentQueue<Action> m_MainThread = new ConcurrentQueue<Action>();
        AndroidJavaObject m_Bridge;
        AwaitableCompletionSource<SegmentationResult> m_Pending;
        string m_PendingId;
        string m_MaskPath;
        int m_NextId;

        /// <summary>True on Android, where the model runs.</summary>
        public static bool IsSupported => Application.platform == RuntimePlatform.Android;

        void Update()
        {
            while (m_MainThread.TryDequeue(out var action))
                action();
        }

        void OnDestroy()
        {
            m_Bridge?.Call("close");
            m_Bridge?.Dispose();
            m_Bridge = null;
        }

        /// <summary>
        /// Cuts out the object at <paramref name="keypoint"/> (normalized, origin at the top-left) in the PNG at
        /// <paramref name="imagePath"/>. Never throws: failures come back in <see cref="SegmentationResult.Error"/>.
        /// </summary>
        public Awaitable<SegmentationResult> SegmentAsync(string imagePath, Vector2 keypoint)
        {
            var source = new AwaitableCompletionSource<SegmentationResult>();
            if (!IsSupported)
            {
                source.SetResult(new SegmentationResult { Error = "Segmentation runs on Android only." });
                return source.Awaitable;
            }

            // A newer request replaces an unanswered one.
            m_Pending?.TrySetResult(new SegmentationResult { Error = "Replaced by a newer request." });
            m_Pending = source;
            m_PendingId = (m_NextId++).ToString();
            m_MaskPath = Path.Combine(Application.temporaryCachePath, "whatsthat_mask.bin");
            try
            {
                if (m_Bridge == null)
                {
                    using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                    using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                    m_Bridge = new AndroidJavaObject(k_BridgeClass, activity, new ListenerProxy(this));
                }

                m_Bridge.Call("segment", m_PendingId, imagePath, keypoint.x, keypoint.y, m_MaskPath);
            }
            catch (Exception e)
            {
                Complete(m_PendingId, new SegmentationResult { Error = e.Message });
            }

            return source.Awaitable;
        }

        void Complete(string id, SegmentationResult result)
        {
            if (m_Pending == null || id != m_PendingId)
                return;
            var pending = m_Pending;
            m_Pending = null;
            pending.TrySetResult(result);
        }

        void OnMask(string id, string infoJson)
        {
            var result = new SegmentationResult { Info = infoJson };
            try
            {
                var info = JsonUtility.FromJson<MaskInfo>(infoJson);
                var raw = File.ReadAllBytes(m_MaskPath);
                result.Size = info.width;
                result.Coverage = info.coverage;
                result.AtKeypoint = info.atKey;
                result.InferMs = info.inferMs;
                result.Mask = FlipRows(raw, info.width, info.height);
            }
            catch (Exception e)
            {
                result.Error = e.Message;
            }

            Complete(id, result);
        }

        /// <summary>Converts rows from top-first (image) to bottom-first (texture) order.</summary>
        public static byte[] FlipRows(byte[] pixels, int width, int height)
        {
            var flipped = new byte[pixels.Length];
            for (var y = 0; y < height; y++)
                Buffer.BlockCopy(pixels, y * width, flipped, (height - 1 - y) * width, width);
            return flipped;
        }

        [Serializable]
        class MaskInfo
        {
            public int width;
            public int height;
            public float coverage;
            public float atKey;
            public long inferMs;
        }

        class ListenerProxy : AndroidJavaProxy
        {
            readonly ObjectSegmenter m_Owner;

            public ListenerProxy(ObjectSegmenter owner) : base(k_BridgeClass + "$Listener") => m_Owner = owner;

            public void onMask(string requestId, string infoJson) => m_Owner.m_MainThread.Enqueue(() => m_Owner.OnMask(requestId, infoJson));

            public void onError(string requestId, string message) =>
                m_Owner.m_MainThread.Enqueue(() => m_Owner.Complete(requestId, new SegmentationResult { Error = message }));
        }
    }
}
