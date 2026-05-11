using System;
using UnityEngine;
using DungeonRunners.Utilities;
using Org.BouncyCastle.Bcpg.Sig;

namespace DungeonRunners.Combat
{
    public static class CombatPackets
    {
        public static byte[] BuildMonsterSpawnPacket(
     Monster monster,
     uint behaviorId,
     uint skillsId,
     uint manipulatorsId,
     uint modifiersId,
     ushort targetEntityId = 0,
     ushort playerEntityId = 0,
     uint rngSeed = 0)
        {
            var writer = new LEWriter();
            int posX = (int)(monster.PosX * 256);
            int posY = (int)(monster.PosY * 256);
            int posZ = (int)(monster.PosZ * 256);
            int heading = (int)(monster.Heading * 256);
            byte lvl = monster.Level;
            if (lvl == 0) lvl = 1;
            uint currentHPWire = CombatManager.Instance.GetMonsterCurrentHPWire(monster, "SPAWN-PKT");

            writer.WriteByte(0x07); // BeginStream

            // ========== OP1: Create Monster Entity (0x01) ==========
            writer.WriteByte(0x01);
            writer.WriteUInt16((ushort)monster.EntityId);
            string entityGCType = MapToBaseGCType(monster.SpawnGCType ?? monster.GCType);
            Debug.LogError($"[SPAWN-PKT] Monster {monster.Name} entityGCType='{entityGCType}' spawnGCType='{monster.SpawnGCType}' baseGCType='{monster.GCType}' pos=({monster.PosX:F2},{monster.PosY:F2},{monster.PosZ:F2}) wire=({posX},{posY},{posZ}) heading={monster.Heading:F2}/{heading} level={lvl} hpWire={currentHPWire}/{monster.MaxHPWire} aggroRange={monster.AggroRange:F2} attackRange={monster.AttackRange:F2}");
            WriteGCType(writer, entityGCType, true);

            // ========== OP2: Init Entity (0x02) - 52 bytes ==========
            writer.WriteByte(0x02);
            writer.WriteUInt16((ushort)monster.EntityId);

            // Entity::readInit (21 bytes)
            writer.WriteUInt32(0x06);
            writer.WriteInt32(posX);
            writer.WriteInt32(posY);
            writer.WriteInt32(posZ);
            writer.WriteInt32(heading);
            writer.WriteByte(0x00);

            // Unit::readInit (6 bytes)
            writer.WriteByte(0x00);
            writer.WriteByte(lvl);
            writer.WriteUInt16(0);
            writer.WriteUInt16(0);

            // StockUnit::setEntityId (25 bytes)
            writer.WriteByte(0x00);
            writer.WriteUInt16(0);
            writer.WriteUInt16(0);
            writer.WriteByte(0x00);
            writer.WriteUInt16(0);
            writer.WriteUInt32(0);
            writer.WriteByte(0x00);
            writer.WriteUInt32(0);
            writer.WriteUInt32(0);
            writer.WriteUInt32(0);

            // ========== RNG Seed (opcode 0x0C) ==========
            if (rngSeed != 0)
            {
                writer.WriteByte(0x0C);
                writer.WriteUInt32(rngSeed);
                Debug.LogError($"[SPAWN-RNG] Wrote opcode 0x0C seed 0x{rngSeed:X8} into entity stream for {monster.Name}");
            }

            // ========== OP3: Create Behavior Component (0x32) ==========
            writer.WriteByte(0x32);
            writer.WriteUInt16((ushort)monster.EntityId);
            writer.WriteUInt16((ushort)behaviorId);
            //WriteGCType(writer, monster.BehaviourType, false);
            string behaviorType = monster.SpawnBehaviourType ?? monster.BehaviourType;
            Debug.LogError($"[SPAWN-PKT] Monster {monster.Name} behaviorType='{behaviorType}' (SpawnOverride='{monster.SpawnBehaviourType}' Default='{monster.BehaviourType}')");
            WriteGCType(writer, behaviorType, false);
            writer.WriteByte(0x01); // hasInit

            // --- Behavior::readInit @0x515970 (4 bytes) ---
            writer.WriteByte(0xFF);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);

            // --- DFCStateMachine::readInit @0x535c20 (23 bytes) ---
            writer.WriteByte(0x85);
            writer.WriteByte(0x00);
            writer.WriteUInt32(0x00000000);
            writer.WriteUInt32(0x00000000);
            writer.WriteUInt32(0x00000000);
            writer.WriteUInt32(0x00000000);
            writer.WriteUInt32(0x00000000);
            writer.WriteByte(0x00);

