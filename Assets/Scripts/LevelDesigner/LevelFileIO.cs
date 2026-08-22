using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace KeepersDomain.LevelDesigner
{
    /// Reads/writes LevelData as JSON under a dedicated Levels folder in
    /// Application.persistentDataPath — writable in both the Editor and a
    /// built player, unlike Application.dataPath (read-only once built).
    public static class LevelFileIO
    {
        private const string LevelsFolderName = "Levels";
        private const string FileExtension = ".json";

        public static string LevelsDirectory => Path.Combine(Application.persistentDataPath, LevelsFolderName);

        public static void Save(string levelName, LevelData data)
        {
            Directory.CreateDirectory(LevelsDirectory);
            var json = JsonUtility.ToJson(data, prettyPrint: true);
            File.WriteAllText(GetPath(levelName), json);
        }

        /// Null if levelName has no save file (deleted/renamed outside the
        /// app since the Load list was last drawn, for instance) — the
        /// caller is expected to handle that rather than crash on a
        /// missing file.
        public static LevelData Load(string levelName)
        {
            var path = GetPath(levelName);
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path);
            return JsonUtility.FromJson<LevelData>(json);
        }

        /// Every saved level's name (file name minus extension), sorted
        /// alphabetically — read fresh from disk each call rather than
        /// cached, so a level saved or removed since the Load list was
        /// last drawn always shows up correctly.
        public static string[] ListLevelNames()
        {
            if (!Directory.Exists(LevelsDirectory))
            {
                return Array.Empty<string>();
            }

            var files = Directory.GetFiles(LevelsDirectory, "*" + FileExtension);
            var names = new string[files.Length];
            for (int i = 0; i < files.Length; i++)
            {
                names[i] = Path.GetFileNameWithoutExtension(files[i]);
            }

            Array.Sort(names, StringComparer.OrdinalIgnoreCase);
            return names;
        }

        /// Strips anything that isn't a letter, digit, space, dash, or
        /// underscore, so a player-typed level name is always safe to use
        /// directly as a file name regardless of platform. Empty (or
        /// all-stripped) input comes back as an empty string — the
        /// caller's job to reject that rather than silently saving to a
        /// blank name.
        public static string SanitizeName(string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(rawName.Length);
            foreach (var c in rawName.Trim())
            {
                if (char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_')
                {
                    builder.Append(c);
                }
            }

            return builder.ToString();
        }

        private static string GetPath(string levelName)
        {
            return Path.Combine(LevelsDirectory, levelName + FileExtension);
        }
    }
}
