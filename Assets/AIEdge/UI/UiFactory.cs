using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace AIEdge.UI
{
    /// <summary>
    /// Builds world-space uGUI panels in code with a shared look. Panels use 1 px = 1 mm and a
    /// <see cref="TrackedDeviceGraphicRaycaster"/>, so XRI hand poke and ray interactors can use them.
    /// </summary>
    public static class UiFactory
    {
        public static readonly Color PanelColor = new Color(0.08f, 0.09f, 0.12f, 0.94f);
        public static readonly Color CardColor = new Color(0.14f, 0.16f, 0.21f, 1f);
        public static readonly Color AccentColor = new Color(0.25f, 0.5f, 0.95f, 1f);
        public static readonly Color DangerColor = new Color(0.75f, 0.28f, 0.28f, 1f);
        public static readonly Color MutedTextColor = new Color(0.7f, 0.73f, 0.8f, 1f);

        /// <summary>Creates a world-space canvas of the given size in millimeters, with a background.</summary>
        public static RectTransform CreatePanel(Transform parent, string name, Vector2 sizeMm)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(TrackedDeviceGraphicRaycaster));
            go.transform.SetParent(parent, false);
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = Camera.main;
            var rect = (RectTransform)go.transform;
            rect.sizeDelta = sizeMm;
            rect.localScale = Vector3.one * 0.001f;

            var background = CreateImage(rect, "Background", PanelColor);
            Stretch(background.rectTransform);
            return rect;
        }

        /// <summary>Creates a colored image.</summary>
        public static Image CreateImage(Transform parent, string name, Color color)
        {
            var image = CreateChild<Image>(parent, name);
            image.color = color;
            return image;
        }

        /// <summary>Creates a text label.</summary>
        public static TextMeshProUGUI CreateText(Transform parent, string name, string text, float size,
            TextAlignmentOptions alignment = TextAlignmentOptions.TopLeft)
        {
            var label = CreateChild<TextMeshProUGUI>(parent, name);
            label.text = text;
            label.fontSize = size;
            label.alignment = alignment;
            label.color = Color.white;
            label.textWrappingMode = TextWrappingModes.Normal;
            label.raycastTarget = false;
            return label;
        }

        /// <summary>Creates a button with a centered label.</summary>
        public static Button CreateButton(Transform parent, string label, Color color, Action onClick, float fontSize = 26)
        {
            var image = CreateImage(parent, label, color);
            var button = image.gameObject.AddComponent<Button>();
            var colors = button.colors;
            colors.highlightedColor = new Color(1.15f, 1.15f, 1.15f, 1f);
            colors.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
            colors.disabledColor = new Color(0.45f, 0.45f, 0.45f, 0.6f);
            button.colors = colors;
            button.onClick.AddListener(() => onClick?.Invoke());

            var text = CreateText(image.transform, "Label", label, fontSize, TextAlignmentOptions.Center);
            Stretch(text.rectTransform);
            return button;
        }

        /// <summary>Sets a button's label text.</summary>
        public static void SetLabel(Button button, string label)
        {
            button.GetComponentInChildren<TextMeshProUGUI>().text = label;
        }

        /// <summary>Creates a horizontal progress bar; drive it with <see cref="SetProgress"/>.</summary>
        public static Image CreateProgressBar(Transform parent, string name)
        {
            var track = CreateImage(parent, name, new Color(1f, 1f, 1f, 0.12f));
            var fill = CreateImage(track.transform, "Fill", AccentColor);
            fill.rectTransform.anchorMin = Vector2.zero;
            fill.rectTransform.anchorMax = new Vector2(0f, 1f);
            fill.rectTransform.offsetMin = fill.rectTransform.offsetMax = Vector2.zero;
            return fill;
        }

        /// <summary>Sets a progress bar fill (0..1).</summary>
        public static void SetProgress(Image fill, float fraction)
        {
            fill.rectTransform.anchorMax = new Vector2(Mathf.Clamp01(fraction), 1f);
        }

        /// <summary>Places a rect by its top-left corner, in millimeters from the parent's top-left.</summary>
        public static void Place(RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
        }

        /// <summary>Stretches a rect over its parent with optional insets.</summary>
        public static void Stretch(RectTransform rect, float left = 0, float right = 0, float top = 0, float bottom = 0)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(left, bottom);
            rect.offsetMax = new Vector2(-right, -top);
        }

        /// <summary>Moves a panel in front of the user's head, level with the horizon.</summary>
        public static void PlaceInFront(Transform panel, float distance, float heightOffset = -0.1f, float lateralOffset = 0f)
        {
            var head = Camera.main != null ? Camera.main.transform : null;
            if (head == null)
                return;

            var forward = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            if (forward.sqrMagnitude < 0.01f)
                forward = Vector3.forward;
            var right = Vector3.Cross(Vector3.up, forward);
            var position = head.position + forward * distance + right * lateralOffset + Vector3.up * heightOffset;
            panel.SetPositionAndRotation(position, Quaternion.LookRotation(forward, Vector3.up));
        }

        static T CreateChild<T>(Transform parent, string name) where T : Component
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go.AddComponent<T>();
        }
    }
}
