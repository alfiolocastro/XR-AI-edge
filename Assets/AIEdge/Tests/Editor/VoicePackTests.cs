using System.IO;
using NUnit.Framework;

namespace AIEdge.Tests
{
    public class VoicePackTests
    {
        string m_Root;

        [SetUp]
        public void SetUp()
        {
            m_Root = Path.Combine(Path.GetTempPath(), "AIEdgeVoiceTests_" + Path.GetRandomFileName());
            Directory.CreateDirectory(m_Root);
            ModelStore.SetRootForTests(m_Root);
        }

        [TearDown]
        public void TearDown()
        {
            ModelStore.SetRootForTests(null);
            Directory.Delete(m_Root, true);
        }

        [Test]
        public void IsNeeded_KeepsEngineFilesOnly()
        {
            Assert.IsTrue(VoiceInfo.IsNeeded("en_GB-alba-medium.onnx"));
            Assert.IsTrue(VoiceInfo.IsNeeded("tokens.txt"));
            Assert.IsTrue(VoiceInfo.IsNeeded("espeak-ng-data/en_dict"));
            Assert.IsTrue(VoiceInfo.IsNeeded("espeak-ng-data/lang/gmw/en-GB-x-rp"));
            Assert.IsFalse(VoiceInfo.IsNeeded("en_GB-alba-medium.onnx.json"));
            Assert.IsFalse(VoiceInfo.IsNeeded("vits-piper-en_GB.py"));
            Assert.IsFalse(VoiceInfo.IsNeeded(".gitattributes"));
        }

        [Test]
        public void Catalog_PinsCommitAndBuildsUrls()
        {
            var voice = VoiceCatalog.Default;
            Assert.AreEqual(40, voice.commitHash.Length);
            StringAssert.Contains(voice.commitHash, voice.TreeUrl);
            Assert.AreEqual($"https://huggingface.co/{voice.repoId}/resolve/{voice.commitHash}/tokens.txt", voice.FileUrl("tokens.txt"));
        }

        [Test]
        public void Store_TracksPartialAndCompleteFolders()
        {
            var voice = VoiceCatalog.Default;
            Assert.AreEqual(ModelState.NotDownloaded, VoiceStore.GetState(voice));

            Directory.CreateDirectory(Path.Combine(VoiceStore.GetPartialPath(voice), "espeak-ng-data"));
            File.WriteAllBytes(Path.Combine(VoiceStore.GetPartialPath(voice), "espeak-ng-data", "phontab"), new byte[10]);
            File.WriteAllBytes(Path.Combine(VoiceStore.GetPartialPath(voice), "tokens.txt"), new byte[5]);
            Assert.AreEqual(ModelState.Partial, VoiceStore.GetState(voice));
            Assert.AreEqual(15, VoiceStore.GetBytes(voice));

            Directory.Move(VoiceStore.GetPartialPath(voice), VoiceStore.GetPath(voice));
            Assert.AreEqual(ModelState.Downloaded, VoiceStore.GetState(voice));
            StringAssert.StartsWith(Path.Combine(m_Root, "voices"), VoiceStore.GetPath(voice));

            VoiceStore.Delete(voice);
            Assert.AreEqual(ModelState.NotDownloaded, VoiceStore.GetState(voice));
        }
    }
}
