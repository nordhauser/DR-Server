using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using DB = DungeonRunners.Database.GameDatabase;
using DungeonRunners.Utilities;

namespace DungeonRunners.Managers
{
    // ═══════════════════════════════════════════════════════════════════════════
    // WORLD ENTITY SPAWNER — Spawns static world entities from zone_world_entities DB
    //
    // Handles: Chests, Shrines, Gates, Teleporters, NPC Portraits, and any
    // other NonCombatInteractive entities defined in .world files.
    //
    // All entities use the same spawn format as portals/checkpoints:
    //   0x01 (Create) + entityId + GCType
    //   0x02 (Init)   + entityId + flags + pos + heading + initFlags
    //
    // Activation is routed by entity_type:
    //   chest      → open, drop loot, despawn
    //   teleporter → zone player to target
    //   shrine     → apply buff (TODO)
    //   gate       → unlock check (TODO)
    //   npc        → quest dialog (TODO)
    // ═══════════════════════════════════════════════════════════════════════════

    public class WorldEntitySpawner
    {
        private static WorldEntitySpawner _instance;
        public static WorldEntitySpawner Instance => _instance ??= new WorldEntitySpawner();

        // DB data keyed by zone name (lowercase)
        private Dictionary<string, List<WorldEntityData>> _entitiesByZone
            = new Dictionary<string, List<WorldEntityData>>(StringComparer.OrdinalIgnoreCase);

        // Live spawned entities keyed by entityId → data (for activation lookup)
        private Dictionary<ushort, WorldEntityData> _spawnedEntities
            = new Dictionary<ushort, WorldEntityData>();

        // ═══════════════════════════════════════════════════════════════
        // INITIALIZATION — Load from DB
        // ═══════════════════════════════════════════════════════════════

