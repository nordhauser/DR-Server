using System;
using System.Collections.Generic;
using UnityEngine;

namespace DungeonRunners.Managers
{
    public static class DungeonMazeSpawner
    {
        // ═══════════════════════════════════════════════════════════════
        // TILE ENCOUNTER POSITIONS (from tiles.json)
        // Local coordinates within a 400x400 tile
        // ═══════════════════════════════════════════════════════════════

        private struct EncounterMarker
        {
            public float LocalX;
            public float LocalY;
            public float LocalZ;

            public EncounterMarker(float x, float y, float z)
            {
                LocalX = x; LocalY = y; LocalZ = z;
            }
        }

        private static readonly Dictionary<string, List<EncounterMarker>> TileEncounters =
     new Dictionary<string, List<EncounterMarker>>(StringComparer.OrdinalIgnoreCase)
 {
    { "elmforest_tileset_1n_a",       new List<EncounterMarker> { new(200, 80, 10) } },
    { "elmforest_tileset_1e_a",       new List<EncounterMarker> { new(180, 200, 30) } },
    { "elmforest_tileset_1s_a",       new List<EncounterMarker> { new(210, 340, 10) } },
    { "elmforest_tileset_1w_a",       new List<EncounterMarker> { new(340, 210, 10) } },
    { "elmforest_tileset_1n1s_a",     new List<EncounterMarker> { new(160, 200, -10) } },
    { "elmforest_tileset_1e1w_a",     new List<EncounterMarker> { new(230, 170, 10) } },
    { "elmforest_tileset_1n1e_a",     new List<EncounterMarker> { new(230, 70, 10), new(70, 250, 10) } },
    { "elmforest_tileset_1n1w_a",     new List<EncounterMarker> { new(250, 210, 30) } },
    { "elmforest_tileset_1e1s_a",     new List<EncounterMarker> { new(180, 310, 10) } },
    { "elmforest_tileset_1s1w_a",     new List<EncounterMarker> { new(320, 180, 10), new(160, 270, 10) } },
    { "elmforest_tileset_1n1e1s_a",   new List<EncounterMarker> { new(220, 280, 10), new(90, 110, 10) } },
    { "elmforest_tileset_1n1e1w_a",   new List<EncounterMarker> { new(200, 120, 10) } },
    { "elmforest_tileset_1n1s1w_a",   new List<EncounterMarker> { new(200, 190, 10) } },
    { "elmforest_tileset_1e1s1w_a",   new List<EncounterMarker> { new(220, 150, 30) } },
    { "elmforest_tileset_1n1e1s1w_a", new List<EncounterMarker> { new(190, 190, 10) } },
    { "tutorial_loot_1n",             new List<EncounterMarker> { new(200, 80, 10) } },
    { "tutorial_loot_1e",             new List<EncounterMarker> { new(180, 200, 30) } },
    { "tutorial_loot_1s",             new List<EncounterMarker> { new(190, 360, 10) } },
    { "tutorial_loot_1w",             new List<EncounterMarker> { new(340, 210, 10) } },
    { "elmforest_questfindring_1n1e1w", new List<EncounterMarker> { new(300, 230, 10) } },
    { "elmforest_questfindring_1w",     new List<EncounterMarker> { new(340, 180, 10) } },
    { "elmforest_tileset_0n_a",       new List<EncounterMarker>() },
 };


        // ═══════════════════════════════════════════════════════════════
        // ENCOUNTER TABLES (from world/dungeon00/enc/*.gc)
        // ═══════════════════════════════════════════════════════════════

        private struct SpawnUnit
        {
            public string GcType;
            public int Count;
            public float Difficulty;
            public SpawnUnit(string gcType, int count, float difficulty = 1f) { GcType = gcType; Count = count; Difficulty = difficulty; }
        }

