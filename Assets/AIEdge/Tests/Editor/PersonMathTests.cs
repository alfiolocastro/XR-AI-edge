using System.Linq;
using AIEdge.WhatsThat;
using NUnit.Framework;
using UnityEngine;

namespace AIEdge.Tests
{
    public class PersonMathTests
    {
        [Test]
        public void ToViewport_FlipsTopLeftBoxesToBottomLeft()
        {
            // A box in the top-left quarter of the image is in the top-left quarter of the viewport.
            var viewport = PersonMath.ToViewport(0.1f, 0.1f, 0.2f, 0.3f);
            Assert.That(viewport.xMin, Is.EqualTo(0.1f).Within(1e-5f));
            Assert.That(viewport.yMin, Is.EqualTo(0.6f).Within(1e-5f));
            Assert.That(viewport.yMax, Is.EqualTo(0.9f).Within(1e-5f));
            Assert.That(viewport.width, Is.EqualTo(0.2f).Within(1e-5f));
        }

        [Test]
        public void Overlap_IsOneForTheSameBoxAndZeroApart()
        {
            var a = new Rect(0.1f, 0.1f, 0.2f, 0.4f);
            Assert.That(PersonMath.Overlap(a, a), Is.EqualTo(1f).Within(1e-5f));
            Assert.That(PersonMath.Overlap(a, new Rect(0.5f, 0.1f, 0.2f, 0.4f)), Is.EqualTo(0f));
            // Half shifted: intersection 0.5, union 1.5.
            Assert.That(PersonMath.Overlap(new Rect(0f, 0f, 1f, 1f), new Rect(0.5f, 0f, 1f, 1f)), Is.EqualTo(1f / 3f).Within(1e-5f));
        }

        [Test]
        public void Match_FollowsPeopleAndFindsNewOnes()
        {
            var tracked = new[] { new Rect(0.1f, 0.2f, 0.2f, 0.6f), new Rect(0.6f, 0.2f, 0.2f, 0.6f) };
            // The second person moved a little, the first is gone, and someone new appeared in the middle.
            var found = new[] { new Rect(0.63f, 0.2f, 0.2f, 0.6f), new Rect(0.38f, 0.1f, 0.15f, 0.5f) };
            var matches = PersonMath.Match(tracked, found);
            Assert.That(matches, Is.EqualTo(new[] { 1, -1 }));
        }

        [Test]
        public void Match_GivesEachTrackOnlyOnce()
        {
            var tracked = new[] { new Rect(0.4f, 0.2f, 0.2f, 0.6f) };
            var found = new[] { new Rect(0.42f, 0.2f, 0.2f, 0.6f), new Rect(0.38f, 0.2f, 0.2f, 0.6f) };
            var matches = PersonMath.Match(tracked, found);
            Assert.That(matches.Count(m => m == 0), Is.EqualTo(1));
            Assert.That(matches.Count(m => m == -1), Is.EqualTo(1));
        }

        [Test]
        public void DepthProbes_StayInsideTheBox()
        {
            var box = new Rect(0.2f, 0.1f, 0.3f, 0.7f);
            var probes = PersonMath.DepthProbes(box).ToList();
            Assert.That(probes[0], Is.EqualTo(box.center));
            Assert.That(probes.All(box.Contains), Is.True);
        }

        [Test]
        public void Label_UsesTheLanguagesDecimalSeparator()
        {
            Assert.That(PersonMath.Label(2.34f, SpeechLanguage.English), Is.EqualTo("Person · 2.3 m"));
            Assert.That(PersonMath.Label(2.34f, SpeechLanguage.Italian), Is.EqualTo("Persona · 2,3 m"));
            Assert.That(PersonMath.Label(float.NaN, SpeechLanguage.Italian), Is.EqualTo("Persona"));
        }

        [Test]
        public void Summary_CountsPeopleNearestFirst()
        {
            Assert.That(PersonMath.Summary(new float[0], SpeechLanguage.English), Is.EqualTo("No people in view"));
            Assert.That(PersonMath.Summary(new[] { 1.8f }, SpeechLanguage.English), Is.EqualTo("1 person in view: 1.8 m"));
            Assert.That(PersonMath.Summary(new[] { 3.4f, float.NaN, 1.8f }, SpeechLanguage.Italian),
                Is.EqualTo("3 persone in vista: 1,8 m · 3,4 m · ?"));
        }
    }
}
