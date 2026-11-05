using System;
using AIEdge.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AIEdge.AskImage
{
    /// <summary>
    /// A "polaroid" pinned where the user framed something: the cropped camera image, the question and the streamed
    /// answer. It owns the image texture and frees it when closed.
    /// </summary>
    public class AnswerCard : MonoBehaviour
    {
        const float k_Width = 260f;
        const float k_Height = 420f;
        const float k_Margin = 12f;
        const float k_MaxImageHeight = 190f;
        const float k_TowardUser = 0.04f;

        /// <summary>Raised when the card is closed.</summary>
        public event Action<AnswerCard> Closed;

        Texture2D m_Image;
        TextMeshProUGUI m_Answer;
        TextMeshProUGUI m_Stats;
        TextMeshProUGUI m_Question;
        const float k_BesideGap = 0.04f;

        /// <summary>Creates a card at a framed region, facing the user.</summary>
        public static AnswerCard Create(Transform parent, Texture2D image, string question, FramedRegion region)
        {
            var go = new GameObject("AnswerCard");
            go.transform.SetParent(parent, false);
            var card = go.AddComponent<AnswerCard>();
            card.m_Image = image;
            card.Build(question);
            go.transform.SetPositionAndRotation(region.Center - region.Rotation * Vector3.forward * k_TowardUser, region.Rotation);
            return card;
        }

        /// <summary>Adds the depth measurement under the question.</summary>
        public void SetMeasurement(string label) => m_Question.text += $"\n<color=#2F6FD0><i>Depth: {MarkdownToTmp.Escape(label)}</i></color>";

        /// <summary>Moves the card beside a measured object on its right, facing the user.</summary>
        public void PinBeside(Vector3 objectCenter, float objectWidth)
        {
            var head = Camera.main != null ? Camera.main.transform.position : objectCenter - transform.forward;
            var forward = Vector3.ProjectOnPlane(objectCenter - head, Vector3.up);
            forward = forward.sqrMagnitude > 1e-4f ? forward.normalized : transform.forward;
            var right = Vector3.Cross(Vector3.up, forward);
            var position = objectCenter + right * (objectWidth / 2f + k_BesideGap + k_Width * 0.0005f) - forward * k_TowardUser;
            transform.SetPositionAndRotation(position, Quaternion.LookRotation(position - head, Vector3.up));
        }

        /// <summary>Shows the answer so far (TextMeshPro rich text).</summary>
        public void SetAnswer(string richText) => m_Answer.text = richText;

        /// <summary>Shows the inference stats line.</summary>
        public void SetStats(string text) => m_Stats.text = text;

        /// <summary>Removes the card.</summary>
        public void Close()
        {
            Closed?.Invoke(this);
            Destroy(gameObject);
        }

        void OnDestroy()
        {
            if (m_Image != null)
                Destroy(m_Image);
        }

        void Build(string question)
        {
            var panel = UiFactory.CreatePanel(transform, "Canvas", new Vector2(k_Width, k_Height));
            panel.Find("Background").GetComponent<Image>().color = new Color(0.96f, 0.95f, 0.92f, 0.97f);

            var inner = k_Width - 2f * k_Margin;
            var imageHeight = Mathf.Min(k_MaxImageHeight, inner * m_Image.height / Mathf.Max(1f, m_Image.width));
            var imageWidth = imageHeight * m_Image.width / Mathf.Max(1f, m_Image.height);
            var photo = new GameObject("Photo", typeof(RectTransform), typeof(RawImage)).GetComponent<RawImage>();
            photo.transform.SetParent(panel, false);
            photo.texture = m_Image;
            photo.raycastTarget = false;
            UiFactory.Place(photo.rectTransform, (k_Width - imageWidth) / 2f, k_Margin, imageWidth, imageHeight);

            var y = k_Margin + imageHeight + 8f;
            var dark = new Color(0.12f, 0.12f, 0.14f, 1f);
            var questionText = m_Question = UiFactory.CreateText(panel, "Question", MarkdownToTmp.Escape(question), 14);
            questionText.color = new Color(0.35f, 0.36f, 0.4f, 1f);
            questionText.fontStyle = FontStyles.Italic;
            questionText.overflowMode = TextOverflowModes.Ellipsis;
            UiFactory.Place(questionText.rectTransform, k_Margin, y, inner, 40);
            y += 42f;

            m_Answer = UiFactory.CreateText(panel, "Answer", "<color=#888>Thinking…</color>", 16);
            m_Answer.color = dark;
            m_Answer.enableAutoSizing = true;
            m_Answer.fontSizeMin = 10;
            m_Answer.fontSizeMax = 16;
            UiFactory.Place(m_Answer.rectTransform, k_Margin, y, inner, k_Height - y - 58f);

            m_Stats = UiFactory.CreateText(panel, "Stats", "", 10);
            m_Stats.color = new Color(0.45f, 0.46f, 0.5f, 1f);
            UiFactory.Place(m_Stats.rectTransform, k_Margin, k_Height - 52f, inner - 70f, 40);

            var close = UiFactory.CreateButton(panel, "Close", new Color(0.25f, 0.26f, 0.3f, 1f), Close, 14);
            UiFactory.Place((RectTransform)close.transform, k_Width - k_Margin - 64f, k_Height - 46f, 64f, 34f);
        }
    }
}
