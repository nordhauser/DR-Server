using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using DungeonRunners.Utilities;

namespace DungeonRunners.Data
{
    /// <summary>
    /// Game Content Object - DFC format compatible with Python server
    /// SESSION 50 CRITICAL ADDITION: WriteInit and WriteData methods for equipment/skills
    /// </summary>
    public class GCObject
    {
        public static readonly byte DFC_VERSION = 0x2D; // Version 45 - CRITICAL for Python compatibility

        public uint Id { get; set; }
        public string Name { get; set; } = "";
        public string NativeClass { get; set; } = "";
        public string GCClass { get; set; } = "";

        /// <summary>
        /// Namespaces whose classes live in the items.pal.* package in the original
        /// DR data hierarchy. The class registry on the client knows these classes
        /// by their fully-qualified name (e.g. "items.pal.mageshoulderspal.rare001"),
        /// not by their bare form. Determined by scanning all 6421 .gc files for
        /// "extends items.pal.X." and "Item = items.pal.X." references — every
        /// namespace appearing in those patterns uses the prefix consistently
        /// (0 conflicts). Every other namespace (1HMeleeWeaponPAL, ScalePAL,
        /// 1HAxeMythicPAL, etc.) is referenced bare and must be sent bare.
        /// </summary>
        private static readonly HashSet<string> _itemsPalNamespaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "1haxepal", "1hmacepal", "1hstaffpal", "1hswordpal",
            "fighterbodypal", "fighterbootspal", "fighterglovespal", "fighterhelmpal",
            "fightershieldpal", "fightershoulderspal",
            "itempackpal", "itempackvisuals",
            "magebodypal", "magebootspal", "mageglovespal", "magehelmpal",
            "mageshieldpal", "mageshoulderspal",
            "rangerbodypal", "rangerbootspal", "rangerglovespal", "rangerhelmpal",
            "rangershoulderspal",
            "shieldvisuals", "voucherpal",
        };

        /// <summary>
        /// Returns the GCClass formatted for client packets. The original DR
        /// client's GCClassRegistry registers classes from items.pal.* namespaces
        /// under their fully-qualified name. Items from other namespaces
        /// (1HMeleeWeaponPAL, ScalePAL, *MythicPAL master files, etc.) are
        /// registered bare. We classify by namespace (first segment before
        /// the dot) against the items.pal. set built from the gc data.
        /// </summary>
        public string GetPacketGCClass() => GetPacketGCClassFor(GCClass);

        /// <summary>
        /// Static form of GetPacketGCClass: takes a raw gc_class string and
        /// returns the form the client expects on the wire. Used by code paths
        /// (e.g. inventory load in UnityGameServer) that don't construct a
        /// GCObject instance but still need to compute the prefixed form.
        /// Single source of truth — do not duplicate this logic anywhere else.
        /// </summary>
        public static string GetPacketGCClassFor(string gcClass)
        {
            if (string.IsNullOrEmpty(gcClass)) return gcClass ?? string.Empty;
            string lower = gcClass.ToLowerInvariant();
            // ItemPack potions → Major potions (client hotbar only recognizes Major/Minor/Noob)
            if (lower == "potionpal.healthpotion_itempack")
                return "items.consumables.consumable_majorhealthpotion";
            if (lower == "potionpal.manapotion_itempack")
                return "items.consumables.consumable_majormanapotion";
            if (lower.StartsWith("items.pal."))
                return lower;
            int dot = lower.IndexOf('.');
            if (dot > 0)
            {
                string ns = lower.Substring(0, dot);
                if (_itemsPalNamespaces.Contains(ns))
                    return "items.pal." + lower;
            }
            return lower;
        }
        public List<GCObjectProperty> Properties { get; set; } = new List<GCObjectProperty>();
        public List<GCObject> Children { get; set; } = new List<GCObject>();
        public byte[] ExtraData { get; set; } = Array.Empty<byte>();
        // Target equipment slot - used for rings where slot can be 3 or 4
        public uint? TargetSlot { get; set; } = null;
        // Pre-assigned ScaleMod — when set, GetModifierGCClass() returns this instead of random
        // Used by merchant buy to ensure the item in inventory matches what was shown in the shop
        public string PresetScaleMod { get; set; } = null;
        // Item rarity — stored as int matching ItemRarity enum (0=Normal...5=Mythic)
        // Set at creation (merchant buy, loot drop), persisted in DB, used for equip level check
        public int StoredRarity { get; set; } = -1;  // -1 = not set (legacy), 0-5 = known rarity
        // Item level — stored permanently at creation time, never changes
        // Per GC files: mythic items do NOT scale (ScaleToObserverLevel is consumables only)
        // Set at creation (merchant buy, loot drop), persisted in DB
        public int StoredLevel { get; set; } = -1;   // -1 = not set (legacy), >= 0 = fixed level

        /// <summary>
        /// Detect rarity from GCClass name patterns. Used as fallback for legacy items
        /// that don't have StoredRarity set. Returns int matching ItemRarity enum.
        /// </summary>
        public static int DetectRarityFromGCClass(string gcClass)
        {
            if (string.IsNullOrEmpty(gcClass)) return 0; // Normal
            string lower = gcClass.ToLowerInvariant();
            // MythicPAL must be checked first (contains "mythic")
            if (lower.Contains("mythicpal")) return 5;  // Mythic
            if (lower.Contains("mythic")) return 5;      // Mythic
            if (lower.Contains("unique")) return 4;       // Unique
            if (lower.Contains("rare")) return 3;         // Rare
            if (lower.Contains("magical") || lower.Contains("magic")) return 2; // Magical
            if (lower.Contains("superior")) return 1;     // Superior
            return 0; // Normal
        }

        /// <summary>
        /// Get effective rarity: uses StoredRarity if set, otherwise detects from GCClass.
        /// Returns int matching ItemRarity enum.
        /// </summary>
        public int GetEffectiveRarity()
        {
            if (StoredRarity >= 0) return StoredRarity;
            return DetectRarityFromGCClass(GCClass);
        }
        // Returns the correct modifier GCClass based on item's armor type
        private string GetModifierGCClass()
        {
            // If a specific ScaleMod was pre-assigned (e.g. from merchant buy), use it
            if (!string.IsNullOrEmpty(PresetScaleMod))
            {
                Debug.LogError($"[MODIFIER] Item '{GCClass}' -> Using PresetScaleMod '{PresetScaleMod}'");
                return PresetScaleMod;
            }

            string armorType = "Scale"; // Default fallback
            string gcLower = GCClass.ToLower();

            // Check in specific order - more specific matches first
            if (gcLower.Contains("plate"))
                armorType = "Plate";
            else if (gcLower.Contains("crystal"))
                armorType = "Crystal";
            else if (gcLower.Contains("chain"))
                armorType = "Chain";
            else if (gcLower.Contains("leather"))
                armorType = "Leather";
            else if (gcLower.Contains("cloth"))
                armorType = "Cloth";
            else if (gcLower.Contains("scale"))
                armorType = "Scale";

            // Get actual rarity from tier suffix (-1=Normal, -2=Superior, etc).
            // Named-rarity items (e.g. `PlatePAL.PlateUniqueArmor5`) have no dash
            // suffix → GetTierFromGcType returns 1 → Normal → we'd pick a Normal
            // ScaleMod, defeating the point. Fall back to GetEffectiveRarity which
            // honours StoredRarity AND DetectRarityFromGCClass name-pattern matching
            // (catches `*unique*`, `*rare*`, etc.).
            int tierSuffix = DungeonRunners.Managers.RarityHelper.GetTierFromGcType(GCClass);
            var itemRarity = DungeonRunners.Managers.RarityHelper.GetRarityFromTier(tierSuffix);
            if (itemRarity == DungeonRunners.Managers.ItemRarity.Normal)
            {
                int effective = GetEffectiveRarity();
                if (effective > 0 && effective < 5)
                    itemRarity = (DungeonRunners.Managers.ItemRarity)effective;
            }
            // Deterministic pick keyed by gcClass — same item type always picks the same
            // ScaleMod across relogs / zone changes. Random would flip every emission.
            string scaleMod = DungeonRunners.Managers.RarityHelper.GetDeterministicScaleMod(GCClass, itemRarity);

            Debug.LogError($"[MODIFIER] Item '{GCClass}' -> ArmorType '{armorType}' -> tier={tierSuffix} rarity={itemRarity} -> ScaleMod '{scaleMod}'");
            return scaleMod;
        }

        public void AddProperty(GCObjectProperty property)
        {
            Properties.Add(property);
        }

        public void AddChild(GCObject child)
        {
            Children.Add(child);
        }

        /// <summary>
        /// Write full GCObject in DFC format (Python-compatible)
        /// </summary>
        public void WriteFullGCObject(LEWriter writer)
        {
            Debug.Log($"[GCObject] Writing DFC object: ID={Id}, NativeClass='{NativeClass}', GCClass='{GCClass}', Props={Properties.Count}, Children={Children.Count}");

            // DFC version byte (0x2D = 45 decimal)
            writer.WriteByte(DFC_VERSION);

            // djb2 hash of native class name
            uint nativeHash = HashDjb2(NativeClass);
            writer.WriteUInt32(nativeHash);
            Debug.Log($"[GCObject] NativeClass hash: '{NativeClass}' -> 0x{nativeHash:X8}");

            // Node ID (uint32 little-endian)
            writer.WriteUInt32(Id);

            // Node name (null-terminated string)
            writer.WriteCString(Name);

            // Number of child nodes
            writer.WriteUInt32((uint)Children.Count);

            // Serialize child nodes recursively
            foreach (var child in Children)
            {
                child.WriteFullGCObject(writer);
            }

            // djb2 hash of GC class name
            // Hash the canonical client-facing form, not the raw GCClass stored
            // in DB. Items in the items.pal.* namespaces are stored bare in
            // character_inventory / character_equipment but the client's class
            // registry expects them by their fully-qualified items.pal.X.Y form.
            // For items in bare namespaces (1HMeleeWeaponPAL, ScalePAL, etc.)
            // GetPacketGCClassFor returns the input unchanged, so this is a
            // no-op for them. Required for SendCharacterList not to crash the
            // client when an items.pal.* item is in inventory or equipment.
            string gcForHash = GetPacketGCClassFor(GCClass);
            uint gcHash = HashDjb2(gcForHash);
            writer.WriteUInt32(gcHash);
            Debug.Log($"[GCObject] GCClass hash: '{GCClass}' -> 0x{gcHash:X8}");
            if (NativeClass == "Avatar")
            {
                Debug.LogError($"[CHARLIST-AVATAR] Avatar GCClass='{GCClass}' -> hash 0x{gcHash:X8}");
            }
            // Write properties with djb2 hashed names
            foreach (var prop in Properties)
            {
                prop.WriteDFC(writer);
            }

            // End object marker (4 null bytes)
            writer.WriteUInt32(0);

            // Extra data
            if (ExtraData.Length > 0)
            {
                writer.WriteBytes(ExtraData);
                Debug.Log($"[GCObject] Wrote {ExtraData.Length} bytes of extra data");
            }
        }

        //=============================================================================
        // WriteInitForDroppedItem - SEPARATE LOGIC
        //=============================================================================
        // ═══════════════════════════════════════════════════════════════════════════════
        // METHOD 1: WriteInitForDroppedItem (starts at line 131)
        // ═══════════════════════════════════════════════════════════════════════════════

        // ═══════════════════════════════════════════════════════════════════════════════
        // METHOD 1: WriteInitForDroppedItem (starts at line 131)
        // ═══════════════════════════════════════════════════════════════════════════════

