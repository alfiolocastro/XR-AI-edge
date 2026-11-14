using AIEdge.TinyGarden;
using NUnit.Framework;
using UnityEngine;

namespace AIEdge.Tests
{
    public class GardenDropTests
    {
        [Test]
        public void DropPlot_PlantsOverAPlotCloseToTheSoil()
        {
            var center = GardenView.PlotLocalPosition(5);
            Assert.That(GardenView.DropPlot(center + Vector3.up * 0.1f), Is.EqualTo(5));
            Assert.That(GardenView.DropPlot(GardenView.PlotLocalPosition(1) + Vector3.up * 0.25f), Is.EqualTo(1));
        }

        [Test]
        public void DropPlot_IgnoresTooHighOrOffThePlanter()
        {
            var center = GardenView.PlotLocalPosition(5);
            Assert.That(GardenView.DropPlot(center + Vector3.up * 0.35f), Is.EqualTo(0));
            // The packets' home beside the planter.
            Assert.That(GardenView.DropPlot(new Vector3(0.28f, 0.04f, 0.15f)), Is.EqualTo(0));
            // Past the edge of the planter.
            Assert.That(GardenView.DropPlot(GardenView.PlotLocalPosition(6) + new Vector3(GardenView.PlotSize, 0.05f, 0f)), Is.EqualTo(0));
        }
    }
}