            // --- UnitBehavior::readInit @0x51fe10 remaining (3 bytes) ---
            writer.WriteByte(0xFF);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);

            // --- StateMachine::ReadMessage @0x5f0c70 (7 bytes) ---
            writer.WriteByte(0x0F);
            writer.WriteUInt16(0xFFFF);
            writer.WriteUInt16(0xFFFF);
            writer.WriteUInt16(0xFFFF);      // roam + aggro — AI must initialize first

            writer.WriteByte(0x10);
            writer.WriteUInt32(0x00000000);
            writer.WriteUInt32(0x00000000);
            writer.WriteUInt16(0x0001);

            // ========== OP4: Create Skills Component (0x32) ==========
            writer.WriteByte(0x32);
            writer.WriteUInt16((ushort)monster.EntityId);
            writer.WriteUInt16((ushort)skillsId);
            WriteGCType(writer, "skills", false);
            writer.WriteByte(0x01);
            writer.WriteByte(0xFF);
            writer.WriteByte(0xFF);
            writer.WriteByte(0xFF);
            writer.WriteByte(0xFF);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
            // ========== OP5: Create Manipulators Component (0x32) ==========
            writer.WriteByte(0x32);
            writer.WriteUInt16((ushort)monster.EntityId);
            writer.WriteUInt16((ushort)manipulatorsId);
            WriteGCType(writer, "manipulators", false);
            writer.WriteByte(0x01);

            var manipulatorsToSend = new System.Collections.Generic.List<ManipulatorEntry>();

            if (monster.Manipulators != null)
            {
                if (monster.Manipulators.TryGetValue("skill1", out var skill1))
                {
                    float cooldown = GetManipulatorFloat(skill1, "CoolDown");
                    float range = GetManipulatorFloat(skill1, "Range");
                    uint id = GetManipulatorUInt(skill1, "ID");
                    manipulatorsToSend.Add(new ManipulatorEntry(skill1.gcType, id, cooldown, range, ManipulatorType.ActiveSkill));
                }

                if (monster.Manipulators.TryGetValue("skill2", out var skill2))
                {
                    float cooldown = GetManipulatorFloat(skill2, "CoolDown");
                    float range = GetManipulatorFloat(skill2, "Range");
                    uint id = GetManipulatorUInt(skill2, "ID");
                    manipulatorsToSend.Add(new ManipulatorEntry(skill2.gcType, id, cooldown, range, ManipulatorType.ActiveSkill));
                }

                if (monster.Manipulators.TryGetValue("primaryweapon", out var weapon))
                {
                    float cooldown = GetManipulatorFloat(weapon, "CoolDown");
                    float range = GetManipulatorFloat(weapon, "Range");
                    uint id = GetManipulatorUInt(weapon, "ID");
                    var weaponType = IsRangedManipulator(weapon) ? ManipulatorType.RangedWeapon : ManipulatorType.MeleeWeapon;
                    manipulatorsToSend.Add(new ManipulatorEntry(weapon.gcType, id, cooldown, range, weaponType));
                }
            }

            writer.WriteByte((byte)manipulatorsToSend.Count);
            Debug.LogError($"[SPAWN-PACKET] Writing {manipulatorsToSend.Count} manipulators");

            foreach (var manip in manipulatorsToSend)
            {
                WriteGCType(writer, manip.GCType, true);
                Debug.LogError($"[SPAWN-PACKET]   Manip: {manip.GCType} type={manip.Type} range={manip.Range:F2} cooldown={manip.Cooldown:F2}");

                if (manip.Type == ManipulatorType.ActiveSkill)
                {
                    writer.WriteUInt32(manip.Id);
                    writer.WriteByte(0x00);
                }
                else if (manip.Type == ManipulatorType.MeleeWeapon)
                {
                    writer.WriteUInt32(manip.Id);
                    writer.WriteByte(0x00);
                    writer.WriteByte(0x00);
                    writer.WriteByte(0x00);
                    writer.WriteByte(0x00);
                    writer.WriteByte(0x00);
                    writer.WriteByte(0x00);
                    writer.WriteUInt16(0x0000);
                    writer.WriteByte(0x00);
                    writer.WriteUInt16(0x0000);
                }
                else
                {
                    writer.WriteUInt32(manip.Id);
                    writer.WriteByte(0x00);
                    writer.WriteByte(0x00);
                    writer.WriteByte(0x00);
                    writer.WriteByte(0x00);
                    writer.WriteByte(0x00);
                    writer.WriteByte(0x00);
                    writer.WriteUInt16(0x0000);
                    writer.WriteUInt16(0x0000);
                }
            }