        // ---------------------------------------------------------------
        // level01_encounter.gc  (rank1 mobs)
        // ---------------------------------------------------------------
        private static readonly SpawnUnit[][] Level01Encounter =
        {
            new[] { new SpawnUnit("creatures.forestCreatures.Warg.Basic.Pup", 1, 0.75f) },
            new[] { new SpawnUnit("creatures.whiskers.broodling.Basic.Grunt", 1, 0.75f) },
            new[] { new SpawnUnit("creatures.forestCreatures.Warg.Basic.Pup", 1, 0.50f) },
            new[] { new SpawnUnit("creatures.whiskers.broodling.Basic.Grunt", 1, 0.60f) },
            new[] { new SpawnUnit("creatures.forestCreatures.Warg.Basic.Pup", 1, 0.25f),
                    new SpawnUnit("creatures.whiskers.broodling.Basic.Grunt", 1, 0.50f) },
        };

        // level01_leader_encounter.gc
        private static readonly SpawnUnit[][] Level01LeaderEncounter =
        {
            new[] { new SpawnUnit("creatures.forestCreatures.Warg.Basic.Pup", 1, 0f),
                    new SpawnUnit("creatures.forestCreatures.Warg.Basic.Grunt", 1, 0.5f) },
        };

        // ---------------------------------------------------------------
        // level02_encounter.gc  (rank2 mobs)
        // ---------------------------------------------------------------
        private static readonly SpawnUnit[][] Level02Encounter =
        {
            new[] { new SpawnUnit("creatures.forestCreatures.Warg.Basic.Pup", 1) },
            new[] { new SpawnUnit("creatures.whiskers.broodling.Basic.Grunt", 1) },
            new[] { new SpawnUnit("creatures.forestCreatures.Warg.Basic.Pup", 1),
                    new SpawnUnit("creatures.whiskers.broodling.Basic.Grunt", 1) },
            new[] { new SpawnUnit("creatures.forestCreatures.Warg.Basic.Grunt", 1) },
            new[] { new SpawnUnit("creatures.forestCreatures.Warg.Basic.Pup", 1),
                    new SpawnUnit("creatures.whiskers.broodling.Basic.Grunt", 1) },
            new[] { new SpawnUnit("creatures.whiskers.broodling.Basic.Grunt", 1),
                    new SpawnUnit("creatures.forestCreatures.Warg.Basic.Pup", 1) },
            new[] { new SpawnUnit("creatures.forestCreatures.Warg.Basic.Pup", 1),
                    new SpawnUnit("creatures.whiskers.broodling.Basic.Grunt", 1) },
            new[] { new SpawnUnit("creatures.whiskers.blademaster.Basic.Grunt", 1) },
        };

        // level02_leader_encounter.gc
        private static readonly SpawnUnit[][] Level02LeaderEncounter =
        {
            new[] { new SpawnUnit("creatures.forestCreatures.Warg.Basic.Grunt", 1),
                    new SpawnUnit("creatures.forestCreatures.Warg.Basic.Pup", 1) },
            new[] { new SpawnUnit("creatures.whiskers.blademaster.Basic.Grunt", 1),
                    new SpawnUnit("creatures.whiskers.broodling.Basic.Grunt", 1) },
        };

        // ---------------------------------------------------------------
        // level03_encounter.gc  (rank3 mobs)
        // ---------------------------------------------------------------
        private static readonly SpawnUnit[][] Level03Encounter =
        {
            new[] { new SpawnUnit("creatures.forestCreatures.Warg.Basic.Pup", 1) },
            new[] { new SpawnUnit("creatures.whiskers.broodling.Basic.Grunt", 1) },
            new[] { new SpawnUnit("creatures.forestCreatures.Warg.Basic.Pup", 1),
                    new SpawnUnit("creatures.whiskers.broodling.Basic.Grunt", 1) },
            new[] { new SpawnUnit("creatures.whiskers.broodling.Basic.Grunt", 1),
                    new SpawnUnit("creatures.forestCreatures.Warg.Basic.Pup", 1) },
            new[] { new SpawnUnit("creatures.whiskers.blademaster.Basic.Grunt", 1),
                    new SpawnUnit("creatures.whiskers.broodling.Basic.Grunt", 1) },
            new[] { new SpawnUnit("creatures.forestCreatures.Warg.Basic.Grunt", 1),
                    new SpawnUnit("creatures.whiskers.broodling.Basic.Grunt", 1) },
            new[] { new SpawnUnit("creatures.forestCreatures.Warg.Basic.Grunt", 1),
                    new SpawnUnit("creatures.forestCreatures.Warg.Basic.Pup", 1) },
        };

