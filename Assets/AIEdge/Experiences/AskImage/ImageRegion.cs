using System.Collections.Generic;
using UnityEngine;

namespace AIEdge.AskImage
{
    /// <summary>
    /// Maps a region the user framed in the world to a pixel rectangle of the passthrough camera image.
    /// </summary>
    public static class ImageRegion
    {
        /// <summary>
        /// Pushes framed corners out along the user's eye rays to at least <paramref name="minDistance"/>. The camera
        /// sits a few centimeters from the eye, so projecting corners held at arm's length would shift the crop; points
        /// farther away line up with what the user saw through the frame.
        /// </summary>
        public static Vector3[] ExtendFromEye(Vector3 eye, IReadOnlyList<Vector3> corners, float minDistance)
        {
            var result = new Vector3[corners.Count];
            for (var i = 0; i < corners.Count; i++)
            {
                var offset = corners[i] - eye;
                var distance = offset.magnitude;
                result[i] = distance >= minDistance || distance < 1e-4f ? corners[i] : eye + offset / distance * minDistance;
            }

            return result;
        }

        /// <summary>True if every point is in front of the camera.</summary>
        public static bool AllInFront(Pose camera, IReadOnlyList<Vector3> points)
        {
            var forward = camera.rotation * Vector3.forward;
            foreach (var point in points)
                if (Vector3.Dot(point - camera.position, forward) <= 0.01f)
                    return false;
            return true;
        }

        /// <summary>Bounding box of viewport points, clamped to the image (0..1). Empty if it misses the image.</summary>
        public static Rect ViewportBounds(IReadOnlyList<Vector2> points)
        {
            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            foreach (var point in points)
            {
                min = Vector2.Min(min, point);
                max = Vector2.Max(max, point);
            }

            min = Vector2.Max(min, Vector2.zero);
            max = Vector2.Min(max, Vector2.one);
            return max.x > min.x && max.y > min.y ? Rect.MinMaxRect(min.x, min.y, max.x, max.y) : Rect.zero;
        }

        /// <summary>
        /// Converts a viewport rectangle to pixels, padded by <paramref name="padFraction"/> of its size on each side,
        /// grown to at least <paramref name="minSize"/> pixels around its center, and kept inside the image.
        /// </summary>
        public static RectInt ToPixels(Rect viewport, Vector2Int resolution, float padFraction, int minSize)
        {
            var size = new Vector2(viewport.width * resolution.x, viewport.height * resolution.y) * (1f + 2f * padFraction);
            size = Vector2.Max(size, new Vector2(minSize, minSize));
            size = Vector2.Min(size, resolution);
            var center = new Vector2(viewport.center.x * resolution.x, viewport.center.y * resolution.y);

            var width = Mathf.RoundToInt(size.x);
            var height = Mathf.RoundToInt(size.y);
            var x = Mathf.Clamp(Mathf.RoundToInt(center.x - size.x / 2f), 0, resolution.x - width);
            var y = Mathf.Clamp(Mathf.RoundToInt(center.y - size.y / 2f), 0, resolution.y - height);
            return new RectInt(x, y, width, height);
        }
    }
}
