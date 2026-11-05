using System.Collections.Generic;
using System.Linq;
using AIEdge.TinyGarden;
using NUnit.Framework;

namespace AIEdge.Tests
{
    public class SpeechTextTests
    {
        static List<string> Stream(params string[] tokens)
        {
            var speech = new SpeechText();
            var spoken = new List<string>();
            foreach (var token in tokens)
                spoken.AddRange(speech.Append(token));
            spoken.AddRange(speech.Flush());
            return spoken;
        }

        [Test]
        public void Clean_StripsMarkdown()
        {
            Assert.AreEqual("Title", SpeechText.Clean("## Title"));
            Assert.AreEqual("bold and italic and code", SpeechText.Clean("**bold** and *italic* and `code`"));
            Assert.AreEqual("first item", SpeechText.Clean("- first item"));
            Assert.AreEqual("see the docs", SpeechText.Clean("see [the docs](https://example.com)"));
            Assert.AreEqual("call water_plots now", SpeechText.Clean("call water_plots now"));
            Assert.AreEqual("", SpeechText.Clean("***"));
        }

        [Test]
        public void Append_SpeaksSentencesAsTheyComplete()
        {
            var speech = new SpeechText();
            CollectionAssert.IsEmpty(speech.Append("Hello the"));
            CollectionAssert.IsEmpty(speech.Append("re."));
            CollectionAssert.AreEqual(new[] { "Hello there." }, speech.Append(" How"));
            CollectionAssert.IsEmpty(speech.Append(" are you?"));
            CollectionAssert.AreEqual(new[] { "How are you?" }, speech.Flush());
        }

        [Test]
        public void Append_DoesNotSplitDecimals()
        {
            CollectionAssert.AreEqual(new[] { "It is about 2.0 m wide." }, Stream("It is about 2.", "0 m wide."));
        }

        [Test]
        public void Append_SplitsListLinesAndSkipsCode()
        {
            var spoken = Stream("Steps:\n", "1. **Mix** it\n", "```python\n", "print(1)\n", "```\n", "Done.");
            CollectionAssert.AreEqual(new[] { "Steps:", "Mix it", "Done." }, spoken);
        }

        [Test]
        public void Append_BreaksLongRunsWithoutPunctuation()
        {
            var words = string.Join(" ", Enumerable.Repeat("word", 120));
            var spoken = Stream(words);
            Assert.Greater(spoken.Count, 1);
            Assert.That(spoken.All(s => s.Length <= 280));
        }
    }

    public class GardenSummaryTests
    {
        [Test]
        public void Summarize_GroupsPlotsByOutcome()
        {
            var events = new[]
            {
                new GardenEvent { plot = 1, ok = true, message = "planted sunflower" },
                new GardenEvent { plot = 2, ok = true, message = "planted sunflower" },
                new GardenEvent { plot = 5, ok = false, message = "already has a rose" },
            };
            Assert.AreEqual("Planted sunflower in plots 1 and 2. Plot 5: already has a rose.", GardenState.Summarize(events));
            Assert.AreEqual("No plots given.", GardenState.Summarize(new[] { new GardenEvent { message = "No plots given." } }));
        }
    }
}
