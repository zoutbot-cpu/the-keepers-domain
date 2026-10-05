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
    ///
    /// In a built player the project's Logs folder doesn't exist and
    /// Application.dataPath is read-only (inside the APK on Android), so the
    /// log goes to Application.persistentDataPath instead. One writer is
    /// kept open for the session (AutoFlush, so a crash still leaves the
    /// tail on disk) rather than reopening the file for every line.
    public static class GameplayLog
    {
        private const string FileName = "gameplay-debug.log";

        private static StreamWriter _writer;
        private static bool _startedThisSession;
        private static bool _disabled;

        private static string FilePath =>
            Application.isEditor
                ? Path.Combine(Application.dataPath, "..", "Logs", FileName)
                : Path.Combine(Application.persistentDataPath, FileName);

        public static void Write(string message)
        {
            if (_disabled)
            {
                return;
            }

            try
            {
                EnsureFreshFileForThisSession();
                _writer.WriteLine($"[{Time.time:0.000}] {message}");
            }
            catch (Exception)
            {
                // Debug convenience only — a locked, missing, or read-only
                // file must never throw into gameplay code. Give up for the
                // rest of the session instead of retrying every line.
                _disabled = true;
                _writer?.Dispose();
                _writer = null;
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

            var filePath = FilePath;
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            _writer = new StreamWriter(filePath, append: false) { AutoFlush = true };
            _writer.WriteLine($"=== Session started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            Application.quitting += CloseWriter;
        }

        private static void CloseWriter()
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
