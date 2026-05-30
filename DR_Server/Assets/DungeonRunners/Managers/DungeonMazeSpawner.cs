using System;
using System.Collections.Generic;
using UnityEngine;
using DungeonRunners.Combat;

namespace DungeonRunners.Managers
{
    public static class DungeonMazeSpawner
    {
        public sealed class ProceduralDungeonSnapshot
        {
            public string ZoneName;
            public uint LayoutSeed;
            public uint RoomSeed;
            public int MazeWidth;
            public int MazeHeight;
            public int EntryGridX;
            public int EntryGridY;
            public int ExitGridX;
            public int ExitGridY;
            public Vector3 PlayerSpawn;
            public float PlayerHeading;
            public Vector3 ExitPlayerSpawn;
            public float ExitPlayerHeading;
            public Vector3 EntryPortalSpawn;
            public float EntryPortalHeading;
            public Vector3 ExitPortalSpawn;
            public float ExitPortalHeading;
            public Vector3 PortalSpawn;
            public float PortalHeading;
            public int EntrySourceIndex = -1;
            public int ExitSourceIndex = -1;
            public string EntryTileType;
            public string ExitTileType;
            public string EntryLinkToZone;
            public string EntryLinkToSpawn;
            public string EntrySpawnName;
            public string EntryPortalGcType;
            public string ExitLinkToZone;
            public string ExitLinkToSpawn;
            public string ExitSpawnName;
            public string ExitPortalGcType;
            public string PlayerAnchorSource;
            public string ExitPlayerAnchorSource;
            public string EntryPortalAnchorSource;
            public string ExitPortalAnchorSource;
            public string PortalAnchorSource;
            public Vector3 PlayerAnchorLocal;
            public Vector3 ExitPlayerAnchorLocal;
            public Vector3 EntryPortalAnchorLocal;
            public Vector3 ExitPortalAnchorLocal;
            public Vector3 PortalAnchorLocal;
            public bool PlayerAnchorWalkable;
            public bool ExitPlayerAnchorWalkable;
            public bool EntryPortalAnchorWalkable;
            public bool ExitPortalAnchorWalkable;
            public bool PortalAnchorWalkable;
            public List<MazeGenerator.MazeCell> Cells = new();
            public List<MazeGenerator.PlacedRoomNode> RoomNodes = new();
            public List<DatabaseLoader.DungeonSpawnData> Spawns = new();
        }

        // ═══════════════════════════════════════════════════════════════
        // PKG effective tile encounter placeholders.
        // Local coordinates within a 400x400 tile.
        // ═══════════════════════════════════════════════════════════════

        private struct EncounterMarker
        {
            public float LocalX;
            public float LocalY;
            public float LocalZ;
            public float Heading;
            public float SizeX;
            public float SizeY;
            public string Source;

            public EncounterMarker(float x, float y, float z, float heading = 0f, float sizeX = 0f, float sizeY = 0f, string source = "PKG tile placeholder")
            {
                LocalX = x; LocalY = y; LocalZ = z;
                Heading = heading;
                SizeX = sizeX;
                SizeY = sizeY;
                Source = source;
            }
        }

        private struct AuthoredAnchor
        {
            public float LocalX;
            public float LocalY;
            public float LocalZ;
            public float Heading;
            public string Source;

            public AuthoredAnchor(float x, float y, float z, float heading, string source)
            {
                LocalX = x;
                LocalY = y;
                LocalZ = z;
                Heading = heading;
                Source = source;
            }
        }

        private static readonly Dictionary<string, List<EncounterMarker>> TileEncounterPlaceholders =
     new Dictionary<string, List<EncounterMarker>>(StringComparer.OrdinalIgnoreCase)
 {
    { "elmforest_tileset_1n_a",       new List<EncounterMarker> { new(200, 80, 10) } },
    { "elmforest_tileset_1e_a",       new List<EncounterMarker> { new(180, 200, 30, 0f, 0f, 150f) } },
    { "elmforest_tileset_1s_a",       new List<EncounterMarker> { new(210, 340, 10) } },
    { "elmforest_tileset_1w_a",       new List<EncounterMarker> { new(340, 210, 10) } },
    { "elmforest_tileset_1n1s_a",     new List<EncounterMarker> { new(160, 200, -10) } },
    { "elmforest_tileset_1e1w_a",     new List<EncounterMarker> { new(230, 170, 10, -180f, 125f, 0f) } },
    { "elmforest_tileset_1n1e_a",     new List<EncounterMarker> { new(230, 70, 10, -180f, 85f, 85f), new(70, 250, 10) } },
    { "elmforest_tileset_1n1w_a",     new List<EncounterMarker> { new(250, 210, 30) } },
    { "elmforest_tileset_1e1s_a",     new List<EncounterMarker> { new(180, 310, 10, 0f, 150f, 150f) } },
    { "elmforest_tileset_1s1w_a",     new List<EncounterMarker> { new(320, 180, 10), new(160, 270, 10) } },
    { "elmforest_tileset_1n1e1s_a",   new List<EncounterMarker> { new(220, 280, 10), new(90, 110, 10) } },
    { "elmforest_tileset_1n1e1w_a",   new List<EncounterMarker> { new(200, 120, 10) } },
    { "elmforest_tileset_1n1s1w_a",   new List<EncounterMarker> { new(200, 190, 10) } },
    { "elmforest_tileset_1e1s1w_a",   new List<EncounterMarker> { new(220, 150, 30) } },
    { "elmforest_tileset_1n1e1s1w_a", new List<EncounterMarker> { new(190, 190, 10) } },
    { "tutorial_loot_1n",             new List<EncounterMarker> { new(200, 80, 10, -180f) } },
    { "tutorial_loot_1e",             new List<EncounterMarker> { new(180, 200, 30, 0f, 100f, 150f) } },
    { "tutorial_loot_1s",             new List<EncounterMarker> { new(190, 360, 10, -900f) } },
    { "tutorial_loot_1w",             new List<EncounterMarker> { new(340, 210, 10, -900f) } },
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
            public string SpawnGcTypeOverride;
            public int Count;
            public float Difficulty;
            public SpawnUnit(string gcType, int count, float difficulty = 1f, string spawnGcTypeOverride = null)
            {
                GcType = gcType;
                Count = count;
                Difficulty = difficulty;
                SpawnGcTypeOverride = spawnGcTypeOverride;
            }
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
            new[] { new SpawnUnit("creatures.forestCreatures.Warg.Basic.Pup", 1, 0.75f) },
            new[] { new SpawnUnit("creatures.whiskers.broodling.Basic.Grunt", 1, 0.75f) },
            new[] { new SpawnUnit("creatures.forestCreatures.Warg.Basic.Pup", 1, 0.25f),
                    new SpawnUnit("creatures.whiskers.broodling.Basic.Grunt", 1, 0.50f) },
            new[] { new SpawnUnit("creatures.forestCreatures.Warg.Basic.Grunt", 1, 0.50f) },
            new[] { new SpawnUnit("creatures.forestCreatures.Warg.Basic.Pup", 1, 0.50f),
                    new SpawnUnit("creatures.whiskers.broodling.Basic.Grunt", 1, 0.50f) },
            new[] { new SpawnUnit("creatures.whiskers.broodling.Basic.Grunt", 1, 0.25f),
                    new SpawnUnit("creatures.forestCreatures.Warg.Basic.Pup", 1, 0.50f) },
            new[] { new SpawnUnit("creatures.forestCreatures.Warg.Basic.Pup", 1, 0.25f),
                    new SpawnUnit("creatures.whiskers.broodling.Basic.Grunt", 1, 0.50f) },
            new[] { new SpawnUnit("creatures.whiskers.blademaster.Basic.Grunt", 1, 0.50f) },
        };

        // level02_leader_encounter.gc
        private static readonly SpawnUnit[][] Level02LeaderEncounter =
        {
            new[] { new SpawnUnit("creatures.forestCreatures.Warg.Basic.Grunt", 1, 0f),
                    new SpawnUnit("creatures.forestCreatures.Warg.Basic.Pup", 1, 0.60f) },
            new[] { new SpawnUnit("creatures.whiskers.blademaster.Basic.Grunt", 1, 0f),
                    new SpawnUnit("creatures.whiskers.broodling.Basic.Grunt", 1, 0.60f) },
        };

