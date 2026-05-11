using DungeonRunners.Utilities;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using DungeonRunners.Data;
using System;

namespace DungeonRunners.Networking
{
    public class InventoryHandler
    {
        private readonly UnityGameServer _server;

        public InventoryHandler(UnityGameServer server)
        {
            _server = server;
        }

        private Dictionary<string, uint> _activeHealthModId = new Dictionary<string, uint>();
        private Dictionary<string, uint> _activeManaModId = new Dictionary<string, uint>();
        private Dictionary<string, uint> _activeBuffModId = new Dictionary<string, uint>();  // key = connId:buffType
        private Dictionary<string, float> _healthPotionCooldown = new Dictionary<string, float>();
        private Dictionary<string, float> _manaPotionCooldown = new Dictionary<string, float>();
        private uint _nextPotionModId = 50000;

        private bool IsPotionOnCooldown(string connId, bool isHealth)
        {
            var dict = isHealth ? _healthPotionCooldown : _manaPotionCooldown;
            if (dict.ContainsKey(connId))
            {
                float elapsed = Time.time - dict[connId];
                if (elapsed < 1.0f)
                    return true;
            }
            return false;
        }

        private void StartPotionCooldown(string connId, bool isHealth)
        {
            var dict = isHealth ? _healthPotionCooldown : _manaPotionCooldown;
            dict[connId] = Time.time;
        }

