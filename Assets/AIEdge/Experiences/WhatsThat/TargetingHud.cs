using System.Collections.Generic;
using AIEdge.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AIEdge.WhatsThat
{
    /// <summary>
    /// The targeting display shown while person detection is on: the room tinted red and, around the edge of the
    /// view, monospaced readouts in red. Left, a visual-scan column with live detector numbers over scrolling data.
    /// Right, a scrolling sensor log. Top, the target analysis (subjects, locked targets, nearest range and threat,
    /// detector speed) and a MATCH box. Side scales track head pitch and the nearest range. Bottom, uptime and mode.
    /// While detection arms it boots with a countdown; while volume up is held, a bar shows the hold. It follows the
    /// head with a little lag, and nothing on it can be pointed at.
    /// </summary>
    public class TargetingHud : MonoBehaviour
    {
        const float k_Distance = 1.4f;
        const int k_LeftRows = 9;
        const int k_RightRows = 16;
        const float k_ScrollInterval = 0.12f;
        const float k_RevealCharsPerSecond = 260f;
        const string k_Mono = "<mspace=0.6em>";

        static readonly Color k_Tint = new Color(0.85f, 0.04f, 0.02f, 0.34f);
        static readonly Color k_Text = new Color(1f, 0.86f, 0.8f, 1f);
        static readonly Color k_Line = new Color(1f, 0.7f, 0.62f, 0.85f);
        static readonly Color k_BoxFill = new Color(1f, 0.8f, 0.75f, 0.85f);
        static readonly Color k_BoxText = new Color(0.45f, 0.02f, 0.02f, 1f);

        /// <summary>True while any part of the display is visible.</summary>
        public bool IsShown => m_Root != null && m_Root.gameObject.activeSelf;

        /// <summary>The red tint's current opacity (0 when detection is off).</summary>
        public float TintAlpha => m_Tint != null ? m_Tint.color.a : 0f;

        /// <summary>The analysis block's text now (for tests).</summary>
        public string AnalysisText => m_Analysis != null ? m_Analysis.text : "";

        readonly Queue<string> m_LeftData = new Queue<string>();
        readonly Queue<string> m_RightData = new Queue<string>();
        readonly List<TextMeshProUGUI> m_Revealed = new List<TextMeshProUGUI>();
        readonly System.Random m_Random = new System.Random(1984);
        RectTransform m_Root;
        Image m_Tint;
        GameObject m_Boot;
        TextMeshProUGUI m_BootText;
        RectTransform m_BootBar;
        GameObject m_Live;
        TextMeshProUGUI m_LeftLive;
        TextMeshProUGUI m_Left;
        TextMeshProUGUI m_Right;
        TextMeshProUGUI m_Analysis;
        TextMeshProUGUI m_Match;
        TextMeshProUGUI m_Uptime;
        TextMeshProUGUI m_ModeText;
        RectTransform m_PitchMarker;
        RectTransform m_RangeMarker;
        GameObject m_Hold;
        TextMeshProUGUI m_HoldText;
        RectTransform m_HoldBar;
        AudioSource m_Audio;
        AudioClip m_BootClip;
        AudioClip m_OnClip;
        AudioClip m_OffClip;
        DetectionSwitch.Mode m_LastMode;
        float m_NextScroll;
        float m_NextReadout;
        float m_OnSince;
        int m_LogIndex = 172;
        int m_LastFrames;
        float m_LastFrameTime;
        float m_Rate;
        bool m_Placed;

        void Start()
        {
            Build();
            m_Audio = gameObject.AddComponent<AudioSource>();
            m_Audio.playOnAwake = false;
            m_Audio.spatialBlend = 0f;
            m_BootClip = Sweep("HudBoot", 180f, 900f, 0.7f);
            m_OnClip = Sweep("HudOn", 1200f, 1500f, 0.18f);
            m_OffClip = Sweep("HudOff", 700f, 160f, 0.45f);
        }

        void OnDestroy()
        {
            if (m_Root != null)
                Destroy(m_Root.gameObject);
        }

        void LateUpdate()
        {
            var detection = DetectionSwitch.Instance;
            var head = UnityEngine.Camera.main;
            if (m_Root == null || detection == null || head == null)
                return;

            var mode = detection.Current;
            if (mode != m_LastMode)
                OnModeChanged(m_LastMode, mode);
            m_LastMode = mode;

            var holding = detection.HoldProgress > 0.05f;
            var visible = mode != DetectionSwitch.Mode.Off || holding;
            if (m_Root.gameObject.activeSelf != visible)
            {
                m_Root.gameObject.SetActive(visible);
                m_Placed = false;
            }

            if (!visible)
                return;
            Follow(head.transform);

            // Hold bar: how far into the 2 s hold that switches detection on or off.
            m_Hold.SetActive(holding);
            if (holding)
            {
                m_HoldText.text = k_Mono + (mode == DetectionSwitch.Mode.Off ? "HOLD TO ENGAGE" : "HOLD TO DISENGAGE");
                m_HoldBar.localScale = new Vector3(Mathf.Clamp01(detection.HoldProgress), 1f, 1f);
            }

            // Boot: the tint fades in with the countdown.
            var arming = mode == DetectionSwitch.Mode.Arming;
            m_Boot.SetActive(arming);
            var tint = mode == DetectionSwitch.Mode.On ? k_Tint.a :
                arming ? k_Tint.a * (1f - detection.ArmingLeft / DetectionSwitch.ArmSeconds) : 0f;
            m_Tint.color = new Color(k_Tint.r, k_Tint.g, k_Tint.b, tint);
            if (arming)
            {
                m_BootText.text = k_Mono + HudText.Countdown(detection.ArmingLeft) +
                    ((int)(Time.unscaledTime * 4f) % 2 == 0 ? "_" : " ");
                m_BootBar.localScale = new Vector3(1f - detection.ArmingLeft / DetectionSwitch.ArmSeconds, 1f, 1f);
            }

            var on = mode == DetectionSwitch.Mode.On;
            m_Live.SetActive(on);
            if (on)
                UpdateLive(head.transform);
        }

        void OnModeChanged(DetectionSwitch.Mode from, DetectionSwitch.Mode to)
        {
            var clip = to == DetectionSwitch.Mode.Arming ? m_BootClip : to == DetectionSwitch.Mode.On ? m_OnClip : m_OffClip;
            if (m_Audio != null && clip != null)
                m_Audio.PlayOneShot(clip);
            if (to != DetectionSwitch.Mode.On)
                return;
            m_OnSince = Time.unscaledTime;
            m_NextReadout = 0f;
            foreach (var text in m_Revealed)
                text.maxVisibleCharacters = 0;
        }

        // Head-locked, with a little lag so it doesn't feel glued to the eyes.
        void Follow(Transform head)
        {
            var position = head.position + head.forward * k_Distance;
            var rotation = Quaternion.LookRotation(head.forward, head.up);
            if (!m_Placed)
            {
                m_Root.SetPositionAndRotation(position, rotation);
                m_Placed = true;
                return;
            }

            var blend = 1f - Mathf.Exp(-14f * Time.unscaledDeltaTime);
            m_Root.SetPositionAndRotation(Vector3.Lerp(m_Root.position, position, blend), Quaternion.Slerp(m_Root.rotation, rotation, blend));
        }

        void UpdateLive(Transform head)
        {
            // Everything types itself out when detection comes on.
            var reveal = (int)((Time.unscaledTime - m_OnSince) * k_RevealCharsPerSecond);
            foreach (var text in m_Revealed)
                text.maxVisibleCharacters = reveal;

            if (Time.unscaledTime >= m_NextScroll)
            {
                m_NextScroll = Time.unscaledTime + k_ScrollInterval;
                Scroll();
            }

            // Head pitch on the left scale: level in the middle, looking up raises the marker.
            var pitch = Mathf.DeltaAngle(0f, -head.eulerAngles.x);
            m_PitchMarker.anchoredPosition = new Vector2(m_PitchMarker.anchoredPosition.x, Mathf.Clamp(pitch * 6f, -250f, 250f));

            if (Time.unscaledTime < m_NextReadout)
                return;
            m_NextReadout = Time.unscaledTime + 0.1f;

            var detector = PersonDetector.Instance;
            var people = 0;
            var locked = 0;
            var nearest = float.NaN;
            var threat = ThreatLevel.Unknown;
            long inferMs = 0;
            var frames = 0;
            if (detector != null)
            {
                inferMs = detector.LastInferMs;
                frames = detector.FrameCount;
                foreach (var person in detector.People)
                {
                    people++;
                    if (person.IsTargeted)
                        locked++;
                    if (!float.IsNaN(person.Distance) && (float.IsNaN(nearest) || person.Distance < nearest))
                    {
                        nearest = person.Distance;
                        threat = person.Threat;
                    }
                }
            }

            // Detection rate, smoothed.
            if (m_LastFrameTime > 0f && Time.unscaledTime > m_LastFrameTime)
                m_Rate = Mathf.Lerp(m_Rate, (frames - m_LastFrames) / (Time.unscaledTime - m_LastFrameTime), 0.3f);
            m_LastFrames = frames;
            m_LastFrameTime = Time.unscaledTime;

            m_Analysis.text = k_Mono + HudText.Analysis(people, locked, nearest, threat, inferMs, m_Rate);
            m_Match.text = k_Mono + HudText.Match(people > 0, locked > 0, threat);
            m_LeftLive.text = k_Mono +
                $"SCAN     {frames % 100000,5}\nTRACK    {people,5}\nLOCK     {locked,5}\nPITCH    {Mathf.RoundToInt(pitch),5}";
            m_Uptime.text = k_Mono + "<< UPTIME >>\n" + HudText.Uptime(Time.unscaledTime - m_OnSince);
            m_ModeText.text = k_Mono + "<< MODE >>\n" + (locked > 0 ? "TARGET LOCK" : people > 0 ? "TRACKING" : "SCAN");
            var range = float.IsNaN(nearest) ? 0f : Mathf.Clamp(nearest, 0f, 7f);
            m_RangeMarker.anchoredPosition = new Vector2(m_RangeMarker.anchoredPosition.x, -250f + range / 7f * 500f);
        }

        void Scroll()
        {
            while (m_LeftData.Count < k_LeftRows)
                m_LeftData.Enqueue(HudText.NumberRow(m_Random));
            while (m_RightData.Count < k_RightRows)
                m_RightData.Enqueue(HudText.LogRow(m_Random, m_LogIndex++));
            m_LeftData.Dequeue();
            m_LeftData.Enqueue(HudText.NumberRow(m_Random));
            m_RightData.Dequeue();
            m_RightData.Enqueue(HudText.LogRow(m_Random, m_LogIndex++));
            m_Left.text = k_Mono + string.Join("\n", m_LeftData);
            m_Right.text = k_Mono + string.Join("\n", m_RightData);
        }

        void Build()
        {
            var go = new GameObject("TargetingHud", typeof(RectTransform), typeof(Canvas));
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            // Drawn before the panels (so they stay readable) and before the person frames.
            canvas.sortingOrder = -20;
            m_Root = (RectTransform)go.transform;
            m_Root.sizeDelta = new Vector2(2000f, 1200f);
            m_Root.localScale = Vector3.one * (k_Distance / 1000f);

            // The tint, far larger than the view.
            m_Tint = Box(m_Root, "Tint", Vector2.zero, new Vector2(9000f, 9000f), new Color(k_Tint.r, k_Tint.g, k_Tint.b, 0f));

            // Boot: countdown and progress bar in the middle.
            m_Boot = new GameObject("Boot", typeof(RectTransform));
            m_Boot.transform.SetParent(m_Root, false);
            m_BootText = Text(m_Boot.transform, "Countdown", new Vector2(0f, 44f), new Vector2(1000f, 60f), 38, TextAlignmentOptions.Center);
            Box(m_Boot.transform, "BarFrame", new Vector2(0f, -10f), new Vector2(504f, 14f), k_Line);
            Box(m_Boot.transform, "BarBack", new Vector2(0f, -10f), new Vector2(500f, 10f), new Color(0.2f, 0f, 0f, 0.9f));
            var bar = Box(m_Boot.transform, "Bar", new Vector2(-250f, -10f), new Vector2(500f, 10f), k_Text);
            m_BootBar = bar.rectTransform;
            m_BootBar.pivot = new Vector2(0f, 0.5f);
            m_Boot.SetActive(false);

            // Hold indicator, low in the middle.
            m_Hold = new GameObject("Hold", typeof(RectTransform));
            m_Hold.transform.SetParent(m_Root, false);
            m_HoldText = Text(m_Hold.transform, "HoldText", new Vector2(0f, -166f), new Vector2(600f, 40f), 26, TextAlignmentOptions.Center);
            Box(m_Hold.transform, "HoldBack", new Vector2(0f, -200f), new Vector2(300f, 8f), new Color(0.2f, 0f, 0f, 0.9f));
            var holdBar = Box(m_Hold.transform, "HoldBar", new Vector2(-150f, -200f), new Vector2(300f, 8f), k_Text);
            m_HoldBar = holdBar.rectTransform;
            m_HoldBar.pivot = new Vector2(0f, 0.5f);
            m_Hold.SetActive(false);

            m_Live = new GameObject("Live", typeof(RectTransform));
            m_Live.transform.SetParent(m_Root, false);
            var live = m_Live.transform;

            // Left: visual scan, live numbers over scrolling data.
            Reveal(Text(live, "ScanTitle", new Vector2(-570f, 400f), new Vector2(400f, 34f), 25, TextAlignmentOptions.Left)).text = k_Mono + "¬ VISUAL SCAN";
            m_LeftLive = Reveal(Text(live, "ScanLive", new Vector2(-570f, 315f), new Vector2(400f, 130f), 22, TextAlignmentOptions.TopLeft));
            Box(live, "ScanRule", new Vector2(-570f, 236f), new Vector2(400f, 3f), k_Line);
            m_Left = Reveal(Text(live, "ScanData", new Vector2(-570f, 80f), new Vector2(400f, 290f), 20, TextAlignmentOptions.TopLeft));

            // Right: sensor log.
            Reveal(Text(live, "LogTitle", new Vector2(570f, 400f), new Vector2(400f, 34f), 25, TextAlignmentOptions.Right)).text = k_Mono + "<< SENSOR LOG >>";
            m_Right = Reveal(Text(live, "LogData", new Vector2(570f, 140f), new Vector2(400f, 460f), 19, TextAlignmentOptions.TopRight));

            // Top: target analysis and the MATCH box.
            Reveal(Text(live, "AnalysisTitle", new Vector2(0f, 470f), new Vector2(560f, 34f), 25, TextAlignmentOptions.Left)).text = k_Mono + "¬ TARGET ANALYSIS";
            m_Analysis = Reveal(Text(live, "Analysis", new Vector2(0f, 405f), new Vector2(560f, 96f), 22, TextAlignmentOptions.TopLeft));
            Reveal(Text(live, "MatchTitle", new Vector2(0f, 340f), new Vector2(560f, 30f), 22, TextAlignmentOptions.Center)).text = k_Mono + "•• MATCH ••";
            Box(live, "MatchBox", new Vector2(0f, 302f), new Vector2(470f, 40f), k_BoxFill);
            m_Match = Reveal(Text(live, "Match", new Vector2(0f, 302f), new Vector2(470f, 40f), 23, TextAlignmentOptions.Center));
            m_Match.color = k_BoxText;

            // Side scales: head pitch (left) and nearest range in meters (right).
            m_PitchMarker = Scale(live, -800f, false);
            m_RangeMarker = Scale(live, 800f, true);

            // Bottom: uptime and mode.
            m_Uptime = Reveal(Text(live, "Uptime", new Vector2(-570f, -330f), new Vector2(400f, 64f), 22, TextAlignmentOptions.TopLeft));
            m_ModeText = Reveal(Text(live, "Mode", new Vector2(570f, -330f), new Vector2(400f, 64f), 22, TextAlignmentOptions.TopRight));
            m_Live.SetActive(false);

            m_Root.gameObject.SetActive(false);
        }

        TextMeshProUGUI Reveal(TextMeshProUGUI text)
        {
            m_Revealed.Add(text);
            return text;
        }

        // A vertical ruler 500 tall with ticks and numbers 0 to 7, and its marker.
        RectTransform Scale(Transform parent, float x, bool numbers)
        {
            Box(parent, "Ruler", new Vector2(x, 0f), new Vector2(2f, 500f), k_Line);
            var side = x < 0f ? 1f : -1f;
            for (var i = 0; i <= 28; i++)
            {
                var major = i % 4 == 0;
                var y = -250f + i * (500f / 28f);
                var length = major ? 18f : 9f;
                Box(parent, "Tick", new Vector2(x + side * length / 2f, y), new Vector2(length, 2f), k_Line);
                if (major && numbers)
                    Text(parent, "Mark", new Vector2(x + side * 34f, y), new Vector2(36f, 24f), 18,
                        TextAlignmentOptions.Center).text = (i / 4).ToString();
            }

            var marker = Box(parent, "Marker", new Vector2(x - side * 14f, 0f), new Vector2(20f, 8f), k_Text);
            return marker.rectTransform;
        }

        static TextMeshProUGUI Text(Transform parent, string name, Vector2 position, Vector2 size, float fontSize, TextAlignmentOptions alignment)
        {
            var text = UiFactory.CreateText(parent, name, "", fontSize, alignment);
            text.color = k_Text;
            text.fontStyle = FontStyles.Bold;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.raycastTarget = false;
            var rect = text.rectTransform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            return text;
        }

        static Image Box(Transform parent, string name, Vector2 position, Vector2 size, Color color)
        {
            var image = UiFactory.CreateImage(parent, name, color);
            image.raycastTarget = false;
            var rect = image.rectTransform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            return image;
        }

        // A pitch sweep with a soft envelope.
        static AudioClip Sweep(string name, float from, float to, float seconds)
        {
            const int rate = 44100;
            var count = (int)(rate * seconds);
            var samples = new float[count];
            var phase = 0f;
            for (var i = 0; i < count; i++)
            {
                var t = i / (float)count;
                phase += 2f * Mathf.PI * Mathf.Lerp(from, to, t * t) / rate;
                var envelope = Mathf.Clamp01(t / 0.05f) * Mathf.Clamp01((1f - t) / 0.3f);
                // A square-ish edge for a synthetic, electronic tone.
                samples[i] = Mathf.Clamp(Mathf.Sin(phase) * 2.5f, -1f, 1f) * 0.16f * envelope;
            }

            var clip = AudioClip.Create(name, count, 1, rate, false);
            clip.SetData(samples, 0);
            return clip;
        }
    }
}
