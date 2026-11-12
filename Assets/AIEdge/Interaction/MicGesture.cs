using System.Collections.Generic;
using AIEdge.UI;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Hands;

namespace AIEdge.Interaction
{
    /// <summary>
    /// Voice input by gesture: hold a closed fist (hand tracking) or a controller to your mouth as if it were a
    /// microphone, and the open panel's Talk button starts listening; move it away and what you said is transcribed
    /// and used. Works in Chat, Point &amp; Ask and Tabletop Garden. <see cref="MicFeedback"/> signals the start and
    /// the end with a sound and a microphone symbol.
    /// </summary>
    public class MicGesture : MonoBehaviour
    {
        const string k_EnabledKey = "AIEdge.MicGesture";

        /// <summary>The gesture in the scene, or null.</summary>
        public static MicGesture Instance { get; private set; }

        /// <summary>Whether the gesture is on. Remembered between sessions.</summary>
        public static bool Enabled
        {
            get => PlayerPrefs.GetInt(k_EnabledKey, 1) == 1;
            set
            {
                PlayerPrefs.SetInt(k_EnabledKey, value ? 1 : 0);
                PlayerPrefs.Save();
            }
        }

        /// <summary>What is held to the mouth right now: "fist", "controller", or null.</summary>
        public string Source { get; private set; }

        /// <summary>Distance from the mic head to the mouth, in meters (infinity when nothing is tracked).</summary>
        public float Distance { get; private set; } = float.PositiveInfinity;

        /// <summary>True while the gesture holds listening on.</summary>
        public bool IsActive => m_Tracker.IsActive;

        readonly MicGestureTracker m_Tracker = new MicGestureTracker();
        readonly List<XRHandSubsystem> m_Subsystems = new List<XRHandSubsystem>();
        XRHandSubsystem m_Hands;
        InputAction m_RightPosition;
        InputAction m_RightTracked;
        InputAction m_LeftPosition;
        InputAction m_LeftTracked;
        MicButton m_Target;
        bool m_Simulated;
        bool m_SimulatedQualified;
        float m_SimulatedDistance;

        void Awake()
        {
            Instance = this;
            // The grip position: the middle of the controller, where a microphone's head would be.
            m_RightPosition = new InputAction("MicRightPosition", binding: "<XRController>{RightHand}/devicePosition");
            m_RightTracked = new InputAction("MicRightTracked", InputActionType.Button, "<XRController>{RightHand}/isTracked");
            m_LeftPosition = new InputAction("MicLeftPosition", binding: "<XRController>{LeftHand}/devicePosition");
            m_LeftTracked = new InputAction("MicLeftTracked", InputActionType.Button, "<XRController>{LeftHand}/isTracked");
        }

        void OnEnable()
        {
            foreach (var action in Actions())
                action.Enable();
            m_Tracker.Reset();
        }

        void OnDisable()
        {
            foreach (var action in Actions())
                action.Disable();
            if (m_Tracker.IsActive)
                m_Target?.EndFromGesture();
            m_Tracker.Reset();
        }

        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
            foreach (var action in Actions())
                action.Dispose();
        }

        IEnumerable<InputAction> Actions()
        {
            yield return m_RightPosition;
            yield return m_RightTracked;
            yield return m_LeftPosition;
            yield return m_LeftTracked;
        }

        /// <summary>
        /// Replaces the tracked hands and controllers with a fixed reading (for self-tests); null returns to tracking.
        /// </summary>
        public void Simulate(bool? qualified, float distance = float.PositiveInfinity)
        {
            m_Simulated = qualified.HasValue;
            m_SimulatedQualified = qualified ?? false;
            m_SimulatedDistance = distance;
        }

