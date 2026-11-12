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
    }
}
