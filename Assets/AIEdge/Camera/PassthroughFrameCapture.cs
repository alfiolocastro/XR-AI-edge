using System.IO;
using Meta.XR;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Android;

namespace AIEdge
{
    /// <summary>
    /// Grabs still frames from the Quest passthrough RGB camera (MRUK <see cref="PassthroughCameraAccess"/>)
    /// and saves them as PNG for multimodal prompts.
    /// </summary>
    [RequireComponent(typeof(PassthroughCameraAccess))]
    public class PassthroughFrameCapture : MonoBehaviour
    {
        const string k_CameraPermission = "horizonos.permission.HEADSET_CAMERA";

        PassthroughCameraAccess m_CameraAccess;
        Texture2D m_Frame;

        /// <summary>True once the camera is streaming and a frame can be captured.</summary>
        public bool IsReady => m_CameraAccess != null && m_CameraAccess.IsPlaying;

        /// <summary>The camera stream resolution, valid once <see cref="IsReady"/>.</summary>
        public Vector2Int Resolution => m_CameraAccess.CurrentResolution;

        /// <summary>The underlying MRUK camera component.</summary>
        public PassthroughCameraAccess CameraAccess => m_CameraAccess;

        void Awake()
        {
            m_CameraAccess = GetComponent<PassthroughCameraAccess>();
        }

        int m_Users;

        /// <summary>
        /// Starts the camera stream for one user (a panel, person detection). Keep it off when not needed: it costs
        /// power and bandwidth. Each call needs a matching <see cref="StopStreaming"/>.
        /// </summary>
        public void StartStreaming()
        {
            m_Users++;
            m_CameraAccess.enabled = true;
        }

        /// <summary>Stops the camera stream once its last user is done.</summary>
        public void StopStreaming()
        {
            m_Users = Mathf.Max(0, m_Users - 1);
            if (m_Users == 0)
                m_CameraAccess.enabled = false;
        }

        void Start()
        {
            // PassthroughCameraAccess waits for the permission but doesn't ask for it.
            if (!Permission.HasUserAuthorizedPermission(k_CameraPermission))
                Permission.RequestUserPermission(k_CameraPermission);
        }

        /// <summary>Saves the latest camera frame as PNG.</summary>
        /// <param name="path">Destination file.</param>
        /// <returns>True if a frame was written.</returns>
        public bool CaptureToFile(string path)
        {
            if (!IsReady)
                return false;

            var size = m_CameraAccess.CurrentResolution;
            if (m_Frame == null || m_Frame.width != size.x || m_Frame.height != size.y)
            {
                if (m_Frame != null)
                    Destroy(m_Frame);
                m_Frame = new Texture2D(size.x, size.y, TextureFormat.RGBA32, false);
            }

            // Blocking GPU readback: fine for a one-shot capture, not per frame.
            m_Frame.SetPixelData(m_CameraAccess.GetColors(), 0);
            m_Frame.Apply(false);
            File.WriteAllBytes(path, m_Frame.EncodeToPNG());
            return true;
        }

        /// <summary>
        /// World pose of the camera when the latest frame was taken. MRUK reports it in tracking space; this applies
        /// the XR Origin's camera offset so it matches world positions such as hand joints.
        /// </summary>
        public Pose GetWorldCameraPose()
        {
            var pose = m_CameraAccess.GetCameraPose();
            var trackingSpace = Camera.main != null ? Camera.main.transform.parent : null;
            return trackingSpace != null ? new Pose(trackingSpace.TransformPoint(pose.position), trackingSpace.rotation * pose.rotation) : pose;
        }

        /// <summary>Copies a region of the latest frame into a new texture and saves it as PNG.</summary>
        /// <param name="region">Pixel rectangle, bottom-left origin like the viewport.</param>
        /// <param name="path">Destination file.</param>
        /// <returns>The cropped texture (caller destroys it), or null if no frame is available.</returns>
        public Texture2D CaptureCrop(RectInt region, string path)
        {
            if (!IsReady)
                return null;

            var size = m_CameraAccess.CurrentResolution;
            var colors = m_CameraAccess.GetColors();
            var crop = new Color32[region.width * region.height];
            for (var row = 0; row < region.height; row++)
                NativeArray<Color32>.Copy(colors, (region.y + row) * size.x + region.x, crop, row * region.width, region.width);

            var texture = new Texture2D(region.width, region.height, TextureFormat.RGBA32, false);
            texture.SetPixels32(crop);
            texture.Apply(false);
            File.WriteAllBytes(path, texture.EncodeToPNG());
            return texture;
        }

        void OnDestroy()
        {
            if (m_Frame != null)
                Destroy(m_Frame);
        }
    }
}
