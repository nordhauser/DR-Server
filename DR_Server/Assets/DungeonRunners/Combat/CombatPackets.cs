using System;
using UnityEngine;
using DungeonRunners.Utilities;
using DungeonRunners.Networking.Sync;
using Org.BouncyCastle.Bcpg.Sig;

namespace DungeonRunners.Combat
{
    public static class CombatPackets
    {
        private static void WriteEntitySynchInfo(LEWriter writer, string packetName, string owner, uint ownerEntityId, uint componentId, byte subtype, byte syncFlags, uint syncHPWire, bool requireHP)
        {
            if (requireHP)
                syncFlags = 0x02;

            writer.WriteByte(syncFlags);
            if ((syncFlags & 0x02) != 0)
                writer.WriteUInt32(syncHPWire);

            string hpText = (syncFlags & 0x02) != 0 ? syncHPWire.ToString() : "none";
            Debug.LogError($"[SYNC-SUFFIX] packet={packetName} owner={owner} entity={ownerEntityId} component={componentId} sub=0x{subtype:X2} flags=0x{syncFlags:X2} hp={hpText}");
        }

        private static void RejectRawAliveHPSuffix(string packetName, byte syncFlags)
        {
            if ((syncFlags & 0x02) != 0)
                throw new InvalidOperationException($"{packetName} raw HP suffix is quarantined; use a resolved EntitySynchInfo payload");
        }

        private static void WriteResolvedEntitySynchInfo(LEWriter writer, string packetName, string owner, ResolvedEntitySynchInfo sync, bool requireHP)
        {
            if (requireHP && !sync.HasHP)
                throw new InvalidOperationException($"{packetName} requires a resolved HP EntitySynchInfo payload");

            sync.Payload.Write(writer);
            string hpText = sync.HasHP ? sync.HPWire.ToString() : "none";
            Debug.LogError($"[SYNC-SUFFIX] packet={packetName} owner={owner} entity={sync.OwnerEntityId} component={sync.ComponentId} sub=0x{sync.Subtype:X2} flags=0x{sync.Flags:X2} hp={hpText} nativeNow={sync.NativeNow:F3} cutoffTick={sync.ValidationCutoffTick} cutoffTime={sync.ValidationCutoffTime:F3} reason={sync.Reason} provenance={sync.Provenance}");
        }

        private static void WriteUnitReadInit(LEWriter writer, byte level, uint currentHPWire, uint currentManaWire)
        {
            byte unitFlags = currentManaWire > 0 ? (byte)0x06 : (byte)0x02;
            writer.WriteByte(unitFlags);
            writer.WriteByte(level);
            writer.WriteUInt16(0);
            writer.WriteUInt16(0);
            writer.WriteUInt32(currentHPWire);
            if ((unitFlags & 0x04) != 0)
                writer.WriteUInt32(currentManaWire);
        }

        private static void WriteBehaviorReadInitNoActions(LEWriter writer, byte endByte)
        {
            writer.WriteByte(0xFF);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
            writer.WriteByte(endByte);
        }

        private static void WriteMonsterUnitMoverReadInit(LEWriter writer)
        {
            writer.WriteByte(0x85);
            writer.WriteByte(0x00);
            writer.WriteUInt32(0x00000000);
            writer.WriteUInt32(0x00000000);
            writer.WriteUInt32(0x00000000);
            writer.WriteUInt32(0x00000000);
            writer.WriteUInt32(0x00000000);
            writer.WriteByte(0x00);
        }

        private static void WriteUnitBehaviorReadInitNoClientControl(LEWriter writer)
        {
            writer.WriteByte(0xFF);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
        }

        private static void WriteStateMachineReadMessageHeader(
            LEWriter writer,
            byte flags,
            ushort field10 = 0,
            ushort field12 = 0,
            ushort field14 = 0)
        {
            writer.WriteByte(flags);
            if ((flags & 0x02) != 0)
                writer.WriteUInt16(field10);
            if ((flags & 0x04) != 0)
                writer.WriteUInt16(field12);
            if ((flags & 0x08) != 0)
                writer.WriteUInt16(field14);
        }

        private static void WriteMonsterBehavior2ReadInit(
            LEWriter writer,
            byte flags,
            ushort primaryTargetId = 0,
            ushort secondaryTargetId = 0,
            ushort targetFilter = 0)
        {
            writer.WriteByte(flags);
            writer.WriteUInt32(0x00000000);
            writer.WriteUInt32(0x00000000);
            if ((flags & 0x04) != 0)
                writer.WriteUInt16(primaryTargetId);
            if ((flags & 0x08) != 0)
                writer.WriteUInt16(secondaryTargetId);
            if ((flags & 0x10) != 0)
                writer.WriteUInt16(targetFilter);
        }

