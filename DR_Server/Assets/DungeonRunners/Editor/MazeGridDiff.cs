#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace DungeonRunners.Editor
{
    /// <summary>
    /// Phase 2 (Option 1-full) helper: diff two MazeGenerator grid dumps line-by-line.
    /// Use cases:
    ///   1. Sanity: same seed dumped twice on server → must match (verifies server determinism).
    ///   2. Cross-source: server's dump vs x32dbg-captured client grid → verifies parity.
    ///
    /// Menu: Tools → DR Reborn → Diff Two Maze Dumps
    /// </summary>
    public static class MazeGridDiff
    {
        [MenuItem("Tools/DR Reborn/Diff Two Maze Dumps")]
        public static void DiffTwoDumps()
        {
            string fileA = EditorUtility.OpenFilePanel("Pick first dump (e.g., server)",
                Application.persistentDataPath, "txt");
            if (string.IsNullOrEmpty(fileA)) return;

            string fileB = EditorUtility.OpenFilePanel("Pick second dump (e.g., client)",
                Application.persistentDataPath, "txt");
            if (string.IsNullOrEmpty(fileB)) return;

            DiffFiles(fileA, fileB);
        }

        /// <summary>
        /// Programmatic entry point — runs the diff and logs results via Debug.LogError.
        /// </summary>
        public static void DiffFiles(string pathA, string pathB)
        {
            if (!File.Exists(pathA))
            {
                Debug.LogError($"[MAZE-DIFF] A not found: {pathA}");
                return;
            }
            if (!File.Exists(pathB))
            {
                Debug.LogError($"[MAZE-DIFF] B not found: {pathB}");
                return;
            }

            string[] linesA = File.ReadAllLines(pathA);
            string[] linesB = File.ReadAllLines(pathB);

            Debug.LogError("[MAZE-DIFF] ═══════════════════════════════════════════════════");
            Debug.LogError($"[MAZE-DIFF] A: {pathA} ({linesA.Length} lines)");
            Debug.LogError($"[MAZE-DIFF] B: {pathB} ({linesB.Length} lines)");

            int maxLines = Mathf.Max(linesA.Length, linesB.Length);
            int diffs = 0;
            int firstDiff = -1;

            for (int i = 0; i < maxLines; i++)
            {
                string la = i < linesA.Length ? linesA[i] : "<EOF>";
                string lb = i < linesB.Length ? linesB[i] : "<EOF>";

                // Ignore ASCII-picture suffix and comments
                if (la.StartsWith("#") && lb.StartsWith("#")) continue;
                if (la.StartsWith("+") || la.StartsWith("|") || string.IsNullOrWhiteSpace(la)) continue;

                if (la != lb)
                {
                    diffs++;
                    if (firstDiff < 0) firstDiff = i + 1;
                    if (diffs <= 10)
                    {
                        Debug.LogError($"[MAZE-DIFF] line {i + 1} differs:");
                        Debug.LogError($"[MAZE-DIFF]   A: {la}");
                        Debug.LogError($"[MAZE-DIFF]   B: {lb}");
                    }
                }
            }

            if (diffs == 0)
            {
                Debug.LogError("[MAZE-DIFF] ✅ IDENTICAL (excluding comments and ASCII picture)");
            }
            else
            {
                Debug.LogError($"[MAZE-DIFF] ❌ {diffs} differences (first at line {firstDiff})");
            }
            Debug.LogError("[MAZE-DIFF] ═══════════════════════════════════════════════════");
        }
    }
}
#endif
