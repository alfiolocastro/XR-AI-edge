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
        public void Region_BoundsThePoints()
        {
            Assert.That(PersonMath.Region(new Vector2[0]), Is.Null);
            var region = PersonMath.Region(new[] { new Vector2(0.6f, 0.1f), new Vector2(0.8f, 0.4f), new Vector2(0.7f, 0.2f) });
            Assert.That(region, Is.EqualTo(Rect.MinMaxRect(0.6f, 0.1f, 0.8f, 0.4f)));
        }

        [Test]
        public void IsOwnBody_DropsBoxesMostlyOnTheUsersHand()
        {
            // A hand and forearm raised at the lower right of the view.
            var hand = new[] { Rect.MinMaxRect(0.6f, 0.0f, 0.9f, 0.5f) };
            // The detector's "person" box around that hand.
            Assert.That(PersonMath.IsOwnBody(Rect.MinMaxRect(0.58f, 0.0f, 0.92f, 0.55f), hand), Is.True);
            // Someone standing across the room, partly behind the hand, is kept.
            Assert.That(PersonMath.IsOwnBody(Rect.MinMaxRect(0.4f, 0.1f, 0.7f, 0.9f), hand), Is.False);
            // Someone elsewhere, or no hands tracked: kept.
            Assert.That(PersonMath.IsOwnBody(Rect.MinMaxRect(0.1f, 0.1f, 0.3f, 0.8f), hand), Is.False);
            Assert.That(PersonMath.IsOwnBody(Rect.MinMaxRect(0.6f, 0.0f, 0.9f, 0.5f), new Rect[0]), Is.False);
        }

        [Test]
        public void IsOwnBody_AddsBothHands()
        {
            // Both hands together in front of the face make one box; each hand covers only part of it.
            var hands = new[] { Rect.MinMaxRect(0.3f, 0.2f, 0.5f, 0.6f), Rect.MinMaxRect(0.5f, 0.2f, 0.7f, 0.6f) };
            Assert.That(PersonMath.IsOwnBody(Rect.MinMaxRect(0.3f, 0.2f, 0.7f, 0.6f), hands), Is.True);
        }

        [Test]
        public void Threat_RisesWithProximityAndApproach()
        {
            Assert.That(PersonMath.Threat(float.NaN, 0f), Is.EqualTo(ThreatLevel.Unknown));
            Assert.That(PersonMath.Threat(4f, 0f), Is.EqualTo(ThreatLevel.Low));
            Assert.That(PersonMath.Threat(2f, 0f), Is.EqualTo(ThreatLevel.Medium));
            Assert.That(PersonMath.Threat(1f, 0f), Is.EqualTo(ThreatLevel.High));
            // Coming closer raises it one level, but never past high; moving away doesn't lower it.
            Assert.That(PersonMath.Threat(4f, 0.5f), Is.EqualTo(ThreatLevel.Medium));
            Assert.That(PersonMath.Threat(1f, 0.5f), Is.EqualTo(ThreatLevel.High));
            Assert.That(PersonMath.Threat(2f, -1f), Is.EqualTo(ThreatLevel.Medium));
        }

        [Test]
        public void IsTargeted_HoldsBetweenTheThresholds()
        {
            Assert.That(PersonMath.IsTargeted(false, 0.65f), Is.False);
            Assert.That(PersonMath.IsTargeted(false, 0.75f), Is.True);
            Assert.That(PersonMath.IsTargeted(true, 0.65f), Is.True);
            Assert.That(PersonMath.IsTargeted(true, 0.55f), Is.False);
        }

        [Test]
        public void ThreatHud_ShowsTargetRangeAndLevel()
        {
            Assert.That(PersonMath.ThreatHud(0.87f, 2.34f, 0f, SpeechLanguage.English),
                Is.EqualTo("TARGET: HUMAN\nMATCH 87%\nRANGE 2.3 M\nTHREAT LEVEL: MEDIUM"));
            Assert.That(PersonMath.ThreatHud(0.9f, 1f, 0.6f, SpeechLanguage.Italian),
                Is.EqualTo("BERSAGLIO: UMANO\nCORRISPONDENZA 90%\nDISTANZA 1,0 M\nIN AVVICINAMENTO\nLIVELLO DI MINACCIA: ALTO"));
            Assert.That(PersonMath.ThreatHud(0.8f, float.NaN, 0f, SpeechLanguage.English),
                Does.Contain("RANGE --").And.EndsWith("THREAT LEVEL: UNKNOWN"));
            Assert.That(PersonMath.ThreatHud(0.8f, 1f, 0f, SpeechLanguage.English, dimThreat: true),
                Does.EndWith("<alpha=#33>THREAT LEVEL: HIGH<alpha=#FF>"));
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
