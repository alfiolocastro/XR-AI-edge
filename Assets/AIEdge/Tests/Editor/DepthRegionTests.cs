using AIEdge.Spatial;
using NUnit.Framework;
using UnityEngine;

namespace AIEdge.Tests
{
    public class DepthRegionTests
    {
        const int k_Size = 9;
        const float k_Step = 0.1f; // ray slope per cell, so cells are 10 cm apart at 1 m
        static readonly Pose k_Camera = new Pose(Vector3.zero, Quaternion.identity);
        static readonly Rect k_Viewport = Rect.MinMaxRect(0.2f, 0.2f, 0.8f, 0.8f);

        // A camera at the origin looking down +z at a wall 2 m away. A 3×3-cell box sits 1 m away in the middle,
        // optionally on a table (the cells under it hit an upward surface).
        static DepthSample[,] Scene(bool withBox, bool onTable)
        {
            var grid = new DepthSample[k_Size, k_Size];
            var mid = k_Size / 2;
            for (var x = 0; x < k_Size; x++)
            for (var y = 0; y < k_Size; y++)
            {
                var direction = new Vector3((x - mid) * k_Step, (y - mid) * k_Step, 1f);
                var inBox = withBox && Mathf.Abs(x - mid) <= 1 && Mathf.Abs(y - mid) <= 1;
                var tableRow = onTable && y == mid - 2 && Mathf.Abs(x - mid) <= 1;
                var distance = inBox ? 1f : tableRow ? 1.1f : 2f;
                var normal = tableRow ? Vector3.up : Vector3.back;
                grid[x, y] = new DepthSample { hit = true, point = direction * distance, normal = normal };
            }

            return grid;
        }

        [Test]
        public void Analyze_FindsBoxCropAndSize()
        {
            var blob = DepthRegion.Analyze(Scene(true, false), k_Viewport, k_Camera);
            Assert.IsNotNull(blob);
            Assert.IsFalse(blob.FillsFrame);
            Assert.AreEqual(9, blob.Cells);
            Assert.AreEqual(0.3f, blob.Width, 0.01f);
            Assert.AreEqual(0.3f, blob.Height, 0.01f);
            Assert.AreEqual(1f, blob.Distance, 0.01f);

            var cell = k_Viewport.width / k_Size;
            Assert.AreEqual(k_Viewport.xMin + 3 * cell, blob.Viewport.xMin, 1e-4f);
            Assert.AreEqual(k_Viewport.xMin + 6 * cell, blob.Viewport.xMax, 1e-4f);
            Assert.IsFalse(blob.OnHorizontalSurface);
        }

        [Test]
        public void Analyze_DetectsSupportingSurface()
        {
            Assert.IsTrue(DepthRegion.Analyze(Scene(true, true), k_Viewport, k_Camera).OnHorizontalSurface);
        }

        [Test]
        public void Analyze_WallFillsFrame()
        {
            var blob = DepthRegion.Analyze(Scene(false, false), k_Viewport, k_Camera);
            Assert.IsTrue(blob.FillsFrame);
            Assert.AreEqual(k_Size * k_Size, blob.Cells);
        }

        [Test]
        public void Analyze_NoHitsReturnsNull()
        {
            Assert.IsNull(DepthRegion.Analyze(new DepthSample[k_Size, k_Size], k_Viewport, k_Camera));
        }

        [Test]
        public void Describe_UsesReadableUnits()
        {
            Assert.AreEqual("34 cm", DepthRegion.FormatLength(0.341f));
            Assert.AreEqual("1.2 m", DepthRegion.FormatLength(1.24f));
            var blob = new DepthBlob { Width = 0.3f, Height = 0.2f, Distance = 1.1f, OnHorizontalSurface = true };
            Assert.AreEqual("Measured by the headset's depth sensor: the main object in this image is about 30 cm wide and 20 cm tall, about 1.1 m away, resting on a horizontal surface.",
                DepthRegion.Describe(blob));
            Assert.AreEqual("30 cm x 20 cm, 1.1 m away", DepthRegion.Label(blob));
        }
    }
}
