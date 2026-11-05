using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace AIEdge.Chat
{
    /// <summary>
    /// Saves chat threads as JSON files in <c>persistentDataPath/chats</c>.
    /// </summary>
    public static class ChatThreadStore
    {
        static string s_DirectoryOverride;

        /// <summary>Folder that holds the thread files.</summary>
        public static string Directory => s_DirectoryOverride ?? Path.Combine(Application.persistentDataPath, "chats");

        /// <summary>Redirects storage to another folder (tests). Pass null to restore.</summary>
        public static void SetDirectoryForTests(string directory) => s_DirectoryOverride = directory;

        static string PathFor(string id) => Path.Combine(Directory, id + ".json");

        /// <summary>Writes a thread, replacing any earlier version. Writes to a temp file first so a crash can't truncate it.</summary>
        public static void Save(ChatThread thread)
        {
            System.IO.Directory.CreateDirectory(Directory);
            var path = PathFor(thread.id);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonUtility.ToJson(thread));
            File.Delete(path);
            File.Move(temp, path);
        }

        /// <summary>Loads one thread, or null if missing or unreadable.</summary>
        public static ChatThread Load(string id)
        {
            var path = PathFor(id);
            return File.Exists(path) ? Read(path) : null;
        }

        /// <summary>Loads all threads, most recently updated first. Unreadable files are skipped.</summary>
        public static List<ChatThread> LoadAll()
        {
            var threads = new List<ChatThread>();
            if (!System.IO.Directory.Exists(Directory))
                return threads;

            foreach (var path in System.IO.Directory.GetFiles(Directory, "*.json"))
            {
                var thread = Read(path);
                if (thread != null)
                    threads.Add(thread);
            }

            threads.Sort((a, b) => b.updatedUnixMs.CompareTo(a.updatedUnixMs));
            return threads;
        }

        /// <summary>Deletes a thread.</summary>
        public static void Delete(string id) => File.Delete(PathFor(id));

        static ChatThread Read(string path)
        {
            try
            {
                var thread = JsonUtility.FromJson<ChatThread>(File.ReadAllText(path));
                return string.IsNullOrEmpty(thread?.id) ? null : thread;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AIEdge] Skipping unreadable chat {path}: {e.Message}");
                return null;
            }
        }
    }
}
