using Meta.XR;
using UnityEngine;

namespace AIEdge.Spatial
{
    /// <summary>
    /// Live depth queries against the real room through MRUK's <see cref="EnvironmentRaycastManager"/> (OpenXR
    /// environment raycast, no scene scan needed). Works under the template's XR Origin: MRUK would otherwise look for
    /// an OVRCameraRig, so the tracking space is set to the camera's parent (the XR Origin camera offset).
    /// </summary>
    [RequireComponent(typeof(EnvironmentRaycastManager))]
    public class DepthService : MonoBehaviour
    {
        const float k_ProbeDistance = 10f;

        /// <summary>The service in the scene, or null.</summary>
        public static DepthService Instance { get; private set; }

        EnvironmentRaycastManager m_Raycaster;
        int m_ReadyFrame = -1;
        bool m_Ready;

        /// <summary>True if the headset supports environment raycasts.</summary>
        public bool IsSupported => EnvironmentRaycastManager.IsSupported;

        /// <summary>True once depth answers queries (it takes a few frames after start, and needs the scene permission).</summary>
        public bool IsReady
        {
            get
            {
                if (m_ReadyFrame == Time.frameCount)
                    return m_Ready;

                m_ReadyFrame = Time.frameCount;
                m_Ready = false;
                var head = Camera.main != null ? Camera.main.transform : null;
                if (head != null && IsSupported && m_Raycaster.isActiveAndEnabled)
                {
                    m_Raycaster.Raycast(new Ray(head.position, head.forward), out var hit, k_ProbeDistance);
                    m_Ready = hit.status != EnvironmentRaycastHitStatus.NotReady && hit.status != EnvironmentRaycastHitStatus.NotSupported;
                }

                return m_Ready;
            }
        }

        void Awake()
        {
            Instance = this;
            m_Raycaster = GetComponent<EnvironmentRaycastManager>();
            if (m_Raycaster.CustomTrackingSpace == null && Camera.main != null)
                m_Raycaster.CustomTrackingSpace = Camera.main.transform.parent;
        }

        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        /// <summary>Casts a ray against the room. True only for a visible hit.</summary>
        public bool Raycast(Ray ray, out EnvironmentRaycastHit hit, float maxDistance = k_ProbeDistance)
        {
            hit = default;
            return IsSupported && m_Raycaster.isActiveAndEnabled && m_Raycaster.Raycast(ray, out hit, maxDistance);
        }

        /// <summary>
        /// Finds a flat, free spot for a box along a ray. The box's local z follows the surface normal and its local y
        /// follows <paramref name="upwards"/>.
        /// </summary>
        public bool PlaceBox(Ray ray, Vector3 boxSize, Vector3 upwards, out EnvironmentRaycastHit hit)
        {
            hit = default;
            return IsSupported && m_Raycaster.isActiveAndEnabled && m_Raycaster.PlaceBox(ray, boxSize, upwards, out hit);
        }

        /// <summary>True if a box overlaps real geometry.</summary>
        public bool CheckBox(Vector3 center, Vector3 halfExtents, Quaternion orientation) =>
            IsSupported && m_Raycaster.isActiveAndEnabled && m_Raycaster.CheckBox(center, halfExtents, orientation);

        /// <summary>Waits until depth is ready or the timeout passes.</summary>
        public async Awaitable<bool> WaitUntilReadyAsync(float timeoutSec)
        {
            var deadline = Time.realtimeSinceStartup + timeoutSec;
            while (!IsReady && Time.realtimeSinceStartup < deadline)
                await Awaitable.NextFrameAsync();
            return IsReady;
        }
    }
}
