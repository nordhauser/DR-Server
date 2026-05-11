using DungeonRunners.Utilities;
using UnityEngine;
using DungeonRunners.Data;
using DungeonRunners.Managers;
using System;
using System.Collections.Generic;

namespace DungeonRunners.Networking
{
    /// <summary>
    /// Handles equipment operations - matches Go server pattern
    /// CRITICAL: Must use BeginStream/EndStream (0x07/0x06)!
    /// CRITICAL: Must use REAL component IDs from tracking, not hardcoded!
    /// CRITICAL: Must have WriteSynch after each component update!
    /// </summary>
    public class EquipmentHandler
    {
        private UnityGameServer _server;

        public EquipmentHandler(UnityGameServer server)
        {
            _server = server;
        }

        public void HandleEquipmentUpdate(RRConnection conn, LEReader reader, ushort componentId, byte subMessage)
        {
            Debug.LogError($"[EQUIPMENT] Equipment update: componentId=0x{componentId:X4}, subMessage=0x{subMessage:X2}");

            switch (subMessage)
            {
                case 0x21:
                    Debug.LogError($"[EQUIPMENT] 0x21 - Query, remaining: {reader.Remaining}");
                    List<byte> bytes21 = new List<byte>();
                    while (reader.Remaining > 0)
                    {
                        byte b = reader.ReadByte();
                        bytes21.Add(b);
                    }
                    if (bytes21.Count > 0)
                        Debug.LogError($"[EQUIPMENT] 0x21 data: {BitConverter.ToString(bytes21.ToArray())}");
                    break;

                case 0x22:
                    Debug.LogError($"[EQUIPMENT] 0x22 - Position update, remaining: {reader.Remaining}");
                    List<byte> bytes22 = new List<byte>();
                    while (reader.Remaining > 0)
                    {
                        byte b = reader.ReadByte();
                        bytes22.Add(b);
                    }
                    if (bytes22.Count > 0)
                        Debug.LogError($"[EQUIPMENT] 0x22 data: {BitConverter.ToString(bytes22.ToArray())}");
                    break;

                case 0x28: // Equip item (add to equipment)
                    HandleAddEquippedItem(conn, reader, componentId);
                    break;

                case 0x29: // Unequip item (remove from equipment)
                    HandleRemoveEquippedItem(conn, reader, componentId);
                    break;

                default:
                    Debug.LogError($"[EQUIPMENT] Unknown Equipment subMessage: 0x{subMessage:X2}");
                    break;
            }
        }

