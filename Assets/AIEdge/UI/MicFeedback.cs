using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AIEdge.UI
{
    /// <summary>
    /// Signals voice input to the user, however it was started (Talk button, mic gesture, the launcher's "Try it"):
    /// a rising two-note tone and a red microphone symbol when listening starts, a falling tone and a crossed-out
    /// symbol when it stops. While listening the symbol's ring follows the voice level; while transcribing it shows
    /// dots. The symbol floats low in the field of view and follows the head gently.
    /// </summary>
    public class MicFeedback : MonoBehaviour
    {
        const float k_Distance = 0.55f;
        const float k_Drop = 0.17f;
        const float k_FollowSpeed = 6f;
        const float k_EndShowSeconds = 1.2f;
        const float k_HintSeconds = 3f;
        const int k_SampleRate = 44100;

        static readonly Color k_ListeningColor = new Color(0.9f, 0.22f, 0.25f, 0.95f);
        static readonly Color k_IdleColor = new Color(0.32f, 0.35f, 0.42f, 0.95f);
        static readonly Color k_HintColor = new Color(0.25f, 0.5f, 0.95f, 0.95f);

        /// <summary>The feedback in the scene, or null.</summary>
        public static MicFeedback Instance { get; private set; }

        /// <summary>Number of start signals given since launch (for tests).</summary>
        public int StartCount { get; private set; }

        /// <summary>Number of stop signals given since launch (for tests).</summary>
        public int StopCount { get; private set; }

        /// <summary>True while the symbol is shown.</summary>
        public bool IsShown => m_Root != null && m_Root.gameObject.activeSelf;

        AudioSource m_Audio;
        AudioClip m_StartClip;
        AudioClip m_StopClip;
        AudioClip m_HintClip;
        RectTransform m_Root;
        Image m_Disc;
        Image m_Ring;
        Image m_Slash;
        TextMeshProUGUI m_Label;
        SpeechInput.Status m_LastState = SpeechInput.Status.Unavailable;
        float m_HideAt = -1f;
        bool m_Placed;

        void Awake()
        {
            Instance = this;
            m_Audio = gameObject.AddComponent<AudioSource>();
            m_Audio.playOnAwake = false;
            m_Audio.spatialBlend = 0f;
            m_Audio.volume = 0.6f;
            m_StartClip = Tone("MicOn", 660f, 990f);
            m_StopClip = Tone("MicOff", 880f, 520f);
            m_HintClip = Tone("MicHint", 520f, 520f);
            Build();
            m_Root.gameObject.SetActive(false);
        }

        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
            if (m_Root != null)
                Destroy(m_Root.gameObject);
        }

        /// <summary>Shows a short message under a muted microphone symbol, with a soft tone.</summary>
        public static void ShowHint(string text)
        {
            if (Instance == null || string.IsNullOrWhiteSpace(text))
                return;
            Instance.Show(k_HintColor, false, text);
            Instance.m_Audio.PlayOneShot(Instance.m_HintClip, 0.6f);
            Instance.m_HideAt = Time.unscaledTime + k_HintSeconds;
        }

        void Update()
        {
            var input = SpeechInput.Instance;
            var state = input != null ? input.State : SpeechInput.Status.Unavailable;
            if (state != m_LastState)
            {
                if (state == SpeechInput.Status.Recording)
                    OnStarted();
                else if (m_LastState == SpeechInput.Status.Recording)
                    OnStopped(state == SpeechInput.Status.Transcribing);
                else if (m_LastState == SpeechInput.Status.Transcribing && IsShown && m_HideAt < 0f)
                    m_HideAt = Time.unscaledTime + 0.4f;
                m_LastState = state;
            }

            if (state == SpeechInput.Status.Recording)
            {
                var level = Mathf.Clamp01(input.Level);
                m_Ring.rectTransform.localScale = Vector3.one * (1f + level * 0.45f);
                m_Ring.color = new Color(k_ListeningColor.r, k_ListeningColor.g, k_ListeningColor.b, 0.25f + level * 0.5f);
                m_Label.text = $"{(AppLanguage.IsItalian ? "Ti ascolto" : "Listening")} {input.RecordedSeconds:0}s";
            }
            else if (state == SpeechInput.Status.Transcribing)
            {
                m_Label.text = (AppLanguage.IsItalian ? "Trascrivo" : "Transcribing") + new string('.', 1 + (int)(Time.unscaledTime * 3f) % 3);
            }

            if (m_HideAt >= 0f && Time.unscaledTime >= m_HideAt)
            {
                m_HideAt = -1f;
                m_Root.gameObject.SetActive(false);
                m_Placed = false;
            }
        }

        void LateUpdate()
        {
            if (!IsShown || Camera.main == null)
                return;
            var head = Camera.main.transform;
            var forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (forward.sqrMagnitude < 0.01f)
                forward = head.forward;
            forward.Normalize();
            var target = head.position + forward * k_Distance + Vector3.down * k_Drop;
            m_Root.position = m_Placed ? Vector3.Lerp(m_Root.position, target, 1f - Mathf.Exp(-k_FollowSpeed * Time.unscaledDeltaTime)) : target;
            m_Root.rotation = Quaternion.LookRotation(m_Root.position - head.position, Vector3.up);
            m_Placed = true;
        }

        void OnStarted()
        {
            StartCount++;
            m_Audio.PlayOneShot(m_StartClip);
            Show(k_ListeningColor, false, AppLanguage.IsItalian ? "Ti ascolto" : "Listening");
            m_HideAt = -1f;
        }

        void OnStopped(bool transcribing)
        {
            StopCount++;
            m_Audio.PlayOneShot(m_StopClip);
            Show(k_IdleColor, true, transcribing ? (AppLanguage.IsItalian ? "Trascrivo" : "Transcribing") : (AppLanguage.IsItalian ? "Microfono spento" : "Mic off"));
            m_HideAt = transcribing ? -1f : Time.unscaledTime + k_EndShowSeconds;
        }

        void Show(Color color, bool muted, string label)
        {
            m_Root.gameObject.SetActive(true);
            m_Disc.color = color;
            m_Slash.enabled = muted;
            m_Ring.enabled = !muted;
            m_Ring.rectTransform.localScale = Vector3.one;
            m_Label.text = label;
        }

        // ---- Building the symbol and the tones --------------------------------------------------------------------

        void Build()
        {
            var canvasObject = new GameObject("MicFeedback", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 100;
            m_Root = (RectTransform)canvasObject.transform;
            m_Root.sizeDelta = new Vector2(320f, 150f);
            m_Root.localScale = Vector3.one * 0.0005f; // 16 cm × 7.5 cm

            var circle = Sprite(Circle(128));
            m_Ring = Element("Ring", circle, 96f, new Vector2(0f, 26f));
            m_Disc = Element("Disc", circle, 84f, new Vector2(0f, 26f));
            Element("Mic", Sprite(Microphone(128)), 64f, new Vector2(0f, 26f));
            m_Slash = Element("Slash", Sprite(Slash(128)), 70f, new Vector2(0f, 26f));

            m_Label = UiFactory.CreateText(m_Root, "Label", "", 24, TextAlignmentOptions.Center);
            m_Label.rectTransform.anchorMin = m_Label.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            m_Label.rectTransform.sizeDelta = new Vector2(320f, 40f);
            m_Label.rectTransform.anchoredPosition = new Vector2(0f, -46f);
            m_Label.textWrappingMode = TextWrappingModes.Normal;
        }

        Image Element(string name, Sprite sprite, float size, Vector2 position)
        {
            var image = UiFactory.CreateImage(m_Root, name, Color.white);
            image.sprite = sprite;
            image.raycastTarget = false;
            image.rectTransform.anchorMin = image.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            image.rectTransform.sizeDelta = new Vector2(size, size);
            image.rectTransform.anchoredPosition = position;
            return image;
        }

        static Sprite Sprite(Texture2D texture) =>
            UnityEngine.Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f));

        static Texture2D Paint(int size, System.Func<Vector2, float> coverage)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[size * size];
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                // Coordinates from -1 to 1, y up; 2x2 supersampling for smooth edges.
                var a = 0f;
                for (var s = 0; s < 4; s++)
                {
                    var p = new Vector2((x + 0.25f + 0.5f * (s % 2)) / size * 2f - 1f, (y + 0.25f + 0.5f * (s / 2)) / size * 2f - 1f);
                    a += coverage(p);
                }

                pixels[y * size + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(a / 4f) * 255));
            }

            texture.SetPixels32(pixels);
            texture.Apply();
            return texture;
        }

        static Texture2D Circle(int size) => Paint(size, p => p.magnitude <= 0.98f ? 1f : 0f);

        // A microphone: a capsule head, a U-shaped holder around it, a stem and a base.
        static Texture2D Microphone(int size) => Paint(size, p =>
        {
            var head = Capsule(p, new Vector2(0f, 0.3f), new Vector2(0f, -0.05f), 0.26f);
            var ring = Mathf.Abs(new Vector2(p.x, p.y + 0.05f).magnitude - 0.45f) < 0.07f && p.y < -0.05f;
            var stem = Mathf.Abs(p.x) < 0.07f && p.y < -0.48f && p.y > -0.72f;
            var foot = Mathf.Abs(p.x) < 0.32f && p.y <= -0.72f && p.y > -0.84f;
            return head || ring || stem || foot ? 1f : 0f;
        });

        static Texture2D Slash(int size) => Paint(size, p => Capsule(p, new Vector2(-0.75f, 0.75f), new Vector2(0.75f, -0.75f), 0.09f) ? 1f : 0f);

        static bool Capsule(Vector2 p, Vector2 a, Vector2 b, float radius)
        {
            var ab = b - a;
            var t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
            return (p - (a + ab * t)).magnitude <= radius;
        }

        // Two short notes with soft attack and release.
        static AudioClip Tone(string name, float first, float second)
        {
            const float note = 0.075f;
            var count = (int)(k_SampleRate * note * 2f);
            var samples = new float[count];
            var phase = 0f;
            for (var i = 0; i < count; i++)
            {
                var t = i / (float)k_SampleRate;
                var frequency = t < note ? first : second;
                phase += 2f * Mathf.PI * frequency / k_SampleRate;
                var local = t % note;
                var envelope = Mathf.Clamp01(local / 0.008f) * Mathf.Clamp01((note - local) / 0.02f);
                samples[i] = Mathf.Sin(phase) * 0.35f * envelope;
            }

            var clip = AudioClip.Create(name, count, 1, k_SampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }
    }
}