        void Update()
        {
            var head = Camera.main != null ? Camera.main.transform : null;
            if (!Enabled || head == null)
            {
                if (m_Tracker.IsActive)
                    EndListening();
                m_Tracker.Reset();
                return;
            }

            bool qualified;
            float distance;
            if (m_Simulated)
            {
                qualified = m_SimulatedQualified;
                distance = m_SimulatedDistance;
                Source = qualified ? "simulated" : null;
            }
            else
            {
                Read(MicGestureMath.Mouth(head.position, head.rotation), out qualified, out distance);
            }

            Distance = distance;
            switch (m_Tracker.Update(qualified, distance, Time.deltaTime))
            {
                case MicGestureEvent.Start:
                    BeginListening();
                    break;
                case MicGestureEvent.Stop:
                    EndListening();
                    break;
            }

            // The panel closed (or its Talk button stopped by itself at the length limit) while the mic was held up.
            if (m_Tracker.IsActive && m_Target != null && !m_Target.IsListening && !m_Target.IsBusy)
                m_Target = null;
        }

        void BeginListening()
        {
            m_Target = MicButton.Current;
            if (m_Target == null)
            {
                MicFeedback.ShowHint(AppLanguage.IsItalian
                    ? "Apri Chat, Point & Ask o Giardino per parlare."
                    : "Open Chat, Point & Ask or Garden to talk.");
                return;
            }

            m_Target.BeginFromGesture();
        }

        void EndListening()
        {
            m_Target?.EndFromGesture();
            m_Target = null;
        }

        // The closest candidate to the mouth: a tracked controller (a held controller means hands aren't tracked), else
        // a hand. A hand qualifies as a mic only while it is a closed fist, but its distance is kept while it loosens.
        void Read(Vector3 mouth, out bool qualified, out float distance)
        {
            qualified = false;
            distance = float.PositiveInfinity;
            Source = null;
            var space = Camera.main.transform.parent;

            foreach (var (position, tracked) in new[] { (m_RightPosition, m_RightTracked), (m_LeftPosition, m_LeftTracked) })
            {
                if (!tracked.IsPressed() || position.activeControl == null)
                    continue;
                var point = position.ReadValue<Vector3>();
                if (space != null)
                    point = space.TransformPoint(point);
                var d = Vector3.Distance(point, mouth);
                if (d < distance)
                {
                    distance = d;
                    qualified = true;
                    Source = "controller";
                }
            }

            if (Source != null)
                return;

            if (m_Hands == null || !m_Hands.running)
            {
                m_Subsystems.Clear();
                SubsystemManager.GetSubsystems(m_Subsystems);
                m_Hands = m_Subsystems.Find(s => s.running);
            }

            if (m_Hands == null)
                return;
            foreach (var hand in new[] { m_Hands.rightHand, m_Hands.leftHand })
            {
                if (!hand.isTracked ||
                    !hand.GetJoint(XRHandJointID.Palm).TryGetPose(out var palm) ||
                    !hand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out var index) ||
                    !hand.GetJoint(XRHandJointID.MiddleTip).TryGetPose(out var middle) ||
                    !hand.GetJoint(XRHandJointID.RingTip).TryGetPose(out var ring) ||
                    !hand.GetJoint(XRHandJointID.LittleTip).TryGetPose(out var little) ||
                    !hand.GetJoint(XRHandJointID.IndexProximal).TryGetPose(out var knuckle) ||
                    !hand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out var thumb))
                    continue;

                var head = MicGestureMath.FistHead(knuckle.position, thumb.position);
                if (space != null)
                    head = space.TransformPoint(head);
                var d = Vector3.Distance(head, mouth);
                var fist = MicGestureMath.IsFist(palm.position, index.position, middle.position, ring.position, little.position);
                // Prefer a fist; otherwise keep the nearest hand's distance so a loosening grip doesn't read as "gone".
                if (fist && (!qualified || d < distance) || !qualified && d < distance)
                {
                    distance = d;
                    qualified = fist;
                    Source = fist ? "fist" : "hand";
                }
            }
        }
    }
}
