using System;
using System.IO;
using UnityEngine;

namespace KeepersDomain.DebugUI
{
    /// Append-only timestamped log for troubleshooting timing-sensitive bugs
    /// (job promotion races, impling state transitions, ...) that are hard to
    /// catch just by watching the game live. Writes to Logs/gameplay-debug.log
    /// — that folder already exists and is gitignored — so a play session can
    /// be reviewed, or its contents pasted back for analysis, after the fact
    /// instead of needing to catch something in the exact instant it happens.
    /// Timestamps are Time.time (seconds since this Play session started),
    /// which is what actually matters for ordering events relative to each
    /// other — wall-clock time is only in the session-start header.
    public static class GameplayLog
    {
        private static readonly string FilePath = Path.Combine(Application.dataPath, "..", "Logs", "gameplay-debug.log");
        private static bool _startedThisSession;

        public static void Write(string message)
        {
            try
            {
                EnsureFreshFileForThisSession();
                File.AppendAllText(FilePath, $"[{Time.time:0.000}] {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // Debug convenience only — a locked or missing file shouldn't affect gameplay.
            }
        }

        /// Owner-tagged line — prefixes "[P1] " / "[P2] " (or "[wild] " for
        /// a negative/no-owner id) so a multi-keeper session's log shows
        /// which keeper each event belongs to.
        public static void Write(int ownerId, string message)
        {
            Write($"{OwnerTag(ownerId)}{message}");
        }

        /// "[P1] " / "[P2] " / … (trailing space), or "[wild] " for
        /// ownerId &lt; 0. Public so callers can also tag a second party
        /// inline (e.g. "P1's creature hits [P2] Foo").
        public static string OwnerTag(int ownerId)
        {
            return ownerId >= 0 ? $"[P{ownerId + 1}] " : "[wild] ";
        }

        private static void EnsureFreshFileForThisSession()
        {
            if (_startedThisSession)
            {
                return;
            }

            _startedThisSession = true;

            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(FilePath, $"=== Session started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
        }
    }
}