        public void Initialize()
        {
            _entitiesByZone.Clear();
            int count = 0;

            using (var c = DB.GetConnection())
            {
                // Check if table exists
                try
                {
                    using (var r = DB.ExecuteReader(c, "SELECT * FROM zone_world_entities"))
                    {
                        while (r.Read())
                        {
                            var e = new WorldEntityData
                            {
                                Id = DB.GetInt(r, "id"),
                                Zone = DB.GetString(r, "zone"),
                                Name = DB.GetString(r, "name"),
                                GCType = DB.GetString(r, "gc_type"),
                                EntityType = DB.GetString(r, "entity_type"),
                                PosX = DB.GetFloat(r, "pos_x"),
                                PosY = DB.GetFloat(r, "pos_y"),
                                PosZ = DB.GetFloat(r, "pos_z"),
                                Heading = DB.GetFloat(r, "heading"),
                                Flags = (uint)DB.GetInt(r, "flags"),
                                ItemGenerator = DB.GetString(r, "item_generator"),
                                ItemCount = DB.GetInt(r, "item_count"),
                                ItemGenerator2 = DB.GetString(r, "item_generator2"),
                                ItemCount2 = DB.GetInt(r, "item_count2"),
                                TargetZone = DB.GetString(r, "target_zone"),
                                TargetSpawn = DB.GetString(r, "target_spawn"),
                                Label = DB.GetString(r, "label"),
                                AllowMultiple = DB.GetInt(r, "allow_multiple") != 0
                            };

                            string key = e.Zone.ToLower();
                            if (!_entitiesByZone.ContainsKey(key))
                                _entitiesByZone[key] = new List<WorldEntityData>();
                            _entitiesByZone[key].Add(e);
                            count++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[WorldEntitySpawner] zone_world_entities table not found or error: {ex.Message}");
                    Debug.LogError($"[WorldEntitySpawner] Run zone_world_entities.sql to create the table.");
                    return;
                }
            }

            Debug.LogError($"[WorldEntitySpawner] Loaded {count} world entities across {_entitiesByZone.Count} zones");
        }

        // ═══════════════════════════════════════════════════════════════
        // SPAWN — Generate entity packets for a zone
        // Returns list of (entityId, data) for tracking by UGS
        // ═══════════════════════════════════════════════════════════════

        public List<SpawnedWorldEntity> GetEntitiesForZone(string zoneName)
        {
            var result = new List<SpawnedWorldEntity>();
            if (string.IsNullOrEmpty(zoneName)) return result;

            string key = zoneName.ToLower();
            if (!_entitiesByZone.TryGetValue(key, out var entities))
                return result;

            foreach (var e in entities)
                result.Add(new SpawnedWorldEntity { Data = e });

            return result;
        }

        /// <summary>
        /// Write entity spawn bytes into a LEWriter stream.
        /// Caller manages entityId assignment and BeginStream/EndStream.
        /// </summary>
        public static void WriteEntitySpawn(LEWriter writer, ushort entityId, ushort behaviorId, WorldEntityData data)
        {
            // 0x01 Create entity
            writer.WriteByte(0x01);
            writer.WriteUInt16(entityId);
            WriteGCType(writer, data.GCType);

            // 0x02 Init entity
            writer.WriteByte(0x02);
            writer.WriteUInt16(entityId);
            writer.WriteUInt32(data.Flags);  // flags from DB (default 0x07)

            // Position (Fixed32)
            writer.WriteInt32((int)(data.PosX * 256));
            writer.WriteInt32((int)(data.PosY * 256));
            writer.WriteInt32((int)(data.PosZ * 256));
            writer.WriteInt32((int)(data.Heading * 256));

            // initFlags — no additional data for NCIs
            writer.WriteByte(0x00);

            // ═══════════════════════════════════════════════════════════
            // NCI/Door extra init bytes — binary proven:
            //
            // NCI::readInit @ 0x5A8E20 reads AFTER WorldEntity::readInit:
            //   byte  → +0x31D (activation flags: bit0=activated, bit1=multiActivate)
            //   byte  → +0x326 (state)
            //   uint16 → +0x324 (counter)
            //   Then conditional linked list if GC flag bit 0x80 at +0x1B3
            //
            // Door::readInit @ 0x5A6A10 reads AFTER WorldEntity::readInit:
            //   byte  → door state bits at +0xC8
            //   byte  → more door state bits at +0xC8
            //   (Door does NOT go through NCI::readInit)
            // ═══════════════════════════════════════════════════════════

            if (data.IsGate)
            {
                // Door::readInit @ 0x5A6A10 — calls WorldEntity directly, 2 bytes:
                writer.WriteByte(0x00);  // door open/closed state
                writer.WriteByte(0x00);  // additional door flags
            }
            else
            {
                // Intermediate parent::readInit @ 0x50A580 — 6 bytes:
                //   byte  → flags (0x00 = no conditional data)
                //   byte  → +0x314 (level/mode)
                //   uint16 → +0x316
                //   uint16 → +0x318
                writer.WriteByte(0x00);  // intermediate flags (no conditionals)
                writer.WriteByte(0x00);  // level/mode
                writer.WriteUInt16(0);   // +0x316
                writer.WriteUInt16(0);   // +0x318

                // NCI::readInit @ 0x5A8E20 — 4 bytes:
                //   byte  → +0x31D (activation flags)
                //   byte  → +0x326 (state)
                //   uint16 → +0x324 (counter)
                writer.WriteByte(0x00);  // activation flags (0 = not activated)
                writer.WriteByte(0x00);  // state (0 = default)
                writer.WriteUInt16(0);   // counter (0 = none)

                // 0x32 CreateChild: Behavior
                // TTD-PROVEN: processComponentCreate@0x5DB30D reads 1 flag byte,
                // then ALWAYS calls readInit@0x5DB379 (vtable+0xC0).
                // There is NO "hasInit" byte — readInit always runs.
                // UnitBehavior::readInit = Behavior(4) + UnitMover(23) + own(3) = 30 bytes
                writer.WriteByte(0x32);
                writer.WriteUInt16(entityId);
                writer.WriteUInt16(behaviorId);
                WriteGCType(writer, "base.noncombatinteractive.behavior");

                // Flag byte — consumed by processComponentCreate, stored in [child+0x60]
                writer.WriteByte(0x01);

                // ── Behavior::readInit (4 bytes) ──
                // Byte 1: flags (XOR'd with [behavior+0x7C])
                writer.WriteByte(0xFF);
                // Byte 2: action class ID — 0x00 = NO action (skips createAction)
                writer.WriteByte(0x00);
                // Byte 3: second action class ID — 0x00 = skip
                writer.WriteByte(0x00);
                // Byte 4: end byte
                writer.WriteByte(0x01);

                // ── UnitMover::readInit (10 bytes, flags=0x08) ──
                // flags=0x08 (bit 3) → mover+0x60 bit 3 set. Combined with GC default
                // bit 0, enables UnitMover::Update@0x535839 scene heading application.
                writer.WriteByte(0x08);  // mover flags
                writer.WriteInt32((int)(data.Heading * 256));  // mover+0x64
                writer.WriteInt32((int)(data.Heading * 256));  // mover+0x68
                writer.WriteByte(0x00);  // waypoint

                // ── UnitBehavior::readInit own (3 bytes) ──
                writer.WriteByte(0xFF);  // flags
                writer.WriteByte(0x00);  // extra
                writer.WriteByte(0x00);  // extra2
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // ACTIVATION — Route entity activation by type
        // Returns the entity type so UGS can handle it appropriately
        // ═══════════════════════════════════════════════════════════════

        public void TrackSpawnedEntity(ushort entityId, WorldEntityData data)
        {
            _spawnedEntities[entityId] = data;
        }

        public bool TryGetEntity(ushort entityId, out WorldEntityData data)
        {
            return _spawnedEntities.TryGetValue(entityId, out data);
        }

        public bool FindEntityByName(string name, string zoneName, out ushort entityId, out WorldEntityData data)
        {
            // Strip instance suffix: "dungeon00_level03_boss_inst2147483649" → "dungeon00_level03_boss"
            string baseZone = zoneName;
            int instIdx = zoneName.IndexOf("_inst", StringComparison.OrdinalIgnoreCase);
            if (instIdx > 0) baseZone = zoneName.Substring(0, instIdx);

            foreach (var kvp in _spawnedEntities)
            {
                var e = kvp.Value;
                if (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.Zone, baseZone, StringComparison.OrdinalIgnoreCase))
                {
                    entityId = kvp.Key;
                    data = e;
                    return true;
                }
            }
            entityId = 0;
            data = null;
            return false;
        }

        public Dictionary<ushort, WorldEntityData> GetSpawnedEntities()
        {
            return _spawnedEntities;
        }

        public void RemoveEntity(ushort entityId)
        {
            _spawnedEntities.Remove(entityId);
        }

        public void ClearZoneEntities()
        {
            _spawnedEntities.Clear();
        }

        // ═══════════════════════════════════════════════════════════════
        // HELPERS
        // ═══════════════════════════════════════════════════════════════

        private static void WriteGCType(LEWriter writer, string typeName)
        {
            writer.WriteByte(0xFF);
            writer.WriteCString(typeName);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // DATA CLASSES
    // ═══════════════════════════════════════════════════════════════════════════

    [Serializable]
    public class WorldEntityData
    {
        public int Id;
        public string Zone;
        public string Name;
        public string GCType;
        public string EntityType;       // chest, gate, shrine, teleporter, npc, nci
        public float PosX, PosY, PosZ;
        public float Heading;
        public uint Flags;              // Default 0x07 = visible|activatable|blocking

        // Chest fields
        public string ItemGenerator;
        public int ItemCount;
        public string ItemGenerator2;
        public int ItemCount2;

        // Teleporter fields
        public string TargetZone;
        public string TargetSpawn;

        // General
        public string Label;
        public bool AllowMultiple;

        public bool IsChest => EntityType == "chest";
        public bool IsTeleporter => EntityType == "teleporter";
        public bool IsShrine => EntityType == "shrine";
        public bool IsGate => EntityType == "gate";
        public bool IsNPC => EntityType == "npc";
    }

    public class SpawnedWorldEntity
    {
        public WorldEntityData Data;
        public ushort EntityId;         // Assigned by UGS during spawn
    }
}