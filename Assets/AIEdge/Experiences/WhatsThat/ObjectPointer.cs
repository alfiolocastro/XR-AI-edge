using System;
using System.Collections.Generic;
using AIEdge.Spatial;
using AIEdge.UI;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.XR.Hands;

namespace AIEdge.WhatsThat
{
    /// <summary>Where the user pointed, found by the depth sensor.</summary>
    public struct PointerTarget
    {
        /// <summary>The pointing ray in world space.</summary>
        public Ray Ray;

        /// <summary>The real-world surface point the ray hit.</summary>
        public Vector3 Point;

        /// <summary>Surface normal at <see cref="Point"/>.</summary>
        public Vector3 Normal;

        /// <summary>"hand" or "controller".</summary>
        public string Source;
    }

    /// <summary>
    /// Points at real objects with a hand or a controller and finds them with the depth sensor. Shows a ray, a reticle
    /// on the real surface, and the live distance from the user's eyes.
    /// <list type="bullet">
    /// <item>Hand: point with the index finger (other fingers curled) and hold still for a moment.</item>
    /// <item>Controller: aim and pull the trigger.</item>
    /// </list>
    /// </summary>
    public class ObjectPointer : MonoBehaviour
    {
        const float k_MaxDistance = 6f;
        const float k_DwellSeconds = 1.0f;
        const float k_SteadyFraction = 0.04f;   // reticle may wander 4% of the distance while dwelling
        const float k_SteadyMin = 0.03f;
        const float k_RearmFraction = 0.12f;    // move this far before the same hand can select again
        const float k_DirectionSmoothing = 12f; // per second; steadies the finger ray
        const float k_ReticleSize = 0.035f;     // at 1 m, scaled with distance

        static readonly Color k_RayColor = new Color(0.55f, 0.75f, 1f, 0.55f);
        static readonly Color k_ReticleColor = new Color(0.55f, 0.75f, 1f, 1f);
        static readonly Color k_FillColor = new Color(0.35f, 0.9f, 0.55f, 0.9f);

        [SerializeField, Tooltip("Room depth. Uses DepthService.Instance if empty.")]
        DepthService m_Depth;

        /// <summary>Raised when the user selects a pointed spot.</summary>
        public event Action<PointerTarget> Selected;

        /// <summary>True while a hand or controller is pointing at a real surface.</summary>
        public bool HasTarget { get; private set; }

        /// <summary>The spot pointed at now (valid when <see cref="HasTarget"/>).</summary>
        public PointerTarget Current { get; private set; }

        /// <summary>When false, the pointer shows but doesn't select (for example while an answer is coming).</summary>
        public bool CanSelect { get; set; } = true;

        readonly List<XRHandSubsystem> m_Subsystems = new List<XRHandSubsystem>();
        XRHandSubsystem m_Hands;
        readonly Vector3[] m_SmoothedDirection = new Vector3[2];

        InputAction m_RightPosition, m_RightRotation, m_RightTrigger, m_RightTracked;
        InputAction m_LeftPosition, m_LeftRotation, m_LeftTrigger, m_LeftTracked;

        LineRenderer m_Line;
        RectTransform m_Reticle;
        RectTransform m_Fill;
        TextMeshProUGUI m_DistanceLabel;

        float m_DwellTime;
        Vector3 m_DwellAnchor;
        bool m_Armed = true;
        Vector3 m_LastSelected;

        void Awake()
        {
            m_RightPosition = new InputAction("RightPointerPosition", binding: "<XRController>{RightHand}/pointerPosition");
            m_RightRotation = new InputAction("RightPointerRotation", binding: "<XRController>{RightHand}/pointerRotation");
            m_RightTrigger = new InputAction("RightTrigger", InputActionType.Button, "<XRController>{RightHand}/{TriggerButton}");
            m_RightTracked = new InputAction("RightTracked", InputActionType.Button, "<XRController>{RightHand}/isTracked");
            m_LeftPosition = new InputAction("LeftPointerPosition", binding: "<XRController>{LeftHand}/pointerPosition");
            m_LeftRotation = new InputAction("LeftPointerRotation", binding: "<XRController>{LeftHand}/pointerRotation");
            m_LeftTrigger = new InputAction("LeftTrigger", InputActionType.Button, "<XRController>{LeftHand}/{TriggerButton}");
            m_LeftTracked = new InputAction("LeftTracked", InputActionType.Button, "<XRController>{LeftHand}/isTracked");
            BuildVisuals();
        }

        void OnEnable()
        {
            foreach (var action in Actions())
                action.Enable();
            m_DwellTime = 0f;
            m_Armed = true;
        }

        void OnDisable()
        {
            foreach (var action in Actions())
                action.Disable();
            HasTarget = false;
            ShowVisuals(false);
        }

        void OnDestroy()
        {
            foreach (var action in Actions())
                action.Dispose();
            if (m_Reticle != null)
                Destroy(m_Reticle.gameObject);
        }