        private void HandleRemoveEquippedItem(RRConnection conn, LEReader reader, ushort componentId)
        {
            uint slot = reader.ReadUInt32();

            Debug.LogError($"[EQUIPMENT] UNEQUIP ITEM - Slot: 0x{slot:X2}");

            PlayerState playerState = _server.GetPlayerState(conn.ConnId.ToString());

            GCObject equippedItem = GetEquippedItemAtSlot(conn, slot);

            if (equippedItem == null)
            {
                Debug.LogError($"[EQUIPMENT] ❌ No item equipped at slot {slot}!");
                return;
            }

            Debug.LogError($"[EQUIPMENT] Found item: {equippedItem.GCClass}");

            // ═══════════════════════════════════════════════════════════════
            // MYTHIC FIX: Client keeps its HP when unequipping items!
            // Do NOT recalculate - send the SAME SynchHP value.
            // Recalculation happens AFTER sending packet for future syncs.
            // ═══════════════════════════════════════════════════════════════
            Debug.LogError($"[EQUIPMENT] SynchHP (keeping same): {playerState.SynchHP}");

            // Get REAL component IDs from tracking
            ushort unitContainerComponentId = GetUnitContainerComponentId(conn);
            ushort manipulatorsComponentId = GetManipulatorsComponentId(conn);

            Debug.LogError($"[EQUIPMENT] Equipment ID: 0x{componentId:X4}");
            Debug.LogError($"[EQUIPMENT] UnitContainer ID: 0x{unitContainerComponentId:X4}");
            Debug.LogError($"[EQUIPMENT] Manipulators ID: 0x{manipulatorsComponentId:X4}");

            var writer = new LEWriter();

            // CRITICAL: BeginStream required!
            writer.WriteByte(0x07);

            // ========================================
            // Part 1: Remove from Equipment
            // ========================================
            writer.WriteByte(0x35); // ComponentUpdate
            writer.WriteUInt16(componentId);
            writer.WriteByte(0x29); // Remove
            writer.WriteUInt32(slot);
            WriteSynch(writer, conn); // Go server has WriteSynch after each update

            // ========================================
            // Part 2: Set as active item in UnitContainer
            // Go server calls GetEquipment().WriteInit() which is Equipment.WriteInit()
            // NOT MeleeWeapon.WriteInit() - so NO weapon extra bytes!
            // ========================================
            writer.WriteByte(0x35); // ComponentUpdate
            writer.WriteUInt16(unitContainerComponentId);
            writer.WriteByte(0x28); // Set active item
            equippedItem.WriteInitWithoutWeaponBytes(writer, playerState.Level);
            WriteSynch(writer, conn);

            // ========================================
            // Part 3: Remove from Manipulators visual
            // Go server uses 0x01 for remove!
            // ========================================
            writer.WriteByte(0x35); // ComponentUpdate
            writer.WriteUInt16(manipulatorsComponentId);
            writer.WriteByte(0x01); // Remove item (Go uses 0x01, old code used 0x1F)
            writer.WriteUInt32(slot);
            WriteSynch(writer, conn);

            // CRITICAL: EndStream required!
            writer.WriteByte(0x06);

            byte[] packet = writer.ToArray();
            Debug.LogError($"[EQUIPMENT] Sending unequip packet, {packet.Length} bytes");
            Debug.LogError($"[EQUIPMENT] Packet hex: {BitConverter.ToString(packet)}");
            _server.SendToClient(conn, packet);

            // ═══════════════════════════════════════════════════════════════
            // AFTER sending packet: Remove from tracking and recalculate
            // ═══════════════════════════════════════════════════════════════
            _server.RemoveEquippedItem(conn.ConnId.ToString(), slot);
            _server.CalculateEquipmentBonuses(conn.ConnId.ToString(), conn.Avatar);
            Debug.LogError($"[EQUIPMENT] HP after tracking update: {playerState.SynchHP}");

            playerState.ActiveItem = equippedItem;

            Debug.LogError($"[EQUIPMENT] ✅ Item unequipped from slot {slot}, now in hand");
        }

