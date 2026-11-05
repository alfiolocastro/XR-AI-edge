using System.Collections.Generic;
using UnityEngine;

namespace AIEdge.Spatial
{
    /// <summary>
    /// Segments the object under a pointed spot from depth alone: a grid of rays from the eye is cast through a square
    /// around the spot, and a flood fill from the center keeps cells whose depth and surface direction change smoothly.
    /// A step in depth (object edge) or a sharp turn of the surface (object meeting the table or wall) stops the fill.
    /// Plain C# so it can be unit tested.
    /// </summary>
    public static class DepthMask
    {
        const float k_StepAbsolute = 0.035f;
        const float k_StepRelative = 0.035f;
        const float k_BandAbsolute = 0.25f;
        const float k_BandRelative = 0.25f;
        const float k_NormalContinuity = 0.75f; // cos ~41° between neighboring cells
        const float k_MaxCoverage = 0.8f;

        /// <summary>
        /// Flood-fills from the center cell. <paramref name="distance"/> holds each ray's hit distance (NaN for no hit)
        /// and <paramref name="normal"/> the surface normals (zero when unknown). Returns null when there is no seed,
        /// or when the fill looks like a surface rather than an object: it covers most of the grid or touches three
        /// or more sides (a wall, floor or table top).
        /// </summary>
        public static bool[,] FloodFill(float[,] distance, Vector3[,] normal)
        {
            var columns = distance.GetLength(0);
            var rows = distance.GetLength(1);
            var seed = new Vector2Int(columns / 2, rows / 2);
            if (float.IsNaN(distance[seed.x, seed.y]))
            {
                if (!FindNearestHit(distance, seed, out seed))
                    return null;
            }

            var seedDistance = distance[seed.x, seed.y];
            var band = Mathf.Max(k_BandAbsolute, k_BandRelative * seedDistance);
            var mask = new bool[columns, rows];
            var queue = new Queue<Vector2Int>();
            mask[seed.x, seed.y] = true;
            queue.Enqueue(seed);
            var count = 0;

            while (queue.Count > 0)
            {
                var cell = queue.Dequeue();
                count++;
                var d = distance[cell.x, cell.y];
                foreach (var next in Neighbors(cell))
                {
                    if (next.x < 0 || next.y < 0 || next.x >= columns || next.y >= rows || mask[next.x, next.y])
                        continue;
                    var dn = distance[next.x, next.y];
                    if (float.IsNaN(dn) || Mathf.Abs(dn - d) > Mathf.Max(k_StepAbsolute, k_StepRelative * d) ||
                        Mathf.Abs(dn - seedDistance) > band)
                        continue;
                    var n0 = normal[cell.x, cell.y];
                    var n1 = normal[next.x, next.y];
                    if (n0 != Vector3.zero && n1 != Vector3.zero && Vector3.Dot(n0, n1) < k_NormalContinuity)
                        continue;

                    mask[next.x, next.y] = true;
                    queue.Enqueue(next);
                }
            }

            if (count > k_MaxCoverage * columns * rows || SidesTouched(mask) >= 3)
                return null;
            return mask;
        }

        /// <summary>Number of grid sides (0-4) the mask reaches.</summary>
        public static int SidesTouched(bool[,] mask)
        {
            var columns = mask.GetLength(0);
            var rows = mask.GetLength(1);
            bool left = false, right = false, bottom = false, top = false;
            for (var y = 0; y < rows; y++)
            {
                left |= mask[0, y];
                right |= mask[columns - 1, y];
            }

            for (var x = 0; x < columns; x++)
            {
                bottom |= mask[x, 0];
                top |= mask[x, rows - 1];
            }

            return (left ? 1 : 0) + (right ? 1 : 0) + (bottom ? 1 : 0) + (top ? 1 : 0);
        }

        /// <summary>Fraction of cells set.</summary>
        public static float Coverage(bool[,] mask)
        {
            var set = 0;
            foreach (var cell in mask)
                if (cell)
                    set++;
            return (float)set / mask.Length;
        }

        /// <summary>
        /// Turns a mask (x right, y up, as a texture) into highlight pixels: a translucent fill and a bright edge
        /// <paramref name="edgeWidth"/> cells wide. Returns alpha per pixel, row by row from the bottom.
        /// </summary>
        public static byte[] HighlightAlpha(bool[,] mask, int edgeWidth, byte fillAlpha = 70, byte edgeAlpha = 235)
        {
            var columns = mask.GetLength(0);
            var rows = mask.GetLength(1);
            var alpha = new byte[columns * rows];
            for (var y = 0; y < rows; y++)
            for (var x = 0; x < columns; x++)
            {
                if (!mask[x, y])
                    continue;
                alpha[y * columns + x] = IsEdge(mask, x, y, edgeWidth) ? edgeAlpha : fillAlpha;
            }

            return alpha;
        }

        static bool IsEdge(bool[,] mask, int x, int y, int width)
        {
            var columns = mask.GetLength(0);
            var rows = mask.GetLength(1);
            for (var dy = -width; dy <= width; dy++)
            for (var dx = -width; dx <= width; dx++)
            {
                var nx = x + dx;
                var ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= columns || ny >= rows || !mask[nx, ny])
                    return true;
            }

            return false;
        }

        static bool FindNearestHit(float[,] distance, Vector2Int center, out Vector2Int found)
        {
            var best = float.MaxValue;
            found = center;
            for (var x = 0; x < distance.GetLength(0); x++)
            for (var y = 0; y < distance.GetLength(1); y++)
            {
                if (float.IsNaN(distance[x, y]))
                    continue;
                var d = (new Vector2Int(x, y) - center).sqrMagnitude;
                if (d < best && d <= 2)
                {
                    best = d;
                    found = new Vector2Int(x, y);
                }
            }

            return best < float.MaxValue;
        }

        static IEnumerable<Vector2Int> Neighbors(Vector2Int cell)
        {
            yield return new Vector2Int(cell.x + 1, cell.y);
            yield return new Vector2Int(cell.x - 1, cell.y);
            yield return new Vector2Int(cell.x, cell.y + 1);
            yield return new Vector2Int(cell.x, cell.y - 1);
        }
    }
}
