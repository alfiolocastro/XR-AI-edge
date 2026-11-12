using System.IO;
using AIEdge.WhatsThat;
using NUnit.Framework;

namespace AIEdge.Tests
{
    public class UserInstructionsTests
    {
        string m_Path;

        [SetUp]
        public void SetUp()
        {
            m_Path = Path.Combine(Path.GetTempPath(), "AIEdgeInstructions_" + Path.GetRandomFileName() + ".json");
            UserInstructions.SetPathForTests(m_Path);
        }

        [TearDown]
        public void TearDown()
        {
            UserInstructions.SetPathForTests(null);
            File.Delete(m_Path);
        }

        [Test]
        public void Compose_AppendsInstructionsAfterPersona()
        {
            Assert.AreEqual("Be brief.", UserInstructions.Compose("Be brief.", null));
            var composed = UserInstructions.Compose("Be brief.", "I'm on a diet.");
            StringAssert.StartsWith("Be brief.\n\n", composed);
            StringAssert.EndsWith("I'm on a diet.", composed);
            StringAssert.Contains("personal instructions", composed);
            StringAssert.StartsWith("The user gave you", UserInstructions.Compose("", "I'm on a diet."));
        }

        [Test]
        public void Set_PersistsTrimsAndRespectsOff()
        {
            Assert.IsFalse(UserInstructions.IsActive);
            Assert.AreEqual("None", UserInstructions.Summary());

            UserInstructions.Set("  I'm on a strict diet.  ", true);
            UserInstructions.SetPathForTests(m_Path); // reload from disk
            Assert.AreEqual("I'm on a strict diet.", UserInstructions.Text);
            Assert.IsTrue(UserInstructions.IsActive);
            StringAssert.Contains("I'm on a strict diet.", UserInstructions.Compose(""));

            UserInstructions.Set("I'm on a strict diet.", false);
            Assert.IsFalse(UserInstructions.IsActive);
            Assert.AreEqual("", UserInstructions.Compose(""));
            StringAssert.StartsWith("Off · ", UserInstructions.Summary());
        }

        [Test]
        public void Set_CapsLength()
        {
            UserInstructions.Set(new string('x', UserInstructions.MaxLength + 50), true);
            Assert.AreEqual(UserInstructions.MaxLength, UserInstructions.Text.Length);
            Assert.LessOrEqual(UserInstructions.Summary(40).Length, 46);
        }

        [Test]
        public void Changed_IsRaisedOnSet()
        {
            var raised = 0;
            void OnChanged() => raised++;
            UserInstructions.Changed += OnChanged;
            UserInstructions.Set("Pirate talk.", true);
            UserInstructions.Changed -= OnChanged;
            Assert.AreEqual(1, raised);
        }

        [Test]
        public void Presets_IncludeStrictDiet()
        {
            Assert.That(System.Array.Exists(UserInstructions.Presets, p => p.Name == "Strict diet" && p.Text.Contains("cake")));
            foreach (var preset in UserInstructions.Presets)
                Assert.LessOrEqual(preset.Text.Length, UserInstructions.MaxLength, preset.Name);
        }

        [Test]
        public void WhatsThatPrompt_LeavesRoomForInstructionsOnlyWhenActive()
        {
            StringAssert.DoesNotContain("personal instructions", PointerMath.Prompt("", SpeechLanguage.English));
            StringAssert.Contains("personal instructions", PointerMath.Prompt("", SpeechLanguage.English, true));
        }

        [Test]
        public void Italian_KeepsReplyInOneLanguage()
        {
            var prompt = PointerMath.Prompt("", SpeechLanguage.Italian, true);
            StringAssert.Contains("written in Italian", prompt);
            StringAssert.EndsWith("Reply in Italian.", prompt);
            StringAssert.Contains("reply language", UserInstructions.Compose("", "I'm on a diet."));
        }
    }
}
