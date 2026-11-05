using System;
using System.Globalization;
using AIEdge.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace AIEdge.Chat
{
    /// <summary>
    /// A saved conversation as a small floating card. Tap it (grab and let go in place) or drop it on the chat
    /// panel to resume the thread; throw it away to delete the thread.
    /// </summary>
    public class ThreadCard : MonoBehaviour
    {
        const float k_Width = 180f;
        const float k_Height = 112f;
        const float k_ThrowSpeed = 1.5f;
        const float k_TapDistance = 0.03f;
        const float k_TapSeconds = 0.6f;
        const float k_DropOnPanelDistance = 0.35f;
        const float k_ThrowLifetime = 1.2f;

        /// <summary>Raised when the user taps the card or drops it on the panel.</summary>
        public event Action<ThreadCard> OpenRequested;

        /// <summary>Raised once when the user throws the card away.</summary>
        public event Action<ThreadCard> Thrown;

        /// <summary>The thread this card shows.</summary>
        public ChatThread Thread { get; private set; }

        /// <summary>True once the user has moved the card; layout then leaves it where it was put.</summary>
        public bool UserPlaced { get; private set; }

        /// <summary>Dropping the card within reach of this transform opens it.</summary>
        public Transform DropTarget { get; set; }

        XRGrabInteractable m_Grab;
        Rigidbody m_Body;
        Image m_Background;
        TextMeshProUGUI m_Title;
        TextMeshProUGUI m_Preview;
        TextMeshProUGUI m_Meta;
        Vector3 m_GrabStart;
        float m_GrabTime;
        Vector3 m_LastPosition;
        Vector3 m_Velocity;
        bool m_PendingThrow;
        float m_ThrowTime = -1f;

        /// <summary>Creates a card for a thread.</summary>
        public static ThreadCard Create(Transform parent, ChatThread thread)
        {
            var go = new GameObject("ThreadCard");
            go.transform.SetParent(parent, false);

            // Collider before the interactable: XRGrabInteractable gathers colliders when it wakes.
            var collider = go.AddComponent<BoxCollider>();
            collider.size = new Vector3(k_Width, k_Height, 20f) * 0.001f;

            var card = go.AddComponent<ThreadCard>();
            card.m_Body = go.AddComponent<Rigidbody>();
            card.m_Body.isKinematic = true;
            card.m_Body.useGravity = false;

            card.m_Grab = go.AddComponent<XRGrabInteractable>();
            card.m_Grab.movementType = XRBaseInteractable.MovementType.Instantaneous;
            card.m_Grab.useDynamicAttach = true;
            card.m_Grab.throwOnDetach = false;
            card.m_Grab.selectEntered.AddListener(card.OnGrabbed);
            card.m_Grab.selectExited.AddListener(card.OnReleased);

            card.Build();
            card.SetThread(thread);
            return card;
        }

        /// <summary>Updates the card's text from its thread.</summary>
        public void SetThread(ChatThread thread)
        {
            Thread = thread;
            m_Title.text = MarkdownToTmp.Escape(thread.title);
            m_Preview.text = MarkdownToTmp.Escape(ChatThread.MakeTitle(thread.LastMessageText));
            var updated = DateTimeOffset.FromUnixTimeMilliseconds(thread.updatedUnixMs).ToLocalTime();
            m_Meta.text = $"{thread.messages.Count} messages · {updated.ToString("MMM d, HH:mm", CultureInfo.InvariantCulture)}";
        }

        /// <summary>Marks the card of the thread shown in the panel.</summary>
        public void SetCurrent(bool current)
        {
            m_Background.color = current ? Color.Lerp(UiFactory.CardColor, UiFactory.AccentColor, 0.45f) : UiFactory.CardColor;
        }

        void Build()
        {
            var panel = UiFactory.CreatePanel(transform, "Canvas", new Vector2(k_Width, k_Height));
            // Grabbed through its collider, not pointed at as UI.
            Destroy(panel.GetComponent<TrackedDeviceGraphicRaycaster>());
            m_Background = panel.Find("Background").GetComponent<Image>();
            m_Background.raycastTarget = false;

            m_Title = UiFactory.CreateText(panel, "Title", "", 19);
            m_Title.fontStyle = FontStyles.Bold;
            m_Title.overflowMode = TextOverflowModes.Ellipsis;
            UiFactory.Place(m_Title.rectTransform, 12, 10, k_Width - 24, 50);

            m_Preview = UiFactory.CreateText(panel, "Preview", "", 14);
            m_Preview.color = UiFactory.MutedTextColor;
            m_Preview.overflowMode = TextOverflowModes.Ellipsis;
            UiFactory.Place(m_Preview.rectTransform, 12, 60, k_Width - 24, 22);

            m_Meta = UiFactory.CreateText(panel, "Meta", "", 12);
            m_Meta.color = UiFactory.MutedTextColor;
            UiFactory.Place(m_Meta.rectTransform, 12, 86, k_Width - 24, 18);
        }

        void OnGrabbed(SelectEnterEventArgs args)
        {
            m_GrabStart = m_LastPosition = transform.position;
            m_GrabTime = Time.time;
            m_Velocity = Vector3.zero;
        }

        void OnReleased(SelectExitEventArgs args)
        {
            if (args.isCanceled)
                return;

            var moved = Vector3.Distance(transform.position, m_GrabStart);
            if (m_Velocity.magnitude > k_ThrowSpeed)
            {
                // Physics changes wait a frame: XRI restores the rigidbody while it finishes the release.
                m_PendingThrow = true;
                return;
            }

            if (moved < k_TapDistance && Time.time - m_GrabTime < k_TapSeconds)
            {
                OpenRequested?.Invoke(this);
                return;
            }

            UserPlaced = true;
            if (DropTarget != null && Vector3.Distance(transform.position, DropTarget.position) < k_DropOnPanelDistance)
                OpenRequested?.Invoke(this);
        }

        void Update()
        {
            if (m_Grab.isSelected)
            {
                var position = transform.position;
                if (Time.deltaTime > 0f)
                    m_Velocity = Vector3.Lerp(m_Velocity, (position - m_LastPosition) / Time.deltaTime, 0.5f);
                m_LastPosition = position;
            }

            if (m_PendingThrow)
            {
                m_PendingThrow = false;
                m_Grab.enabled = false;
                m_Body.isKinematic = false;
                m_Body.useGravity = true;
                m_Body.linearVelocity = m_Velocity;
                m_Body.angularVelocity = UnityEngine.Random.insideUnitSphere * 6f;
                m_ThrowTime = Time.time;
                Thrown?.Invoke(this);
            }

            if (m_ThrowTime >= 0f)
            {
                var t = (Time.time - m_ThrowTime) / k_ThrowLifetime;
                transform.localScale = Vector3.one * Mathf.Max(0f, 1f - t);
                if (t >= 1f)
                    Destroy(gameObject);
            }
        }
    }
}