        public void WriteInitForDroppedItem(LEWriter writer, int playerLevel)
        {
            Debug.LogError($"[DROP-WRITEINIT] ========== STARTING ==========");
            Debug.LogError($"[DROP-WRITEINIT] GCClass: '{GCClass}'");
            Debug.LogError($"[DROP-WRITEINIT] NativeClass: '{NativeClass}'");

            // FORCE CORRECT NATIVECLASS FOR RINGS/AMULETS
            string gcCheck = GCClass.ToLower();
            if (gcCheck.Contains("ring") || gcCheck.Contains("amulet"))
            {
                NativeClass = "Item";
            }

            writer.WriteByte(0xFF);
            writer.WriteCString(GetPacketGCClass());
            Debug.LogError($"[DROP-WRITEINIT] ✅ Wrote 0xFF + GCClass");

            string gcLowerCheck = GCClass.ToLower();

            // ═════════════════════════════════════════════════════════════════════════
            // CONSUMABLES (potions, scrolls, vouchers, skill books, town portals, quest
            // items): these extend ActiveItem/BaseVoucher/BaseSkillBook which use the
            // 'simple' Item::readData format — NOT Equipment::readInit. The equipment
            // format writes 12 bytes (equipSlot+fill+level+mods+rarity) but the client's
            // potion parser expects only 9 bytes (id+invX+invY+qty+level+flags+modCount).
            // The 3 extra bytes bleed into the next packet → client reads 0x00 as a
            // message type → 'Unknown message type(0)' crash.
            //
            // This matches the format HandlePlaceItemInInventory uses for the same
            // class of items at InventoryHandler.cs:~354 (the 'simple items: bare
            // readData format' branch) which is proven to work — that's why player
            // drops of potions succeed on the same WriteInitForDroppedItem call path.
            // ═════════════════════════════════════════════════════════════════════════
            bool isConsumableDrop = NativeClass == "ActiveItem"
                || gcLowerCheck.Contains("potion")
                || gcLowerCheck.Contains("scroll")
                || gcLowerCheck.Contains("skillbook")
                || gcLowerCheck.Contains("voucher")
                || gcLowerCheck.Contains("consumable")
                || gcLowerCheck.Contains("townportal")
                || gcLowerCheck.Contains("questitem")
                || gcLowerCheck.Contains("itempal");  // Dungeon09ItemPAL, Dungeon16ItemPAL, etc
            if (isConsumableDrop)
            {
                int simpleLevel = StoredLevel >= 0 ? StoredLevel : 1;
                writer.WriteUInt32(0);                       // id (0 for world drop)
                writer.WriteByte(0x00);                      // invX  → +0x80
                writer.WriteByte(0x00);                      // invY  → +0x81
                writer.WriteByte(0x01);                      // qty   → +0x82
                writer.WriteByte((byte)simpleLevel);         // level → +0x7F
                writer.WriteByte(0x00);                      // flags → +0x83 (bit2=0: no extra 2 bytes)
                // Binary RE: 0x583920 walks GC object's transient children.
                // For each transient child, calls child->vtable+0xB4 (readData).
                // ItemAttributeModifier::readData@0x588AE0 reads 1 byte (flags).
                // Potions with "transient Mod1 extends ItemAttributeModifier" in their
                // GC definition (DragonJuice, IntBuff) need this extra byte.
                // Potions WITHOUT transient children (health/mana) do NOT.
                bool hasTransientMod = gcLowerCheck.Contains("dragonjuice")
                    || gcLowerCheck.Contains("intbuff");
                if (hasTransientMod)
                {
                    writer.WriteByte(0x00);                  // transient Mod1 flags (no bits set)
                }
                writer.WriteByte(0x00);                      // ReadChildData<ItemModifier> count = 0
                Debug.LogError($"[DROP-WRITEINIT] ✅ CONSUMABLE format: level={simpleLevel}, transientMod={hasTransientMod}");
                Debug.LogError($"[DROP-WRITEINIT] ========== COMPLETE ==========");
                return;
            }

            if (NativeClass == "Item" || gcLowerCheck.Contains("ring") || gcLowerCheck.Contains("amulet"))
            {
                bool isAmulet = gcLowerCheck.Contains("amulet");
                uint ringSlot = TargetSlot ?? GetEquipmentSlotFromGCClass();

                // ───────────────────────────────────────────────────────────────────
                // Jewelry drop write — mirrors the merchant `WriteItem` pattern for
                // mythic items (`MerchantManager.cs:~2517-2664`) so the same item
                // gcType serializes consistently from a shop-purchase 0x1E and a
                // world drop. Don't call merchant code, just copy the source policy:
                //   • Placeholder count = `_mythicModSlots[key] - 1` if in the
                //     merchant dict (subtract 1 because OP5 emits the flag byte
                //     separately and the dict value already counts it), else
                //     `itemData.modCount`, else a sane jewelry default.
                //   • Phase-2 wire mods = `ItemStatDatabase.GetItemWireMods` —
                //     empty for non-IG-stub mythics (the `.gc` already carries
                //     inline Mod1..N children, so writing extra mods here would
                //     double-stack stats), non-empty for IG-stub mythics where
                //     the wire mods come from Phase 5 parse.
                //   • For non-mythic jewelry, keep the previous category-based
                //     placeholder defaults and an empty Phase-2 list.
                //   • ALWAYS emit placeholder bytes + Phase-2 count; never
                //     early-return after the level byte (that's the bug that
                //     made the client read the outer 0x06 EndStream as a Phase-2
                //     count → comm error 6).
                // ───────────────────────────────────────────────────────────────────
                bool isMythicJewelry = gcLowerCheck.Contains("mythic");
                int jewelryLevel = StoredLevel >= 0 ? StoredLevel : (isMythicJewelry ? (playerLevel + 3) : GetItemRequiredLevel());

                writer.WriteUInt32(ringSlot);
                writer.WriteByte(0x00);
                writer.WriteByte(0x00);
                writer.WriteByte(0x01);
                writer.WriteByte((byte)jewelryLevel);

                if (isMythicJewelry)
                {
                    // Mythic jewelry uses Equipment::readInit shape: flag + N placeholders
                    // (= .gc-baked Mod1..N children) + Phase 2 count + per-mod (0x04 +
                    // UInt32 DJB2 + 0x00 ItemModifier flags). Non-IG-stub mythics emit
                    // Phase 2 count=0 because Mod1..N are already inline in the .gc.
                    int jewelryPlaceholderCount;
                    List<uint> jewelryResolvedHashes = new List<uint>();
                    string jewelryKey = GCClass.ToLowerInvariant();
                    if (jewelryKey.StartsWith("items.pal.")) jewelryKey = jewelryKey.Substring("items.pal.".Length);
                    if (DungeonRunners.Managers.MerchantManager._mythicModSlots.TryGetValue(jewelryKey, out int merchantSlots))
                    {
                        jewelryPlaceholderCount = System.Math.Max(0, merchantSlots - 1);
                    }
                    else
                    {
                        var jewelryMods = isAmulet
                            ? DatabaseLoader.GetAmuletModifiers(GCClass)
                            : DatabaseLoader.GetRingModifiers(GCClass);
                        jewelryPlaceholderCount = jewelryMods.Count;
                    }
                    var jewelryWireMods = DungeonRunners.Data.ItemStatDatabase.Instance.GetItemWireMods(GCClass);
                    foreach (var (slot, modRef) in jewelryWireMods.OrderBy(w => w.Slot))
                    {
                        uint h = DungeonRunners.Data.ItemStatDatabase.Instance.GetGCClassHash(modRef);
                        if (h != 0) jewelryResolvedHashes.Add(h);
                    }

                    Debug.LogError($"[DROP-{(isAmulet ? "AMULET" : "RING")}-MYTHIC] GCClass: {GCClass}, Slot: {ringSlot}, Level: {jewelryLevel}, Placeholders: {jewelryPlaceholderCount}, WireMods: {jewelryResolvedHashes.Count}");

                    writer.WriteByte(0x00);  // mythic flag
                    for (int m = 0; m < jewelryPlaceholderCount; m++) writer.WriteByte(0x00);
                    writer.WriteByte((byte)jewelryResolvedHashes.Count);
                    foreach (uint h in jewelryResolvedHashes)
                    {
                        writer.WriteByte(0x04);
                        writer.WriteUInt32(h);
                        writer.WriteByte(0x00);
                    }
                }
                else
                {
                    // Non-mythic jewelry: 1 Phase-1 placeholder (BaseRing/BaseAmulet
                    // inherit 1 ItemModifier-descendant child from the engine `Item`
                    // base class — empirically confirmed by client crash trace
                    // ItemObject::readInit → Item::readData → ReadChildData<ItemModifier>
                    // when we wrote 0 placeholders; the 0xFF byte of the ScaleMod block
                    // was consumed as Phase 2 count → UInt16-sentinel path → 25K-iteration
                    // loop → tag 0x61 = 'a' from inside the cstring) + Phase 2 ScaleMod
                    // block with rarity fallback. Without the ScaleMod, Token Master
                    // Unique drops rendered white.
                    int dropTier = DungeonRunners.Managers.RarityHelper.GetTierFromGcType(GCClass);
                    var dropRarity = DungeonRunners.Managers.RarityHelper.GetRarityFromTier(dropTier);
                    if (dropRarity == DungeonRunners.Managers.ItemRarity.Normal)
                    {
                        int effective = GetEffectiveRarity();
                        if (effective > 0 && effective < 5)
                            dropRarity = (DungeonRunners.Managers.ItemRarity)effective;
                    }
                    Debug.LogError($"[DROP-{(isAmulet ? "AMULET" : "RING")}-NONMYTHIC] GCClass: {GCClass}, Slot: {ringSlot}, Level: {jewelryLevel}, Rarity: {dropRarity}");
                    writer.WriteByte(0x00);  // 1 Phase-1 placeholder (inherited from Item)

                    // Path B — wire-mod injection (Rare/Unique direct + Magic/Superior wrapper)
                    var dropJewelryMods = DungeonRunners.Data.ItemStatDatabase.Instance.GetItemWireMods(GCClass);
                    if (dropJewelryMods.Count == 0)
                        dropJewelryMods = DungeonRunners.Data.ItemStatDatabase.Instance.GetWrapperIGWireMods(GCClass, dropRarity.ToString());

                    if (DungeonRunners.Data.ItemStatDatabase.PathBEnabled && dropJewelryMods.Count > 0)
                    {
                        var hashes = new List<uint>();
                        foreach (var (slot, modRef) in dropJewelryMods.OrderBy(w => w.Slot))
                        {
                            uint h = DungeonRunners.Data.ItemStatDatabase.Instance.GetGCClassHash(modRef);
                            if (h != 0) hashes.Add(h);
                        }
                        writer.WriteByte((byte)hashes.Count);
                        foreach (uint h in hashes)
                        {
                            writer.WriteByte(0x04);
                            writer.WriteUInt32(h);
                            writer.WriteByte(0x00);
                        }
                        Debug.LogError($"[IG-INJECT] drop-{(isAmulet ? "amulet" : "ring")} {GCClass} rarity={dropRarity} mods={hashes.Count}");
                    }
                    else if (dropRarity == DungeonRunners.Managers.ItemRarity.Normal)
                    {
                        writer.WriteByte(0x00);  // Phase 2 count = 0
                    }
                    else
                    {
                        writer.WriteByte(0x01);  // Phase 2 count = 1
                        writer.WriteByte(0xFF);
                        writer.WriteCString(GetModifierGCClass());
                        writer.WriteByte(0x03);
                        writer.WriteByte(0x15);
                        writer.WriteUInt32(0x11111111);
                    }
                }

                return;
            }

            ItemData itemData = DatabaseLoader.FindItem(GCClass);
            string gcLower = GCClass.ToLower();

            bool isMythic = gcLower.Contains("mythic")
                || DungeonRunners.Managers.MerchantManager._mythicModSlots.ContainsKey(
                    gcLower.StartsWith("items.pal.") ? gcLower.Substring(10) : gcLower);
            bool isPartialbuilt = gcLower.Contains("partialbuilt");
            bool isPrebuilt = gcLower.Contains("prebuilt");
            bool isGeneratedboss = gcLower.Contains("generatedboss");
            bool hasMythicSeasonal = gcLower.Contains("mythicseasonal");
            bool hasMythicInName = gcLower.Contains("mythic") && !hasMythicSeasonal;
            bool hasSeasonal = gcLower.Contains("seasonal");
            bool hasWishingwell = gcLower.Contains("wishingwell");
            bool hasUnique = gcLower.Contains("unique");
            bool hasJustSeasonal = hasSeasonal && !gcLower.Contains("mythic") && !hasUnique;
            bool hasUniqueSeasonal = hasUnique && hasSeasonal;

            Debug.LogError($"[DROP-WRITEINIT] ItemData lookup: {(itemData != null ? "FOUND" : "NULL")}, isMythic={isMythic}, isPartialbuilt={isPartialbuilt}, isPrebuilt={isPrebuilt}, isGeneratedboss={isGeneratedboss}, hasMythicSeasonal={hasMythicSeasonal}, hasSeasonal={hasSeasonal}, hasWishingwell={hasWishingwell}, hasUniqueSeasonal={hasUniqueSeasonal}");

            uint equipSlot = GetPropertyUInt32("Slot");
            if (equipSlot == 0)
            {
                equipSlot = GetEquipmentSlotFromGCClass();
            }
            Debug.LogError($"[DROP-WRITEINIT] 🔥 Using equipSlot={equipSlot}");

            writer.WriteUInt32(equipSlot);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
            writer.WriteByte(0x01);

            int level = StoredLevel >= 0 ? StoredLevel : (isMythic ? (playerLevel + 3) : GetItemRequiredLevel());
            writer.WriteByte((byte)level);

            bool writeFlag = false;

            if (isMythic && !isPartialbuilt)
            {
                writeFlag = true;
            }
            else if (isPartialbuilt && hasMythicSeasonal)
            {
                writeFlag = true;
            }
            else if (isPartialbuilt && hasUniqueSeasonal && !gcLower.Contains("fighter"))
            {
                writeFlag = true;
            }
            else if (isPrebuilt && hasMythicInName)
            {
                writeFlag = true;
            }
            else if (isPrebuilt && hasMythicSeasonal)
            {
                writeFlag = true;
            }
            else if (isPrebuilt && hasWishingwell && !gcLower.Contains("fighter") && !gcLower.Contains("helm"))
            {
                writeFlag = true;
            }

            // Fallback: NON-MythicPAL items in lookup table always get flag byte (GetOP5ModCount assumes it)
            // MythicPAL items already have correct writeFlag logic above — don't override them
            if (!writeFlag && isMythic && GCClass.IndexOf("MythicPAL", System.StringComparison.OrdinalIgnoreCase) < 0)
            {
                writeFlag = true;
            }

            if (writeFlag)
            {
                if ((gcLower.Contains("scale") && !gcLower.Contains("helm") && !gcLower.Contains("armor") && !gcLower.Contains("100") && !gcLower.Contains("101")) ||
                    (gcLower.Contains("splint") && !gcLower.Contains("shoulder") && !gcLower.Contains("armor") && !gcLower.Contains("100") && !gcLower.Contains("101")))
                {
                    Debug.LogError($"[DROP-WRITEINIT] SKIPPED flag for scale/splint");
                    writeFlag = false;
                }
            }

            bool isWeapon = (NativeClass == "MeleeWeapon" || NativeClass == "RangedWeapon");
            if (writeFlag && (!isWeapon || isMythic))
            {
                writer.WriteByte(0x00);
                Debug.LogError($"[EQUIP-WRITEINIT] Wrote FLAG byte (armor only)");
            }

            Debug.LogError($"[DROP-WRITEINIT] ✅ Wrote basic item data (equipSlot={equipSlot}, level={level})");

            int modCount;

            // ═══ GC LOOKUP — exact modCount from parsed GC files ═══
            int dropGcLookup = DungeonRunners.Managers.MerchantManager.GetOP5ModCount(GCClass);
            if (dropGcLookup >= 0)
            {
                modCount = dropGcLookup;
                if (!writeFlag) modCount++;  // scale/splint exception removed flag byte, compensate
                Debug.LogError($"[DROP-WRITEINIT] GC LOOKUP: {GCClass} → modCount={modCount}");
            }
            else if (isPartialbuilt)
            {
                if (hasMythicSeasonal)
                {
                    if (gcLower.Contains("fighter"))
                    {
                        if (gcLower.Contains("seasonal002"))
                        {
                            modCount = 4;
                        }
                        else
                        {
                            modCount = 3;
                        }
                    }
                    else if (gcLower.Contains("ranger"))
                    {
                        modCount = 4;
                    }
                    else
                    {
                        modCount = 2;
                    }
                }
                else if (hasMythicInName)
                {
                    if (gcLower.Contains("fighter"))
                    {
                        modCount = 5;
                    }
                    else if (gcLower.Contains("ranger"))
                    {
                        modCount = 2;
                    }
                    else
                    {
                        modCount = 2;
                    }
                }
                else if (hasJustSeasonal)
                {
                    if (gcLower.Contains("fighter"))
                    {
                        modCount = 3;
                    }
                    else if (gcLower.Contains("ranger"))
                    {
                        if (gcLower.Contains("helm"))
                        {
                            modCount = 3;
                        }
                        else
                        {
                            modCount = 4;
                        }
                    }
                    else if (gcLower.Contains("mage"))
                    {
                        if (gcLower.Contains("helm"))
                        {
                            modCount = 3;
                        }
                        else
                        {
                            modCount = 4;
                        }
                    }
                    else
                    {
                        modCount = 4;
                    }
                }
                else if (hasUniqueSeasonal)
                {
                    if (gcLower.Contains("fighter"))
                    {
                        if (gcLower.Contains("shield"))
                        {
                            modCount = 4;
                        }
                        else
                        {
                            modCount = 3;
                        }
                    }
                    else if (gcLower.Contains("ranger"))
                    {
                        modCount = 3;
                    }
                    else
                    {
                        modCount = 3;
                    }
                }
                else if (hasWishingwell)
                {
                    if (gcLower.Contains("fighter"))
                    {
                        modCount = 4;
                    }
                    else if (gcLower.Contains("ranger"))
                    {
                        modCount = 3;
                    }
                    else if (gcLower.Contains("mage"))
                    {
                        if (gcLower.Contains("helm"))
                        {
                            modCount = 4;
                        }
                        else
                        {
                            modCount = 3;
                        }
                    }
                    else
                    {
                        modCount = 3;
                    }
                }
                else
                {
                    if (gcLower.Contains("fighter"))
                    {
                        modCount = 4;
                    }
                    else if (gcLower.Contains("ranger"))
                    {
                        modCount = 3;
                    }
                    else
                    {
                        modCount = 3;
                    }
                }
            }
            else if (isPrebuilt)
            {
                if (hasMythicInName)
                {
                    if (gcLower.Contains("mythic002"))
                    {
                        if (gcLower.Contains("helm"))
                        {
                            modCount = 6;
                        }
                        else
                        {
                            modCount = 5;
                        }
                    }
                    else if (gcLower.Contains("mythic003"))
                    {
                        if (gcLower.Contains("helm"))
                        {
                            modCount = 6;
                        }
                        else
                        {
                            modCount = 5;
                        }
                    }
                    else
                    {
                        if (gcLower.Contains("fighter"))
                        {
                            modCount = 5;
                        }
                        else if (gcLower.Contains("ranger"))
                        {
                            modCount = 6;
                        }
                        else if (gcLower.Contains("mage"))
                        {
                            modCount = 5;
                        }
                        else
                        {
                            modCount = 5;
                        }
                    }
                }
                else if (hasMythicSeasonal)
                {
                    if (gcLower.Contains("fighter"))
                    {
                        if (gcLower.Contains("helm"))
                        {
                            modCount = 6;
                        }
                        else if (gcLower.Contains("shield"))
                        {
                            modCount = 6;
                        }
                        else
                        {
                            modCount = 5;
                        }
                    }
                    else if (gcLower.Contains("ranger"))
                    {
                        if (gcLower.Contains("helm"))
                        {
                            modCount = 6;
                        }
                        else if (gcLower.Contains("shield"))
                        {
                            modCount = 6;
                        }
                        else
                        {
                            modCount = 5;
                        }
                    }
                    else if (gcLower.Contains("mage"))
                    {
                        if (gcLower.Contains("helm"))
                        {
                            modCount = 6;
                        }
                        else if (gcLower.Contains("shield"))
                        {
                            modCount = 7;
                        }
                        else
                        {
                            modCount = 5;
                        }
                    }
                    else
                    {
                        modCount = 5;
                    }
                }
                else if (gcLower.Contains("boss"))
                {
                    if (gcLower.Contains("fighter"))
                    {
                        if (gcLower.Contains("helm"))
                        {
                            modCount = 6;
                        }
                        else
                        {
                            modCount = 7;
                        }
                    }
                    else if (gcLower.Contains("ranger"))
                    {
                        if (gcLower.Contains("helm"))
                        {
                            modCount = 6;
                        }
                        else
                        {
                            modCount = 7;
                        }
                    }
                    else if (gcLower.Contains("mage"))
                    {
                        if (gcLower.Contains("shield"))
                        {
                            modCount = 6;
                        }
                        else
                        {
                            modCount = 7;
                        }
                    }
                    else
                    {
                        modCount = 4;
                    }
                }
                else if (hasWishingwell && !gcLower.Contains("fighter"))
                {
                    if (gcLower.Contains("helm"))
                    {
                        modCount = 7;
                    }
                    else
                    {
                        modCount = 5;
                    }
                }
                else
                {
                    modCount = itemData?.modCount ?? 4;
                }
            }
            else if (isGeneratedboss)
            {
                if (gcLower.Contains("fighter"))
                {
                    if (gcLower.Contains("helm") || gcLower.Contains("shield"))
                    {
                        modCount = 6;
                    }
                    else
                    {
                        modCount = 7;
                    }
                }
                else if (gcLower.Contains("ranger"))
                {
                    if (gcLower.Contains("helm") || gcLower.Contains("shield"))
                    {
                        modCount = 6;
                    }
                    else
                    {
                        modCount = 7;
                    }
                }
                else if (gcLower.Contains("mage"))
                {
                    modCount = 2;
                }
                else
                {
                    modCount = 4;
                }
            }
            else if (isMythic && NativeClass == "Armor")
            {
                modCount = 5;
                if (gcLower.Contains("chain") && !gcLower.Contains("shield"))
                    modCount = 4;
                else if (gcLower.Contains("crystal"))
                    modCount = 6;
                else if (gcLower.Contains("leather"))
                {
                    if (gcLower.Contains("shield") || gcLower.Contains("gloves") || gcLower.Contains("helm") || gcLower.Contains("armor"))
                        modCount = 6;
                }
                else if (gcLower.Contains("scale"))
                    modCount = 6;
                else if (gcLower.Contains("splint"))
                {
                    if (gcLower.Contains("100") || gcLower.Contains("101"))
                    {
                        modCount = 1;
                    }
                    else
                    {
                        modCount = 6;
                    }
                }
                else if (gcLower.Contains("plate") && gcLower.Contains("shield"))
                {
                    if (gcLower.Contains("shield3"))
                    {
                        modCount = 6;
                    }
                    else
                    {
                        modCount = 5;
                    }
                }
            }
            else if (isMythic && (gcLower.Contains("sword") || gcLower.Contains("axe") || gcLower.Contains("mace") || gcLower.Contains("staff") || gcLower.Contains("pick") || gcLower.Contains("club") || gcLower.Contains("katana") || gcLower.Contains("polearm") || gcLower.Contains("crossbow") || gcLower.Contains("bow") || gcLower.Contains("weapon")))
            {
                if (gcLower.Contains("mythic6") || gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                {
                    modCount = 6;
                }
                else
                {
                    modCount = 5;
                }
            }
            else
            {
                modCount = itemData?.modCount ?? 3;
                if (gcLower.Contains("chain") && !gcLower.Contains("shield"))
                    modCount = 1;
            }

            for (int i = 0; i < modCount; i++)
            {
                writer.WriteByte(0x00);
            }

            if (dropGcLookup >= 0)
            {
                // Mythic armor/weapon drop. Was previously writing a single 0x00 here
                // (no Phase 2 mods at all), which left the client expecting more bytes
                // and made it read the 0x06 EndStream marker as in-stream data → comm
                // error 6 / "type tag" desync. Mirror the merchant 0x1E write
                // (MerchantManager.WriteItem ~line 2618-2657) and the amulet+ring
                // drop branches at line 376-388 / 459-465: pull wire mods from
                // ItemStatDatabase, resolve each to its DJB2 hash, and emit one
                // [0x04][UInt32 hash][0x00] tagged class ref per mod after a
                // single Phase 2 count byte. Yields a properly-statted mythic on
                // pickup. Same wire format the merchant uses for IG-stub mythics.
                var dropWireMods = DungeonRunners.Data.ItemStatDatabase.Instance.GetItemWireMods(GCClass);
                var dropResolved = new List<uint>(dropWireMods.Count);
                foreach (var (slot, modRef) in dropWireMods.OrderBy(w => w.Slot))
                {
                    uint h = DungeonRunners.Data.ItemStatDatabase.Instance.GetGCClassHash(modRef);
                    if (h != 0) dropResolved.Add(h);
                }
                writer.WriteByte((byte)dropResolved.Count);
                foreach (uint h in dropResolved)
                {
                    writer.WriteByte(0x04);
                    writer.WriteUInt32(h);
                    writer.WriteByte(0x00);
                }
                Debug.LogError($"[DROP-WRITEINIT] GC lookup item — wrote {dropResolved.Count} resolved wire mods (was 1 empty)");
            }
            else
            {
                int dropTier = DungeonRunners.Managers.RarityHelper.GetTierFromGcType(GCClass);
                var dropRarity = DungeonRunners.Managers.RarityHelper.GetRarityFromTier(dropTier);
                // Named-rarity items (e.g. `PlatePAL.PlateUniqueArmor5`) have no
                // dash-suffix → tier 1 → Normal → no ScaleMod block. Fall back to
                // GetEffectiveRarity so StoredRarity / name-pattern-detected Unique
                // items get their tier-appropriate ScaleMod and render in the right
                // colour.
                if (dropRarity == DungeonRunners.Managers.ItemRarity.Normal)
                {
                    int effective = GetEffectiveRarity();
                    if (effective > 0 && effective < 5)
                        dropRarity = (DungeonRunners.Managers.ItemRarity)effective;
                }

                // Path B — try wire-mod injection (direct + wrapper) before legacy ScaleMod.
                var dropArmorMods = DungeonRunners.Data.ItemStatDatabase.Instance.GetItemWireMods(GCClass);
                if (dropArmorMods.Count == 0)
                    dropArmorMods = DungeonRunners.Data.ItemStatDatabase.Instance.GetWrapperIGWireMods(GCClass, dropRarity.ToString());

                if (DungeonRunners.Data.ItemStatDatabase.PathBEnabled && dropArmorMods.Count > 0)
                {
                    var hashes = new List<uint>();
                    var skipped = new List<string>();
                    var emitted = new List<string>();
                    foreach (var (slot, modRef) in dropArmorMods.OrderBy(w => w.Slot))
                    {
                        uint h = DungeonRunners.Data.ItemStatDatabase.Instance.GetGCClassHash(modRef);
                        if (h != 0) { hashes.Add(h); emitted.Add($"{modRef}#0x{h:X8}"); }
                        else skipped.Add(modRef);
                    }
                    writer.WriteByte((byte)hashes.Count);
                    foreach (uint h in hashes)
                    {
                        writer.WriteByte(0x04);
                        writer.WriteUInt32(h);
                        writer.WriteByte(0x00);
                    }
                    Debug.LogError($"[IG-INJECT] drop-armor {GCClass} rarity={dropRarity} mods={hashes.Count}/{dropArmorMods.Count} phase1ModCount={modCount} emitted=[{string.Join(" | ", emitted)}] skipped=[{string.Join(",", skipped)}]");
                }
                else if (dropRarity == DungeonRunners.Managers.ItemRarity.Normal)
                {
                    writer.WriteByte(0x00);
                }
                else
                {
                    writer.WriteByte(0x01);
                    writer.WriteByte(0xFF);
                    writer.WriteCString(GetModifierGCClass());
                    writer.WriteByte(0x03);
                    writer.WriteByte(0x15);
                    writer.WriteUInt32(0x11111111);
                }
            }

            Debug.LogError($"[DROP-WRITEINIT] ========== COMPLETE ==========");
        }


        //=============================================================================
        // WriteInit - SEPARATE LOGIC
        //=============================================================================
        // ═══════════════════════════════════════════════════════════════════════════════
        // METHOD 2: WriteInit (starts at line 961)
        // ═══════════════════════════════════════════════════════════════════════════════

        // ═══════════════════════════════════════════════════════════════════════════════
        // METHOD 2: WriteInit (starts at line 961)
        // ═══════════════════════════════════════════════════════════════════════════════

        public void WriteInit(LEWriter writer, int playerLevel)
        {
            Debug.LogError($"[GCOBJECT-WRITEINIT-CALLED] ⚠️⚠️⚠️ GCObject.WriteInit() CALLED FOR: {NativeClass} - {GCClass}");
            Debug.LogError($"[WRITEINT-START] {NativeClass} - {GCClass}");

            // FORCE CORRECT NATIVECLASS FOR RINGS/AMULETS
            string gcCheck = GCClass.ToLower();
            if (gcCheck.Contains("ring") || gcCheck.Contains("amulet"))
            {
                NativeClass = "Item";
            }

            if (NativeClass == "Armor" || NativeClass == "MeleeWeapon" || NativeClass == "RangedWeapon" || NativeClass == "Item")
            {
                int startPos = writer.Position;
                Debug.LogError($"[EQUIPMENT-INIT] Writing FULL equipment data for {GCClass}");

                writer.WriteByte(0xFF);
                writer.WriteCString(GetPacketGCClass());
                Debug.LogError($"[EQUIPMENT-INIT] Wrote type tag + GCClass: {GCClass}");

                uint equipSlot = GetPropertyUInt32("Slot");
                if (equipSlot == 0)
                {
                    equipSlot = GetEquipmentSlotFromGCClass();
                }
                writer.WriteUInt32(equipSlot);
                writer.WriteByte(0x00);
                writer.WriteByte(0x00);
                writer.WriteByte(0x01);

                // ═══════════════════════════════════════════════════════════════════════════════
                // FIXED ITEM SERIALIZATION FOR RINGS/AMULETS
                // ═══════════════════════════════════════════════════════════════════════════════
                string gcLower = GCClass.ToLower();
                if (NativeClass == "Item" || gcLower.Contains("ring") || gcLower.Contains("amulet"))
                {
                    bool isAmulet = gcLower.Contains("amulet");

                    if (isAmulet)
                    {
                        bool isMythicAmulet = gcLower.Contains("mythic");
                        int itemLevel = StoredLevel >= 0 ? StoredLevel : (isMythicAmulet ? (playerLevel + 3) : GetItemRequiredLevel());

                        int amuletModCount;
                        List<string> amuletMods;

                        if (isMythicAmulet)
                        {
                            amuletMods = DatabaseLoader.GetAmuletModifiers(GCClass);
                            amuletModCount = amuletMods.Count;
                        }
                        else
                        {
                            // Normalized to 1 across all non-mythic amulet PAL classes — matches
                            // WriteInitForInventory and the 2026-05-20 jewelry analysis: BaseAmulet
                            // contributes exactly 1 inherited ItemModifier-descendant child to
                            // Phase 1, regardless of PAL subtype. The legacy =2 for
                            // questamuletpal/uniqueamuletpal was speculative.
                            amuletModCount = 1;
                            amuletMods = new List<string>();
                        }

                        // Path B — non-mythic amulet wire-mod injection
                        if (!isMythicAmulet)
                        {
                            int eqTier = DungeonRunners.Managers.RarityHelper.GetTierFromGcType(GCClass);
                            var eqRarity = DungeonRunners.Managers.RarityHelper.GetRarityFromTier(eqTier);
                            if (eqRarity == DungeonRunners.Managers.ItemRarity.Normal)
                            {
                                int effective = GetEffectiveRarity();
                                if (effective > 0 && effective < 5)
                                    eqRarity = (DungeonRunners.Managers.ItemRarity)effective;
                            }
                            var pbAmuletWire = DungeonRunners.Data.ItemStatDatabase.Instance.GetItemWireMods(GCClass);
                            if (pbAmuletWire.Count == 0)
                                pbAmuletWire = DungeonRunners.Data.ItemStatDatabase.Instance.GetWrapperIGWireMods(GCClass, eqRarity.ToString());
                            if (DungeonRunners.Data.ItemStatDatabase.PathBEnabled && pbAmuletWire.Count > 0)
                            {
                                foreach (var (_, modRef) in pbAmuletWire.OrderBy(w => w.Slot))
                                    amuletMods.Add(modRef);
                            }
                        }

                        Debug.LogError($"[INIT-AMULET] GCClass: {GCClass}, Level: {itemLevel}, ModCount: {amuletModCount}, Mythic: {isMythicAmulet}, PathBMods: {amuletMods.Count}");

                        writer.WriteByte((byte)itemLevel);

                        if (amuletModCount <= 0 && amuletMods.Count == 0)
                        {
                            return;
                        }

                        if (isMythicAmulet)
                        {
                            writer.WriteByte(0x00);
                        }

                        for (int m = 0; m < amuletModCount; m++)
                        {
                            writer.WriteByte(0x00);
                        }

                        var resolvedAmuletHashes = new List<uint>(amuletMods.Count);
                        foreach (var mod in amuletMods)
                        {
                            uint amuletHash = DungeonRunners.Data.ItemStatDatabase.Instance.GetGCClassHash(mod);
                            if (amuletHash != 0) resolvedAmuletHashes.Add(amuletHash);
                        }
                        writer.WriteByte((byte)resolvedAmuletHashes.Count);
                        foreach (uint amuletHash in resolvedAmuletHashes)
                        {
                            writer.WriteByte(0x04);
                            writer.WriteUInt32(amuletHash);
                            writer.WriteByte(0x00);
                        }
                        if (!isMythicAmulet && resolvedAmuletHashes.Count > 0)
                            Debug.LogError($"[IG-INJECT] init-amulet {GCClass} mods={resolvedAmuletHashes.Count}");

                        return;
                    }
                    else
                    {
                        bool isMythicRing = gcLower.Contains("mythic");
                        int itemLevel = StoredLevel >= 0 ? StoredLevel : (isMythicRing ? (playerLevel + 3) : GetItemRequiredLevel());

                        int ringModCount;
                        List<string> ringMods;

                        if (isMythicRing)
                        {
                            ringMods = DatabaseLoader.GetRingModifiers(GCClass);
                            ringModCount = ringMods.Count;
                        }
                        else
                        {
                            // Phase-1 placeholder count = 1 across all non-mythic ring PAL classes
                            // (BaseRing's single inherited ItemModifier child).
                            ringModCount = 1;
                            ringMods = new List<string>();
                        }

                        // Path B — non-mythic ring wire-mod injection
                        if (!isMythicRing)
                        {
                            int eqTier = DungeonRunners.Managers.RarityHelper.GetTierFromGcType(GCClass);
                            var eqRarity = DungeonRunners.Managers.RarityHelper.GetRarityFromTier(eqTier);
                            if (eqRarity == DungeonRunners.Managers.ItemRarity.Normal)
                            {
                                int effective = GetEffectiveRarity();
                                if (effective > 0 && effective < 5)
                                    eqRarity = (DungeonRunners.Managers.ItemRarity)effective;
                            }
                            var pbRingWire = DungeonRunners.Data.ItemStatDatabase.Instance.GetItemWireMods(GCClass);
                            if (pbRingWire.Count == 0)
                                pbRingWire = DungeonRunners.Data.ItemStatDatabase.Instance.GetWrapperIGWireMods(GCClass, eqRarity.ToString());
                            if (DungeonRunners.Data.ItemStatDatabase.PathBEnabled && pbRingWire.Count > 0)
                            {
                                foreach (var (_, modRef) in pbRingWire.OrderBy(w => w.Slot))
                                    ringMods.Add(modRef);
                            }
                        }

                        Debug.LogError($"[INIT-RING] GCClass: {GCClass}, Level: {itemLevel}, ModCount: {ringModCount}, Mythic: {isMythicRing}, PathBMods: {ringMods.Count}");

                        writer.WriteByte((byte)itemLevel);

                        if (ringModCount <= 0 && ringMods.Count == 0)
                        {
                            return;
                        }

                        if (isMythicRing)
                        {
                            writer.WriteByte(0x00);
                        }

                        for (int m = 0; m < ringModCount; m++)
                        {
                            writer.WriteByte(0x00);
                        }

                        var resolvedRingHashes = new List<uint>(ringMods.Count);
                        foreach (var mod in ringMods)
                        {
                            uint ringHash = DungeonRunners.Data.ItemStatDatabase.Instance.GetGCClassHash(mod);
                            if (ringHash != 0) resolvedRingHashes.Add(ringHash);
                        }
                        writer.WriteByte((byte)resolvedRingHashes.Count);
                        foreach (uint ringHash in resolvedRingHashes)
                        {
                            writer.WriteByte(0x04);
                            writer.WriteUInt32(ringHash);
                            writer.WriteByte(0x00);
                        }
                        if (!isMythicRing && resolvedRingHashes.Count > 0)
                            Debug.LogError($"[IG-INJECT] init-ring {GCClass} mods={resolvedRingHashes.Count}");

                        return;
                    }
                }

                ItemData itemData = DatabaseLoader.FindItem(GCClass);

                // Detection flags
                bool isMythic = gcLower.Contains("mythic")
                || DungeonRunners.Managers.MerchantManager._mythicModSlots.ContainsKey(
                    gcLower.StartsWith("items.pal.") ? gcLower.Substring(10) : gcLower);
                bool isPartialbuilt = gcLower.Contains("partialbuilt");
                bool isPrebuilt = gcLower.Contains("prebuilt");
                bool isGeneratedboss = gcLower.Contains("generatedboss");
                bool hasMythicSeasonal = gcLower.Contains("mythicseasonal");
                bool hasMythicInName = gcLower.Contains("mythic") && !hasMythicSeasonal;
                bool hasSeasonal = gcLower.Contains("seasonal");
                bool hasWishingwell = gcLower.Contains("wishingwell");
                bool hasUnique = gcLower.Contains("unique");
                bool hasJustSeasonal = hasSeasonal && !gcLower.Contains("mythic") && !hasUnique;
                bool hasUniqueSeasonal = hasUnique && hasSeasonal;

                int level = StoredLevel >= 0 ? StoredLevel : (isMythic ? (playerLevel + 3) : GetItemRequiredLevel());
                writer.WriteByte((byte)level);
                Debug.LogError($"[EQUIPMENT-INIT] Wrote level: {level} (0x{level:X2})");

                //------------------------------------------
                // FLAG BYTE LOGIC (SEPARATE)
                //------------------------------------------
                bool writeFlag = false;

                if (isMythic && !isPartialbuilt)
                {
                    writeFlag = true;
                }
                else if (isPartialbuilt && hasMythicSeasonal)
                {
                    writeFlag = true;
                }
                else if (isPartialbuilt && hasUniqueSeasonal && !gcLower.Contains("fighter"))
                {
                    writeFlag = true;
                }
                else if (isPrebuilt && hasMythicInName)
                {
                    writeFlag = true;
                }
                else if (isPrebuilt && hasMythicSeasonal)
                {
                    writeFlag = true;
                }
                else if (isPrebuilt && hasWishingwell && !gcLower.Contains("fighter") && !gcLower.Contains("helm"))
                {
                    writeFlag = true;
                }

                // Fallback: NON-MythicPAL items in lookup table always get flag byte (GetOP5ModCount assumes it)
                // MythicPAL items already have correct writeFlag logic above — don't override them
                if (!writeFlag && isMythic && GCClass.IndexOf("MythicPAL", System.StringComparison.OrdinalIgnoreCase) < 0)
                {
                    writeFlag = true;
                }

                if (writeFlag)
                {
                    if ((gcLower.Contains("scale") && !gcLower.Contains("helm") && !gcLower.Contains("armor") && !gcLower.Contains("100") && !gcLower.Contains("101")) ||
                        (gcLower.Contains("splint") && !gcLower.Contains("shoulder") && !gcLower.Contains("armor") && !gcLower.Contains("100") && !gcLower.Contains("101")))
                    {
                        Debug.LogError($"[EQUIPMENT-INIT] SKIPPED flag for scale/splint");
                        writeFlag = false;
                    }
                }

                bool isWeapon = (NativeClass == "MeleeWeapon" || NativeClass == "RangedWeapon");
                if (writeFlag && (!isWeapon || isMythic))
                {
                    writer.WriteByte(0x00);
                    Debug.LogError($"[EQUIP-WRITEINIT] Wrote FLAG byte (armor only)");
                }

                //------------------------------------------
                // MOD COUNT LOGIC (SEPARATE)
                //------------------------------------------
                int itemModCount;

                // ═══ GC LOOKUP — exact modCount from parsed GC files ═══
                int writeInitGcLookup = DungeonRunners.Managers.MerchantManager.GetOP5ModCount(GCClass);
                if (writeInitGcLookup >= 0)
                {
                    itemModCount = writeInitGcLookup;
                    if (!writeFlag) itemModCount++;  // scale/splint exception removed flag byte, compensate
                    Debug.LogError($"[EQUIPMENT-INIT] GC LOOKUP: {GCClass} → itemModCount={itemModCount}");
                }
                else if (isPartialbuilt)
                {
                    if (hasMythicSeasonal)
                    {
                        if (gcLower.Contains("fighter"))
                        {
                            if (gcLower.Contains("seasonal002"))
                            {
                                itemModCount = 4;
                                Debug.LogError($"[EQUIPMENT-INIT] PARTIALBUILT MYTHICSEASONAL002 FIGHTER - modCount=4");
                            }
                            else
                            {
                                itemModCount = 3;
                                Debug.LogError($"[EQUIPMENT-INIT] PARTIALBUILT MYTHICSEASONAL001 FIGHTER - modCount=3");
                            }
                        }
                        else if (gcLower.Contains("ranger"))
                        {
                            itemModCount = 4;
                            Debug.LogError($"[EQUIPMENT-INIT] PARTIALBUILT MYTHICSEASONAL RANGER - modCount=4");
                        }
                        else
                        {
                            itemModCount = 2;
                            Debug.LogError($"[EQUIPMENT-INIT] PARTIALBUILT MYTHICSEASONAL MAGE - modCount=2");
                        }
                    }
                    else if (hasMythicInName)
                    {
                        if (gcLower.Contains("fighter"))
                        {
                            itemModCount = 5;
                            Debug.LogError($"[EQUIPMENT-INIT] PARTIALBUILT MYTHIC FIGHTER - modCount=5");
                        }
                        else if (gcLower.Contains("ranger"))
                        {
                            itemModCount = 2;
                            Debug.LogError($"[EQUIPMENT-INIT] PARTIALBUILT MYTHIC RANGER - modCount=2");
                        }
                        else
                        {
                            itemModCount = 2;
                            Debug.LogError($"[EQUIPMENT-INIT] PARTIALBUILT MYTHIC MAGE - modCount=2");
                        }
                    }
                    else if (hasJustSeasonal)
                    {
                        if (gcLower.Contains("fighter"))
                        {
                            if (gcLower.Contains("helm"))
                            {
                                itemModCount = 3;
                                Debug.LogError($"[EQUIPMENT-INIT] PARTIALBUILT JUST-SEASONAL FIGHTER HELM - modCount=3");
                            }
                            else
                            {
                                itemModCount = 3;
                                Debug.LogError($"[EQUIPMENT-INIT] PARTIALBUILT JUST-SEASONAL FIGHTER - modCount=3");
                            }
                        }
                        else if (gcLower.Contains("ranger"))
                        {
                            if (gcLower.Contains("helm"))
                            {
                                itemModCount = 3;
                                Debug.LogError($"[EQUIPMENT-INIT] PARTIALBUILT JUST-SEASONAL RANGER HELM - modCount=3");
                            }
                            else
                            {
                                itemModCount = 4;
                                Debug.LogError($"[EQUIPMENT-INIT] PARTIALBUILT JUST-SEASONAL RANGER - modCount=4");
                            }
                        }
                        else if (gcLower.Contains("mage"))
                        {
                            if (gcLower.Contains("helm"))
                            {
                                itemModCount = 3;
                                Debug.LogError($"[EQUIPMENT-INIT] PARTIALBUILT JUST-SEASONAL MAGE HELM - modCount=3");
                            }
                            else
                            {
                                itemModCount = 4;
                                Debug.LogError($"[EQUIPMENT-INIT] PARTIALBUILT JUST-SEASONAL MAGE - modCount=4");
                            }
                        }
                        else
                        {
                            itemModCount = 4;
                            Debug.LogError($"[EQUIPMENT-INIT] PARTIALBUILT JUST-SEASONAL DEFAULT - modCount=4");
                        }
                    }
                    else if (hasUniqueSeasonal)
                    {
                        if (gcLower.Contains("fighter"))
                        {
                            if (gcLower.Contains("shield"))
                            {
                                itemModCount = 4;
                                Debug.LogError($"[EQUIPMENT-INIT] PARTIALBUILT UNIQUESEASONAL FIGHTER SHIELD - modCount=4");
                            }
                            else
                            {
                                itemModCount = 3;
                                Debug.LogError($"[EQUIPMENT-INIT] PARTIALBUILT UNIQUESEASONAL FIGHTER - modCount=3");
                            }
                        }
                        else if (gcLower.Contains("ranger"))
                        {
                            itemModCount = 3;
                            Debug.LogError($"[EQUIPMENT-INIT] PARTIALBUILT UNIQUESEASONAL RANGER - modCount=3");
                        }
                        else
                        {
                            itemModCount = 3;
                            Debug.LogError($"[EQUIPMENT-INIT] PARTIALBUILT UNIQUESEASONAL MAGE - modCount=3");
                        }
                    }
                    else if (hasWishingwell)
                    {
                        if (gcLower.Contains("fighter"))
                        {
                            itemModCount = 4;
                            Debug.LogError($"[EQUIPMENT-INIT] PARTIALBUILT WISHINGWELL FIGHTER - modCount=4");
                        }
                        else if (gcLower.Contains("ranger"))
                        {
                            itemModCount = 3;
                            Debug.LogError($"[EQUIPMENT-INIT] PARTIALBUILT WISHINGWELL RANGER - modCount=3");
                        }
                        else if (gcLower.Contains("mage"))
                        {
                            if (gcLower.Contains("helm"))
                            {
                                itemModCount = 4;
                                Debug.LogError($"[EQUIPMENT-INIT] PARTIALBUILT WISHINGWELL MAGE HELM - modCount=4");
                            }
                            else
                            {
                                itemModCount = 3;
                                Debug.LogError($"[EQUIPMENT-INIT] PARTIALBUILT WISHINGWELL MAGE - modCount=3");
                            }
                        }
                        else
                        {
                            itemModCount = 3;
                            Debug.LogError($"[EQUIPMENT-INIT] PARTIALBUILT WISHINGWELL DEFAULT - modCount=3");
                        }
                    }
                    else
                    {
                        if (gcLower.Contains("fighter"))
                        {
                            itemModCount = 4;
                            Debug.LogError($"[EQUIPMENT-INIT] PARTIALBUILT UNIQUE FIGHTER - modCount=4");
                        }
                        else if (gcLower.Contains("ranger"))
                        {
                            itemModCount = 3;
                            Debug.LogError($"[EQUIPMENT-INIT] PARTIALBUILT UNIQUE RANGER - modCount=3");
                        }
                        else
                        {
                            itemModCount = 3;
                            Debug.LogError($"[EQUIPMENT-INIT] PARTIALBUILT UNIQUE MAGE - modCount=3");
                        }
                    }
                }
                else if (isPrebuilt)
                {
                    if (hasMythicInName)
                    {
                        if (gcLower.Contains("mythic002"))
                        {
                            if (gcLower.Contains("helm"))
                            {
                                itemModCount = 6;
                                Debug.LogError($"[EQUIPMENT-INIT] PREBUILT MYTHIC002 HELM - modCount=6");
                            }
                            else
                            {
                                itemModCount = 5;
                                Debug.LogError($"[EQUIPMENT-INIT] PREBUILT MYTHIC002 - modCount=5");
                            }
                        }
                        else if (gcLower.Contains("mythic003"))
                        {
                            if (gcLower.Contains("helm"))
                            {
                                itemModCount = 6;
                                Debug.LogError($"[EQUIPMENT-INIT] PREBUILT MYTHIC003 HELM - modCount=6");
                            }
                            else
                            {
                                itemModCount = 5;
                                Debug.LogError($"[EQUIPMENT-INIT] PREBUILT MYTHIC003 - modCount=5");
                            }
                        }
                        else
                        {
                            if (gcLower.Contains("fighter"))
                            {
                                itemModCount = 5;
                                Debug.LogError($"[EQUIPMENT-INIT] PREBUILT MYTHIC001 FIGHTER - modCount=5");
                            }
                            else if (gcLower.Contains("ranger"))
                            {
                                itemModCount = 6;
                                Debug.LogError($"[EQUIPMENT-INIT] PREBUILT MYTHIC001 RANGER - modCount=6");
                            }
                            else if (gcLower.Contains("mage"))
                            {
                                itemModCount = 5;
                                Debug.LogError($"[EQUIPMENT-INIT] PREBUILT MYTHIC001 MAGE - modCount=5");
                            }
                            else
                            {
                                itemModCount = 5;
                                Debug.LogError($"[EQUIPMENT-INIT] PREBUILT MYTHIC001 DEFAULT - modCount=5");
                            }
                        }
                    }
                    else if (hasMythicSeasonal)
                    {
                        if (gcLower.Contains("fighter"))
                        {
                            if (gcLower.Contains("helm"))
                            {
                                itemModCount = 6;
                                Debug.LogError($"[EQUIPMENT-INIT] PREBUILT MYTHICSEASONAL FIGHTER HELM - modCount=6");
                            }
                            else if (gcLower.Contains("shield"))
                            {
                                itemModCount = 6;
                                Debug.LogError($"[EQUIPMENT-INIT] PREBUILT MYTHICSEASONAL FIGHTER SHIELD - modCount=6");
                            }
                            else
                            {
                                itemModCount = 5;
                                Debug.LogError($"[EQUIPMENT-INIT] PREBUILT MYTHICSEASONAL FIGHTER - modCount=5");
                            }
                        }
                        else if (gcLower.Contains("ranger"))
                        {
                            if (gcLower.Contains("helm"))
                            {
                                itemModCount = 6;
                                Debug.LogError($"[EQUIPMENT-INIT] PREBUILT MYTHICSEASONAL RANGER HELM - modCount=6");
                            }
                            else if (gcLower.Contains("shield"))
                            {
                                itemModCount = 6;
                                Debug.LogError($"[EQUIPMENT-INIT] PREBUILT MYTHICSEASONAL RANGER SHIELD - modCount=6");
                            }
                            else
                            {
                                itemModCount = 5;
                                Debug.LogError($"[EQUIPMENT-INIT] PREBUILT MYTHICSEASONAL RANGER - modCount=5");
                            }
                        }
                        else if (gcLower.Contains("mage"))
                        {
                            if (gcLower.Contains("helm"))
                            {
                                itemModCount = 6;
                                Debug.LogError($"[EQUIPMENT-INIT] PREBUILT MYTHICSEASONAL MAGE HELM - modCount=6");
                            }
                            else if (gcLower.Contains("shield"))
                            {
                                itemModCount = 7;
                                Debug.LogError($"[EQUIPMENT-INIT] PREBUILT MYTHICSEASONAL MAGE SHIELD - modCount=7");
                            }
                            else
                            {
                                itemModCount = 5;
                                Debug.LogError($"[EQUIPMENT-INIT] PREBUILT MYTHICSEASONAL MAGE - modCount=5");
                            }
                        }
                        else
                        {
                            itemModCount = 5;
                            Debug.LogError($"[EQUIPMENT-INIT] PREBUILT MYTHICSEASONAL DEFAULT - modCount=5");
                        }
                    }
                    else if (gcLower.Contains("boss"))
                    {
                        if (gcLower.Contains("fighter"))
                        {
                            if (gcLower.Contains("helm"))
                            {
                                itemModCount = 6;
                                Debug.LogError($"[EQUIPMENT-INIT] PREBUILT BOSS FIGHTER HELM - modCount=6");
                            }
                            else
                            {
                                itemModCount = 7;
                                Debug.LogError($"[EQUIPMENT-INIT] PREBUILT BOSS FIGHTER - modCount=7");
                            }
                        }
                        else if (gcLower.Contains("ranger"))
                        {
                            if (gcLower.Contains("helm"))
                            {
                                itemModCount = 6;
                                Debug.LogError($"[EQUIPMENT-INIT] PREBUILT BOSS RANGER HELM - modCount=6");
                            }
                            else
                            {
                                itemModCount = 7;
                                Debug.LogError($"[EQUIPMENT-INIT] PREBUILT BOSS RANGER - modCount=7");
                            }
                        }
                        else if (gcLower.Contains("mage"))
                        {
                            if (gcLower.Contains("shield"))
                            {
                                itemModCount = 6;
                                Debug.LogError($"[EQUIPMENT-INIT] PREBUILT BOSS MAGE SHIELD - modCount=6");
                            }
                            else
                            {
                                itemModCount = 7;
                                Debug.LogError($"[EQUIPMENT-INIT] PREBUILT BOSS MAGE - modCount=7");
                            }
                        }
                        else
                        {
                            itemModCount = 4;
                            Debug.LogError($"[EQUIPMENT-INIT] PREBUILT BOSS DEFAULT - modCount=4");
                        }
                    }
                    else if (hasWishingwell && !gcLower.Contains("fighter"))
                    {
                        if (gcLower.Contains("helm"))
                        {
                            itemModCount = 7;
                            Debug.LogError($"[EQUIPMENT-INIT] PREBUILT WISHINGWELL HELM - modCount=7");
                        }
                        else
                        {
                            itemModCount = 5;
                            Debug.LogError($"[EQUIPMENT-INIT] PREBUILT WISHINGWELL - modCount=5");
                        }
                    }
                    else
                    {
                        itemModCount = itemData?.modCount ?? 4;
                        Debug.LogError($"[EQUIPMENT-INIT] PREBUILT DEFAULT - modCount={itemModCount}");
                    }
                }
                else if (isGeneratedboss)
                {
                    if (gcLower.Contains("fighter"))
                    {
                        if (gcLower.Contains("helm") || gcLower.Contains("shield"))
                        {
                            itemModCount = 6;
                            Debug.LogError($"[EQUIPMENT-INIT] GENERATEDBOSS FIGHTER HELM/SHIELD - modCount=6");
                        }
                        else
                        {
                            itemModCount = 7;
                            Debug.LogError($"[EQUIPMENT-INIT] GENERATEDBOSS FIGHTER - modCount=7");
                        }
                    }
                    else if (gcLower.Contains("ranger"))
                    {
                        if (gcLower.Contains("helm") || gcLower.Contains("shield"))
                        {
                            itemModCount = 6;
                            Debug.LogError($"[EQUIPMENT-INIT] GENERATEDBOSS RANGER HELM/SHIELD - modCount=6");
                        }
                        else
                        {
                            itemModCount = 7;
                            Debug.LogError($"[EQUIPMENT-INIT] GENERATEDBOSS RANGER - modCount=7");
                        }
                    }
                    else if (gcLower.Contains("mage"))
                    {
                        if (gcLower.Contains("helm") || gcLower.Contains("shield"))
                        {
                            itemModCount = 2;
                            Debug.LogError($"[EQUIPMENT-INIT] GENERATEDBOSS MAGE HELM/SHIELD - modCount=2");
                        }
                        else
                        {
                            itemModCount = 2;
                            Debug.LogError($"[EQUIPMENT-INIT] GENERATEDBOSS MAGE - modCount=2");
                        }
                    }
                    else
                    {
                        itemModCount = 4;
                        Debug.LogError($"[EQUIPMENT-INIT] GENERATEDBOSS DEFAULT - modCount=4");
                    }
                }
                else if (isMythic && NativeClass == "Armor")
                {
                    itemModCount = 5;
                    if (gcLower.Contains("chain") && !gcLower.Contains("shield"))
                        itemModCount = 4;
                    else if (gcLower.Contains("crystal"))
                        itemModCount = 6;
                    else if (gcLower.Contains("leather"))
                    {
                        if (gcLower.Contains("shield") || gcLower.Contains("gloves") || gcLower.Contains("helm") || gcLower.Contains("armor"))
                            itemModCount = 6;
                    }
                    else if (gcLower.Contains("scale"))
                        itemModCount = 6;
                    else if (gcLower.Contains("splint"))
                    {
                        if (gcLower.Contains("100"))
                        {
                            itemModCount = 1;
                            Debug.LogError($"[OP5] MYTHIC SPLINT 100 - modCount=1");
                        }
                        else if (gcLower.Contains("101"))
                        {
                            itemModCount = 1;
                            Debug.LogError($"[OP5] MYTHIC SPLINT 101 - modCount=1");
                        }
                        else
                        {
                            itemModCount = 6;
                        }
                    }
                    else if (gcLower.Contains("plate") && gcLower.Contains("shield"))
                    {
                        if (gcLower.Contains("shield3"))
                        {
                            itemModCount = 6;
                            Debug.LogError($"[OP5] MYTHIC PLATE SHIELD FX - modCount=6");
                        }
                        else
                        {
                            itemModCount = 5;
                            Debug.LogError($"[OP5] MYTHIC PLATE SHIELD - modCount=5");
                        }
                    }
                    Debug.LogError($"[OP5] MYTHIC - modCount={itemModCount}");
                }
                else if (isMythic && gcLower.Contains("sword"))
                {
                    if (gcLower.Contains("mythic6") || gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                    {
                        itemModCount = 6;
                        Debug.LogError($"[XXX] MYTHIC SWORD FX - modCount=6");
                    }
                    else
                    {
                        itemModCount = 5;
                        Debug.LogError($"[XXX] MYTHIC SWORD - modCount=5");
                    }
                }
                else if (isMythic && gcLower.Contains("axe"))
                {
                    if (gcLower.Contains("mythic6") || gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                    {
                        itemModCount = 6;
                        Debug.LogError($"[XXX] MYTHIC AXE FX - modCount=6");
                    }
                    else
                    {
                        itemModCount = 5;
                        Debug.LogError($"[XXX] MYTHIC AXE - modCount=5");
                    }
                }
                else if (isMythic && gcLower.Contains("mace"))
                {
                    if (gcLower.Contains("mythic6") || gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                    {
                        itemModCount = 6;
                        Debug.LogError($"[XXX] MYTHIC MACE FX - modCount=6");
                    }
                    else
                    {
                        itemModCount = 5;
                        Debug.LogError($"[XXX] MYTHIC MACE - modCount=5");
                    }
                }
                else if (isMythic && gcLower.Contains("staff"))
                {
                    if (gcLower.Contains("mythic6") || gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                    {
                        itemModCount = 6;
                        Debug.LogError($"[XXX] MYTHIC STAFF FX - modCount=6");
                    }
                    else
                    {
                        itemModCount = 5;
                        Debug.LogError($"[XXX] MYTHIC STAFF - modCount=5");
                    }
                }
                else if (isMythic && gcLower.Contains("pick"))
                {
                    if (gcLower.Contains("mythic6") || gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                    {
                        itemModCount = 6;
                        Debug.LogError($"[XXX] MYTHIC PICK FX - modCount=6");
                    }
                    else
                    {
                        itemModCount = 5;
                        Debug.LogError($"[XXX] MYTHIC PICK - modCount=5");
                    }
                }
                else if (isMythic && gcLower.Contains("club"))
                {
                    if (gcLower.Contains("mythic6") || gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                    {
                        itemModCount = 6;
                        Debug.LogError($"[XXX] MYTHIC CLUB FX - modCount=6");
                    }
                    else
                    {
                        itemModCount = 5;
                        Debug.LogError($"[XXX] MYTHIC CLUB - modCount=5");
                    }
                }
                else if (isMythic && gcLower.Contains("katana"))
                {
                    if (gcLower.Contains("mythic6") || gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                    {
                        itemModCount = 6;
                        Debug.LogError($"[XXX] MYTHIC KATANA FX - modCount=6");
                    }
                    else
                    {
                        itemModCount = 5;
                        Debug.LogError($"[XXX] MYTHIC KATANA - modCount=5");
                    }
                }
                else if (isMythic && gcLower.Contains("polearm"))
                {
                    if (gcLower.Contains("mythic6") || gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                    {
                        itemModCount = 6;
                        Debug.LogError($"[XXX] MYTHIC POLEARM FX - modCount=6");
                    }
                    else
                    {
                        itemModCount = 5;
                        Debug.LogError($"[XXX] MYTHIC POLEARM - modCount=5");
                    }
                }
                else if (isMythic && gcLower.Contains("crossbow"))
                {
                    if (gcLower.Contains("mythic6") || gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                    {
                        itemModCount = 6;
                        Debug.LogError($"[XXX] MYTHIC CROSSBOW FX - modCount=6");
                    }
                    else
                    {
                        itemModCount = 5;
                        Debug.LogError($"[XXX] MYTHIC CROSSBOW - modCount=5");
                    }
                }
                else if (isMythic && gcLower.Contains("bow"))
                {
                    if (gcLower.Contains("mythic6") || gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                    {
                        itemModCount = 6;
                        Debug.LogError($"[XXX] MYTHIC BOW FX - modCount=6");
                    }
                    else
                    {
                        itemModCount = 5;
                        Debug.LogError($"[XXX] MYTHIC BOW - modCount=5");
                    }
                }
                else if (isMythic && gcLower.Contains("weapon"))
                {
                    if (gcLower.Contains("mythic6") || gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                    {
                        itemModCount = 6;
                        Debug.LogError($"[XXX] MYTHIC WEAPON FX - modCount=6");
                    }
                    else
                    {
                        itemModCount = 5;
                        Debug.LogError($"[XXX] MYTHIC WEAPON - modCount=5");
                    }
                }
                else
                {
                    itemModCount = itemData?.modCount ?? 3;
                    if (gcLower.Contains("chain") && !gcLower.Contains("shield"))
                        itemModCount = 1;
                    Debug.LogError($"[EQUIPMENT-INIT] REGULAR - modCount={itemModCount}");
                }

                Debug.LogError($"[EQUIPMENT-INIT] 🔍 DEBUG: GCClass={GCClass}, isMythic={isMythic}, isPartialbuilt={isPartialbuilt}, isGeneratedboss={isGeneratedboss}, hasMythicSeasonal={hasMythicSeasonal}, hasSeasonal={hasSeasonal}, hasUniqueSeasonal={hasUniqueSeasonal}, finalModCount={itemModCount}");

                for (int i = 0; i < itemModCount; i++)
                {
                    writer.WriteByte(0x00);
                }
                Debug.LogError($"[EQUIPMENT-INIT] Wrote {itemModCount} mod slot bytes");

                // ItemModifiers — Normal items (Binder.Mod1) have no attribute children,
                // so the 0x03/0x15/uint32 trailer would corrupt parsing. Write count=0.
                // Superior+ mods extend EnhancementsPAL with children, need full trailer.
                if (writeInitGcLookup >= 0)
                {
                    writer.WriteByte(0x00);
                    Debug.LogError($"[EQUIPMENT-INIT] GC lookup item - no ScaleMod");
                }
                else
                {
                    // Same GetEffectiveRarity fallback as drop / inventory / OP5 — without
                    // it, named-Unique items (`PlatePAL.PlateUniqueArmor5` etc.) have no
                    // dash-suffix → GetTierFromGcType returns Normal → no ScaleMod block →
                    // stream short by 8+|cstring| → OP4 equipment relog desync (tag 97 if
                    // next gcType starts with 'a'). Repro 2026-05-19 after equipping a
                    // Token Master Unique.
                    int writeTier = DungeonRunners.Managers.RarityHelper.GetTierFromGcType(GCClass);
                    var writeRarity = DungeonRunners.Managers.RarityHelper.GetRarityFromTier(writeTier);
                    if (writeRarity == DungeonRunners.Managers.ItemRarity.Normal)
                    {
                        int effective = GetEffectiveRarity();
                        if (effective > 0 && effective < 5)
                            writeRarity = (DungeonRunners.Managers.ItemRarity)effective;
                    }

                    // Path B — armor wire-mod injection (mirrors WriteInitForInventory/Equip)
                    var initArmorMods = DungeonRunners.Data.ItemStatDatabase.Instance.GetItemWireMods(GCClass);
                    if (initArmorMods.Count == 0)
                        initArmorMods = DungeonRunners.Data.ItemStatDatabase.Instance.GetWrapperIGWireMods(GCClass, writeRarity.ToString());

                    if (DungeonRunners.Data.ItemStatDatabase.PathBEnabled && initArmorMods.Count > 0)
                    {
                        var hashes = new List<uint>();
                        var emitted = new List<string>();
                        var skipped = new List<string>();
                        foreach (var (slot, modRef) in initArmorMods.OrderBy(w => w.Slot))
                        {
                            uint h = DungeonRunners.Data.ItemStatDatabase.Instance.GetGCClassHash(modRef);
                            if (h != 0) { hashes.Add(h); emitted.Add($"{modRef}#0x{h:X8}"); }
                            else skipped.Add(modRef);
                        }
                        writer.WriteByte((byte)hashes.Count);
                        foreach (uint h in hashes)
                        {
                            writer.WriteByte(0x04);
                            writer.WriteUInt32(h);
                            writer.WriteByte(0x00);
                        }
                        Debug.LogError($"[IG-INJECT] init-armor {GCClass} rarity={writeRarity} storedRarity={StoredRarity} effective={GetEffectiveRarity()} mods={hashes.Count}/{initArmorMods.Count} phase1ModCount={itemModCount} emitted=[{string.Join(" | ", emitted)}] skipped=[{string.Join(",", skipped)}]");
                    }
                    else if (writeRarity == DungeonRunners.Managers.ItemRarity.Normal)
                    {
                        writer.WriteByte(0x00);  // no ScaleMod children
                        Debug.LogError($"[EQUIPMENT-INIT] Normal item - no ScaleMod");
                    }
                    else
                    {
                        writer.WriteByte(0x01);
                        writer.WriteByte(0xFF);
                        writer.WriteCString(GetModifierGCClass());
                        Debug.LogError($"[EQUIPMENT-INIT] Writing modifier from GetModifierGCClass() rarity={writeRarity}");
                        writer.WriteByte(0x03);
                        writer.WriteByte(0x15);
                        writer.WriteUInt32(0x11111111);
                    }
                }

                Debug.LogError($"[EQUIPMENT-INIT] Wrote ItemModifier");

                // Weapon extra bytes
                if (NativeClass == "MeleeWeapon" ||
                    GCClass.ToLower().Contains("sword") ||
                    GCClass.ToLower().Contains("axe") ||
                    GCClass.ToLower().Contains("mace") ||
                    GCClass.ToLower().Contains("staff") ||
                    GCClass.ToLower().Contains("pick") ||
                    GCClass.ToLower().Contains("club") ||
                    GCClass.ToLower().Contains("katana") ||
                    GCClass.ToLower().Contains("polearm"))
                {
                    writer.WriteUInt16(0x01);
                    writer.WriteByte(0x02);
                    writer.WriteUInt16(0x00);
                    Debug.LogError($"[EQUIPMENT-INIT] Wrote MeleeWeapon extra bytes");
                }
                else if (NativeClass == "RangedWeapon" ||
                    GCClass.ToLower().Contains("crossbow") ||
                    GCClass.ToLower().Contains("bow") ||
                    GCClass.ToLower().Contains("gun") ||
                    GCClass.ToLower().Contains("cannon"))
                {
                    writer.WriteUInt16(0x0000);
                    writer.WriteUInt16(0x0000);
                    Debug.LogError($"[EQUIPMENT-INIT] Wrote RangedWeapon extra bytes");
                }

                int endPos = writer.Position;
                int eqLen = endPos - startPos;
                var eqBuf = writer.ToArray();
                var eqHex = new System.Text.StringBuilder(eqLen * 3);
                for (int hi = 0; hi < eqLen; hi++)
                {
                    if (hi > 0) eqHex.Append(' ');
                    eqHex.Append(eqBuf[startPos + hi].ToString("X2"));
                }
                Debug.LogError($"[EQUIPMENT-INIT] ✅ FULL equipment data written for {GCClass}, bytes={eqLen} hex={eqHex}");
            }
            else if (NativeClass == "ActiveSkill" || NativeClass == "PassiveSkill")
            {
                writer.WriteByte(0x00);
                Debug.LogError($"[SKILL-INIT] ✅ Skill WriteInit complete: {GCClass}");
            }
            else
            {
                Debug.LogError($"[OTHER-INIT] ✅ Other object WriteInit: {NativeClass}");
            }
            Debug.LogError($"[WRITEINT-END] {NativeClass} - {GCClass}");
        }

        //=============================================================================
        // WriteInitWithoutWeaponBytes - SEPARATE LOGIC
        //=============================================================================
        //=============================================================================
        // WriteInitWithoutWeaponBytes - SEPARATE LOGIC
        //=============================================================================
        // ═══════════════════════════════════════════════════════════════════════════════
        // METHOD 3: WriteInitWithoutWeaponBytes (starts at line 1811)
        // ═══════════════════════════════════════════════════════════════════════════════

        public void WriteInitWithoutWeaponBytes(LEWriter writer, int playerLevel)
        {
            Debug.LogError($"[GCOBJECT-WRITEINIT-NO-WEAPON] Writing item WITHOUT weapon bytes: {GCClass}");

            // FORCE CORRECT NATIVECLASS FOR RINGS/AMULETS
            string gcCheck = GCClass.ToLower();
            if (gcCheck.Contains("ring") || gcCheck.Contains("amulet"))
            {
                NativeClass = "Item";
            }

            if (NativeClass == "Armor" || NativeClass == "MeleeWeapon" || NativeClass == "RangedWeapon" || NativeClass == "Item")
            {
                writer.WriteByte(0xFF);
                writer.WriteCString(GetPacketGCClass());

                uint equipSlot = GetPropertyUInt32("Slot");
                if (equipSlot == 0)
                {
                    equipSlot = GetEquipmentSlotFromGCClass();
                }
                writer.WriteUInt32(equipSlot);
                writer.WriteByte(0x00);
                writer.WriteByte(0x00);
                writer.WriteByte(0x01);

                // ═══════════════════════════════════════════════════════════════════════════════
                // FIXED ITEM SERIALIZATION FOR RINGS/AMULETS
                // ═══════════════════════════════════════════════════════════════════════════════
                string gcLower = GCClass.ToLower();
                if (NativeClass == "Item" || gcLower.Contains("ring") || gcLower.Contains("amulet"))
                {
                    bool isAmulet = gcLower.Contains("amulet");

                    if (isAmulet)
                    {
                        bool isMythicAmulet = gcLower.Contains("mythic");
                        int itemLevel = StoredLevel >= 0 ? StoredLevel : (isMythicAmulet ? (playerLevel + 3) : GetItemRequiredLevel());

                        int amuletModCount;
                        List<string> amuletMods;

                        if (isMythicAmulet)
                        {
                            amuletMods = DatabaseLoader.GetAmuletModifiers(GCClass);
                            amuletModCount = amuletMods.Count;
                        }
                        else
                        {
                            // Phase-1 placeholder = 1 across all non-mythic amulet PAL classes
                            // (normalized — was =2 for questamuletpal/uniqueamuletpal but
                            // BaseAmulet has only the inherited Item flag byte).
                            amuletModCount = 1;
                            amuletMods = new List<string>();
                        }

                        // Path B — non-mythic amulet wire-mod injection (matches WriteInit / OP5).
                        // Without this, re-equipping an amulet emits zero Phase-2 mods while OP5
                        // emits the full set — tooltip flips between empty and full on zone-switch.
                        if (!isMythicAmulet)
                        {
                            int eqTier = DungeonRunners.Managers.RarityHelper.GetTierFromGcType(GCClass);
                            var eqRarity = DungeonRunners.Managers.RarityHelper.GetRarityFromTier(eqTier);
                            if (eqRarity == DungeonRunners.Managers.ItemRarity.Normal)
                            {
                                int effective = GetEffectiveRarity();
                                if (effective > 0 && effective < 5)
                                    eqRarity = (DungeonRunners.Managers.ItemRarity)effective;
                            }
                            var pbAmuletWire = DungeonRunners.Data.ItemStatDatabase.Instance.GetItemWireMods(GCClass);
                            if (pbAmuletWire.Count == 0)
                                pbAmuletWire = DungeonRunners.Data.ItemStatDatabase.Instance.GetWrapperIGWireMods(GCClass, eqRarity.ToString());
                            if (DungeonRunners.Data.ItemStatDatabase.PathBEnabled && pbAmuletWire.Count > 0)
                            {
                                foreach (var (_, modRef) in pbAmuletWire.OrderBy(w => w.Slot))
                                    amuletMods.Add(modRef);
                            }
                        }

                        Debug.LogError($"[NOWEAPON-AMULET] GCClass: {GCClass}, Level: {itemLevel}, ModCount: {amuletModCount}, Mythic: {isMythicAmulet}, PathBMods: {amuletMods.Count}");

                        writer.WriteByte((byte)itemLevel);

                        if (amuletModCount <= 0 && amuletMods.Count == 0)
                        {
                            return;
                        }

                        if (isMythicAmulet)
                        {
                            writer.WriteByte(0x00);
                        }

                        for (int m = 0; m < amuletModCount; m++)
                        {
                            writer.WriteByte(0x00);
                        }

                        var resolvedAmuletHashes = new List<uint>(amuletMods.Count);
                        foreach (var mod in amuletMods)
                        {
                            uint amuletHash = DungeonRunners.Data.ItemStatDatabase.Instance.GetGCClassHash(mod);
                            if (amuletHash != 0) resolvedAmuletHashes.Add(amuletHash);
                        }
                        writer.WriteByte((byte)resolvedAmuletHashes.Count);
                        foreach (uint amuletHash in resolvedAmuletHashes)
                        {
                            writer.WriteByte(0x04);
                            writer.WriteUInt32(amuletHash);
                            writer.WriteByte(0x00);
                        }
                        if (!isMythicAmulet && resolvedAmuletHashes.Count > 0)
                            Debug.LogError($"[IG-INJECT] noweap-amulet {GCClass} mods={resolvedAmuletHashes.Count}");

                        return;
                    }
                    else
                    {
                        bool isMythicRing = gcLower.Contains("mythic");
                        int itemLevel = StoredLevel >= 0 ? StoredLevel : (isMythicRing ? (playerLevel + 3) : GetItemRequiredLevel());

                        int ringModCount;
                        List<string> ringMods;

                        if (isMythicRing)
                        {
                            ringMods = DatabaseLoader.GetRingModifiers(GCClass);
                            ringModCount = ringMods.Count;
                        }
                        else
                        {
                            // Phase-1 placeholder = 1 across all non-mythic ring PAL classes.
                            ringModCount = 1;
                            ringMods = new List<string>();
                        }

                        // Path B — non-mythic ring wire-mod injection (matches amulet branch above).
                        if (!isMythicRing)
                        {
                            int eqTier = DungeonRunners.Managers.RarityHelper.GetTierFromGcType(GCClass);
                            var eqRarity = DungeonRunners.Managers.RarityHelper.GetRarityFromTier(eqTier);
                            if (eqRarity == DungeonRunners.Managers.ItemRarity.Normal)
                            {
                                int effective = GetEffectiveRarity();
                                if (effective > 0 && effective < 5)
                                    eqRarity = (DungeonRunners.Managers.ItemRarity)effective;
                            }
                            var pbRingWire = DungeonRunners.Data.ItemStatDatabase.Instance.GetItemWireMods(GCClass);
                            if (pbRingWire.Count == 0)
                                pbRingWire = DungeonRunners.Data.ItemStatDatabase.Instance.GetWrapperIGWireMods(GCClass, eqRarity.ToString());
                            if (DungeonRunners.Data.ItemStatDatabase.PathBEnabled && pbRingWire.Count > 0)
                            {
                                foreach (var (_, modRef) in pbRingWire.OrderBy(w => w.Slot))
                                    ringMods.Add(modRef);
                            }
                        }

                        Debug.LogError($"[NOWEAPON-RING] GCClass: {GCClass}, Level: {itemLevel}, ModCount: {ringModCount}, Mythic: {isMythicRing}, PathBMods: {ringMods.Count}");

                        writer.WriteByte((byte)itemLevel);

                        if (ringModCount <= 0 && ringMods.Count == 0)
                        {
                            return;
                        }

                        if (isMythicRing)
                        {
                            writer.WriteByte(0x00);
                        }

                        for (int m = 0; m < ringModCount; m++)
                        {
                            writer.WriteByte(0x00);
                        }

                        var resolvedRingHashes = new List<uint>(ringMods.Count);
                        foreach (var mod in ringMods)
                        {
                            uint ringHash = DungeonRunners.Data.ItemStatDatabase.Instance.GetGCClassHash(mod);
                            if (ringHash != 0) resolvedRingHashes.Add(ringHash);
                        }
                        writer.WriteByte((byte)resolvedRingHashes.Count);
                        foreach (uint ringHash in resolvedRingHashes)
                        {
                            writer.WriteByte(0x04);
                            writer.WriteUInt32(ringHash);
                            writer.WriteByte(0x00);
                        }
                        if (!isMythicRing && resolvedRingHashes.Count > 0)
                            Debug.LogError($"[IG-INJECT] noweap-ring {GCClass} mods={resolvedRingHashes.Count}");

                        return;
                    }
                }

                ItemData itemData = DatabaseLoader.FindItem(GCClass);

                // Detection flags
                bool isMythic = gcLower.Contains("mythic")
                || DungeonRunners.Managers.MerchantManager._mythicModSlots.ContainsKey(
                    gcLower.StartsWith("items.pal.") ? gcLower.Substring(10) : gcLower);
                bool isPartialbuilt = gcLower.Contains("partialbuilt");
                bool isPrebuilt = gcLower.Contains("prebuilt");
                bool isGeneratedboss = gcLower.Contains("generatedboss");
                bool hasMythicSeasonal = gcLower.Contains("mythicseasonal");
                bool hasMythicInName = gcLower.Contains("mythic") && !hasMythicSeasonal;
                bool hasSeasonal = gcLower.Contains("seasonal");
                bool hasWishingwell = gcLower.Contains("wishingwell");
                bool hasUnique = gcLower.Contains("unique");
                bool hasJustSeasonal = hasSeasonal && !gcLower.Contains("mythic") && !hasUnique;
                bool hasUniqueSeasonal = hasUnique && hasSeasonal;

                int level = StoredLevel >= 0 ? StoredLevel : (isMythic ? (playerLevel + 3) : GetItemRequiredLevel());
                writer.WriteByte((byte)level);

                //------------------------------------------
                // FLAG BYTE LOGIC (SEPARATE)
                //------------------------------------------
                bool writeFlag = false;

                if (isMythic && !isPartialbuilt)
                {
                    writeFlag = true;
                }
                else if (isPartialbuilt && hasMythicSeasonal)
                {
                    writeFlag = true;
                }
                else if (isPartialbuilt && hasUniqueSeasonal && !gcLower.Contains("fighter"))
                {
                    writeFlag = true;
                }
                else if (isPrebuilt && hasMythicInName)
                {
                    writeFlag = true;
                }
                else if (isPrebuilt && hasMythicSeasonal)
                {
                    writeFlag = true;
                }
                else if (isPrebuilt && hasWishingwell && !gcLower.Contains("fighter") && !gcLower.Contains("helm"))
                {
                    writeFlag = true;
                }

                // Fallback: NON-MythicPAL items in lookup table always get flag byte (GetOP5ModCount assumes it)
                // MythicPAL items already have correct writeFlag logic above — don't override them
                if (!writeFlag && isMythic && GCClass.IndexOf("MythicPAL", System.StringComparison.OrdinalIgnoreCase) < 0)
                {
                    writeFlag = true;
                }

                if (writeFlag)
                {
                    if ((gcLower.Contains("scale") && !gcLower.Contains("helm") && !gcLower.Contains("armor") && !gcLower.Contains("100") && !gcLower.Contains("101")) ||
                        (gcLower.Contains("splint") && !gcLower.Contains("shoulder") && !gcLower.Contains("armor") && !gcLower.Contains("100") && !gcLower.Contains("101")))
                    {
                        Debug.LogError($"[WRITEINIT-NO-WEAPON] SKIPPED flag for scale/splint");
                        writeFlag = false;
                    }
                }

                bool isWeapon = (NativeClass == "MeleeWeapon" || NativeClass == "RangedWeapon");
                if (writeFlag && (!isWeapon || isMythic))
                {
                    writer.WriteByte(0x00);
                    Debug.LogError($"[EQUIP-WRITEINIT] Wrote FLAG byte (armor only)");
                }

                //------------------------------------------
                // MOD COUNT LOGIC (SEPARATE)
                //------------------------------------------
                int itemModCount;

                // ═══ GC LOOKUP — exact modCount from parsed GC files ═══
                int noWepGcLookup = DungeonRunners.Managers.MerchantManager.GetOP5ModCount(GCClass);
                if (noWepGcLookup >= 0)
                {
                    itemModCount = noWepGcLookup;
                    if (!writeFlag) itemModCount++;  // scale/splint exception removed flag byte, compensate
                    Debug.LogError($"[WRITEINIT-NO-WEAPON] GC LOOKUP: {GCClass} → itemModCount={itemModCount}");
                }
                else if (isPartialbuilt)
                {
                    if (hasMythicSeasonal)
                    {
                        if (gcLower.Contains("fighter"))
                        {
                            if (gcLower.Contains("seasonal002"))
                            {
                                itemModCount = 4;
                                Debug.LogError($"[WRITEINIT-NO-WEAPON] PARTIALBUILT MYTHICSEASONAL002 FIGHTER - modCount=4");
                            }
                            else
                            {
                                itemModCount = 3;
                                Debug.LogError($"[WRITEINIT-NO-WEAPON] PARTIALBUILT MYTHICSEASONAL001 FIGHTER - modCount=3");
                            }
                        }
                        else if (gcLower.Contains("ranger"))
                        {
                            itemModCount = 4;
                            Debug.LogError($"[WRITEINIT-NO-WEAPON] PARTIALBUILT MYTHICSEASONAL RANGER - modCount=4");
                        }
                        else
                        {
                            itemModCount = 2;
                            Debug.LogError($"[WRITEINIT-NO-WEAPON] PARTIALBUILT MYTHICSEASONAL MAGE - modCount=2");
                        }
                    }
                    else if (hasMythicInName)
                    {
                        if (gcLower.Contains("fighter"))
                        {
                            itemModCount = 5;
                            Debug.LogError($"[WRITEINIT-NO-WEAPON] PARTIALBUILT MYTHIC FIGHTER - modCount=5");
                        }
                        else if (gcLower.Contains("ranger"))
                        {
                            itemModCount = 2;
                            Debug.LogError($"[WRITEINIT-NO-WEAPON] PARTIALBUILT MYTHIC RANGER - modCount=2");
                        }
                        else
                        {
                            itemModCount = 2;
                            Debug.LogError($"[WRITEINIT-NO-WEAPON] PARTIALBUILT MYTHIC MAGE - modCount=2");
                        }
                    }
                    else if (hasJustSeasonal)
                    {
                        if (gcLower.Contains("fighter"))
                        {
                            if (gcLower.Contains("helm"))
                            {
                                itemModCount = 3;
                                Debug.LogError($"[WRITEINIT-NO-WEAPON] PARTIALBUILT JUST-SEASONAL FIGHTER HELM - modCount=3");
                            }
                            else
                            {
                                itemModCount = 3;
                                Debug.LogError($"[WRITEINIT-NO-WEAPON] PARTIALBUILT JUST-SEASONAL FIGHTER - modCount=3");
                            }
                        }
                        else if (gcLower.Contains("ranger"))
                        {
                            if (gcLower.Contains("helm"))
                            {
                                itemModCount = 3;
                                Debug.LogError($"[WRITEINIT-NO-WEAPON] PARTIALBUILT JUST-SEASONAL RANGER HELM - modCount=3");
                            }
                            else
                            {
                                itemModCount = 4;
                                Debug.LogError($"[WRITEINIT-NO-WEAPON] PARTIALBUILT JUST-SEASONAL RANGER - modCount=4");
                            }
                        }
                        else if (gcLower.Contains("mage"))
                        {
                            if (gcLower.Contains("helm"))
                            {
                                itemModCount = 3;
                                Debug.LogError($"[WRITEINIT-NO-WEAPON] PARTIALBUILT JUST-SEASONAL MAGE HELM - modCount=3");
                            }
                            else
                            {
                                itemModCount = 4;
                                Debug.LogError($"[WRITEINIT-NO-WEAPON] PARTIALBUILT JUST-SEASONAL MAGE - modCount=4");
                            }
                        }
                        else
                        {
                            itemModCount = 4;
                            Debug.LogError($"[WRITEINIT-NO-WEAPON] PARTIALBUILT JUST-SEASONAL DEFAULT - modCount=4");
                        }
                    }
                    else if (hasUniqueSeasonal)
                    {
                        if (gcLower.Contains("fighter"))
                        {
                            if (gcLower.Contains("shield"))
                            {
                                itemModCount = 4;
                                Debug.LogError($"[WRITEINIT-NO-WEAPON] PARTIALBUILT UNIQUESEASONAL FIGHTER SHIELD - modCount=4");
                            }
                            else
                            {
                                itemModCount = 3;
                                Debug.LogError($"[WRITEINIT-NO-WEAPON] PARTIALBUILT UNIQUESEASONAL FIGHTER - modCount=3");
                            }
                        }
                        else if (gcLower.Contains("ranger"))
                        {
                            itemModCount = 3;
                            Debug.LogError($"[WRITEINIT-NO-WEAPON] PARTIALBUILT UNIQUESEASONAL RANGER - modCount=3");
                        }
                        else
                        {
                            itemModCount = 3;
                            Debug.LogError($"[WRITEINIT-NO-WEAPON] PARTIALBUILT UNIQUESEASONAL MAGE - modCount=3");
                        }
                    }
                    else if (hasWishingwell)
                    {
                        if (gcLower.Contains("fighter"))
                        {
                            itemModCount = 4;
                            Debug.LogError($"[WRITEINIT-NO-WEAPON] PARTIALBUILT WISHINGWELL FIGHTER - modCount=4");
                        }
                        else if (gcLower.Contains("ranger"))
                        {
                            itemModCount = 3;
                            Debug.LogError($"[WRITEINIT-NO-WEAPON] PARTIALBUILT WISHINGWELL RANGER - modCount=3");
                        }
                        else if (gcLower.Contains("mage"))
                        {
                            if (gcLower.Contains("helm"))
                            {
                                itemModCount = 4;
                                Debug.LogError($"[WRITEINIT-NO-WEAPON] PARTIALBUILT WISHINGWELL MAGE HELM - modCount=4");
                            }
                            else
                            {
                                itemModCount = 3;
                                Debug.LogError($"[WRITEINIT-NO-WEAPON] PARTIALBUILT WISHINGWELL MAGE - modCount=3");
                            }
                        }
                        else
                        {
                            itemModCount = 3;
                            Debug.LogError($"[WRITEINIT-NO-WEAPON] PARTIALBUILT WISHINGWELL DEFAULT - modCount=3");
                        }
                    }
                    else
                    {
                        if (gcLower.Contains("fighter"))
                        {
                            itemModCount = 4;
                            Debug.LogError($"[WRITEINIT-NO-WEAPON] PARTIALBUILT UNIQUE FIGHTER - modCount=4");
                        }
                        else if (gcLower.Contains("ranger"))
                        {
                            itemModCount = 3;
                            Debug.LogError($"[WRITEINIT-NO-WEAPON] PARTIALBUILT UNIQUE RANGER - modCount=3");
                        }
                        else
                        {
                            itemModCount = 3;
                            Debug.LogError($"[WRITEINIT-NO-WEAPON] PARTIALBUILT UNIQUE MAGE - modCount=3");
                        }
                    }
                }
                else if (isPrebuilt)
                {
                    if (hasMythicInName)
                    {
                        if (gcLower.Contains("mythic002"))
                        {
                            if (gcLower.Contains("helm"))
                            {
                                itemModCount = 6;
                                Debug.LogError($"[WRITEINIT-NO-WEAPON] PREBUILT MYTHIC002 HELM - modCount=6");
                            }
                            else
                            {
                                itemModCount = 5;
                                Debug.LogError($"[WRITEINIT-NO-WEAPON] PREBUILT MYTHIC002 - modCount=5");
                            }
                        }
                        else if (gcLower.Contains("mythic003"))
                        {
                            if (gcLower.Contains("helm"))
                            {
                                itemModCount = 6;
                                Debug.LogError($"[WRITEINIT-NO-WEAPON] PREBUILT MYTHIC003 HELM - modCount=6");
                            }
                            else
                            {
                                itemModCount = 5;
                                Debug.LogError($"[WRITEINIT-NO-WEAPON] PREBUILT MYTHIC003 - modCount=5");
                            }
                        }
                        else
                        {
                            if (gcLower.Contains("fighter"))
                            {
                                itemModCount = 5;
                                Debug.LogError($"[WRITEINIT-NO-WEAPON] PREBUILT MYTHIC001 FIGHTER - modCount=5");
                            }
                            else if (gcLower.Contains("ranger"))
                            {
                                itemModCount = 6;
                                Debug.LogError($"[WRITEINIT-NO-WEAPON] PREBUILT MYTHIC001 RANGER - modCount=6");
                            }
                            else if (gcLower.Contains("mage"))
                            {
                                itemModCount = 5;
                                Debug.LogError($"[WRITEINIT-NO-WEAPON] PREBUILT MYTHIC001 MAGE - modCount=5");
                            }
                            else
                            {
                                itemModCount = 5;
                                Debug.LogError($"[WRITEINIT-NO-WEAPON] PREBUILT MYTHIC001 DEFAULT - modCount=5");
                            }
                        }
                    }
                    else if (hasMythicSeasonal)
                    {
                        if (gcLower.Contains("fighter"))
                        {
                            if (gcLower.Contains("helm"))
                            {
                                itemModCount = 6;
                                Debug.LogError($"[WRITEINIT-NO-WEAPON] PREBUILT MYTHICSEASONAL FIGHTER HELM - modCount=6");
                            }
                            else if (gcLower.Contains("shield"))
                            {
                                itemModCount = 6;
                                Debug.LogError($"[WRITEINIT-NO-WEAPON] PREBUILT MYTHICSEASONAL FIGHTER SHIELD - modCount=6");
                            }
                            else
                            {
                                itemModCount = 5;
                                Debug.LogError($"[WRITEINIT-NO-WEAPON] PREBUILT MYTHICSEASONAL FIGHTER - modCount=5");
                            }
                        }
                        else if (gcLower.Contains("ranger"))
                        {
                            if (gcLower.Contains("helm"))
                            {
                                itemModCount = 6;
                                Debug.LogError($"[WRITEINIT-NO-WEAPON] PREBUILT MYTHICSEASONAL RANGER HELM - modCount=6");
                            }
                            else if (gcLower.Contains("shield"))
                            {
                                itemModCount = 6;
                                Debug.LogError($"[WRITEINIT-NO-WEAPON] PREBUILT MYTHICSEASONAL RANGER SHIELD - modCount=6");
                            }
                            else
                            {
                                itemModCount = 5;
                                Debug.LogError($"[WRITEINIT-NO-WEAPON] PREBUILT MYTHICSEASONAL RANGER - modCount=5");
                            }
                        }
                        else if (gcLower.Contains("mage"))
                        {
                            if (gcLower.Contains("helm"))
                            {
                                itemModCount = 6;
                                Debug.LogError($"[WRITEINIT-NO-WEAPON] PREBUILT MYTHICSEASONAL MAGE HELM - modCount=6");
                            }
                            else if (gcLower.Contains("shield"))
                            {
                                itemModCount = 7;
                                Debug.LogError($"[WRITEINIT-NO-WEAPON] PREBUILT MYTHICSEASONAL MAGE SHIELD - modCount=7");
                            }
                            else
                            {
                                itemModCount = 5;
                                Debug.LogError($"[WRITEINIT-NO-WEAPON] PREBUILT MYTHICSEASONAL MAGE - modCount=5");
                            }
                        }
                        else
                        {
                            itemModCount = 5;
                            Debug.LogError($"[WRITEINIT-NO-WEAPON] PREBUILT MYTHICSEASONAL DEFAULT - modCount=5");
                        }
                    }
                    else if (gcLower.Contains("boss"))
                    {
                        if (gcLower.Contains("fighter"))
                        {
                            if (gcLower.Contains("helm"))
                            {
                                itemModCount = 6;
                                Debug.LogError($"[WRITEINIT-NO-WEAPON] PREBUILT BOSS FIGHTER HELM - modCount=6");
                            }
                            else
                            {
                                itemModCount = 7;
                                Debug.LogError($"[WRITEINIT-NO-WEAPON] PREBUILT BOSS FIGHTER - modCount=7");
                            }
                        }
                        else if (gcLower.Contains("ranger"))
                        {
                            if (gcLower.Contains("helm"))
                            {
                                itemModCount = 6;
                                Debug.LogError($"[WRITEINIT-NO-WEAPON] PREBUILT BOSS RANGER HELM - modCount=6");
                            }
                            else
                            {
                                itemModCount = 7;
                                Debug.LogError($"[WRITEINIT-NO-WEAPON] PREBUILT BOSS RANGER - modCount=7");
                            }
                        }
                        else if (gcLower.Contains("mage"))
                        {
                            if (gcLower.Contains("shield"))
                            {
                                itemModCount = 6;
                                Debug.LogError($"[WRITEINIT-NO-WEAPON] PREBUILT BOSS MAGE SHIELD - modCount=6");
                            }
                            else
                            {
                                itemModCount = 7;
                                Debug.LogError($"[WRITEINIT-NO-WEAPON] PREBUILT BOSS MAGE - modCount=7");
                            }
                        }
                        else
                        {
                            itemModCount = 4;
                            Debug.LogError($"[WRITEINIT-NO-WEAPON] PREBUILT BOSS DEFAULT - modCount=4");
                        }
                    }
                    else if (hasWishingwell && !gcLower.Contains("fighter"))
                    {
                        if (gcLower.Contains("helm"))
                        {
                            itemModCount = 7;
                            Debug.LogError($"[WRITEINIT-NO-WEAPON] PREBUILT WISHINGWELL HELM - modCount=7");
                        }
                        else
                        {
                            itemModCount = 5;
                            Debug.LogError($"[WRITEINIT-NO-WEAPON] PREBUILT WISHINGWELL - modCount=5");
                        }
                    }
                    else
                    {
                        itemModCount = itemData?.modCount ?? 4;
                        Debug.LogError($"[WRITEINIT-NO-WEAPON] PREBUILT DEFAULT - modCount={itemModCount}");
                    }
                }
                else if (isGeneratedboss)
                {
                    if (gcLower.Contains("fighter"))
                    {
                        if (gcLower.Contains("helm") || gcLower.Contains("shield"))
                        {
                            itemModCount = 6;
                            Debug.LogError($"[WRITEINIT-NO-WEAPON] GENERATEDBOSS FIGHTER HELM/SHIELD - modCount=6");
                        }
                        else
                        {
                            itemModCount = 7;
                            Debug.LogError($"[WRITEINIT-NO-WEAPON] GENERATEDBOSS FIGHTER - modCount=7");
                        }
                    }
                    else if (gcLower.Contains("ranger"))
                    {
                        if (gcLower.Contains("helm") || gcLower.Contains("shield"))
                        {
                            itemModCount = 6;
                            Debug.LogError($"[WRITEINIT-NO-WEAPON] GENERATEDBOSS RANGER HELM/SHIELD - modCount=6");
                        }
                        else
                        {
                            itemModCount = 7;
                            Debug.LogError($"[WRITEINIT-NO-WEAPON] GENERATEDBOSS RANGER - modCount=7");
                        }
                    }
                    else if (gcLower.Contains("mage"))
                    {
                        if (gcLower.Contains("helm") || gcLower.Contains("shield"))
                        {
                            itemModCount = 2;
                            Debug.LogError($"[WRITEINIT-NO-WEAPON] GENERATEDBOSS MAGE HELM/SHIELD - modCount=2");
                        }
                        else
                        {
                            itemModCount = 2;
                            Debug.LogError($"[WRITEINIT-NO-WEAPON] GENERATEDBOSS MAGE - modCount=2");
                        }
                    }
                    else
                    {
                        itemModCount = 4;
                        Debug.LogError($"[WRITEINIT-NO-WEAPON] GENERATEDBOSS DEFAULT - modCount=4");
                    }
                }
                else if (isMythic && NativeClass == "Armor")
                {
                    itemModCount = 5;
                    if (gcLower.Contains("chain") && !gcLower.Contains("shield"))
                        itemModCount = 4;
                    else if (gcLower.Contains("crystal"))
                        itemModCount = 6;
                    else if (gcLower.Contains("leather"))
                    {
                        if (gcLower.Contains("shield") || gcLower.Contains("gloves") || gcLower.Contains("helm") || gcLower.Contains("armor"))
                            itemModCount = 6;
                    }
                    else if (gcLower.Contains("scale"))
                        itemModCount = 6;
                    else if (gcLower.Contains("splint"))
                    {
                        if (gcLower.Contains("100"))
                        {
                            itemModCount = 1;
                            Debug.LogError($"[OP5] MYTHIC SPLINT 100 - modCount=1");
                        }
                        else if (gcLower.Contains("101"))
                        {
                            itemModCount = 1;
                            Debug.LogError($"[OP5] MYTHIC SPLINT 101 - modCount=1");
                        }
                        else
                        {
                            itemModCount = 6;
                        }
                    }
                    else if (gcLower.Contains("plate") && gcLower.Contains("shield"))
                    {
                        if (gcLower.Contains("shield3"))
                        {
                            itemModCount = 6;
                            Debug.LogError($"[OP5] MYTHIC PLATE SHIELD FX - modCount=6");
                        }
                        else
                        {
                            itemModCount = 5;
                            Debug.LogError($"[OP5] MYTHIC PLATE SHIELD - modCount=5");
                        }
                    }
                    Debug.LogError($"[OP5] MYTHIC - modCount={itemModCount}");
                }
                else if (isMythic && gcLower.Contains("sword"))
                {
                    if (gcLower.Contains("mythic6") || gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                    {
                        itemModCount = 6;
                        Debug.LogError($"[XXX] MYTHIC SWORD FX - modCount=6");
                    }
                    else
                    {
                        itemModCount = 5;
                        Debug.LogError($"[XXX] MYTHIC SWORD - modCount=5");
                    }
                }
                else if (isMythic && gcLower.Contains("axe"))
                {
                    if (gcLower.Contains("mythic6") || gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                    {
                        itemModCount = 6;
                        Debug.LogError($"[XXX] MYTHIC AXE FX - modCount=6");
                    }
                    else
                    {
                        itemModCount = 5;
                        Debug.LogError($"[XXX] MYTHIC AXE - modCount=5");
                    }
                }
                else if (isMythic && gcLower.Contains("mace"))
                {
                    if (gcLower.Contains("mythic6") || gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                    {
                        itemModCount = 6;
                        Debug.LogError($"[XXX] MYTHIC MACE FX - modCount=6");
                    }
                    else
                    {
                        itemModCount = 5;
                        Debug.LogError($"[XXX] MYTHIC MACE - modCount=5");
                    }
                }
                else if (isMythic && gcLower.Contains("staff"))
                {
                    if (gcLower.Contains("mythic6") || gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                    {
                        itemModCount = 6;
                        Debug.LogError($"[XXX] MYTHIC STAFF FX - modCount=6");
                    }
                    else
                    {
                        itemModCount = 5;
                        Debug.LogError($"[XXX] MYTHIC STAFF - modCount=5");
                    }
                }
                else if (isMythic && gcLower.Contains("pick"))
                {
                    if (gcLower.Contains("mythic6") || gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                    {
                        itemModCount = 6;
                        Debug.LogError($"[XXX] MYTHIC PICK FX - modCount=6");
                    }
                    else
                    {
                        itemModCount = 5;
                        Debug.LogError($"[XXX] MYTHIC PICK - modCount=5");
                    }
                }
                else if (isMythic && gcLower.Contains("club"))
                {
                    if (gcLower.Contains("mythic6") || gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                    {
                        itemModCount = 6;
                        Debug.LogError($"[XXX] MYTHIC CLUB FX - modCount=6");
                    }
                    else
                    {
                        itemModCount = 5;
                        Debug.LogError($"[XXX] MYTHIC CLUB - modCount=5");
                    }
                }
                else if (isMythic && gcLower.Contains("katana"))
                {
                    if (gcLower.Contains("mythic6") || gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                    {
                        itemModCount = 6;
                        Debug.LogError($"[XXX] MYTHIC KATANA FX - modCount=6");
                    }
                    else
                    {
                        itemModCount = 5;
                        Debug.LogError($"[XXX] MYTHIC KATANA - modCount=5");
                    }
                }
                else if (isMythic && gcLower.Contains("polearm"))
                {
                    if (gcLower.Contains("mythic6") || gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                    {
                        itemModCount = 6;
                        Debug.LogError($"[XXX] MYTHIC POLEARM FX - modCount=6");
                    }
                    else
                    {
                        itemModCount = 5;
                        Debug.LogError($"[XXX] MYTHIC POLEARM - modCount=5");
                    }
                }
                else if (isMythic && gcLower.Contains("crossbow"))
                {
                    if (gcLower.Contains("mythic6") || gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                    {
                        itemModCount = 6;
                        Debug.LogError($"[XXX] MYTHIC CROSSBOW FX - modCount=6");
                    }
                    else
                    {
                        itemModCount = 5;
                        Debug.LogError($"[XXX] MYTHIC CROSSBOW - modCount=5");
                    }
                }
                else if (isMythic && gcLower.Contains("bow"))
                {
                    if (gcLower.Contains("mythic6") || gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                    {
                        itemModCount = 6;
                        Debug.LogError($"[XXX] MYTHIC BOW FX - modCount=6");
                    }
                    else
                    {
                        itemModCount = 5;
                        Debug.LogError($"[XXX] MYTHIC BOW - modCount=5");
                    }
                }
                else if (isMythic && gcLower.Contains("weapon"))
                {
                    if (gcLower.Contains("mythic6") || gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                    {
                        itemModCount = 6;
                        Debug.LogError($"[XXX] MYTHIC WEAPON FX - modCount=6");
                    }
                    else
                    {
                        itemModCount = 5;
                        Debug.LogError($"[XXX] MYTHIC WEAPON - modCount=5");
                    }
                }
                else
                {
                    itemModCount = itemData?.modCount ?? 3;
                    if (gcLower.Contains("chain") && !gcLower.Contains("shield"))
                        itemModCount = 1;
                    Debug.LogError($"[WRITEINIT-NO-WEAPON] REGULAR - modCount={itemModCount}");
                }

                Debug.LogError($"[WRITEINIT-NO-WEAPON] 🔍 DEBUG: GCClass={GCClass}, isMythic={isMythic}, isPartialbuilt={isPartialbuilt}, isGeneratedboss={isGeneratedboss}, hasMythicSeasonal={hasMythicSeasonal}, hasSeasonal={hasSeasonal}, hasUniqueSeasonal={hasUniqueSeasonal}, finalModCount={itemModCount}");

                for (int i = 0; i < itemModCount; i++)
                {
                    writer.WriteByte(0x00);
                }

                // ItemModifiers — Normal vs colored rarity
                if (noWepGcLookup >= 0)
                {
                    writer.WriteByte(0x00);
                    Debug.LogError($"[WRITEINIT-NO-WEAPON] GC lookup item - no ScaleMod");
                }
                else
                {
                    // GetEffectiveRarity fallback — see matching comment at WriteInit
                    // weapon-bearing site. Named-Unique items lack a -N suffix so this
                    // would otherwise write Normal and drop the ScaleMod block.
                    int noWepTier = DungeonRunners.Managers.RarityHelper.GetTierFromGcType(GCClass);
                    var noWepRarity = DungeonRunners.Managers.RarityHelper.GetRarityFromTier(noWepTier);
                    if (noWepRarity == DungeonRunners.Managers.ItemRarity.Normal)
                    {
                        int effective = GetEffectiveRarity();
                        if (effective > 0 && effective < 5)
                            noWepRarity = (DungeonRunners.Managers.ItemRarity)effective;
                    }

                    // Path B — wire-mod injection (matches WriteInit / OP5 / INV-RESTORE / etc).
                    // WriteInitWithoutWeaponBytes is called by EquipmentHandler.HandleAddEquippedItem
                    // when the player drops a cursor item onto an equipment slot. Without Path B
                    // here, re-equipping after unequip emits a single ScaleMod while OP5 (zone-in)
                    // emits the full wire-mod set — tooltips visibly change between re-equip and
                    // the next zone-switch.
                    var noWepWireMods = DungeonRunners.Data.ItemStatDatabase.Instance.GetItemWireMods(GCClass);
                    if (noWepWireMods.Count == 0)
                        noWepWireMods = DungeonRunners.Data.ItemStatDatabase.Instance.GetWrapperIGWireMods(GCClass, noWepRarity.ToString());

                    if (DungeonRunners.Data.ItemStatDatabase.PathBEnabled && noWepWireMods.Count > 0)
                    {
                        var hashes = new List<uint>();
                        foreach (var (slot, modRef) in noWepWireMods.OrderBy(w => w.Slot))
                        {
                            uint h = DungeonRunners.Data.ItemStatDatabase.Instance.GetGCClassHash(modRef);
                            if (h != 0) hashes.Add(h);
                        }
                        writer.WriteByte((byte)hashes.Count);
                        foreach (uint h in hashes)
                        {
                            writer.WriteByte(0x04);
                            writer.WriteUInt32(h);
                            writer.WriteByte(0x00);
                        }
                        Debug.LogError($"[IG-INJECT] noweap-armor {GCClass} rarity={noWepRarity} mods={hashes.Count} phase1ModCount={itemModCount}");
                    }
                    else if (noWepRarity == DungeonRunners.Managers.ItemRarity.Normal)
                    {
                        writer.WriteByte(0x00);
                    }
                    else
                    {
                        writer.WriteByte(0x01);
                        writer.WriteByte(0xFF);
                        writer.WriteCString(GetModifierGCClass());
                        writer.WriteByte(0x03);
                        writer.WriteByte(0x15);
                        writer.WriteUInt32(0x11111111);
                    }
                }
                Debug.LogError($"[EQUIPMENT-INIT-NO-WEAPON] ✅ Written WITHOUT weapon bytes for {GCClass}");
            }
            else
            {
                WriteInit(writer, playerLevel);
            }
        }

        //=============================================================================
        // WriteInitForInventory - SEPARATE LOGIC
        //=============================================================================
        //=============================================================================
        // WriteInitForInventory - SEPARATE LOGIC
        //=============================================================================
        // ═══════════════════════════════════════════════════════════════════════════════
        // METHOD 4: WriteInitForInventory (starts at line 2616)
        // ═══════════════════════════════════════════════════════════════════════════════

        // ═══════════════════════════════════════════════════════════════════════════════
        // METHOD 4: WriteInitForInventory (starts at line 2616)
        // ═══════════════════════════════════════════════════════════════════════════════

        public void WriteInitForInventory(LEWriter writer, byte posX, byte posY, uint inventorySlot, int playerLevel)
        {
            Debug.LogError($"[DEBUG-GCCLASS] GCClass = '{GCClass}'");
            Debug.LogError($"[INVENTORY-WRITEINIT] Writing item at position ({posX}, {posY}), slot={inventorySlot}: {GCClass}");

            // FORCE CORRECT NATIVECLASS FOR RINGS/AMULETS
            string gcCheck = GCClass.ToLower();
            if (gcCheck.Contains("ring") || gcCheck.Contains("amulet"))
            {
                NativeClass = "Item";
            }

            if (NativeClass == "Armor" || NativeClass == "MeleeWeapon" || NativeClass == "RangedWeapon" || NativeClass == "Item")
            {
                writer.WriteByte(0xFF);
                writer.WriteCString(GetPacketGCClass());
                writer.WriteUInt32(inventorySlot);
                writer.WriteByte(posX);
                writer.WriteByte(posY);
                writer.WriteByte(0x01);  // Count

                // ═══════════════════════════════════════════════════════════════════════════════
                // FIXED ITEM SERIALIZATION FOR RINGS/AMULETS
                // ═══════════════════════════════════════════════════════════════════════════════
                string gcLower = GCClass.ToLower();
                if (NativeClass == "Item" || gcLower.Contains("ring") || gcLower.Contains("amulet"))

                {
                    bool isAmulet = gcLower.Contains("amulet");

                    // Ghidra-verified jewelry layout: BaseRing / BaseAmulet have ZERO
                    // inherited ItemModifier children, so Phase 1 of ReadChildData<ItemModifier>
                    // (@ 0x00583920) reads 0 bytes for non-mythic rings/amulets. Mythic
                    // *MythicPAL.* classes define Mod1..N inline so Phase 1 reads N bytes.
                    // The Phase 2 trailer is then: 1-byte count + N × (tagByte + class + data).
                    // For non-mythic Unique jewelry we emit a single ScaleMod child (count=1
                    // + 0xFF + scaleMod cstring + 0x03 + 0x15 + UInt32 0x11111111) so the
                    // item renders at its real tier instead of the white "Normal" fallback.
                    if (isAmulet)
                    {
                        bool isMythicAmulet = gcLower.Contains("mythic");
                        int itemLevel = StoredLevel >= 0 ? StoredLevel : (isMythicAmulet ? playerLevel : GetItemRequiredLevel());

                        writer.WriteByte((byte)itemLevel);

                        if (isMythicAmulet)
                        {
                            var amuletMods = DatabaseLoader.GetAmuletModifiers(GCClass);
                            int amuletModCount = amuletMods.Count;
                            Debug.LogError($"[INV-AMULET-MYTHIC] GCClass: {GCClass}, Level: {itemLevel}, Placeholders: {amuletModCount}");

                            writer.WriteByte(0x00);  // mythic flag
                            for (int m = 0; m < amuletModCount; m++) writer.WriteByte(0x00);

                            var resolvedAmuletHashes = new List<uint>(amuletMods.Count);
                            foreach (var mod in amuletMods)
                            {
                                uint amuletHash = DungeonRunners.Data.ItemStatDatabase.Instance.GetGCClassHash(mod);
                                if (amuletHash != 0) resolvedAmuletHashes.Add(amuletHash);
                            }
                            writer.WriteByte((byte)resolvedAmuletHashes.Count);
                            foreach (uint amuletHash in resolvedAmuletHashes)
                            {
                                writer.WriteByte(0x04);
                                writer.WriteUInt32(amuletHash);
                                writer.WriteByte(0x00);
                            }
                        }
                        else
                        {
                            // Non-mythic amulet: 1 Phase-1 placeholder (inherited from Item
                            // base class) + Phase 2. Path B injects N hash-tagged mods when wire
                            // mods are available; otherwise falls back to single ScaleMod.
                            int invTier = DungeonRunners.Managers.RarityHelper.GetTierFromGcType(GCClass);
                            var invRarity = DungeonRunners.Managers.RarityHelper.GetRarityFromTier(invTier);
                            if (invRarity == DungeonRunners.Managers.ItemRarity.Normal)
                            {
                                int effective = GetEffectiveRarity();
                                if (effective > 0 && effective < 5)
                                    invRarity = (DungeonRunners.Managers.ItemRarity)effective;
                            }
                            Debug.LogError($"[INV-AMULET-NONMYTHIC] GCClass: {GCClass}, Level: {itemLevel}, Rarity: {invRarity}");
                            writer.WriteByte(0x00);  // Phase 1 placeholder (inherited)

                            var amuletWireMods = DungeonRunners.Data.ItemStatDatabase.Instance.GetItemWireMods(GCClass);
                            if (amuletWireMods.Count == 0)
                                amuletWireMods = DungeonRunners.Data.ItemStatDatabase.Instance.GetWrapperIGWireMods(GCClass, invRarity.ToString());

                            if (DungeonRunners.Data.ItemStatDatabase.PathBEnabled && amuletWireMods.Count > 0)
                            {
                                var hashes = new List<uint>();
                                foreach (var (slot, modRef) in amuletWireMods.OrderBy(w => w.Slot))
                                {
                                    uint h = DungeonRunners.Data.ItemStatDatabase.Instance.GetGCClassHash(modRef);
                                    if (h != 0) hashes.Add(h);
                                }
                                writer.WriteByte((byte)hashes.Count);
                                foreach (uint h in hashes)
                                {
                                    writer.WriteByte(0x04);
                                    writer.WriteUInt32(h);
                                    writer.WriteByte(0x00);
                                }
                                Debug.LogError($"[IG-INJECT] inv-amulet {GCClass} rarity={invRarity} mods={hashes.Count}");
                            }
                            else if (invRarity == DungeonRunners.Managers.ItemRarity.Normal)
                            {
                                writer.WriteByte(0x00);  // Phase 2 count = 0, no ScaleMod
                            }
                            else
                            {
                                writer.WriteByte(0x01);  // Phase 2 count = 1
                                writer.WriteByte(0xFF);
                                writer.WriteCString(GetModifierGCClass());
                                writer.WriteByte(0x03);
                                writer.WriteByte(0x15);
                                writer.WriteUInt32(0x11111111);
                            }
                        }
                        return;
                    }
                    else
                    {
                        bool isMythicRing = gcLower.Contains("mythic");
                        int itemLevel = StoredLevel >= 0 ? StoredLevel : (isMythicRing ? playerLevel : GetItemRequiredLevel());

                        writer.WriteByte((byte)itemLevel);

                        if (isMythicRing)
                        {
                            var ringMods = DatabaseLoader.GetRingModifiers(GCClass);
                            int ringModCount = ringMods.Count;
                            Debug.LogError($"[INV-RING-MYTHIC] GCClass: {GCClass}, Level: {itemLevel}, Placeholders: {ringModCount}");

                            writer.WriteByte(0x00);  // mythic flag
                            for (int m = 0; m < ringModCount; m++) writer.WriteByte(0x00);

                            var resolvedRingHashes = new List<uint>(ringMods.Count);
                            foreach (var mod in ringMods)
                            {
                                uint ringHash = DungeonRunners.Data.ItemStatDatabase.Instance.GetGCClassHash(mod);
                                if (ringHash != 0) resolvedRingHashes.Add(ringHash);
                            }
                            writer.WriteByte((byte)resolvedRingHashes.Count);
                            foreach (uint ringHash in resolvedRingHashes)
                            {
                                writer.WriteByte(0x04);
                                writer.WriteUInt32(ringHash);
                                writer.WriteByte(0x00);
                            }
                        }
                        else
                        {
                            // Non-mythic ring: 1 Phase-1 placeholder + Path B injection (with
                            // ScaleMod fallback when no wire mods are available).
                            int invTier = DungeonRunners.Managers.RarityHelper.GetTierFromGcType(GCClass);
                            var invRarity = DungeonRunners.Managers.RarityHelper.GetRarityFromTier(invTier);
                            if (invRarity == DungeonRunners.Managers.ItemRarity.Normal)
                            {
                                int effective = GetEffectiveRarity();
                                if (effective > 0 && effective < 5)
                                    invRarity = (DungeonRunners.Managers.ItemRarity)effective;
                            }
                            Debug.LogError($"[INV-RING-NONMYTHIC] GCClass: {GCClass}, Level: {itemLevel}, Rarity: {invRarity}");
                            writer.WriteByte(0x00);  // Phase 1 placeholder (inherited)

                            var ringWireMods = DungeonRunners.Data.ItemStatDatabase.Instance.GetItemWireMods(GCClass);
                            if (ringWireMods.Count == 0)
                                ringWireMods = DungeonRunners.Data.ItemStatDatabase.Instance.GetWrapperIGWireMods(GCClass, invRarity.ToString());

                            if (DungeonRunners.Data.ItemStatDatabase.PathBEnabled && ringWireMods.Count > 0)
                            {
                                var hashes = new List<uint>();
                                foreach (var (slot, modRef) in ringWireMods.OrderBy(w => w.Slot))
                                {
                                    uint h = DungeonRunners.Data.ItemStatDatabase.Instance.GetGCClassHash(modRef);
                                    if (h != 0) hashes.Add(h);
                                }
                                writer.WriteByte((byte)hashes.Count);
                                foreach (uint h in hashes)
                                {
                                    writer.WriteByte(0x04);
                                    writer.WriteUInt32(h);
                                    writer.WriteByte(0x00);
                                }
                                Debug.LogError($"[IG-INJECT] inv-ring {GCClass} rarity={invRarity} mods={hashes.Count}");
                            }
                            else if (invRarity == DungeonRunners.Managers.ItemRarity.Normal)
                            {
                                writer.WriteByte(0x00);  // Phase 2 count = 0, no ScaleMod
                            }
                            else
                            {
                                writer.WriteByte(0x01);  // Phase 2 count = 1
                                writer.WriteByte(0xFF);
                                writer.WriteCString(GetModifierGCClass());
                                writer.WriteByte(0x03);
                                writer.WriteByte(0x15);
                                writer.WriteUInt32(0x11111111);
                            }
                        }
                        return;
                    }
                }

                ItemData itemData = DatabaseLoader.FindItem(GCClass);

                // Detection flags
                bool isMythic = gcLower.Contains("mythic")
                || DungeonRunners.Managers.MerchantManager._mythicModSlots.ContainsKey(
                    gcLower.StartsWith("items.pal.") ? gcLower.Substring(10) : gcLower);
                bool isPartialbuilt = gcLower.Contains("partialbuilt");
                bool isPrebuilt = gcLower.Contains("prebuilt");
                bool isGeneratedboss = gcLower.Contains("generatedboss");
                bool hasMythicSeasonal = gcLower.Contains("mythicseasonal");
                bool hasMythicInName = gcLower.Contains("mythic") && !hasMythicSeasonal;
                bool hasSeasonal = gcLower.Contains("seasonal");
                bool hasWishingwell = gcLower.Contains("wishingwell");
                bool hasUnique = gcLower.Contains("unique");
                bool hasJustSeasonal = hasSeasonal && !gcLower.Contains("mythic") && !hasUnique;
                bool hasUniqueSeasonal = hasUnique && hasSeasonal;

                int level = StoredLevel >= 0 ? StoredLevel : playerLevel;
                writer.WriteByte((byte)level);

                //------------------------------------------
                // FLAG BYTE LOGIC (SEPARATE)
                //------------------------------------------
                bool writeFlag = false;

                if (isMythic && !isPartialbuilt)
                {
                    writeFlag = true;
                }
                else if (isPartialbuilt && hasMythicSeasonal)
                {
                    writeFlag = true;
                }
                else if (isPartialbuilt && hasUniqueSeasonal && !gcLower.Contains("fighter"))
                {
                    writeFlag = true;
                }
                else if (isPrebuilt && hasMythicInName)
                {
                    writeFlag = true;
                }
                else if (isPrebuilt && hasMythicSeasonal)
                {
                    writeFlag = true;
                }
                else if (isPrebuilt && hasWishingwell && !gcLower.Contains("fighter") && !gcLower.Contains("helm"))
                {
                    writeFlag = true;
                }

                // Fallback: NON-MythicPAL items in lookup table always get flag byte (GetOP5ModCount assumes it)
                // MythicPAL items already have correct writeFlag logic above — don't override them
                if (!writeFlag && isMythic && GCClass.IndexOf("MythicPAL", System.StringComparison.OrdinalIgnoreCase) < 0)
                {
                    writeFlag = true;
                }

                if (writeFlag)
                {
                    if ((gcLower.Contains("scale") && !gcLower.Contains("helm") && !gcLower.Contains("armor") && !gcLower.Contains("100") && !gcLower.Contains("101")) ||
                        (gcLower.Contains("splint") && !gcLower.Contains("shoulder") && !gcLower.Contains("armor") && !gcLower.Contains("100") && !gcLower.Contains("101")))
                    {
                        Debug.LogError($"[INVENTORY-WRITEINIT] SKIPPED flag for scale/splint");
                        writeFlag = false;
                    }
                }

                bool isWeapon = (NativeClass == "MeleeWeapon" || NativeClass == "RangedWeapon");
                if (writeFlag && (!isWeapon || isMythic))
                {
                    writer.WriteByte(0x00);
                    Debug.LogError($"[EQUIP-WRITEINIT] Wrote FLAG byte (armor only)");
                }

                //------------------------------------------
                // MOD COUNT LOGIC (SEPARATE)
                //------------------------------------------
                int itemModCount;

                // ═══ GC LOOKUP — exact modCount from parsed GC files ═══
                int invGcLookup = DungeonRunners.Managers.MerchantManager.GetOP5ModCount(GCClass);
                if (invGcLookup >= 0)
                {
                    itemModCount = invGcLookup;
                    if (!writeFlag) itemModCount++;  // scale/splint exception removed flag byte, compensate
                    Debug.LogError($"[INVENTORY-WRITEINIT] GC LOOKUP: {GCClass} → itemModCount={itemModCount}");
                }
                else if (isPartialbuilt)
                {
                    if (hasMythicSeasonal)
                    {
                        if (gcLower.Contains("fighter"))
                        {
                            if (gcLower.Contains("seasonal002"))
                            {
                                itemModCount = 4;
                                Debug.LogError($"[INVENTORY-WRITEINIT] PARTIALBUILT MYTHICSEASONAL002 FIGHTER - modCount=4");
                            }
                            else
                            {
                                itemModCount = 3;
                                Debug.LogError($"[INVENTORY-WRITEINIT] PARTIALBUILT MYTHICSEASONAL001 FIGHTER - modCount=3");
                            }
                        }
                        else if (gcLower.Contains("ranger"))
                        {
                            itemModCount = 4;
                            Debug.LogError($"[INVENTORY-WRITEINIT] PARTIALBUILT MYTHICSEASONAL RANGER - modCount=4");
                        }
                        else
                        {
                            itemModCount = 2;
                            Debug.LogError($"[INVENTORY-WRITEINIT] PARTIALBUILT MYTHICSEASONAL MAGE - modCount=2");
                        }
                    }
                    else if (hasMythicInName)
                    {
                        if (gcLower.Contains("fighter"))
                        {
                            itemModCount = 5;
                            Debug.LogError($"[INVENTORY-WRITEINIT] PARTIALBUILT MYTHIC FIGHTER - modCount=5");
                        }
                        else if (gcLower.Contains("ranger"))
                        {
                            itemModCount = 2;
                            Debug.LogError($"[INVENTORY-WRITEINIT] PARTIALBUILT MYTHIC RANGER - modCount=2");
                        }
                        else
                        {
                            itemModCount = 2;
                            Debug.LogError($"[INVENTORY-WRITEINIT] PARTIALBUILT MYTHIC MAGE - modCount=2");
                        }
                    }
                    else if (hasJustSeasonal)
                    {
                        if (gcLower.Contains("fighter"))
                        {
                            if (gcLower.Contains("helm"))
                            {
                                itemModCount = 3;
                                Debug.LogError($"[INVENTORY-WRITEINIT] PARTIALBUILT JUST-SEASONAL FIGHTER HELM - modCount=3");
                            }
                            else
                            {
                                itemModCount = 3;
                                Debug.LogError($"[INVENTORY-WRITEINIT] PARTIALBUILT JUST-SEASONAL FIGHTER - modCount=3");
                            }
                        }
                        else if (gcLower.Contains("ranger"))
                        {
                            if (gcLower.Contains("helm"))
                            {
                                itemModCount = 3;
                                Debug.LogError($"[INVENTORY-WRITEINIT] PARTIALBUILT JUST-SEASONAL RANGER HELM - modCount=3");
                            }
                            else
                            {
                                itemModCount = 4;
                                Debug.LogError($"[INVENTORY-WRITEINIT] PARTIALBUILT JUST-SEASONAL RANGER - modCount=4");
                            }
                        }
                        else if (gcLower.Contains("mage"))
                        {
                            if (gcLower.Contains("helm"))
                            {
                                itemModCount = 3;
                                Debug.LogError($"[INVENTORY-WRITEINIT] PARTIALBUILT JUST-SEASONAL MAGE HELM - modCount=3");
                            }
                            else
                            {
                                itemModCount = 4;
                                Debug.LogError($"[INVENTORY-WRITEINIT] PARTIALBUILT JUST-SEASONAL MAGE - modCount=4");
                            }
                        }
                        else
                        {
                            itemModCount = 4;
                            Debug.LogError($"[INVENTORY-WRITEINIT] PARTIALBUILT JUST-SEASONAL DEFAULT - modCount=4");
                        }
                    }
                    else if (hasUniqueSeasonal)
                    {
                        if (gcLower.Contains("fighter"))
                        {
                            if (gcLower.Contains("shield"))
                            {
                                itemModCount = 4;
                                Debug.LogError($"[INVENTORY-WRITEINIT] PARTIALBUILT UNIQUESEASONAL FIGHTER SHIELD - modCount=4");
                            }
                            else
                            {
                                itemModCount = 3;
                                Debug.LogError($"[INVENTORY-WRITEINIT] PARTIALBUILT UNIQUESEASONAL FIGHTER - modCount=3");
                            }
                        }
                        else if (gcLower.Contains("ranger"))
                        {
                            itemModCount = 3;
                            Debug.LogError($"[INVENTORY-WRITEINIT] PARTIALBUILT UNIQUESEASONAL RANGER - modCount=3");
                        }
                        else
                        {
                            itemModCount = 3;
                            Debug.LogError($"[INVENTORY-WRITEINIT] PARTIALBUILT UNIQUESEASONAL MAGE - modCount=3");
                        }
                    }
                    else if (hasWishingwell)
                    {
                        if (gcLower.Contains("fighter"))
                        {
                            itemModCount = 4;
                            Debug.LogError($"[INVENTORY-WRITEINIT] PARTIALBUILT WISHINGWELL FIGHTER - modCount=4");
                        }
                        else if (gcLower.Contains("ranger"))
                        {
                            itemModCount = 3;
                            Debug.LogError($"[INVENTORY-WRITEINIT] PARTIALBUILT WISHINGWELL RANGER - modCount=3");
                        }
                        else if (gcLower.Contains("mage"))
                        {
                            if (gcLower.Contains("helm"))
                            {
                                itemModCount = 4;
                                Debug.LogError($"[INVENTORY-WRITEINIT] PARTIALBUILT WISHINGWELL MAGE HELM - modCount=4");
                            }
                            else
                            {
                                itemModCount = 3;
                                Debug.LogError($"[INVENTORY-WRITEINIT] PARTIALBUILT WISHINGWELL MAGE - modCount=3");
                            }
                        }
                        else
                        {
                            itemModCount = 3;
                            Debug.LogError($"[INVENTORY-WRITEINIT] PARTIALBUILT WISHINGWELL DEFAULT - modCount=3");
                        }
                    }
                    else
                    {
                        if (gcLower.Contains("fighter"))
                        {
                            itemModCount = 4;
                            Debug.LogError($"[INVENTORY-WRITEINIT] PARTIALBUILT UNIQUE FIGHTER - modCount=4");
                        }
                        else if (gcLower.Contains("ranger"))
                        {
                            itemModCount = 3;
                            Debug.LogError($"[INVENTORY-WRITEINIT] PARTIALBUILT UNIQUE RANGER - modCount=3");
                        }
                        else
                        {
                            itemModCount = 3;
                            Debug.LogError($"[INVENTORY-WRITEINIT] PARTIALBUILT UNIQUE MAGE - modCount=3");
                        }
                    }
                }
                else if (isPrebuilt)
                {
                    if (hasMythicInName)
                    {
                        if (gcLower.Contains("mythic002"))
                        {
                            if (gcLower.Contains("helm"))
                            {
                                itemModCount = 6;
                                Debug.LogError($"[INVENTORY-WRITEINIT] PREBUILT MYTHIC002 HELM - modCount=6");
                            }
                            else
                            {
                                itemModCount = 5;
                                Debug.LogError($"[INVENTORY-WRITEINIT] PREBUILT MYTHIC002 - modCount=5");
                            }
                        }
                        else if (gcLower.Contains("mythic003"))
                        {
                            if (gcLower.Contains("helm"))
                            {
                                itemModCount = 6;
                                Debug.LogError($"[INVENTORY-WRITEINIT] PREBUILT MYTHIC003 HELM - modCount=6");
                            }
                            else
                            {
                                itemModCount = 5;
                                Debug.LogError($"[INVENTORY-WRITEINIT] PREBUILT MYTHIC003 - modCount=5");
                            }
                        }
                        else
                        {
                            if (gcLower.Contains("fighter"))
                            {
                                itemModCount = 5;
                                Debug.LogError($"[INVENTORY-WRITEINIT] PREBUILT MYTHIC001 FIGHTER - modCount=5");
                            }
                            else if (gcLower.Contains("ranger"))
                            {
                                itemModCount = 6;
                                Debug.LogError($"[INVENTORY-WRITEINIT] PREBUILT MYTHIC001 RANGER - modCount=6");
                            }
                            else if (gcLower.Contains("mage"))
                            {
                                itemModCount = 5;
                                Debug.LogError($"[INVENTORY-WRITEINIT] PREBUILT MYTHIC001 MAGE - modCount=5");
                            }
                            else
                            {
                                itemModCount = 5;
                                Debug.LogError($"[INVENTORY-WRITEINIT] PREBUILT MYTHIC001 DEFAULT - modCount=5");
                            }
                        }
                    }
                    else if (hasMythicSeasonal)
                    {
                        if (gcLower.Contains("fighter"))
                        {
                            if (gcLower.Contains("helm"))
                            {
                                itemModCount = 6;
                                Debug.LogError($"[INVENTORY-WRITEINIT] PREBUILT MYTHICSEASONAL FIGHTER HELM - modCount=6");
                            }
                            else if (gcLower.Contains("shield"))
                            {
                                itemModCount = 6;
                                Debug.LogError($"[INVENTORY-WRITEINIT] PREBUILT MYTHICSEASONAL FIGHTER SHIELD - modCount=6");
                            }
                            else
                            {
                                itemModCount = 5;
                                Debug.LogError($"[INVENTORY-WRITEINIT] PREBUILT MYTHICSEASONAL FIGHTER - modCount=5");
                            }
                        }
                        else if (gcLower.Contains("ranger"))
                        {
                            if (gcLower.Contains("helm"))
                            {
                                itemModCount = 6;
                                Debug.LogError($"[INVENTORY-WRITEINIT] PREBUILT MYTHICSEASONAL RANGER HELM - modCount=6");
                            }
                            else if (gcLower.Contains("shield"))
                            {
                                itemModCount = 6;
                                Debug.LogError($"[INVENTORY-WRITEINIT] PREBUILT MYTHICSEASONAL RANGER SHIELD - modCount=6");
                            }
                            else
                            {
                                itemModCount = 5;
                                Debug.LogError($"[INVENTORY-WRITEINIT] PREBUILT MYTHICSEASONAL RANGER - modCount=5");
                            }
                        }
                        else if (gcLower.Contains("mage"))
                        {
                            if (gcLower.Contains("helm"))
                            {
                                itemModCount = 6;
                                Debug.LogError($"[INVENTORY-WRITEINIT] PREBUILT MYTHICSEASONAL MAGE HELM - modCount=6");
                            }
                            else if (gcLower.Contains("shield"))
                            {
                                itemModCount = 7;
                                Debug.LogError($"[INVENTORY-WRITEINIT] PREBUILT MYTHICSEASONAL MAGE SHIELD - modCount=7");
                            }
                            else
                            {
                                itemModCount = 5;
                                Debug.LogError($"[INVENTORY-WRITEINIT] PREBUILT MYTHICSEASONAL MAGE - modCount=5");
                            }
                        }
                        else
                        {
                            itemModCount = 5;
                            Debug.LogError($"[INVENTORY-WRITEINIT] PREBUILT MYTHICSEASONAL DEFAULT - modCount=5");
                        }
                    }
                    else if (gcLower.Contains("boss"))
                    {
                        if (gcLower.Contains("fighter"))
                        {
                            if (gcLower.Contains("helm"))
                            {
                                itemModCount = 6;
                                Debug.LogError($"[INVENTORY-WRITEINIT] PREBUILT BOSS FIGHTER HELM - modCount=6");
                            }
                            else
                            {
                                itemModCount = 7;
                                Debug.LogError($"[INVENTORY-WRITEINIT] PREBUILT BOSS FIGHTER - modCount=7");
                            }
                        }
                        else if (gcLower.Contains("ranger"))
                        {
                            if (gcLower.Contains("helm"))
                            {
                                itemModCount = 6;
                                Debug.LogError($"[INVENTORY-WRITEINIT] PREBUILT BOSS RANGER HELM - modCount=6");
                            }
                            else
                            {
                                itemModCount = 7;
                                Debug.LogError($"[INVENTORY-WRITEINIT] PREBUILT BOSS RANGER - modCount=7");
                            }
                        }
                        else if (gcLower.Contains("mage"))
                        {
                            if (gcLower.Contains("shield"))
                            {
                                itemModCount = 6;
                                Debug.LogError($"[INVENTORY-WRITEINIT] PREBUILT BOSS MAGE SHIELD - modCount=6");
                            }
                            else
                            {
                                itemModCount = 7;
                                Debug.LogError($"[INVENTORY-WRITEINIT] PREBUILT BOSS MAGE - modCount=7");
                            }
                        }
                        else
                        {
                            itemModCount = 4;
                            Debug.LogError($"[INVENTORY-WRITEINIT] PREBUILT BOSS DEFAULT - modCount=4");
                        }
                    }
                    else if (hasWishingwell && !gcLower.Contains("fighter"))
                    {
                        if (gcLower.Contains("helm"))
                        {
                            itemModCount = 7;
                            Debug.LogError($"[INVENTORY-WRITEINIT] PREBUILT WISHINGWELL HELM - modCount=7");
                        }
                        else
                        {
                            itemModCount = 5;
                            Debug.LogError($"[INVENTORY-WRITEINIT] PREBUILT WISHINGWELL - modCount=5");
                        }
                    }
                    else
                    {
                        itemModCount = itemData?.modCount ?? 4;
                        Debug.LogError($"[INVENTORY-WRITEINIT] PREBUILT DEFAULT - modCount={itemModCount}");
                    }
                }
                else if (isGeneratedboss)
                {
                    if (gcLower.Contains("fighter"))
                    {
                        if (gcLower.Contains("helm") || gcLower.Contains("shield"))
                        {
                            itemModCount = 6;
                            Debug.LogError($"[INVENTORY-WRITEINIT] GENERATEDBOSS FIGHTER HELM/SHIELD - modCount=6");
                        }
                        else
                        {
                            itemModCount = 7;
                            Debug.LogError($"[INVENTORY-WRITEINIT] GENERATEDBOSS FIGHTER - modCount=7");
                        }
                    }
                    else if (gcLower.Contains("ranger"))
                    {
                        if (gcLower.Contains("helm") || gcLower.Contains("shield"))
                        {
                            itemModCount = 6;
                            Debug.LogError($"[INVENTORY-WRITEINIT] GENERATEDBOSS RANGER HELM/SHIELD - modCount=6");
                        }
                        else
                        {
                            itemModCount = 7;
                            Debug.LogError($"[INVENTORY-WRITEINIT] GENERATEDBOSS RANGER - modCount=7");
                        }
                    }
                    else if (gcLower.Contains("mage"))
                    {
                        if (gcLower.Contains("helm") || gcLower.Contains("shield"))
                        {
                            itemModCount = 2;
                            Debug.LogError($"[INVENTORY-WRITEINIT] GENERATEDBOSS MAGE HELM/SHIELD - modCount=2");
                        }
                        else
                        {
                            itemModCount = 2;
                            Debug.LogError($"[INVENTORY-WRITEINIT] GENERATEDBOSS MAGE - modCount=2");
                        }
                    }
                    else
                    {
                        itemModCount = 4;
                        Debug.LogError($"[INVENTORY-WRITEINIT] GENERATEDBOSS DEFAULT - modCount=4");
                    }
                }
                else if (isMythic && NativeClass == "Armor")
                {
                    itemModCount = 5;
                    if (gcLower.Contains("chain") && !gcLower.Contains("shield"))
                        itemModCount = 4;
                    else if (gcLower.Contains("crystal"))
                        itemModCount = 6;
                    else if (gcLower.Contains("leather"))
                    {
                        if (gcLower.Contains("shield") || gcLower.Contains("gloves") || gcLower.Contains("helm") || gcLower.Contains("armor"))
                            itemModCount = 6;
                    }
                    else if (gcLower.Contains("scale"))
                        itemModCount = 6;
                    else if (gcLower.Contains("splint"))
                    {
                        if (gcLower.Contains("100"))
                        {
                            itemModCount = 1;
                            Debug.LogError($"[OP5] MYTHIC SPLINT 100 - modCount=1");
                        }
                        else if (gcLower.Contains("101"))
                        {
                            itemModCount = 1;
                            Debug.LogError($"[OP5] MYTHIC SPLINT 101 - modCount=1");
                        }
                        else
                        {
                            itemModCount = 6;
                        }
                    }
                    else if (gcLower.Contains("plate") && gcLower.Contains("shield"))
                    {
                        if (gcLower.Contains("shield3"))
                        {
                            itemModCount = 6;
                            Debug.LogError($"[OP5] MYTHIC PLATE SHIELD FX - modCount=6");
                        }
                        else
                        {
                            itemModCount = 5;
                            Debug.LogError($"[OP5] MYTHIC PLATE SHIELD - modCount=5");
                        }
                    }
                    Debug.LogError($"[OP5] MYTHIC - modCount={itemModCount}");
                }
                else if (isMythic && gcLower.Contains("sword"))
                {
                    if (gcLower.Contains("mythic6") || gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                    {
                        itemModCount = 6;
                        Debug.LogError($"[XXX] MYTHIC SWORD FX - modCount=6");
                    }
                    else
                    {
                        itemModCount = 5;
                        Debug.LogError($"[XXX] MYTHIC SWORD - modCount=5");
                    }
                }
                else if (isMythic && gcLower.Contains("axe"))
                {
                    if (gcLower.Contains("mythic6") || gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                    {
                        itemModCount = 6;
                        Debug.LogError($"[XXX] MYTHIC AXE FX - modCount=6");
                    }
                    else
                    {
                        itemModCount = 5;
                        Debug.LogError($"[XXX] MYTHIC AXE - modCount=5");
                    }
                }
                else if (isMythic && gcLower.Contains("mace"))
                {
                    if (gcLower.Contains("mythic6") || gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                    {
                        itemModCount = 6;
                        Debug.LogError($"[XXX] MYTHIC MACE FX - modCount=6");
                    }
                    else
                    {
                        itemModCount = 5;
                        Debug.LogError($"[XXX] MYTHIC MACE - modCount=5");
                    }
                }
                else if (isMythic && gcLower.Contains("staff"))
                {
                    if (gcLower.Contains("mythic6") || gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                    {
                        itemModCount = 6;
                        Debug.LogError($"[XXX] MYTHIC STAFF FX - modCount=6");
                    }
                    else
                    {
                        itemModCount = 5;
                        Debug.LogError($"[XXX] MYTHIC STAFF - modCount=5");
                    }
                }
                else if (isMythic && gcLower.Contains("pick"))
                {
                    if (gcLower.Contains("mythic6") || gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                    {
                        itemModCount = 6;
                        Debug.LogError($"[XXX] MYTHIC PICK FX - modCount=6");
                    }
                    else
                    {
                        itemModCount = 5;
                        Debug.LogError($"[XXX] MYTHIC PICK - modCount=5");
                    }
                }
                else if (isMythic && gcLower.Contains("club"))
                {
                    if (gcLower.Contains("mythic6") || gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                    {
                        itemModCount = 6;
                        Debug.LogError($"[XXX] MYTHIC CLUB FX - modCount=6");
                    }
                    else
                    {
                        itemModCount = 5;
                        Debug.LogError($"[XXX] MYTHIC CLUB - modCount=5");
                    }
                }
                else if (isMythic && gcLower.Contains("katana"))
                {
                    if (gcLower.Contains("mythic6") || gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                    {
                        itemModCount = 6;
                        Debug.LogError($"[XXX] MYTHIC KATANA FX - modCount=6");
                    }
                    else
                    {
                        itemModCount = 5;
                        Debug.LogError($"[XXX] MYTHIC KATANA - modCount=5");
                    }
                }
                else if (isMythic && gcLower.Contains("polearm"))
                {
                    if (gcLower.Contains("mythic6") || gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                    {
                        itemModCount = 6;
                        Debug.LogError($"[XXX] MYTHIC POLEARM FX - modCount=6");
                    }
                    else
                    {
                        itemModCount = 5;
                        Debug.LogError($"[XXX] MYTHIC POLEARM - modCount=5");
                    }
                }
                else if (isMythic && gcLower.Contains("crossbow"))
                {
                    if (gcLower.Contains("mythic6") || gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                    {
                        itemModCount = 6;
                        Debug.LogError($"[XXX] MYTHIC CROSSBOW FX - modCount=6");
                    }
                    else
                    {
                        itemModCount = 5;
                        Debug.LogError($"[XXX] MYTHIC CROSSBOW - modCount=5");
                    }
                }
                else if (isMythic && gcLower.Contains("bow"))
                {
                    if (gcLower.Contains("mythic6") || gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                    {
                        itemModCount = 6;
                        Debug.LogError($"[XXX] MYTHIC BOW FX - modCount=6");
                    }
                    else
                    {
                        itemModCount = 5;
                        Debug.LogError($"[XXX] MYTHIC BOW - modCount=5");
                    }
                }
                else if (isMythic && gcLower.Contains("weapon"))
                {
                    if (gcLower.Contains("mythic6") || gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                    {
                        itemModCount = 6;
                        Debug.LogError($"[XXX] MYTHIC WEAPON FX - modCount=6");
                    }
                    else
                    {
                        itemModCount = 5;
                        Debug.LogError($"[XXX] MYTHIC WEAPON - modCount=5");
                    }
                }
                else
                {
                    itemModCount = itemData?.modCount ?? 3;
                    if (gcLower.Contains("chain") && !gcLower.Contains("shield"))
                        itemModCount = 1;
                    Debug.LogError($"[INVENTORY-WRITEINIT] REGULAR - modCount={itemModCount}");
                }

                Debug.LogError($"[INVENTORY-WRITEINIT] 🔍 DEBUG: finalModCount={itemModCount}");

                for (int i = 0; i < itemModCount; i++)
                {
                    writer.WriteByte(0x00);
                }

                // ItemModifiers — Normal vs colored rarity
                // GC lookup items (mythic/special) always get 0x00 — mods baked into GC class
                if (invGcLookup >= 0)
                {
                    writer.WriteByte(0x00);
                    Debug.LogError($"[INVENTORY-WRITEINIT] GC lookup item - no ScaleMod (mods baked in GC class)");
                }
                else
                {
                    // Same GetEffectiveRarity fallback as the drop branch (iteration 15)
                    // — named-Unique items have no dash suffix so GetTierFromGcType returns
                    // Normal. Without this fallback, picked-up Unique armor rendered white
                    // in inventory despite rendering purple as a world drop.
                    int invTier2 = DungeonRunners.Managers.RarityHelper.GetTierFromGcType(GCClass);
                    var invRarity2 = DungeonRunners.Managers.RarityHelper.GetRarityFromTier(invTier2);
                    if (invRarity2 == DungeonRunners.Managers.ItemRarity.Normal)
                    {
                        int effective = GetEffectiveRarity();
                        if (effective > 0 && effective < 5)
                            invRarity2 = (DungeonRunners.Managers.ItemRarity)effective;
                    }

                    // Path B — try wire-mod injection first. Direct lookup covers Rare/Unique
                    // direct-Item IGs; wrapper lookup covers Magic/Superior wrapper IGs.
                    var armorWireMods = DungeonRunners.Data.ItemStatDatabase.Instance.GetItemWireMods(GCClass);
                    if (armorWireMods.Count == 0)
                        armorWireMods = DungeonRunners.Data.ItemStatDatabase.Instance.GetWrapperIGWireMods(GCClass, invRarity2.ToString());

                    if (DungeonRunners.Data.ItemStatDatabase.PathBEnabled && armorWireMods.Count > 0)
                    {
                        var hashes = new List<uint>();
                        var skipped = new List<string>();
                        var emitted = new List<string>();
                        foreach (var (slot, modRef) in armorWireMods.OrderBy(w => w.Slot))
                        {
                            uint h = DungeonRunners.Data.ItemStatDatabase.Instance.GetGCClassHash(modRef);
                            if (h != 0) { hashes.Add(h); emitted.Add($"{modRef}#0x{h:X8}"); }
                            else skipped.Add(modRef);
                        }
                        writer.WriteByte((byte)hashes.Count);
                        foreach (uint h in hashes)
                        {
                            writer.WriteByte(0x04);
                            writer.WriteUInt32(h);
                            writer.WriteByte(0x00);
                        }
                        Debug.LogError($"[IG-INJECT] inv-armor {GCClass} rarity={invRarity2} mods={hashes.Count}/{armorWireMods.Count} phase1ModCount={itemModCount} emitted=[{string.Join(" | ", emitted)}] skipped=[{string.Join(",", skipped)}]");
                    }
                    else if (invRarity2 == DungeonRunners.Managers.ItemRarity.Normal)
                    {
                        writer.WriteByte(0x00);
                    }
                    else
                    {
                        writer.WriteByte(0x01);
                        writer.WriteByte(0xFF);
                        writer.WriteCString(GetModifierGCClass());
                        writer.WriteByte(0x03);
                        writer.WriteByte(0x15);
                        writer.WriteUInt32(0x11111111);
                    }
                }

                // Weapon extra bytes
                /*   if (NativeClass == "MeleeWeapon" ||
                       GCClass.ToLower().Contains("sword") ||
                       GCClass.ToLower().Contains("axe") ||
                       GCClass.ToLower().Contains("mace") ||
                       GCClass.ToLower().Contains("staff") ||
                       GCClass.ToLower().Contains("pick") ||
                       GCClass.ToLower().Contains("club") ||
                       GCClass.ToLower().Contains("katana") ||
                       GCClass.ToLower().Contains("polearm"))
                   {
                       writer.WriteUInt16(0x01);
                       writer.WriteByte(0x02);
                       writer.WriteUInt16(0x00);
                       Debug.LogError($"[INVENTORY-WRITEINIT] Wrote MeleeWeapon extra bytes");
                   }
                   else if (NativeClass == "RangedWeapon" ||
                       GCClass.ToLower().Contains("crossbow") ||
                       GCClass.ToLower().Contains("bow") ||
                       GCClass.ToLower().Contains("gun") ||
                       GCClass.ToLower().Contains("cannon"))
                   {
                       writer.WriteUInt16(0x0000);
                       writer.WriteUInt16(0x0000);
                       Debug.LogError($"[INVENTORY-WRITEINIT] Wrote RangedWeapon extra bytes");
                   }*/

                Debug.LogError($"[INVENTORY-WRITEINIT] ✅ Complete for {GCClass}");
            }
            else
            {
                WriteInit(writer, playerLevel);
            }
        }

        //=============================================================================
        // WriteInitForEquip - SEPARATE LOGIC
        //=============================================================================
        //=============================================================================
        // WriteInitForEquip - SEPARATE LOGIC
        //=============================================================================
        // ═══════════════════════════════════════════════════════════════════════════════
        // METHOD 5: WriteInitForEquip (starts at line 3414)
        // ═══════════════════════════════════════════════════════════════════════════════

        public void WriteInitForEquip(LEWriter writer, int playerLevel)
        {
            Debug.LogError($"[EQUIP-WRITEINIT] Writing equipped item: {GCClass}");

            // FORCE CORRECT NATIVECLASS FOR RINGS/AMULETS
            string gcCheck = GCClass.ToLower();
            if (gcCheck.Contains("ring") || gcCheck.Contains("amulet"))
            {
                NativeClass = "Item";
            }

            if (NativeClass == "Armor" || NativeClass == "MeleeWeapon" || NativeClass == "RangedWeapon" || NativeClass == "Item")
            {
                writer.WriteByte(0xFF);
                writer.WriteCString(GetPacketGCClass());

                uint equipSlot = TargetSlot ?? GetPropertyUInt32("Slot");
                if (equipSlot == 0)
                {
                    equipSlot = GetEquipmentSlotFromGCClass();
                }
                writer.WriteUInt32(equipSlot);
                writer.WriteByte(0x00);
                writer.WriteByte(0x00);
                writer.WriteByte(0x01);

                // ═══════════════════════════════════════════════════════════════════════════════
                // FIXED ITEM SERIALIZATION FOR RINGS/AMULETS
                // ═══════════════════════════════════════════════════════════════════════════════
                string gcLower = GCClass.ToLower();
                if (NativeClass == "Item" || gcLower.Contains("ring") || gcLower.Contains("amulet"))
                {
                    bool isAmulet = gcLower.Contains("amulet");

                    // Equipped jewelry — mirror the drop/inventory write. BaseRing /
                    // BaseAmulet have 0 inherited ItemModifier children (Ghidra-verified
                    // ReadChildData<ItemModifier> @ 0x00583920), so non-mythic rings/
                    // amulets write 0 placeholders + ScaleMod block. Mythic jewelry writes
                    // flag + N placeholders (from .gc Mod1..N) + Phase 2 hashes.
                    if (isAmulet)
                    {
                        bool isMythicAmulet = gcLower.Contains("mythic");
                        int itemLevel = StoredLevel >= 0 ? StoredLevel : (isMythicAmulet ? (playerLevel + 3) : GetItemRequiredLevel());
                        writer.WriteByte((byte)itemLevel);

                        if (isMythicAmulet)
                        {
                            var amuletMods = DatabaseLoader.GetAmuletModifiers(GCClass);
                            int amuletModCount = amuletMods.Count;
                            Debug.LogError($"[EQUIP-AMULET-MYTHIC] GCClass: {GCClass}, Slot: {equipSlot}, Level: {itemLevel}, Placeholders: {amuletModCount}");
                            writer.WriteByte(0x00);  // mythic flag
                            for (int m = 0; m < amuletModCount; m++) writer.WriteByte(0x00);
                            var resolvedAmuletHashes = new List<uint>(amuletMods.Count);
                            foreach (var mod in amuletMods)
                            {
                                uint amuletHash = DungeonRunners.Data.ItemStatDatabase.Instance.GetGCClassHash(mod);
                                if (amuletHash != 0) resolvedAmuletHashes.Add(amuletHash);
                            }
                            writer.WriteByte((byte)resolvedAmuletHashes.Count);
                            foreach (uint amuletHash in resolvedAmuletHashes)
                            {
                                writer.WriteByte(0x04);
                                writer.WriteUInt32(amuletHash);
                                writer.WriteByte(0x00);
                            }
                        }
                        else
                        {
                            // 1 Phase-1 placeholder (inherited) + Path B injection / ScaleMod fallback.
                            int eqTier = DungeonRunners.Managers.RarityHelper.GetTierFromGcType(GCClass);
                            var eqRarity = DungeonRunners.Managers.RarityHelper.GetRarityFromTier(eqTier);
                            if (eqRarity == DungeonRunners.Managers.ItemRarity.Normal)
                            {
                                int effective = GetEffectiveRarity();
                                if (effective > 0 && effective < 5)
                                    eqRarity = (DungeonRunners.Managers.ItemRarity)effective;
                            }
                            Debug.LogError($"[EQUIP-AMULET-NONMYTHIC] GCClass: {GCClass}, Slot: {equipSlot}, Level: {itemLevel}, Rarity: {eqRarity}");
                            writer.WriteByte(0x00);  // Phase 1 placeholder (inherited)

                            var eqAmuletMods = DungeonRunners.Data.ItemStatDatabase.Instance.GetItemWireMods(GCClass);
                            if (eqAmuletMods.Count == 0)
                                eqAmuletMods = DungeonRunners.Data.ItemStatDatabase.Instance.GetWrapperIGWireMods(GCClass, eqRarity.ToString());

                            if (DungeonRunners.Data.ItemStatDatabase.PathBEnabled && eqAmuletMods.Count > 0)
                            {
                                var hashes = new List<uint>();
                                foreach (var (slot, modRef) in eqAmuletMods.OrderBy(w => w.Slot))
                                {
                                    uint h = DungeonRunners.Data.ItemStatDatabase.Instance.GetGCClassHash(modRef);
                                    if (h != 0) hashes.Add(h);
                                }
                                writer.WriteByte((byte)hashes.Count);
                                foreach (uint h in hashes)
                                {
                                    writer.WriteByte(0x04);
                                    writer.WriteUInt32(h);
                                    writer.WriteByte(0x00);
                                }
                                Debug.LogError($"[IG-INJECT] equip-amulet {GCClass} rarity={eqRarity} mods={hashes.Count}");
                            }
                            else if (eqRarity == DungeonRunners.Managers.ItemRarity.Normal)
                            {
                                writer.WriteByte(0x00);
                            }
                            else
                            {
                                writer.WriteByte(0x01);
                                writer.WriteByte(0xFF);
                                writer.WriteCString(GetModifierGCClass());
                                writer.WriteByte(0x03);
                                writer.WriteByte(0x15);
                                writer.WriteUInt32(0x11111111);
                            }
                        }
                        return;
                    }
                    else
                    {
                        bool isMythicRing = gcLower.Contains("mythic");
                        int itemLevel = StoredLevel >= 0 ? StoredLevel : (isMythicRing ? (playerLevel + 3) : GetItemRequiredLevel());
                        writer.WriteByte((byte)itemLevel);

                        if (isMythicRing)
                        {
                            var ringMods = DatabaseLoader.GetRingModifiers(GCClass);
                            int ringModCount = ringMods.Count;
                            Debug.LogError($"[EQUIP-RING-MYTHIC] GCClass: {GCClass}, Slot: {equipSlot}, Level: {itemLevel}, Placeholders: {ringModCount}");
                            writer.WriteByte(0x00);  // mythic flag
                            for (int m = 0; m < ringModCount; m++) writer.WriteByte(0x00);
                            var resolvedRingHashes = new List<uint>(ringMods.Count);
                            foreach (var mod in ringMods)
                            {
                                uint ringHash = DungeonRunners.Data.ItemStatDatabase.Instance.GetGCClassHash(mod);
                                if (ringHash != 0) resolvedRingHashes.Add(ringHash);
                            }
                            writer.WriteByte((byte)resolvedRingHashes.Count);
                            foreach (uint ringHash in resolvedRingHashes)
                            {
                                writer.WriteByte(0x04);
                                writer.WriteUInt32(ringHash);
                                writer.WriteByte(0x00);
                            }
                        }
                        else
                        {
                            // 1 Phase-1 placeholder (inherited) + Path B injection / ScaleMod fallback.
                            int eqTier = DungeonRunners.Managers.RarityHelper.GetTierFromGcType(GCClass);
                            var eqRarity = DungeonRunners.Managers.RarityHelper.GetRarityFromTier(eqTier);
                            if (eqRarity == DungeonRunners.Managers.ItemRarity.Normal)
                            {
                                int effective = GetEffectiveRarity();
                                if (effective > 0 && effective < 5)
                                    eqRarity = (DungeonRunners.Managers.ItemRarity)effective;
                            }
                            Debug.LogError($"[EQUIP-RING-NONMYTHIC] GCClass: {GCClass}, Slot: {equipSlot}, Level: {itemLevel}, Rarity: {eqRarity}");
                            writer.WriteByte(0x00);  // Phase 1 placeholder (inherited)

                            var eqRingMods = DungeonRunners.Data.ItemStatDatabase.Instance.GetItemWireMods(GCClass);
                            if (eqRingMods.Count == 0)
                                eqRingMods = DungeonRunners.Data.ItemStatDatabase.Instance.GetWrapperIGWireMods(GCClass, eqRarity.ToString());

                            if (DungeonRunners.Data.ItemStatDatabase.PathBEnabled && eqRingMods.Count > 0)
                            {
                                var hashes = new List<uint>();
                                foreach (var (slot, modRef) in eqRingMods.OrderBy(w => w.Slot))
                                {
                                    uint h = DungeonRunners.Data.ItemStatDatabase.Instance.GetGCClassHash(modRef);
                                    if (h != 0) hashes.Add(h);
                                }
                                writer.WriteByte((byte)hashes.Count);
                                foreach (uint h in hashes)
                                {
                                    writer.WriteByte(0x04);
                                    writer.WriteUInt32(h);
                                    writer.WriteByte(0x00);
                                }
                                Debug.LogError($"[IG-INJECT] equip-ring {GCClass} rarity={eqRarity} mods={hashes.Count}");
                            }
                            else if (eqRarity == DungeonRunners.Managers.ItemRarity.Normal)
                            {
                                writer.WriteByte(0x00);
                            }
                            else
                            {
                                writer.WriteByte(0x01);
                                writer.WriteByte(0xFF);
                                writer.WriteCString(GetModifierGCClass());
                                writer.WriteByte(0x03);
                                writer.WriteByte(0x15);
                                writer.WriteUInt32(0x11111111);
                            }
                        }
                        return;
                    }
                }

                ItemData itemData = DatabaseLoader.FindItem(GCClass);

                // Detection flags
                bool isMythic = gcLower.Contains("mythic")
                || DungeonRunners.Managers.MerchantManager._mythicModSlots.ContainsKey(
                    gcLower.StartsWith("items.pal.") ? gcLower.Substring(10) : gcLower);
                bool isPartialbuilt = gcLower.Contains("partialbuilt");
                bool isPrebuilt = gcLower.Contains("prebuilt");
                bool isGeneratedboss = gcLower.Contains("generatedboss");
                bool hasMythicSeasonal = gcLower.Contains("mythicseasonal");
                bool hasMythicInName = gcLower.Contains("mythic") && !hasMythicSeasonal;
                bool hasSeasonal = gcLower.Contains("seasonal");
                bool hasWishingwell = gcLower.Contains("wishingwell");
                bool hasUnique = gcLower.Contains("unique");
                bool hasJustSeasonal = hasSeasonal && !gcLower.Contains("mythic") && !hasUnique;
                bool hasUniqueSeasonal = hasUnique && hasSeasonal;

                int level = StoredLevel >= 0 ? StoredLevel : (isMythic ? (playerLevel + 3) : GetItemRequiredLevel());
                writer.WriteByte((byte)level);

                //------------------------------------------
                // FLAG BYTE LOGIC (SEPARATE)
                //------------------------------------------
                bool writeFlag = false;

                if (isMythic && !isPartialbuilt)
                {
                    writeFlag = true;
                }
                else if (isPartialbuilt && hasMythicSeasonal)
                {
                    writeFlag = true;
                }
                else if (isPartialbuilt && hasUniqueSeasonal && !gcLower.Contains("fighter"))
                {
                    writeFlag = true;
                }
                else if (isPrebuilt && hasMythicInName)
                {
                    writeFlag = true;
                }
                else if (isPrebuilt && hasMythicSeasonal)
                {
                    writeFlag = true;
                }
                else if (isPrebuilt && hasWishingwell && !gcLower.Contains("fighter") && !gcLower.Contains("helm"))
                {
                    writeFlag = true;
                }

                // Fallback: NON-MythicPAL items in lookup table always get flag byte (GetOP5ModCount assumes it)
                // MythicPAL items already have correct writeFlag logic above — don't override them
                if (!writeFlag && isMythic && GCClass.IndexOf("MythicPAL", System.StringComparison.OrdinalIgnoreCase) < 0)
                {
                    writeFlag = true;
                }

                if (writeFlag)
                {
                    if ((gcLower.Contains("scale") && !gcLower.Contains("helm") && !gcLower.Contains("armor") && !gcLower.Contains("100") && !gcLower.Contains("101")) ||
                        (gcLower.Contains("splint") && !gcLower.Contains("shoulder") && !gcLower.Contains("armor") && !gcLower.Contains("100") && !gcLower.Contains("101")))
                    {
                        Debug.LogError($"[EQUIP-WRITEINIT] SKIPPED flag for scale/splint");
                        writeFlag = false;
                    }
                }

                bool isWeapon = (NativeClass == "MeleeWeapon" || NativeClass == "RangedWeapon");
                if (writeFlag && (!isWeapon || isMythic))
                {
                    writer.WriteByte(0x00);
                    Debug.LogError($"[EQUIP-WRITEINIT] Wrote FLAG byte (armor only)");
                }

                //------------------------------------------
                // MOD COUNT LOGIC (SEPARATE)
                //------------------------------------------
                int itemModCount;

                // ═══ GC LOOKUP — exact modCount from parsed GC files ═══
                int equipGcLookup = DungeonRunners.Managers.MerchantManager.GetOP5ModCount(GCClass);
                if (equipGcLookup >= 0)
                {
                    itemModCount = equipGcLookup;
                    if (!writeFlag) itemModCount++;  // scale/splint exception removed flag byte, compensate
                    Debug.LogError($"[EQUIP-WRITEINIT] GC LOOKUP: {GCClass} → itemModCount={itemModCount}");
                }
                else if (isPartialbuilt)
                {
                    if (hasMythicSeasonal)
                    {
                        if (gcLower.Contains("fighter"))
                        {
                            if (gcLower.Contains("seasonal002"))
                            {
                                itemModCount = 4;
                                Debug.LogError($"[EQUIP-WRITEINIT] PARTIALBUILT MYTHICSEASONAL002 FIGHTER - modCount=4");
                            }
                            else
                            {
                                itemModCount = 3;
                                Debug.LogError($"[EQUIP-WRITEINIT] PARTIALBUILT MYTHICSEASONAL001 FIGHTER - modCount=3");
                            }
                        }
                        else if (gcLower.Contains("ranger"))
                        {
                            itemModCount = 4;
                            Debug.LogError($"[EQUIP-WRITEINIT] PARTIALBUILT MYTHICSEASONAL RANGER - modCount=4");
                        }
                        else
                        {
                            itemModCount = 2;
                            Debug.LogError($"[EQUIP-WRITEINIT] PARTIALBUILT MYTHICSEASONAL MAGE - modCount=2");
                        }
                    }
                    else if (hasMythicInName)
                    {
                        if (gcLower.Contains("fighter"))
                        {
                            itemModCount = 5;
                            Debug.LogError($"[EQUIP-WRITEINIT] PARTIALBUILT MYTHIC FIGHTER - modCount=5");
                        }
                        else if (gcLower.Contains("ranger"))
                        {
                            itemModCount = 2;
                            Debug.LogError($"[EQUIP-WRITEINIT] PARTIALBUILT MYTHIC RANGER - modCount=2");
                        }
                        else
                        {
                            itemModCount = 2;
                            Debug.LogError($"[EQUIP-WRITEINIT] PARTIALBUILT MYTHIC MAGE - modCount=2");
                        }
                    }
                    else if (hasJustSeasonal)
                    {
                        if (gcLower.Contains("fighter"))
                        {
                            itemModCount = 3;
                            Debug.LogError($"[EQUIP-WRITEINIT] PARTIALBUILT JUST-SEASONAL FIGHTER - modCount=3");
                        }
                        else if (gcLower.Contains("ranger"))
                        {
                            if (gcLower.Contains("helm"))
                            {
                                itemModCount = 3;
                                Debug.LogError($"[EQUIP-WRITEINIT] PARTIALBUILT JUST-SEASONAL RANGER HELM - modCount=3");
                            }
                            else
                            {
                                itemModCount = 4;
                                Debug.LogError($"[EQUIP-WRITEINIT] PARTIALBUILT JUST-SEASONAL RANGER - modCount=4");
                            }
                        }
                        else if (gcLower.Contains("mage"))
                        {
                            if (gcLower.Contains("helm"))
                            {
                                itemModCount = 3;
                                Debug.LogError($"[EQUIP-WRITEINIT] PARTIALBUILT JUST-SEASONAL MAGE HELM - modCount=3");
                            }
                            else
                            {
                                itemModCount = 4;
                                Debug.LogError($"[EQUIP-WRITEINIT] PARTIALBUILT JUST-SEASONAL MAGE - modCount=4");
                            }
                        }
                        else
                        {
                            itemModCount = 4;
                            Debug.LogError($"[EQUIP-WRITEINIT] PARTIALBUILT JUST-SEASONAL DEFAULT - modCount=4");
                        }
                    }
                    else if (hasUniqueSeasonal)
                    {
                        if (gcLower.Contains("fighter"))
                        {
                            if (gcLower.Contains("shield"))
                            {
                                itemModCount = 4;
                                Debug.LogError($"[EQUIP-WRITEINIT] PARTIALBUILT UNIQUESEASONAL FIGHTER SHIELD - modCount=4");
                            }
                            else
                            {
                                itemModCount = 3;
                                Debug.LogError($"[EQUIP-WRITEINIT] PARTIALBUILT UNIQUESEASONAL FIGHTER - modCount=3");
                            }
                        }
                        else if (gcLower.Contains("ranger"))
                        {
                            itemModCount = 3;
                            Debug.LogError($"[EQUIP-WRITEINIT] PARTIALBUILT UNIQUESEASONAL RANGER - modCount=3");
                        }
                        else
                        {
                            itemModCount = 3;
                            Debug.LogError($"[EQUIP-WRITEINIT] PARTIALBUILT UNIQUESEASONAL MAGE - modCount=3");
                        }
                    }
                    else if (hasWishingwell)
                    {
                        if (gcLower.Contains("fighter"))
                        {
                            itemModCount = 4;
                            Debug.LogError($"[EQUIP-WRITEINIT] PARTIALBUILT WISHINGWELL FIGHTER - modCount=4");
                        }
                        else if (gcLower.Contains("ranger"))
                        {
                            itemModCount = 3;
                            Debug.LogError($"[EQUIP-WRITEINIT] PARTIALBUILT WISHINGWELL RANGER - modCount=3");
                        }
                        else if (gcLower.Contains("mage"))
                        {
                            if (gcLower.Contains("helm"))
                            {
                                itemModCount = 4;
                                Debug.LogError($"[EQUIP-WRITEINIT] PARTIALBUILT WISHINGWELL MAGE HELM - modCount=4");
                            }
                            else
                            {
                                itemModCount = 3;
                                Debug.LogError($"[EQUIP-WRITEINIT] PARTIALBUILT WISHINGWELL MAGE - modCount=3");
                            }
                        }
                        else
                        {
                            itemModCount = 3;
                            Debug.LogError($"[EQUIP-WRITEINIT] PARTIALBUILT WISHINGWELL DEFAULT - modCount=3");
                        }
                    }
                    else
                    {
                        if (gcLower.Contains("fighter"))
                        {
                            itemModCount = 4;
                            Debug.LogError($"[EQUIP-WRITEINIT] PARTIALBUILT UNIQUE FIGHTER - modCount=4");
                        }
                        else if (gcLower.Contains("ranger"))
                        {
                            itemModCount = 3;
                            Debug.LogError($"[EQUIP-WRITEINIT] PARTIALBUILT UNIQUE RANGER - modCount=3");
                        }
                        else
                        {
                            itemModCount = 3;
                            Debug.LogError($"[EQUIP-WRITEINIT] PARTIALBUILT UNIQUE MAGE - modCount=3");
                        }
                    }
                }
                else if (isPrebuilt)
                {
                    if (hasMythicInName)
                    {
                        if (gcLower.Contains("mythic002"))
                        {
                            if (gcLower.Contains("helm"))
                            {
                                itemModCount = 6;
                                Debug.LogError($"[EQUIP-WRITEINIT] PREBUILT MYTHIC002 HELM - modCount=6");
                            }
                            else
                            {
                                itemModCount = 5;
                                Debug.LogError($"[EQUIP-WRITEINIT] PREBUILT MYTHIC002 - modCount=5");
                            }
                        }
                        else if (gcLower.Contains("mythic003"))
                        {
                            if (gcLower.Contains("helm"))
                            {
                                itemModCount = 6;
                                Debug.LogError($"[EQUIP-WRITEINIT] PREBUILT MYTHIC003 HELM - modCount=6");
                            }
                            else
                            {
                                itemModCount = 5;
                                Debug.LogError($"[EQUIP-WRITEINIT] PREBUILT MYTHIC003 - modCount=5");
                            }
                        }
                        else
                        {
                            if (gcLower.Contains("fighter"))
                            {
                                itemModCount = 5;
                                Debug.LogError($"[EQUIP-WRITEINIT] PREBUILT MYTHIC001 FIGHTER - modCount=5");
                            }
                            else if (gcLower.Contains("ranger"))
                            {
                                itemModCount = 6;
                                Debug.LogError($"[EQUIP-WRITEINIT] PREBUILT MYTHIC001 RANGER - modCount=6");
                            }
                            else if (gcLower.Contains("mage"))
                            {
                                itemModCount = 5;
                                Debug.LogError($"[EQUIP-WRITEINIT] PREBUILT MYTHIC001 MAGE - modCount=5");
                            }
                            else
                            {
                                itemModCount = 5;
                                Debug.LogError($"[EQUIP-WRITEINIT] PREBUILT MYTHIC001 DEFAULT - modCount=5");
                            }
                        }
                    }
                    else if (hasMythicSeasonal)
                    {
                        if (gcLower.Contains("fighter"))
                        {
                            if (gcLower.Contains("helm"))
                            {
                                itemModCount = 6;
                                Debug.LogError($"[EQUIP-WRITEINIT] PREBUILT MYTHICSEASONAL FIGHTER HELM - modCount=6");
                            }
                            else if (gcLower.Contains("shield"))
                            {
                                itemModCount = 6;
                                Debug.LogError($"[EQUIP-WRITEINIT] PREBUILT MYTHICSEASONAL FIGHTER SHIELD - modCount=6");
                            }
                            else
                            {
                                itemModCount = 5;
                                Debug.LogError($"[EQUIP-WRITEINIT] PREBUILT MYTHICSEASONAL FIGHTER - modCount=5");
                            }
                        }
                        else if (gcLower.Contains("ranger"))
                        {
                            if (gcLower.Contains("helm"))
                            {
                                itemModCount = 6;
                                Debug.LogError($"[EQUIP-WRITEINIT] PREBUILT MYTHICSEASONAL RANGER HELM - modCount=6");
                            }
                            else if (gcLower.Contains("shield"))
                            {
                                itemModCount = 6;
                                Debug.LogError($"[EQUIP-WRITEINIT] PREBUILT MYTHICSEASONAL RANGER SHIELD - modCount=6");
                            }
                            else
                            {
                                itemModCount = 5;
                                Debug.LogError($"[EQUIP-WRITEINIT] PREBUILT MYTHICSEASONAL RANGER - modCount=5");
                            }
                        }
                        else if (gcLower.Contains("mage"))
                        {
                            if (gcLower.Contains("helm"))
                            {
                                itemModCount = 6;
                                Debug.LogError($"[EQUIP-WRITEINIT] PREBUILT MYTHICSEASONAL MAGE HELM - modCount=6");
                            }
                            else if (gcLower.Contains("shield"))
                            {
                                itemModCount = 7;
                                Debug.LogError($"[EQUIP-WRITEINIT] PREBUILT MYTHICSEASONAL MAGE SHIELD - modCount=7");
                            }
                            else
                            {
                                itemModCount = 5;
                                Debug.LogError($"[EQUIP-WRITEINIT] PREBUILT MYTHICSEASONAL MAGE - modCount=5");
                            }
                        }
                        else
                        {
                            itemModCount = 5;
                            Debug.LogError($"[EQUIP-WRITEINIT] PREBUILT MYTHICSEASONAL DEFAULT - modCount=5");
                        }
                    }
                    else if (gcLower.Contains("boss"))
                    {
                        if (gcLower.Contains("fighter"))
                        {
                            if (gcLower.Contains("helm"))
                            {
                                itemModCount = 6;
                                Debug.LogError($"[EQUIP-WRITEINIT] PREBUILT BOSS FIGHTER HELM - modCount=6");
                            }
                            else
                            {
                                itemModCount = 7;
                                Debug.LogError($"[EQUIP-WRITEINIT] PREBUILT BOSS FIGHTER - modCount=7");
                            }
                        }
                        else if (gcLower.Contains("ranger"))
                        {
                            if (gcLower.Contains("helm"))
                            {
                                itemModCount = 6;
                                Debug.LogError($"[EQUIP-WRITEINIT] PREBUILT BOSS RANGER HELM - modCount=6");
                            }
                            else
                            {
                                itemModCount = 7;
                                Debug.LogError($"[EQUIP-WRITEINIT] PREBUILT BOSS RANGER - modCount=7");
                            }
                        }
                        else if (gcLower.Contains("mage"))
                        {
                            if (gcLower.Contains("shield"))
                            {
                                itemModCount = 6;
                                Debug.LogError($"[EQUIP-WRITEINIT] PREBUILT BOSS MAGE SHIELD - modCount=6");
                            }
                            else
                            {
                                itemModCount = 7;
                                Debug.LogError($"[EQUIP-WRITEINIT] PREBUILT BOSS MAGE - modCount=7");
                            }
                        }
                        else
                        {
                            itemModCount = 4;
                            Debug.LogError($"[EQUIP-WRITEINIT] PREBUILT BOSS DEFAULT - modCount=4");
                        }
                    }
                    else if (hasWishingwell && !gcLower.Contains("fighter"))
                    {
                        if (gcLower.Contains("helm"))
                        {
                            itemModCount = 7;
                            Debug.LogError($"[EQUIP-WRITEINIT] PREBUILT WISHINGWELL HELM - modCount=7");
                        }
                        else
                        {
                            itemModCount = 5;
                            Debug.LogError($"[EQUIP-WRITEINIT] PREBUILT WISHINGWELL - modCount=5");
                        }
                    }
                    else
                    {
                        itemModCount = itemData?.modCount ?? 4;
                        Debug.LogError($"[EQUIP-WRITEINIT] PREBUILT DEFAULT - modCount={itemModCount}");
                    }
                }
                else if (isGeneratedboss)
                {
                    if (gcLower.Contains("fighter"))
                    {
                        if (gcLower.Contains("helm") || gcLower.Contains("shield"))
                        {
                            itemModCount = 6;
                            Debug.LogError($"[EQUIP-WRITEINIT] GENERATEDBOSS FIGHTER HELM/SHIELD - modCount=6");
                        }
                        else
                        {
                            itemModCount = 7;
                            Debug.LogError($"[EQUIP-WRITEINIT] GENERATEDBOSS FIGHTER - modCount=7");
                        }
                    }
                    else if (gcLower.Contains("ranger"))
                    {
                        if (gcLower.Contains("helm") || gcLower.Contains("shield"))
                        {
                            itemModCount = 6;
                            Debug.LogError($"[EQUIP-WRITEINIT] GENERATEDBOSS RANGER HELM/SHIELD - modCount=6");
                        }
                        else
                        {
                            itemModCount = 7;
                            Debug.LogError($"[EQUIP-WRITEINIT] GENERATEDBOSS RANGER - modCount=7");
                        }
                    }
                    else if (gcLower.Contains("mage"))
                    {
                        if (gcLower.Contains("helm") || gcLower.Contains("shield"))
                        {
                            itemModCount = 2;
                            Debug.LogError($"[EQUIP-WRITEINIT] GENERATEDBOSS MAGE HELM/SHIELD - modCount=2");
                        }
                        else
                        {
                            itemModCount = 2;
                            Debug.LogError($"[EQUIP-WRITEINIT] GENERATEDBOSS MAGE - modCount=2");
                        }
                    }
                    else
                    {
                        itemModCount = 4;
                        Debug.LogError($"[EQUIP-WRITEINIT] GENERATEDBOSS DEFAULT - modCount=4");
                    }
                }
                else if (isMythic && NativeClass == "Armor")
                {
                    itemModCount = 5;
                    if (gcLower.Contains("chain") && !gcLower.Contains("shield"))
                        itemModCount = 4;
                    else if (gcLower.Contains("crystal"))
                        itemModCount = 6;
                    else if (gcLower.Contains("leather"))
                    {
                        if (gcLower.Contains("shield") || gcLower.Contains("gloves") || gcLower.Contains("helm") || gcLower.Contains("armor"))
                            itemModCount = 6;
                    }
                    else if (gcLower.Contains("scale"))
                        itemModCount = 6;
                    else if (gcLower.Contains("splint"))
                    {
                        if (gcLower.Contains("100") || gcLower.Contains("101"))
                        {
                            itemModCount = 1;
                            Debug.LogError($"[OP5] MYTHIC SPLINT 100/101 - modCount=1");
                        }
                        else
                        {
                            itemModCount = 6;
                        }
                    }
                    else if (gcLower.Contains("plate") && gcLower.Contains("shield"))
                    {
                        if (gcLower.Contains("shield3"))
                        {
                            itemModCount = 6;
                            Debug.LogError($"[OP5] MYTHIC PLATE SHIELD FX - modCount=6");
                        }
                        else
                        {
                            itemModCount = 5;
                            Debug.LogError($"[OP5] MYTHIC PLATE SHIELD - modCount=5");
                        }
                    }
                    Debug.LogError($"[OP5] MYTHIC - modCount={itemModCount}");
                }
                else if (isMythic && gcLower.Contains("sword"))
                {
                    if (gcLower.Contains("mythic6") || gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                    {
                        itemModCount = 6;
                        Debug.LogError($"[XXX] MYTHIC SWORD FX - modCount=6");
                    }
                    else
                    {
                        itemModCount = 5;
                        Debug.LogError($"[XXX] MYTHIC SWORD - modCount=5");
                    }
                }
                else if (isMythic && gcLower.Contains("axe"))
                {
                    if (gcLower.Contains("mythic6") || gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                    {
                        itemModCount = 6;
                        Debug.LogError($"[XXX] MYTHIC AXE FX - modCount=6");
                    }
                    else
                    {
                        itemModCount = 5;
                        Debug.LogError($"[XXX] MYTHIC AXE - modCount=5");
                    }
                }
                else if (isMythic && gcLower.Contains("mace"))
                {
                    if (gcLower.Contains("mythic6") || gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                    {
                        itemModCount = 6;
                        Debug.LogError($"[XXX] MYTHIC MACE FX - modCount=6");
                    }
                    else
                    {
                        itemModCount = 5;
                        Debug.LogError($"[XXX] MYTHIC MACE - modCount=5");
                    }
                }
                else if (isMythic && gcLower.Contains("staff"))
                {
                    if (gcLower.Contains("mythic6") || gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                    {
                        itemModCount = 6;
                        Debug.LogError($"[XXX] MYTHIC STAFF FX - modCount=6");
                    }
                    else
                    {
                        itemModCount = 5;
                        Debug.LogError($"[XXX] MYTHIC STAFF - modCount=5");
                    }
                }
                else if (isMythic && gcLower.Contains("pick"))
                {
                    if (gcLower.Contains("mythic6") || gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                    {
                        itemModCount = 6;
                        Debug.LogError($"[XXX] MYTHIC PICK FX - modCount=6");
                    }
                    else
                    {
                        itemModCount = 5;
                        Debug.LogError($"[XXX] MYTHIC PICK - modCount=5");
                    }
                }
                else if (isMythic && gcLower.Contains("club"))
                {
                    if (gcLower.Contains("mythic6") || gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                    {
                        itemModCount = 6;
                        Debug.LogError($"[XXX] MYTHIC CLUB FX - modCount=6");
                    }
                    else
                    {
                        itemModCount = 5;
                        Debug.LogError($"[XXX] MYTHIC CLUB - modCount=5");
                    }
                }
                else if (isMythic && gcLower.Contains("katana"))
                {
                    if (gcLower.Contains("mythic6") || gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                    {
                        itemModCount = 6;
                        Debug.LogError($"[XXX] MYTHIC KATANA FX - modCount=6");
                    }
                    else
                    {
                        itemModCount = 5;
                        Debug.LogError($"[XXX] MYTHIC KATANA - modCount=5");
                    }
                }
                else if (isMythic && gcLower.Contains("polearm"))
                {
                    if (gcLower.Contains("mythic6") || gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                    {
                        itemModCount = 6;
                        Debug.LogError($"[XXX] MYTHIC POLEARM FX - modCount=6");
                    }
                    else
                    {
                        itemModCount = 5;
                        Debug.LogError($"[XXX] MYTHIC POLEARM - modCount=5");
                    }
                }
                else if (isMythic && gcLower.Contains("crossbow"))
                {
                    if (gcLower.Contains("mythic6") || gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                    {
                        itemModCount = 6;
                        Debug.LogError($"[XXX] MYTHIC CROSSBOW FX - modCount=6");
                    }
                    else
                    {
                        itemModCount = 5;
                        Debug.LogError($"[XXX] MYTHIC CROSSBOW - modCount=5");
                    }
                }
                else if (isMythic && gcLower.Contains("bow"))
                {
                    if (gcLower.Contains("mythic6") || gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                    {
                        itemModCount = 6;
                        Debug.LogError($"[XXX] MYTHIC BOW FX - modCount=6");
                    }
                    else
                    {
                        itemModCount = 5;
                        Debug.LogError($"[XXX] MYTHIC BOW - modCount=5");
                    }
                }
                else if (isMythic && gcLower.Contains("weapon"))
                {
                    if (gcLower.Contains("mythic6") || gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                    {
                        itemModCount = 6;
                        Debug.LogError($"[XXX] MYTHIC WEAPON FX - modCount=6");
                    }
                    else
                    {
                        itemModCount = 5;
                        Debug.LogError($"[XXX] MYTHIC WEAPON - modCount=5");
                    }
                }
                else
                {
                    itemModCount = itemData?.modCount ?? 3;
                    if (gcLower.Contains("chain") && !gcLower.Contains("shield"))
                        itemModCount = 1;
                    Debug.LogError($"[EQUIP-WRITEINIT] REGULAR - modCount={itemModCount}");
                }

                Debug.LogError($"[EQUIP-WRITEINIT] 🔍 DEBUG: finalModCount={itemModCount}");

                for (int i = 0; i < itemModCount; i++)
                {
                    writer.WriteByte(0x00);
                }

                // ItemModifiers — Normal vs colored rarity
                if (equipGcLookup >= 0)
                {
                    writer.WriteByte(0x00);
                    Debug.LogError($"[EQUIP-WRITEINIT] GC lookup item - no ScaleMod");
                }
                else
                {
                    // Same GetEffectiveRarity fallback as drop / inventory (iteration 15+17).
                    int eqTier = DungeonRunners.Managers.RarityHelper.GetTierFromGcType(GCClass);
                    var eqRarity = DungeonRunners.Managers.RarityHelper.GetRarityFromTier(eqTier);
                    if (eqRarity == DungeonRunners.Managers.ItemRarity.Normal)
                    {
                        int effective = GetEffectiveRarity();
                        if (effective > 0 && effective < 5)
                            eqRarity = (DungeonRunners.Managers.ItemRarity)effective;
                    }

                    // Path B — same wire-mod injection as the inventory armor branch.
                    var eqArmorMods = DungeonRunners.Data.ItemStatDatabase.Instance.GetItemWireMods(GCClass);
                    if (eqArmorMods.Count == 0)
                        eqArmorMods = DungeonRunners.Data.ItemStatDatabase.Instance.GetWrapperIGWireMods(GCClass, eqRarity.ToString());

                    if (DungeonRunners.Data.ItemStatDatabase.PathBEnabled && eqArmorMods.Count > 0)
                    {
                        var hashes = new List<uint>();
                        foreach (var (slot, modRef) in eqArmorMods.OrderBy(w => w.Slot))
                        {
                            uint h = DungeonRunners.Data.ItemStatDatabase.Instance.GetGCClassHash(modRef);
                            if (h != 0) hashes.Add(h);
                        }
                        writer.WriteByte((byte)hashes.Count);
                        foreach (uint h in hashes)
                        {
                            writer.WriteByte(0x04);
                            writer.WriteUInt32(h);
                            writer.WriteByte(0x00);
                        }
                        Debug.LogError($"[IG-INJECT] equip-armor {GCClass} rarity={eqRarity} mods={hashes.Count} phase1ModCount={itemModCount}");
                    }
                    else if (eqRarity == DungeonRunners.Managers.ItemRarity.Normal)
                    {
                        writer.WriteByte(0x00);
                    }
                    else
                    {
                        writer.WriteByte(0x01);
                        writer.WriteByte(0xFF);
                        writer.WriteCString(GetModifierGCClass());
                        writer.WriteByte(0x03);
                        writer.WriteByte(0x15);
                        writer.WriteUInt32(0x11111111);
                    }
                }

                // Weapon extra bytes
                if (NativeClass == "MeleeWeapon" ||
                    GCClass.ToLower().Contains("sword") ||
                    GCClass.ToLower().Contains("axe") ||
                    GCClass.ToLower().Contains("mace") ||
                    GCClass.ToLower().Contains("staff") ||
                    GCClass.ToLower().Contains("pick") ||
                    GCClass.ToLower().Contains("club") ||
                    GCClass.ToLower().Contains("katana") ||
                    GCClass.ToLower().Contains("polearm"))
                {
                    writer.WriteUInt16(0x01);
                    writer.WriteByte(0x02);
                    writer.WriteUInt16(0x00);
                    Debug.LogError($"[EQUIP-WRITEINIT] Wrote MeleeWeapon extra bytes");
                }
                else if (NativeClass == "RangedWeapon" ||
                    GCClass.ToLower().Contains("crossbow") ||
                    GCClass.ToLower().Contains("bow") ||
                    GCClass.ToLower().Contains("gun") ||
                    GCClass.ToLower().Contains("cannon"))
                {
                    writer.WriteUInt16(0x0000);
                    writer.WriteUInt16(0x0000);
                    Debug.LogError($"[EQUIP-WRITEINIT] Wrote RangedWeapon extra bytes");
                }

                Debug.LogError($"[EQUIP-WRITEINIT] ✅ Complete for {GCClass}");
            }
            else
            {
                WriteInit(writer, playerLevel);
            }
        }

        //=============================================================================
        // WriteInitForDrop - Simple
        //=============================================================================
        public void WriteInitForDrop(LEWriter writer)
        {
            writer.WriteByte(0xFF);
            writer.WriteCString(GetPacketGCClass());
        }

        //=============================================================================
        // WriteData - For skills
        //=============================================================================
        public void WriteData(LEWriter writer)
        {
            if (NativeClass == "ActiveSkill")
            {
                uint skillSlot = GetPropertyUInt32("SkillSlot");
                if (skillSlot == 0)
                {
                    skillSlot = 100;
                    Debug.LogError($"[SKILL-DATA] WARNING: No SkillSlot property, using default 100");
                }

                writer.WriteUInt32(skillSlot);
                writer.WriteByte(0x01);
                Debug.LogError($"[SKILL-DATA] ✅ Skill WriteData: slot={skillSlot}, level=1");
            }
            else
            {
                Debug.LogError($"[WRITE-DATA] ⚠️ WriteData called on non-skill: {NativeClass}");
            }
        }

        private uint GetPropertyUInt32(string propertyName)
        {
            var prop = Properties?.FirstOrDefault(p => p.Name == propertyName);
            if (prop is UInt32Property uintProp)
            {
                return uintProp.Value;
            }
            return 0;
        }

        public uint GetEquipmentSlotFromGCClass()
        {
            // If a specific slot was set (for rings), use it
            if (TargetSlot.HasValue)
                return TargetSlot.Value;

            string lower = GCClass.ToLower();
            if (lower.Contains("helm")) return 5;
            if (lower.Contains("shoulder") || lower.Contains("pauldron")) return 8;
            if (lower.Contains("armor") || lower.Contains("body") || lower.Contains("chest")) return 6;
            if (lower.Contains("gloves")) return 2;
            if (lower.Contains("boots")) return 7;
            if (lower.Contains("shield")) return 11;
            // RINGS AND AMULETS - Slots 3, 4, 1
            if (lower.Contains("ring")) return 3;   // RingSlot1 (default)
            if (lower.Contains("amulet")) return 1; // AmuletSlot
            if (lower.Contains("weapon") || lower.Contains("sword") || lower.Contains("axe") ||
         lower.Contains("mace") || lower.Contains("staff") || lower.Contains("pick") || lower.Contains("club") || lower.Contains("katana") || lower.Contains("polearm")) return 10;
            return 10;
        }

        public int GetItemRequiredLevel()
        {
            return ItemData.GetRequiredLevelFromGCClass(GCClass);
        }

        public static uint HashDjb2(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return 5381;
            }

            uint hash = 5381;
            string lower = s.ToLowerInvariant();

            foreach (char c in lower)
            {
                hash = ((hash << 5) + hash) + (uint)c;
            }

            return hash & 0xFFFFFFFF;
        }

        public static GCObject CreatePlayer(Character character)
        {
            Debug.Log($"[GCObject] Creating player: {character.Name}");
            var player = new GCObject
            {
                Id = character.Id,
                NativeClass = "Player",
                GCClass = "Player",
                Name = character.Name,
                Properties = new List<GCObjectProperty>()
            };

            player.Properties.Add(new StringProperty { Name = "Name", Value = character.Name });

            var extraData = new List<byte>();
            extraData.AddRange(Encoding.UTF8.GetBytes("plzwork1"));
            extraData.Add(0);
            extraData.AddRange(Encoding.UTF8.GetBytes("plzwork2"));
            extraData.Add(0);

            var idBytes = BitConverter.GetBytes((uint)0x05040302);
            extraData.AddRange(idBytes);
            extraData.AddRange(idBytes);

            extraData.Add(0);
            extraData.Add(0xAA);
            extraData.AddRange(Encoding.UTF8.GetBytes("Normal"));
            extraData.Add(0);
            extraData.Add(0x02);
            extraData.Add(0x00);
            extraData.AddRange(BitConverter.GetBytes((uint)0x05040302));

            player.ExtraData = extraData.ToArray();
            Debug.Log($"[GCObject] Created player with {player.ExtraData.Length} bytes extra data");
            return player;
        }
    }

    public abstract class GCObjectProperty
    {
        public string Name { get; set; } = "";
        public abstract void Serialize(System.IO.BinaryWriter writer);
        public abstract void WriteDFC(LEWriter writer);
    }

    public class StringProperty : GCObjectProperty
    {
        public string Value { get; set; } = "";

        public override void Serialize(System.IO.BinaryWriter writer)
        {
            foreach (var b in Encoding.UTF8.GetBytes(Name))
                writer.Write(b);
            writer.Write((byte)0);
            writer.Write((byte)1);
            foreach (var b in Encoding.UTF8.GetBytes(Value))
                writer.Write(b);
            writer.Write((byte)0);
        }

        public override void WriteDFC(LEWriter writer)
        {
            uint nameHash = GCObject.HashDjb2(Name);
            writer.WriteUInt32(nameHash);
            writer.WriteCString(Value);
        }
    }

    public class UInt32Property : GCObjectProperty
    {
        public uint Value { get; set; }

        public override void Serialize(System.IO.BinaryWriter writer)
        {
            foreach (var b in Encoding.UTF8.GetBytes(Name))
                writer.Write(b);
            writer.Write((byte)0);
            writer.Write((byte)2);
            writer.Write(Value);
        }

        public override void WriteDFC(LEWriter writer)
        {
            uint nameHash = GCObject.HashDjb2(Name);
            writer.WriteUInt32(nameHash);
            writer.WriteUInt32(Value);
        }
    }
}