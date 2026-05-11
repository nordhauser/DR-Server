// PathMapManager.cs - Manages PathMaps for all zones
using System.Collections.Generic;
using UnityEngine;

namespace DungeonRunners.Core
{
    public class PathMapManager
    {
        private static PathMapManager _instance;
        public static PathMapManager Instance => _instance ??= new PathMapManager();

        private Dictionary<string, PathMap> _pathMaps = new Dictionary<string, PathMap>();
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

        /// <summary>Get PathMap for a specific zone</summary>
        public PathMap GetPathMap(string zoneName)
        {
            if (!_loaded) LoadAllPathMaps();
            if (string.IsNullOrWhiteSpace(zoneName)) return null;
            string key = zoneName.ToLowerInvariant();
            if (_pathMaps.TryGetValue(key, out var pathMap)) return pathMap;
            int instIndex = key.IndexOf("_inst", System.StringComparison.OrdinalIgnoreCase);
            if (instIndex > 0 && _pathMaps.TryGetValue(key.Substring(0, instIndex), out pathMap)) return pathMap;
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
