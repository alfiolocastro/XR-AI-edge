using AIEdge.Interaction;
using NUnit.Framework;
using UnityEngine;

namespace AIEdge.Tests
{
    public class MicGestureTests
    {
        const float k_Step = 0.05f;

        static MicGestureEvent Run(MicGestureTracker tracker, bool qualified, float distance, float seconds)
        {
            var result = MicGestureEvent.None;
            for (var t = 0f; t < seconds - 1e-4f; t += k_Step)
            {
                var e = tracker.Update(qualified, distance, k_Step);
                if (e != MicGestureEvent.None)
                    result = e;
            }

            return result;
        }

        [Test]
        public void Mouth_IsBelowAndInFrontOfTheEyes()
        {
            var mouth = MicGestureMath.Mouth(new Vector3(0f, 1.6f, 0f), Quaternion.identity);
            Assert.AreEqual(1.52f, mouth.y, 1e-4f);
            Assert.Greater(mouth.z, 0f);

            // Turned to face +X: the mouth is in front along +X.
            var turned = MicGestureMath.Mouth(Vector3.zero, Quaternion.Euler(0f, 90f, 0f));
            Assert.Greater(turned.x, 0.05f);
        }

        [Test]
        public void IsFist_NeedsThreeCurledFingers()
        {
            var palm = Vector3.zero;
            var curled = new Vector3(0.03f, 0f, 0.02f);
            var open = new Vector3(0f, 0f, 0.09f);
            Assert.IsTrue(MicGestureMath.IsFist(palm, curled, curled, curled, curled));
            Assert.IsTrue(MicGestureMath.IsFist(palm, open, curled, curled, curled), "index out, still a fist");
            Assert.IsFalse(MicGestureMath.IsFist(palm, open, open, curled, curled));
            Assert.IsFalse(MicGestureMath.IsFist(palm, open, open, open, open));
        }

        [Test]
        public void Tracker_StartsOnlyAfterHoldingAtTheMouth()
        {
            var tracker = new MicGestureTracker();
            Assert.AreEqual(MicGestureEvent.None, Run(tracker, true, 0.05f, 0.2f), "a brush past the face");
            Assert.AreEqual(MicGestureEvent.None, Run(tracker, true, 0.5f, 0.1f));
            Assert.AreEqual(MicGestureEvent.None, Run(tracker, true, 0.05f, 0.2f), "the hold restarts after moving away");
            Assert.AreEqual(MicGestureEvent.Start, Run(tracker, true, 0.05f, 0.2f));
            Assert.IsTrue(tracker.IsActive);
        }

        [Test]
        public void Tracker_NeedsAMic()
        {
            var tracker = new MicGestureTracker();
            Assert.AreEqual(MicGestureEvent.None, Run(tracker, false, 0.05f, 1f), "an open hand at the mouth");
            Assert.AreEqual(MicGestureEvent.None, Run(tracker, true, 0.15f, 1f), "a fist under the chin, not at the mouth");
        }

        [Test]
        public void Tracker_KeepsListeningWhileNearAndStopsWhenAway()
        {
            var tracker = new MicGestureTracker();
            Run(tracker, true, 0.05f, 0.5f);
            Assert.AreEqual(MicGestureEvent.None, Run(tracker, true, 0.15f, 2f), "between start and stop distance");
            Assert.AreEqual(MicGestureEvent.None, Run(tracker, false, 0.08f, 2f), "the fist loosens a little at the mouth");
            Assert.AreEqual(MicGestureEvent.None, Run(tracker, true, 0.5f, 0.2f), "a short wobble away");
            Assert.AreEqual(MicGestureEvent.None, Run(tracker, true, 0.05f, 0.1f));
            Assert.AreEqual(MicGestureEvent.Stop, Run(tracker, true, 0.5f, 0.4f));
            Assert.IsFalse(tracker.IsActive);
        }

        [Test]
        public void Tracker_StopsWhenTrackingIsLostAndWaitsBeforeRestarting()
        {
            var tracker = new MicGestureTracker();
            Run(tracker, true, 0.05f, 0.5f);
            Assert.AreEqual(MicGestureEvent.Stop, Run(tracker, false, float.PositiveInfinity, 0.4f));
            Assert.AreEqual(MicGestureEvent.None, Run(tracker, true, 0.05f, 0.4f), "cooldown");
            Assert.AreEqual(MicGestureEvent.Start, Run(tracker, true, 0.05f, 0.6f));
        }
    }
}
