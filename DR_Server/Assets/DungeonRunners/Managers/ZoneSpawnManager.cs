using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using DungeonRunners.Combat;

namespace DungeonRunners.Managers
{
    public class ZoneSpawnManager
    {
        private static ZoneSpawnManager _instance;
        public static ZoneSpawnManager Instance => _instance ??= new ZoneSpawnManager();

        private HashSet<string> _spawnedZones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private const uint MAZE_SEED = 0xBEEFBEEF;
        private const int MAX_SPAWNS_FOR_TESTING = 3;  // TEMP: reduce for cleaner logs

        // TEMP: Override spawn positions — place 3 mobs ~100 units from dungeon00_level01 portal (626, -463)
        private List<DatabaseLoader.DungeonSpawnData> GetTestSpawns(string zoneName)
        {
            var spawns = new List<DatabaseLoader.DungeonSpawnData>();
            // Use the first creature type from the zone's normal spawn data
            var normalSpawns = GetNormalSpawnData(zoneName);
            string gcType = normalSpawns != null && normalSpawns.Count > 0 ? normalSpawns[0].gcType : "world.creature.warg_grunt";

            float baseX = 640f;  // just 14 units from player spawn (626, -463)
            float baseY = -463f;
            float baseZ = 10f;

            for (int i = 0; i < MAX_SPAWNS_FOR_TESTING; i++)
            {
                spawns.Add(new DatabaseLoader.DungeonSpawnData
                {
                    gcType = gcType,
                    posX = baseX + (i * 10f),  // space 10 apart: 640, 650, 660
                    posY = baseY,
                    posZ = baseZ,
                    heading = 180f
                });
            }
            Debug.LogError($"[ZoneSpawnManager] TEST MODE: {spawns.Count} mobs of type '{gcType}' near ({baseX}, {baseY})");
            return spawns;
        }

        public List<Monster> SpawnZoneMobs(string zoneName)
        {
            var spawned = new List<Monster>();

            if (string.IsNullOrEmpty(zoneName))
                return spawned;

            if (_spawnedZones.Contains(zoneName))
            {
                Debug.LogError($"[ZoneSpawnManager] Zone '{zoneName}' already spawned, skipping");
                return spawned;
            }

            List<DatabaseLoader.DungeonSpawnData> spawnDefs = GetSpawnData(zoneName);

            if (spawnDefs == null || spawnDefs.Count == 0)
            {
                Debug.LogError($"[ZoneSpawnManager] No spawn data for zone '{zoneName}'");
                return spawned;
            }

            Debug.LogError($"[ZoneSpawnManager] ═══════════════════════════════════════════════════");
            Debug.LogError($"[ZoneSpawnManager] SPAWNING {spawnDefs.Count} MOBS FOR ZONE: {zoneName}");
            Debug.LogError($"[ZoneSpawnManager] ═══════════════════════════════════════════════════");

            // PathMap for terrain height correction
            var pathMap = DungeonRunners.Core.PathMapManager.Instance.GetPathMap(zoneName);

            foreach (var def in spawnDefs)
            {
                // Correct Z to terrain height if PathMap available
                float correctedZ = def.posZ;
                if (pathMap != null && pathMap.IsWalkable(def.posX, def.posY))
                {
                    correctedZ = pathMap.GetHeightAt(def.posX, def.posY, def.posZ);
                }

                var monster = CombatManager.Instance.SpawnMonster(
                    def.gcType,
                    def.posX,
                    def.posY,
                    correctedZ,
                    def.heading,
                    zoneName,
                    def.encounterGroupKey,
                    def.encounterDifficulty >= 0f ? def.encounterDifficulty : 1f
                );

                if (monster != null)
                {
                    spawned.Add(monster);
                    if (correctedZ != def.posZ)
                        Debug.LogError($"[ZoneSpawnManager] ✅ Spawned {monster.Name} ({def.gcType}) at ({def.posX}, {def.posY}, {correctedZ}) [Z corrected from {def.posZ}]");
                    else
                        Debug.LogError($"[ZoneSpawnManager] ✅ Spawned {monster.Name} ({def.gcType}) at ({def.posX}, {def.posY}, {def.posZ})");
                }
                else
                {
                    Debug.LogError($"[ZoneSpawnManager] ❌ FAILED to spawn '{def.gcType}' - not found in creature database!");
                }
            }

            // Barrel spawning DISABLED — world.objects.barrel.breakable GC types crash client
            var worldObjects = WorldObjectSpawner.GenerateBarrels(zoneName, MAZE_SEED);
            // foreach (var obj in worldObjects)
            //     CombatManager.Instance.SpawnMonster(obj.gcType, obj.posX, obj.posY, obj.posZ, obj.heading, zoneName);

            _spawnedZones.Add(zoneName);
            Debug.LogError($"[ZoneSpawnManager] ✅ Zone '{zoneName}' complete: {spawned.Count}/{spawnDefs.Count} mobs");
            return spawned;
        }

        private List<DatabaseLoader.DungeonSpawnData> GetSpawnData(string zoneName, uint? seed = null)
        {
            // return GetTestSpawns(zoneName);
            return GetNormalSpawnData(zoneName, seed);
        }