        public static byte[] BuildMonsterSpawnPacket(
     Monster monster,
     uint behaviorId,
     uint skillsId,
     uint manipulatorsId,
     uint modifiersId,
     ushort targetEntityId,
     ushort playerEntityId,
     ResolvedEntitySynchInfo sync)
        {
            var writer = new LEWriter();
            int posX = (int)(monster.PosX * 256);
            int posY = (int)(monster.PosY * 256);
            int posZ = (int)(monster.PosZ * 256);
            int heading = (int)(monster.Heading * 256);
            byte lvl = monster.Level;
            if (lvl == 0) lvl = 1;
            if (monster.IsAlive && !sync.HasHP)
                throw new InvalidOperationException($"MON-SPAWN requires resolved HP for alive monster {monster.Name}#{monster.EntityId}");
            uint resolvedHPWire = sync.HPWire;
            if (resolvedHPWire > monster.MaxHPWire) resolvedHPWire = monster.MaxHPWire;
            uint resolvedManaWire = monster.MaxManaWire > 0 ? Math.Min(monster.CurrentManaWire, monster.MaxManaWire) : monster.CurrentManaWire;

            writer.WriteByte(0x07); // BeginStream

            // ========== OP1: Create Monster Entity (0x01) ==========
            writer.WriteByte(0x01);
            writer.WriteUInt16((ushort)monster.EntityId);
            string entityGCType = MapToBaseGCType(monster.SpawnGCType ?? monster.GCType);
            Debug.LogError($"[SPAWN-PKT] Monster {monster.Name} entityGCType='{entityGCType}' spawnGCType='{monster.SpawnGCType}' baseGCType='{monster.GCType}' pos=({monster.PosX:F2},{monster.PosY:F2},{monster.PosZ:F2}) wire=({posX},{posY},{posZ}) heading={monster.Heading:F2}/{heading} level={lvl} hpWire={resolvedHPWire}/{monster.MaxHPWire} manaWire={resolvedManaWire}/{monster.MaxManaWire} aggroRange={monster.AggroRange:F2} attackRange={monster.AttackRange:F2}");
            WriteGCType(writer, entityGCType, true);

            // ========== OP2: Init Entity (0x02) ==========
            writer.WriteByte(0x02);
            writer.WriteUInt16((ushort)monster.EntityId);

            // Entity::readInit (21 bytes)
            writer.WriteUInt32(0x06);
            writer.WriteInt32(posX);
            writer.WriteInt32(posY);
            writer.WriteInt32(posZ);
            writer.WriteInt32(heading);
            writer.WriteByte(0x00);

            // Unit::readInit
            WriteUnitReadInit(writer, lvl, resolvedHPWire, resolvedManaWire);

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

            // ========== OP3: Create Behavior Component (0x32) ==========
            writer.WriteByte(0x32);
            writer.WriteUInt16((ushort)monster.EntityId);
            writer.WriteUInt16((ushort)behaviorId);
            //WriteGCType(writer, monster.BehaviourType, false);
            string behaviorType = monster.SpawnBehaviourType ?? monster.BehaviourType;
            Debug.LogError($"[SPAWN-PKT] Monster {monster.Name} behaviorType='{behaviorType}' (SpawnOverride='{monster.SpawnBehaviourType}' Default='{monster.BehaviourType}')");
            WriteGCType(writer, behaviorType, false);
            writer.WriteByte(0x01); // hasInit

            WriteBehaviorReadInitNoActions(writer, 0x00);
            WriteMonsterUnitMoverReadInit(writer);
            WriteUnitBehaviorReadInitNoClientControl(writer);
            WriteStateMachineReadMessageHeader(writer, 0x0F, 0xFFFF, 0xFFFF, 0xFFFF);
            WriteMonsterBehavior2ReadInit(writer, 0x10, targetFilter: 0x0001);

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
            WriteResolvedEntitySynchInfo(writer, "MON-SPAWN-ACTION", "Monster", sync, true);

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
            WriteResolvedEntitySynchInfo(writer, "MON-SPAWN-MOVER", "Monster", new ResolvedEntitySynchInfo(sync.Payload, sync.OwnerEntityId, behaviorId, 0x65, sync.NativeNow, sync.Reason, sync.Provenance, sync.ValidationCutoffTick, sync.ValidationCutoffTime), true);

            writer.WriteByte(0x06);  // EndStream


            byte[] packet = writer.ToArray();
            Debug.LogError($"[MONSTER-SPAWN-HEX] Size: {packet.Length} hex: {BitConverter.ToString(packet)}");
            return packet;
        }

