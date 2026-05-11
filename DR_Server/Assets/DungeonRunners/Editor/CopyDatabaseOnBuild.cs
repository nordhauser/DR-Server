using UnityEngine;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using System.IO;

namespace DungeonRunners
{
    /// <summary>
    /// Copies the Database folder to the build output directory after building.
    /// DatabaseLoader expects Database/ next to the exe in builds:
    ///   Application.dataPath/../Database/ = BuildFolder/Database/
    /// </summary>
    public class CopyDatabaseOnBuild : IPostprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPostprocessBuild(BuildReport report)
        {
            string buildPath = report.summary.outputPath;
            string buildDir = Path.GetDirectoryName(buildPath);

            // Source: Assets/DungeonRunners/Database/
            string sourceDir = Path.Combine(Application.dataPath, "DungeonRunners", "Database");

            // Destination: BuildFolder/Database/
            string destDir = Path.Combine(buildDir, "Database");

            if (!Directory.Exists(sourceDir))
            {
                Debug.LogError($"[BUILD] Database source not found: {sourceDir}");
                return;
            }

            CopyDirectory(sourceDir, destDir);

            // Count files for verification
            int fileCount = Directory.GetFiles(destDir, "*.json", SearchOption.AllDirectories).Length;
            Debug.LogError($"[BUILD] ✅ Copied Database to {destDir} ({fileCount} json files)");
        }

        private static void CopyDirectory(string source, string dest)
        {
            Directory.CreateDirectory(dest);

            foreach (string file in Directory.GetFiles(source))
            {
                // Skip .meta files
                if (file.EndsWith(".meta")) continue;

                string destFile = Path.Combine(dest, Path.GetFileName(file));
                Directory.CreateDirectory(Path.GetDirectoryName(destFile));
                if (ShouldPreserveExistingDatabaseFile(destFile))
                    continue;
                File.Copy(file, destFile, true);
            }

            foreach (string dir in Directory.GetDirectories(source))
            {
                string dirName = Path.GetFileName(dir);
                CopyDirectory(dir, Path.Combine(dest, dirName));
            }
        }

        private static bool ShouldPreserveExistingDatabaseFile(string destFile)
        {
            if (!File.Exists(destFile)) return false;
            string name = Path.GetFileName(destFile).ToLowerInvariant();
            return name == "dungeon_runners.db" || name == "dungeon_runners.db-wal" || name == "dungeon_runners.db-shm";
        }
    }
}
