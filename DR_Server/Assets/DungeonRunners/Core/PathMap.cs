// PathMap.cs - PathMap loader and height lookup for Dungeon Runners server
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace DungeonRunners.Core
{
    public class PathNode
    {
        public int GridX;
        public int GridY;
        public float WorldX;
        public float WorldY;
        public float Height;
        public byte ConnectionFlags;
        public byte SolidFlag;

        public bool IsWalkable => SolidFlag < 0xFE;
    }

    public class PathMap
    {
        public string ZoneName { get; private set; }
        public float WorldOffsetX { get; private set; }
        public float WorldOffsetY { get; private set; }

        private const float TILE_SIZE = 10.0f;

        private Dictionary<(int, int), PathNode> _nodeGrid = new Dictionary<(int, int), PathNode>();

        private float _minWorldX, _maxWorldX;
        private float _minWorldY, _maxWorldY;
        public float MinWorldX => _minWorldX;
        public float MaxWorldX => _maxWorldX;
        public float MinWorldY => _minWorldY;
        public float MaxWorldY => _maxWorldY;

        public static PathMap LoadFromFile(string filePath)
        {
            if (!File.Exists(filePath))
            {
                UnityEngine.Debug.LogError($"[PathMap] File not found: {filePath}");
                return null;
            }

            string json = File.ReadAllText(filePath);
            return LoadFromJson(json);
        }

        /// <summary>Load PathMap directly from SQLite relational tables � no JSON.</summary>
        public static PathMap LoadFromSQLite(string zoneName)
        {
            var pathMap = new PathMap();
            try
            {
                using (var conn = DungeonRunners.Database.GameDatabase.GetConnection())
                {
                    // Load zone header
                    using (var r = DungeonRunners.Database.GameDatabase.ExecuteReader(conn,
                        "SELECT * FROM pathmap_zones WHERE zone_name = @z", ("@z", zoneName)))
                    {
                        if (!r.Read())
                        {
                            UnityEngine.Debug.LogWarning($"[PathMap] No pathmap for zone '{zoneName}'");
                            return null;
                        }
                        pathMap.ZoneName = zoneName;
                        pathMap.WorldOffsetX = (float)DungeonRunners.Database.GameDatabase.GetFloat(r, "world_offset_x");
                        pathMap.WorldOffsetY = (float)DungeonRunners.Database.GameDatabase.GetFloat(r, "world_offset_y");
                        pathMap._minWorldX = (float)DungeonRunners.Database.GameDatabase.GetFloat(r, "world_min_x");
                        pathMap._maxWorldX = (float)DungeonRunners.Database.GameDatabase.GetFloat(r, "world_max_x");
                        pathMap._minWorldY = (float)DungeonRunners.Database.GameDatabase.GetFloat(r, "world_min_y");
                        pathMap._maxWorldY = (float)DungeonRunners.Database.GameDatabase.GetFloat(r, "world_max_y");
                    }

                    // Load all nodes
                    using (var r = DungeonRunners.Database.GameDatabase.ExecuteReader(conn,
                        "SELECT gx, gy, wx, wy, h, c, s FROM pathmap_nodes WHERE zone_name = @z", ("@z", zoneName)))
                    {
                        while (r.Read())
                        {
                            var node = new PathNode
                            {
                                GridX = r.GetInt32(0),
                                GridY = r.GetInt32(1),
                                WorldX = (float)r.GetDouble(2),
                                WorldY = (float)r.GetDouble(3),
                                Height = (float)r.GetDouble(4),
                                ConnectionFlags = (byte)r.GetInt32(5),
                                SolidFlag = (byte)r.GetInt32(6)
                            };
                            pathMap._nodeGrid[(node.GridX, node.GridY)] = node;
                        }
                    }
                }
                UnityEngine.Debug.Log($"[PathMap] Loaded '{zoneName}' from SQLite: {pathMap._nodeGrid.Count} nodes");
                return pathMap;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"[PathMap] SQLite load error for '{zoneName}': {ex.Message}");
                return null;
            }
        }

        public static PathMap LoadFromJson(string json)
        {
            var pathMap = new PathMap();

            try
            {
                // Simple JSON parsing without external dependencies
                pathMap.ZoneName = ExtractString(json, "zoneName");
                pathMap.WorldOffsetX = ExtractFloat(json, "worldOffsetX");
                pathMap.WorldOffsetY = ExtractFloat(json, "worldOffsetY");

                // Extract bounds
                int boundsStart = json.IndexOf("\"worldBounds\"");
                if (boundsStart >= 0)
                {
                    int braceStart = json.IndexOf("{", boundsStart);
                    int braceEnd = json.IndexOf("}", braceStart);
                    string boundsJson = json.Substring(braceStart, braceEnd - braceStart + 1);
                    pathMap._minWorldX = ExtractFloat(boundsJson, "minX");
                    pathMap._maxWorldX = ExtractFloat(boundsJson, "maxX");
                    pathMap._minWorldY = ExtractFloat(boundsJson, "minY");
                    pathMap._maxWorldY = ExtractFloat(boundsJson, "maxY");
                }

                // Extract nodes array
                int nodesStart = json.IndexOf("\"nodes\"");
                if (nodesStart >= 0)
                {
                    int arrayStart = json.IndexOf("[", nodesStart);
                    int arrayEnd = json.LastIndexOf("]");
                    string nodesJson = json.Substring(arrayStart + 1, arrayEnd - arrayStart - 1);

                    // Parse each node object
                    int pos = 0;
                    while (pos < nodesJson.Length)
                    {
                        int objStart = nodesJson.IndexOf("{", pos);
                        if (objStart < 0) break;

                        int objEnd = nodesJson.IndexOf("}", objStart);
                        if (objEnd < 0) break;

                        string nodeJson = nodesJson.Substring(objStart, objEnd - objStart + 1);

                        var node = new PathNode
                        {
                            GridX = ExtractInt(nodeJson, "gx"),
                            GridY = ExtractInt(nodeJson, "gy"),
                            WorldX = ExtractFloat(nodeJson, "wx"),
                            WorldY = ExtractFloat(nodeJson, "wy"),
                            Height = ExtractFloat(nodeJson, "h"),
                            ConnectionFlags = (byte)ExtractInt(nodeJson, "c"),
                            SolidFlag = (byte)ExtractInt(nodeJson, "s")
                        };

                        pathMap._nodeGrid[(node.GridX, node.GridY)] = node;
                        pos = objEnd + 1;
                    }
                }

                UnityEngine.Debug.Log($"[PathMap] Loaded '{pathMap.ZoneName}' with {pathMap._nodeGrid.Count} nodes");
                return pathMap;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"[PathMap] Parse error: {ex.Message}");
                return null;
            }
        }

        private static string ExtractString(string json, string key)
        {
            string search = $"\"{key}\"";
            int keyPos = json.IndexOf(search);
            if (keyPos < 0) return "";

            int colonPos = json.IndexOf(":", keyPos);
            int quoteStart = json.IndexOf("\"", colonPos);
            int quoteEnd = json.IndexOf("\"", quoteStart + 1);
            return json.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);
        }

        private static float ExtractFloat(string json, string key)
        {
            string search = $"\"{key}\"";
            int keyPos = json.IndexOf(search);
            if (keyPos < 0) return 0f;

            int colonPos = json.IndexOf(":", keyPos);
            int valueStart = colonPos + 1;

            // Skip whitespace
            while (valueStart < json.Length && char.IsWhiteSpace(json[valueStart]))
                valueStart++;

            int valueEnd = valueStart;
            while (valueEnd < json.Length && (char.IsDigit(json[valueEnd]) || json[valueEnd] == '.' || json[valueEnd] == '-'))
                valueEnd++;

            string valueStr = json.Substring(valueStart, valueEnd - valueStart);
            float.TryParse(valueStr, out float result);
            return result;
        }

        private static int ExtractInt(string json, string key)
        {
            return (int)ExtractFloat(json, key);
        }

        public (int gridX, int gridY) WorldToGrid(float worldX, float worldY)
        {
            int gridX = (int)Math.Round((worldX - WorldOffsetX) / TILE_SIZE);
            int gridY = (int)Math.Round((worldY - WorldOffsetY) / TILE_SIZE);
            return (gridX, gridY);
        }

        public PathNode GetNodeAt(int gridX, int gridY)
        {
            _nodeGrid.TryGetValue((gridX, gridY), out var node);
            return node;
        }

        public PathNode GetNodeAtWorld(float worldX, float worldY)
        {
            var (gx, gy) = WorldToGrid(worldX, worldY);
            return GetNodeAt(gx, gy);
        }

        /// <summary>
        /// Get the height (Z) at the given world X,Y position.
        /// </summary>
        public float GetHeightAt(float worldX, float worldY, float defaultHeight = 50f)
        {
            // Quick bounds check
            if (worldX < _minWorldX || worldX > _maxWorldX ||
                worldY < _minWorldY || worldY > _maxWorldY)
            {
                return defaultHeight;
            }

            var (gx, gy) = WorldToGrid(worldX, worldY);

            // Try exact node first
            var node = GetNodeAt(gx, gy);
            if (node != null && node.IsWalkable)
            {
                return node.Height;
            }

            // Search nearby nodes
            for (int radius = 1; radius <= 3; radius++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    for (int dy = -radius; dy <= radius; dy++)
                    {
                        if (Math.Abs(dx) != radius && Math.Abs(dy) != radius)
                            continue;

                        node = GetNodeAt(gx + dx, gy + dy);
                        if (node != null && node.IsWalkable)
                        {
                            return node.Height;
                        }
                    }
                }
            }

            return defaultHeight;
        }

        public bool IsWalkable(float worldX, float worldY)
        {
            var node = GetNodeAtWorld(worldX, worldY);
            return node?.IsWalkable == true;
        }

        public bool CanReachPoint(float startX, float startY, float endX, float endY)
        {
            var startNode = GetReachNodeAtWorld(startX, startY);
            var endNode = GetReachNodeAtWorld(endX, endY);
            if (startNode == null || endNode == null) return false;
            if (startNode.GridX == endNode.GridX && startNode.GridY == endNode.GridY) return true;

            float dx = endX - startX;
            float dy = endY - startY;
            float dist = Mathf.Sqrt(dx * dx + dy * dy);
            if (dist <= 0.001f) return true;

            int steps = Mathf.Max(1, Mathf.CeilToInt(dist / (TILE_SIZE * 0.5f)));
            for (int i = 1; i < steps; i++)
            {
                float t = i / (float)steps;
                var node = GetNodeAtWorld(startX + dx * t, startY + dy * t);
                if (node == null || !node.IsWalkable) return false;
            }

            return true;
        }

        private PathNode GetReachNodeAtWorld(float worldX, float worldY)
        {
            var (gx, gy) = WorldToGrid(worldX, worldY);
            var node = GetNodeAt(gx, gy);
            if (node != null && node.IsWalkable) return node;
            for (int radius = 1; radius <= 2; radius++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    for (int dy = -radius; dy <= radius; dy++)
                    {
                        if (Math.Abs(dx) != radius && Math.Abs(dy) != radius) continue;
                        node = GetNodeAt(gx + dx, gy + dy);
                        if (node != null && node.IsWalkable) return node;
                    }
                }
            }
            return null;
        }
    }
}