        private void HandleAddEquippedItem(RRConnection conn, LEReader reader, ushort componentId)
        {
            uint slot = reader.ReadUInt32();
            Debug.LogError($"[EQUIPMENT] EQUIP ITEM - Client requested slot: 0x{slot:X2}");

            PlayerState playerState = _server.GetPlayerState(conn.ConnId.ToString());
            GCObject item = playerState.ActiveItem;

            if (item == null)
            {
                Debug.LogError($"[EQUIPMENT] ❌ No active item to equip!");
                return;
            }

            // Quest items are inventory-only — never equippable
            if (item.GCClass.StartsWith("QuestItemPAL", StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogError($"[EQUIPMENT] ❌ REJECTED quest item: {item.GCClass} — not equippable");
                return;
            }

            // 🔥 FIX: Validate that item belongs in the requested slot
            // Clear TargetSlot first — it's runtime state, not the item's base type
            // Without this, a weapon previously in off-hand (TargetSlot=11) can't go back to primary
            item.TargetSlot = null;
            uint correctSlot = item.GetEquipmentSlotFromGCClass();
            if (slot != correctSlot)
            {
                // Allow 1H weapons in off-hand slot (11) for dual-wielding
                bool is1HWeapon = (correctSlot == 10) && !item.GCClass.ToLowerInvariant().Contains("2h");
                if (is1HWeapon && slot == 11)
                {
                    Debug.LogError($"[EQUIPMENT] ✅ Dual-wield: 1H weapon {item.GCClass} → off-hand slot 11");
                }
                // Allow rings in either ring slot (3 or 4)
                else if ((correctSlot == 3 || correctSlot == 4) && (slot == 3 || slot == 4))
                {
                    Debug.LogError($"[EQUIPMENT] ✅ Ring slot override: {item.GCClass} → slot {slot}");
                }
                else
                {
                    Debug.LogError($"[EQUIPMENT] ❌ REJECTED - {item.GCClass} belongs in slot {correctSlot}, not slot {slot}! Item stays in hand.");
                    return;
                }
            }
            Debug.LogError($"[EQUIPMENT] ✅ Slot {slot} is valid for {item.GCClass}");

            // 🔥 LEVEL CHECK: Prevent equipping items above player's level
            // Uses stored level (fixed at creation) + stored rarity + level deltas from GlobalKnobs.gc
            // Per GC files: ScaleToObserverLevel is consumables only — ALL equipment has fixed levels
            // Level deltas configurable in server.cfg: itemLevelDeltaNormal, itemLevelDeltaMythic, etc.
            // Toggle: enableEquipLevelCheck = true/false in server.cfg
            {
                bool levelCheckEnabled = DungeonRunners.Core.ServerSettings.GetBool("enableEquipLevelCheck", true);

                if (levelCheckEnabled)
                {
                    int itemLevel = item.StoredLevel >= 0 ? item.StoredLevel : RarityHelper.GetItemLevel(item.GCClass);
                    int requiredLevel = Math.Max(0, Math.Min(100, itemLevel - 5));

                    if (playerState.Level < requiredLevel)
                    {
                        Debug.LogError($"[EQUIPMENT] ❌ LEVEL TOO LOW - {item.GCClass} requires level {requiredLevel} (storedLevel={item.StoredLevel}), player is level {playerState.Level}. Item stays in hand.");
                        return;
                    }
                    Debug.LogError($"[EQUIPMENT] ✅ Level check passed: player lv{playerState.Level} >= required lv{requiredLevel} (storedLevel={item.StoredLevel})");
                }
                else
                {
                    Debug.LogError($"[EQUIPMENT] ✅ Level check DISABLED via server.cfg");
                }
            }

            // 🔥 2H/SHIELD/DUAL-WIELD CONFLICT
            {
                string gcLow = item.GCClass.ToLowerInvariant();
                bool is2HWeapon = gcLow.Contains("2h");
                bool isShield = gcLow.Contains("shield") || gcLow.Contains("buckler");

                if (is2HWeapon && slot == 10)
                {
                    // Trying to equip a 2H weapon — check if shield or off-hand weapon is equipped
                    GCObject equippedOffhand = GetEquippedItemAtSlot(conn, 11);
                    if (equippedOffhand != null)
                    {
                        Debug.LogError($"[EQUIPMENT] ❌ BLOCKED - Cannot equip 2H weapon {item.GCClass} while off-hand {equippedOffhand.GCClass} is equipped. Unequip off-hand first.");
                        return;
                    }
                }
                else if (slot == 11)
                {
                    // Trying to equip shield or off-hand weapon — check if main weapon is 2H
                    GCObject equippedWeapon = GetEquippedItemAtSlot(conn, 10);
                    if (equippedWeapon != null && equippedWeapon.GCClass.ToLowerInvariant().Contains("2h"))
                    {
                        Debug.LogError($"[EQUIPMENT] ❌ BLOCKED - Cannot equip off-hand {item.GCClass} while 2H weapon {equippedWeapon.GCClass} is equipped. Unequip weapon first.");
                        return;
                    }
                }
            }

            // ═══════════════════════════════════════════════════════════════
            // DUAL-WIELD: Set TargetSlot for 1H weapons going to off-hand
            // This ensures WriteInitWithoutWeaponBytes sends slot 11 in packets
            // ═══════════════════════════════════════════════════════════════
            uint itemCorrectSlot = item.GetEquipmentSlotFromGCClass();
            if (itemCorrectSlot == 10 && slot == 11)
            {
                item.TargetSlot = 11;
                Debug.LogError($"[EQUIPMENT] Set TargetSlot=11 for dual-wield off-hand: {item.GCClass}");
            }
            else if (itemCorrectSlot == 10 && slot == 10)
            {
                item.TargetSlot = null;
                Debug.LogError($"[EQUIPMENT] Cleared TargetSlot for primary weapon: {item.GCClass}");
            }

            // Check if slot already has an item
            GCObject existingItem = GetEquippedItemAtSlot(conn, slot);
            Debug.LogError($"[EQUIPMENT] Equipping item: {item.GCClass}");

            if (existingItem != null)
            {
                Debug.LogError($"[EQUIPMENT] Slot {slot} has {existingItem.GCClass} - will SWAP");
            }

            // ═══════════════════════════════════════════════════════════════
            // MYTHIC FIX: Track item and recalculate BEFORE packet!
            // Client adds bonus then checks sync, so we must send NEW value.
            // ═══════════════════════════════════════════════════════════════
            _server.TrackEquippedItem(conn.ConnId.ToString(), slot, item);
            _server.CalculateEquipmentBonuses(conn.ConnId.ToString(), conn.Avatar);
            Debug.LogError($"[EQUIPMENT] SynchHP after recalc: {playerState.SynchHP}");

            ushort unitContainerComponentId = GetUnitContainerComponentId(conn);
            ushort manipulatorsComponentId = GetManipulatorsComponentId(conn);

            var writer = new LEWriter();
            writer.WriteByte(0x07);

            if (existingItem != null)
            {
                // SWAP: Remove old from Equipment first
                writer.WriteByte(0x35);
                writer.WriteUInt16(componentId);
                writer.WriteByte(0x29); // Remove
                writer.WriteUInt32(slot);
                WriteSynch(writer, conn);

                // SWAP: Remove old from Manipulators
                writer.WriteByte(0x35);
                writer.WriteUInt16(manipulatorsComponentId);
                writer.WriteByte(0x01); // Remove
                writer.WriteUInt32(slot);
                WriteSynch(writer, conn);
            }

            // Add new item to equipment
            writer.WriteByte(0x35);
            writer.WriteUInt16(componentId);
            writer.WriteByte(0x28);
            item.WriteInitWithoutWeaponBytes(writer, playerState.Level);
            WriteSynch(writer, conn);

            // UnitContainer: swap puts old in hand, otherwise clear
            writer.WriteByte(0x35);
            writer.WriteUInt16(unitContainerComponentId);
            if (existingItem != null)
            {
                writer.WriteByte(0x28); // SetActiveItem - old item goes to hand
                existingItem.WriteInitWithoutWeaponBytes(writer, playerState.Level);
            }
            else
            {
                writer.WriteByte(0x29); // ClearActiveItem
            }
            WriteSynch(writer, conn);

            // Add new to manipulators visual
            writer.WriteByte(0x35);
            writer.WriteUInt16(manipulatorsComponentId);
            writer.WriteByte(0x00);
            item.WriteInitWithoutWeaponBytes(writer, playerState.Level);
            WriteSynch(writer, conn);

            writer.WriteByte(0x06);

            byte[] packet = writer.ToArray();
            Debug.LogError($"[EQUIPMENT] Sending equip packet, {packet.Length} bytes");
            _server.SendToClient(conn, packet);

            if (existingItem != null)
            {
                playerState.ActiveItem = existingItem;
                Debug.LogError($"[EQUIPMENT] ✅ SWAPPED - {item.GCClass} equipped, {existingItem.GCClass} now in hand");
            }
            else
            {
                playerState.ActiveItem = null;
                Debug.LogError($"[EQUIPMENT] ✅ Item equipped to slot {slot}");
            }

            Debug.LogError($"[EQUIPMENT] Saved to DB after equip");
        }

        /// <summary>
        /// WriteSynch - Go server pattern
        /// </summary>
        /// <summary>
        /// WriteSynch - Go server pattern
        /// </summary>
        private void WriteSynch(LEWriter writer, RRConnection conn)
        {
            _server.WritePlayerEntitySynch(conn, writer);
        }

        private GCObject GetEquippedItemAtSlot(RRConnection conn, uint slot)
        {
            return _server.GetEquippedItem(conn.ConnId.ToString(), slot);
        }

        private ushort GetUnitContainerComponentId(RRConnection conn)
        {
            return _server.GetUnitContainerComponentId(conn.ConnId.ToString());
        }

        private ushort GetManipulatorsComponentId(RRConnection conn)
        {
            return _server.GetManipulatorsComponentId(conn.ConnId.ToString());
        }
    }
}
