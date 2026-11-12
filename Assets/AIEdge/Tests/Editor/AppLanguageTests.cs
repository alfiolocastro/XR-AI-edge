using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace AIEdge.Tests
{
    public class AppLanguageTests
    {
        const string k_Key = "AIEdge.Language";
        string m_Saved;
        string m_InstructionsPath;

        [SetUp]
        public void SetUp()
        {
            m_Saved = PlayerPrefs.GetString(k_Key, null);
            PlayerPrefs.DeleteKey(k_Key);
            AppLanguage.ResetForTests();
            m_InstructionsPath = Path.Combine(Path.GetTempPath(), "AIEdgeLanguage_" + Path.GetRandomFileName() + ".json");
            UserInstructions.SetPathForTests(m_InstructionsPath);
        }

        [TearDown]
        public void TearDown()
        {
            if (m_Saved != null)
                PlayerPrefs.SetString(k_Key, m_Saved);
            else
                PlayerPrefs.DeleteKey(k_Key);
            AppLanguage.ResetForTests();
            UserInstructions.SetPathForTests(null);
            File.Delete(m_InstructionsPath);
        }

        [Test]
        public void Set_PersistsAndRaisesChangedOnce()
        {
            var raised = 0;
            void OnChanged() => raised++;
            AppLanguage.Changed += OnChanged;
            AppLanguage.Set(SpeechLanguage.Italian);
            AppLanguage.Set(SpeechLanguage.Italian);
            AppLanguage.Set("fr");
            AppLanguage.Changed -= OnChanged;

            Assert.AreEqual(1, raised);
            AppLanguage.ResetForTests();
            Assert.AreEqual(SpeechLanguage.Italian, AppLanguage.Current);
            Assert.IsTrue(AppLanguage.IsItalian);
        }

        [Test]
        public void SystemPrompt_PutsLanguageRuleLast()
        {
            UserInstructions.Set("Talk like a pirate.", true);
            var italian = AppLanguage.SystemPrompt("You are helpful.", SpeechLanguage.Italian);
            StringAssert.StartsWith("You are helpful.", italian);
            StringAssert.Contains("Talk like a pirate.", italian);
            StringAssert.EndsWith(AppLanguage.ReplyRule(SpeechLanguage.Italian), italian);
            Assert.Less(italian.IndexOf("pirate"), italian.IndexOf("Rispondi"));

            UserInstructions.Set("Talk like a pirate.", false);
            Assert.AreEqual(AppLanguage.ReplyRule(SpeechLanguage.English), AppLanguage.SystemPrompt("", SpeechLanguage.English));
        }

        [Test]
        public void ForModel_RemindsOnlyInItalian()
        {
            const string question = "Dimmi due cose sulla luna.";
            StringAssert.StartsWith(question, AppLanguage.ForModel(question, SpeechLanguage.Italian));
            StringAssert.EndsWith("nessuna parola in inglese.)", AppLanguage.ForModel(question, SpeechLanguage.Italian));
            Assert.AreEqual(question, AppLanguage.ForModel(question, SpeechLanguage.English));
        }

        [Test]
        public void WhisperPack_DownloadsOnlyInt8Files()
        {
            var whisper = VoiceCatalog.Whisper;
            Assert.IsTrue(whisper.Needs("base-encoder.int8.onnx"));
            Assert.IsTrue(whisper.Needs("base-tokens.txt"));
            Assert.IsFalse(whisper.Needs("base-encoder.onnx"));
            Assert.IsFalse(whisper.Needs("test_wavs/0.wav"));
            CollectionAssert.DoesNotContain(VoiceCatalog.All, whisper);
            Assert.IsTrue(VoiceCatalog.Alba.Needs("tokens.txt"));
        }
    }
}