            // ========== OP6: Create Modifiers Component (0x32) ==========
            writer.WriteByte(0x32);
            writer.WriteUInt16((ushort)monster.EntityId);
            writer.WriteUInt16((ushort)modifiersId);
            WriteGCType(writer, "modifiers", false);
            writer.WriteByte(0x01);
            writer.WriteUInt32(0);
            writer.WriteByte(0x00);
            writer.WriteUInt32(0);

            // ========== OP7: SpawnAction ==========
            writer.WriteByte(0x35);
            writer.WriteUInt16((ushort)behaviorId);
            writer.WriteByte(0x04);
            writer.WriteByte(0x04);
            writer.WriteByte(0xFF);
            writer.WriteInt32(posX);
            writer.WriteInt32(posY);
            writer.WriteInt32(posZ);
            writer.WriteUInt16((ushort)monster.EntityId);
            writer.WriteByte(0x02);
            writer.WriteUInt32(currentHPWire);

            // ========== OP8: MoverUpdate ==========
            writer.WriteByte(0x35);
            writer.WriteUInt16((ushort)behaviorId);
            writer.WriteByte(0x65);
            writer.WriteByte(0x00);
            writer.WriteByte(0x01);
            writer.WriteByte(0x03);
            writer.WriteInt32(heading);
            writer.WriteInt32(posX);
            writer.WriteInt32(posY);
            writer.WriteByte(0x02);
            writer.WriteUInt32(currentHPWire);

            writer.WriteByte(0x06);  // EndStream


