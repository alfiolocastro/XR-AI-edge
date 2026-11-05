using System;
using System.Collections.Generic;
using AIEdge.UI;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace AIEdge.AskImage
{
    /// <summary>
    /// A rectangle the user framed, facing them.
    /// </summary>
    public struct FramedRegion
    {
        /// <summary>Corners: bottom-left, top-left, top-right, bottom-right.</summary>
        public Vector3[] Corners;
        public Vector3 Center;
        public Quaternion Rotation;
        public Vector2 Size;

        /// <summary>Builds a rectangle facing <paramref name="eye"/> with <paramref name="a"/> and <paramref name="b"/> on its diagonal.</summary>
        public static FramedRegion FromDiagonal(Vector3 eye, Vector3 a, Vector3 b)
        {
            var center = (a + b) / 2f;
            var forward = center - eye;
            forward = forward.sqrMagnitude > 1e-6f ? forward.normalized : Vector3.forward;
            var right = Vector3.Cross(Vector3.up, forward);
            right = right.sqrMagnitude > 1e-6f ? right.normalized : Vector3.right;
            var up = Vector3.Cross(forward, right);
            var diagonal = b - a;
            var size = new Vector2(Mathf.Abs(Vector3.Dot(diagonal, right)), Mathf.Abs(Vector3.Dot(diagonal, up)));
            return Create(center, Quaternion.LookRotation(forward, up), size);
        }

        /// <summary>A rectangle straight ahead of a head pose, level with the horizon.</summary>
        public static FramedRegion InFront(Pose head, float distance, Vector2 size)
        {
            var forward = Vector3.ProjectOnPlane(head.rotation * Vector3.forward, Vector3.up);
            forward = forward.sqrMagnitude > 1e-6f ? forward.normalized : Vector3.forward;
            return Create(head.position + forward * distance, Quaternion.LookRotation(forward, Vector3.up), size);
        }

        static FramedRegion Create(Vector3 center, Quaternion rotation, Vector2 size)
        {
            var right = rotation * Vector3.right * (size.x / 2f);
            var up = rotation * Vector3.up * (size.y / 2f);
            return new FramedRegion
            {
                Center = center,
                Rotation = rotation,
                Size = size,
                Corners = new[] { center - right - up, center - right + up, center + right + up, center + right - up },
            };
        }
    }

    /// <summary>
    /// Two-hand framing: pinch with both hands, pull them apart to draw a rectangle over something in the room, and
    /// let go to take the shot. Uses XR Hands joints and draws the rectangle as it is framed.
    /// </summary>
    public class FrameGesture : MonoBehaviour
    {
        const float k_PinchOn = 0.02f;
        const float k_PinchOff = 0.035f;
        const float k_StartWindow = 0.6f;
        const float k_MinStartSeparation = 0.06f;
        const float k_MinSide = 0.05f;
        const float k_Edge = 4f;

        /// <summary>Raised when the user releases a frame big enough to use.</summary>
        public event Action<FramedRegion> Framed;

        /// <summary>True while both hands are drawing a frame.</summary>
        public bool IsFraming { get; private set; }

        readonly List<XRHandSubsystem> m_Subsystems = new List<XRHandSubsystem>();
        readonly bool[] m_Pinching = new bool[2];
        readonly float[] m_PinchStart = new float[2];
        readonly Vector3[] m_PinchPoint = new Vector3[2];
        XRHandSubsystem m_Hands;
        RectTransform m_Outline;
        FramedRegion m_Region;

        void OnDisable()
        {
            IsFraming = false;
            m_Pinching[0] = m_Pinching[1] = false;
            if (m_Outline != null)
                m_Outline.gameObject.SetActive(false);
        }

        void Update()
        {
            if (m_Hands == null || !m_Hands.running)
            {
                m_Subsystems.Clear();
                SubsystemManager.GetSubsystems(m_Subsystems);
                m_Hands = m_Subsystems.Find(s => s.running);
                if (m_Hands == null)
                    return;
            }

            UpdatePinch(0, m_Hands.leftHand);
            UpdatePinch(1, m_Hands.rightHand);

            var head = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
            var both = m_Pinching[0] && m_Pinching[1];
            if (!IsFraming)
            {
                IsFraming = both && Mathf.Abs(m_PinchStart[0] - m_PinchStart[1]) < k_StartWindow &&
                    Vector3.Distance(m_PinchPoint[0], m_PinchPoint[1]) > k_MinStartSeparation;
                if (!IsFraming)
                    return;
            }

            if (both)
            {
                m_Region = FramedRegion.FromDiagonal(head, m_PinchPoint[0], m_PinchPoint[1]);
                ShowOutline(m_Region);
                return;
            }

            IsFraming = false;
            m_Outline.gameObject.SetActive(false);
            if (m_Region.Size.x >= k_MinSide && m_Region.Size.y >= k_MinSide)
            {
                Debug.Log($"[AIEdge] Framed {m_Region.Size.x * 100f:0}x{m_Region.Size.y * 100f:0} cm at {m_Region.Center}");
                Framed?.Invoke(m_Region);
            }
        }

        void UpdatePinch(int index, XRHand hand)
        {
            if (!hand.isTracked ||
                !hand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out var thumb) ||
                !hand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out var indexTip))
            {
                m_Pinching[index] = false;
                return;
            }

            var gap = Vector3.Distance(thumb.position, indexTip.position);
            var was = m_Pinching[index];
            m_Pinching[index] = was ? gap < k_PinchOff : gap < k_PinchOn;
            if (m_Pinching[index] && !was)
                m_PinchStart[index] = Time.time;

            // Joint poses are in tracking space, like the camera's local pose under the XR Origin.
            var point = (thumb.position + indexTip.position) / 2f;
            var trackingSpace = Camera.main != null ? Camera.main.transform.parent : null;
            m_PinchPoint[index] = trackingSpace != null ? trackingSpace.TransformPoint(point) : point;
        }

        void ShowOutline(FramedRegion region)
        {
            if (m_Outline == null)
                BuildOutline();

            m_Outline.gameObject.SetActive(true);
            m_Outline.SetPositionAndRotation(region.Center, region.Rotation);
            m_Outline.sizeDelta = region.Size * 1000f;
        }

        void BuildOutline()
        {
            m_Outline = UiFactory.CreatePanel(transform, "FrameOutline", Vector2.one * 100f);
            Destroy(m_Outline.GetComponent<TrackedDeviceGraphicRaycaster>());
            var background = m_Outline.Find("Background").GetComponent<Image>();
            background.color = new Color(1f, 1f, 1f, 0.06f);
            background.raycastTarget = false;

            void Edge(Vector2 anchorMin, Vector2 anchorMax, Vector2 size)
            {
                var edge = UiFactory.CreateImage(m_Outline, "Edge", Color.white);
                edge.raycastTarget = false;
                edge.rectTransform.anchorMin = anchorMin;
                edge.rectTransform.anchorMax = anchorMax;
                edge.rectTransform.sizeDelta = size;
                edge.rectTransform.anchoredPosition = Vector2.zero;
            }

            Edge(new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, k_Edge));
            Edge(new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, k_Edge));
            Edge(new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(k_Edge, 0f));
            Edge(new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(k_Edge, 0f));

            var hint = UiFactory.CreateText(m_Outline, "Hint", "Let go to ask", 18, TMPro.TextAlignmentOptions.Center);
            hint.rectTransform.anchorMin = hint.rectTransform.anchorMax = new Vector2(0.5f, 0f);
            hint.rectTransform.pivot = new Vector2(0.5f, 1f);
            hint.rectTransform.sizeDelta = new Vector2(200f, 30f);
            hint.rectTransform.anchoredPosition = new Vector2(0f, -8f);
        }
    }
}