        // ---------------------------------------------------------------
        // level03_encounter.gc  (rank3 mobs)
        // ---------------------------------------------------------------
        private static readonly SpawnUnit[][] Level03Encounter =
        {
            new[] { new SpawnUnit("creatures.forestCreatures.Warg.Basic.Pup", 1, 0.75f) },
            new[] { new SpawnUnit("creatures.whiskers.broodling.Basic.Grunt", 1, 0.75f) },
            new[] { new SpawnUnit("creatures.forestCreatures.Warg.Basic.Pup", 1, 0.50f),
                    new SpawnUnit("creatures.whiskers.broodling.Basic.Grunt", 1, 0.50f) },
            new[] { new SpawnUnit("creatures.whiskers.broodling.Basic.Grunt", 1, 0.50f),
                    new SpawnUnit("creatures.forestCreatures.Warg.Basic.Pup", 1, 0.25f) },
            new[] { new SpawnUnit("creatures.whiskers.blademaster.Basic.Grunt", 1, 0.25f),
                    new SpawnUnit("creatures.whiskers.broodling.Basic.Grunt", 1, 0.50f) },
            new[] { new SpawnUnit("creatures.forestCreatures.Warg.Basic.Grunt", 1, 0f),
                    new SpawnUnit("creatures.whiskers.broodling.Basic.Grunt", 1, 0.50f) },
            new[] { new SpawnUnit("creatures.forestCreatures.Warg.Basic.Grunt", 1, 0f),
                    new SpawnUnit("creatures.forestCreatures.Warg.Basic.Pup", 1, 0.50f) },
        };

        // level03_leader_encounter.gc
        private static readonly SpawnUnit[][] Level03LeaderEncounter =
        {
            new[] { new SpawnUnit("creatures.forestCreatures.Warg.Basic.Grunt", 1, 0.25f),
                    new SpawnUnit("creatures.forestCreatures.Warg.Basic.Pup", 1, 0.75f) },
            new[] { new SpawnUnit("creatures.whiskers.blademaster.Basic.Grunt", 1, 0.25f),
                    new SpawnUnit("creatures.whiskers.broodling.Basic.Grunt", 1, 0.75f) },
        };

        // level04_encounter.gc, used by dungeon00_level03_boss static encounter markers.
        private static readonly SpawnUnit[][] Level04Encounter =
        {
            new[] { new SpawnUnit("creatures.whiskers.broodling.Basic.Grunt", 1, 0.75f),
                    new SpawnUnit("terrain.misc.interactives.Breakiable_Barrel_01", 1, 0f),
                    new SpawnUnit("terrain.misc.interactives.Breakiable_Barrel_01", 1, 0f) },
            new[] { new SpawnUnit("creatures.whiskers.broodling.Basic.Grunt", 1, 1.0f),
                    new SpawnUnit("terrain.misc.interactives.Breakiable_Barrel_01", 1, 0f) },
            new[] { new SpawnUnit("creatures.whiskers.broodling.Basic.Grunt", 1, 0.65f),
                    new SpawnUnit("creatures.forestCreatures.Warg.Basic.Pup", 1, 0.35f),
                    new SpawnUnit("terrain.misc.interactives.Breakiable_Barrel_01", 1, 0f) },
            new[] { new SpawnUnit("creatures.whiskers.broodling.Basic.Grunt", 1, 0.65f),
                    new SpawnUnit("creatures.forestCreatures.Warg.Basic.Grunt", 1, 0.35f),
                    new SpawnUnit("terrain.misc.interactives.Breakiable_Barrel_01", 1, 0f) },
            new[] { new SpawnUnit("creatures.whiskers.broodling.Basic.Grunt", 1, 0.65f),
                    new SpawnUnit("creatures.whiskers.blademaster.Basic.Grunt", 1, 0.35f),
                    new SpawnUnit("terrain.misc.interactives.Breakiable_Barrel_01", 1, 0f) },
            new[] { new SpawnUnit("creatures.whiskers.broodling.Basic.Grunt", 1, 0.65f),
                    new SpawnUnit("creatures.whiskers.blademaster.Basic.Grunt", 1, 0.35f),
                    new SpawnUnit("terrain.misc.interactives.Breakiable_Barrel_01", 1, 0f) },
        };

        // level04_leader_encounter.gc, used by LootGuardEncounter in dungeon00_level03_boss.
        private static readonly SpawnUnit[][] Level04LeaderEncounter =
        {
            new[] { new SpawnUnit("creatures.whiskers.blademaster.Basic.Grunt", 1, 0f),
                    new SpawnUnit("creatures.whiskers.broodling.Basic.Grunt", 1, 0.5f) },
            new[] { new SpawnUnit("creatures.whiskers.blademaster.Basic.Grunt", 1, 0f),
                    new SpawnUnit("creatures.forestCreatures.Warg.Basic.Pup", 1, 0.5f) },
            new[] { new SpawnUnit("creatures.forestCreatures.Warg.Basic.Grunt", 1, 0f),
                    new SpawnUnit("creatures.whiskers.broodling.Basic.Grunt", 1, 0.5f) },
            new[] { new SpawnUnit("creatures.forestCreatures.Warg.Basic.Grunt", 1, 0f),
                    new SpawnUnit("creatures.forestCreatures.Warg.Basic.Pup", 1, 0.5f) },
        };


        // ═══════════════════════════════════════════════════════════════
        // LEVEL DEFINITIONS
        // ═══════════════════════════════════════════════════════════════

        private class RoomNodeDef
        {
            public string TileSet;
            public int? GridX;
            public int? GridY;
            public int Chance = 100;
            public SpawnUnit[][] EncounterTable;
            public string LinkToSpawn;
            public string LinkToZone;
            public string SpawnName;
            public string PortalGcType;
        }

