using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using AIEdge.Spatial;
using AIEdge.UI;
using TMPro;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace AIEdge.WhatsThat
{
    /// <summary>A person tracked in the room.</summary>
    public class TrackedPerson
    {
        /// <summary>Where the detector last saw them, in camera viewport coordinates.</summary>
        public Rect Viewport;

        /// <summary>Detector confidence of the last sighting.</summary>
        public float Score;

        /// <summary>Distance from the user, measured by depth, in meters (NaN when depth found nothing).</summary>
        public float Distance = float.NaN;

        /// <summary>Center of the person in the world (valid when <see cref="Distance"/> is).</summary>
        public Vector3 Position;

        internal float LastSeen;
        internal RectTransform Frame;
        internal TextMeshProUGUI Label;
        internal bool Placed;
    }

    /// <summary>
    /// Finds people in the passthrough camera image in the background while What's That? is open, and draws a frame
    /// around each one in the room with their distance. Runs EfficientDet-Lite0 on the CPU through
    /// <c>com.xraiedge.litertlm.DetectorBridge</c> at a few frames a second: each frame is shrunk to 320×320 on the GPU
    /// and read back without blocking. Only boxes are found, nothing that identifies anyone, and no image leaves the
    /// headset.
    /// </summary>
    public class PersonDetector : MonoBehaviour
    {
        const string k_BridgeClass = "com.xraiedge.litertlm.DetectorBridge";
        const string k_EnabledKey = "AIEdge.PersonDetection";
        const int k_Size = 320;
        // Frame units are millimeters at 1 m and grow with distance, so the border and label keep their apparent size.
        const float k_FrameUnitsPerMeter = 1000f;
        const float k_Border = 6f;

        static readonly Color k_FrameColor = new Color(1f, 0.78f, 0.25f, 0.9f);

        [SerializeField, Tooltip("Seconds between detections.")]
        float m_Interval = 0.2f;

        [SerializeField, Tooltip("Minimum detector confidence for a person.")]
        float m_MinScore = 0.5f;

        [SerializeField, Tooltip("Seconds a person stays shown after the last sighting.")]
        float m_KeepSeconds = 0.8f;

        /// <summary>Whether people are detected. Remembered between sessions.</summary>
        public static bool Enabled
        {
            get => PlayerPrefs.GetInt(k_EnabledKey, 1) == 1;
            set
            {
                PlayerPrefs.SetInt(k_EnabledKey, value ? 1 : 0);
                PlayerPrefs.Save();
            }
        }

        /// <summary>Raised after each processed frame.</summary>
        public event Action Updated;

        /// <summary>The people in view now.</summary>
        public IReadOnlyList<TrackedPerson> People => m_People;

        /// <summary>Frames processed since start.</summary>
        public int FrameCount { get; private set; }

        /// <summary>Inference time of the last frame, in milliseconds.</summary>
        public long LastInferMs { get; private set; }

        /// <summary>Time from frame capture to results of the last frame, in seconds.</summary>
        public float LastLatency { get; private set; }

        /// <summary>The last error, or null.</summary>
        public string Error { get; private set; }

        /// <summary>The detector's last raw result (JSON), for diagnostics.</summary>
        public string LastResultJson { get; private set; }

        /// <summary>When set, the next frame sent to the detector is also saved there as a PNG (for tests).</summary>
        public string DumpNextFrame { get; set; }

        /// <summary>Hides the frames (they stay tracked), for example while the panel is hidden.</summary>
        public bool ShowFrames { get; set; } = true;

        readonly ConcurrentQueue<Action> m_MainThread = new ConcurrentQueue<Action>();
        readonly List<TrackedPerson> m_People = new List<TrackedPerson>();
        readonly Dictionary<string, (Pose pose, float time)> m_Requests = new Dictionary<string, (Pose, float)>();
        readonly Dictionary<string, AwaitableCompletionSource<List<PersonBox>>> m_TestRequests =
            new Dictionary<string, AwaitableCompletionSource<List<PersonBox>>>();
        AndroidJavaObject m_Bridge;
        PassthroughFrameCapture m_Camera;
        DepthService m_Depth;
        RenderTexture m_Small;
        Transform m_FrameRoot;
        float m_Next;
        bool m_ReadbackPending;
        bool m_Unavailable;
        int m_NextId;

        /// <summary>True on devices that can run the detector.</summary>
        public static bool IsSupported => Application.platform == RuntimePlatform.Android;

        void Awake()
        {
            m_FrameRoot = new GameObject("PeopleFrames").transform;
        }

        void OnDisable()
        {
            foreach (var person in m_People)
                Destroy(person.Frame.gameObject);
            m_People.Clear();
            m_Requests.Clear();
            Updated?.Invoke();
        }

        void OnDestroy()
        {
            m_Bridge?.Call("close");
            m_Bridge?.Dispose();
            m_Bridge = null;
            if (m_Small != null)
                m_Small.Release();
            if (m_FrameRoot != null)
                Destroy(m_FrameRoot.gameObject);
        }

        void Update()
        {
            while (m_MainThread.TryDequeue(out var action))
                action();

            // Forget people not seen for a while.
            for (var i = m_People.Count - 1; i >= 0; i--)
            {
                if (Time.unscaledTime - m_People[i].LastSeen <= m_KeepSeconds)
                    continue;
                Destroy(m_People[i].Frame.gameObject);
                m_People.RemoveAt(i);
                Updated?.Invoke();
            }

            if (m_FrameRoot.gameObject.activeSelf != ShowFrames)
                m_FrameRoot.gameObject.SetActive(ShowFrames);
            if (!Enabled || !IsSupported || m_Unavailable || Time.unscaledTime < m_Next || m_ReadbackPending)
                return;
            if (m_Camera == null)
                m_Camera = FindAnyObjectByType<PassthroughFrameCapture>();
            if (m_Camera == null || !m_Camera.IsReady || !EnsureBridge() || m_Bridge.Call<bool>("isBusy"))
                return;

            var texture = m_Camera.CameraAccess.GetTexture();
            if (texture == null)
                return;
            m_Next = Time.unscaledTime + m_Interval;
            // The pose is the camera's at this frame, so boxes are placed where the person was when it was taken.
            var id = (m_NextId++).ToString();
            m_Requests[id] = (m_Camera.GetWorldCameraPose(), Time.unscaledTime);
            Submit(texture, id);
        }

        // Shrinks the image on the GPU (stretching 4:3 to square: boxes come back normalized, so that's undone for
        // free), reads it back without stalling the frame and hands it to the detector.
        void Submit(Texture texture, string id)
        {
            if (m_Small == null)
            {
                m_Small = new RenderTexture(k_Size, k_Size, 0, RenderTextureFormat.ARGB32);
                m_Small.Create();
            }

            // Halve until close to the model size, then the last step: one big bilinear step would skip most pixels
            // and alias small, distant people away.
            var source = texture;
            RenderTexture temporary = null;
            var width = texture.width;
            var height = texture.height;
            while (width > k_Size * 2 || height > k_Size * 2)
            {
                width = Mathf.Max(k_Size, width / 2);
                height = Mathf.Max(k_Size, height / 2);
                var half = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(source, half);
                if (temporary != null)
                    RenderTexture.ReleaseTemporary(temporary);
                source = temporary = half;
            }

            Graphics.Blit(source, m_Small);
            if (temporary != null)
                RenderTexture.ReleaseTemporary(temporary);
            m_ReadbackPending = true;
            AsyncGPUReadback.Request(m_Small, 0, TextureFormat.RGBA32, request =>
            {
                m_ReadbackPending = false;
                if (!request.hasError && DumpNextFrame != null)
                {
                    var dump = new Texture2D(k_Size, k_Size, TextureFormat.RGBA32, false);
                    dump.LoadRawTextureData(request.GetData<byte>());
                    dump.Apply();
                    System.IO.File.WriteAllBytes(DumpNextFrame, dump.EncodeToPNG());
                    Destroy(dump);
                    DumpNextFrame = null;
                }

                var sent = !request.hasError && m_Bridge != null &&
                    m_Bridge.Call<bool>("detect", id, request.GetData<byte>().Reinterpret<sbyte>().ToArray(), true, m_MinScore);
                if (sent)
                    return;
                m_Requests.Remove(id);
                if (m_TestRequests.Remove(id, out var test))
                    test.TrySetResult(new List<PersonBox>());
            });
        }

        /// <summary>
        /// Detects people in a texture through the same GPU path as the live camera (for tests: it checks the image
        /// orientation). The boxes are in viewport coordinates.
        /// </summary>
        public async Awaitable<List<PersonBox>> DetectTextureAsync(Texture texture)
        {
            if (!IsSupported || !EnsureBridge())
            {
                Error ??= "Person detection runs on Android only.";
                return new List<PersonBox>();
            }

            while (m_ReadbackPending || m_Bridge.Call<bool>("isBusy"))
                await Awaitable.NextFrameAsync();
            var source = new AwaitableCompletionSource<List<PersonBox>>();
            var id = "texture" + m_NextId++;
            m_TestRequests[id] = source;
            Submit(texture, id);
            return await source.Awaitable;
        }

        /// <summary>Detects people in an image file (for tests). The boxes are in viewport coordinates.</summary>
        public Awaitable<List<PersonBox>> DetectFileAsync(string imagePath)
        {
            var source = new AwaitableCompletionSource<List<PersonBox>>();
            if (!IsSupported || !EnsureBridge())
            {
                Error ??= "Person detection runs on Android only.";
                source.SetResult(new List<PersonBox>());
                return source.Awaitable;
            }

            var id = "file" + m_NextId++;
            m_TestRequests[id] = source;
            m_Bridge.Call("detectFile", id, imagePath, m_MinScore);
            return source.Awaitable;
        }

        bool EnsureBridge()
        {
            if (m_Bridge != null)
                return true;
            if (m_Unavailable)
                return false;
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                m_Bridge = new AndroidJavaObject(k_BridgeClass, activity, new ListenerProxy(this));
                return true;
            }
            catch (Exception e)
            {
                Error = e.Message;
                m_Unavailable = true;
                Debug.LogWarning($"[AIEdge] Person detection unavailable: {e.Message}");
                return false;
            }
        }

        [Serializable]
        class Box
        {
            public float x, y, w, h, score;
        }

        [Serializable]
        class Result
        {
            public Box[] people;
            public long inferMs;
        }

        static List<PersonBox> Parse(string json, out long inferMs)
        {
            var result = JsonUtility.FromJson<Result>(json);
            inferMs = result?.inferMs ?? 0;
            var boxes = new List<PersonBox>();
            foreach (var box in result?.people ?? Array.Empty<Box>())
                boxes.Add(new PersonBox { Viewport = PersonMath.ToViewport(box.x, box.y, box.w, box.h), Score = box.score });
            return boxes;
        }

        void OnDetections(string id, string json)
        {
            LastResultJson = json;
            if (m_TestRequests.Remove(id, out var test))
            {
                test.TrySetResult(Parse(json, out _));
                return;
            }

            if (!m_Requests.Remove(id, out var request) || !isActiveAndEnabled)
                return;
            var boxes = Parse(json, out var inferMs);
            FrameCount++;
            LastInferMs = inferMs;
            LastLatency = Time.unscaledTime - request.time;
            Error = null;
            Apply(boxes, request.pose);
            Updated?.Invoke();
        }

        void OnError(string id, string message)
        {
            m_Requests.Remove(id);
            if (m_TestRequests.Remove(id, out var file))
                file.TrySetResult(new List<PersonBox>());
            Error = message;
            Debug.LogWarning($"[AIEdge] Person detection: {message}");
        }

        // Matches the new boxes to the people already tracked, then places each one in the room with depth.
        void Apply(List<PersonBox> boxes, Pose pose)
        {
            var tracked = new List<Rect>();
            foreach (var person in m_People)
                tracked.Add(person.Viewport);
            var found = new List<Rect>();
            foreach (var box in boxes)
                found.Add(box.Viewport);
            var matches = PersonMath.Match(tracked, found);

            for (var i = 0; i < boxes.Count; i++)
            {
                var person = matches[i] >= 0 ? m_People[matches[i]] : null;
                if (person == null)
                {
                    person = new TrackedPerson();
                    CreateFrame(person);
                    m_People.Add(person);
                }

                person.Viewport = boxes[i].Viewport;
                person.Score = boxes[i].Score;
                person.LastSeen = Time.unscaledTime;
                Place(person, pose);
            }
        }

        void Place(TrackedPerson person, Pose pose)
        {
            var access = m_Camera.CameraAccess;
            if (m_Depth == null)
                m_Depth = DepthService.Instance;

            // The nearest depth hit among a few points on the person.
            var distance = float.NaN;
            var hitPoint = Vector3.zero;
            if (m_Depth != null && m_Depth.IsReady)
            {
                foreach (var probe in PersonMath.DepthProbes(person.Viewport))
                {
                    var ray = access.ViewportPointToRay(probe, pose);
                    if (!m_Depth.Raycast(ray, out var hit))
                        continue;
                    var d = Vector3.Distance(pose.position, hit.point);
                    if (float.IsNaN(distance) || d < distance)
                    {
                        distance = d;
                        hitPoint = hit.point;
                    }
                }
            }

            person.Distance = distance;
            if (float.IsNaN(distance))
            {
                // No depth: keep the last placement (or hide until there is one).
                person.Frame.gameObject.SetActive(person.Placed);
                return;
            }

            // The box's corners, cast from the camera onto the plane through the person facing the camera.
            var forward = pose.rotation * Vector3.forward;
            var depth = Vector3.Dot(hitPoint - pose.position, forward);
            Vector3 Corner(float x, float y)
            {
                var ray = access.ViewportPointToRay(new Vector2(x, y), pose);
                return ray.origin + ray.direction * (depth / Mathf.Max(0.1f, Vector3.Dot(ray.direction, forward)));
            }

            var v = person.Viewport;
            var bottomLeft = Corner(v.xMin, v.yMin);
            var bottomRight = Corner(v.xMax, v.yMin);
            var topLeft = Corner(v.xMin, v.yMax);
            var center = Corner(v.center.x, v.center.y);
            var size = new Vector2(Vector3.Distance(bottomLeft, bottomRight), Vector3.Distance(bottomLeft, topLeft));
            var rotation = Quaternion.LookRotation(forward, pose.rotation * Vector3.up);

            // Smooth between frames so the frame glides with the person instead of jumping.
            var frame = person.Frame;
            var blend = person.Placed ? 0.5f : 1f;
            var unitsPerMeter = k_FrameUnitsPerMeter / Mathf.Max(0.5f, depth);
            var targetSize = size * unitsPerMeter;
            frame.localScale = Vector3.one / unitsPerMeter;
            frame.position = Vector3.Lerp(frame.position, center, blend);
            frame.rotation = person.Placed ? Quaternion.Slerp(frame.rotation, rotation, blend) : rotation;
            frame.sizeDelta = person.Placed ? Vector2.Lerp(frame.sizeDelta, targetSize, blend) : targetSize;
            person.Position = center;
            person.Label.text = PersonMath.Label(distance, AppLanguage.Current);
            person.Placed = true;
            frame.gameObject.SetActive(true);
        }

        // A world-space frame: four bars along the edges and a label above.
        void CreateFrame(TrackedPerson person)
        {
            var go = new GameObject("PersonFrame", typeof(RectTransform), typeof(Canvas));
            go.transform.SetParent(m_FrameRoot, false);
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 50;
            var frame = (RectTransform)go.transform;
            frame.localScale = Vector3.one / k_FrameUnitsPerMeter;
            frame.sizeDelta = new Vector2(500f, 1500f);

            Bar(frame, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, -k_Border)); // top
            Bar(frame, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, k_Border)); // bottom
            Bar(frame, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(k_Border, 0f)); // left
            Bar(frame, new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(-k_Border, 0f)); // right

            var label = UiFactory.CreateText(frame, "Label", "", 40, TextAlignmentOptions.Bottom);
            label.color = k_FrameColor;
            label.fontStyle = FontStyles.Bold;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            var rect = label.rectTransform;
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.sizeDelta = new Vector2(600f, 60f);
            rect.anchoredPosition = new Vector2(0f, 10f);

            person.Frame = frame;
            person.Label = label;
            go.SetActive(false);
        }

        static void Bar(RectTransform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 thickness)
        {
            var bar = UiFactory.CreateImage(parent, "Edge", k_FrameColor);
            bar.raycastTarget = false;
            var rect = bar.rectTransform;
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = new Vector2(anchorMin.x == anchorMax.x ? anchorMin.x : 0.5f, anchorMin.y == anchorMax.y ? anchorMin.y : 0.5f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            rect.sizeDelta = new Vector2(Mathf.Abs(thickness.x), Mathf.Abs(thickness.y));
        }

        class ListenerProxy : AndroidJavaProxy
        {
            readonly PersonDetector m_Owner;

            public ListenerProxy(PersonDetector owner) : base(k_BridgeClass + "$Listener") => m_Owner = owner;

            public void onDetections(string requestId, string resultJson) =>
                m_Owner.m_MainThread.Enqueue(() => m_Owner.OnDetections(requestId, resultJson));

            public void onError(string requestId, string message) =>
                m_Owner.m_MainThread.Enqueue(() => m_Owner.OnError(requestId, message));
        }
    }
}
