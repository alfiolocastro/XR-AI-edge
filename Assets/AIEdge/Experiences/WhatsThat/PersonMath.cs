using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace AIEdge.WhatsThat
{
    /// <summary>A person found in a camera frame.</summary>
    [Serializable]
    public struct PersonBox
    {
        /// <summary>Box in camera viewport coordinates: normalized, origin at the bottom-left (as MRUK's viewport).</summary>
        public Rect Viewport;

        /// <summary>Detector confidence, 0 to 1.</summary>
        public float Score;
    }

    /// <summary>How close a person is, for the targeting display.</summary>
    public enum ThreatLevel
    {
        Unknown,
        Low,
        Medium,
        High,
    }

    /// <summary>
    /// Geometry for person detection: converting the detector's boxes to camera viewport rectangles, matching boxes
    /// between frames so their frames move smoothly instead of flickering, and the summary line. Plain C# so it can be
    /// unit tested.
    /// </summary>
    public static class PersonMath
    {
        /// <summary>
        /// A detector box (normalized, origin at the top-left, as image rows run top to bottom) as a viewport
        /// rectangle (origin at the bottom-left).
        /// </summary>
        public static Rect ToViewport(float x, float y, float width, float height) =>
            new Rect(x, 1f - y - height, width, height);

        /// <summary>Intersection over union of two rectangles, 0 to 1.</summary>
        public static float Overlap(Rect a, Rect b)
        {
            var xMin = Mathf.Max(a.xMin, b.xMin);
            var yMin = Mathf.Max(a.yMin, b.yMin);
            var xMax = Mathf.Min(a.xMax, b.xMax);
            var yMax = Mathf.Min(a.yMax, b.yMax);
            if (xMax <= xMin || yMax <= yMin)
                return 0f;
            var inter = (xMax - xMin) * (yMax - yMin);
            return inter / (a.width * a.height + b.width * b.height - inter);
        }

        /// <summary>
        /// Pairs each new box with the tracked box it overlaps most (at least <paramref name="minOverlap"/>), greedily
        /// by overlap. Returns, for each new box, the index of its tracked box, or -1 for a new person.
        /// </summary>
        public static int[] Match(IReadOnlyList<Rect> tracked, IReadOnlyList<Rect> found, float minOverlap = 0.2f)
        {
            var result = new int[found.Count];
            for (var i = 0; i < result.Length; i++)
                result[i] = -1;

            var pairs = new List<(float overlap, int track, int box)>();
            for (var t = 0; t < tracked.Count; t++)
            for (var f = 0; f < found.Count; f++)
            {
                var overlap = Overlap(tracked[t], found[f]);
                if (overlap >= minOverlap)
                    pairs.Add((overlap, t, f));
            }

            pairs.Sort((a, b) => b.overlap.CompareTo(a.overlap));
            var usedTracks = new HashSet<int>();
            foreach (var (_, track, box) in pairs)
            {
                if (result[box] >= 0 || usedTracks.Contains(track))
                    continue;
                result[box] = track;
                usedTracks.Add(track);
            }

            return result;
        }

        /// <summary>
        /// Viewport points to probe with depth for a person's distance: the middle of the box (the torso), then a bit
        /// above and below, so a gap between arms and body still finds them.
        /// </summary>
        public static IEnumerable<Vector2> DepthProbes(Rect viewport)
        {
            yield return viewport.center;
            yield return new Vector2(viewport.center.x, viewport.yMin + viewport.height * 0.65f);
            yield return new Vector2(viewport.center.x, viewport.yMin + viewport.height * 0.35f);
            yield return new Vector2(viewport.xMin + viewport.width * 0.35f, viewport.center.y);
            yield return new Vector2(viewport.xMin + viewport.width * 0.65f, viewport.center.y);
        }

        /// <summary>The label above a person's frame: "Persona · 2,3 m" / "Person · 2.3 m".</summary>
        public static string Label(float distance, string language)
        {
            var italian = language == SpeechLanguage.Italian;
            var name = italian ? "Persona" : "Person";
            if (float.IsNaN(distance) || distance <= 0f)
                return name;
            var culture = italian ? CultureInfo.GetCultureInfo("it-IT") : CultureInfo.InvariantCulture;
            return $"{name} · {distance.ToString("0.0", culture)} m";
        }

        /// <summary>The panel's summary: how many people are in view and how far, nearest first.</summary>
        public static string Summary(IReadOnlyList<float> distances, string language)
        {
            var italian = language == SpeechLanguage.Italian;
            if (distances.Count == 0)
                return italian ? "Nessuna persona in vista" : "No people in view";
            var culture = italian ? CultureInfo.GetCultureInfo("it-IT") : CultureInfo.InvariantCulture;
            var sorted = new List<float>(distances);
            // Nearest first; people without a distance last.
            sorted.Sort((a, b) => float.IsNaN(a) ? (float.IsNaN(b) ? 0 : 1) : float.IsNaN(b) ? -1 : a.CompareTo(b));
            var parts = new List<string>();
            foreach (var d in sorted)
                parts.Add(float.IsNaN(d) || d <= 0f ? "?" : $"{d.ToString("0.0", culture)} m");
            var count = distances.Count;
            var head = italian
                ? (count == 1 ? "1 persona in vista" : $"{count} persone in vista")
                : (count == 1 ? "1 person in view" : $"{count} people in view");
            return $"{head}: {string.Join(" · ", parts)}";
        }

        /// <summary>The viewport rectangle around a set of projected points (null when there are none).</summary>
        public static Rect? Region(IReadOnlyList<Vector2> points)
        {
            if (points.Count == 0)
                return null;
            var min = points[0];
            var max = points[0];
            foreach (var p in points)
            {
                min = Vector2.Min(min, p);
                max = Vector2.Max(max, p);
            }

            return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }

        /// <summary>
        /// True when a box is mostly the user's own body: at least <paramref name="minCovered"/> of it lies within the
        /// regions of their hands and forearms (or controllers). The detector readily takes a hand held up in front of
        /// the headset for a person, and depth can't measure it (the depth map leaves hands out), so its distance is
        /// the wall behind.
        /// </summary>
        public static bool IsOwnBody(Rect box, IReadOnlyList<Rect> regions, float minCovered = 0.45f)
        {
            var area = box.width * box.height;
            if (area <= 0f || regions.Count == 0)
                return false;
            var covered = 0f;
            foreach (var region in regions)
            {
                var w = Mathf.Min(box.xMax, region.xMax) - Mathf.Max(box.xMin, region.xMin);
                var h = Mathf.Min(box.yMax, region.yMax) - Mathf.Max(box.yMin, region.yMin);
                if (w > 0f && h > 0f)
                    covered += w * h;
            }

            return covered / area >= minCovered;
        }

        /// <summary>Smoothed score at or above which a person is targeted.</summary>
        public const float TargetScore = 0.7f;

        /// <summary>Smoothed score below which a targeted person is let go (lower, so the display doesn't flicker).</summary>
        public const float ReleaseScore = 0.6f;

        /// <summary>Approach speed, in meters a second, above which a person counts as approaching.</summary>
        public const float ApproachingSpeed = 0.3f;

        /// <summary>Whether a person is targeted, with hysteresis between <see cref="TargetScore"/> and <see cref="ReleaseScore"/>.</summary>
        public static bool IsTargeted(bool wasTargeted, float smoothedScore) =>
            smoothedScore >= (wasTargeted ? ReleaseScore : TargetScore);

        /// <summary>
        /// Threat level from proximity alone (nothing about who the person is): within 1.2 m high, within 2.5 m medium,
        /// farther low; one level up while they approach.
        /// </summary>
        public static ThreatLevel Threat(float distance, float approachSpeed)
        {
            if (float.IsNaN(distance) || distance <= 0f)
                return ThreatLevel.Unknown;
            var level = distance < 1.2f ? ThreatLevel.High : distance < 2.5f ? ThreatLevel.Medium : ThreatLevel.Low;
            if (approachSpeed > ApproachingSpeed && level < ThreatLevel.High)
                level++;
            return level;
        }

        /// <summary>
        /// The targeting display beside a high-confidence person: target, match, range, approach and threat level, in
        /// capitals. With <paramref name="dimThreat"/> the threat line is dimmed (it blinks when high).
        /// </summary>
        public static string ThreatHud(float score, float distance, float approachSpeed, string language, bool dimThreat = false)
        {
            var italian = language == SpeechLanguage.Italian;
            var culture = italian ? CultureInfo.GetCultureInfo("it-IT") : CultureInfo.InvariantCulture;
            var level = Threat(distance, approachSpeed);
            var range = float.IsNaN(distance) || distance <= 0f ? "--" : $"{distance.ToString("0.0", culture)} M";
            var match = Mathf.RoundToInt(Mathf.Clamp01(score) * 100f);
            string levelName;
            switch (level)
            {
                case ThreatLevel.Low: levelName = italian ? "BASSO" : "LOW"; break;
                case ThreatLevel.Medium: levelName = italian ? "MEDIO" : "MEDIUM"; break;
                case ThreatLevel.High: levelName = italian ? "ALTO" : "HIGH"; break;
                default: levelName = italian ? "SCONOSCIUTO" : "UNKNOWN"; break;
            }

            var lines = new List<string>
            {
                italian ? "BERSAGLIO: UMANO" : "TARGET: HUMAN",
                (italian ? "CORRISPONDENZA " : "MATCH ") + match + "%",
                (italian ? "DISTANZA " : "RANGE ") + range,
            };
            if (approachSpeed > ApproachingSpeed && level != ThreatLevel.Unknown)
                lines.Add(italian ? "IN AVVICINAMENTO" : "APPROACHING");
            var threat = (italian ? "LIVELLO DI MINACCIA: " : "THREAT LEVEL: ") + levelName;
            lines.Add(dimThreat ? $"<alpha=#33>{threat}<alpha=#FF>" : threat);
            return string.Join("\n", lines);
        }
    }
}
