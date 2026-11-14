using AIEdge.UI;
using NUnit.Framework;
using UnityEngine;

namespace AIEdge.Tests
{
    public class MicButtonTests
    {
        GameObject m_Parent;

        [SetUp]
        public void SetUp() => m_Parent = new GameObject("MicButtonTests", typeof(RectTransform));

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(m_Parent);

        [Test]
        public void GestureWords_GoToTheGestureHandler()
        {
            string typed = null, spoken = null;
            var mic = MicButton.Create(m_Parent.transform, t => typed = t, null, 20, t => spoken = t).GetComponent<MicButton>();

            mic.DeliverForTests("what is this", true);
            Assert.That(spoken, Is.EqualTo("what is this"));
            Assert.That(typed, Is.Null);

            mic.DeliverForTests("what color", false);
            Assert.That(typed, Is.EqualTo("what color"));
            Assert.That(spoken, Is.EqualTo("what is this"));
        }

        [Test]
        public void WithoutAGestureHandler_GestureWordsFillTheQuestion()
        {
            string typed = null;
            var mic = MicButton.Create(m_Parent.transform, t => typed = t).GetComponent<MicButton>();
            mic.DeliverForTests("hello", true);
            Assert.That(typed, Is.EqualTo("hello"));
        }
    }
}
