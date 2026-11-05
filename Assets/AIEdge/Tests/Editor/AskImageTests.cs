using AIEdge.AskImage;
using NUnit.Framework;
using UnityEngine;

namespace AIEdge.Tests
{
    public class AskImageTests
    {
        const float k_Epsilon = 1e-4f;

        [Test]
        public void FromDiagonal_FacesEyeAndMeasuresSides()
        {
            var region = FramedRegion.FromDiagonal(Vector3.zero, new Vector3(-0.1f, -0.05f, 0.5f), new Vector3(0.1f, 0.05f, 0.5f));
            Assert.AreEqual(0.2f, region.Size.x, k_Epsilon);
            Assert.AreEqual(0.1f, region.Size.y, k_Epsilon);
            Assert.That(Vector3.Distance(region.Center, new Vector3(0f, 0f, 0.5f)), Is.LessThan(k_Epsilon));
            Assert.That(Vector3.Angle(region.Rotation * Vector3.forward, Vector3.forward), Is.LessThan(0.01f));
            Assert.AreEqual(4, region.Corners.Length);
        }

        [Test]
        public void FromDiagonal_OrderOfHandsDoesNotMatter()
        {
            var a = FramedRegion.FromDiagonal(Vector3.zero, new Vector3(0.1f, -0.05f, 0.5f), new Vector3(-0.1f, 0.05f, 0.5f));
            Assert.AreEqual(0.2f, a.Size.x, k_Epsilon);
            Assert.AreEqual(0.1f, a.Size.y, k_Epsilon);
        }

        [Test]
        public void InFront_IsLevelAndAtDistance()
        {
            var head = new Pose(new Vector3(0f, 1.6f, 0f), Quaternion.Euler(30f, 90f, 0f));
            var region = FramedRegion.InFront(head, 1.2f, new Vector2(0.6f, 0.4f));
            Assert.That(Vector3.Distance(region.Center, new Vector3(1.2f, 1.6f, 0f)), Is.LessThan(k_Epsilon));
            Assert.AreEqual(region.Corners[0].y, region.Corners[3].y, k_Epsilon);
        }

        [Test]
        public void ExtendFromEye_PushesNearPointsOut_KeepsFarOnes()
        {
            var points = ImageRegion.ExtendFromEye(Vector3.zero, new[] { new Vector3(0f, 0f, 0.4f), new Vector3(0f, 0f, 3f) }, 1f);
            Assert.AreEqual(1f, points[0].z, k_Epsilon);
            Assert.AreEqual(3f, points[1].z, k_Epsilon);
        }

        [Test]
        public void AllInFront_RejectsPointsBehindCamera()
        {
            var camera = new Pose(Vector3.zero, Quaternion.identity);
            Assert.IsTrue(ImageRegion.AllInFront(camera, new[] { Vector3.forward }));
            Assert.IsFalse(ImageRegion.AllInFront(camera, new[] { Vector3.forward, Vector3.back }));
        }

        [Test]
        public void ViewportBounds_ClampsAndDetectsMiss()
        {
            var bounds = ImageRegion.ViewportBounds(new[] { new Vector2(-0.2f, 0.4f), new Vector2(0.5f, 1.3f) });
            Assert.AreEqual(Rect.MinMaxRect(0f, 0.4f, 0.5f, 1f), bounds);
            Assert.AreEqual(Rect.zero, ImageRegion.ViewportBounds(new[] { new Vector2(1.2f, 0.5f), new Vector2(1.5f, 0.6f) }));
        }

        [Test]
        public void ToPixels_PadsGrowsAndStaysInside()
        {
            var resolution = new Vector2Int(1280, 960);
            var padded = ImageRegion.ToPixels(Rect.MinMaxRect(0.25f, 0.25f, 0.75f, 0.75f), resolution, 0.1f, 64);
            Assert.AreEqual(new RectInt(256, 192, 768, 576), padded);

            var tiny = ImageRegion.ToPixels(Rect.MinMaxRect(0.5f, 0.5f, 0.51f, 0.51f), resolution, 0f, 224);
            Assert.AreEqual(224, tiny.width);
            Assert.AreEqual(224, tiny.height);

            var corner = ImageRegion.ToPixels(Rect.MinMaxRect(0.95f, 0f, 1f, 0.05f), resolution, 0f, 224);
            Assert.AreEqual(1280 - 224, corner.x);
            Assert.AreEqual(0, corner.y);
        }
    }
}
