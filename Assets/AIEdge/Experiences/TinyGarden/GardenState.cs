using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace AIEdge.TinyGarden
{
    /// <summary>Seeds the model can plant (the Gallery's four kinds).</summary>
    public enum Seed
    {
        None,
        Sunflower,
        Daisy,
        Rose,
        Special,
    }

    /// <summary>
    /// Growth of one plot: seed → (water) → sprout → thirsty → (water) → bud → flower → (harvest) → empty.
    /// </summary>
    public enum PlotStage
    {
        Empty,
        Seed,
        Sprout,
        Thirsty,
        Bud,
        Flower,
    }

    /// <summary>One garden plot.</summary>
    [Serializable]
    public class Plot
    {
        public Seed seed;
        public PlotStage stage;

        /// <summary>Seconds of growth left while a sprout or bud grows after watering.</summary>
        public float growthLeft;

        /// <summary>True while freshly watered soil is still wet (the plant is growing).</summary>
        public bool IsWet => stage == PlotStage.Sprout || stage == PlotStage.Bud;
    }

    /// <summary>What happened to one plot when an action was applied.</summary>
    public struct GardenEvent
    {
        /// <summary>Plot number 1-9, or 0 for the whole action.</summary>
        public int plot;
        public bool ok;
        public string message;

        public override string ToString() => plot > 0 ? $"plot {plot}: {message}" : message;
    }

    /// <summary>A tool call from the Tiny Garden model.</summary>
    [Serializable]
    public class GardenToolCall
    {
        public string name;
        public string seed;
        public int[] plots;

        /// <summary>Parses a tool name and its JSON arguments. Unknown or malformed arguments leave fields empty.</summary>
        public static GardenToolCall Parse(string name, string argsJson)
        {
            GardenToolCall call;
            try
            {
                call = JsonUtility.FromJson<GardenToolCall>(string.IsNullOrEmpty(argsJson) ? "{}" : argsJson) ?? new GardenToolCall();
            }
            catch (ArgumentException)
            {
                call = new GardenToolCall();
            }

            call.name = name;
            call.plots ??= Array.Empty<int>();
            return call;
        }

        public override string ToString() =>
            string.IsNullOrEmpty(seed) ? $"{name}([{string.Join(", ", plots)}])" : $"{name}({seed}, [{string.Join(", ", plots)}])";
    }

    /// <summary>
    /// The Tabletop Garden game: a 3×3 grid of plots numbered 1-9 like the Gallery's (1-3 top row, 7-9 bottom row).
    /// The model's tools and the user's hands both act through this state. Plain C# so it can be unit tested.
    /// </summary>
    public class GardenState
    {
        public const int PlotCount = 9;
        public const float GrowSeconds = 6f;
        public const int SpecialUnlockHarvests = 3;

        readonly Plot[] m_Plots = new Plot[PlotCount];
        readonly int[] m_Harvested = new int[Enum.GetValues(typeof(Seed)).Length];

        /// <summary>Raised whenever any plot or the harvest count changes.</summary>
        public event Action Changed;

        public GardenState()
        {
            for (var i = 0; i < PlotCount; i++)
                m_Plots[i] = new Plot();
        }

        /// <summary>A plot by number, 1-9.</summary>
        public Plot this[int number] => m_Plots[number - 1];

        /// <summary>Flowers harvested so far of one kind.</summary>
        public int HarvestedCount(Seed seed) => m_Harvested[(int)seed];

        /// <summary>Flowers harvested so far of all kinds.</summary>
        public int TotalHarvested => m_Harvested.Sum();

        /// <summary>The special seed unlocks after a few harvests.</summary>
        public bool SpecialUnlocked => TotalHarvested >= SpecialUnlockHarvests;

        /// <summary>Empties every plot and clears the harvest.</summary>
        public void Reset()
        {
            for (var i = 0; i < PlotCount; i++)
                m_Plots[i] = new Plot();
            Array.Clear(m_Harvested, 0, m_Harvested.Length);
            Changed?.Invoke();
        }

        /// <summary>Applies a model tool call.</summary>
        public List<GardenEvent> Apply(GardenToolCall call)
        {
            switch (call.name)
            {
                case "plant_seed":
                    return TryParseSeed(call.seed, out var seed)
                        ? Plant(seed, call.plots)
                        : new List<GardenEvent> { new GardenEvent { message = $"There's no \"{call.seed}\" seed. Try sunflower, daisy, rose or special." } };
                case "water_plots":
                    return Water(call.plots);
                case "harvest_plots":
                    return Harvest(call.plots);
                default:
                    return new List<GardenEvent> { new GardenEvent { message = $"Unknown action {call.name}." } };
            }
        }

        /// <summary>Plants a seed in empty plots.</summary>
        public List<GardenEvent> Plant(Seed seed, IEnumerable<int> plots)
        {
            return ForEachPlot(plots, (number, plot) =>
            {
                if (seed == Seed.Special && !SpecialUnlocked)
                    return Fail(number, $"the special seed unlocks after {SpecialUnlockHarvests} harvests");
                if (plot.stage != PlotStage.Empty)
                    return Fail(number, $"already has a {Name(plot.seed)}");

                plot.seed = seed;
                plot.stage = PlotStage.Seed;
                return Ok(number, $"planted {Name(seed)}");
            });
        }

        /// <summary>Waters plots: a seed sprouts, a thirsty sprout grows a bud.</summary>
        public List<GardenEvent> Water(IEnumerable<int> plots)
        {
            return ForEachPlot(plots, (number, plot) =>
            {
                switch (plot.stage)
                {
                    case PlotStage.Empty:
                        return Fail(number, "nothing planted");
                    case PlotStage.Seed:
                        plot.stage = PlotStage.Sprout;
                        plot.growthLeft = GrowSeconds;
                        return Ok(number, $"{Name(plot.seed)} sprouting");
                    case PlotStage.Thirsty:
                        plot.stage = PlotStage.Bud;
                        plot.growthLeft = GrowSeconds;
                        return Ok(number, $"{Name(plot.seed)} budding");
                    case PlotStage.Flower:
                        return Fail(number, "already in bloom, harvest it");
                    default:
                        return Fail(number, "still wet, growing");
                }
            });
        }

        /// <summary>Harvests flowers in bloom, emptying their plots.</summary>
        public List<GardenEvent> Harvest(IEnumerable<int> plots)
        {
            return ForEachPlot(plots, (number, plot) =>
            {
                if (plot.stage == PlotStage.Empty)
                    return Fail(number, "nothing to harvest");
                if (plot.stage != PlotStage.Flower)
                    return Fail(number, $"{Name(plot.seed)} not in bloom yet");

                var seed = plot.seed;
                m_Harvested[(int)seed]++;
                plot.seed = Seed.None;
                plot.stage = PlotStage.Empty;
                return Ok(number, $"harvested {Name(seed)}");
            });
        }

        /// <summary>Advances growth: sprouts get thirsty and buds bloom after <see cref="GrowSeconds"/>.</summary>
        public void Tick(float deltaSeconds)
        {
            var changed = false;
            foreach (var plot in m_Plots)
            {
                if (!plot.IsWet)
                    continue;

                plot.growthLeft -= deltaSeconds;
                if (plot.growthLeft > 0f)
                    continue;

                plot.growthLeft = 0f;
                plot.stage = plot.stage == PlotStage.Sprout ? PlotStage.Thirsty : PlotStage.Flower;
                changed = true;
            }

            if (changed)
                Changed?.Invoke();
        }

        /// <summary>Maps a model seed name to a seed. Accepts the Gallery's aliases for the special seed.</summary>
        public static bool TryParseSeed(string name, out Seed seed)
        {
            switch ((name ?? "").Trim().ToLowerInvariant().TrimEnd('s'))
            {
                case "sunflower": seed = Seed.Sunflower; return true;
                case "daisy":
                case "daisie": seed = Seed.Daisy; return true;
                case "rose": seed = Seed.Rose; return true;
                case "special":
                case "secret":
                case "edge gallery": seed = Seed.Special; return true;
                default: seed = Seed.None; return false;
            }
        }

        /// <summary>
        /// A short spoken summary of what an action did, grouping plots with the same outcome:
        /// "Planted sunflower in plots 1 and 2. Plot 5: already has a rose."
        /// </summary>
        public static string Summarize(IEnumerable<GardenEvent> events)
        {
            var sentences = new List<string>();
            foreach (var group in events.GroupBy(e => (e.ok, e.message)))
            {
                var plots = group.Where(e => e.plot > 0).Select(e => e.plot).ToList();
                var message = group.Key.message;
                if (plots.Count == 0)
                    sentences.Add(message);
                else if (group.Key.ok)
                    sentences.Add($"{char.ToUpperInvariant(message[0])}{message.Substring(1)} in {DeicticResolver.FormatPlots(plots)}");
                else
                {
                    var where = DeicticResolver.FormatPlots(plots);
                    sentences.Add($"{char.ToUpperInvariant(where[0])}{where.Substring(1)}: {message}");
                }
            }

            return sentences.Count == 0 ? "" : string.Join(". ", sentences.Select(s => s.TrimEnd('.'))) + ".";
        }

        /// <summary>Lower-case seed name for messages.</summary>
        public static string Name(Seed seed) => seed == Seed.Special ? "special flower" : seed.ToString().ToLowerInvariant();

        List<GardenEvent> ForEachPlot(IEnumerable<int> plots, Func<int, Plot, GardenEvent> action)
        {
            var events = new List<GardenEvent>();
            var numbers = (plots ?? Enumerable.Empty<int>()).Distinct().OrderBy(n => n).ToList();
            if (numbers.Count == 0)
                events.Add(new GardenEvent { message = "No plots given." });

            foreach (var number in numbers)
                events.Add(number >= 1 && number <= PlotCount ? action(number, this[number]) : Fail(number, "is not a plot (1-9)"));

            if (events.Any(e => e.ok))
                Changed?.Invoke();
            return events;
        }

        static GardenEvent Ok(int plot, string message) => new GardenEvent { plot = plot, ok = true, message = message };

        static GardenEvent Fail(int plot, string message) => new GardenEvent { plot = plot, ok = false, message = message };
    }
}