        private class LevelDef
        {
            public int MazeWidth;
            public int MazeHeight;
            public int MazeRandomness;
            public int MazeSparseness;
            public int MazeDeadEndRemovalChance;
            public RoomNodeDef[] RoomNodes;
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
                    RoomNodes = new[]
                    {
                        new RoomNodeDef { TileSet = "elmforest_hub_", GridY = 4, LinkToSpawn = "start1", LinkToZone = "tutorial", SpawnName = "start1", PortalGcType = "misc.zoneportal_hub" },
                        new RoomNodeDef { TileSet = "elmforest_down_", GridY = 0, LinkToSpawn = "start2", LinkToZone = "dungeon00_level02", SpawnName = "start2", PortalGcType = "misc.zoneportal_agg" },
                        new RoomNodeDef { TileSet = "elmforest_questfindring_", GridY = 3, EncounterTable = Level01LeaderEncounter },
                        new RoomNodeDef { TileSet = "tutorial_loot_", GridY = 2, EncounterTable = Level01LeaderEncounter },
                    },
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
                    RoomNodes = new[]
                    {
                        new RoomNodeDef { TileSet = "elmforest_up_", GridY = 3, LinkToSpawn = "start2", LinkToZone = "dungeon00_level01", SpawnName = "start2", PortalGcType = "misc.zoneportal_agg" },
                        new RoomNodeDef { TileSet = "elmforest_down_", GridY = 0, LinkToSpawn = "start3", LinkToZone = "dungeon00_level03", SpawnName = "start3", PortalGcType = "misc.zoneportal_agg" },
                        new RoomNodeDef { TileSet = "tutorial_loot_", GridY = 1, EncounterTable = Level02LeaderEncounter },
                    },
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
                    RoomNodes = new[]
                    {
                        new RoomNodeDef { TileSet = "elmforest_up_", GridY = 3, LinkToSpawn = "start3", LinkToZone = "dungeon00_level02", SpawnName = "start3", PortalGcType = "misc.zoneportal_agg" },
                        new RoomNodeDef { TileSet = "elmforest_undergroundentrance_", GridY = 0, LinkToSpawn = "boss_spawn", LinkToZone = "dungeon00_level03_boss", SpawnName = "boss_spawn", PortalGcType = "misc.zoneportal_agg" },
                        new RoomNodeDef { TileSet = "tutorial_loot_", GridY = 1, EncounterTable = Level03LeaderEncounter },
                    },
                    EncounterTable = Level03Encounter,
                    LeaderEncounterTable = Level03LeaderEncounter,
                    LeaderGridYs = new[] { 1 },
                    EntryGridX = 3, EntryGridY = 3,
                    ExitGridX = 3, ExitGridY = 0,
                }
            },
        };
        // Dungeon-specific creature paths (carry per-dungeon behavior overrides)
        private static readonly Dictionary<string, Dictionary<string, string>> DungeonCreaturePaths = new()
{
    { "dungeon00_level01", new Dictionary<string, string> {
        { "creatures.forestCreatures.Warg.Basic.Pup", "world.dungeon00.mob.melee01.rank1" },
        { "creatures.forestCreatures.Warg.Basic.Grunt", "world.dungeon00.mob.melee02.Rank1" },
        { "creatures.whiskers.broodling.Basic.Grunt", "world.dungeon00.mob.melee03.rank1" },
        { "creatures.whiskers.blademaster.Basic.Grunt", "world.dungeon00.mob.melee04.rank1" },
    }},
    { "dungeon00_level02", new Dictionary<string, string> {
        { "creatures.forestCreatures.Warg.Basic.Pup", "world.dungeon00.mob.melee01.rank2" },
        { "creatures.forestCreatures.Warg.Basic.Grunt", "world.dungeon00.mob.melee02.Rank2" },
        { "creatures.whiskers.broodling.Basic.Grunt", "world.dungeon00.mob.melee03.rank2" },
        { "creatures.whiskers.blademaster.Basic.Grunt", "world.dungeon00.mob.melee04.rank2" },
    }},
    { "dungeon00_level03", new Dictionary<string, string> {
        { "creatures.forestCreatures.Warg.Basic.Pup", "world.dungeon00.mob.melee01.rank3" },
        { "creatures.forestCreatures.Warg.Basic.Grunt", "world.dungeon00.mob.melee02.Rank3" },
        { "creatures.whiskers.broodling.Basic.Grunt", "world.dungeon00.mob.melee03.rank3" },
        { "creatures.whiskers.blademaster.Basic.Grunt", "world.dungeon00.mob.melee04.rank3" },
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

        private const float DefaultEncounterFootprint = 100f;
        private const float MinEncounterUnitSpacing = 24f;

        private static int CountSpawnSlots(SpawnUnit[] group)
        {
            int count = 0;
            foreach (var unit in group)
                count += Math.Max(0, unit.Count);
            return count;
        }

        private static float MarkerExtentX(EncounterMarker marker)
        {
            return marker.SizeX > 0f ? marker.SizeX : DefaultEncounterFootprint;
        }

        private static float MarkerExtentY(EncounterMarker marker)
        {
            return marker.SizeY > 0f ? marker.SizeY : DefaultEncounterFootprint;
        }

        private static float MarkerSearchRadius(EncounterMarker marker)
        {
            float sizeX = MarkerExtentX(marker);
            float sizeY = MarkerExtentY(marker);
            return Mathf.Clamp(Mathf.Max(sizeX, sizeY) * 1.5f, 80f, 250f);
        }

        private static (float x, float y) ResolveEncounterSpawnPoint(EncounterMarker marker, float worldX, float worldY, int slotIndex, int slotCount)
        {
            if (slotCount <= 1)
                return (worldX, worldY);

            float offsetX = 0f;
            float offsetY = 0f;
            float radiusX = Mathf.Min(MarkerExtentX(marker) * 0.35f, 35f);
            float radiusY = Mathf.Min(MarkerExtentY(marker) * 0.35f, 35f);

            if (slotCount == 2)
            {
                float side = slotIndex == 0 ? -1f : 1f;
                if (MarkerExtentY(marker) > MarkerExtentX(marker))
                    offsetY = side * radiusY;
                else
                    offsetX = side * radiusX;
            }
            else
            {
                float angle = (360f / slotCount) * slotIndex;
                float rad = angle * Mathf.Deg2Rad;
                offsetX = Mathf.Cos(rad) * radiusX;
                offsetY = Mathf.Sin(rad) * radiusY;
            }

            float heading = NormalizeHeading(marker.Heading) * Mathf.Deg2Rad;
            float cos = Mathf.Cos(heading);
            float sin = Mathf.Sin(heading);
            return (worldX + offsetX * cos - offsetY * sin, worldY + offsetX * sin + offsetY * cos);
        }

        private static Vector3 ResolveEncounterSpawnPoint(
            MazeGenerator.MazeCell cell,
            EncounterMarker marker,
            int slotIndex,
            int slotCount,
            out bool groundAdjusted,
            out string groundSource)
        {
            groundAdjusted = false;
            groundSource = "none";
            if (cell == null)
                return Vector3.zero;

            float worldX = cell.WorldOriginX + marker.LocalX;
            float worldY = cell.WorldOriginY + marker.LocalY;
            var resolved = ResolveEncounterSpawnPoint(marker, worldX, worldY, slotIndex, slotCount);

            float localX = resolved.x - cell.WorldOriginX;
            float localY = resolved.y - cell.WorldOriginY;
            float clampedLocalX = Mathf.Clamp(localX, 0f, MazeGenerator.TILE_SIZE);
            float clampedLocalY = Mathf.Clamp(localY, 0f, MazeGenerator.TILE_SIZE);
            bool clamped = Mathf.Abs(clampedLocalX - localX) > 0.01f || Mathf.Abs(clampedLocalY - localY) > 0.01f;
            if (clamped)
            {
                localX = clampedLocalX;
                localY = clampedLocalY;
                resolved.x = cell.WorldOriginX + localX;
                resolved.y = cell.WorldOriginY + localY;
            }

            float groundZ = ResolveSameTileGroundZ(cell, marker, localX, localY, out groundSource);
            groundAdjusted = clamped || Mathf.Abs(groundZ - marker.LocalZ) > 0.01f;
            if (clamped)
                groundSource += ";xyClampedSameTile";

            return new Vector3(resolved.x, resolved.y, groundZ);
        }

        private static float ResolveSameTileGroundZ(MazeGenerator.MazeCell cell, EncounterMarker marker, float localX, float localY, out string source)
        {
            source = $"same-tile authored marker z={marker.LocalZ:F1}";
            float bestZ = NormalizeUnitGroundZ(marker.LocalZ);
            float bestDistSq = 0f;

            if (TileEncounterPlaceholders.TryGetValue(cell.TileType, out var markers) && markers != null)
            {
                bool found = false;
                for (int i = 0; i < markers.Count; i++)
                {
                    var candidate = markers[i];
                    float dx = localX - candidate.LocalX;
                    float dy = localY - candidate.LocalY;
                    float distSq = dx * dx + dy * dy;
                    if (!found || distSq < bestDistSq)
                    {
                        found = true;
                        bestDistSq = distSq;
                        bestZ = NormalizeUnitGroundZ(candidate.LocalZ);
                        source = $"same-tile PKG ground support tile='{cell.TileType}' marker={i} authoredZ={candidate.LocalZ:F1} groundZ={bestZ:F1}";
                    }
                }
            }

            if (Mathf.Abs(bestZ - marker.LocalZ) > 0.01f)
                source += ";unitGroundFromSupport";
            return bestZ;
        }

        private static float NormalizeUnitGroundZ(float authoredZ)
        {
            if (authoredZ > 20f || authoredZ < 0f)
                return 10f;
            return authoredZ;
        }

        private static float NormalizeHeading(float heading)
        {
            heading %= 360f;
            if (heading < 0f) heading += 360f;
            return heading;
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

        private static bool HasEncounterClearance(List<Vector2> occupied, float x, float y)
        {
            float minDistSq = MinEncounterUnitSpacing * MinEncounterUnitSpacing;
            for (int i = 0; i < occupied.Count; i++)
            {
                float dx = occupied[i].x - x;
                float dy = occupied[i].y - y;
                if (dx * dx + dy * dy < minDistSq)
                    return false;
            }
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

        internal static (float x, float y, bool found) FindWalkableSpot(string zoneName, float x, float y, float radius, List<Vector2> occupied)
        {
            if (IsOpenSpot(zoneName, x, y) && HasEncounterClearance(occupied, x, y))
                return (x, y, true);

            for (float r = 5f; r <= radius; r += 5f)
            {
                for (float angle = 0; angle < 360; angle += 20)
                {
                    float rad = angle * Mathf.Deg2Rad;
                    float testX = x + Mathf.Cos(rad) * r;
                    float testY = y + Mathf.Sin(rad) * r;
                    if (IsOpenSpot(zoneName, testX, testY) && HasEncounterClearance(occupied, testX, testY))
                        return (testX, testY, true);
                }
            }
            return (x, y, false);
        }

        private static readonly EncounterMarker[] Level03BossRegularMarkers =
        {
            new(110f, -990f, 40f),
            new(-170f, -960f, 40f),
            new(-500f, -530f, 40f),
            new(160f, -320f, 39.9961f),
            new(60f, -110f, 40f),
            new(-20f, -230f, 39.9961f),
            new(-250f, -190f, 40f),
            new(-150f, -450f, 40f),
            new(-260f, -580f, 39.9961f),
            new(-150f, -790f, 39.9961f),
            new(20f, -850f, 40f),
        };

        private static readonly EncounterMarker Level03BossLootGuardMarker =
            new(-440f, -660f, 40f);

        private static readonly (SpawnUnit Unit, float X, float Y, float Z, float Heading)[] Level03BossPosse =
        {
            (new SpawnUnit("world.dungeon00.mob.boss", 1, 1f, "world.dungeon00.mob.boss_posse.RattleTooth"), 405f, -1195f, 40f, 0f),
            (new SpawnUnit("creatures.whiskers.blademaster.Basic.Grunt", 1, 1f, "world.dungeon00.mob.boss_posse.Blademaster"), 430f, -1205f, 40f, 0f),
            (new SpawnUnit("creatures.whiskers.blademaster.Basic.Grunt", 1, 1f, "world.dungeon00.mob.boss_posse.Blademaster"), 380f, -1205f, 40f, 0f),
            (new SpawnUnit("creatures.whiskers.broodling.Basic.Grunt", 1, 1f, "world.dungeon00.mob.boss_posse.Broodling"), 450f, -1260f, 40f, 0f),
            (new SpawnUnit("creatures.whiskers.broodling.Basic.Grunt", 1, 1f, "world.dungeon00.mob.boss_posse.Broodling"), 360f, -1260f, 40f, 0f),
            (new SpawnUnit("creatures.forestCreatures.Warg.Basic.Grunt", 1, 1f, "world.dungeon00.mob.boss_posse.boss_guard"), 420f, -1230f, 40f, 0f),
            (new SpawnUnit("creatures.forestCreatures.Warg.Basic.Grunt", 1, 1f, "world.dungeon00.mob.boss_posse.boss_guard"), 390f, -1230f, 40f, 0f),
        };

        private static string NormalizeBaseZone(string zoneName)
        {
            if (string.IsNullOrEmpty(zoneName))
                return zoneName;
            int instIdx = zoneName.IndexOf("_inst", StringComparison.OrdinalIgnoreCase);
            return instIdx > 0 ? zoneName.Substring(0, instIdx) : zoneName;
        }

        private static int NextTableIndex(MersenneTwister rng, int count)
        {
            if (count <= 1)
                return 0;
            return (int)rng.Generate(0, (uint)(count - 1));
        }

        private static void AddSpawn(List<DatabaseLoader.DungeonSpawnData> spawns, string zoneName, SpawnUnit unit,
            float x, float y, float z, float heading, string groupKey)
        {
            spawns.Add(new DatabaseLoader.DungeonSpawnData
            {
                zoneName = zoneName,
                gcType = unit.GcType,
                spawnGcTypeOverride = unit.SpawnGcTypeOverride,
                posX = x,
                posY = y,
                posZ = z,
                heading = NormalizeHeading(heading),
                encounterGroupKey = groupKey,
                encounterDifficulty = unit.Difficulty
            });
        }

        private static int AddStaticEncounterSpawns(List<DatabaseLoader.DungeonSpawnData> spawns, string zoneName,
            SpawnUnit[][] table, EncounterMarker marker, string groupKey, MersenneTwister rng)
        {
            var group = table[NextTableIndex(rng, table.Length)];
            int groupSlots = CountSpawnSlots(group);
            int groupSlot = 0;
            int count = 0;
            foreach (var unit in group)
            {
                for (int i = 0; i < unit.Count; i++)
                {
                    int slot = groupSlot++;
                    var (spawnX, spawnY) = ResolveEncounterSpawnPoint(marker, marker.LocalX, marker.LocalY, slot, groupSlots);
                    AddSpawn(spawns, zoneName, unit, spawnX, spawnY, marker.LocalZ, marker.Heading, groupKey);
                    count++;
                }
            }
            return count;
        }


        // ═══════════════════════════════════════════════════════════════
        // PUBLIC API
        // ═══════════════════════════════════════════════════════════════

        public static bool IsStaticBossZone(string zoneName)
        {
            return string.Equals(NormalizeBaseZone(zoneName), "dungeon00_level03_boss", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsProceduralZone(string zoneName)
        {
            return LevelDefs.ContainsKey(NormalizeBaseZone(zoneName));
        }

        public static List<DatabaseLoader.DungeonSpawnData> GenerateStaticBossSpawns(string zoneName, uint seed = 0xBEEFBEEF)
        {
            string baseZone = NormalizeBaseZone(zoneName);
            var spawns = new List<DatabaseLoader.DungeonSpawnData>();
            if (!IsStaticBossZone(baseZone))
                return spawns;

            var rng = new MersenneTwister(seed);
            int regular = 0;
            for (int i = 0; i < Level03BossRegularMarkers.Length; i++)
                regular += AddStaticEncounterSpawns(spawns, baseZone, Level04Encounter, Level03BossRegularMarkers[i], $"{baseZone}:enc:{i}", rng);

            int leaders = AddStaticEncounterSpawns(spawns, baseZone, Level04LeaderEncounter, Level03BossLootGuardMarker, $"{baseZone}:leader:0", rng);
            foreach (var posse in Level03BossPosse)
                AddSpawn(spawns, baseZone, posse.Unit, posse.X, posse.Y, posse.Z, posse.Heading, $"{baseZone}:boss:0");

            Debug.LogError($"[MazeSpawner] BOSS STATIC TOTAL: {spawns.Count} units ({regular} regular + {leaders} loot-guard + {Level03BossPosse.Length} boss-posse) for {baseZone}");
            return spawns;
        }

        public static bool TryGetMazeDimensions(string zoneName,
            out int width, out int height, out int entryX, out int entryY,
            out int randomness, out int sparseness, out int deadEndRemoval)
        {
            if (LevelDefs.TryGetValue(NormalizeBaseZone(zoneName), out var def))
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

        private static MazeGenerator.MazeCell FindCell(List<MazeGenerator.MazeCell> cells, int gridX, int gridY)
        {
            if (cells == null) return null;
            for (int i = 0; i < cells.Count; i++)
            {
                var cell = cells[i];
                if (cell.GridX == gridX && cell.GridY == gridY)
                    return cell;
            }
            return null;
        }

        private static Vector3 ResolveCellAnchor(string zoneName, MazeGenerator.MazeCell cell, float localX, float localY, float localZ)
        {
            if (cell == null)
                return Vector3.zero;

            float x = cell.WorldOriginX + localX;
            float y = cell.WorldOriginY + localY;
            var (safeX, safeY, found) = FindWalkableSpot(zoneName, x, y);
            if (found)
            {
                x = safeX;
                y = safeY;
            }

            float z = DungeonRunners.Core.PathMapManager.Instance.GetHeight(zoneName, x, y, localZ);
            return new Vector3(x, y, z);
        }

        private static MazeGenerator.PlacedRoomNode FindPlacedRoomNode(ProceduralDungeonSnapshot snapshot, int sourceIndex, params string[] tileSetFallbacks)
        {
            if (snapshot?.RoomNodes == null)
                return null;

            for (int i = 0; i < snapshot.RoomNodes.Count; i++)
            {
                var placed = snapshot.RoomNodes[i];
                if (placed != null && placed.SourceIndex == sourceIndex)
                    return placed;
            }

            if (tileSetFallbacks == null || tileSetFallbacks.Length == 0)
                return null;

            for (int i = 0; i < snapshot.RoomNodes.Count; i++)
            {
                var placed = snapshot.RoomNodes[i];
                if (placed == null || string.IsNullOrEmpty(placed.TileSet))
                    continue;

                for (int j = 0; j < tileSetFallbacks.Length; j++)
                {
                    if (placed.TileSet.StartsWith(tileSetFallbacks[j], StringComparison.OrdinalIgnoreCase))
                        return placed;
                }
            }

            return null;
        }

        private static RoomNodeDef GetRoomNodeDef(LevelDef level, int sourceIndex)
        {
            if (level?.RoomNodes == null || sourceIndex < 0 || sourceIndex >= level.RoomNodes.Length)
                return null;
            return level.RoomNodes[sourceIndex];
        }

        private static bool MatchesSpawnName(string requested, string spawnName, string linkToSpawn)
        {
            if (string.IsNullOrWhiteSpace(requested))
                return false;

            string normalized = requested.Trim();
            return (!string.IsNullOrEmpty(spawnName) && normalized.Equals(spawnName, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(linkToSpawn) && normalized.Equals(linkToSpawn, StringComparison.OrdinalIgnoreCase));
        }

        public static bool TryResolveSpawnPoint(
            ProceduralDungeonSnapshot snapshot,
            string spawnPoint,
            out Vector3 position,
            out float heading,
            out int sourceIndex,
            out string tileType,
            out int gridX,
            out int gridY,
            out Vector3 local,
            out string source)
        {
            position = Vector3.zero;
            heading = 0f;
            sourceIndex = -1;
            tileType = "";
            gridX = 0;
            gridY = 0;
            local = Vector3.zero;
            source = "";

            if (snapshot == null || string.IsNullOrWhiteSpace(spawnPoint))
                return false;

            if (MatchesSpawnName(spawnPoint, snapshot.EntrySpawnName, snapshot.EntryLinkToSpawn))
            {
                position = snapshot.PlayerSpawn;
                heading = snapshot.PlayerHeading;
                sourceIndex = snapshot.EntrySourceIndex;
                tileType = snapshot.EntryTileType;
                gridX = snapshot.EntryGridX;
                gridY = snapshot.EntryGridY;
                local = snapshot.PlayerAnchorLocal;
                source = snapshot.PlayerAnchorSource;
                return true;
            }

            if (MatchesSpawnName(spawnPoint, snapshot.ExitSpawnName, snapshot.ExitLinkToSpawn))
            {
                position = snapshot.ExitPlayerSpawn;
                heading = snapshot.ExitPlayerHeading;
                sourceIndex = snapshot.ExitSourceIndex;
                tileType = snapshot.ExitTileType;
                gridX = snapshot.ExitGridX;
                gridY = snapshot.ExitGridY;
                local = snapshot.ExitPlayerAnchorLocal;
                source = snapshot.ExitPlayerAnchorSource;
                return true;
            }

            return false;
        }

        private static string PortalRoomSuffix(string tileType)
        {
            if (string.IsNullOrEmpty(tileType))
                return null;

            string[] prefixes =
            {
                "elmforest_hub_",
                "elmforest_down_",
                "elmforest_up_"
            };

            for (int i = 0; i < prefixes.Length; i++)
            {
                string prefix = prefixes[i];
                if (tileType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return tileType.Substring(prefix.Length);
            }

            return null;
        }

        private static bool TryGetAuthoredDungeonAnchor(string tileType, bool playerSpawn, out AuthoredAnchor anchor)
        {
            anchor = default;
            string source = playerSpawn ? "PKG misc.Waypoint SpawnPoint" : "PKG zoneportal model";

            if (!string.IsNullOrEmpty(tileType) &&
                tileType.StartsWith("elmforest_undergroundentrance_", StringComparison.OrdinalIgnoreCase))
            {
                string suffix = tileType.Substring("elmforest_undergroundentrance_".Length);
                if (playerSpawn)
                {
                    switch (suffix)
                    {
                        case "1n":
                            anchor = new AuthoredAnchor(234f, 73f, 10f, -180f, "PKG misc.Waypoint SpawnPoint");
                            return true;
                        case "1e":
                            anchor = new AuthoredAnchor(70f, 170f, 10f, 90f, "PKG misc.Waypoint SpawnPoint");
                            return true;
                        case "1s":
                            anchor = new AuthoredAnchor(165f, 305f, 10f, 0f, "PKG misc.Waypoint SpawnPoint");
                            return true;
                        case "1w":
                            anchor = new AuthoredAnchor(330f, 240f, 10f, -90f, "PKG misc.Waypoint SpawnPoint");
                            return true;
                    }
                }
                else
                {
                    switch (suffix)
                    {
                        case "1n":
                            anchor = new AuthoredAnchor(236f, 128f, 14f, 0f, "PKG misc.ZonePortal_agg");
                            return true;
                        case "1e":
                            anchor = new AuthoredAnchor(127f, 164f, 10f, 90f, "PKG misc.ZonePortal_agg");
                            return true;
                        case "1s":
                            anchor = new AuthoredAnchor(165f, 273f, 15f, 0f, "PKG misc.ZonePortal_agg");
                            return true;
                        case "1w":
                            anchor = new AuthoredAnchor(272f, 236f, 10f, 90f, "PKG misc.ZonePortal_agg");
                            return true;
                    }
                }
            }

            string portalSuffix = PortalRoomSuffix(tileType);
            if (portalSuffix == null)
                return false;

            if (playerSpawn)
            {
                switch (portalSuffix)
                {
                    case "1n":
                        anchor = new AuthoredAnchor(150f, 70f, 10f, 180f, source);
                        return true;
                    case "1e":
                        anchor = new AuthoredAnchor(100f, 245f, 10f, 90f, source);
                        return true;
                    case "1s":
                        anchor = new AuthoredAnchor(239f, 331f, 10f, 0f, source);
                        return true;
                    case "1w":
                        anchor = new AuthoredAnchor(323f, 161f, 10f, -90f, source);
                        return true;
                }
            }
            else
            {
                switch (portalSuffix)
                {
                    case "1n":
                        anchor = new AuthoredAnchor(160f, 147f, 30f, 0f, source);
                        return true;
                    case "1e":
                        anchor = new AuthoredAnchor(150f, 240f, 30f, -90f, source);
                        return true;
                    case "1s":
                        anchor = new AuthoredAnchor(240f, 251f, 30f, 0f, source);
                        return true;
                    case "1w":
                        anchor = new AuthoredAnchor(252f, 160f, 30f, 90f, source);
                        return true;
                }
            }

            return false;
        }

        private static bool IsInsideCell(MazeGenerator.MazeCell cell, float x, float y)
        {
            if (cell == null)
                return false;

            return x >= cell.WorldOriginX &&
                   x <= cell.WorldOriginX + MazeGenerator.TILE_SIZE &&
                   y >= cell.WorldOriginY &&
                   y <= cell.WorldOriginY + MazeGenerator.TILE_SIZE;
        }

        private static (float x, float y, bool found) FindWalkableSpotInCell(string zoneName, MazeGenerator.MazeCell cell, float x, float y, float radius = 60f)
        {
            if (cell == null)
                return (x, y, false);

            if (IsInsideCell(cell, x, y) && IsOpenSpot(zoneName, x, y))
                return (x, y, true);

            float maxRadius = Mathf.Min(radius, MazeGenerator.TILE_SIZE * 0.25f);
            for (float r = 5f; r <= maxRadius; r += 5f)
            {
                for (float angle = 0; angle < 360; angle += 20)
                {
                    float rad = angle * Mathf.Deg2Rad;
                    float testX = x + Mathf.Cos(rad) * r;
                    float testY = y + Mathf.Sin(rad) * r;
                    if (IsInsideCell(cell, testX, testY) && IsOpenSpot(zoneName, testX, testY))
                        return (testX, testY, true);
                }
            }

            return (x, y, false);
        }

        private static Vector3 ResolveAuthoredAnchor(string zoneName, MazeGenerator.MazeCell cell, AuthoredAnchor anchor, out bool walkable)
        {
            return ResolveAuthoredAnchor(zoneName, cell, anchor, out walkable, out _);
        }

        private static Vector3 ResolveAuthoredAnchor(string zoneName, MazeGenerator.MazeCell cell, AuthoredAnchor anchor, out bool walkable, out Vector3 rawWorld)
        {
            return ResolveAuthoredAnchor(zoneName, cell, anchor, out walkable, out rawWorld, true);
        }

        private static Vector3 ResolveAuthoredAnchor(string zoneName, MazeGenerator.MazeCell cell, AuthoredAnchor anchor, out bool walkable, out Vector3 rawWorld, bool snapToWalkable)
        {
            walkable = false;
            rawWorld = Vector3.zero;
            if (cell == null)
                return Vector3.zero;

            float x = cell.WorldOriginX + anchor.LocalX;
            float y = cell.WorldOriginY + anchor.LocalY;
            rawWorld = new Vector3(x, y, anchor.LocalZ);
            if (snapToWalkable)
            {
                var safe = FindWalkableSpotInCell(zoneName, cell, x, y);
                if (safe.found)
                {
                    x = safe.x;
                    y = safe.y;
                    walkable = true;
                }

                float z = DungeonRunners.Core.PathMapManager.Instance.GetHeight(zoneName, x, y, anchor.LocalZ);
                return new Vector3(x, y, z);
            }

            return rawWorld;
        }

        private static void ResolveSnapshotAnchors(ProceduralDungeonSnapshot snapshot, LevelDef level, string zoneName, List<MazeGenerator.MazeCell> cells)
        {
            if (snapshot == null || level == null || cells == null || cells.Count == 0)
                return;

            snapshot.MazeWidth = level.MazeWidth;
            snapshot.MazeHeight = level.MazeHeight;

            var entryNode = FindPlacedRoomNode(snapshot, 0, "elmforest_hub_", "elmforest_up_");
            var exitNode = FindPlacedRoomNode(snapshot, 1, "elmforest_down_", "elmforest_undergroundentrance_");

            var entryCell = entryNode != null
                ? FindCell(cells, entryNode.GridX, entryNode.GridY)
                : FindCell(cells, level.EntryGridX, level.EntryGridY);
            var exitCell = exitNode != null
                ? FindCell(cells, exitNode.GridX, exitNode.GridY)
                : FindCell(cells, level.ExitGridX, level.ExitGridY);

            entryCell ??= cells[0];
            exitCell ??= entryCell;

            snapshot.EntrySourceIndex = entryNode?.SourceIndex ?? -1;
            snapshot.ExitSourceIndex = exitNode?.SourceIndex ?? -1;
            var entryDef = GetRoomNodeDef(level, snapshot.EntrySourceIndex);
            var exitDef = GetRoomNodeDef(level, snapshot.ExitSourceIndex);
            snapshot.EntryGridX = entryCell.GridX;
            snapshot.EntryGridY = entryCell.GridY;
            snapshot.ExitGridX = exitCell.GridX;
            snapshot.ExitGridY = exitCell.GridY;
            snapshot.EntryTileType = entryNode?.TileType ?? entryCell.TileType;
            snapshot.ExitTileType = exitNode?.TileType ?? exitCell.TileType;
            snapshot.EntryLinkToZone = entryDef?.LinkToZone;
            snapshot.EntryLinkToSpawn = entryDef?.LinkToSpawn;
            snapshot.EntrySpawnName = entryDef?.SpawnName;
            snapshot.EntryPortalGcType = entryDef?.PortalGcType;
            snapshot.ExitLinkToZone = exitDef?.LinkToZone;
            snapshot.ExitLinkToSpawn = exitDef?.LinkToSpawn;
            snapshot.ExitSpawnName = exitDef?.SpawnName;
            snapshot.ExitPortalGcType = exitDef?.PortalGcType;

            if (!TryGetAuthoredDungeonAnchor(snapshot.EntryTileType, true, out var playerAnchor))
                playerAnchor = new AuthoredAnchor(200f, 200f, 0f, 0f, "PARTIAL fallback local tile center");
            if (!TryGetAuthoredDungeonAnchor(snapshot.ExitTileType, true, out var exitPlayerAnchor))
                exitPlayerAnchor = new AuthoredAnchor(200f, 200f, 0f, 0f, "PARTIAL fallback local tile center");
            if (!TryGetAuthoredDungeonAnchor(snapshot.EntryTileType, false, out var entryPortalAnchor))
                entryPortalAnchor = new AuthoredAnchor(200f, 200f, 0f, 0f, "PARTIAL fallback local tile center");
            if (!TryGetAuthoredDungeonAnchor(snapshot.ExitTileType, false, out var exitPortalAnchor))
                exitPortalAnchor = new AuthoredAnchor(200f, 200f, 0f, 0f, "PARTIAL fallback local tile center");

            snapshot.PlayerAnchorLocal = new Vector3(playerAnchor.LocalX, playerAnchor.LocalY, playerAnchor.LocalZ);
            snapshot.ExitPlayerAnchorLocal = new Vector3(exitPlayerAnchor.LocalX, exitPlayerAnchor.LocalY, exitPlayerAnchor.LocalZ);
            snapshot.EntryPortalAnchorLocal = new Vector3(entryPortalAnchor.LocalX, entryPortalAnchor.LocalY, entryPortalAnchor.LocalZ);
            snapshot.ExitPortalAnchorLocal = new Vector3(exitPortalAnchor.LocalX, exitPortalAnchor.LocalY, exitPortalAnchor.LocalZ);
            snapshot.PlayerAnchorSource = playerAnchor.Source;
            snapshot.ExitPlayerAnchorSource = exitPlayerAnchor.Source;
            snapshot.EntryPortalAnchorSource = entryPortalAnchor.Source;
            snapshot.ExitPortalAnchorSource = exitPortalAnchor.Source;
            snapshot.PlayerSpawn = ResolveAuthoredAnchor(zoneName, entryCell, playerAnchor, out bool playerWalkable, out Vector3 playerRawWorld, snapToWalkable: false);
            snapshot.PlayerHeading = NormalizeHeading(playerAnchor.Heading);
            snapshot.PlayerAnchorWalkable = playerWalkable;
            snapshot.ExitPlayerSpawn = ResolveAuthoredAnchor(zoneName, exitCell, exitPlayerAnchor, out bool exitPlayerWalkable, out Vector3 exitPlayerRawWorld, snapToWalkable: false);
            snapshot.ExitPlayerHeading = NormalizeHeading(exitPlayerAnchor.Heading);
            snapshot.ExitPlayerAnchorWalkable = exitPlayerWalkable;
            snapshot.EntryPortalSpawn = ResolveAuthoredAnchor(zoneName, entryCell, entryPortalAnchor, out bool entryPortalWalkable, out Vector3 entryPortalRawWorld, snapToWalkable: false);
            snapshot.EntryPortalHeading = NormalizeHeading(entryPortalAnchor.Heading);
            snapshot.EntryPortalAnchorWalkable = entryPortalWalkable;
            snapshot.ExitPortalSpawn = ResolveAuthoredAnchor(zoneName, exitCell, exitPortalAnchor, out bool exitPortalWalkable, out Vector3 exitPortalRawWorld, snapToWalkable: false);
            snapshot.ExitPortalHeading = NormalizeHeading(exitPortalAnchor.Heading);
            snapshot.ExitPortalAnchorWalkable = exitPortalWalkable;

            snapshot.PortalSpawn = snapshot.ExitPortalSpawn;
            snapshot.PortalHeading = snapshot.ExitPortalHeading;
            snapshot.PortalAnchorLocal = snapshot.ExitPortalAnchorLocal;
            snapshot.PortalAnchorSource = snapshot.ExitPortalAnchorSource;
            snapshot.PortalAnchorWalkable = snapshot.ExitPortalAnchorWalkable;

            Debug.LogError($"[DUNGEON-TRANSFORM] role=player src={snapshot.EntrySourceIndex} tile='{snapshot.EntryTileType}' grid=({snapshot.EntryGridX},{snapshot.EntryGridY}) worldGridY={entryCell.WorldGridY} origin=({entryCell.WorldOriginX:F1},{entryCell.WorldOriginY:F1}) center=({entryCell.WorldCenterX:F1},{entryCell.WorldCenterY:F1}) local=({snapshot.PlayerAnchorLocal.x:F1},{snapshot.PlayerAnchorLocal.y:F1},{snapshot.PlayerAnchorLocal.z:F1}) rawAuthoredWorld=({playerRawWorld.x:F1},{playerRawWorld.y:F1},{playerRawWorld.z:F1}) sentWorld=({snapshot.PlayerSpawn.x:F1},{snapshot.PlayerSpawn.y:F1},{snapshot.PlayerSpawn.z:F1}) snapApplied=False walkable={snapshot.PlayerAnchorWalkable}");
            Debug.LogError($"[DUNGEON-TRANSFORM] role=exit-player src={snapshot.ExitSourceIndex} tile='{snapshot.ExitTileType}' grid=({snapshot.ExitGridX},{snapshot.ExitGridY}) worldGridY={exitCell.WorldGridY} origin=({exitCell.WorldOriginX:F1},{exitCell.WorldOriginY:F1}) center=({exitCell.WorldCenterX:F1},{exitCell.WorldCenterY:F1}) local=({snapshot.ExitPlayerAnchorLocal.x:F1},{snapshot.ExitPlayerAnchorLocal.y:F1},{snapshot.ExitPlayerAnchorLocal.z:F1}) rawAuthoredWorld=({exitPlayerRawWorld.x:F1},{exitPlayerRawWorld.y:F1},{exitPlayerRawWorld.z:F1}) sentWorld=({snapshot.ExitPlayerSpawn.x:F1},{snapshot.ExitPlayerSpawn.y:F1},{snapshot.ExitPlayerSpawn.z:F1}) snapApplied=False walkable={snapshot.ExitPlayerAnchorWalkable}");
            Debug.LogError($"[DUNGEON-TRANSFORM] role=entry-portal src={snapshot.EntrySourceIndex} tile='{snapshot.EntryTileType}' grid=({snapshot.EntryGridX},{snapshot.EntryGridY}) worldGridY={entryCell.WorldGridY} origin=({entryCell.WorldOriginX:F1},{entryCell.WorldOriginY:F1}) center=({entryCell.WorldCenterX:F1},{entryCell.WorldCenterY:F1}) local=({snapshot.EntryPortalAnchorLocal.x:F1},{snapshot.EntryPortalAnchorLocal.y:F1},{snapshot.EntryPortalAnchorLocal.z:F1}) rawAuthoredWorld=({entryPortalRawWorld.x:F1},{entryPortalRawWorld.y:F1},{entryPortalRawWorld.z:F1}) sentWorld=({snapshot.EntryPortalSpawn.x:F1},{snapshot.EntryPortalSpawn.y:F1},{snapshot.EntryPortalSpawn.z:F1}) snapApplied=False walkable={snapshot.EntryPortalAnchorWalkable}");
            Debug.LogError($"[DUNGEON-TRANSFORM] role=exit-portal src={snapshot.ExitSourceIndex} tile='{snapshot.ExitTileType}' grid=({snapshot.ExitGridX},{snapshot.ExitGridY}) worldGridY={exitCell.WorldGridY} origin=({exitCell.WorldOriginX:F1},{exitCell.WorldOriginY:F1}) center=({exitCell.WorldCenterX:F1},{exitCell.WorldCenterY:F1}) local=({snapshot.ExitPortalAnchorLocal.x:F1},{snapshot.ExitPortalAnchorLocal.y:F1},{snapshot.ExitPortalAnchorLocal.z:F1}) rawAuthoredWorld=({exitPortalRawWorld.x:F1},{exitPortalRawWorld.y:F1},{exitPortalRawWorld.z:F1}) sentWorld=({snapshot.ExitPortalSpawn.x:F1},{snapshot.ExitPortalSpawn.y:F1},{snapshot.ExitPortalSpawn.z:F1}) snapApplied=False walkable={snapshot.ExitPortalAnchorWalkable}");
            Debug.LogError($"[DUNGEON-SNAPSHOT] anchor player role=entry src={snapshot.EntrySourceIndex} spawnName={snapshot.EntrySpawnName} linkToSpawn={snapshot.EntryLinkToSpawn} linkToZone={snapshot.EntryLinkToZone} tile='{snapshot.EntryTileType}' grid=({snapshot.EntryGridX},{snapshot.EntryGridY}) local=({snapshot.PlayerAnchorLocal.x:F1},{snapshot.PlayerAnchorLocal.y:F1},{snapshot.PlayerAnchorLocal.z:F1}) rawAuthoredWorld=({playerRawWorld.x:F1},{playerRawWorld.y:F1},{playerRawWorld.z:F1}) sentWorld=({snapshot.PlayerSpawn.x:F1},{snapshot.PlayerSpawn.y:F1},{snapshot.PlayerSpawn.z:F1}) heading={snapshot.PlayerHeading:F1} snapApplied=False source='{snapshot.PlayerAnchorSource}'");
            Debug.LogError($"[DUNGEON-SNAPSHOT] anchor player role=exit src={snapshot.ExitSourceIndex} spawnName={snapshot.ExitSpawnName} linkToSpawn={snapshot.ExitLinkToSpawn} linkToZone={snapshot.ExitLinkToZone} tile='{snapshot.ExitTileType}' grid=({snapshot.ExitGridX},{snapshot.ExitGridY}) local=({snapshot.ExitPlayerAnchorLocal.x:F1},{snapshot.ExitPlayerAnchorLocal.y:F1},{snapshot.ExitPlayerAnchorLocal.z:F1}) rawAuthoredWorld=({exitPlayerRawWorld.x:F1},{exitPlayerRawWorld.y:F1},{exitPlayerRawWorld.z:F1}) sentWorld=({snapshot.ExitPlayerSpawn.x:F1},{snapshot.ExitPlayerSpawn.y:F1},{snapshot.ExitPlayerSpawn.z:F1}) heading={snapshot.ExitPlayerHeading:F1} snapApplied=False source='{snapshot.ExitPlayerAnchorSource}'");
            Debug.LogError($"[DUNGEON-SNAPSHOT] anchor portal role=entry src={snapshot.EntrySourceIndex} gc={snapshot.EntryPortalGcType} target={snapshot.EntryLinkToZone} spawnPoint={snapshot.EntryLinkToSpawn} tile='{snapshot.EntryTileType}' grid=({snapshot.EntryGridX},{snapshot.EntryGridY}) local=({snapshot.EntryPortalAnchorLocal.x:F1},{snapshot.EntryPortalAnchorLocal.y:F1},{snapshot.EntryPortalAnchorLocal.z:F1}) rawAuthoredWorld=({entryPortalRawWorld.x:F1},{entryPortalRawWorld.y:F1},{entryPortalRawWorld.z:F1}) sentWorld=({snapshot.EntryPortalSpawn.x:F1},{snapshot.EntryPortalSpawn.y:F1},{snapshot.EntryPortalSpawn.z:F1}) heading={snapshot.EntryPortalHeading:F1} snapApplied=False source='{snapshot.EntryPortalAnchorSource}'");
            Debug.LogError($"[DUNGEON-SNAPSHOT] anchor portal role=exit src={snapshot.ExitSourceIndex} gc={snapshot.ExitPortalGcType} target={snapshot.ExitLinkToZone} spawnPoint={snapshot.ExitLinkToSpawn} tile='{snapshot.ExitTileType}' grid=({snapshot.ExitGridX},{snapshot.ExitGridY}) local=({snapshot.ExitPortalAnchorLocal.x:F1},{snapshot.ExitPortalAnchorLocal.y:F1},{snapshot.ExitPortalAnchorLocal.z:F1}) rawAuthoredWorld=({exitPortalRawWorld.x:F1},{exitPortalRawWorld.y:F1},{exitPortalRawWorld.z:F1}) sentWorld=({snapshot.ExitPortalSpawn.x:F1},{snapshot.ExitPortalSpawn.y:F1},{snapshot.ExitPortalSpawn.z:F1}) heading={snapshot.ExitPortalHeading:F1} snapApplied=False source='{snapshot.ExitPortalAnchorSource}'");
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
            return GenerateSnapshot(zoneName, seed).Spawns;
        }

        public static ProceduralDungeonSnapshot GenerateSnapshot(string zoneName, uint seed = 0xBEEFBEEF, uint roomSeed = 0, string instanceKey = null)
        {
            string baseZone = NormalizeBaseZone(zoneName);
            var snapshot = new ProceduralDungeonSnapshot
            {
                ZoneName = baseZone,
                LayoutSeed = seed,
                RoomSeed = roomSeed
            };
            var spawns = snapshot.Spawns;

            if (!LevelDefs.TryGetValue(baseZone, out LevelDef level))
            {
                Debug.LogError($"[MazeSpawner] No level definition for zone '{zoneName}'");
                return snapshot;
            }

            Debug.LogError($"[MazeSpawner] ═══════════════════════════════════════════════════");
            Debug.LogError($"[MazeSpawner] GENERATING MAZE for {baseZone}");
            Debug.LogError($"[MazeSpawner]   Size: {level.MazeWidth}x{level.MazeHeight}  Seed: 0x{seed:X8}");

            // 1. Generate the maze
            Debug.LogError($"[MazeSpawner]   RNG source: local dungeon generator seed=0x{seed:X8} roomReady={CombatManager.Instance.IsRoomRngReady} roomSeed=0x{CombatManager.Instance.RoomSeed:X8}");

            var maze = new MazeGenerator(
                level.MazeWidth, level.MazeHeight, seed,
                level.MazeRandomness, level.MazeSparseness,
                level.MazeDeadEndRemovalChance,
                null
            );
            Debug.LogError($"[MazeSpawner]   Generated world root: native integer half-grid, tileSize={MazeGenerator.TILE_SIZE}, no PathMap bounds center override");
            if (level.RoomNodes != null)
            {
                for (int nodeIndex = 0; nodeIndex < level.RoomNodes.Length; nodeIndex++)
                {
                    var node = level.RoomNodes[nodeIndex];
                    maze.AddRoomNode(node.TileSet, node.GridX, node.GridY, node.Chance, nodeIndex);
                }
            }
            var cells = maze.Generate();

            Debug.LogError($"[MazeSpawner]   Generated {cells.Count} cells");

            // Phase 2 (Option 1-full): dump the maze grid to disk for diffing against
            // x32dbg-captured client grids. One file per (zone, seed) pair. Keeps the
            // last 8 dumps per zone so re-runs don't drown the disk; older files removed.
            // Path: <persistent>/maze_dumps/<zone>_<seed>.txt
            try
            {
                string dumpDir = System.IO.Path.Combine(UnityEngine.Application.persistentDataPath, "maze_dumps");
                System.IO.Directory.CreateDirectory(dumpDir);
                string dumpPath = System.IO.Path.Combine(dumpDir, $"{baseZone}_0x{seed:X8}.txt");
                string body = maze.DumpGrid();
                string ascii = maze.AsciiGridPicture();
                System.IO.File.WriteAllText(dumpPath, body + "\n" + ascii);
                Debug.LogError($"[MAZE-DUMP] wrote {dumpPath} ({body.Length + ascii.Length} bytes) zone={baseZone} seed=0x{seed:X8}");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[MAZE-DUMP] failed to write dump for zone={baseZone} seed=0x{seed:X8}: {ex.Message}");
            }

            // Phase 3 (Option 1-full): build a per-instance PathMap from the placed tile
            // geometry and register it so WanderSimulator etc. stop hitting the "procedural
            // instance has no PathMap" warning. Build cost is ~1-15s per zone-in depending
            // on dungeon size; paid once. Registration key is the full instance name (e.g.
            // dungeon00_level01_inst2147483649) when provided; falls back to zoneName for
            // legacy callers. Since LayoutSeed is now per-instance, each instance gets its
            // own PathMap.
            try
            {
                string pathMapKey = string.IsNullOrEmpty(instanceKey) ? zoneName : instanceKey;
                var builtPathMap = DungeonRunners.Utilities.PathMapBuilder.Build(pathMapKey, cells);
                if (builtPathMap != null)
                {
                    DungeonRunners.Core.PathMapManager.Instance.RegisterInstancePathMap(pathMapKey, builtPathMap);
                    // Phase 4b: re-fire the parity test now that we have a real dungeon PathMap.
                    // The boot-time run hits zero matching PathMaps; the post-zone-in run can
                    // actually validate captured cases.
                    DungeonRunners.Core.PathfinderClientParityTest.RunAll();

                    // A1.2 of plan vivid-marinating-pixel (2026-05-28): optional sweep of
                    // PathMapBuilder.WallHeightThreshold against captured client traces. Off
                    // by default — flip PathMapThresholdSweep.Enabled=true to fire once on
                    // the next zone enter, read the [PATHMAP-SWEEP] summary line, then flip
                    // back off. Doesn't replace the production PathMap (built above) — only
                    // builds scratch maps for scoring.
                    if (DungeonRunners.Utilities.PathMapThresholdSweep.Enabled)
                        DungeonRunners.Utilities.PathMapThresholdSweep.Run(pathMapKey, cells);
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[PATHMAP-BUILD] failed for zone={zoneName} seed=0x{seed:X8}: {ex.Message}");
            }

            // 2. Place encounters at tile encounter markers
            int encIdx = 0;
            int totalRegular = 0;

            // Process cells top-to-bottom, left-to-right (match client iteration order)
            cells.Sort((a, b) =>
            {
                int cmp = b.GridY.CompareTo(a.GridY);
                return cmp != 0 ? cmp : a.GridX.CompareTo(b.GridX);
            });

            var cellsByKey = new Dictionary<string, MazeGenerator.MazeCell>();
            foreach (var cell in cells)
                cellsByKey[CellKey(cell)] = cell;

            var roomCellKeys = new HashSet<string>();
            var encounterRooms = new List<(MazeGenerator.MazeCell Cell, RoomNodeDef Node, MazeGenerator.PlacedRoomNode Placed)>();
            foreach (var placed in maze.PlacedRoomNodes)
            {
                string key = $"{placed.GridX}:{placed.GridY}";
                roomCellKeys.Add(key);
                if (cellsByKey.TryGetValue(key, out var cell))
                {
                    RoomNodeDef node = null;
                    if (level.RoomNodes != null && placed.SourceIndex >= 0 && placed.SourceIndex < level.RoomNodes.Length)
                        node = level.RoomNodes[placed.SourceIndex];

                    Debug.LogError($"[MazeSpawner]   RoomNode src={placed.SourceIndex} tileSet='{placed.TileSet}' tile='{placed.TileType}' grid=({placed.GridX},{placed.GridY}) encounter={(node?.EncounterTable != null)}");
                    if (node?.EncounterTable != null)
                        encounterRooms.Add((cell, node, placed));
                }
                else
                {
                    Debug.LogError($"[MazeSpawner]   RoomNode src={placed.SourceIndex} grid=({placed.GridX},{placed.GridY}) has no generated cell");
                }
            }

            var fallbackLeaderCells = encounterRooms.Count == 0 ? SelectLeaderCells(cells, level) : new List<MazeGenerator.MazeCell>();
            foreach (var leaderCell in fallbackLeaderCells)
                roomCellKeys.Add(CellKey(leaderCell));

            foreach (var cell in cells)
            {
                if (roomCellKeys.Contains(CellKey(cell)))
                    continue;

                if (!TileEncounterPlaceholders.TryGetValue(cell.TileType, out var markers) || markers.Count == 0)
                    continue;

                for (int markerIndex = 0; markerIndex < markers.Count; markerIndex++)
                {
                    var marker = markers[markerIndex];
                    int groupOrdinal = encIdx;
                    string groupKey = $"{baseZone}:enc:{groupOrdinal}";
                    int encounterChoiceIndex = maze.NextInt(0, level.EncounterTable.Length);
                    var group = level.EncounterTable[encounterChoiceIndex];
                    int groupSlots = CountSpawnSlots(group);
                    int groupSlot = 0;
                    encIdx++;

                    foreach (var unit in group)
                    {
                        for (int i = 0; i < unit.Count; i++)
                        {
                            int slot = groupSlot++;
                            Vector3 spawnPoint = ResolveEncounterSpawnPoint(cell, marker, slot, groupSlots, out bool groundApplied, out string groundSource);

                            spawns.Add(new DatabaseLoader.DungeonSpawnData
                            {
                                zoneName = baseZone,
                                gcType = unit.GcType,
                                spawnGcTypeOverride = unit.SpawnGcTypeOverride,
                                posX = spawnPoint.x,
                                posY = spawnPoint.y,
                                posZ = spawnPoint.z,
                                heading = NormalizeHeading(marker.Heading),
                                encounterGroupKey = groupKey,
                                encounterDifficulty = unit.Difficulty,
                                gridX = cell.GridX,
                                gridY = cell.GridY,
                                tileType = cell.TileType,
                                worldOriginX = cell.WorldOriginX,
                                worldOriginY = cell.WorldOriginY,
                                localX = marker.LocalX,
                                localY = marker.LocalY,
                                localZ = marker.LocalZ,
                                placementRole = "regular",
                                placeholderSource = $"{marker.Source};ground={groundSource}",
                                placeholderIndex = markerIndex,
                                placeholderSizeX = marker.SizeX,
                                placeholderSizeY = marker.SizeY,
                                encounterChoiceIndex = encounterChoiceIndex,
                                snapApplied = groundApplied
                            });
                            totalRegular++;
                        }
                    }
                }
            }

            Debug.LogError($"[MazeSpawner]   Placed {totalRegular} regular mobs from {encIdx} markers");

            // 3. Place authored EncounterType rooms at their actual generated RoomNode cells.
            int leaderIdx = 0;
            int totalLeaders = 0;

            var leaderRooms = new List<(MazeGenerator.MazeCell Cell, SpawnUnit[][] Table, string Source)>();
            foreach (var encounterRoom in encounterRooms)
                leaderRooms.Add((encounterRoom.Cell, encounterRoom.Node.EncounterTable, $"roomNode:{encounterRoom.Placed.SourceIndex}"));
            if (leaderRooms.Count == 0 && level.LeaderEncounterTable != null && level.LeaderGridYs != null)
            {
                foreach (var fallbackCell in fallbackLeaderCells)
                    leaderRooms.Add((fallbackCell, level.LeaderEncounterTable, "fallback-gridY"));
            }

            if (leaderRooms.Count > 0)
            {
                foreach (var leaderRoom in leaderRooms)
                {
                    var bestCell = leaderRoom.Cell;
                    int leaderGroupOrdinal = leaderIdx;
                    string leaderGroupKey = $"{baseZone}:leader:{leaderGroupOrdinal}";
                    int leaderChoiceIndex = maze.NextInt(0, leaderRoom.Table.Length);
                    var leaderGroup = leaderRoom.Table[leaderChoiceIndex];
                    leaderIdx++;
                    Debug.LogError($"[MazeSpawner]   Leader encounter source={leaderRoom.Source} group={leaderGroupKey} cell=({bestCell.GridX},{bestCell.GridY}) tile='{bestCell.TileType}'");
                    if (!TileEncounterPlaceholders.TryGetValue(bestCell.TileType, out var leaderMarkers) || leaderMarkers.Count == 0)
                        leaderMarkers = new List<EncounterMarker> { new(200f, 200f, 0f, source: "PARTIAL fallback tile center; PKG placeholder missing") };

                    int leaderSlots = CountSpawnSlots(leaderGroup);
                    int leaderMobs = 0;
                    foreach (var unit in leaderGroup)
                    {
                        for (int i = 0; i < unit.Count; i++)
                        {
                            int slot = leaderMobs++;
                            int markerIndex = Math.Min(slot, leaderMarkers.Count - 1);
                            EncounterMarker marker = leaderMarkers[markerIndex];

                            Vector3 spawnPoint = ResolveEncounterSpawnPoint(bestCell, marker, slot, leaderSlots, out bool groundApplied, out string groundSource);
                            spawns.Add(new DatabaseLoader.DungeonSpawnData
                            {
                                zoneName = baseZone,
                                gcType = unit.GcType,
                                spawnGcTypeOverride = unit.SpawnGcTypeOverride,
                                posX = spawnPoint.x,
                                posY = spawnPoint.y,
                                posZ = spawnPoint.z,
                                heading = NormalizeHeading(marker.Heading),
                                encounterGroupKey = leaderGroupKey,
                                encounterDifficulty = unit.Difficulty,
                                gridX = bestCell.GridX,
                                gridY = bestCell.GridY,
                                tileType = bestCell.TileType,
                                worldOriginX = bestCell.WorldOriginX,
                                worldOriginY = bestCell.WorldOriginY,
                                localX = marker.LocalX,
                                localY = marker.LocalY,
                                localZ = marker.LocalZ,
                                placementRole = "leader",
                                placeholderSource = $"{marker.Source};ground={groundSource}",
                                placeholderIndex = markerIndex,
                                placeholderSizeX = marker.SizeX,
                                placeholderSizeY = marker.SizeY,
                                encounterChoiceIndex = leaderChoiceIndex,
                                snapApplied = groundApplied
                            });
                            totalLeaders++;
                        }
                    }
                }
            }

            snapshot.Cells = new List<MazeGenerator.MazeCell>(cells);
            snapshot.RoomNodes = new List<MazeGenerator.PlacedRoomNode>(maze.PlacedRoomNodes);
            ResolveSnapshotAnchors(snapshot, level, baseZone, cells);
            Debug.LogError($"[MazeSpawner] ✅ TOTAL: {spawns.Count} mobs ({totalRegular} regular + {totalLeaders} leaders) for {baseZone}");
            Debug.LogError($"[DUNGEON-SNAPSHOT] zone={baseZone} layoutSeed=0x{snapshot.LayoutSeed:X8} roomSeed=0x{snapshot.RoomSeed:X8} cells={snapshot.Cells.Count} roomNodes={snapshot.RoomNodes.Count} spawns={snapshot.Spawns.Count} entry=({snapshot.EntryGridX},{snapshot.EntryGridY}) entryTile='{snapshot.EntryTileType}' player=({snapshot.PlayerSpawn.x:F1},{snapshot.PlayerSpawn.y:F1},{snapshot.PlayerSpawn.z:F1}) entryPortal=({snapshot.EntryPortalSpawn.x:F1},{snapshot.EntryPortalSpawn.y:F1},{snapshot.EntryPortalSpawn.z:F1}) exit=({snapshot.ExitGridX},{snapshot.ExitGridY}) exitTile='{snapshot.ExitTileType}' exitPortal=({snapshot.ExitPortalSpawn.x:F1},{snapshot.ExitPortalSpawn.y:F1},{snapshot.ExitPortalSpawn.z:F1}) yTransform=worldGridY=gridY/native-BuildWorld");
            return snapshot;
        }
    }
}
