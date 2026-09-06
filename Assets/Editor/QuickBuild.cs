using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace KeepersDomain.EditorTools
{
    /// One-click shareable build for playtesters. Compiles a Windows x64
    /// player, zips it, and drops the archive in the user's Downloads
    /// folder as TheKeepersDomain-v&lt;version&gt;.zip (version = Player
    /// Settings > Version, e.g. "0.00083"). No build-pipeline setup — just
    /// Tools > Quick Build and hand someone the zip.
    ///
    /// The player is built into Builds/QuickBuild/ (gitignored); the zip is
    /// what you share. "Quick Build (dev)" is the same thing with the
    /// development console + full Player.log for bug reports.
    public static class QuickBuild
    {
        private const BuildTarget Target = BuildTarget.StandaloneWindows64;

        [MenuItem("Tools/Quick Build", priority = 200)]
        public static void BuildRelease() => Run(development: false);

        [MenuItem("Tools/Quick Build (dev)", priority = 201)]
        public static void BuildDev() => Run(development: true);

        private static void Run(bool development)
        {
            var version = string.IsNullOrWhiteSpace(Application.version) ? "0.0" : Application.version.Trim();
            var buildName = $"TheKeepersDomain-v{version}" + (development ? "-dev" : "");

            var scenes = EnabledScenes();
            if (scenes.Length == 0)
            {
                EditorUtility.DisplayDialog("Quick Build",
                    "No scenes are enabled in File > Build Settings — add Prototype.unity there first.", "OK");
                return;
            }

            if (EditorUserBuildSettings.activeBuildTarget != Target
                && !EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Standalone, Target))
            {
                EditorUtility.DisplayDialog("Quick Build",
                    $"Couldn't switch the active build target to {Target}. Install the Windows build support module and try again.", "OK");
                return;
            }

            var outDir = Path.GetFullPath(Path.Combine("Builds", "QuickBuild", buildName));
            if (Directory.Exists(outDir))
            {
                Directory.Delete(outDir, recursive: true);
            }
            Directory.CreateDirectory(outDir);

            var exePath = Path.Combine(outDir, $"{Application.productName}.exe");
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = exePath,
                target = Target,
                options = development
                    ? BuildOptions.Development | BuildOptions.AllowDebugging
                    : BuildOptions.None,
            });

            if (report.summary.result != BuildResult.Succeeded)
            {
                EditorUtility.DisplayDialog("Quick Build",
                    $"Build {report.summary.result} ({report.summary.totalErrors} errors). See the Console.", "OK");
                return;
            }

            var downloads = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(downloads);

            var zipPath = Path.Combine(downloads, $"{buildName}.zip");
            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            // includeBaseDirectory: the archive unpacks to <buildName>/... ,
            // not a loose pile of files in whatever folder it's opened in.
            ZipFile.CreateFromDirectory(outDir, zipPath,
                System.IO.Compression.CompressionLevel.Optimal, includeBaseDirectory: true);

            var sizeMb = new FileInfo(zipPath).Length / (1024f * 1024f);
            Debug.Log($"Quick Build → {zipPath}  ({sizeMb:0.0} MB)");
            EditorUtility.RevealInFinder(zipPath);
        }

        private static string[] EnabledScenes()
        {
            var list = new List<string>();
            foreach (var s in EditorBuildSettings.scenes)
            {
                if (s.enabled && !string.IsNullOrEmpty(s.path))
                {
                    list.Add(s.path);
                }
            }
            return list.ToArray();
        }
    }
}
