using AIEdge.Spatial;
using UnityEngine;
using UnityEngine.UI;

namespace AIEdge.WhatsThat
{
    /// <summary>
    /// A glowing cut-out over a real object: a mask drawn on a plane at the object's depth, facing the viewer, so it
    /// lines up with the object as seen from the eye (or the camera, for an image mask). A translucent fill with a
    /// bright edge, gently pulsing.
    /// </summary>
    public class ObjectHighlight : MonoBehaviour
    {
        const float k_PixelsPerMeter = 1000f; // canvas units: 1 unit = 1 mm
        const float k_FadeSeconds = 0.25f;
        const float k_PulseSpeed = 3f;

        /// <summary>Highlight while pointing (depth only).</summary>
        public static readonly Color LiveColor = new Color(0.45f, 0.8f, 1f, 1f);

        /// <summary>Highlight of the selected object (image segmentation).</summary>
        public static readonly Color SelectedColor = new Color(0.35f, 1f, 0.6f, 1f);

        /// <summary>"depth" or "segmentation": where the current mask came from.</summary>
        public string Source { get; private set; } = "";

        /// <summary>Fraction of the plane covered by the mask.</summary>
        public float Coverage { get; private set; }

        RectTransform m_Canvas;
        RawImage m_Image;
        Texture2D m_Texture;
        Color m_Color = LiveColor;
        float m_Shown;
        bool m_Pulse = true;

        /// <summary>Creates a hidden highlight.</summary>
        public static ObjectHighlight Create(string name)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Canvas));
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = -1;
            var highlight = go.AddComponent<ObjectHighlight>();
            highlight.m_Canvas = (RectTransform)go.transform;
            highlight.m_Canvas.localScale = Vector3.one / k_PixelsPerMeter;
            var image = new GameObject("Mask", typeof(RectTransform), typeof(RawImage));
            image.transform.SetParent(go.transform, false);
            highlight.m_Image = image.GetComponent<RawImage>();
            highlight.m_Image.raycastTarget = false;
            var rect = (RectTransform)image.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            highlight.SetVisible(false);
            return highlight;
        }

        /// <summary>Places the plane: center, orientation (+z away from the viewer, +y up) and size in meters.</summary>
        public void SetPlane(Vector3 center, Quaternion rotation, Vector2 size)
        {
            m_Canvas.SetPositionAndRotation(center, rotation);
            m_Canvas.sizeDelta = size * k_PixelsPerMeter;
        }

        /// <summary>Shows a depth mask (x right, y up).</summary>
        public void ShowDepthMask(bool[,] mask)
        {
            var columns = mask.GetLength(0);
            var rows = mask.GetLength(1);
            Coverage = DepthMask.Coverage(mask);
            SetAlpha(DepthMask.HighlightAlpha(mask, 1, 60, 200), columns, rows, FilterMode.Bilinear);
            Source = "depth";
        }

        /// <summary>
        /// Shows an image mask: <paramref name="alpha"/> is <paramref name="width"/> x <paramref name="height"/>,
        /// row by row from the bottom.
        /// </summary>
        public void ShowMask(byte[] alpha, int width, int height, float coverage)
        {
            Coverage = coverage;
            SetAlpha(alpha, width, height, FilterMode.Bilinear);
            Source = "segmentation";
        }

        /// <summary>
        /// Shows a segmentation mask (probabilities 0-255, <paramref name="size"/>², row 0 at the bottom), reduced to
        /// <see cref="SegmentationResolution"/> and drawn as a fill with a bright edge.
        /// </summary>
        public void ShowSegmentation(byte[] probabilities, int size, float coverage)
        {
            var mask = Downsample(probabilities, size, SegmentationResolution);
            ShowMask(DepthMask.HighlightAlpha(mask, 2, 55, 230), SegmentationResolution, SegmentationResolution, coverage);
        }

        /// <summary>Resolution of the drawn segmentation highlight.</summary>
        public const int SegmentationResolution = 256;

        /// <summary>Reduces a square probability mask to a boolean mask (x right, y up) by sampling cell centers.</summary>
        public static bool[,] Downsample(byte[] probabilities, int size, int target)
        {
            var mask = new bool[target, target];
            for (var y = 0; y < target; y++)
            for (var x = 0; x < target; x++)
            {
                var sx = Mathf.Min(size - 1, (int)((x + 0.5f) * size / target));
                var sy = Mathf.Min(size - 1, (int)((y + 0.5f) * size / target));
                mask[x, y] = probabilities[sy * size + sx] > 127;
            }

            return mask;
        }

        /// <summary>Tint and pulse.</summary>
        public void SetStyle(Color color, bool pulse)
        {
            m_Color = color;
            m_Pulse = pulse;
        }

        /// <summary>Shows or hides the highlight (fading in when shown).</summary>
        public void SetVisible(bool visible)
        {
            if (visible && !gameObject.activeSelf)
                m_Shown = Time.time;
            gameObject.SetActive(visible);
        }

        void SetAlpha(byte[] alpha, int width, int height, FilterMode filter)
        {
            if (m_Texture == null || m_Texture.width != width || m_Texture.height != height)
            {
                if (m_Texture != null)
                    Destroy(m_Texture);
                m_Texture = new Texture2D(width, height, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            }

            m_Texture.filterMode = filter;
            var pixels = new Color32[width * height];
            for (var i = 0; i < pixels.Length; i++)
                pixels[i] = new Color32(255, 255, 255, alpha[i]);
            m_Texture.SetPixels32(pixels);
            m_Texture.Apply(false);
            m_Image.texture = m_Texture;
            SetVisible(true);
        }

        void Update()
        {
            var fade = Mathf.Clamp01((Time.time - m_Shown) / k_FadeSeconds);
            var pulse = m_Pulse ? 0.8f + 0.2f * Mathf.Sin(Time.time * k_PulseSpeed) : 1f;
            var color = m_Color;
            color.a = fade * pulse;
            m_Image.color = color;
        }

        void OnDestroy()
        {
            if (m_Texture != null)
                Destroy(m_Texture);
        }
    }
}
