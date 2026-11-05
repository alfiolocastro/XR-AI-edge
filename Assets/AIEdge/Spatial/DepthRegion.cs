using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace AIEdge.Spatial
{
    /// <summary>One depth ray result in a sample grid.</summary>
    public struct DepthSample
    {
        public bool hit;
        public Vector3 point;
        public Vector3 normal;
    }

    /// <summary>The foreground object found in a depth grid, with its real size.</summary>
    public class DepthBlob
    {
        /// <summary>Tight viewport rectangle around the object (inside the sampled rectangle).</summary>
        public Rect Viewport;

        /// <summary>Width and height across the camera's view, in meters.</summary>
        public float Width;
        public float Height;

        /// <summary>Distance from the camera to the object's center, in meters.</summary>
        public float Distance;

        /// <summary>World point at the object's center.</summary>
        public Vector3 Center;

        /// <summary>True if the object fills nearly the whole sampled rectangle (a wall or backdrop, no tighter crop).</summary>
        public bool FillsFrame;

        /// <summary>True if the rays just below the object hit an upward-facing surface.</summary>
        public bool OnHorizontalSurface;

        public int Cells;
    }

    /// <summary>
    /// Depth-guided smart crop and measurements (PortPlan §4.1, §4.2): from a grid of depth hits over the framed
    /// region, grow the object under the center until depth jumps, then measure it in meters. No segmentation model.
    /// </summary>
    public static class DepthRegion
    {
        const float k_StepTolerance = 0.04f;
        const float k_StepToleranceRelative = 0.08f;
        const float k_BandTolerance = 0.15f;
        const float k_BandToleranceRelative = 0.2f;
        const float k_FillsFrameFraction = 0.85f;
        const float k_UpwardNormal = 0.8f;

        /// <summary>
        /// Finds the object under the grid center. <paramref name="grid"/> is indexed [x, y] with y = 0 at the
        /// bottom, matching viewport coordinates over <paramref name="viewport"/>. Returns null if nothing usable.
        /// </summary>
        public static DepthBlob Analyze(DepthSample[,] grid, Rect viewport, Pose camera)
        {
            var columns = grid.GetLength(0);
            var rows = grid.GetLength(1);
            var forward = camera.rotation * Vector3.forward;
            var right = camera.rotation * Vector3.right;
            var up = camera.rotation * Vector3.up;
            float Depth(int x, int y) => Vector3.Dot(grid[x, y].point - camera.position, forward);

            if (!FindSeed(grid, out var seed))
                return null;

            var seedDepth = Depth(seed.x, seed.y);
            var band = Mathf.Max(k_BandTolerance, k_BandToleranceRelative * seedDepth);
            var filled = new bool[columns, rows];
            var queue = new Queue<Vector2Int>();
            filled[seed.x, seed.y] = true;
            queue.Enqueue(seed);
            var count = 0;
            var min = seed;
            var max = seed;
            while (queue.Count > 0)
            {
                var cell = queue.Dequeue();
                count++;
                min = Vector2Int.Min(min, cell);
                max = Vector2Int.Max(max, cell);
                var depth = Depth(cell.x, cell.y);
                foreach (var offset in new[] { Vector2Int.left, Vector2Int.right, Vector2Int.up, Vector2Int.down })
                {
                    var next = cell + offset;
                    if (next.x < 0 || next.y < 0 || next.x >= columns || next.y >= rows || filled[next.x, next.y] || !grid[next.x, next.y].hit)
                        continue;
                    var nextDepth = Depth(next.x, next.y);
                    if (Mathf.Abs(nextDepth - depth) > Mathf.Max(k_StepTolerance, k_StepToleranceRelative * depth) ||
                        Mathf.Abs(nextDepth - seedDepth) > band)
                        continue;
                    filled[next.x, next.y] = true;
                    queue.Enqueue(next);
                }
            }

            // Size across the view: spread of the filled hits plus one sample spacing (each sample covers a cell).
            float minRight = float.MaxValue, maxRight = float.MinValue, minUp = float.MaxValue, maxUp = float.MinValue;
            float spacingSum = 0f;
            var spacingCount = 0;
            var centerSum = Vector3.zero;
            for (var x = 0; x < columns; x++)
            for (var y = 0; y < rows; y++)
            {
                if (!filled[x, y])
                    continue;
                var point = grid[x, y].point;
                centerSum += point;
                var alongRight = Vector3.Dot(point, right);
                var alongUp = Vector3.Dot(point, up);
                minRight = Mathf.Min(minRight, alongRight);
                maxRight = Mathf.Max(maxRight, alongRight);
                minUp = Mathf.Min(minUp, alongUp);
                maxUp = Mathf.Max(maxUp, alongUp);
                if (x + 1 < columns && filled[x + 1, y])
                {
                    spacingSum += Vector3.Distance(point, grid[x + 1, y].point);
                    spacingCount++;
                }
                if (y + 1 < rows && filled[x, y + 1])
                {
                    spacingSum += Vector3.Distance(point, grid[x, y + 1].point);
                    spacingCount++;
                }
            }

            var spacing = spacingCount > 0 ? spacingSum / spacingCount : CellSpacing(viewport, columns, seedDepth);
            var center = centerSum / count;
            var cellWidth = viewport.width / columns;
            var cellHeight = viewport.height / rows;
            return new DepthBlob
            {
                Cells = count,
                FillsFrame = count > k_FillsFrameFraction * columns * rows,
                Viewport = Rect.MinMaxRect(
                    viewport.xMin + min.x * cellWidth, viewport.yMin + min.y * cellHeight,
                    viewport.xMin + (max.x + 1) * cellWidth, viewport.yMin + (max.y + 1) * cellHeight),
                Width = maxRight - minRight + spacing,
                Height = maxUp - minUp + spacing,
                Center = center,
                Distance = Vector3.Distance(camera.position, center),
                OnHorizontalSurface = RestsOnHorizontalSurface(grid, filled, min, max),
            };
        }

        // The center cell, or the nearest hit around it.
        static bool FindSeed(DepthSample[,] grid, out Vector2Int seed)
        {
            var center = new Vector2Int(grid.GetLength(0) / 2, grid.GetLength(1) / 2);
            for (var radius = 0; radius <= 1; radius++)
            for (var dx = -radius; dx <= radius; dx++)
            for (var dy = -radius; dy <= radius; dy++)
            {
                var cell = center + new Vector2Int(dx, dy);
                if (cell.x >= 0 && cell.y >= 0 && cell.x < grid.GetLength(0) && cell.y < grid.GetLength(1) && grid[cell.x, cell.y].hit)
                {
                    seed = cell;
                    return true;
                }
            }

            seed = default;
            return false;
        }

        static bool RestsOnHorizontalSurface(DepthSample[,] grid, bool[,] filled, Vector2Int min, Vector2Int max)
        {
            // The rays right below the object's lowest filled cell in each column.
            int upward = 0, total = 0;
            for (var x = min.x; x <= max.x; x++)
            {
                var lowest = -1;
                for (var y = min.y; y <= max.y && lowest < 0; y++)
                    if (filled[x, y])
                        lowest = y;
                if (lowest <= 0 || !grid[x, lowest - 1].hit || filled[x, lowest - 1])
                    continue;
                total++;
                if (grid[x, lowest - 1].normal.y > k_UpwardNormal)
                    upward++;
            }

            return total > 0 && upward * 2 > total;
        }

        static float CellSpacing(Rect viewport, int columns, float depth) => depth * viewport.width / columns;

        /// <summary>"34 cm" below a meter, "1.2 m" above.</summary>
        public static string FormatLength(float meters) =>
            meters < 1f
                ? $"{Mathf.Max(1, Mathf.RoundToInt(meters * 100f)).ToString(CultureInfo.InvariantCulture)} cm"
                : $"{meters.ToString("0.0", CultureInfo.InvariantCulture)} m";

        /// <summary>A sentence of measurements for the model's prompt.</summary>
        public static string Describe(DepthBlob blob)
        {
            var size = $"about {FormatLength(blob.Width)} wide and {FormatLength(blob.Height)} tall";
            var distance = $"about {FormatLength(blob.Distance)} away";
            return blob.FillsFrame
                ? $"Measured by the headset's depth sensor: the area in this image is {size}, {distance}."
                : $"Measured by the headset's depth sensor: the main object in this image is {size}, {distance}" +
                  (blob.OnHorizontalSurface ? ", resting on a horizontal surface." : ".");
        }

        /// <summary>A short label for the answer card.</summary>
        public static string Label(DepthBlob blob) =>
            $"{FormatLength(blob.Width)} x {FormatLength(blob.Height)}, {FormatLength(blob.Distance)} away";
    }
}
