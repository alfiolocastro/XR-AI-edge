using System.Linq;
using AIEdge.TinyGarden;
using NUnit.Framework;

namespace AIEdge.Tests
{
    public class GardenStateTests
    {
        GardenState m_Garden;

        [SetUp]
        public void SetUp() => m_Garden = new GardenState();

        void GrowToFlower(params int[] plots)
        {
            m_Garden.Water(plots);
            m_Garden.Tick(GardenState.GrowSeconds);
            m_Garden.Water(plots);
            m_Garden.Tick(GardenState.GrowSeconds);
        }

        [Test]
        public void FullCycle_PlantWaterGrowHarvest()
        {
            m_Garden.Plant(Seed.Rose, new[] { 5 });
            Assert.AreEqual(PlotStage.Seed, m_Garden[5].stage);

            m_Garden.Water(new[] { 5 });
            Assert.AreEqual(PlotStage.Sprout, m_Garden[5].stage);
            m_Garden.Tick(GardenState.GrowSeconds - 0.1f);
            Assert.AreEqual(PlotStage.Sprout, m_Garden[5].stage);
            m_Garden.Tick(0.2f);
            Assert.AreEqual(PlotStage.Thirsty, m_Garden[5].stage);

            m_Garden.Water(new[] { 5 });
            Assert.AreEqual(PlotStage.Bud, m_Garden[5].stage);
            m_Garden.Tick(GardenState.GrowSeconds);
            Assert.AreEqual(PlotStage.Flower, m_Garden[5].stage);

            var events = m_Garden.Harvest(new[] { 5 });
            Assert.IsTrue(events.Single().ok);
            Assert.AreEqual(PlotStage.Empty, m_Garden[5].stage);
            Assert.AreEqual(1, m_Garden.HarvestedCount(Seed.Rose));
        }

        [Test]
        public void Plant_RejectsOccupiedAndInvalidPlots()
        {
            m_Garden.Plant(Seed.Daisy, new[] { 1 });
            var events = m_Garden.Plant(Seed.Rose, new[] { 1, 2, 12 });
            Assert.IsFalse(events.Single(e => e.plot == 1).ok);
            Assert.IsTrue(events.Single(e => e.plot == 2).ok);
            Assert.IsFalse(events.Single(e => e.plot == 12).ok);
            Assert.AreEqual(Seed.Daisy, m_Garden[1].seed);
        }

        [Test]
        public void Harvest_RejectsUnripe()
        {
            m_Garden.Plant(Seed.Sunflower, new[] { 3 });
            Assert.IsFalse(m_Garden.Harvest(new[] { 3 }).Single().ok);
            Assert.IsFalse(m_Garden.Harvest(new[] { 4 }).Single().ok);
        }

        [Test]
        public void Special_UnlocksAfterHarvests()
        {
            Assert.IsFalse(m_Garden.Plant(Seed.Special, new[] { 9 }).Single().ok);
            m_Garden.Plant(Seed.Daisy, new[] { 1, 2, 3 });
            GrowToFlower(1, 2, 3);
            m_Garden.Harvest(new[] { 1, 2, 3 });
            Assert.IsTrue(m_Garden.SpecialUnlocked);
            Assert.IsTrue(m_Garden.Plant(Seed.Special, new[] { 9 }).Single().ok);
        }

        [Test]
        public void Changed_RaisedOnlyWhenSomethingChanges()
        {
            var count = 0;
            m_Garden.Changed += () => count++;
            m_Garden.Water(new[] { 1 });
            Assert.AreEqual(0, count);
            m_Garden.Plant(Seed.Rose, new[] { 1 });
            Assert.AreEqual(1, count);
        }

        [Test]
        public void Apply_ParsesModelToolCalls()
        {
            var plant = GardenToolCall.Parse("plant_seed", "{\"seed\":\"sunflower\",\"plots\":[1,2,3]}");
            Assert.AreEqual(3, m_Garden.Apply(plant).Count(e => e.ok));
            Assert.AreEqual("plant_seed(sunflower, [1, 2, 3])", plant.ToString());

            Assert.IsTrue(m_Garden.Apply(GardenToolCall.Parse("water_plots", "{\"plots\":[2]}")).Single().ok);
            Assert.IsFalse(m_Garden.Apply(GardenToolCall.Parse("plant_seed", "{\"seed\":\"cactus\",\"plots\":[4]}")).Single().ok);
            Assert.IsFalse(m_Garden.Apply(GardenToolCall.Parse("dig", "not json")).Single().ok);
        }

        [Test]
        public void TryParseSeed_AcceptsGalleryAliasesAndPlurals()
        {
            Assert.IsTrue(GardenState.TryParseSeed("Sunflowers", out var a) && a == Seed.Sunflower);
            Assert.IsTrue(GardenState.TryParseSeed("daisies", out var b) && b == Seed.Daisy);
            Assert.IsTrue(GardenState.TryParseSeed("edge gallery", out var c) && c == Seed.Special);
            Assert.IsFalse(GardenState.TryParseSeed("tulip", out _));
        }
    }

    public class DeicticResolverTests
    {
        [Test]
        public void Resolve_ReplacesPointingWords()
        {
            Assert.AreEqual("water plots 4 and 5", DeicticResolver.Resolve("water these", new[] { 5, 4 }));
            Assert.AreEqual("plant a rose in plot 5", DeicticResolver.Resolve("plant a rose here", new[] { 5 }));
            Assert.AreEqual("harvest plots 1, 2 and 3", DeicticResolver.Resolve("harvest those flowers", new[] { 1, 2, 3 }));
            Assert.AreEqual("water plot 7", DeicticResolver.Resolve("water right here", new[] { 7 }));
        }

        [Test]
        public void ExplicitPlots_ReadsWrittenPlotNumbers()
        {
            CollectionAssert.AreEqual(new[] { 9 }, DeicticResolver.ExplicitPlots("plant the special seed in plot 9"));
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, DeicticResolver.ExplicitPlots("water plots 1, 2 and 3"));
            CollectionAssert.AreEqual(new[] { 4, 6 }, DeicticResolver.ExplicitPlots("harvest plot 6 and plot 4"));
            CollectionAssert.IsEmpty(DeicticResolver.ExplicitPlots("water the top row"));
        }

        [Test]
        public void Resolve_LeavesTextAloneWithoutPointingOrDeixis()
        {
            Assert.AreEqual("water these", DeicticResolver.Resolve("water these", new int[0]));
            Assert.AreEqual("water the top row", DeicticResolver.Resolve("water the top row", new[] { 5 }));
            Assert.AreEqual("plant daisies in the left column", DeicticResolver.Resolve("plant daisies in the left column", new[] { 2 }));
        }
    }
}