        // level03_leader_encounter.gc
        private static readonly SpawnUnit[][] Level03LeaderEncounter =
        {
            new[] { new SpawnUnit("creatures.forestCreatures.Warg.Basic.Grunt", 1),
                    new SpawnUnit("creatures.forestCreatures.Warg.Basic.Pup", 1) },
            new[] { new SpawnUnit("creatures.whiskers.blademaster.Basic.Grunt", 1),
                    new SpawnUnit("creatures.whiskers.broodling.Basic.Grunt", 1) },
        };


        // ═══════════════════════════════════════════════════════════════
        // LEVEL DEFINITIONS
        // ═══════════════════════════════════════════════════════════════

        private class LevelDef
        {
            public int MazeWidth;
            public int MazeHeight;
            public int MazeRandomness;
            public int MazeSparseness;
            public int MazeDeadEndRemovalChance;
            public SpawnUnit[][] EncounterTable;
            public SpawnUnit[][] LeaderEncounterTable;
            public int[] LeaderGridYs;
            public int EntryGridX;   // Grid cell where player spawns - skip encounters here
            public int EntryGridY;
            public int ExitGridX;
            public int ExitGridY;
        }

        private static readonly Dictionary<string, LevelDef> LevelDefs =
            new Dictionary<string, LevelDef>(StringComparer.OrdinalIgnoreCase)
        {
            {
                "dungeon00_level01", new LevelDef
                {
                    MazeWidth = 4, MazeHeight = 5,
                    MazeRandomness = 90, MazeSparseness = 5,
                    MazeDeadEndRemovalChance = 100,
                    EncounterTable = Level01Encounter,
                    LeaderEncounterTable = Level01LeaderEncounter,
                    LeaderGridYs = new[] { 3, 2 },
                    EntryGridX = 3, EntryGridY = 4,
                    ExitGridX = 3, ExitGridY = 0,
                }
            },
            {
                "dungeon00_level02", new LevelDef
                {
                    MazeWidth = 4, MazeHeight = 4,
                    MazeRandomness = 90, MazeSparseness = 5,
                    MazeDeadEndRemovalChance = 100,
                    EncounterTable = Level02Encounter,
                    LeaderEncounterTable = Level02LeaderEncounter,
                    LeaderGridYs = new[] { 1 },
                    EntryGridX = 3, EntryGridY = 3,
                    ExitGridX = 3, ExitGridY = 0,
                }
            },
            {
                "dungeon00_level03", new LevelDef
                {
                    MazeWidth = 4, MazeHeight = 4,
                    MazeRandomness = 90, MazeSparseness = 5,
                    MazeDeadEndRemovalChance = 100,
                    EncounterTable = Level03Encounter,
                    LeaderEncounterTable = Level03LeaderEncounter,
                    LeaderGridYs = new[] { 1 },
                    EntryGridX = 3, EntryGridY = 3,
                    ExitGridX = 3, ExitGridY = 0,
                }
            },
        };


        // ═══════════════════════════════════════════════════════════════
        // SMALL OFFSETS for group spawning
        // So multiple mobs at the same marker don't stack on each other
        // ═══════════════════════════════════════════════════════════════

        // Wider spread so players cannot walk around the group. Tile rooms are ~400u across.
        private static readonly float[][] GroupOffsets =
        {
            new[] {   0f,   0f },
            new[] {  60f,   0f },
            new[] { -60f,   0f },
            new[] {   0f,  60f },
            new[] {   0f, -60f },
            new[] {  45f,  45f },
            new[] { -45f,  45f },
            new[] {  45f, -45f },
            new[] { -45f, -45f },
            new[] {  90f,  30f },
            new[] { -90f, -30f },
            new[] {  30f,  90f },
            new[] { -30f, -90f },
        };

