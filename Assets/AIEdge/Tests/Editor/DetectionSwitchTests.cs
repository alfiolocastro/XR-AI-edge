using AIEdge.WhatsThat;
using NUnit.Framework;

namespace AIEdge.Tests
{
    public class DetectionSwitchTests
    {
        const float k_Step = 0.05f;

        static KeyHoldEvent HoldFor(KeyHoldTracker tracker, float seconds)
        {
            var result = tracker.Update(true, 0f);
            for (var t = 0f; t < seconds - 1e-4f; t += k_Step)
            {
                var e = tracker.Update(true, k_Step);
                if (e != KeyHoldEvent.None)
                    result = e;
            }

            return result;
        }

        [Test]
        public void KeyHold_ShortPressIsAPress()
        {
            var tracker = new KeyHoldTracker();
            Assert.That(HoldFor(tracker, 0.3f), Is.EqualTo(KeyHoldEvent.None));
            Assert.That(tracker.Update(false, k_Step), Is.EqualTo(KeyHoldEvent.Press));
        }

        [Test]
        public void KeyHold_FiresOnceAtTwoSecondsWithoutWaitingForRelease()
        {
            var tracker = new KeyHoldTracker();
            Assert.That(HoldFor(tracker, 1.9f), Is.EqualTo(KeyHoldEvent.None));
            Assert.That(tracker.Progress, Is.EqualTo(0.95f).Within(0.03f));
            Assert.That(HoldFor2(tracker, 0.2f), Is.EqualTo(KeyHoldEvent.Hold));
            // Holding on, then letting go, does nothing more: no second hold and no press.
            Assert.That(HoldFor2(tracker, 3f), Is.EqualTo(KeyHoldEvent.None));
            Assert.That(tracker.Update(false, k_Step), Is.EqualTo(KeyHoldEvent.None));
            Assert.That(tracker.Progress, Is.EqualTo(0f));
        }

        [Test]
        public void KeyHold_TapWithinOneFrameStillCounts()
        {
            var tracker = new KeyHoldTracker();
            Assert.That(tracker.Update(true, 0f), Is.EqualTo(KeyHoldEvent.None));
            Assert.That(tracker.Update(false, 0f), Is.EqualTo(KeyHoldEvent.Press));
        }

        // Continues a hold already in progress.
        static KeyHoldEvent HoldFor2(KeyHoldTracker tracker, float seconds)
        {
            var result = KeyHoldEvent.None;
            for (var t = 0f; t < seconds - 1e-4f; t += k_Step)
            {
                var e = tracker.Update(true, k_Step);
                if (e != KeyHoldEvent.None)
                    result = e;
            }

            return result;
        }

        [Test]
        public void HudText_AnalysisLinesUpTheLiveNumbers()
        {
            var text = HudText.Analysis(2, 1, 2.34f, ThreatLevel.Medium, 46, 4.7f);
            var lines = text.Split('\n');
            Assert.That(lines, Has.Length.EqualTo(3));
            Assert.That(lines[0], Is.EqualTo("SUBJECTS     2   LOCKED     1"));
            Assert.That(lines[1], Is.EqualTo("RANGE    02.3M   THREAT  MEDIUM"));
            Assert.That(lines[2], Is.EqualTo("INFER    46MS   RATE    4.7HZ"));
            Assert.That(HudText.Analysis(0, 0, float.NaN, ThreatLevel.Unknown, 0, 0f).Split('\n')[1],
                Is.EqualTo("RANGE     --.-   THREAT      --"));
        }

        [Test]
        public void HudText_MatchAndCountdown()
        {
            Assert.That(HudText.Match(true, true, ThreatLevel.High), Is.EqualTo("HUMAN  ·  THREAT HIGH"));
            Assert.That(HudText.Match(true, false, ThreatLevel.Low), Is.EqualTo("ACQUIRING TARGET"));
            Assert.That(HudText.Match(false, false, ThreatLevel.Unknown), Is.EqualTo("SCANNING"));
            Assert.That(HudText.Countdown(4.2f), Is.EqualTo("VISUAL ANALYSIS ONLINE IN 5"));
            Assert.That(HudText.Countdown(0f), Is.EqualTo("VISUAL ANALYSIS ONLINE IN 0"));
            Assert.That(HudText.Uptime(3725f), Is.EqualTo("01:02:05"));
        }

        [Test]
        public void HudText_DataRowsKeepTheirWidth()
        {
            var random = new System.Random(3);
            for (var i = 0; i < 50; i++)
            {
                Assert.That(HudText.NumberRow(random), Has.Length.EqualTo(20));
                Assert.That(HudText.LogRow(random, 990 + i), Has.Length.EqualTo(21));
            }
        }
    }
}