        IEnumerable<InputAction> Actions()
        {
            yield return m_RightPosition;
            yield return m_RightRotation;
            yield return m_RightTrigger;
            yield return m_RightTracked;
            yield return m_LeftPosition;
            yield return m_LeftRotation;
            yield return m_LeftTrigger;
            yield return m_LeftTracked;
        }

        void Update()
        {
            if (m_Depth == null)
                m_Depth = DepthService.Instance;
            if (m_Depth == null || !m_Depth.IsReady)
            {
                HasTarget = false;
                ShowVisuals(false);
                return;
            }

            // Controllers first (a held controller means hands aren't tracked), then a pointing hand.
            var found = TryController(m_RightPosition, m_RightRotation, m_RightTracked, m_RightTrigger, out var ray, out var triggered) ||
                        TryController(m_LeftPosition, m_LeftRotation, m_LeftTracked, m_LeftTrigger, out ray, out triggered);
            var source = "controller";
            if (!found)
            {
                source = "hand";
                triggered = false;
                found = TryHand(out ray);
            }

            if (!found || !m_Depth.Raycast(ray, out var hit, k_MaxDistance))
            {
                HasTarget = false;
                m_DwellTime = 0f;
                m_Armed = true;
                ShowVisuals(found, ray, ray.origin + ray.direction * 0.6f, Vector3.zero, 0f);
                return;
            }

            HasTarget = true;
            Current = new PointerTarget { Ray = ray, Point = hit.point, Normal = hit.normal, Source = source };
            var eye = Camera.main != null ? Camera.main.transform.position : ray.origin;
            var distance = Vector3.Distance(eye, hit.point);

            var progress = 0f;
            if (source == "controller")
            {
                if (triggered && CanSelect)
                    Select();
            }
            else
            {
                progress = UpdateDwell(hit.point, distance);
            }

            ShowVisuals(true, ray, hit.point, hit.normal, progress, distance);
        }

        // Hand selection: hold the reticle steady for a moment. After a selection, the hand must move away (or stop
        // pointing) before it can select again, so one gesture asks once.
        float UpdateDwell(Vector3 point, float distance)
        {
            if (!m_Armed)
            {
                if (Vector3.Distance(point, m_LastSelected) > Mathf.Max(k_SteadyMin, k_RearmFraction * distance))
                    m_Armed = true;
                return 0f;
            }

            if (!CanSelect)
            {
                m_DwellTime = 0f;
                return 0f;
            }

            if (m_DwellTime <= 0f || Vector3.Distance(point, m_DwellAnchor) > Mathf.Max(k_SteadyMin, k_SteadyFraction * distance))
            {
                m_DwellAnchor = point;
                m_DwellTime = Time.deltaTime;
                return 0f;
            }

            m_DwellTime += Time.deltaTime;
            if (m_DwellTime < k_DwellSeconds)
                return m_DwellTime / k_DwellSeconds;

            m_DwellTime = 0f;
            Select();
            return 1f;
        }

        void Select()
        {
            m_Armed = false;
            m_LastSelected = Current.Point;
            Selected?.Invoke(Current);
        }

        bool TryController(InputAction position, InputAction rotation, InputAction tracked, InputAction trigger, out Ray ray, out bool triggered)
        {
            ray = default;
            triggered = false;
            if (!tracked.IsPressed() || position.activeControl == null)
                return false;

            var space = TrackingSpace();
            var origin = position.ReadValue<Vector3>();
            var direction = rotation.ReadValue<Quaternion>() * Vector3.forward;
            if (space != null)
            {
                origin = space.TransformPoint(origin);
                direction = space.TransformDirection(direction);
            }

            ray = new Ray(origin, direction);
            triggered = trigger.WasPressedThisFrame();
            return true;
        }

        bool TryHand(out Ray ray)
        {
            ray = default;
            if (m_Hands == null || !m_Hands.running)
            {
                m_Subsystems.Clear();
                SubsystemManager.GetSubsystems(m_Subsystems);
                m_Hands = m_Subsystems.Find(s => s.running);
            }

            if (m_Hands == null)
                return false;
            return TryPointingHand(m_Hands.rightHand, 0, out ray) || TryPointingHand(m_Hands.leftHand, 1, out ray);
        }

        // A pointing pose: index finger out, middle finger curled. The ray runs from the index knuckle through the
        // fingertip, smoothed so small tremors don't shake the reticle at a distance.
        bool TryPointingHand(XRHand hand, int index, out Ray ray)
        {
            ray = default;
            if (!hand.isTracked ||
                !hand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out var tip) ||
                !hand.GetJoint(XRHandJointID.IndexProximal).TryGetPose(out var knuckle) ||
                !hand.GetJoint(XRHandJointID.MiddleTip).TryGetPose(out var middle) ||
                !hand.GetJoint(XRHandJointID.Palm).TryGetPose(out var palm))
                return false;

            if (!PointerMath.IsPointingPose(tip.position, middle.position, palm.position))
            {
                m_SmoothedDirection[index] = Vector3.zero;
                return false;
            }

