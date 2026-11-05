using NUnit.Framework;

namespace AIEdge.Tests
{
    public class SpeechLanguageTests
    {
        [TestCase("The Moon is about 384,000 km away from the Earth.", "en")]
        [TestCase("What can you see in this picture?", "en")]
        [TestCase("Planted sunflower in plots 1 and 2.", "en")]
        [TestCase("La Luna è a circa 384.000 km dalla Terra.", "it")]
        [TestCase("Non ci sono nuvole e il cielo è sereno.", "it")]
        [TestCase("Ecco cosa vedo nell'immagine: una tazza di caffè.", "it")]
        [TestCase("L'acqua bolle a cento gradi.", "it")]
        public void Detect_RecognizesLanguage(string text, string expected)
        {
            Assert.AreEqual(expected, SpeechLanguage.Detect(text));
        }

        [TestCase("")]
        [TestCase("   ")]
        [TestCase("42.")]
        [TestCase("Gemma 4 E2B")]
        public void Detect_ReturnsNullWithoutSignal(string text)
        {
            Assert.IsNull(SpeechLanguage.Detect(text));
        }

        [Test]
        public void Catalog_HasOneVoicePerLanguage()
        {
            Assert.AreEqual(VoiceCatalog.Alba, VoiceCatalog.ForLanguage(SpeechLanguage.English));
            Assert.AreEqual(VoiceCatalog.Paola, VoiceCatalog.ForLanguage(SpeechLanguage.Italian));
            Assert.IsNull(VoiceCatalog.ForLanguage("de"));
            foreach (var voice in VoiceCatalog.All)
            {
                Assert.AreEqual(40, voice.commitHash.Length, voice.id);
                Assert.Greater(voice.sizeBytes, 0, voice.id);
            }
        }
    }
}
