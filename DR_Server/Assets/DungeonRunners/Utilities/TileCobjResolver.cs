using System;
using System.Collections.Generic;
using System.IO;

namespace DungeonRunners.Utilities
{
    /// <summary>
    /// Resolves GC-script package paths (e.g. <c>terrain.elmforest.walls.elmforest_4_straight_2</c>)
    /// to their corresponding <c>.cobj</c> files in the unpacked <c>666 game.pki dump/</c> folder.
    ///
    /// Resolution rule: take the leaf component of the dotted path and match (case-insensitively)
    /// against flat filenames in the dump. The dump folder is flat — no subdirectories — so a
    /// single dictionary lookup suffices.
    ///
    /// Many GC paths resolve to no <c>.cobj</c> (visual-only props, encounter spawn points,
    /// abstract base classes). These return <c>null</c>; callers should treat them as
    /// non-blocking.
    ///
    /// Built lazily on first use. Subsequent lookups are O(1).
    /// </summary>
    public static class TileCobjResolver
    {
        private const string DumpEnvVar = "DR_GAME_PKI_DUMP";
        private const string DumpDefaultPath = @"C:\Users\tippi\Documents\Dungeon Runners\666 game.pki dump";

        private static readonly object _initLock = new object();
        private static Dictionary<string, string> _leafToPath; // leaf-name (lowercase, no extension) → absolute .cobj path
        private static Dictionary<string, string> _tileToPath; // tile-name (lowercase, no extension) → absolute .tile path

        public static int CobjFileCount
        {
            get
            {
                EnsureIndexed();
                return _leafToPath?.Count ?? 0;
            }
        }

        public static int TileFileCount
        {
            get
            {
                EnsureIndexed();
                return _tileToPath?.Count ?? 0;
            }
        }

        public static string ResolveTilePath(string tileTypeName)
        {
            if (string.IsNullOrEmpty(tileTypeName)) return null;
            EnsureIndexed();
            if (_tileToPath == null) return null;
            return _tileToPath.TryGetValue(tileTypeName.ToLowerInvariant(), out string fullPath) ? fullPath : null;
        }

        public static string ResolveCobjPath(string extendsPath)
        {
            if (string.IsNullOrEmpty(extendsPath)) return null;

            EnsureIndexed();
            if (_leafToPath == null) return null;

            string leaf = LeafOf(extendsPath).ToLowerInvariant();
            return _leafToPath.TryGetValue(leaf, out string fullPath) ? fullPath : null;
        }

        public static string ResolveCobjPathByLeaf(string leafName)
        {
            if (string.IsNullOrEmpty(leafName)) return null;
            EnsureIndexed();
            if (_leafToPath == null) return null;
            return _leafToPath.TryGetValue(leafName.ToLowerInvariant(), out string fullPath) ? fullPath : null;
        }

        public static CobjData LoadCobj(string extendsPath)
        {
            string path = ResolveCobjPath(extendsPath);
            return path == null ? null : CobjParser.ParseFile(path);
        }

        private static void EnsureIndexed()
        {
            if (_leafToPath != null) return;

            lock (_initLock)
            {
                if (_leafToPath != null) return;

                string dumpDir = ResolveDumpDir();
                if (dumpDir == null)
                {
                    _leafToPath = new Dictionary<string, string>();
                    _tileToPath = new Dictionary<string, string>();
                    return;
                }

                var cobjIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string file in Directory.GetFiles(dumpDir, "*.cobj"))
                {
                    string leaf = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                    cobjIndex[leaf] = file;
                }
                _leafToPath = cobjIndex;

                var tileIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string file in Directory.GetFiles(dumpDir, "*.tile"))
                {
                    string leaf = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                    tileIndex[leaf] = file;
                }
                _tileToPath = tileIndex;
            }
        }

        private static string ResolveDumpDir()
        {
            string envPath = Environment.GetEnvironmentVariable(DumpEnvVar);
            if (!string.IsNullOrEmpty(envPath) && Directory.Exists(envPath)) return envPath;
            if (Directory.Exists(DumpDefaultPath)) return DumpDefaultPath;
            return null;
        }

        private static string LeafOf(string dottedPath)
        {
            int dot = dottedPath.LastIndexOf('.');
            return dot < 0 ? dottedPath : dottedPath.Substring(dot + 1);
        }
    }
}