            var space = TrackingSpace();
            var origin = space != null ? space.TransformPoint(tip.position) : tip.position;
            var back = space != null ? space.TransformPoint(knuckle.position) : knuckle.position;
            var direction = (origin - back).normalized;
            var smoothed = m_SmoothedDirection[index];
            smoothed = smoothed == Vector3.zero
                ? direction
                : Vector3.Slerp(smoothed, direction, 1f - Mathf.Exp(-k_DirectionSmoothing * Time.deltaTime));
            m_SmoothedDirection[index] = smoothed;
            ray = new Ray(origin, smoothed);
            return true;
        }

        static Transform TrackingSpace() => Camera.main != null ? Camera.main.transform.parent : null;

        // ---- Visuals: a ray, a reticle with a dwell fill, and the live distance ----------------------------------

        void BuildVisuals()
        {
            m_Line = gameObject.AddComponent<LineRenderer>();
            // The UI shader is always in the build: an unlit, vertex-colored material that works for lines too.
            m_Line.sharedMaterial = Canvas.GetDefaultCanvasMaterial();
            m_Line.positionCount = 2;
            m_Line.startWidth = 0.004f;
            m_Line.endWidth = 0.002f;
            m_Line.startColor = k_RayColor;
            m_Line.endColor = new Color(k_RayColor.r, k_RayColor.g, k_RayColor.b, 0.15f);
            m_Line.useWorldSpace = true;
            m_Line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            m_Line.receiveShadows = false;

            var canvas = new GameObject("PointerReticle", typeof(RectTransform), typeof(Canvas));
            canvas.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            m_Reticle = (RectTransform)canvas.transform;
            m_Reticle.sizeDelta = new Vector2(100f, 100f);

            // A square ring rotated 45° (a diamond), filled from the center while the hand dwells.
            var ring = new GameObject("Ring", typeof(RectTransform)).transform;
            ring.SetParent(m_Reticle, false);
            ring.localRotation = Quaternion.Euler(0f, 0f, 45f);
            const float side = 70f;
            const float stroke = 9f;
            AddBar(ring, new Vector2(0f, side / 2f), new Vector2(side + stroke, stroke));
            AddBar(ring, new Vector2(0f, -side / 2f), new Vector2(side + stroke, stroke));
            AddBar(ring, new Vector2(side / 2f, 0f), new Vector2(stroke, side + stroke));
            AddBar(ring, new Vector2(-side / 2f, 0f), new Vector2(stroke, side + stroke));
            var fill = UiFactory.CreateImage(ring, "Fill", k_FillColor);
            m_Fill = fill.rectTransform;
            m_Fill.sizeDelta = new Vector2(side - stroke, side - stroke);

            m_DistanceLabel = UiFactory.CreateText(m_Reticle, "Distance", "", 34, TextAlignmentOptions.Center);
            m_DistanceLabel.fontStyle = FontStyles.Bold;
            m_DistanceLabel.color = Color.white;
            m_DistanceLabel.outlineWidth = 0.25f;
            m_DistanceLabel.outlineColor = new Color32(0, 0, 0, 200);
            var label = m_DistanceLabel.rectTransform;
            label.sizeDelta = new Vector2(240f, 50f);
            label.anchoredPosition = new Vector2(0f, -78f);
            ShowVisuals(false);
        }

        static void AddBar(Transform parent, Vector2 position, Vector2 size)
        {
            var bar = UiFactory.CreateImage(parent, "Bar", k_ReticleColor).rectTransform;
            bar.anchoredPosition = position;
            bar.sizeDelta = size;
        }

        void ShowVisuals(bool visible) => ShowVisuals(visible, default, Vector3.zero, Vector3.zero, 0f);

        void ShowVisuals(bool visible, Ray ray, Vector3 end, Vector3 normal, float progress, float distance = -1f)
        {
            m_Line.enabled = visible;
            var hasHit = visible && normal != Vector3.zero;
            if (m_Reticle != null)
                m_Reticle.gameObject.SetActive(hasHit);
            if (!visible)
                return;

            m_Line.SetPosition(0, ray.origin);
            m_Line.SetPosition(1, end);
            if (!hasHit)
                return;

            // Lie on the surface, nudged toward the user, and keep the same apparent size at any distance.
            var eye = Camera.main != null ? Camera.main.transform.position : ray.origin;
            var toEye = (eye - end).normalized;
            var facing = Vector3.Dot(normal, toEye) > 0.2f ? normal : toEye;
            m_Reticle.position = end + facing * 0.01f;
            m_Reticle.rotation = Quaternion.LookRotation(-facing, Mathf.Abs(Vector3.Dot(facing, Vector3.up)) > 0.9f ? Camera.main.transform.forward : Vector3.up);
            m_Reticle.localScale = Vector3.one * (k_ReticleSize * Mathf.Max(0.4f, distance) / 100f);
            m_Fill.localScale = Vector3.one * Mathf.Clamp01(progress);
            m_DistanceLabel.text = distance >= 0f ? DepthRegion.FormatLength(distance) : "";
        }
    }
}
