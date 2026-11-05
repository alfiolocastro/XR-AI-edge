using AIEdge.Spatial;
using AIEdge.WhatsThat;
using NUnit.Framework;
using UnityEngine;

namespace AIEdge.Tests
{
    public class DepthMaskTests
    {
        const int k_Grid = 20;

        // A wall at 2 m facing the eye, with optional objects in front of it.
        static (float[,] distance, Vector3[,] normal) Wall(float wall = 2f)
        {
            var distance = new float[k_Grid, k_Grid];
            var normal = new Vector3[k_Grid, k_Grid];
            for (var x = 0; x < k_Grid; x++)
            for (var y = 0; y < k_Grid; y++)
            {
                distance[x, y] = wall;
                normal[x, y] = Vector3.back;
            }

            return (distance, normal);
        }

        static void Box((float[,] distance, Vector3[,] normal) scene, int x0, int y0, int x1, int y1, float distance)
        {
            for (var x = x0; x <= x1; x++)
            for (var y = y0; y <= y1; y++)
                scene.distance[x, y] = distance;
        }

        [Test]
        public void FloodFill_CutsOutObjectInFrontOfWall()
        {
            var scene = Wall();
            Box(scene, 6, 5, 13, 14, 1.5f); // 8 x 10 cells, 50 cm in front of the wall
            var mask = DepthMask.FloodFill(scene.distance, scene.normal);
            Assert.IsNotNull(mask);
            Assert.AreEqual(80f / (k_Grid * k_Grid), DepthMask.Coverage(mask), 1e-4f);
            Assert.IsTrue(mask[10, 10]);
            Assert.IsFalse(mask[2, 2]);
            Assert.AreEqual(0, DepthMask.SidesTouched(mask));
        }

        [Test]
        public void FloodFill_RejectsBareWall()
        {
            var scene = Wall();
            Assert.IsNull(DepthMask.FloodFill(scene.distance, scene.normal));
        }

        [Test]
        public void FloodFill_StopsWhereTheSurfaceTurns()
        {
            // An object resting on a table: same depth where they meet, but the table faces up.
            var scene = Wall();
            Box(scene, 6, 6, 13, 13, 1.5f);
            for (var x = 0; x < k_Grid; x++)
            for (var y = 0; y < 6; y++)
            {
                scene.distance[x, y] = 1.5f;
                scene.normal[x, y] = Vector3.up;
            }

            var mask = DepthMask.FloodFill(scene.distance, scene.normal);
            Assert.IsNotNull(mask);
            Assert.IsFalse(mask[10, 3], "table");
            Assert.IsTrue(mask[10, 6]);
        }

        [Test]
        public void FloodFill_StartsNextToCenterWhenCenterMisses()
        {
            var scene = Wall();
            Box(scene, 6, 6, 13, 13, 1.5f);
            scene.distance[10, 10] = float.NaN;
            var mask = DepthMask.FloodFill(scene.distance, scene.normal);
            Assert.IsNotNull(mask);
            Assert.IsTrue(mask[11, 10]);
        }

        [Test]
        public void HighlightAlpha_HasBrightEdgeAndDimFill()
        {
            var mask = new bool[10, 10];
            for (var x = 2; x < 8; x++)
            for (var y = 2; y < 8; y++)
                mask[x, y] = true;
            var alpha = DepthMask.HighlightAlpha(mask, 1, 50, 200);
            Assert.AreEqual(0, alpha[0]);
            Assert.AreEqual(200, alpha[2 * 10 + 2], "corner is edge");
            Assert.AreEqual(50, alpha[5 * 10 + 5], "inside is fill");
        }

        [Test]
        public void FlipRows_ReversesRowOrder()
        {
            var pixels = new byte[] { 1, 2, 3, 4, 5, 6 };
            CollectionAssert.AreEqual(new byte[] { 5, 6, 3, 4, 1, 2 }, ObjectSegmenter.FlipRows(pixels, 2, 3));
        }

        [Test]
        public void Downsample_ThresholdsAtHalf()
        {
            var size = 4;
            var probabilities = new byte[size * size];
            probabilities[0] = 255; // bottom-left pixel
            probabilities[size * size - 1] = 100; // top-right, below threshold
            var mask = ObjectHighlight.Downsample(probabilities, size, 2);
            Assert.IsFalse(mask[0, 0], "cell centers sample pixel (1,1)");
            probabilities[1 * size + 1] = 200;
            mask = ObjectHighlight.Downsample(probabilities, size, 2);
            Assert.IsTrue(mask[0, 0]);
            Assert.IsFalse(mask[1, 1]);
        }

        [Test]
        public void SegmentationResult_IsUsableOnlyForAPlausibleObject()
        {
            Assert.IsTrue(new SegmentationResult { AtKeypoint = 0.9f, Coverage = 0.2f }.IsUsable);
            Assert.IsFalse(new SegmentationResult { AtKeypoint = 0.2f, Coverage = 0.2f }.IsUsable, "misses the point");
            Assert.IsFalse(new SegmentationResult { AtKeypoint = 0.9f, Coverage = 0.95f }.IsUsable, "whole image");
            Assert.IsFalse(new SegmentationResult { AtKeypoint = 0.9f, Coverage = 0.2f, Error = "x" }.IsUsable);
        }
    }
}
