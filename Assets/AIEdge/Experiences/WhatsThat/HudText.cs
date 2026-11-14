using System;
using System.Globalization;
using System.Text;

namespace AIEdge.WhatsThat
{
    /// <summary>
    /// Text for the targeting display: the live analysis block from the detector's numbers and the decorative
    /// scrolling data rows. Monospaced capitals, so columns line up. Plain C# so it can be unit tested.
    /// </summary>
    public static class HudText
    {
        static readonly CultureInfo k_Invariant = CultureInfo.InvariantCulture;
        const string k_Letters = "ABCDEFGHJKLMNPRSTUVWXYZ";
        const string k_Hex = "0123456789ABCDEF";

        /// <summary>The analysis block: people and locked targets, nearest range and threat, detector speed.</summary>
        public static string Analysis(int people, int locked, float nearest, ThreatLevel threat, long inferMs, float fps)
        {
            var range = float.IsNaN(nearest) || nearest <= 0f ? " --.-" : nearest.ToString("00.0", k_Invariant) + "M";
            return $"SUBJECTS  {people,4}   LOCKED  {locked,4}\n" +
                $"RANGE    {range,5}   THREAT  {ThreatName(threat),6}\n" +
                $"INFER  {inferMs,4}MS   RATE   {fps.ToString("0.0", k_Invariant),4}HZ";
        }

        /// <summary>The boxed line under MATCH: the nearest locked target, else what the system is doing.</summary>
        public static string Match(bool anyone, bool locked, ThreatLevel threat) =>
            locked ? $"HUMAN  ·  THREAT {ThreatName(threat)}" : anyone ? "ACQUIRING TARGET" : "SCANNING";

        /// <summary>Clock for the uptime readout.</summary>
        public static string Uptime(float seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0f, seconds));
            return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
        }

        /// <summary>Short name of a threat level.</summary>
        public static string ThreatName(ThreatLevel threat) => threat switch
        {
            ThreatLevel.Low => "LOW",
            ThreatLevel.Medium => "MEDIUM",
            ThreatLevel.High => "HIGH",
            _ => "--",
        };

        /// <summary>A decorative numeric row for the left column: four number groups.</summary>
        public static string NumberRow(Random random) =>
            $"{random.Next(10000000, 100000000)} {random.Next(100, 1000)} {random.Next(10, 100)} {random.Next(1000, 10000)}";

        /// <summary>A decorative log row for the right column: index, code, register and value.</summary>
        public static string LogRow(Random random, int index)
        {
            var code = new StringBuilder(4);
            for (var i = 0; i < 4; i++)
                code.Append(k_Hex[random.Next(k_Hex.Length)]);
            var register = $"{k_Letters[random.Next(k_Letters.Length)]}{random.Next(10, 100)}";
            return $"{index % 1000:000} {code} {register} {random.Next(10000000, 100000000)}";
        }
    }
}