        // Dungeon-specific creature paths (carry per-dungeon behavior overrides)
        private static readonly Dictionary<string, Dictionary<string, string>> DungeonCreaturePaths = new()
{
    { "dungeon00_level01", new Dictionary<string, string> {
        { "creatures.forestCreatures.Warg.Basic.Pup", "world.dungeon00.mob.melee01.rank1" },
        { "creatures.forestCreatures.Warg.Basic.Grunt", "world.dungeon00.mob.melee02.Rank1" },
        { "creatures.whiskers.broodling.Basic.Grunt", "world.dungeon00.mob.melee03.rank1" },
        { "creatures.whiskers.blademaster.Basic.Grunt", "world.dungeon00.mob.melee03.rank1" },
    }},
    { "dungeon00_level02", new Dictionary<string, string> {
        { "creatures.forestCreatures.Warg.Basic.Pup", "world.dungeon00.mob.melee01.rank2" },
        { "creatures.forestCreatures.Warg.Basic.Grunt", "world.dungeon00.mob.melee02.Rank2" },
        { "creatures.whiskers.broodling.Basic.Grunt", "world.dungeon00.mob.melee03.rank2" },
        { "creatures.whiskers.blademaster.Basic.Grunt", "world.dungeon00.mob.melee03.rank2" },
    }},
    { "dungeon00_level03", new Dictionary<string, string> {
        { "creatures.forestCreatures.Warg.Basic.Pup", "world.dungeon00.mob.melee01.rank3" },
        { "creatures.forestCreatures.Warg.Basic.Grunt", "world.dungeon00.mob.melee02.Rank3" },
        { "creatures.whiskers.broodling.Basic.Grunt", "world.dungeon00.mob.melee03.rank3" },
        { "creatures.whiskers.blademaster.Basic.Grunt", "world.dungeon00.mob.melee03.rank3" },
    }},
};

        private static string ResolveCreaturePath(string zoneName, string baseGcType)
        {
            string mode = DungeonRunners.Combat.CombatManager.Instance.GetBehaviorMode(zoneName);
            if (mode != "dungeon_specific") return baseGcType;

            if (DungeonCreaturePaths.TryGetValue(zoneName, out var map))
                if (map.TryGetValue(baseGcType, out string dungeonPath))
                    return dungeonPath;

            return baseGcType;
        }
        // ═══════════════════════════════════════════════════════════════
        // WALKABILITY HELPER — uses PathMap to avoid spawning in walls/trees
        // ═══════════════════════════════════════════════════════════════

        // A spot is only "open" if itself AND all 4 cardinal neighbors (20u out) are walkable.
        // Kills cliff-edge, tree-trunk, and wall-adjacent spawns that visually look clipped.
        private static bool IsOpenSpot(string zoneName, float x, float y)
        {
            var pm = DungeonRunners.Core.PathMapManager.Instance;
            if (!pm.IsWalkable(zoneName, x, y)) return false;
            if (!pm.IsWalkable(zoneName, x + 20f, y)) return false;
            if (!pm.IsWalkable(zoneName, x - 20f, y)) return false;
            if (!pm.IsWalkable(zoneName, x, y + 20f)) return false;
            if (!pm.IsWalkable(zoneName, x, y - 20f)) return false;
            return true;
        }

        internal static (float x, float y, bool found) FindWalkableSpot(string zoneName, float x, float y, float radius = 250f)
        {
            if (IsOpenSpot(zoneName, x, y))
                return (x, y, true);

            for (float r = 5f; r <= radius; r += 5f)
            {
                for (float angle = 0; angle < 360; angle += 20)
                {
                    float rad = angle * Mathf.Deg2Rad;
                    float testX = x + Mathf.Cos(rad) * r;
                    float testY = y + Mathf.Sin(rad) * r;
                    if (IsOpenSpot(zoneName, testX, testY))
                        return (testX, testY, true);
                }
            }
            return (x, y, false);
        }


        // ═══════════════════════════════════════════════════════════════
        // PUBLIC API
        // ═══════════════════════════════════════════════════════════════

        public static bool IsProceduralZone(string zoneName)
        {
            return LevelDefs.ContainsKey(zoneName);
        }

