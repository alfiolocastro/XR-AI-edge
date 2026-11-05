using AIEdge.WhatsThat;
using NUnit.Framework;
using UnityEngine;

namespace AIEdge.Tests
{
    public class WhatsThatTests
    {
        [Test]
        public void IsPointingPose_NeedsIndexOutAndMiddleCurled()
        {
            var palm = Vector3.zero;
            Assert.IsTrue(PointerMath.IsPointingPose(new Vector3(0, 0, 0.12f), new Vector3(0, -0.02f, 0.04f), palm));
            Assert.IsFalse(PointerMath.IsPointingPose(new Vector3(0, 0, 0.05f), new Vector3(0, -0.02f, 0.04f), palm), "index curled");
            Assert.IsFalse(PointerMath.IsPointingPose(new Vector3(0, 0, 0.12f), new Vector3(0, 0, 0.11f), palm), "open hand");
        }

        [Test]
        public void RegionSize_GrowsWithDistanceWithinLimits()
        {
            Assert.AreEqual(0.3f, PointerMath.RegionSize(0.4f), 1e-4f);
            Assert.AreEqual(0.9f, PointerMath.RegionSize(2f), 1e-4f);
            Assert.AreEqual(1.4f, PointerMath.RegionSize(6f), 1e-4f);
        }

        [Test]
        public void Prompt_AsksForBoldNameAndAddsFactsAndLanguage()
        {
            var english = PointerMath.Prompt("It is about 30 cm wide.", SpeechLanguage.English);
            StringAssert.Contains("name in bold", english);
            StringAssert.Contains("It is about 30 cm wide.", english);
            StringAssert.DoesNotContain("Italian", english);
            StringAssert.EndsWith("Reply in Italian.", PointerMath.Prompt("", SpeechLanguage.Italian));
        }

        [TestCase("**Coffee mug** — A white ceramic mug with a handle.", "Coffee mug", "A white ceramic mug with a handle.")]
        [TestCase("This is a **potted plant**. It has green leaves.", "potted plant", "This is a potted plant. It has green leaves.")]
        [TestCase("**Lampada da tavolo:** una lampada nera.", "Lampada da tavolo", "una lampada nera.")]
        [TestCase("A laptop. It is open on the desk.", "A laptop", "It is open on the desk.")]
        [TestCase("Keyboard", "Keyboard", "")]
        [TestCase("", "", "")]
        public void ParseAnswer_SplitsNameAndDescription(string reply, string name, string description)
        {
            var (parsedName, parsedDescription) = PointerMath.ParseAnswer(reply);
            Assert.AreEqual(name, parsedName);
            Assert.AreEqual(description, parsedDescription);
        }

        [TestCase(0.42f, "en", "It's about 40 centimeters away from you.")]
        [TestCase(1.0f, "en", "It's about 1 meter away from you.")]
        [TestCase(1.43f, "en", "It's about 1.4 meters away from you.")]
        [TestCase(2.04f, "en", "It's about 2 meters away from you.")]
        [TestCase(0.67f, "it", "Si trova a circa 65 centimetri da te.")]
        [TestCase(1.0f, "it", "Si trova a circa 1 metro da te.")]
        [TestCase(1.43f, "it", "Si trova a circa 1,4 metri da te.")]
        public void SpokenDistance_UsesWordsAndLocalNumbers(float meters, string language, string expected)
        {
            Assert.AreEqual(expected, PointerMath.SpokenDistance(meters, language));
        }
    }
}
