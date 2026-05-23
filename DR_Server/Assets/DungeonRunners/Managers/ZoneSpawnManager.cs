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
        private Dictionary<string, DungeonMazeSpawner.ProceduralDungeonSnapshot> _proceduralSnapshots
            = new Dictionary<string, DungeonMazeSpawner.ProceduralDungeonSnapshot>(StringComparer.OrdinalIgnoreCase);

        private const uint MAZE_SEED = 0xBEEFBEEF;

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
            bool proceduralSpawns = DungeonMazeSpawner.IsProceduralZone(zoneName);

            foreach (var def in spawnDefs)
            {
                // Correct Z to terrain height if PathMap available
                float correctedZ = def.posZ;
                if (!proceduralSpawns && pathMap != null && pathMap.IsWalkable(def.posX, def.posY))
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
                    def.encounterDifficulty >= 0f ? def.encounterDifficulty : 1f,
                    def.spawnGcTypeOverride
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
            // foreach (var obj in worldObjects)
            //     CombatManager.Instance.SpawnMonster(obj.gcType, obj.posX, obj.posY, obj.posZ, obj.heading, zoneName);

            _spawnedZones.Add(zoneName);
            Debug.LogError($"[ZoneSpawnManager] ✅ Zone '{zoneName}' complete: {spawned.Count}/{spawnDefs.Count} mobs");
            return spawned;
        }

        private List<DatabaseLoader.DungeonSpawnData> GetSpawnData(string zoneName, uint? seed = null)
        {
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

            if (DungeonMazeSpawner.IsStaticBossZone(zoneName))
            {
                uint spawnSeed = seed ?? MAZE_SEED;
                Debug.LogError($"[ZoneSpawnManager] Zone '{zoneName}' is STATIC BOSS - generating authored encounter spawns seed 0x{spawnSeed:X8}");
                return DungeonMazeSpawner.GenerateStaticBossSpawns(zoneName, spawnSeed);
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

        public DungeonMazeSpawner.ProceduralDungeonSnapshot GetOrCreateProceduralSnapshot(
            string zoneName, string instanceKey, uint layoutSeed, uint roomSeed = 0)
        {
            if (string.IsNullOrEmpty(zoneName) || !DungeonMazeSpawner.IsProceduralZone(zoneName))
                return null;

            string key = string.IsNullOrEmpty(instanceKey) ? zoneName : instanceKey;
            uint nativeLayoutSeed = layoutSeed != 0 ? layoutSeed : roomSeed;
            if (_proceduralSnapshots.TryGetValue(key, out var snapshot))
            {
                bool layoutMismatch = snapshot.LayoutSeed != nativeLayoutSeed;
                bool roomMismatch = roomSeed != 0 && snapshot.RoomSeed != 0 && snapshot.RoomSeed != roomSeed;
                if ((layoutMismatch || roomMismatch) && !_spawnedZones.Contains(key))
                {
                    Debug.LogError($"[DUNGEON-SNAPSHOT] rebuild instance='{key}' zone={zoneName} oldLayout=0x{snapshot.LayoutSeed:X8} newLayout=0x{nativeLayoutSeed:X8} requestedLayout=0x{layoutSeed:X8} oldRoom=0x{snapshot.RoomSeed:X8} newRoom=0x{roomSeed:X8}");
                    snapshot = DungeonMazeSpawner.GenerateSnapshot(zoneName, nativeLayoutSeed, roomSeed);
                    _proceduralSnapshots[key] = snapshot;
                }
                else
                {
                    if (snapshot.RoomSeed == 0 && roomSeed != 0)
                    {
                        snapshot.RoomSeed = roomSeed;
                        Debug.LogError($"[DUNGEON-SNAPSHOT] resolved room seed instance='{key}' zone={zoneName} roomSeed=0x{roomSeed:X8}");
                    }
                    if (layoutMismatch || roomMismatch)
                        Debug.LogError($"[DUNGEON-SNAPSHOT] keep spawned snapshot instance='{key}' zone={zoneName} layoutSeed=0x{snapshot.LayoutSeed:X8} requestedLayout=0x{layoutSeed:X8} nativeLayout=0x{nativeLayoutSeed:X8} roomSeed=0x{snapshot.RoomSeed:X8} requestedRoom=0x{roomSeed:X8}");
                }
                Debug.LogError($"[DUNGEON-SNAPSHOT] reuse instance='{key}' zone={zoneName} layoutSeed=0x{snapshot.LayoutSeed:X8} requestedLayout=0x{layoutSeed:X8} roomSeed=0x{snapshot.RoomSeed:X8} entry=({snapshot.EntryGridX},{snapshot.EntryGridY}) tile='{snapshot.EntryTileType}' player=({snapshot.PlayerSpawn.x:F1},{snapshot.PlayerSpawn.y:F1},{snapshot.PlayerSpawn.z:F1}) entryPortal=({snapshot.EntryPortalSpawn.x:F1},{snapshot.EntryPortalSpawn.y:F1},{snapshot.EntryPortalSpawn.z:F1}) exit=({snapshot.ExitGridX},{snapshot.ExitGridY}) tile='{snapshot.ExitTileType}' exitPortal=({snapshot.ExitPortalSpawn.x:F1},{snapshot.ExitPortalSpawn.y:F1},{snapshot.ExitPortalSpawn.z:F1}) spawns={snapshot.Spawns.Count} yTransform=worldGridY=gridY/native-BuildWorld");
                return snapshot;
            }

            snapshot = DungeonMazeSpawner.GenerateSnapshot(zoneName, nativeLayoutSeed, roomSeed);
            _proceduralSnapshots[key] = snapshot;
            Debug.LogError($"[DUNGEON-SNAPSHOT] cache instance='{key}' zone={zoneName} layoutSeed=0x{nativeLayoutSeed:X8} requestedLayout=0x{layoutSeed:X8} roomSeed=0x{roomSeed:X8} entry=({snapshot.EntryGridX},{snapshot.EntryGridY}) tile='{snapshot.EntryTileType}' player=({snapshot.PlayerSpawn.x:F1},{snapshot.PlayerSpawn.y:F1},{snapshot.PlayerSpawn.z:F1}) entryPortal=({snapshot.EntryPortalSpawn.x:F1},{snapshot.EntryPortalSpawn.y:F1},{snapshot.EntryPortalSpawn.z:F1}) exit=({snapshot.ExitGridX},{snapshot.ExitGridY}) tile='{snapshot.ExitTileType}' exitPortal=({snapshot.ExitPortalSpawn.x:F1},{snapshot.ExitPortalSpawn.y:F1},{snapshot.ExitPortalSpawn.z:F1}) cells={snapshot.Cells.Count} roomNodes={snapshot.RoomNodes.Count} spawns={snapshot.Spawns.Count} yTransform=worldGridY=gridY/native-BuildWorld");
            return snapshot;
        }

        public bool TryGetProceduralSnapshot(string instanceKey, out DungeonMazeSpawner.ProceduralDungeonSnapshot snapshot)
        {
            snapshot = null;
            if (string.IsNullOrEmpty(instanceKey))
                return false;
            return _proceduralSnapshots.TryGetValue(instanceKey, out snapshot);
        }

        /// <summary>
        /// Spawn mobs for a specific instance. Uses real zoneName for spawn data,
        /// but tags monsters with instanceKey so each group gets their own mobs.
        /// Binary: DungeonGenerator::generate(Random) — same seed = same dungeon per group.
        /// Binary: ZoneClient::GotoInstance(int) — each group has own instance.
        /// </summary>
        public List<Monster> SpawnZoneMobsForInstance(string zoneName, string instanceKey, uint? seed = null, uint roomSeed = 0)
        {
            var spawned = new List<Monster>();

            if (string.IsNullOrEmpty(zoneName))
                return spawned;

            if (_spawnedZones.Contains(instanceKey))
            {
                Debug.LogError($"[ZoneSpawnManager] Instance '{instanceKey}' already spawned, skipping");
                return spawned;
            }

            List<DatabaseLoader.DungeonSpawnData> spawnDefs;
            bool proceduralSpawns = DungeonMazeSpawner.IsProceduralZone(zoneName);
            if (proceduralSpawns)
            {
                uint spawnSeed = seed ?? MAZE_SEED;
                var snapshot = GetOrCreateProceduralSnapshot(zoneName, instanceKey, spawnSeed, roomSeed);
                spawnDefs = snapshot?.Spawns;
            }
            else
            {
                spawnDefs = GetSpawnData(zoneName, seed);
            }

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
                if (!proceduralSpawns && pathMap != null && pathMap.IsWalkable(def.posX, def.posY))
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
                    def.encounterDifficulty >= 0f ? def.encounterDifficulty : 1f,
                    def.spawnGcTypeOverride
                );

                if (monster != null)
                {
                    spawned.Add(monster);
                    Debug.LogError($"[DUNGEON-SPAWN] instance='{instanceKey}' zone={zoneName} role={def.placementRole ?? ""} group={def.encounterGroupKey ?? ""} grid=({def.gridX},{def.gridY}) tile='{def.tileType ?? ""}' origin=({def.worldOriginX:F1},{def.worldOriginY:F1}) local=({def.localX:F1},{def.localY:F1},{def.localZ:F1}) placeholder='{def.placeholderSource ?? ""}' marker={def.placeholderIndex} size=({def.placeholderSizeX:F1},{def.placeholderSizeY:F1}) choice={def.encounterChoiceIndex} snapApplied={def.snapApplied} gc='{def.gcType}' spawnGc='{def.spawnGcTypeOverride ?? ""}' pos=({def.posX:F1},{def.posY:F1},{correctedZ:F1}) heading={def.heading:F1} difficulty={def.encounterDifficulty:F2} zSource={(proceduralSpawns ? "snapshot" : "pathmap")} monster={monster.Name} level={monster.Level} tier={monster.Tier ?? ""} maxHP={monster.MaxHPWire}");
                }
            }

            // Barrel spawning DISABLED — world.objects.barrel.breakable GC types crash client
            // foreach (var obj in worldObjects)
            //     CombatManager.Instance.SpawnMonster(obj.gcType, obj.posX, obj.posY, obj.posZ, obj.heading, instanceKey);

            _spawnedZones.Add(instanceKey);
            Debug.LogError($"[ZoneSpawnManager] Instance '{instanceKey}' complete: {spawned.Count}/{spawnDefs.Count} mobs");
            return spawned;
        }

        public bool HasSpawnsForZone(string zoneName)
        {
            return DungeonMazeSpawner.IsProceduralZone(zoneName) ||
                   DungeonMazeSpawner.IsStaticBossZone(zoneName) ||
                   DatabaseLoader.DungeonSpawns.ContainsKey(zoneName);
        }

        public bool IsZoneSpawned(string zoneName)
        {
            return _spawnedZones.Contains(zoneName);
        }

        public void ResetZone(string zoneName)
        {
            _spawnedZones.Remove(zoneName);
            _proceduralSnapshots.Remove(zoneName);
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
            {
                _spawnedZones.Remove(z);
                _proceduralSnapshots.Remove(z);
            }
            var snapshotKeys = _proceduralSnapshots.Keys
                .Where(z => z.Equals(baseZoneName, StringComparison.OrdinalIgnoreCase) ||
                            z.StartsWith(baseZoneName + "_inst", StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var z in snapshotKeys)
                _proceduralSnapshots.Remove(z);
            _proceduralSnapshots.Remove(baseZoneName);
            Debug.LogError($"[ZoneSpawnManager] ResetZoneAndInstances('{baseZoneName}'): cleared {toRemove.Count} entries");
        }

        public void ResetAll()
        {
            _spawnedZones.Clear();
            _proceduralSnapshots.Clear();
        }
    }
}