        public void HandleUnitContainerUpdate(RRConnection conn, LEReader reader, ushort componentId, byte subMessage)
        {
            Debug.LogError($"[INVENTORY] UnitContainer update: componentId=0x{componentId:X4}, subMessage=0x{subMessage:X2}");
            switch (subMessage)
            {
                case 0x21:
                    HandleInventoryQuery(conn, reader, componentId);
                    break;
                case 0x22:
                    HandleItemPositionUpdate(conn, reader, componentId);
                    break;
                case 0x23:
                    HandleDropItem(conn, reader, componentId);
                    break;
                case 0x25:
                    HandleUseItem(conn, reader, componentId);
                    break;
                case 0x26:
                    HandleUseItemPosition(conn, reader, componentId);
                    break;
                case 0x27:
                    HandleUseItemTarget(conn, reader, componentId);
                    break;
                case 0x28:
                    HandlePickupItemFromInventory(conn, reader, componentId);
                    break;
                case 0x29:
                    HandlePlaceItemInInventory(conn, reader, componentId);
                    break;
                default:
                    Debug.LogError($"[INVENTORY] Unknown UnitContainer subMessage: 0x{subMessage:X2}");
                    break;
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // HOTBAR USE (0x25) — spacebar / number keys / quick-use clicks
        // ═══════════════════════════════════════════════════════════════════
        private void HandleUseItem(RRConnection conn, LEReader reader, ushort componentId)
        {
            Debug.LogError($"[USE-ITEM] ═══════════════════════════════════════════════════");

            uint clientItemId = reader.ReadUInt32();
            Debug.LogError($"[USE-ITEM] Client item ID: {clientItemId}");

            string connId = conn.ConnId.ToString();
            PlayerState playerState = _server.GetPlayerState(connId);

            // Try direct slot lookup first
            uint actualSlot = clientItemId;
            var itemData = _server.GetInventoryItemBySlot(connId, clientItemId);

            // Quick-use bar sends client entity IDs offset from server slots
            if (itemData == null)
            {
                var allItems = _server.GetAllInventoryItems(connId);
                if (allItems != null && allItems.Count > 0)
                {
                    uint minServerSlot = uint.MaxValue;
                    foreach (var key in allItems.Keys)
                        if (key < minServerSlot) minServerSlot = key;

                    if (clientItemId > minServerSlot)
                    {
                        uint offset = clientItemId - minServerSlot;
                        actualSlot = clientItemId - offset;
                        itemData = _server.GetInventoryItemBySlot(connId, actualSlot);
                        Debug.LogError($"[USE-ITEM] Offset={offset}, trying slot {actualSlot}");
                    }

                    if (itemData == null)
                    {
                        var sortedSlots = new List<uint>(allItems.Keys);
                        sortedSlots.Sort();

                        if (sortedSlots.Count > 0)
                        {
                            uint guessBase = clientItemId - (uint)(sortedSlots.Count - 1);
                            int itemIndex = (int)(clientItemId - guessBase);
                            if (itemIndex >= 0 && itemIndex < sortedSlots.Count)
                            {
                                actualSlot = sortedSlots[itemIndex];
                                itemData = _server.GetInventoryItemBySlot(connId, actualSlot);
                                Debug.LogError($"[USE-ITEM] Index lookup: idx={itemIndex}, slot={actualSlot}");
                            }
                        }
                    }

                    if (itemData == null)
                    {
                        foreach (var kvp in allItems)
                            Debug.LogError($"[USE-ITEM] Available: slot {kvp.Key} = {kvp.Value.item.GCClass}");
                    }
                }
            }

            if (itemData == null)
            {
                Debug.LogError($"[USE-ITEM] No item found for client ID {clientItemId}");
                return;
            }

            GCObject item = itemData.Value.item;
            byte itemX = itemData.Value.x;
            byte itemY = itemData.Value.y;
            string gcLower = item.GCClass.ToLower();

            Debug.LogError($"[USE-ITEM] Using: {item.GCClass} at ({itemX},{itemY}) slot={actualSlot}");

            // Cooldown check — before consuming the item
            if (gcLower.Contains("healthpotion") && IsPotionOnCooldown(connId, true))
            {
                Debug.LogError("[HEALTH-POTION] On cooldown, ignoring");
                return;
            }
            if (gcLower.Contains("manapotion") && IsPotionOnCooldown(connId, false))
            {
                Debug.LogError("[MANA-POTION] On cooldown, ignoring");
                return;
            }

            if (gcLower.Contains("townportal"))
            {
                int tpCount = _server.GetStackCount(connId, actualSlot);
                int remaining = tpCount - 1;
                if (remaining > 0)
                    _server.SetStackCount(connId, actualSlot, remaining);
                else
                {
                    _server.FreeInventorySlots(connId, itemX, itemY, 1, 1);
                    _server.RemoveInventoryItemBySlot(connId, actualSlot);
                }
                _server.SpawnTownPortalWithRemoval(conn, "tutorial", componentId, clientItemId,
                    playerState, gcLower, itemX, itemY, remaining);
                return;
            }

            // ═══════════════════════════════════════════════════════════════
            // SKILL BOOKS — right-click teaches the skill in the book.
            // From SkillBookPAL.gc: each book has Effect { SkillToLearn = X; }
            // The book is single-use even if Stackable (stack count decrements
            // per use). When stack reaches 0 the slot is freed.
            //
            // We map the book's gc_class to the skill it teaches. Currently
            // only SummonBlingGnome is supported — extend this map as more
            // skill books are implemented.
            //
            // Unknown-message-type(0) crash before this fix: the book fell
            // through to the generic potion-style decrement which wrote a
            // 0x1E ItemAdd with potion-specific byte layout (flags + mod
            // count) that the client interpreted as a fresh message stream,
            // ending on a 0x00 that became "message type 0".
            // ═══════════════════════════════════════════════════════════════
            if (gcLower.Contains("skillbook"))
            {
                string skillToLearn = null;
                // Map gc_class -> skill. SkillBookPAL.SummonBlingGnome teaches skills.generic.SummonBlingGnome
                if (gcLower.Contains("summonblinggnome"))
                    skillToLearn = "skills.generic.SummonBlingGnome";
                // (Future: map other skill books here)

                Debug.LogError($"[SKILLBOOK] Consumed '{item.GCClass}' -> teaches '{skillToLearn}'");

                // Consume the book from inventory (always single-use per click,
                // even from a stack — matches potion behavior)
                int bookStack = _server.GetStackCount(connId, actualSlot);
                if (bookStack > 1)
                {
                    _server.SetStackCount(connId, actualSlot, bookStack - 1);
                }
                else
                {
                    _server.FreeInventorySlots(connId, itemX, itemY, 1, 1);
                    _server.RemoveInventoryItemBySlot(connId, actualSlot);
                }

                // Send ONLY the 0x1F ItemRemoved packet — NO 0x1E re-add.
                // Client auto-decrements stack based on 0x1F when stack > 0
                // was already known, but for simplicity we always remove and
                // (for stacks > 1) re-add via a fresh 0x1E with correct bytes.
                var skillBookWriter = new LEWriter();
                skillBookWriter.WriteByte(0x07);
                skillBookWriter.WriteByte(0x35);
                skillBookWriter.WriteUInt16(componentId);
                skillBookWriter.WriteByte(0x1F);                      // ItemRemoved
                skillBookWriter.WriteUInt32(clientItemId);
                _server.WritePlayerEntitySynch(conn, skillBookWriter);

                // If stack remaining > 0, re-add the item with updated count
                if (bookStack > 1)
                {
                    int newCount = bookStack - 1;
                    skillBookWriter.WriteByte(0x35);
                    skillBookWriter.WriteUInt16(componentId);
                    skillBookWriter.WriteByte(0x1E);                  // ItemAdd
                    skillBookWriter.WriteByte(0x0B);                  // inv id (main)
                    skillBookWriter.WriteByte(0xFF);
                    skillBookWriter.WriteCString(DungeonRunners.Data.GCObject.GetPacketGCClassFor(gcLower));
                    skillBookWriter.WriteUInt32(clientItemId);
                    skillBookWriter.WriteByte(itemX);
                    skillBookWriter.WriteByte(itemY);
                    skillBookWriter.WriteByte((byte)newCount);
                    skillBookWriter.WriteByte(0x01);                  // level
                    skillBookWriter.WriteByte(0x00);                  // flags (simple item)
                    skillBookWriter.WriteByte(0x00);                  // ReadChildData<ItemModifier> count = 0
                    _server.WritePlayerEntitySynch(conn, skillBookWriter);
                }

                skillBookWriter.WriteByte(0x06);
                _server.SendToClient(conn, skillBookWriter.ToArray());

                // Teach the skill via runtime hot-add (no logout required).
                // Uses the same pattern as the skill trainer: persists to DB,
                // adds to Manipulators, sends 0x32 Skills hot-add packet +
                // 0xDF DLL UI-refresh packet. Skill appears in book immediately.
                if (skillToLearn != null)
                {
                    bool granted = _server.GrantSkillRuntime(conn, skillToLearn);
                    string shortName = skillToLearn.Substring(skillToLearn.LastIndexOf('.') + 1);
                    _server.SendSystemMessage(conn, granted
                        ? $"[Skill Book] You learned {shortName}!"
                        : $"[Skill Book] You already know {shortName}, but the book vanishes anyway.");
                }
                return;
            }

            // Stack decrement — keep same slot, use clientItemId for all client packets
            int stackCount = _server.GetStackCount(connId, actualSlot);

            if (stackCount > 1)
            {
                int newCount = stackCount - 1;
                _server.SetStackCount(connId, actualSlot, newCount);

                var useWriter = new LEWriter();
                useWriter.WriteByte(0x07);

                useWriter.WriteByte(0x35);
                useWriter.WriteUInt16(componentId);
                useWriter.WriteByte(0x1F);
                useWriter.WriteUInt32(clientItemId);
                _server.WritePlayerEntitySynch(conn, useWriter);

                useWriter.WriteByte(0x35);
                useWriter.WriteUInt16(componentId);
                useWriter.WriteByte(0x1E);
                useWriter.WriteByte(0x0B);
                useWriter.WriteByte(0xFF);
                useWriter.WriteCString(DungeonRunners.Data.GCObject.GetPacketGCClassFor(gcLower));
                useWriter.WriteUInt32(clientItemId);
                useWriter.WriteByte(itemX);
                useWriter.WriteByte(itemY);
                useWriter.WriteByte((byte)newCount);
                useWriter.WriteByte(0x01);
                useWriter.WriteByte(0x00);
                if (gcLower.Contains("dragonjuice") || gcLower.Contains("intbuff"))
                    useWriter.WriteByte(0x00);  // transient Mod1 flags
                useWriter.WriteByte(0x00);
                _server.WritePlayerEntitySynch(conn, useWriter);

                useWriter.WriteByte(0x06);
                _server.SendToClient(conn, useWriter.ToArray());
            }
            else
            {
                _server.FreeInventorySlots(connId, itemX, itemY, 1, 1);
                _server.RemoveInventoryItemBySlot(connId, actualSlot);

                var useWriter = new LEWriter();
                useWriter.WriteByte(0x07);
                useWriter.WriteByte(0x35);
                useWriter.WriteUInt16(componentId);
                useWriter.WriteByte(0x1F);
                useWriter.WriteUInt32(clientItemId);
                _server.WritePlayerEntitySynch(conn, useWriter);
                useWriter.WriteByte(0x06);
                _server.SendToClient(conn, useWriter.ToArray());
            }

            // Potion effects
            if (gcLower.Contains("healthpotion"))
            {
                uint healAmount = 50 * 256;
                uint maxHP = playerState.MaxHPWire;
                playerState.AvatarHP = Math.Min(playerState.AvatarHP + healAmount, maxHP);
                Debug.LogError($"[HEALTH-POTION] Healed! HP: {playerState.AvatarHP / 256} / {maxHP / 256}");
                SendHPUpdate(conn, componentId, playerState);
                SendPotionModifier(conn, playerState, "PotionPAL.HealthPotion_Noob.Modifier");
                StartPotionCooldown(connId, true);
            }
            else if (gcLower.Contains("manapotion"))
            {
                uint manaAmount = 50 * 256;
                uint maxMana = playerState.MaxManaWire;
                playerState.SetCurrentMana(Math.Min(playerState.CurrentManaWire + manaAmount, maxMana));
                Debug.LogError($"[MANA-POTION] Restored! Mana: {playerState.CurrentManaWire / 256} / {maxMana / 256}");
                SendManaUpdate(conn, componentId, playerState);
                SendPotionModifier(conn, playerState, "PotionPAL.ManaPotion_Noob.Modifier");
                StartPotionCooldown(connId, false);
            }
            else if (gcLower.Contains("dragonjuice"))
            {
                string modPath = gcLower + ".modifier";
                float dur = gcLower.Contains("_lg") ? 120.0f : 60.0f;
                Debug.LogError($"[DRAGONJUICE] Buff applied! (+STR -END {dur}s) modifier={modPath}");
                SendBuffModifier(conn, playerState, modPath, dur);
            }
            else if (gcLower.Contains("intbuff"))
            {
                string modPath = gcLower + ".modifier";
                float dur = gcLower.Contains("_lg") ? 180.0f : 60.0f;
                Debug.LogError($"[INTBUFF] Buff applied! (+INT {dur}s) modifier={modPath}");
                SendBuffModifier(conn, playerState, modPath, dur);
            }

            Debug.LogError($"[USE-ITEM] Item consumed!");
            Debug.LogError($"[USE-ITEM] ═══════════════════════════════════════════════════");
        }

        private void HandleUseItemPosition(RRConnection conn, LEReader reader, ushort componentId)
        {
            Debug.LogError($"[USE-ITEM-POS] 0x26 received, remaining: {reader.Remaining}");
            if (reader.Remaining >= 4)
            {
                // 0x26 sends the same item ID as 0x25, just with possible position data after
                // Route to the same use handler
                HandleUseItem(conn, reader, componentId);
            }
            else
            {
                Debug.LogError($"[USE-ITEM-POS] Not enough data");
            }
        }

        private void HandleUseItemTarget(RRConnection conn, LEReader reader, ushort componentId)
        {
            Debug.LogError($"[USE-ITEM-TARGET] 0x27 received, remaining: {reader.Remaining}");
            if (reader.Remaining > 0)
            {
                byte[] rawData = reader.PeekRemaining();
                Debug.LogError($"[USE-ITEM-TARGET] RAW BYTES ({rawData.Length}): {BitConverter.ToString(rawData)}");
            }
        }

        private void HandleInventoryQuery(RRConnection conn, LEReader reader, ushort componentId)
        {
            Debug.LogError($"[INVENTORY] 0x21 - Inventory query, remaining: {reader.Remaining}");
            var bytes = new List<byte>();
            while (reader.Remaining > 0)
                bytes.Add(reader.ReadByte());
            Debug.LogError($"[INVENTORY] 0x21 data: {BitConverter.ToString(bytes.ToArray())}");
        }

        private void HandleItemPositionUpdate(RRConnection conn, LEReader reader, ushort componentId)
        {
            Debug.LogError($"[INVENTORY] 0x22 - Item position update, remaining: {reader.Remaining}");
            var bytes = new List<byte>();
            while (reader.Remaining > 0)
                bytes.Add(reader.ReadByte());
            Debug.LogError($"[INVENTORY] 0x22 data: {BitConverter.ToString(bytes.ToArray())}");
        }

        private void HandlePlaceItemInInventory(RRConnection conn, LEReader reader, ushort componentId)
        {
            byte inventoryID = reader.ReadByte();
            byte x = reader.ReadByte();
            byte y = reader.ReadByte();
            Debug.LogError($"[INVENTORY] PLACE ITEM - Inventory: 0x{inventoryID:X2}, Pos: ({x}, {y})");

            PlayerState playerState = _server.GetPlayerState(conn.ConnId.ToString());
            GCObject item = playerState.ActiveItem;

            if (item == null)
            {
                Debug.LogError("[INVENTORY] No active item to place!");
                return;
            }

            ItemData itemData = DatabaseLoader.FindItem(item.GCClass);
            int itemWidth = itemData?.inventoryWidth ?? 1;
            int itemHeight = itemData?.inventoryHeight ?? 1;

            if (_server.IsInventorySlotOccupied(conn.ConnId.ToString(), x, y, itemWidth, itemHeight))
            {
                Debug.LogError($"[INVENTORY] ❌ Cannot place {itemWidth}x{itemHeight} item at ({x}, {y}) - overlaps!");
                return;
            }

            uint trackingSlot = _server.GetNextInventorySlot(conn.ConnId.ToString());

            var writer = new LEWriter();
            writer.WriteByte(0x07);

            writer.WriteByte(0x35);
            writer.WriteUInt16(componentId);
            writer.WriteByte(0x29);
            _server.WritePlayerEntitySynch(conn, writer);

            writer.WriteByte(0x35);
            writer.WriteUInt16(componentId);
            writer.WriteByte(0x1E);
            writer.WriteByte(inventoryID);

            string gcCheck = item.GCClass.ToLower();
            if (gcCheck.Contains("questitem") || gcCheck.Contains("consumable") || gcCheck.Contains("potion") || gcCheck.Contains("townportal") || gcCheck.Contains("scroll") || gcCheck.Contains("skillbook") || gcCheck.Contains("voucher"))
            {
                // Restore stack count from temp cursor storage
                string connId = conn.ConnId.ToString();
                int stackCount = _server.GetStackCount(connId, 0xFFFFFFFF);
                if (stackCount <= 0) stackCount = 1;

                // Simple items: bare readData format, no ScaleMod
                writer.WriteByte(0xFF);
                writer.WriteCString(DungeonRunners.Data.GCObject.GetPacketGCClassFor(gcCheck));
                writer.WriteUInt32(trackingSlot);
                writer.WriteByte(x);
                writer.WriteByte(y);
                writer.WriteByte((byte)stackCount);
                writer.WriteByte(0x01);  // level
                writer.WriteByte(0x00);  // flags
                // Binary RE: 0x583920 walks transient children before count byte.
                // DragonJuice/IntBuff have transient Mod1 (ItemAttributeModifier)
                // whose readData@0x588AE0 reads 1 byte (flags).
                if (gcCheck.Contains("dragonjuice") || gcCheck.Contains("intbuff"))
                    writer.WriteByte(0x00);  // transient Mod1 flags
                writer.WriteByte(0x00);  // ReadChildData<ItemModifier> count = 0

                // Restore stack tracking after write
                _server.SetStackCount(connId, trackingSlot, stackCount);
                _server.SetStackCount(connId, 0xFFFFFFFF, 0);  // clear temp
            }
            else
            {
                int itemLevel = item.GetItemRequiredLevel();
                item.WriteInitForInventory(writer, x, y, trackingSlot, itemLevel);
            }
            _server.WritePlayerEntitySynch(conn, writer);

            writer.WriteByte(0x06);

            _server.SendToClient(conn, writer.ToArray());
            _server.OccupyInventorySlots(conn.ConnId.ToString(), x, y, itemWidth, itemHeight);
            _server.TrackInventoryItem(conn.ConnId.ToString(), trackingSlot, item, x, y);

            playerState.ActiveItem = null;
            _server.SavePlayerInventoryPublic(conn);
            Debug.LogError($"[INVENTORY] ✅ Item placed at ({x}, {y})");
        }

        // ═══════════════════════════════════════════════════════════════════
        // INVENTORY CLICK (0x28) — clicking item in bag
        // ═══════════════════════════════════════════════════════════════════
        private void HandlePickupItemFromInventory(RRConnection conn, LEReader reader, ushort componentId)
        {
            uint index = reader.ReadUInt32();
            Debug.LogError($"[INVENTORY] PICKUP ITEM - Index: {index}");
            string connId = conn.ConnId.ToString();
            PlayerState playerState = _server.GetPlayerState(connId);

            if (playerState.ActiveItem != null)
            {
                Debug.LogError($"[INVENTORY] ❌ Already holding item: {playerState.ActiveItem.GCClass}");
                return;
            }

            var itemData = _server.GetAndRemoveInventoryItem(connId, index);
            if (itemData == null)
            {
                Debug.LogError($"[INVENTORY] ❌ No item at index {index}!");
                return;
            }

            GCObject item = itemData.Value.item;
            byte storedX = itemData.Value.x;
            byte storedY = itemData.Value.y;

            ItemData dbItem = DatabaseLoader.FindItem(item.GCClass);
            int itemWidth = dbItem?.inventoryWidth ?? 1;
            int itemHeight = dbItem?.inventoryHeight ?? 1;

            string gcLower = item.GCClass.ToLower();

            // ── QUEST ITEMS: pick up to cursor with bare format (no ScaleMod) ──
            if (gcLower.Contains("questitem"))
            {
                _server.FreeInventorySlots(connId, storedX, storedY, itemWidth, itemHeight);

                var questWriter = new LEWriter();
                questWriter.WriteByte(0x07);

                questWriter.WriteByte(0x35);
                questWriter.WriteUInt16(componentId);
                questWriter.WriteByte(0x1F);
                questWriter.WriteUInt32(index);
                _server.WritePlayerEntitySynch(conn, questWriter);

                questWriter.WriteByte(0x35);
                questWriter.WriteUInt16(componentId);
                questWriter.WriteByte(0x28);
                questWriter.WriteByte(0xFF);
                questWriter.WriteCString(DungeonRunners.Data.GCObject.GetPacketGCClassFor(gcLower));
                questWriter.WriteUInt32(0x00);
                questWriter.WriteByte(0x00);
                questWriter.WriteByte(0x00);
                questWriter.WriteByte(0x01);
                questWriter.WriteByte(0x01);
                questWriter.WriteByte(0x00);
                questWriter.WriteByte(0x00);
                _server.WritePlayerEntitySynch(conn, questWriter);

                questWriter.WriteByte(0x06);
                _server.SendToClient(conn, questWriter.ToArray());

                playerState.ActiveItem = item;
                Debug.LogError($"[INVENTORY] ✅ Quest item picked up to cursor: {item.GCClass}");
                return;
            }

            if (gcLower.Contains("townportal"))
            {
                int tpCount = _server.GetStackCount(connId, index);
                int remaining = tpCount - 1;
                if (remaining > 0)
                {
                    var tpItem = new GCObject { GCClass = item.GCClass, NativeClass = "Item" };
                    _server.TrackInventoryItem(connId, index, tpItem, storedX, storedY);
                    _server.SetStackCount(connId, index, remaining);
                }
                else
                {
                    _server.FreeInventorySlots(connId, storedX, storedY, itemWidth, itemHeight);
                    _server.RemoveInventoryItemBySlot(connId, index);
                }
                _server.SpawnTownPortalWithRemoval(conn, "dungeon00_level01", componentId, index,
                    playerState, gcLower, storedX, storedY, remaining);
                return;
            }

            // ── POTIONS / SKILLBOOKS / VOUCHERS: pick up to cursor for moving ──
            // Right-click / hotbar sends 0x25 (HandleUseItem) for actual consumption
            // Left-click sends 0x28 which picks up to cursor — simple 9-byte format
            if (gcLower.Contains("consumable") || gcLower.Contains("potion")
                || gcLower.Contains("skillbook") || gcLower.Contains("voucher"))
            {
                int stackCount = _server.GetStackCount(connId, index);
                _server.FreeInventorySlots(connId, storedX, storedY, itemWidth, itemHeight);

                var consWriter = new LEWriter();
                consWriter.WriteByte(0x07);

                consWriter.WriteByte(0x35);
                consWriter.WriteUInt16(componentId);
                consWriter.WriteByte(0x1F);
                consWriter.WriteUInt32(index);
                _server.WritePlayerEntitySynch(conn, consWriter);

                consWriter.WriteByte(0x35);
                consWriter.WriteUInt16(componentId);
                consWriter.WriteByte(0x28);
                consWriter.WriteByte(0xFF);
                consWriter.WriteCString(DungeonRunners.Data.GCObject.GetPacketGCClassFor(gcLower));
                consWriter.WriteUInt32(0x00);
                consWriter.WriteByte(0x00);
                consWriter.WriteByte(0x00);
                consWriter.WriteByte((byte)(stackCount > 0 ? stackCount : 1));
                consWriter.WriteByte(0x01);
                consWriter.WriteByte(0x00);
                if (gcLower.Contains("dragonjuice") || gcLower.Contains("intbuff"))
                    consWriter.WriteByte(0x00);  // transient Mod1 flags
                consWriter.WriteByte(0x00);
                _server.WritePlayerEntitySynch(conn, consWriter);

                consWriter.WriteByte(0x06);
                _server.SendToClient(conn, consWriter.ToArray());

                playerState.ActiveItem = item;
                // Store stack count so HandlePlaceItemInInventory can restore it
                _server.SetStackCount(connId, 0xFFFFFFFF, stackCount);  // temp key for cursor item
                Debug.LogError($"[INVENTORY] ✅ Consumable picked up to cursor: {item.GCClass} x{stackCount}");
                return;
            }

            // Regular equipment - pick up to cursor
            _server.FreeInventorySlots(connId, storedX, storedY, itemWidth, itemHeight);

            var writer = new LEWriter();
            writer.WriteByte(0x07);

            writer.WriteByte(0x35);
            writer.WriteUInt16(componentId);
            writer.WriteByte(0x1F);
            writer.WriteUInt32(index);
            _server.WritePlayerEntitySynch(conn, writer);

            writer.WriteByte(0x35);
            writer.WriteUInt16(componentId);
            writer.WriteByte(0x28);
            item.WriteInitWithoutWeaponBytes(writer, playerState.Level);
            _server.WritePlayerEntitySynch(conn, writer);

            writer.WriteByte(0x06);

            _server.SendToClient(conn, writer.ToArray());

            playerState.ActiveItem = item;
            Debug.LogError($"[INVENTORY] ✅ Item picked up from inventory, now in hand!");
        }

        private void SendHPUpdate(RRConnection conn, ushort componentId, PlayerState playerState)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x35);
            writer.WriteUInt16(componentId);
            writer.WriteByte(0x00);
            _server.WritePlayerEntitySynch(conn, writer);
            writer.WriteByte(0x06);
            _server.SendToClient(conn, writer.ToArray());
        }

