using System;
using System.Collections.Generic;

namespace AIEdge
{
    /// <summary>
    /// A downloadable LiteRT-LM model and its runtime defaults.
    /// </summary>
    [Serializable]
    public class ModelInfo
    {
        /// <summary>Stable identifier used in code and settings.</summary>
        public string id;

        /// <summary>Name shown to the user.</summary>
        public string displayName;

        /// <summary>Hugging Face repository, e.g. "litert-community/gemma-4-E2B-it-litert-lm".</summary>
        public string repoId;

        /// <summary>File name inside the repository and on device.</summary>
        public string fileName;

        /// <summary>Pinned commit, so the file never changes under us.</summary>
        public string commitHash;

        /// <summary>Exact file size in bytes, or 0 if unknown (size check skipped).</summary>
        public long sizeBytes;

        /// <summary>True if Hugging Face requires accepting a license and a token.</summary>
        public bool gated;

        /// <summary>True if the model accepts image input.</summary>
        public bool supportsImage;

        /// <summary>KV-cache size in tokens.</summary>
        public int maxTokens;

        /// <summary>Default sampling parameters.</summary>
        public SamplerSettings sampler;

        /// <summary>Backend for the language model.</summary>
        public LlmBackend backend;

        /// <summary>Backend for the image encoder when <see cref="supportsImage"/>.</summary>
        public LlmBackend visionBackend;

        /// <summary>Direct download URL at the pinned commit.</summary>
        public string DownloadUrl => $"https://huggingface.co/{repoId}/resolve/{commitHash}/{fileName}?download=true";

        /// <summary>Model page, where gated licenses are accepted.</summary>
        public string PageUrl => $"https://huggingface.co/{repoId}";
    }

    /// <summary>
    /// The models this app uses. Entries mirror Google AI Edge Gallery's allowlist (model_allowlists/1_0_22.json).
    /// </summary>
    public static class ModelCatalog
    {
        /// <summary>Gemma 4 E2B instruction-tuned: text and image chat. Not gated.</summary>
        public static readonly ModelInfo Gemma4E2B = new ModelInfo
        {
            id = "gemma-4-e2b-it",
            displayName = "Gemma 4 E2B",
            repoId = "litert-community/gemma-4-E2B-it-litert-lm",
            fileName = "gemma-4-E2B-it.litertlm",
            commitHash = "6e5c4f1e395deb959c494953478fa5cec4b8008f",
            sizeBytes = 2588147712,
            gated = false,
            supportsImage = true,
            maxTokens = 4000,
            sampler = SamplerSettings.Default,
            // Measured on Quest 3 in Phase 0: GPU decodes ~19 tok/s vs ~10 on CPU.
            backend = LlmBackend.Gpu,
            visionBackend = LlmBackend.Gpu,
        };

        /// <summary>FunctionGemma 270M fine-tuned for Tiny Garden function calling. Gated (auto-approve).</summary>
        public static readonly ModelInfo TinyGarden = new ModelInfo
        {
            id = "tiny-garden-270m",
            displayName = "Tiny Garden 270M",
            repoId = "litert-community/functiongemma-270m-ft-tiny-garden",
            fileName = "tiny_garden_q8_ekv1024.litertlm",
            commitHash = "c205853ff82da86141a1105faa2344a8b176dfe7",
            sizeBytes = 288964608,
            gated = true,
            supportsImage = false,
            maxTokens = 1024,
            sampler = new SamplerSettings { topK = 64, topP = 0.95f, temperature = 0f },
            backend = LlmBackend.Cpu,
            visionBackend = LlmBackend.None,
        };

        /// <summary>All catalog models.</summary>
        public static IReadOnlyList<ModelInfo> All { get; } = new[] { Gemma4E2B, TinyGarden };

        /// <summary>Finds a model by id, or null.</summary>
        public static ModelInfo Find(string id)
        {
            foreach (var model in All)
            {
                if (model.id == id)
                    return model;
            }

            return null;
        }
    }
}
