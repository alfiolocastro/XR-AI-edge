using Meta.XR;
using UnityEngine;

namespace AIEdge.Spatial
{
    /// <summary>
    /// Placement on real geometry (PortPlan §4.3, §4.4): objects on free flat surfaces, panels pinned to walls and kept
    /// out of furniture, and depth grids for the smart crop.
    /// </summary>
    public static class DepthPlacement
    {
        static readonly float[] k_SurfacePitches = { 35f, 45f, 28f, 55f, 65f };
        const float k_MinUpNormal = 0.85f;
        const float k_WallMaxUpNormal = 0.25f;
        const float k_WallMinConfidence = 0.5f;
        const float k_WallGap = 0.04f;
        const float k_PushStep = 0.05f;
        const int k_MaxPushSteps = 5; // at most 25 cm closer
        const float k_MinPanelDistance = 0.65f;

        /// <summary>
        /// Looks down and ahead of the user for a flat, free surface (a table or the floor) that fits a footprint.
        /// The pose faces away from the user, so a planter's near edge faces them.
        /// </summary>
        /// <param name="footprint">Width (across) and depth (away from the user) in meters.</param>
        /// <param name="maxDistance">Furthest horizontal distance from the head, in meters.</param>
        /// <param name="report">What each ray found, for logs.</param>
        public static bool TryPlaceOnSurface(DepthService depth, Vector2 footprint, float maxDistance, out Pose pose, out string report)
        {
            pose = default;
            report = "";
            var head = Camera.main != null ? Camera.main.transform : null;
            if (depth == null || head == null || !depth.IsReady)
                return false;

            var forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            forward = forward.sqrMagnitude > 1e-4f ? forward.normalized : Vector3.forward;
            var right = Vector3.Cross(Vector3.up, forward);

            // Where the user looks first (they look at the table they want), then a sweep down from the horizon.
            var directions = new System.Collections.Generic.List<Vector3>();
            if (Vector3.Dot(head.forward, Vector3.down) > 0.25f)
                directions.Add(head.forward);
            foreach (var pitch in k_SurfacePitches)
                directions.Add(Quaternion.AngleAxis(pitch, right) * forward);

            var log = new System.Text.StringBuilder();
            foreach (var direction in directions)
            {
                var ray = new Ray(head.position, direction);
                var pitch = Vector3.Angle(direction, forward);
                // Box z follows the surface normal, box y follows "upwards" (away from the user on a table).
                if (!depth.PlaceBox(ray, new Vector3(footprint.x, footprint.y, 0.02f), forward, out var hit))
                {
                    depth.Raycast(ray, out var probe);
                    log.Append($"{pitch:0}°: {probe.status} n.y {probe.normal.y:0.00} {Vector3.Distance(head.position, probe.point):0.00} m, no fit; ");
                    continue;
                }

                var horizontal = Vector3.ProjectOnPlane(hit.point - head.position, Vector3.up).magnitude;
                if (hit.normal.y < k_MinUpNormal || horizontal > maxDistance)
                {
                    log.Append($"{pitch:0}°: fit but n.y {hit.normal.y:0.00}, {horizontal:0.00} m away; ");
                    continue;
                }

                report = log.Append($"{pitch:0}°: placed").ToString();
                pose = new Pose(hit.point, Quaternion.LookRotation(forward, Vector3.up));
                return true;
            }

            report = log.ToString();
            return false;
        }

        /// <summary>
        /// Pins a panel flat on the wall straight ahead, if one is within reading distance. The panel's front faces
        /// out of the wall.
        /// </summary>
        public static bool TrySnapToWall(DepthService depth, Transform panel, float minDistance, float maxDistance, float heightOffset)
        {
            var head = Camera.main != null ? Camera.main.transform : null;
            if (depth == null || head == null || !depth.IsReady)
                return false;

            var forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            forward = forward.sqrMagnitude > 1e-4f ? forward.normalized : Vector3.forward;
            var origin = head.position + Vector3.up * heightOffset;
            if (!depth.Raycast(new Ray(origin, forward), out var hit, maxDistance))
                return false;
            if (Mathf.Abs(hit.normal.y) > k_WallMaxUpNormal || hit.normalConfidence < k_WallMinConfidence)
                return false;
            if (Vector3.Distance(origin, hit.point) < minDistance)
                return false;

            var outward = Vector3.ProjectOnPlane(hit.normal, Vector3.up).normalized;
            panel.SetPositionAndRotation(hit.point + outward * k_WallGap, Quaternion.LookRotation(-outward, Vector3.up));
            return true;
        }

        /// <summary>
        /// Moves a panel toward the user until it no longer overlaps real geometry, so text never sinks into furniture.
        /// Returns the distance moved, in meters.
        /// </summary>
        /// <param name="sizeMeters">Panel width and height.</param>
        public static float PushOutOfGeometry(DepthService depth, Transform panel, Vector2 sizeMeters)
        {
            var head = Camera.main != null ? Camera.main.transform : null;
            if (depth == null || head == null || !depth.IsReady)
                return 0f;

            var halfExtents = new Vector3(sizeMeters.x / 2f, sizeMeters.y / 2f, 0.02f);
            var moved = 0f;
            for (var step = 0; step < k_MaxPushSteps && depth.CheckBox(panel.position, halfExtents, panel.rotation); step++)
            {
                var toHead = head.position - panel.position;
                if (toHead.magnitude - k_PushStep < k_MinPanelDistance)
                    break;
                panel.position += toHead.normalized * k_PushStep;
                moved += k_PushStep;
            }

            return moved;
        }

        /// <summary>
        /// Casts a grid of depth rays through a viewport rectangle of the passthrough camera image, for
        /// <see cref="DepthRegion.Analyze"/>. Indexed [x, y] with y = 0 at the bottom.
        /// </summary>
        public static DepthSample[,] SampleGrid(DepthService depth, PassthroughCameraAccess camera, Pose cameraPose, Rect viewport, int size)
        {
            var grid = new DepthSample[size, size];
            for (var x = 0; x < size; x++)
            for (var y = 0; y < size; y++)
            {
                var point = new Vector2(viewport.xMin + (x + 0.5f) / size * viewport.width, viewport.yMin + (y + 0.5f) / size * viewport.height);
                var ray = camera.ViewportPointToRay(point, cameraPose);
                if (depth.Raycast(ray, out var hit, 8f))
                    grid[x, y] = new DepthSample { hit = true, point = hit.point, normal = hit.normal };
            }

            return grid;
        }
    }
}