        private void SendManaUpdate(RRConnection conn, ushort componentId, PlayerState playerState)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x35);
            writer.WriteUInt16(componentId);
            writer.WriteByte(0x00);
            _server.WritePlayerEntitySynch(conn, writer);
            writer.WriteByte(0x06);
            _server.SendToClient(conn, writer.ToArray());
        }

        private void SendPotionModifier(RRConnection conn, PlayerState playerState, string gcType)
        {
            string connId = conn.ConnId.ToString();
            uint instanceId = _nextPotionModId++;

            bool isHealth = gcType.Contains("health");
            var activeDict = isHealth ? _activeHealthModId : _activeManaModId;

            if (activeDict.ContainsKey(connId) && activeDict[connId] != 0)
            {
                uint oldId = activeDict[connId];
                var removeWriter = new LEWriter();
                removeWriter.WriteByte(0x07);
                removeWriter.WriteByte(0x35);
                removeWriter.WriteUInt16(conn.ModifiersId);
                removeWriter.WriteByte(0x01);
                removeWriter.WriteUInt32(oldId);
                _server.WritePlayerEntitySynch(conn, removeWriter);
                removeWriter.WriteByte(0x06);
                _server.SendToClient(conn, removeWriter.ToArray());
            }

            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x35);
            writer.WriteUInt16(conn.ModifiersId);
            writer.WriteByte(0x00);
            writer.WriteByte(0xFF);
            writer.WriteCString(gcType);
            writer.WriteUInt32(instanceId);
            writer.WriteByte(0);
            writer.WriteUInt32(0);
            writer.WriteUInt32(0);
            writer.WriteByte(0);
            _server.WritePlayerEntitySynch(conn, writer);
            writer.WriteByte(0x06);
            _server.SendToClient(conn, writer.ToArray());

            activeDict[connId] = instanceId;

            // Schedule removal of the modifier after its GC duration (5 seconds)
            // Without this, duration=0 in the wire packet means INFINITE on the client
            float duration = 5.0f; // from PotionPAL.gc Duration=5
            _server.StartCoroutine(RemovePotionModifierAfterDelay(conn, playerState, instanceId, duration, isHealth));
        }

        private IEnumerator RemovePotionModifierAfterDelay(
            RRConnection conn, PlayerState playerState, uint modInstanceId, float delay, bool isHealth)
        {
            yield return new WaitForSeconds(delay);

            string connId = conn.ConnId.ToString();
            var activeDict = isHealth ? _activeHealthModId : _activeManaModId;

            // Only remove if this is still the active modifier (hasn't been replaced by another potion)
            if (activeDict.ContainsKey(connId) && activeDict[connId] == modInstanceId)
            {
                var removeWriter = new LEWriter();
                removeWriter.WriteByte(0x07);
                removeWriter.WriteByte(0x35);
                removeWriter.WriteUInt16(conn.ModifiersId);
                removeWriter.WriteByte(0x01);           // remove opcode
                removeWriter.WriteUInt32(modInstanceId);
                _server.WritePlayerEntitySynch(conn, removeWriter);
                removeWriter.WriteByte(0x06);
                _server.SendToClient(conn, removeWriter.ToArray());

                activeDict[connId] = 0;
                Debug.LogError($"[POTION] Removed {(isHealth ? "health" : "mana")} modifier {modInstanceId} after {delay}s");
            }
        }

        /// <summary>
        /// Sends a timed buff modifier (DragonJuice, IntBuff) with duration in the wire packet.
        /// Uses separate _activeBuffModId dictionary so buffs don't collide with health/mana.
        /// Wire format: Modifier::readData@0x4FF390 reads uint32 ID, byte Level,
        /// uint32 PowerLevel, uint32 Duration (ticks = seconds * 1000/24), byte SourceIsSelf.
        /// </summary>
        private void SendBuffModifier(RRConnection conn, PlayerState playerState, string gcType, float durationSec)
        {
            string connId = conn.ConnId.ToString();
            uint instanceId = _nextPotionModId++;
            uint durationTicks = (uint)(durationSec * 30);  // modifier timer runs at 30 ticks/sec

            // Key by buff type so different buffs stack, same type refreshes
            // e.g. "dragonjuice" or "intbuff" extracted from gcType
            string buffType = gcType.Contains("dragonjuice") ? "dragonjuice" : "intbuff";
            string buffKey = connId + ":" + buffType;

            // Remove previous buff of SAME type if active
            if (_activeBuffModId.ContainsKey(buffKey) && _activeBuffModId[buffKey] != 0)
            {
                uint oldId = _activeBuffModId[buffKey];
                var removeWriter = new LEWriter();
                removeWriter.WriteByte(0x07);
                removeWriter.WriteByte(0x35);
                removeWriter.WriteUInt16(conn.ModifiersId);
                removeWriter.WriteByte(0x01);
                removeWriter.WriteUInt32(oldId);
                _server.WritePlayerEntitySynch(conn, removeWriter);
                removeWriter.WriteByte(0x06);
                _server.SendToClient(conn, removeWriter.ToArray());
            }

            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x35);
            writer.WriteUInt16(conn.ModifiersId);
            writer.WriteByte(0x00);              // add modifier
            writer.WriteByte(0xFF);              // readType marker
            writer.WriteCString(gcType);         // e.g. potionpal.dragonjuice_sm.modifier
            writer.WriteUInt32(instanceId);      // unique ID
            writer.WriteByte(0x01);              // level
            writer.WriteUInt32(0);               // powerLevel
            writer.WriteUInt32(durationTicks);   // duration in ticks (client auto-removes)
            writer.WriteByte(0x01);              // sourceIsSelf = true
            _server.WritePlayerEntitySynch(conn, writer);
            writer.WriteByte(0x06);
            _server.SendToClient(conn, writer.ToArray());

            _activeBuffModId[buffKey] = instanceId;

            // Track for zone transition re-send
            _server.TrackModifierSent(conn.LoginName, gcType, instanceId,
                level: 1, powerLevel: 0, duration: durationTicks, sourceIsSelf: 1);

            // Server-side removal after duration (backup in case client doesn't auto-remove)
            _server.StartCoroutine(RemoveBuffModifierAfterDelay(conn, playerState, instanceId, durationSec, buffKey, gcType));
            Debug.LogError($"[BUFF] Applied {gcType} id={instanceId} duration={durationSec}s ({durationTicks} ticks)");
        }

        private IEnumerator RemoveBuffModifierAfterDelay(
            RRConnection conn, PlayerState playerState, uint modInstanceId, float delay, string buffKey, string gcType)
        {
            yield return new WaitForSeconds(delay);

            if (_activeBuffModId.ContainsKey(buffKey) && _activeBuffModId[buffKey] == modInstanceId)
            {
                var removeWriter = new LEWriter();
                removeWriter.WriteByte(0x07);
                removeWriter.WriteByte(0x35);
                removeWriter.WriteUInt16(conn.ModifiersId);
                removeWriter.WriteByte(0x01);
                removeWriter.WriteUInt32(modInstanceId);
                _server.WritePlayerEntitySynch(conn, removeWriter);
                removeWriter.WriteByte(0x06);
                _server.SendToClient(conn, removeWriter.ToArray());

                _activeBuffModId[buffKey] = 0;
                _server.UntrackModifier(conn.LoginName, gcType);
                Debug.LogError($"[BUFF] Removed buff modifier {modInstanceId} ({buffKey}) after {delay}s");
            }
        }

        private void HandleDropItem(RRConnection conn, LEReader reader, ushort componentId)
        {
            string connId = conn.ConnId.ToString();
            PlayerState playerState = _server.GetPlayerState(connId);
            GCObject item = playerState.ActiveItem;

            if (item == null)
            {
                Debug.LogError("[STATE] ❌ ERROR: No active item to drop!");
                return;
            }

            float playerX = conn.PlayerPosX;
            float playerY = conn.PlayerPosY;
            ushort entityId = _server.GetNextLootEntityId();
            int fx = (int)(playerX * 256);
            int fy = (int)(playerY * 256);

            var body = new LEWriter();

            body.WriteByte(0x35);
            body.WriteUInt16(componentId);
            body.WriteByte(0x29);
            _server.WritePlayerEntitySynch(conn, body);

            body.WriteByte(0x01);
            body.WriteUInt16(entityId);
            body.WriteByte(0xFF);
            body.WriteCString("itemobject");

            body.WriteByte(0x02);
            body.WriteUInt16(entityId);
            body.WriteUInt32(0x00000006);
            body.WriteInt32(fx);
            body.WriteInt32(fy);
            int fz = (int)(conn.PlayerPosZ * 256);
            body.WriteInt32(fz);
            body.WriteInt32(0);
            body.WriteByte(0xF7);
            body.WriteUInt16(0x0000);
            body.WriteByte(0x00);
            body.WriteUInt32(0x00000000);

            body.WriteByte(0x00);  // +0x101 state = 0 triggers FlipController animation (binary RE: 0x58ac2e)
            body.WriteUInt16(0x2233);
            body.WriteUInt32(0x00000000);
            body.WriteInt32(fx);
            body.WriteInt32(fy);
            body.WriteByte(0xBA);

            item.WriteInitForDroppedItem(body, playerState.Level);

            var chanWriter = new LEWriter();
            chanWriter.WriteByte(0x07);
            chanWriter.WriteBytes(body.ToArray());
            chanWriter.WriteByte(0x06);

            _server.SendToClient(conn, chanWriter.ToArray());
            int cursorQty = _server.GetStackCount(connId, 0xFFFFFFFF);
            if (cursorQty <= 0) cursorQty = 1;
            _server.TrackDroppedItem(entityId, item, conn, cursorQty);
            _server.SetStackCount(connId, 0xFFFFFFFF, 0); // clear cursor stack
            playerState.ActiveItem = null;
            Debug.LogError($"[DROP] ✅ Item dropped, EntityId={entityId}");

            // ═══ MULTIPLAYER: Broadcast dropped item to other players ═══
            var otherBody = new LEWriter();
            otherBody.WriteByte(0x01);
            otherBody.WriteUInt16(entityId);
            otherBody.WriteByte(0xFF);
            otherBody.WriteCString("itemobject");
            otherBody.WriteByte(0x02);
            otherBody.WriteUInt16(entityId);
            otherBody.WriteUInt32(0x00000006);
            otherBody.WriteInt32(fx);
            otherBody.WriteInt32(fy);
            otherBody.WriteInt32(fz);
            otherBody.WriteInt32(0);
            otherBody.WriteByte(0xF7);
            otherBody.WriteUInt16(0x0000);
            otherBody.WriteByte(0x00);
            otherBody.WriteUInt32(0x00000000);
            otherBody.WriteByte(0x00);  // +0x101 state = 0 triggers FlipController animation (binary RE: 0x58ac2e)
            otherBody.WriteUInt16(0x2233);
            otherBody.WriteUInt32(0x00000000);
            otherBody.WriteInt32(fx);
            otherBody.WriteInt32(fy);
            otherBody.WriteByte(0xBA);
            item.WriteInitForDroppedItem(otherBody, playerState.Level);

            var otherChan = new LEWriter();
            otherChan.WriteByte(0x07);
            otherChan.WriteBytes(otherBody.ToArray());
            otherChan.WriteByte(0x06);
            byte[] otherPacket = otherChan.ToArray();

            foreach (var other in _server.GetConnections().Values)
            {
                if (other == conn) continue;
                if (!other.IsSpawned) continue;
                if (other.CurrentZoneGcType != conn.CurrentZoneGcType) continue;
                if (other.InstanceId != conn.InstanceId) continue;
                _server.SendToClient(other, otherPacket);
            }
            Debug.LogError($"[DROP] Broadcast dropped item to other players");
        }
    }
}