            byte[] packet = writer.ToArray();
            Debug.LogError($"[MONSTER-SPAWN-HEX] Size: {packet.Length} hex: {BitConverter.ToString(packet)}");
            return packet;
        }
        public static byte[] BuildSkillsStateMachineInit(uint skillsComponentId, uint entityId, uint currentHPWire)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x07);  // BeginStream
            writer.WriteByte(0x35);  // ComponentUpdate
            writer.WriteUInt16((ushort)skillsComponentId);
            writer.WriteByte(0x65);  // StateMachine message (VALID sub-type!)
            writer.WriteByte(0x02);  // HP sync flag  

            writer.WriteUInt32(currentHPWire);  // HP value
            writer.WriteByte(0x06);  // EndStream
            return writer.ToArray();
        }
        /// <summary>
        /// Build interval packet (opcode 0x0D) - triggers client component reporting cycle.
        /// Original server sent this every 4th tick via writeIntervals@ServerEntityManager.
        /// Without this, client never activates position/state reporting for entities.
        /// </summary>
        public static byte[] BuildIntervalPacket(uint updateNumber, uint entityUpdateNum,
            ushort nodeCountA, ushort nodeCountB)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x0D);                 // interval opcode
            writer.WriteUInt32(updateNumber);       // SEM+0xB28: global update number
            writer.WriteUInt32(updateNumber);       // SEM+0xB14: sync counter
            writer.WriteUInt32(0);                  // SEM+0xB10: sync counter
                                                    // Per-entity interval data (entity+0x20, +0x54, +0x58)
            writer.WriteUInt32(entityUpdateNum);    // entity update counter
            writer.WriteUInt16(nodeCountA);         // component node count A
            writer.WriteUInt16(nodeCountB);         // component node count B
            return writer.ToArray();
        }



        public static byte[] BuildStateMachineMessage(uint behaviorId, ushort messageType, uint value = 0, ushort targetScope = 0)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x07);  // BeginStream

            writer.WriteByte(0x35);  // ComponentUpdate
            writer.WriteUInt16((ushort)behaviorId);
            writer.WriteByte(0x64);  // StateMachine message type

            byte flags = 0x00;
            if (targetScope != 0) flags |= 0x01;

            writer.WriteByte(flags);
            writer.WriteUInt16(messageType);
            writer.WriteUInt32(value);

            if ((flags & 0x01) != 0)
            {
                writer.WriteUInt16(targetScope);
            }

            writer.WriteByte(0x00);
            writer.WriteByte(0x06);  // EndStream
            return writer.ToArray();
        }

        public static byte[] BuildMonsterDespawnPacket(uint entityId)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x03);
            writer.WriteUInt16((ushort)entityId);
            writer.WriteByte(0x06);
            return writer.ToArray();
        }
        /// <summary>
        /// Builds a Skills state machine initialization message.
        /// This sends type 0x15 with param=3 and sub-type 0x0F to trigger state machine setup at entity+0x1b0.
        /// Must be sent AFTER monster spawn to initialize RNG/combat message handling.
        /// 
        /// Binary analysis:
        /// - Handler at 0x609c20 checks: [esp+0x18]=0x15, [esp+0x20]=3, [payload+8]=0x0F
        /// - When conditions met, calls 0x608b60 -> 0x47b280 -> 0x41daf0 which writes entity+0x1b0
        /// </summary>




        public static byte[] BuildMonsterMovePacket(uint entityId, uint behaviorId, float posX, float posY, float posZ, float heading, byte sessionId, uint currentHPWire)
        {
            var writer = new LEWriter();

            writer.WriteByte(0x07);  // BeginStream

            writer.WriteByte(0x35);  // ComponentUpdate
            writer.WriteUInt16((ushort)behaviorId);
            writer.WriteByte(0x04);  // CreateAction1
            writer.WriteByte(0x01);  // MoveTo = 1
            writer.WriteByte(sessionId);
            writer.WriteByte(0x00);

            writer.WriteByte(0x02);
            writer.WriteUInt32(currentHPWire);

            writer.WriteByte(0x06);  // EndStream

            return writer.ToArray();
        }

        public static byte[] BuildDamagePacket(DamageEvent evt)
        {
            var writer = new LEWriter();

            writer.WriteByte(0x07);
            writer.WriteByte(0x28);
            writer.WriteUInt16((ushort)evt.DefenderId);
            writer.WriteByte(0x1A);

            writer.WriteUInt32(evt.AttackerId);
            writer.WriteUInt32(evt.DefenderId);
            writer.WriteInt32((int)evt.DamageWire);

            writer.WriteByte(0x00);
            byte flags = 0;
            if (evt.IsCritical) flags |= 0x01;
            writer.WriteByte(flags);

            writer.WriteFloat(evt.PosX);
            writer.WriteFloat(evt.PosY);
            writer.WriteFloat(evt.PosZ);

            writer.WriteByte(0x06);

            return writer.ToArray();
        }

        public static byte[] BuildHPUpdatePacket(uint entityId, uint currentHPWire, uint maxHPWire)
        {
            var writer = new LEWriter();

            writer.WriteByte(0x07);
            writer.WriteByte(0x28);
            writer.WriteUInt16((ushort)entityId);
            writer.WriteByte(0x0F);

            writer.WriteUInt32(currentHPWire);
            writer.WriteUInt32(maxHPWire);

            writer.WriteByte(0x06);

            return writer.ToArray();
        }
        /// <summary>
        /// Build opcode 0x36 (processUpdateComponent) packet.
        /// Binary: handler at 0x5DB6A0 reads componentID(2), calls vtable+0xB8 (readUpdate, 
        /// which is a no-op for ALL component types), then EntitySynchInfo::ReadFromStream 
        /// reads syncFlags(1) + HP(4 if flags&2).
        /// Wire format: 0x36 + componentID(2) + syncFlags(1) + HP(4)
        /// </summary>
        public static byte[] BuildProcessUpdateComponent(ushort componentId, uint currentHPWire)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x07);           // BeginStream
            writer.WriteByte(0x36);           // processUpdateComponent opcode
            writer.WriteUInt16(componentId);  // componentID (any valid component)
            writer.WriteByte(0x02);           // syncFlags = HP present
            writer.WriteUInt32(currentHPWire); // HP value (Fixed32 8.8)
            writer.WriteByte(0x06);           // EndStream
            return writer.ToArray();
        }
        public static byte[] BuildDeathPacket(uint entityId, uint killerId)
        {
            var writer = new LEWriter();

            writer.WriteByte(0x07);
            writer.WriteByte(0x28);
            writer.WriteUInt16((ushort)entityId);
            writer.WriteByte(0x20);

            writer.WriteUInt32(killerId);

            writer.WriteByte(0x06);

            return writer.ToArray();
        }
        /// <summary>
        /// Destroy and recreate a monster's behavior component with the player set as watcher (target).
        /// VERIFIED in EXE: opcode 0x33 (destroy) calls vtable[0xC0]=readInit @ 0x5DB4D2,
        /// so it consumes the FULL readInit data from the stream just like 0x32 (create).
        /// The watcher at MonsterBehavior2+0x198 can ONLY be set during readInit.
        /// </summary>
        public static byte[] BuildBehaviorWatcherUpdate(
             Monster monster,
             uint oldBehaviorId,    // ID to destroy
             uint newBehaviorId,    // new ID to create
             ushort targetEntityId)
        {
            var writer = new LEWriter();

            writer.WriteByte(0x07); // BeginStream

            // ========== Destroy old behavior component ==========
            writer.WriteByte(0x33);  // Destroy component
            writer.WriteUInt16((ushort)oldBehaviorId);
            // --- Behavior::readInit @0x515970 (4 bytes) ---
            writer.WriteByte(0xFF);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
            writer.WriteByte(0x01);
            // --- DFCStateMachine::readInit @0x535c20 (23 bytes, flags=0x85) ---
            writer.WriteByte(0x85);
            writer.WriteByte(0x00);
            writer.WriteUInt32(0x00000000);
            writer.WriteUInt32(0x00000000);
            writer.WriteUInt32(0x00000000);
            writer.WriteUInt32(0x00000000);
            writer.WriteUInt32(0x00000000);
            writer.WriteByte(0x00);
            // --- UnitBehavior::readInit remaining (3 bytes) ---
            writer.WriteByte(0xFF);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
            // --- StateMachine::ReadMessage @0x5f0c70 (7 bytes, flags=0x0E) ---
            writer.WriteByte(0x0F);
            writer.WriteUInt16(0x0000);
            writer.WriteUInt16(0x0005);
            writer.WriteUInt16(0x0009);
            // --- MonsterBehavior2 watcher flags (no watchers for destroy) ---
            writer.WriteByte(0x10);
            writer.WriteUInt32(0x00000000);
            writer.WriteUInt32(0x00000000);
            writer.WriteUInt16(0x0001);

            // ========== Re-create behavior component WITH watcher ==========
            writer.WriteByte(0x32);  // Create component
            writer.WriteUInt16((ushort)monster.EntityId);
            writer.WriteUInt16((ushort)newBehaviorId);  // NEW ID
            string behaviorType = monster.SpawnBehaviourType ?? monster.BehaviourType;
            WriteGCType(writer, behaviorType, false);
            //  WriteGCType(writer, "crowd_behavior", false);
            writer.WriteByte(0x01); // hasInit
            // --- Behavior::readInit @0x515970 (4 bytes) ---
            writer.WriteByte(0xFF);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
            writer.WriteByte(0x01);
            // --- DFCStateMachine::readInit @0x535c20 (23 bytes, flags=0x85) ---
            writer.WriteByte(0x85);
            writer.WriteByte(0x00);
            writer.WriteUInt32(0x00000000);
            writer.WriteUInt32(0x00000000);
            writer.WriteUInt32(0x00000000);
            writer.WriteUInt32(0x00000000);
            writer.WriteUInt32(0x00000000);
            writer.WriteByte(0x00);
            // --- UnitBehavior::readInit remaining (3 bytes) ---
            writer.WriteByte(0xFF);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
            // --- StateMachine::ReadMessage @0x5f0c70 (7 bytes, flags=0x0E) ---
            writer.WriteByte(0x0E);
            writer.WriteUInt16(0x0000);
            writer.WriteUInt16(0x0005);
            writer.WriteUInt16(0x0009);
            // --- MonsterBehavior2 watcher flags WITH target ---
            writer.WriteByte(0x14);  // was 0x1C — match old code
            writer.WriteUInt32(0x00000000);
            writer.WriteUInt32(0x00000000);
            writer.WriteUInt16(targetEntityId);  // PRIMARY only
                                                 // REMOVE: writer.WriteUInt16(targetEntityId);  // no secondary
            writer.WriteUInt16(0x0001);

            writer.WriteByte(0x06); // EndStream

            byte[] packet = writer.ToArray();
            Debug.LogError($"[WATCHER-UPDATE] Built packet size={packet.Length}, oldId={oldBehaviorId}, newId={newBehaviorId}, target={targetEntityId}");
            Debug.LogError($"[WATCHER-UPDATE] Hex: {BitConverter.ToString(packet).Replace("-", "")}");
            return packet;
        }

        /// <summary>
        /// Send 0x64 to monster's BehaviorId to set bit0 at UnitBehavior+0x156.
        /// processUpdate type 0x64 reads 1 byte.
        /// If nonzero → sets bit0 → calls FollowClient → client starts sending 0x65 position updates.
        /// </summary>
        public static byte[] BuildEnableClientControl(uint behaviorId, bool enable, uint currentHPWire)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x07);  // BeginStream
            writer.WriteByte(0x35);  // ComponentUpdate
            writer.WriteUInt16((ushort)behaviorId);
            writer.WriteByte(0x64);  // UnitBehavior processUpdate type
            writer.WriteByte((byte)(enable ? 0x01 : 0x00));
            writer.WriteByte(0x02);  // sync suffix flags (bit 1 = HP present)
            writer.WriteUInt32(currentHPWire);
            writer.WriteByte(0x06);  // EndStream
            return writer.ToArray();
        }

        private static void WriteGCType(LEWriter writer, string gcType, bool preserveCase)
        {
            string safeTypeName = preserveCase ? gcType : gcType.ToLower();
            writer.WriteByte(0xFF);
            writer.WriteCString(safeTypeName);
        }

        private static float GetManipulatorFloat(ManipulatorData manipulator, string property)
        {
            if (manipulator?.properties == null) return 0f;
            if (!manipulator.properties.TryGetValue(property, out string value)) return 0f;
            if (float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float result))
                return result;
            return 0f;
        }

        private static uint GetManipulatorUInt(ManipulatorData manipulator, string property)
        {
            if (manipulator?.properties == null) return 0;
            if (!manipulator.properties.TryGetValue(property, out string value)) return 0;
            if (uint.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out uint result))
                return result;
            return 0;
        }

        private static bool IsRangedManipulator(ManipulatorData manipulator)
        {
            if (manipulator?.properties == null) return false;
            return manipulator.properties.ContainsKey("ShotType")
                || manipulator.properties.ContainsKey("UseProjectile")
                || manipulator.properties.ContainsKey("ProjectileSpeed")
                || manipulator.properties.ContainsKey("ProjectileSize");
        }

        // Maps creature gcTypes to their base class that has the proper Label/Name defined
        private static string MapToBaseGCType(string gcType)
        {
            switch (gcType.ToLower())
            {
                // Dungeon00 mobs
                case "creatures.forestcreatures.warg.basic.pup":
                    return "world.dungeon00.mob.melee01.rank1";  // Dew Valley Pup
                case "creatures.forestcreatures.warg.basic.grunt":
                    return "world.dungeon00.mob.melee02.rank1";  // Dew Valley Wolf
                case "creatures.whiskers.broodling.basic.grunt":
                    return "world.dungeon00.mob.melee03.rank1";  // Whisker Ratling
                case "creatures.whiskers.blademaster.basic.grunt":
                    return "world.dungeon00.mob.melee04.rank1";  // Whisker Blademaster
                case "creatures.whiskers.broodling.basic.champion":
                    return "world.dungeon00.mob.boss";          // Rattle Tooth (boss)
                case "world.objects.barrel.breakable":
                case "world.objects.barrel.breakable.02":
                case "world.objects.barrel.breakable.03":
                    return "world.dungeon00.mob.CreatureBarrel"; // Exploding barrel
                default:
                    return gcType;
            }
        }

        /// <summary>
        /// Build packet for monster attacking player
        /// </summary>
        public static byte[] BuildMonsterAttackPacket(uint monsterBehaviorId, ushort targetPlayerId, byte sessionId, byte useFlags, uint currentHPWire)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x07);  // BeginStream

            writer.WriteByte(0x35);              // ComponentUpdate
            writer.WriteUInt16((ushort)monsterBehaviorId);
            writer.WriteByte(0x04);              // CreateAction
            writer.WriteByte(0x50);              // UseTarget action type
            writer.WriteByte(sessionId);
            writer.WriteByte(useFlags);
            writer.WriteUInt16(targetPlayerId);  // target
            writer.WriteByte(0x02);
            writer.WriteUInt32(currentHPWire);

            writer.WriteByte(0x06);  // EndStream

            return writer.ToArray();
        }

        // Helper struct for manipulator entries
        private struct ManipulatorEntry
        {
            public string GCType;
            public uint Id;
            public float Cooldown;
            public float Range;
            public ManipulatorType Type;

            public ManipulatorEntry(string gcType, uint id, float cooldown, float range, ManipulatorType type)
            {
                GCType = gcType;
                Id = id;
                Cooldown = cooldown;
                Range = range;
                Type = type;
            }
        }

        private enum ManipulatorType
        {
            MeleeWeapon,
            RangedWeapon,
            ActiveSkill
        }
    }
}