        private List<DatabaseLoader.DungeonSpawnData> GetNormalSpawnData(string zoneName, uint? seed = null)
        {
            // Procedural dungeon levels - generate from maze at runtime
            if (DungeonMazeSpawner.IsProceduralZone(zoneName))
            {
                uint spawnSeed = seed ?? MAZE_SEED;
                Debug.LogError($"[ZoneSpawnManager] Zone '{zoneName}' is PROCEDURAL - generating from maze seed 0x{spawnSeed:X8}");
                return DungeonMazeSpawner.GenerateSpawns(zoneName, spawnSeed);
            }

            // Static zones (boss room) - load from database JSON
            List<DatabaseLoader.DungeonSpawnData> staticSpawns;
            if (DatabaseLoader.DungeonSpawns.TryGetValue(zoneName, out staticSpawns) && staticSpawns.Count > 0)
            {
                Debug.LogError($"[ZoneSpawnManager] Zone '{zoneName}' using STATIC data ({staticSpawns.Count} spawns)");
                return staticSpawns;
            }

            return null;
        }

        /// <summary>
        /// Spawn mobs for a specific instance. Uses real zoneName for spawn data,
        /// but tags monsters with instanceKey so each group gets their own mobs.
        /// Binary: DungeonGenerator::generate(Random) — same seed = same dungeon per group.
        /// Binary: ZoneClient::GotoInstance(int) — each group has own instance.
        /// </summary>
        public List<Monster> SpawnZoneMobsForInstance(string zoneName, string instanceKey, uint? seed = null)
        {
            var spawned = new List<Monster>();

            if (string.IsNullOrEmpty(zoneName))
                return spawned;

            if (_spawnedZones.Contains(instanceKey))
            {
                Debug.LogError($"[ZoneSpawnManager] Instance '{instanceKey}' already spawned, skipping");
                return spawned;
            }

            List<DatabaseLoader.DungeonSpawnData> spawnDefs = GetSpawnData(zoneName, seed);

            if (spawnDefs == null || spawnDefs.Count == 0)
            {
                Debug.LogError($"[ZoneSpawnManager] No spawn data for zone '{zoneName}'");
                return spawned;
            }

            Debug.LogError($"[ZoneSpawnManager] SPAWNING {spawnDefs.Count} MOBS FOR INSTANCE: {instanceKey} (zone: {zoneName})");

            // PathMap for terrain height correction
            var pathMap = DungeonRunners.Core.PathMapManager.Instance.GetPathMap(zoneName);

            foreach (var def in spawnDefs)
            {
                // Correct Z to terrain height if PathMap available
                float correctedZ = def.posZ;
                if (pathMap != null && pathMap.IsWalkable(def.posX, def.posY))
                {
                    correctedZ = pathMap.GetHeightAt(def.posX, def.posY, def.posZ);
                }

                var monster = CombatManager.Instance.SpawnMonster(
                    def.gcType,
                    def.posX,
                    def.posY,
                    correctedZ,
                    def.heading,
                    instanceKey,
                    def.encounterGroupKey,
                    def.encounterDifficulty >= 0f ? def.encounterDifficulty : 1f
                );

                if (monster != null)
                {
                    spawned.Add(monster);
                }
            }

            // Barrel spawning DISABLED — world.objects.barrel.breakable GC types crash client
            var worldObjects = WorldObjectSpawner.GenerateBarrels(zoneName, seed ?? MAZE_SEED);
            // foreach (var obj in worldObjects)
            //     CombatManager.Instance.SpawnMonster(obj.gcType, obj.posX, obj.posY, obj.posZ, obj.heading, instanceKey);

            _spawnedZones.Add(instanceKey);
            Debug.LogError($"[ZoneSpawnManager] Instance '{instanceKey}' complete: {spawned.Count}/{spawnDefs.Count} mobs");
            return spawned;
        }

        public bool HasSpawnsForZone(string zoneName)
        {
            return DungeonMazeSpawner.IsProceduralZone(zoneName) ||
                   DatabaseLoader.DungeonSpawns.ContainsKey(zoneName);
        }

        public bool IsZoneSpawned(string zoneName)
        {
            return _spawnedZones.Contains(zoneName);
        }

        public void ResetZone(string zoneName)
        {
            _spawnedZones.Remove(zoneName);
        }

        /// <summary>
        /// Resets the base zone AND all instanced variants (e.g. dungeon00_level01_inst2147483649).
        /// </summary>
        public void ResetZoneAndInstances(string baseZoneName)
        {
            var toRemove = _spawnedZones
                .Where(z => z.Equals(baseZoneName, StringComparison.OrdinalIgnoreCase) ||
                            z.StartsWith(baseZoneName + "_inst", StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var z in toRemove)
                _spawnedZones.Remove(z);
            Debug.LogError($"[ZoneSpawnManager] ResetZoneAndInstances('{baseZoneName}'): cleared {toRemove.Count} entries");
        }

        public void ResetAll()
        {
            _spawnedZones.Clear();
        }
    }
}
