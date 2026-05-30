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

            // 2026-05-28 — A1.3 explored tile-level aliasing for tutorial_loot_* →
            // elmforest_loot_*_a (same logical shape, different theme). REVERTED:
            // even tile-level cross-theme aliasing distorts the layout — the
            // elmforest_loot tile drops walls in corridors the tutorial_loot variant
            // leaves open. Phase 4b parity dropped 66% → 33% (case 2 went from
            // SoftPass to "directReach mismatch ours=False client=True", i.e. we
            // now have a wall where the client doesn't).
            //
            // Conclusion: missing-asset substitution across themes doesn't work.
            // The default behavior (return null → cell stays fully walkable when
            // its tile is missing) is the least-bad answer until we either find
            // the actual tutorial_loot geometry source or accept the missing
            // coverage. The 1/3 baseline failure stands; A1.4+A2 are the next
            // axes to try.
            return _tileToPath.TryGetValue(tileTypeName.ToLowerInvariant(), out string fullPath) ? fullPath : null;
        }

        public static string ResolveCobjPath(string extendsPath)
        {
            if (string.IsNullOrEmpty(extendsPath)) return null;

            EnsureIndexed();
            if (_leafToPath == null) return null;

            string leaf = LeafOf(extendsPath).ToLowerInvariant();
            if (_leafToPath.TryGetValue(leaf, out string fullPath)) return fullPath;

            // A1.3 follow-up (2026-05-28): cobj aliasing. The world data references
            // qualifier-prefixed variants (_nm_ = navmesh marker, s_ = stair,
            // t_ = transition, theme-prefixed entrances) whose actual collision
            // geometry lives under the un-prefixed name. Without these aliases the
            // PathMap silently leaves the wall geometry walkable, which causes
            // Phase 4b directReach mismatches on captured client paths.
            string alias = ResolveCobjAlias(leaf);
            if (alias != null && _leafToPath.TryGetValue(alias, out string aliasedPath))
            {
                LogAliasOnce(leaf, alias);
                return aliasedPath;
            }

            return null;
        }

        private static readonly HashSet<string> _aliasLogSeen =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static void LogAliasOnce(string leaf, string alias)
        {
            // Cheap thread-safe-ish dedup: HashSet has its own mutex via Add return,
            // and we only need at-most-once log semantics, not strict at-least-once.
            lock (_aliasLogSeen)
            {
                if (!_aliasLogSeen.Add(leaf)) return;
            }
            UnityEngine.Debug.LogError($"[PATHMAP-BUILD] cobj alias: '{leaf}' → '{alias}' (first hit this session)");
        }

        private static string ResolveCobjAlias(string lowerLeaf)
        {
            // 2026-05-28 LESSON LEARNED: leaf-name cobj aliasing across themes
            // doesn't work, even for what look like semantic equivalents.
            //
            // Tried and REVERTED:
            //   • _nm_ / _s_ / _t_ qualifier stripping — those are navmesh path markers
            //     placed at WALKABLE locations, not wall geometry. Aliasing dropped
            //     walls into corridors and made mazes impassable (parity 66% → 0%).
            //   • elmforest_orokentrance_* → cave_orokentrance — cave entrance geometry
            //     is shape-incompatible with where the elmforest entrance sits;
            //     dropped a wall blocking a corridor (parity 66% → 33%).
            //
            // Conclusion: the unresolved cobj names ARE the right answer being null.
            // The client's actual collision data for those locations comes from
            // somewhere else (tile-bake at load time? hint-file we don't read yet?).
            // The remaining 1/3 Phase 4b fail is a deeper problem than name aliasing —
            // probably A1.4 (8-way slide) or beyond.
            return null;
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
