using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AIEdge.UI
{
    /// <summary>
    /// Editor for the user's standing instructions (<see cref="UserInstructions"/>): a text box, ready-made presets
    /// such as "Strict diet", and an on/off switch. Opened from the launcher and from What's That?.
    /// </summary>
    public class InstructionsPanel : MonoBehaviour
    {
        const float k_Width = 760f;
        const float k_Height = 600f;

        [SerializeField, Tooltip("Distance from the user's head when opened, in meters.")]
        float m_Distance = 0.85f;

        /// <summary>Raised when the panel closes.</summary>
        public event Action Closed;

        /// <summary>True while the panel is open.</summary>
        public bool IsOpen => m_Panel != null && m_Panel.gameObject.activeSelf;

        RectTransform m_Panel;
        TMP_InputField m_Input;
        Button m_Toggle;
        TextMeshProUGUI m_Count;
        bool m_Enabled;

        /// <summary>Opens the editor in front of the user with the saved instructions.</summary>
        public void Open()
        {
            if (m_Panel == null)
                Build();
            m_Panel.gameObject.SetActive(true);
            UiFactory.PlaceInFront(transform, m_Distance, -0.05f);
            m_Input.text = UserInstructions.Text;
            m_Enabled = UserInstructions.Enabled;
            Refresh();
        }

        /// <summary>Closes without saving.</summary>
        public void Close()
        {
            if (m_Panel == null || !m_Panel.gameObject.activeSelf)
                return;
            m_Panel.gameObject.SetActive(false);
            Closed?.Invoke();
        }

        void Save()
        {
            UserInstructions.Set(m_Input.text, m_Enabled);
            Close();
        }

        void Refresh()
        {
            UiFactory.SetLabel(m_Toggle, m_Enabled ? "Instructions: on" : "Instructions: off");
            m_Toggle.GetComponent<Image>().color = m_Enabled ? UiFactory.AccentColor : UiFactory.CardColor;
            var length = m_Input.text.Length;
            m_Count.text = $"{length} / {UserInstructions.MaxLength} characters · Used by AI Chat, Point & Ask and What's That?";
            m_Count.color = length > UserInstructions.MaxLength ? new Color(1f, 0.5f, 0.5f) : UiFactory.MutedTextColor;
        }

        void Build()
        {
            m_Panel = UiFactory.CreatePanel(transform, "InstructionsPanel", new Vector2(k_Width, k_Height));

            var title = UiFactory.CreateText(m_Panel, "Title", "My instructions", 30);
            title.fontStyle = FontStyles.Bold;
            UiFactory.Place(title.rectTransform, 24, 16, 500, 42);
            var close = UiFactory.CreateButton(m_Panel, "Close", UiFactory.CardColor, Close, 22);
            UiFactory.Place((RectTransform)close.transform, k_Width - 116, 18, 92, 50);

            var how = UiFactory.CreateText(m_Panel, "HowTo",
                "Tell Gemma about yourself and how to reply, in your own words. It follows these in every answer, " +
                "including about things you point at. Start from an example:", 19);
            how.color = UiFactory.MutedTextColor;
            UiFactory.Place(how.rectTransform, 24, 68, k_Width - 48, 56);

            var presets = UserInstructions.Presets;
            var chipWidth = (k_Width - 48 - 8 * (presets.Length - 1)) / presets.Length;
            for (var i = 0; i < presets.Length; i++)
            {
                var preset = presets[i];
                var chip = UiFactory.CreateButton(m_Panel, preset.Name, UiFactory.CardColor, () =>
                {
                    m_Input.text = preset.Text;
                    m_Enabled = true;
                    Refresh();
                }, 18);
                UiFactory.Place((RectTransform)chip.transform, 24 + i * (chipWidth + 8), 132, chipWidth, 48);
            }

            m_Input = CreateInputField(m_Panel);
            UiFactory.Place((RectTransform)m_Input.transform, 24, 196, k_Width - 48, 240);
            m_Input.onValueChanged.AddListener(_ => Refresh());

            m_Count = UiFactory.CreateText(m_Panel, "Count", "", 16);
            UiFactory.Place(m_Count.rectTransform, 24, 442, k_Width - 48, 28);

            m_Toggle = UiFactory.CreateButton(m_Panel, "Instructions: on", UiFactory.AccentColor, () =>
            {
                m_Enabled = !m_Enabled;
                Refresh();
            }, 20);
            UiFactory.Place((RectTransform)m_Toggle.transform, 24, 520, 240, 56);
            var clear = UiFactory.CreateButton(m_Panel, "Clear", UiFactory.CardColor, () => m_Input.text = "", 20);
            UiFactory.Place((RectTransform)clear.transform, 276, 520, 160, 56);
            var save = UiFactory.CreateButton(m_Panel, "Save", UiFactory.AccentColor, Save, 22);
            UiFactory.Place((RectTransform)save.transform, k_Width - 224, 520, 200, 56);
        }

        static TMP_InputField CreateInputField(Transform parent)
        {
            var background = UiFactory.CreateImage(parent, "Instructions", UiFactory.CardColor);
            var area = new GameObject("Text Area", typeof(RectTransform), typeof(RectMask2D));
            area.transform.SetParent(background.transform, false);
            var areaRect = (RectTransform)area.transform;
            UiFactory.Stretch(areaRect, 16, 16, 12, 12);

            var hint = UiFactory.CreateText(areaRect, "Placeholder",
                "For example: I'm on a strict diet. If I point at a cake, make fun of me.", 22);
            hint.color = UiFactory.MutedTextColor;
            hint.fontStyle = FontStyles.Italic;
            UiFactory.Stretch(hint.rectTransform);
            var text = UiFactory.CreateText(areaRect, "Text", "", 22);
            UiFactory.Stretch(text.rectTransform);

            var field = background.gameObject.AddComponent<TMP_InputField>();
            field.targetGraphic = background;
            field.textViewport = areaRect;
            field.textComponent = text;
            field.placeholder = hint;
            field.fontAsset = text.font;
            field.pointSize = 22;
            field.lineType = TMP_InputField.LineType.MultiLineNewline;
            field.characterLimit = UserInstructions.MaxLength;
            field.richText = false;
            return field;
        }
    }
}
