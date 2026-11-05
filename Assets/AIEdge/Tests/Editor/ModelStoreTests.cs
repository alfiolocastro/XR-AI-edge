using System.IO;
using NUnit.Framework;

namespace AIEdge.Tests
{
    public class ModelStoreTests
    {
        string m_Root;
        ModelInfo m_Model;

        [SetUp]
        public void SetUp()
        {
            m_Root = Path.Combine(Path.GetTempPath(), "AIEdgeTests_" + Path.GetRandomFileName());
            Directory.CreateDirectory(m_Root);
            ModelStore.SetRootForTests(m_Root);
            m_Model = new ModelInfo { id = "test", displayName = "Test", fileName = "test.litertlm", sizeBytes = 10 };
        }

        [TearDown]
        public void TearDown()
        {
            ModelStore.SetRootForTests(null);
            Directory.Delete(m_Root, true);
        }

        void WriteBytes(string path, int count)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, new byte[count]);
        }

        [Test]
        public void State_IsNotDownloaded_WhenNoFiles()
        {
            Assert.AreEqual(ModelState.NotDownloaded, ModelStore.GetState(m_Model));
            Assert.AreEqual(0, ModelStore.GetPartialBytes(m_Model));
        }

        [Test]
        public void State_IsPartial_WithPartFile()
        {
            WriteBytes(ModelStore.GetPartialPath(m_Model), 4);

            Assert.AreEqual(ModelState.Partial, ModelStore.GetState(m_Model));
            Assert.AreEqual(4, ModelStore.GetPartialBytes(m_Model));
        }

        [Test]
        public void State_IsDownloaded_OnlyWhenSizeMatches()
        {
            WriteBytes(ModelStore.GetPath(m_Model), 9);
            Assert.AreNotEqual(ModelState.Downloaded, ModelStore.GetState(m_Model));

            WriteBytes(ModelStore.GetPath(m_Model), 10);
            Assert.AreEqual(ModelState.Downloaded, ModelStore.GetState(m_Model));
        }

        [Test]
        public void State_IsDownloaded_ForAnySize_WhenSizeUnknown()
        {
            m_Model.sizeBytes = 0;
            WriteBytes(ModelStore.GetPath(m_Model), 3);

            Assert.AreEqual(ModelState.Downloaded, ModelStore.GetState(m_Model));
        }

        [Test]
        public void Delete_RemovesModelAndPartial()
        {
            WriteBytes(ModelStore.GetPath(m_Model), 10);
            WriteBytes(ModelStore.GetPartialPath(m_Model), 4);

            ModelStore.Delete(m_Model);

            Assert.IsFalse(File.Exists(ModelStore.GetPath(m_Model)));
            Assert.IsFalse(File.Exists(ModelStore.GetPartialPath(m_Model)));
            Assert.AreEqual(ModelState.NotDownloaded, ModelStore.GetState(m_Model));
        }

        [Test]
        public void Paths_StayInsideModelsDirectory()
        {
            StringAssert.StartsWith(Path.Combine(m_Root, "models"), ModelStore.GetPath(m_Model));
            StringAssert.EndsWith(".part", ModelStore.GetPartialPath(m_Model));
        }

        [Test]
        public void HuggingFaceToken_RoundTripsTrimmed_AndClears()
        {
            Assert.IsNull(ModelStore.HuggingFaceToken);

            ModelStore.HuggingFaceToken = "  hf_abc \n";
            Assert.AreEqual("hf_abc", ModelStore.HuggingFaceToken);

            ModelStore.HuggingFaceToken = " ";
            Assert.IsNull(ModelStore.HuggingFaceToken);
        }
    }
}
