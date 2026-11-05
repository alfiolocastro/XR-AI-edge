using System.Collections.Generic;
using NUnit.Framework;

namespace AIEdge.Tests
{
    public class ModelCatalogTests
    {
        [Test]
        public void Ids_AndFileNames_AreUnique()
        {
            var ids = new HashSet<string>();
            var files = new HashSet<string>();
            foreach (var model in ModelCatalog.All)
            {
                Assert.IsTrue(ids.Add(model.id), $"Duplicate id {model.id}");
                Assert.IsTrue(files.Add(model.fileName), $"Duplicate file {model.fileName}");
            }
        }

        [Test]
        public void Entries_ArePinnedAndSized()
        {
            foreach (var model in ModelCatalog.All)
            {
                Assert.AreEqual(40, model.commitHash.Length, $"{model.id} commit hash");
                Assert.Greater(model.sizeBytes, 0, $"{model.id} size");
                Assert.Greater(model.maxTokens, 0, $"{model.id} max tokens");
                StringAssert.EndsWith(".litertlm", model.fileName);
                Assert.AreNotEqual(LlmBackend.None, model.backend, $"{model.id} backend");
            }
        }

        [Test]
        public void DownloadUrl_ResolvesPinnedCommit()
        {
            var model = ModelCatalog.Gemma4E2B;
            Assert.AreEqual(
                "https://huggingface.co/litert-community/gemma-4-E2B-it-litert-lm/resolve/6e5c4f1e395deb959c494953478fa5cec4b8008f/gemma-4-E2B-it.litertlm?download=true",
                model.DownloadUrl);
        }

        [Test]
        public void ImageModels_HaveVisionBackend()
        {
            foreach (var model in ModelCatalog.All)
            {
                if (model.supportsImage)
                    Assert.AreNotEqual(LlmBackend.None, model.visionBackend, model.id);
            }
        }

        [Test]
        public void Find_ReturnsEntryOrNull()
        {
            Assert.AreSame(ModelCatalog.TinyGarden, ModelCatalog.Find(ModelCatalog.TinyGarden.id));
            Assert.IsNull(ModelCatalog.Find("missing"));
        }

        [Test]
        public void DownloadProgress_Fraction_IsClampedAndSafe()
        {
            Assert.AreEqual(0.5f, new DownloadProgress(5, 10, 0).Fraction, 1e-6f);
            Assert.AreEqual(1f, new DownloadProgress(20, 10, 0).Fraction);
            Assert.AreEqual(0f, new DownloadProgress(5, 0, 0).Fraction);
        }

        [Test]
        public void FormatBytes_UsesReadableUnits()
        {
            Assert.AreEqual("2.4 GB", ModelDownloader.FormatBytes(2588147712));
            Assert.AreEqual("276 MB", ModelDownloader.FormatBytes(288964608));
            Assert.AreEqual("2 KB", ModelDownloader.FormatBytes(2048));
            Assert.AreEqual("?", ModelDownloader.FormatBytes(-1));
        }
    }
}