        public static byte[] BuildMonsterEntityInitHPRefreshPacket(Monster monster, uint currentHPWire)
        {
            var writer = new LEWriter();
            int posX = (int)(monster.PosX * 256);
            int posY = (int)(monster.PosY * 256);
            int posZ = (int)(monster.PosZ * 256);
            int heading = (int)(monster.Heading * 256);
            byte lvl = monster.Level;
            if (lvl == 0) lvl = 1;
            if (currentHPWire > monster.MaxHPWire) currentHPWire = monster.MaxHPWire;
            uint currentManaWire = monster.MaxManaWire > 0 ? Math.Min(monster.CurrentManaWire, monster.MaxManaWire) : monster.CurrentManaWire;

            writer.WriteByte(0x07);
            writer.WriteByte(0x02);
            writer.WriteUInt16((ushort)monster.EntityId);
            writer.WriteUInt32(0x06);
            writer.WriteInt32(posX);
            writer.WriteInt32(posY);
            writer.WriteInt32(posZ);
            writer.WriteInt32(heading);
            writer.WriteByte(0x00);
            WriteUnitReadInit(writer, lvl, currentHPWire, currentManaWire);
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
            writer.WriteByte(0x06);
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
        public static byte[] BuildMonsterDespawnPacket(uint entityId)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x05);
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




        public static byte[] BuildMonsterMovePacket(uint entityId, uint behaviorId, float targetX, float targetY, ResolvedEntitySynchInfo sync)
        {
            var writer = new LEWriter();
            int fx = (int)(targetX * 256f);
            int fy = (int)(targetY * 256f);

            writer.WriteByte(0x07);  // BeginStream

            writer.WriteByte(0x35);  // ComponentUpdate
            writer.WriteUInt16((ushort)behaviorId);
            writer.WriteByte(0x04);  // CreateAction1
            writer.WriteByte(0x01);  // MoveTo = 1
            writer.WriteByte(0x00);
            writer.WriteInt32(fx);
            writer.WriteInt32(fy);

            WriteResolvedEntitySynchInfo(writer, "MON-MOVE", "Monster", sync, true);

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
        public static byte[] BuildProcessUpdateComponent(ushort componentId, byte syncFlags, uint syncHPWire)
        {
            RejectRawAliveHPSuffix("PROCESS-UPDATE-COMPONENT", syncFlags);
            var writer = new LEWriter();
            writer.WriteByte(0x07);           // BeginStream
            writer.WriteByte(0x36);           // processUpdateComponent opcode
            writer.WriteUInt16(componentId);  // componentID (any valid component)
            WriteEntitySynchInfo(writer, "PROCESS-UPDATE-COMPONENT", "Unknown", 0, componentId, 0x00, syncFlags, syncHPWire, false);
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
        /// Send 0x64 to monster's BehaviorId to set bit0 at UnitBehavior+0x156.
        /// processUpdate type 0x64 reads 1 byte.
        /// If nonzero → sets bit0 → calls FollowClient → client starts sending 0x65 position updates.
        /// </summary>
        public static byte[] BuildEnableClientControl(uint behaviorId, bool enable, byte syncFlags, uint syncHPWire)
        {
            RejectRawAliveHPSuffix("ENABLE-CLIENT-CONTROL", syncFlags);
            var writer = new LEWriter();
            writer.WriteByte(0x07);  // BeginStream
            writer.WriteByte(0x35);  // ComponentUpdate
            writer.WriteUInt16((ushort)behaviorId);
            writer.WriteByte(0x64);  // UnitBehavior processUpdate type
            writer.WriteByte((byte)(enable ? 0x01 : 0x00));
            WriteEntitySynchInfo(writer, "ENABLE-CLIENT-CONTROL", "Unknown", 0, behaviorId, 0x64, syncFlags, syncHPWire, false);
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
        public static byte[] BuildMonsterAttackPacket(uint monsterEntityId, uint monsterBehaviorId, ushort targetPlayerId, byte useFlags, ResolvedEntitySynchInfo sync, bool useTargetAction)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x07);  // BeginStream

            writer.WriteByte(0x35);              // ComponentUpdate
            writer.WriteUInt16((ushort)monsterBehaviorId);
            writer.WriteByte(0x04);              // CreateAction
            writer.WriteByte(useTargetAction ? (byte)0x50 : (byte)0xF0);
            writer.WriteByte(0x00);
            if (useTargetAction)
                writer.WriteByte(useFlags);
            writer.WriteUInt16(targetPlayerId);  // target
            WriteResolvedEntitySynchInfo(writer, "MON-ATTACK", "Monster", sync, true);

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