        public static bool TryGetMazeDimensions(string zoneName,
            out int width, out int height, out int entryX, out int entryY,
            out int randomness, out int sparseness, out int deadEndRemoval)
        {
            if (LevelDefs.TryGetValue(zoneName, out var def))
            {
                width = def.MazeWidth;
                height = def.MazeHeight;
                entryX = def.EntryGridX;
                entryY = def.EntryGridY;
                randomness = def.MazeRandomness;
                sparseness = def.MazeSparseness;
                deadEndRemoval = def.MazeDeadEndRemovalChance;
                return true;
            }
            width = height = entryX = entryY = randomness = sparseness = deadEndRemoval = 0;
            return false;
        }

        private static string CellKey(MazeGenerator.MazeCell cell)
        {
            return $"{cell.GridX}:{cell.GridY}";
        }

        private static List<MazeGenerator.MazeCell> SelectLeaderCells(List<MazeGenerator.MazeCell> cells, LevelDef level)
        {
            var selected = new List<MazeGenerator.MazeCell>();
            if (level.LeaderEncounterTable == null || level.LeaderGridYs == null)
                return selected;

            var used = new HashSet<string>();
            foreach (int leaderY in level.LeaderGridYs)
            {
                MazeGenerator.MazeCell bestCell = null;
                int bestDist = int.MaxValue;

                foreach (var cell in cells)
                {
                    if (cell.GridY != leaderY)
                        continue;

                    string key = CellKey(cell);
                    if (used.Contains(key))
                        continue;

                    int dist = Math.Abs(cell.GridX - level.MazeWidth / 2);
                    if (bestCell == null || dist < bestDist)
                    {
                        bestCell = cell;
                        bestDist = dist;
                    }
                }

                if (bestCell != null)
                {
                    selected.Add(bestCell);
                    used.Add(CellKey(bestCell));
                }
            }

            return selected;
        }

