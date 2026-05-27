// PathMapManager.cs - Manages PathMaps for all zones
using System.Collections.Generic;
using UnityEngine;
using DungeonRunners.Managers;

namespace DungeonRunners.Core
{
    public class PathMapManager
    {
        private static PathMapManager _instance;
        public static PathMapManager Instance => _instance ??= new PathMapManager();

        private Dictionary<string, PathMap> _pathMaps = new Dictionary<string, PathMap>();
        private HashSet<string> _proceduralInstancePathMapMissLogged = new HashSet<string>();
        private bool _loaded = false;

        /// <summary>
        /// Load all PathMaps from SQLite pathmap_zones + pathmap_nodes tables
        /// </summary>
        public void LoadAllPathMaps()
        {
            if (_loaded) return;

            try
            {
                using (var conn = DungeonRunners.Database.GameDatabase.GetConnection())
                using (var reader = DungeonRunners.Database.GameDatabase.ExecuteReader(conn,
                    "SELECT zone_name FROM pathmap_zones"))
                {
                    var zones = new List<string>();
                    while (reader.Read())
                        zones.Add(reader.GetString(0));

                    foreach (var zone in zones)
                    {
                        var pathMap = PathMap.LoadFromSQLite(zone);
                        if (pathMap != null)
                        {
                            string key = zone.ToLowerInvariant();
                            _pathMaps[key] = pathMap;
                            Debug.Log($"[PathMapManager] Loaded from SQLite: {key}");
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[PathMapManager] SQLite error: {ex.Message}");
            }

            _loaded = true;
            Debug.Log($"[PathMapManager] Total PathMaps loaded: {_pathMaps.Count}");
        }

        /// <summary>
        /// Register a per-instance PathMap built from geometry (Phase 3 of Option 1-full).
        /// Keyed by the full instance zone name (e.g. <c>dungeon00_level01_inst42</c>) so
        /// GetPathMap returns this instead of the missing-static-pathmap warning.
        /// </summary>
        public void RegisterInstancePathMap(string zoneName, PathMap pathMap)
        {
            if (string.IsNullOrWhiteSpace(zoneName) || pathMap == null) return;
            string key = zoneName.ToLowerInvariant();
            _pathMaps[key] = pathMap;
            _proceduralInstancePathMapMissLogged.Remove(key);
            Debug.Log($"[PathMapManager] Registered instance PathMap '{key}' with {pathMap.NodeCount} nodes");
        }

        /// <summary>Remove a per-instance PathMap (call on instance teardown to free memory).</summary>
        public void UnregisterInstancePathMap(string zoneName)
        {
            if (string.IsNullOrWhiteSpace(zoneName)) return;
            string key = zoneName.ToLowerInvariant();
            if (_pathMaps.Remove(key))
                Debug.Log($"[PathMapManager] Unregistered instance PathMap '{key}'");
        }

        /// <summary>
        /// Find the first registered PathMap whose key starts with <paramref name="prefix"/>.
        /// Used by Phase 4b parity test to match captured cases to whichever live instance
        /// of a base zone is currently registered (instance IDs vary per session).
        /// </summary>
        public PathMap FindByPrefix(string prefix)
        {
            if (string.IsNullOrEmpty(prefix)) return null;
            if (!_loaded) LoadAllPathMaps();
            string key = prefix.ToLowerInvariant();
            foreach (var kv in _pathMaps)
            {
                if (kv.Key.StartsWith(key, System.StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            }
            return null;
        }

        /// <summary>Get PathMap for a specific zone</summary>
        public PathMap GetPathMap(string zoneName)
        {
            if (!_loaded) LoadAllPathMaps();
            if (string.IsNullOrWhiteSpace(zoneName)) return null;
            string key = zoneName.ToLowerInvariant();
            if (_pathMaps.TryGetValue(key, out var pathMap)) return pathMap;
            int instIndex = key.IndexOf("_inst", System.StringComparison.OrdinalIgnoreCase);
            if (instIndex > 0)
            {
                string baseKey = key.Substring(0, instIndex);
                if (DungeonMazeSpawner.IsProceduralZone(baseKey))
                {
                    if (_proceduralInstancePathMapMissLogged.Add(key))
                        Debug.LogError($"[PathMapManager] Procedural instance '{zoneName}' has no generated PathMap; not using base static PathMap '{baseKey}' for reach checks");
                    return null;
                }
                if (_pathMaps.TryGetValue(baseKey, out pathMap)) return pathMap;
            }
            return pathMap;
        }

        /// <summary>Get height at world position for a specific zone</summary>
        public float GetHeight(string zoneName, float worldX, float worldY, float defaultHeight = 50f)
        {
            var pathMap = GetPathMap(zoneName);
            if (pathMap == null) return defaultHeight;
            return pathMap.GetHeightAt(worldX, worldY, defaultHeight);
        }

        /// <summary>Check if a position is walkable</summary>
        public bool IsWalkable(string zoneName, float worldX, float worldY)
        {
            var pathMap = GetPathMap(zoneName);
            return pathMap?.IsWalkable(worldX, worldY) ?? true;
        }
    }
}
