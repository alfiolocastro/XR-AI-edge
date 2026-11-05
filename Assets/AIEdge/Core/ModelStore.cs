using System.IO;
using UnityEngine;

namespace AIEdge
{
    /// <summary>
    /// Where a model stands on this device.
    /// </summary>
    public enum ModelState
    {
        NotDownloaded,
        Partial,
        Downloaded,
    }

    /// <summary>
    /// On-device model files and the Hugging Face token. Models live in <c>persistentDataPath/models</c>
    /// (<c>/sdcard/Android/data/&lt;package&gt;/files/models</c>), so they can also be provisioned with
    /// <c>metavr files push</c> during development.
    /// </summary>
    public static class ModelStore
    {
        const string k_PartialSuffix = ".part";
        const string k_TokenFileName = "hf_token.txt";

        static string s_RootOverride;

        /// <summary>Folder that holds model files.</summary>
        public static string ModelsDirectory => Path.Combine(Root, "models");

        static string Root => s_RootOverride ?? Application.persistentDataPath;

        /// <summary>Redirects storage to another folder (tests). Pass null to restore.</summary>
        public static void SetRootForTests(string root) => s_RootOverride = root;

        /// <summary>Final path of a model file.</summary>
        public static string GetPath(ModelInfo model) => Path.Combine(ModelsDirectory, model.fileName);

        /// <summary>Path of an in-progress download, kept for resuming.</summary>
        public static string GetPartialPath(ModelInfo model) => GetPath(model) + k_PartialSuffix;

        /// <summary>Current state of a model on disk.</summary>
        public static ModelState GetState(ModelInfo model)
        {
            var path = GetPath(model);
            if (File.Exists(path) && (model.sizeBytes <= 0 || new FileInfo(path).Length == model.sizeBytes))
                return ModelState.Downloaded;

            return File.Exists(GetPartialPath(model)) ? ModelState.Partial : ModelState.NotDownloaded;
        }

        /// <summary>Bytes already on disk for a partial download.</summary>
        public static long GetPartialBytes(ModelInfo model)
        {
            var partial = GetPartialPath(model);
            return File.Exists(partial) ? new FileInfo(partial).Length : 0;
        }

        /// <summary>Deletes a model and any partial download.</summary>
        public static void Delete(ModelInfo model)
        {
            File.Delete(GetPath(model));
            File.Delete(GetPartialPath(model));
        }

        /// <summary>
        /// Hugging Face access token for gated models, stored in <c>persistentDataPath/hf_token.txt</c>.
        /// Null if not set. Development builds can provision it with <c>metavr files push</c>.
        /// </summary>
        public static string HuggingFaceToken
        {
            get
            {
                var path = Path.Combine(Root, k_TokenFileName);
                if (!File.Exists(path))
                    return null;

                var token = File.ReadAllText(path).Trim();
                return token.Length > 0 ? token : null;
            }
            set
            {
                var path = Path.Combine(Root, k_TokenFileName);
                if (string.IsNullOrWhiteSpace(value))
                    File.Delete(path);
                else
                    File.WriteAllText(path, value.Trim());
            }
        }

        /// <summary>Free bytes on the volume that holds the models, or -1 if unknown.</summary>
        public static long GetFreeBytes()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            Directory.CreateDirectory(ModelsDirectory);
            using var statFs = new AndroidJavaObject("android.os.StatFs", ModelsDirectory);
            return statFs.Call<long>("getAvailableBytes");
#else
            try
            {
                Directory.CreateDirectory(ModelsDirectory);
                return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(ModelsDirectory))).AvailableFreeSpace;
            }
            catch (System.Exception)
            {
                return -1;
            }
#endif
        }
    }
}