        public static List<DatabaseLoader.DungeonSpawnData> GenerateSpawns(string zoneName, uint seed = 0xBEEFBEEF)
        {
            var spawns = new List<DatabaseLoader.DungeonSpawnData>();

            if (!LevelDefs.TryGetValue(zoneName, out LevelDef level))
            {
                Debug.LogError($"[MazeSpawner] No level definition for zone '{zoneName}'");
                return spawns;
            }

            Debug.LogError($"[MazeSpawner] ═══════════════════════════════════════════════════");
            Debug.LogError($"[MazeSpawner] GENERATING MAZE for {zoneName}");
            Debug.LogError($"[MazeSpawner]   Size: {level.MazeWidth}x{level.MazeHeight}  Seed: 0x{seed:X8}");

            // 1. Generate the maze
            var maze = new MazeGenerator(
                level.MazeWidth, level.MazeHeight, seed,
                level.MazeRandomness, level.MazeSparseness,
                level.MazeDeadEndRemovalChance
            );
            var pmForCenter = DungeonRunners.Core.PathMapManager.Instance.GetPathMap(zoneName);
            if (pmForCenter != null)
            {
                maze.CenterOverrideX = (pmForCenter.MinWorldX + pmForCenter.MaxWorldX) / 2f;
                maze.CenterOverrideY = (pmForCenter.MinWorldY + pmForCenter.MaxWorldY) / 2f;
                Debug.LogError($"[MazeSpawner]   Zone center override: ({maze.CenterOverrideX:F0}, {maze.CenterOverrideY:F0})");
            }
            var cells = maze.Generate();

            Debug.LogError($"[MazeSpawner]   Generated {cells.Count} cells");

            // 2. Place encounters at tile encounter markers
            int encIdx = 0;
            int totalRegular = 0;

            // Process cells top-to-bottom, left-to-right (match client iteration order)
            cells.Sort((a, b) =>
            {
                int cmp = b.GridY.CompareTo(a.GridY);
                return cmp != 0 ? cmp : a.GridX.CompareTo(b.GridX);
            });

            var leaderCells = SelectLeaderCells(cells, level);
            var leaderCellKeys = new HashSet<string>();
            foreach (var leaderCell in leaderCells)
                leaderCellKeys.Add(CellKey(leaderCell));

            foreach (var cell in cells)
            {
                if (leaderCellKeys.Contains(CellKey(cell)))
                    continue;

                if ((cell.GridX == level.EntryGridX && cell.GridY == level.EntryGridY) ||
                    (cell.GridX == level.ExitGridX && cell.GridY == level.ExitGridY))
                    continue;

                if (!TileEncounters.TryGetValue(cell.TileType, out var markers) || markers.Count == 0)
                    continue;

                foreach (var marker in markers)
                {
                    float worldX = cell.WorldOriginX + marker.LocalX;
                    float worldY = cell.WorldOriginY + marker.LocalY;
                    float worldZ = marker.LocalZ;

                    int groupOrdinal = encIdx;
                    string groupKey = $"{zoneName}:enc:{groupOrdinal}";
                    var group = level.EncounterTable[maze.NextInt(0, level.EncounterTable.Length)];
                    encIdx++;

                    int mobsInGroup = 0;
                    foreach (var unit in group)
                    {
                        for (int i = 0; i < unit.Count; i++)
                        {
                            int offsetIdx = mobsInGroup % GroupOffsets.Length;
                            float offX = GroupOffsets[offsetIdx][0] + maze.NextFloat(-12f, 12f);
                            float offY = GroupOffsets[offsetIdx][1] + maze.NextFloat(-12f, 12f);

                            var (safeX, safeY, foundW) = FindWalkableSpot(zoneName, worldX + offX, worldY + offY);
                            if (!foundW) { mobsInGroup++; continue; }

                            spawns.Add(new DatabaseLoader.DungeonSpawnData
                            {
                                zoneName = zoneName,
                                gcType = unit.GcType,
                                posX = safeX,
                                posY = safeY,
                                posZ = DungeonRunners.Core.PathMapManager.Instance.GetHeight(zoneName, safeX, safeY, worldZ) + 3f,
                                heading = maze.NextFloat(0f, 360f),
                                encounterGroupKey = groupKey,
                                encounterDifficulty = unit.Difficulty
                            });
                            totalRegular++;
                            mobsInGroup++;
                        }
                    }
                }
            }

            Debug.LogError($"[MazeSpawner]   Placed {totalRegular} regular mobs from {encIdx} markers");

            // 3. Place leader encounters at special room rows
            int leaderIdx = 0;
            int totalLeaders = 0;

            if (level.LeaderEncounterTable != null && level.LeaderGridYs != null)
            {
                foreach (var bestCell in leaderCells)
                {
                    int leaderGroupOrdinal = leaderIdx;
                    string leaderGroupKey = $"{zoneName}:leader:{leaderGroupOrdinal}";
                    var leaderGroup = level.LeaderEncounterTable[maze.NextInt(0, level.LeaderEncounterTable.Length)];
                    leaderIdx++;

                    int leaderMobs = 0;
                    foreach (var unit in leaderGroup)
                    {
                        for (int i = 0; i < unit.Count; i++)
                        {
                            int offsetIdx = leaderMobs % GroupOffsets.Length;
                            float offX = GroupOffsets[offsetIdx][0] + maze.NextFloat(-12f, 12f);
                            float offY = GroupOffsets[offsetIdx][1] + maze.NextFloat(-12f, 12f);

                            var (safeX, safeY, foundW) = FindWalkableSpot(zoneName, bestCell.WorldCenterX + offX, bestCell.WorldCenterY + offY);
                            if (!foundW) { leaderMobs++; continue; }
                            spawns.Add(new DatabaseLoader.DungeonSpawnData
                            {
                                zoneName = zoneName,
                                gcType = unit.GcType,
                                posX = safeX,
                                posY = safeY,
                                posZ = DungeonRunners.Core.PathMapManager.Instance.GetHeight(zoneName, safeX, safeY, 0f) + 3f,
                                heading = maze.NextFloat(0f, 360f),
                                encounterGroupKey = leaderGroupKey,
                                encounterDifficulty = unit.Difficulty
                            });
                            totalLeaders++;
                            leaderMobs++;
                        }
                    }
                }
            }

            Debug.LogError($"[MazeSpawner] ✅ TOTAL: {spawns.Count} mobs ({totalRegular} regular + {totalLeaders} leaders) for {zoneName}");
            return spawns;
        }
    }
}
