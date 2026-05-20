using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using UnityEngine;
using DungeonRunners.Utilities;
using DungeonRunners.Networking;
using DungeonRunners.Core;
namespace DungeonRunners.Managers

{
    /// <summary>
    /// Static Merchant Manager with refresh timer support
    /// </summary>
    public static class MerchantManager
    {
        // All merchant definitions keyed by NPC gcType
        private static Dictionary<string, MerchantData> _merchants = new Dictionary<string, MerchantData>(StringComparer.OrdinalIgnoreCase);

        // Runtime merchant instances (with generated items) keyed by NPC gcType
        private static Dictionary<string, MerchantRuntimeData> _runtimeMerchants = new Dictionary<string, MerchantRuntimeData>(StringComparer.OrdinalIgnoreCase);

        // Track when each dynamic inventory was last regenerated
        private static Dictionary<string, DateTime> _lastRegeneration = new Dictionary<string, DateTime>();

        private static readonly List<PendingMerchantRefreshAdd> _pendingMerchantRefreshAdds = new List<PendingMerchantRefreshAdd>();
        private static readonly object _merchantRandomLock = new object();
        private static readonly System.Random _merchantRandom = new System.Random();
        private static bool VerboseMerchantItemLogging => ServerSettings.GetBool("verboseMerchantItemLogging", false);

        // Sellable items with proper casing loaded from sellable_items.json
        private static List<SellableItem> _sellableItems = new List<SellableItem>();

        // Track buy prices for sold items: key = "connId:gcClass" → buyPrice
        // Set on purchase, read on save to character_inventory.buy_price
        public static readonly Dictionary<string, uint> _buyPrices = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

        public static void SetBuyPrice(string connId, string gcClass, uint price)
        {
            string key = connId + ":" + gcClass.ToLowerInvariant();
            _buyPrices[key] = price;
        }

        public static uint GetBuyPrice(string connId, string gcClass)
        {
            string key = connId + ":" + gcClass.ToLowerInvariant();
            return _buyPrices.TryGetValue(key, out uint p) ? p : 0;
        }
        // Item dimensions lookup
        private static Dictionary<string, ItemDimensions> _itemDimensions = new Dictionary<string, ItemDimensions>();

        // Mythic modSlots lookup — derived from GC file Mod child counts + 1 (flags byte)
        // Item::readData reads: ID(4) + InvX(1) + InvY(1) + Qty(1) + Level(1) + flags(1) + ReadChildData<ItemModifier>
        // ReadChildData Phase 1 reads 1 byte per GC-defined ItemModifier child
        // So modSlots = 1 (flags) + number of Mod children in GC class
        public static readonly Dictionary<string, int> _mythicModSlots = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            // ═══ MythicPAL Weapons (modSlots = 1 flags + directMods) ═══
            { "1haxemythicpal.1haxemythic1", 6 }, { "1haxemythicpal.1haxemythic2", 6 }, { "1haxemythicpal.1haxemythic3", 6 },
            { "1haxemythicpal.1haxemythic4", 6 }, { "1haxemythicpal.1haxemythic5", 7 }, { "1hgunmythicpal.1hgunmythic1", 1 },
            { "1hgunmythicpal.1hgunmythic2", 1 }, { "1hmacemythicpal.1hmacemythic1", 6 }, { "1hmacemythicpal.1hmacemythic2", 6 },
            { "1hmacemythicpal.1hmacemythic3", 6 }, { "1hmacemythicpal.1hmacemythic4", 6 }, { "1hmacemythicpal.1hmacemythic5", 6 },
            { "1hmacemythicpal.1hmacemythic6", 6 }, { "1hmacemythicpal.1hmacemythic7", 7 }, { "1hmacemythicpal.1hmacemythic8", 6 },
            { "1hpickmythicpal.1hpickmythic1", 6 }, { "1hpickmythicpal.1hpickmythic2", 6 }, { "1hpickmythicpal.1hpickmythic3", 6 },
            { "1hpickmythicpal.1hpickmythic4", 6 }, { "1hstaffmythicpal.1hstaffmythic1", 6 }, { "1hstaffmythicpal.1hstaffmythic1000", 4 },
            { "1hstaffmythicpal.1hstaffmythic2", 6 }, { "1hstaffmythicpal.1hstaffmythic3", 6 }, { "1hstaffmythicpal.1hstaffmythic4", 6 },
            { "1hstaffmythicpal.1hstaffmythic5", 7 }, { "1hstaffmythicpal.1hstaffmythic6", 6 }, { "1hswordmythicpal.1hswordmythic1", 6 },
            { "1hswordmythicpal.1hswordmythic2", 6 }, { "1hswordmythicpal.1hswordmythic3", 6 }, { "1hswordmythicpal.1hswordmythic4", 6 },
            { "1hswordmythicpal.1hswordmythic5", 7 }, { "1hswordmythicpal.1hswordmythic6", 7 }, { "1hswordmythicpal.1hswordmythic7", 7 },
            { "1hswordmythicpal.1hswordmythic8", 6 }, { "2haxemythicpal.2haxemythic1", 6 }, { "2haxemythicpal.2haxemythic100", 1 },
            { "2haxemythicpal.2haxemythic101", 1 }, { "2haxemythicpal.2haxemythic102", 1 }, { "2haxemythicpal.2haxemythic103", 1 },
            { "2haxemythicpal.2haxemythic104", 1 }, { "2haxemythicpal.2haxemythic105", 1 }, { "2haxemythicpal.2haxemythic106", 2 },
            { "2haxemythicpal.2haxemythic107", 1 }, { "2haxemythicpal.2haxemythic2", 6 }, { "2haxemythicpal.2haxemythic3", 6 },
            { "2haxemythicpal.2haxemythic4", 6 }, { "2haxemythicpal.2haxemythic5", 6 }, { "2haxemythicpal.2haxemythic6", 6 },
            { "2haxemythicpal.2haxemythic7", 7 }, { "2haxemythicpal.2haxemythic8", 6 }, { "2hcannonmythicpal.2hcannonmythic1", 6 },
            { "2hcannonmythicpal.2hcannonmythic2", 6 }, { "2hcannonmythicpal.2hcannonmythic3", 6 }, { "2hcrossbowmythicpal.2hcrossbowmythic1", 6 },
            { "2hcrossbowmythicpal.2hcrossbowmythic2", 6 }, { "2hcrossbowmythicpal.2hcrossbowmythic3", 6 }, { "2hcrossbowmythicpal.2hcrossbowmythic4", 6 },
            { "2hcrossbowmythicpal.2hcrossbowmythic5", 7 }, { "2hgunmythicpal.2hgunmythic1", 6 }, { "2hgunmythicpal.2hgunmythic1000", 3 },
            { "2hgunmythicpal.2hgunmythic2", 6 }, { "2hgunmythicpal.2hgunmythic3", 6 }, { "2hgunmythicpal.2hgunmythic4", 6 },
            { "2hgunmythicpal.2hgunmythic5", 6 }, { "2hgunmythicpal.2hgunmythic6", 6 }, { "2hmacemythicpal.2hmacemythic1", 6 },
            { "2hmacemythicpal.2hmacemythic2", 6 }, { "2hmacemythicpal.2hmacemythic3", 6 }, { "2hmacemythicpal.2hmacemythic4", 6 },
            { "2hmacemythicpal.2hmacemythic5", 6 }, { "2hmacemythicpal.2hmacemythic6", 6 }, { "2hpickmythicpal.2hpickmythic1", 6 },
            { "2hpickmythicpal.2hpickmythic100", 1 }, { "2hpickmythicpal.2hpickmythic101", 1 }, { "2hpickmythicpal.2hpickmythic102", 1 },
            { "2hpickmythicpal.2hpickmythic103", 1 }, { "2hpickmythicpal.2hpickmythic104", 1 }, { "2hpickmythicpal.2hpickmythic2", 6 },
            { "2hpickmythicpal.2hpickmythic3", 6 }, { "2hstaffmythicpal.2hstaffmythic1", 6 }, { "2hstaffmythicpal.2hstaffmythic1000", 4 },
            { "2hstaffmythicpal.2hstaffmythic2", 6 }, { "2hstaffmythicpal.2hstaffmythic3", 6 }, { "2hswordmythicpal.2hswordmythic1", 6 },
            { "2hswordmythicpal.2hswordmythic100", 1 }, { "2hswordmythicpal.2hswordmythic2", 6 }, { "2hswordmythicpal.2hswordmythic3", 6 },
            { "2hswordmythicpal.2hswordmythic4", 6 },
            // ═══ MythicPAL Armor (modSlots = 1 flags + directMods, NO inherited from BaseArmorClasses) ═══
            { "chainmythicpal.chainmythicarmor1", 5 }, { "chainmythicpal.chainmythicboots1", 5 },
            { "chainmythicpal.chainmythicgloves1", 5 }, { "chainmythicpal.chainmythichelm1", 5 },
            { "chainmythicpal.chainmythicshoulders1", 5 },
            // ═══ MythicPAL Shields (+1 inherited from BaseShieldClasses) ═══
            { "chainmythicpal.chainmythicshield1", 6 },
            { "crystalmythicpal.crystalmythicarmor1", 7 }, { "crystalmythicpal.crystalmythicarmor1000", 4 },
            { "crystalmythicpal.crystalmythicarmor1001", 4 }, { "crystalmythicpal.crystalmythicarmor2", 7 }, { "crystalmythicpal.crystalmythicboots1", 7 },
            { "crystalmythicpal.crystalmythicboots1000", 4 }, { "crystalmythicpal.crystalmythicboots1001", 4 }, { "crystalmythicpal.crystalmythicboots2", 7 },
            { "crystalmythicpal.crystalmythicgloves1", 7 }, { "crystalmythicpal.crystalmythicgloves1000", 4 }, { "crystalmythicpal.crystalmythicgloves1001", 4 },
            { "crystalmythicpal.crystalmythicgloves2", 7 }, { "crystalmythicpal.crystalmythichelm1", 7 }, { "crystalmythicpal.crystalmythichelm1000", 4 },
            { "crystalmythicpal.crystalmythichelm1001", 4 }, { "crystalmythicpal.crystalmythichelm2", 7 }, { "crystalmythicpal.crystalmythicshield1", 7 },
            { "crystalmythicpal.crystalmythicshoulders1", 7 }, { "crystalmythicpal.crystalmythicshoulders1000", 4 }, { "crystalmythicpal.crystalmythicshoulders1001", 4 },
            { "crystalmythicpal.crystalmythicshoulders2", 7 }, { "leathermythicpal.leathermythicarmor1", 7 }, { "leathermythicpal.leathermythicarmor3", 7 },
            { "leathermythicpal.leathermythicboots1", 6 }, { "leathermythicpal.leathermythicboots2", 7 }, { "leathermythicpal.leathermythicboots3", 6 },
            { "leathermythicpal.leathermythicgloves1", 7 }, { "leathermythicpal.leathermythicgloves3", 7 }, { "leathermythicpal.leathermythicgloves4", 7 },
            { "leathermythicpal.leathermythichelm1", 7 }, { "leathermythicpal.leathermythichelm3", 7 }, { "leathermythicpal.leathermythichelm4", 7 },
            { "leathermythicpal.leathermythicshield1", 7 }, { "leathermythicpal.leathermythicshoulders1", 6 }, { "leathermythicpal.leathermythicshoulders3", 6 },
            { "platemythicpal.platemythicarmor1", 6 }, { "platemythicpal.platemythicarmor3", 6 }, { "platemythicpal.platemythicarmor4", 6 },
            { "platemythicpal.platemythicarmor5", 6 }, { "platemythicpal.platemythicboots1", 6 }, { "platemythicpal.platemythicboots3", 6 },
            { "platemythicpal.platemythicboots5", 6 }, { "platemythicpal.platemythicgloves1", 6 }, { "platemythicpal.platemythicgloves3", 6 },
            { "platemythicpal.platemythicgloves5", 6 }, { "platemythicpal.platemythichelm1", 6 }, { "platemythicpal.platemythichelm3", 6 },
            { "platemythicpal.platemythichelm4", 6 }, { "platemythicpal.platemythichelm5", 6 }, { "platemythicpal.platemythichelm6", 6 },
            { "platemythicpal.platemythichelm7", 6 }, { "platemythicpal.platemythicshield1", 6 }, { "platemythicpal.platemythicshield2", 6 },
            { "platemythicpal.platemythicshield3", 7 }, { "platemythicpal.platemythicshoulders1", 6 }, { "platemythicpal.platemythicshoulders3", 6 },
            { "platemythicpal.platemythicshoulders5", 6 }, { "scalemythicpal.scalemythicarmor1", 7 }, { "scalemythicpal.scalemythicboots1", 6 },
            { "scalemythicpal.scalemythicgloves1", 6 }, { "scalemythicpal.scalemythichelm1", 7 }, { "scalemythicpal.scalemythichelm2", 7 },
            { "scalemythicpal.scalemythicshield1", 6 }, { "scalemythicpal.scalemythicshoulders1", 6 }, { "splintmythicpal.splintmythicarmor1", 7 },
            { "splintmythicpal.splintmythicarmor100", 2 }, { "splintmythicpal.splintmythicarmor101", 2 }, { "splintmythicpal.splintmythicboots1", 6 },
            { "splintmythicpal.splintmythicboots100", 2 }, { "splintmythicpal.splintmythicboots101", 2 }, { "splintmythicpal.splintmythicgloves1", 6 },
            { "splintmythicpal.splintmythicgloves100", 2 }, { "splintmythicpal.splintmythicgloves101", 2 }, { "splintmythicpal.splintmythichelm1", 6 },
            { "splintmythicpal.splintmythichelm100", 2 }, { "splintmythicpal.splintmythichelm101", 2 }, { "splintmythicpal.splintmythicshield1", 6 },
            { "splintmythicpal.splintmythicshoulders1", 7 }, { "splintmythicpal.splintmythicshoulders100", 2 }, { "splintmythicpal.splintmythicshoulders101", 2 },
            // ═══ 1H Weapon PreBuilt/Boss/Generated ═══
            { "1haxepal.mythicprebuilt001", 6 }, { "1haxepal.mythicprebuilt002", 6 }, { "1haxepal.mythicprebuilt003", 6 },
            { "1haxepal.mythicprebuilt004", 6 }, { "1haxepal.mythicprebuilt005", 6 }, { "1haxepal.mythicprebuiltboss001", 7 },
            { "1haxepal.generatedmythic001", 1 }, { "1haxepal.generatedmythic001_divine", 1 }, { "1haxepal.generatedmythic001_fire", 1 },
            { "1haxepal.generatedmythic001_ice", 1 }, { "1haxepal.generatedmythic001_poison", 1 }, { "1haxepal.generatedmythic001_shadow", 1 },
            { "1hmacepal.mythicprebuilt001", 6 }, { "1hmacepal.mythicprebuilt002", 6 }, { "1hmacepal.mythicprebuilt003", 6 },
            { "1hmacepal.mythicprebuilt004", 6 }, { "1hmacepal.mythicprebuiltboss001", 6 }, { "1hmacepal.mythicprebuiltboss002", 7 },
            { "1hmacepal.mythicprebuiltboss003", 6 }, { "1hmacepal.mythicprebuiltseasonal001", 7 }, { "1hmacepal.mythicprebuiltwishingwell001", 6 },
            { "1hmacepal.mythicpartialbuiltseasonal001", 4 }, { "1hpickpal.mythicprebuilt001", 6 }, { "1hpickpal.mythicprebuilt002", 6 },
            { "1hpickpal.mythicprebuilt003", 6 }, { "1hpickpal.mythicprebuilt004", 6 }, { "1hstaffpal.mythicprebuilt001", 7 },
            { "1hstaffpal.mythicprebuilt002", 7 }, { "1hstaffpal.mythicprebuilt003", 7 }, { "1hstaffpal.mythicprebuilt004", 6 },
            { "1hstaffpal.mythicprebuiltboss001", 7 }, { "1hstaffpal.mythicprebuiltboss002", 6 }, { "1hswordpal.mythicprebuilt001", 6 },
            { "1hswordpal.mythicprebuilt002", 6 }, { "1hswordpal.mythicprebuilt003", 6 }, { "1hswordpal.mythicprebuilt004", 6 },
            { "1hswordpal.mythicprebuilt005", 7 }, { "1hswordpal.mythicprebuilt006", 7 }, { "1hswordpal.mythicprebuilt007", 6 },
            { "1hswordpal.mythicprebuiltboss001", 7 }, { "1hswordpal.generatedmythic001", 1 }, { "1hswordpal.generatedmythic001_divine", 1 },
            { "1hswordpal.generatedmythic001_fire", 1 }, { "1hswordpal.generatedmythic001_ice", 1 }, { "1hswordpal.generatedmythic001_poison", 1 },
            { "1hswordpal.generatedmythic001_shadow", 1 },
            // ═══ Fighter Class Armor (PartialBuilt) ═══
            { "fighterbodypal.partialbuiltmythic001", 5 }, { "fighterbodypal.partialbuiltmythicseasonal001", 4 }, { "fighterbodypal.partialbuiltunique001", 4 },
            { "fighterbodypal.partialbuiltuniqueseasonal001", 3 }, { "fighterbootspal.partialbuiltmythic001", 5 }, { "fighterbootspal.partialbuiltmythicseasonal001", 4 },
            { "fighterbootspal.partialbuiltunique001", 4 }, { "fighterbootspal.partialbuiltuniqueseasonal001", 3 }, { "fighterglovespal.partialbuiltmythic001", 5 },
            { "fighterglovespal.partialbuiltmythicseasonal001", 4 }, { "fighterglovespal.partialbuiltunique001", 4 }, { "fighterglovespal.partialbuiltuniqueseasonal001", 3 },
            { "fightershoulderspal.partialbuiltmythic001", 5 }, { "fightershoulderspal.partialbuiltmythicseasonal001", 4 }, { "fightershoulderspal.partialbuiltunique001", 4 },
            { "fightershoulderspal.partialbuiltuniqueseasonal001", 3 }, { "fighterhelmpal.partialbuiltmythic001", 5 }, { "fighterhelmpal.partialbuiltmythicseasonal001", 4 },
            { "fighterhelmpal.partialbuiltunique001", 4 }, { "fighterhelmpal.partialbuiltuniqueseasonal001", 3 }, { "fighterhelmpal.partialbuiltmythicseasonal002", 5 },
            { "fighterhelmpal.partialbuiltseasonal001", 3 }, { "fightershieldpal.partialbuiltmythic001", 5 }, { "fightershieldpal.partialbuiltmythicseasonal001", 4 },
            { "fightershieldpal.partialbuiltunique001", 4 }, { "fightershieldpal.partialbuiltuniqueseasonal001", 4 },
            // ═══ Mage Class Armor (PreBuilt/PartialBuilt/Generated) ═══
            { "magebodypal.generatedboss001", 2 }, { "magebodypal.generatedboss002", 2 }, { "magebodypal.generatedmythic001", 2 },
            { "magebodypal.generatedmythic002", 2 }, { "magebodypal.generatedmythic003", 2 }, { "magebodypal.generatedmythic004", 2 },
            { "magebodypal.generatedwishingwell001", 2 }, { "magebodypal.partialbuiltmythicseasonal001", 3 }, { "magebodypal.partialbuiltuniqueseasonal001", 4 },
            { "magebodypal.prebuiltboss001", 7 }, { "magebodypal.prebuiltmythic001", 6 }, { "magebodypal.prebuiltmythic002", 6 },
            { "magebodypal.prebuiltmythic003", 6 }, { "magebodypal.prebuiltmythic004", 6 }, { "magebodypal.prebuiltwishingwell001", 6 },
            { "magebootspal.generatedmythic001", 2 }, { "magebootspal.generatedmythic002", 2 }, { "magebootspal.generatedmythic003", 2 },
            { "magebootspal.generatedmythic004", 2 }, { "magebootspal.generatedwishingwell001", 2 }, { "magebootspal.partialbuiltmythicseasonal001", 3 },
            { "magebootspal.partialbuiltuniqueseasonal001", 4 }, { "magebootspal.prebuiltmythic001", 6 }, { "magebootspal.prebuiltmythic002", 6 },
            { "magebootspal.prebuiltmythic003", 6 }, { "magebootspal.prebuiltmythic004", 6 }, { "magebootspal.prebuiltmythic005", 6 },
            { "magebootspal.prebuiltwishingwell001", 6 }, { "mageglovespal.generatedmythic001", 2 }, { "mageglovespal.generatedmythic002", 2 },
            { "mageglovespal.generatedmythic003", 2 }, { "mageglovespal.generatedmythic004", 2 }, { "mageglovespal.generatedwishingwell001", 2 },
            { "mageglovespal.partialbuiltmythicseasonal001", 3 }, { "mageglovespal.partialbuiltuniqueseasonal001", 4 }, { "mageglovespal.prebuiltmythic001", 6 },
            { "mageglovespal.prebuiltmythic002", 6 }, { "mageglovespal.prebuiltmythic003", 6 }, { "mageglovespal.prebuiltmythic004", 6 },
            { "mageglovespal.prebuiltwishingwell001", 6 }, { "magehelmpal.generatedboss001", 2 }, { "magehelmpal.generatedboss002", 2 },
            { "magehelmpal.generatedmythic001", 2 }, { "magehelmpal.generatedmythic002", 2 }, { "magehelmpal.generatedmythic003", 2 },
            { "magehelmpal.generatedmythic004", 2 }, { "magehelmpal.generatedwishingwell001", 2 }, { "magehelmpal.generatedwishingwell002", 2 },
            { "magehelmpal.partialbuiltmythicseasonal001", 3 }, { "magehelmpal.partialbuiltmythicseasonal002", 5 }, { "magehelmpal.partialbuiltseasonal001", 3 },
            { "magehelmpal.partialbuiltuniqueseasonal001", 4 }, { "magehelmpal.partialbuiltwishingwell001", 4 }, { "magehelmpal.partialbuiltwishingwell002", 4 },
            { "magehelmpal.partialbuiltwishingwell003", 4 }, { "magehelmpal.prebuiltboss001", 7 }, { "magehelmpal.prebuiltmythic001", 6 },
            { "magehelmpal.prebuiltmythic002", 7 }, { "magehelmpal.prebuiltmythic003", 7 }, { "magehelmpal.prebuiltmythic004", 7 },
            { "magehelmpal.prebuiltmythic005", 6 }, { "magehelmpal.prebuiltwishingwell001", 7 }, { "mageshoulderspal.generatedmythic001", 2 },
            { "mageshoulderspal.generatedmythic002", 2 }, { "mageshoulderspal.generatedmythic003", 2 }, { "mageshoulderspal.generatedmythic004", 2 },
            { "mageshoulderspal.generatedwishingwell001", 2 }, { "mageshoulderspal.partialbuiltmythicseasonal001", 3 }, { "mageshoulderspal.partialbuiltuniqueseasonal001", 4 },
            { "mageshoulderspal.prebuiltmythic001", 6 }, { "mageshoulderspal.prebuiltmythic002", 6 }, { "mageshoulderspal.prebuiltmythic003", 6 },
            { "mageshoulderspal.prebuiltwishingwell001", 6 },
            // ═══ Mage Class Shields ═══
            { "mageshieldpal.generatedmythic001", 2 }, { "mageshieldpal.generateduniqueseasonal001", 2 }, { "mageshieldpal.prebuiltboss001", 6 },
            { "mageshieldpal.prebuiltmythic001", 6 }, { "mageshieldpal.prebuiltmythic002", 6 }, { "mageshieldpal.prebuiltmythic003", 6 },
            { "mageshieldpal.prebuiltmythicseasonal001", 8 },
            // ═══ Ranger Class Armor (PreBuilt/PartialBuilt) ═══
            { "rangerbodypal.partialbuiltunique001", 3 }, { "rangerbodypal.prebuiltmythic001", 7 }, { "rangerbootspal.partialbuiltunique001", 3 },
            { "rangerglovespal.partialbuiltunique001", 3 }, { "rangerhelmpal.partialbuiltmythicseasonal001", 2 }, { "rangerhelmpal.partialbuiltmythicseasonal002", 5 },
            { "rangerhelmpal.partialbuiltseasonal001", 3 }, { "rangerhelmpal.partialbuiltunique001", 3 }, { "rangershoulderspal.partialbuiltunique001", 3 },
        };

        private static bool _initialized = false;

        private const int NATIVE_REFRESH_TIMER_TICKS = 0x2328;
        private const double REFRESH_TIMER_TICKS_PER_SECOND = 30.0;
        public const float DEFAULT_REFRESH_INTERVAL_SECONDS = (float)(NATIVE_REFRESH_TIMER_TICKS / REFRESH_TIMER_TICKS_PER_SECOND);
        private const double REFRESH_ADD_DELAY_SECONDS = 0x000F / REFRESH_TIMER_TICKS_PER_SECOND;

        /// <summary>
        /// Initialize the merchant system - call this after DatabaseLoader.LoadAll()
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;

            Debug.LogError("[MerchantManager] ═══════════════════════════════════════════════════════════");
            Debug.LogError("[MerchantManager] INITIALIZING MERCHANT SYSTEM");
            Debug.LogError("[MerchantManager] ═══════════════════════════════════════════════════════════");

            LoadItemDimensions();
            LoadSellableItems();
            LoadMerchants();
            InitializeRuntimeMerchants();

            _initialized = true;
        }

        public static void ValidateAllSellableItems()
        {
            Debug.LogError("[MerchantManager] ═══ VALIDATING ALL SELLABLE ITEMS ═══");
            int bad = 0;
            int notInDb = 0;
            foreach (var item in _sellableItems)
            {
                string lookup = item.gcType.ToLowerInvariant();
                if (lookup.StartsWith("items.pal."))
                    lookup = lookup.Substring(10);

                var data = DatabaseLoader.FindItem(lookup);
                if (data == null)
                {
                    Debug.LogError($"[VALIDATE] ❌ NOT IN DB: {item.gcType} (lookup: {lookup})");
                    notInDb++;
                    bad++;
                    continue;
                }

                if (data.modCount < 0 || data.modCount > 10)
                {
                    Debug.LogError($"[VALIDATE] ❌ BAD modCount={data.modCount}: {item.gcType}");
                    bad++;
                }

                if (data.inventoryWidth <= 0 || data.inventoryHeight <= 0)
                {
                    Debug.LogError($"[VALIDATE] ⚠️ BAD dimensions {data.inventoryWidth}x{data.inventoryHeight}: {item.gcType}");
                    bad++;
                }

                // Check if Shield/Shoulder/Buckler/Pauldron — log details for debugging
                string lower = item.gcType.ToLowerInvariant();
                if (lower.Contains("shield") || lower.Contains("buckler") || lower.Contains("shoulder") || lower.Contains("pauldron"))
                {
                    Debug.LogError($"[VALIDATE] 🛡️ SHIELD/SHOULDER: {item.gcType} modCount={data.modCount} dims={data.inventoryWidth}x{data.inventoryHeight} slot={data.slotType ?? "NULL"}");
                }
            }
            Debug.LogError($"[MerchantManager] ═══ VALIDATION DONE: {_sellableItems.Count} items, {bad} problems ({notInDb} not in DB) ═══");
        }

        #region Loading


        public static void ResetAllTimers()
        {
            foreach (var key in _lastRegeneration.Keys.ToList())
            {
                _lastRegeneration[key] = DateTime.UtcNow;
            }
            Debug.LogError("[MerchantManager] ⏱️ All timers reset at server ready");
        }





        private static void LoadSellableItems()
        {
            try
            {
                _sellableItems = new List<SellableItem>();
                using (var conn = DungeonRunners.Database.GameDatabase.GetConnection())
                using (var reader = DungeonRunners.Database.GameDatabase.ExecuteReader(conn, "SELECT gc_type, name, gold_value, gc_gold_value FROM sellable_items"))
                {
                    while (reader.Read())
                    {
                        _sellableItems.Add(new SellableItem
                        {
                            gcType = DungeonRunners.Database.GameDatabase.GetString(reader, "gc_type"),
                            name = DungeonRunners.Database.GameDatabase.GetString(reader, "name"),
                            goldValue = DungeonRunners.Database.GameDatabase.GetFloat(reader, "gold_value"),
                            gcGoldValue = DungeonRunners.Database.GameDatabase.GetFloat(reader, "gc_gold_value")
                        });
                    }
                }
                Debug.LogError($"[MerchantManager] Loaded {_sellableItems.Count} sellable items from SQLite");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[MerchantManager] SQLite sellable_items error: {ex.Message}");
            }
        }

        private static void LoadItemDimensions()
        {
            try
            {
                using (var conn = DungeonRunners.Database.GameDatabase.GetConnection())
                using (var reader = DungeonRunners.Database.GameDatabase.ExecuteReader(conn,
                    "SELECT gc_type, width, height FROM item_dimensions"))
                {
                    while (reader.Read())
                    {
                        string key = reader.GetString(0);
                        _itemDimensions[key] = new ItemDimensions
                        {
                            width = reader.GetInt32(1),
                            height = reader.GetInt32(2)
                        };
                    }
                }
                Debug.LogError($"[MerchantManager] ✅ Loaded {_itemDimensions.Count} item dimensions from SQLite");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[MerchantManager] ❌ Item dimensions error: {ex.Message}");
            }
        }

        private static void LoadMerchants()
        {
            _merchants.Clear();

            if (DatabaseLoader.Merchants == null || DatabaseLoader.Merchants.Count == 0)
            {
                Debug.LogError("[MerchantManager] ⚠️ No merchants loaded from database!");
                return;
            }

            foreach (var merchantData in DatabaseLoader.Merchants)
            {
                _merchants[merchantData.npcGcType] = merchantData;
                Debug.LogError($"[MerchantManager] ✓ Loaded merchant: {merchantData.npcGcType}");

                foreach (var inv in merchantData.inventories)
                {
                    float regenInterval = inv.regenerateIntervalSeconds > 0 ? inv.regenerateIntervalSeconds : DEFAULT_REFRESH_INTERVAL_SECONDS;
                    string invType = inv.staticContents ? "STATIC" : $"DYNAMIC (regen every {regenInterval}s)";
                    Debug.LogError($"[MerchantManager]   - {inv.name} (ID={inv.id}): {invType}, {inv.items.Count} items");
                }
            }

            Debug.LogError($"[MerchantManager] ✅ Loaded {_merchants.Count} merchants");
        }

        private static void InitializeRuntimeMerchants()
        {
            _runtimeMerchants.Clear();
            _lastRegeneration.Clear();

            foreach (var kvp in _merchants)
            {
                var merchantData = kvp.Value;
                var runtimeMerchant = CreateRuntimeMerchant(merchantData);
                _runtimeMerchants[kvp.Key] = runtimeMerchant;
            }

            Debug.LogError($"[MerchantManager] ✅ Initialized {_runtimeMerchants.Count} runtime merchants");
        }

        private static MerchantRuntimeData CreateRuntimeMerchant(MerchantData merchantData)
        {
            var runtime = new MerchantRuntimeData
            {
                npcGcType = merchantData.npcGcType,
                merchantGcType = merchantData.merchantGcType,
                inventories = new List<MerchantInventoryRuntimeData>()
            };
            foreach (var invData in merchantData.inventories)
            {
                string itemGenerator = invData.itemGenerator;
                int minItemLevel = invData.minItemLevel;
                int maxItemLevel = invData.maxItemLevel;
                float regenerateIntervalSeconds = invData.regenerateIntervalSeconds > 0 ? invData.regenerateIntervalSeconds : DEFAULT_REFRESH_INTERVAL_SECONDS;
                string label = invData.label;
                ApplyNativeMerchantInventoryOverrides(merchantData.npcGcType, invData, ref itemGenerator, ref minItemLevel, ref maxItemLevel, ref regenerateIntervalSeconds, ref label);

                var runtimeInv = new MerchantInventoryRuntimeData
                {
                    name = invData.name,
                    gcType = invData.gcType,
                    id = invData.id,
                    label = label,
                    width = invData.width,
                    height = invData.height,
                    staticContents = invData.staticContents,
                    autoGenerateItems = invData.autoGenerateItems,
                    itemGenerator = itemGenerator,
                    minItemLevel = minItemLevel,
                    maxItemLevel = maxItemLevel,
                    regenerateIntervalSeconds = regenerateIntervalSeconds,
                    items = new List<MerchantItemRuntimeData>()
                };
                if (invData.staticContents)
                {
                    foreach (var item in invData.items)
                    {
                        // Rarity from tier suffix
                        int tier = RarityHelper.GetTierFromGcType(item.gcType);
                        var itemRarity = RarityHelper.GetRarityFromTier(tier);

                        // FIX: MythicPAL items have NO -N suffix → force Mythic rarity
                        if (RarityHelper.IsMythicPALItem(item.gcType) || IsEnabledMythicItem(item.gcType))
                            itemRarity = ItemRarity.Mythic;

                        // USE SAME LEVEL AS WriteItem SENDS TO CLIENT
                        // Static init has no player level — mythic level set at generation time in GenerateInventoryItems
                        int level = RarityHelper.GetItemLevel(item.gcType);

                        // Get GoldValue — try DB first (gc_gold_value), fallback to name-based
                        float baseGoldValue = RarityHelper.GetBaseGoldValue(item.gcType);
                        if (_sellableItems != null)
                        {
                            string lookupType = item.gcType.ToLowerInvariant();
                            if (lookupType.StartsWith("items.pal."))
                                lookupType = lookupType.Substring(10);
                            var sellable = _sellableItems.FirstOrDefault(s =>
                                string.Equals(s.gcType, lookupType, StringComparison.OrdinalIgnoreCase) ||
                                s.gcType.EndsWith(lookupType, StringComparison.OrdinalIgnoreCase));
                            if (sellable != null && sellable.gcGoldValue > 0)
                                baseGoldValue = sellable.gcGoldValue;
                        }

                        uint itemPrice = RarityHelper.CalculateBuyPrice(level, itemRarity, baseGoldValue);

                        runtimeInv.items.Add(new MerchantItemRuntimeData
                        {
                            gcType = item.gcType,
                            inventoryX = item.inventoryX,
                            inventoryY = item.inventoryY,
                            id = item.id,  // Use item_slot_id from DB
                            quantity = item.quantity > 0 ? item.quantity : 1,
                            level = level,
                            rarity = itemRarity,
                            price = itemPrice,
                            goldValue = baseGoldValue,
                            scaleMod = RarityHelper.GetRandomScaleMod(itemRarity)
                        });
                        // Keep nextItemId above any static ID to avoid collisions
                        if (item.id >= runtime.nextItemId)
                            runtime.nextItemId = item.id + 1;
                    }
                    // If this static inventory has quest items, mark it for server-side sending
                    // with hasItems=true. This gives quest items unique IDs (500+) that don't
                    // collide with other static tabs' GC-hardcoded IDs (255-262).
                    // Disassembly of getItemByID confirms the client returns the FIRST match
                    // across all inventories — unique IDs are the only way to disambiguate.
                    bool hasQuestItems = runtimeInv.items.Any(i =>
                        i.gcType.IndexOf("QuestItem", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (hasQuestItems)
                    {
                        runtimeInv.serverSendsItems = true;
                        // Assign unique IDs starting at 500 to avoid collision with GC-hardcoded 255-262
                        int nextQuestId = 500;
                        foreach (var qi in runtimeInv.items)
                        {
                            qi.id = nextQuestId++;
                            Debug.LogError($"[MerchantManager] Quest item '{qi.gcType}' assigned unique ID={qi.id}");
                        }
                        if (nextQuestId > runtime.nextItemId)
                            runtime.nextItemId = nextQuestId;
                    }
                }
                else if (invData.autoGenerateItems)
                {
                    GenerateInventoryItems(runtime, runtimeInv, invData, 1);
                    string regenKey = $"{merchantData.npcGcType}_{invData.id}";
                    _lastRegeneration[regenKey] = DateTime.UtcNow;
                }
                runtime.inventories.Add(runtimeInv);
            }
            return runtime;
        }

        private static void ApplyNativeMerchantInventoryOverrides(string npcGcType, MerchantInventoryData invData, ref string itemGenerator, ref int minItemLevel, ref int maxItemLevel, ref float regenerateIntervalSeconds, ref string label)
        {
            if (string.Equals(npcGcType, "world.tutorial.npc.HermitVendor", StringComparison.OrdinalIgnoreCase) && invData.id == 2)
            {
                itemGenerator = "MerchantSuperiorIG";
                minItemLevel = 3;
                maxItemLevel = 10;
                regenerateIntervalSeconds = DEFAULT_REFRESH_INTERVAL_SECONDS;
            }

            // Townstone weapon vendors (Hughard / VendorWeapon2 / VendorWeapon3):
            // restore native 300s regen interval (DB had Tim's invented 180s); rename
            // tab 3 from "Superior" to retail label "Scrap Heap" per Kubjas's memory.
            if (string.Equals(npcGcType, "world.town.npc.VendorWeapon1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(npcGcType, "world.town.npc.VendorWeapon2", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(npcGcType, "world.town.npc.VendorWeapon3", StringComparison.OrdinalIgnoreCase))
            {
                regenerateIntervalSeconds = DEFAULT_REFRESH_INTERVAL_SECONDS;
                if (invData.id == 3)
                    label = "Scrap Heap";
            }

            // Townstone weapon vendors level range cascade — symmetric across all 3 tabs
            // per Kubjas's retail memory. V1 locked at 3-20 by direct recall; V2/V3 set
            // to a clean overlapping cascade since the client carries no per-vendor level
            // data (it's locked inside game.pkg). Boundary overlap at 20 (V1↔V2) and 40-50
            // (V2↔V3) is standard MMO design.
            if (string.Equals(npcGcType, "world.town.npc.VendorWeapon1", StringComparison.OrdinalIgnoreCase))
            {
                minItemLevel = 3;
                maxItemLevel = 20;
            }
            else if (string.Equals(npcGcType, "world.town.npc.VendorWeapon2", StringComparison.OrdinalIgnoreCase))
            {
                minItemLevel = 20;
                maxItemLevel = 50;
            }
            else if (string.Equals(npcGcType, "world.town.npc.VendorWeapon3", StringComparison.OrdinalIgnoreCase))
            {
                minItemLevel = 40;
                maxItemLevel = 100;
            }
        }

        #endregion

        #region Refresh Timer System

        /// <summary>
        /// Check if any inventories need regeneration and regenerate them.
        /// Returns TRUE if any inventory was regenerated (caller should push update to clients).
        /// </summary>
        public static bool CheckAndRegenerateInventories(string npcGcType)
        {
            if (!_runtimeMerchants.TryGetValue(npcGcType, out var runtimeMerchant))
                return false;
            if (!_merchants.TryGetValue(npcGcType, out var merchantData))
                return false;

            var now = DateTime.UtcNow;
            bool anyRegenerated = false;

            for (int i = 0; i < runtimeMerchant.inventories.Count; i++)
            {
                var runtimeInv = runtimeMerchant.inventories[i];

                if (runtimeInv.staticContents)
                    continue;

                string regenKey = $"{npcGcType}_{runtimeInv.id}";

                if (!_lastRegeneration.TryGetValue(regenKey, out var lastRegen))
                {
                    lastRegen = DateTime.MinValue;
                }

                float intervalSeconds = runtimeInv.regenerateIntervalSeconds > 0 ? runtimeInv.regenerateIntervalSeconds : DEFAULT_REFRESH_INTERVAL_SECONDS;
                var elapsed = (now - lastRegen).TotalSeconds;

                if (elapsed >= intervalSeconds)
                {
                    Debug.LogError($"[MerchantManager] 🔄 REGENERATING inventory '{runtimeInv.name}' for {npcGcType}");

                    var invData = merchantData.inventories.FirstOrDefault(inv => inv.id == runtimeInv.id);

                    if (invData != null)
                    {
                        GenerateInventoryItems(runtimeMerchant, runtimeInv, invData, runtimeInv.generatedForLevel > 0 ? runtimeInv.generatedForLevel : 100);
                    }

                    _lastRegeneration[regenKey] = now;
                    anyRegenerated = true;
                }
            }

            return anyRegenerated;
        }

        /// <summary>
        /// Write a merchant refresh update packet. This resends the full merchant component data
        /// as a ComponentUpdate (0x35) so clients receive fresh inventory after timer expires.
        /// </summary>
        public static void WriteMerchantRefreshUpdate(LEWriter writer, string npcGcType, ushort merchantComponentId)
        {
            if (!_runtimeMerchants.TryGetValue(npcGcType, out var runtimeMerchant))
            {
                Debug.LogError($"[MerchantManager] ❌ No runtime merchant for {npcGcType}");
                return;
            }

            Debug.LogError($"[MerchantManager] 📤 Writing refresh update for {npcGcType}");

            // ComponentUpdate header
            writer.WriteByte(0x35);                      // ComponentUpdate opcode
            writer.WriteUInt16(merchantComponentId);     // Merchant component ID
            writer.WriteByte(0x1E);                      // AddItem submessage (Container::processUpdate handles 0x1E)

            // Actually, let's try a different approach - send inventory contents directly
            // The client expects items to be added back after regeneration

            // Find the dynamic inventory
            foreach (var inventory in runtimeMerchant.inventories)
            {
                if (inventory.staticContents || inventory.items.Count == 0)
                    continue;

                Debug.LogError($"[MerchantManager] 📤 Refresh inventory '{inventory.name}': {inventory.items.Count} items");

                // Write inventory ID
                writer.WriteByte((byte)inventory.id);

                // Write item count
                writer.WriteByte((byte)inventory.items.Count);

                // Write each item
                foreach (var item in inventory.items)
                {
                    WriteItem(writer, item);
                }
            }
        }

        /// <summary>
        /// Get all merchant NPC types that have dynamic inventories
        /// </summary>
        public static List<string> GetDynamicMerchantTypes()
        {
            var result = new List<string>();
            foreach (var kvp in _runtimeMerchants)
            {
                bool hasDynamic = kvp.Value.inventories.Any(inv => !inv.staticContents && inv.autoGenerateItems);
                if (hasDynamic)
                {
                    result.Add(kvp.Key);
                }
            }
            return result;
        }

        /// <summary>
        /// Get remaining seconds until next regeneration for an inventory.
        /// Returns 0 for static inventories.
        /// </summary>
        public static int GetSecondsUntilRegeneration(string npcGcType, int inventoryId)
        {
            int ticks = GetTicksUntilRegeneration(npcGcType, inventoryId);
            return Math.Max(0, (int)Math.Ceiling(ticks / REFRESH_TIMER_TICKS_PER_SECOND));
        }

        public static int GetTicksUntilRegeneration(string npcGcType, int inventoryId)
        {
            string regenKey = $"{npcGcType}_{inventoryId}";

            if (!_runtimeMerchants.TryGetValue(npcGcType, out var runtimeMerchant))
                return 0;

            var inventory = runtimeMerchant.inventories.FirstOrDefault(inv => inv.id == inventoryId);
            if (inventory == null || inventory.staticContents)
                return 0;

            float intervalSeconds = inventory.regenerateIntervalSeconds > 0 ? inventory.regenerateIntervalSeconds : DEFAULT_REFRESH_INTERVAL_SECONDS;

            if (!_lastRegeneration.TryGetValue(regenKey, out var lastRegen))
            {
                return Math.Max(0, (int)Math.Round(intervalSeconds * REFRESH_TIMER_TICKS_PER_SECOND));
            }

            var elapsed = (DateTime.UtcNow - lastRegen).TotalSeconds;
            var remainingTicks = (intervalSeconds - elapsed) * REFRESH_TIMER_TICKS_PER_SECOND;

            return Math.Max(0, (int)Math.Ceiling(remainingTicks));
        }

        /// <summary>
        /// Force regenerate all dynamic inventories for a merchant immediately.
        /// </summary>
        public static void ForceRegenerate(string npcGcType, int playerLevel = 100)
        {
            if (!_runtimeMerchants.TryGetValue(npcGcType, out var runtimeMerchant))
                return;

            if (!_merchants.TryGetValue(npcGcType, out var merchantData))
                return;

            foreach (var runtimeInv in runtimeMerchant.inventories)
            {
                if (runtimeInv.staticContents)
                    continue;

                var invData = merchantData.inventories.FirstOrDefault(inv => inv.id == runtimeInv.id);
                if (invData != null)
                {
                    Debug.LogError($"[MerchantManager] 🔄 FORCE regenerating inventory '{runtimeInv.name}' for {npcGcType} (playerLevel={playerLevel})");
                    GenerateInventoryItems(runtimeMerchant, runtimeInv, invData, playerLevel);

                    SetLastRegeneration(npcGcType, runtimeInv.id, DateTime.UtcNow);
                }
            }
        }

        /// <summary>
        /// Called before sending merchant inventory to a player.
        /// Regenerates dynamic inventories if the player's level differs from what was generated.
        /// This implements the ItemTimeline system — merchants show level-appropriate items.
        /// Returns true if inventory was regenerated (caller should rebuild packet).
        /// </summary>
        public static bool EnsureInventoryForLevel(string npcGcType, int playerLevel)
        {
            if (!_runtimeMerchants.TryGetValue(npcGcType, out var runtimeMerchant))
                return false;

            if (!_merchants.TryGetValue(npcGcType, out var merchantData))
                return false;

            bool anyRegenerated = false;

            foreach (var runtimeInv in runtimeMerchant.inventories)
            {
                if (runtimeInv.staticContents)
                    continue;

                // Regenerate if player level is different from what we generated for
                if (runtimeInv.generatedForLevel != playerLevel)
                {
                    var invData = merchantData.inventories.FirstOrDefault(inv => inv.id == runtimeInv.id);
                    if (invData != null)
                    {
                        Debug.LogError($"[MerchantManager] 🔄 ItemTimeline: regenerating '{runtimeInv.name}' for player level {playerLevel} (was level {runtimeInv.generatedForLevel})");
                        GenerateInventoryItems(runtimeMerchant, runtimeInv, invData, playerLevel);
                        SetLastRegeneration(npcGcType, runtimeInv.id, DateTime.UtcNow);

                        anyRegenerated = true;
                    }
                }
            }
            return anyRegenerated;
        }

        private static bool UsesGeneratedRefresh(MerchantInventoryRuntimeData inventory)
        {
            return inventory != null
                && !inventory.staticContents
                && inventory.autoGenerateItems
                && !string.IsNullOrWhiteSpace(inventory.itemGenerator);
        }

        private static void SetLastRegeneration(string npcGcType, int inventoryId, DateTime now)
        {
            _lastRegeneration[$"{npcGcType}_{inventoryId}"] = now;
        }

        #endregion

        public static (int width, int height) GetItemDimensions(string gcType)
        {
            string lookupType = gcType;

            if (lookupType.StartsWith("items.pal.", StringComparison.OrdinalIgnoreCase))
            {
                lookupType = lookupType.Substring(10);
            }
            else if (lookupType.StartsWith("items.consumables.", StringComparison.OrdinalIgnoreCase))
            {
                lookupType = lookupType.Substring(18);
            }

            lookupType = lookupType.ToLowerInvariant();

            // Consumables (potions, scrolls, town portal) and quest items are always 1x1
            if (lookupType.Contains("potion") || lookupType.Contains("scroll") ||
                lookupType.Contains("consumable") || lookupType.Contains("townportal") ||
                lookupType.Contains("questitem") ||
                gcType.IndexOf("consumables", StringComparison.OrdinalIgnoreCase) >= 0 ||
                gcType.IndexOf("questitem", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return (1, 1);
            }

            var itemData = DatabaseLoader.FindItem(lookupType);
            if (itemData != null && itemData.inventoryWidth > 0 && itemData.inventoryHeight > 0)
            {
                return (itemData.inventoryWidth, itemData.inventoryHeight);
            }

            Debug.LogError($"[DIMS] ⚠️ {gcType} → FALLBACK 2x2 (key={lookupType}, FindItem={itemData != null})");
            return (2, 2);  // fallback - should never hit now
        }

        private static (int x, int y) FindSpot(bool[,] occupied, int gridWidth, int gridHeight, int itemWidth, int itemHeight)
        {
            for (int y = 0; y <= gridHeight - itemHeight; y++)
            {
                for (int x = 0; x <= gridWidth - itemWidth; x++)
                {
                    bool canPlace = true;
                    for (int dx = 0; dx < itemWidth && canPlace; dx++)
                    {
                        for (int dy = 0; dy < itemHeight && canPlace; dy++)
                        {
                            if (occupied[x + dx, y + dy])
                                canPlace = false;
                        }
                    }
                    if (canPlace)
                        return (x, y);
                }
            }
            return (-1, -1);
        }

        /// <summary>
        /// Get the OP5 modCount for a special item from the GC lookup table.
        /// OP5 writes flag byte separately then modCount bytes. Total = 1 + modCount.
        /// Merchant modSlots includes the flag byte. So OP5 modCount = modSlots - 1.
        /// Returns -1 if not in lookup.
        /// </summary>
        public static int GetOP5ModCount(string gcType)
        {
            string key = gcType.ToLowerInvariant();
            if (key.StartsWith("items.pal.")) key = key.Substring(10);
            if (!_mythicModSlots.TryGetValue(key, out int merchantModSlots))
                return -1;

            // OP5 writes flag byte separately, then modCount more bytes.
            // Merchant modSlots includes the flag byte in the count.
            // So: OP5 modCount = merchantModSlots - 1 (for all item types)
            int op5ModCount = merchantModSlots - 1;
            return op5ModCount < 0 ? 0 : op5ModCount;
        }

        #region Item Generation

        private static readonly bool DISABLE_MYTHICPAL = false;
        private static readonly bool ENABLE_1H_WEAPON_PREBUILT = true;
        private static readonly bool ENABLE_FIGHTER_CLASS_ARMOR = true;
        private static readonly bool ENABLE_MAGE_CLASS_ARMOR = true;
        private static readonly bool ENABLE_RANGER_CLASS_ARMOR = true;

        private static bool IsEnabledMythicItem(string gcType)
        {
            if (string.IsNullOrEmpty(gcType)) return false;
            string lower = gcType.ToLowerInvariant();
            if (lower.StartsWith("items.pal.")) lower = lower.Substring(10);
            if (lower.Contains("mythicpal")) return !DISABLE_MYTHICPAL;
            if (!_mythicModSlots.ContainsKey(lower)) return false;
            string pal = lower.Split('.')[0];
            if (ENABLE_1H_WEAPON_PREBUILT && (pal == "1haxepal" || pal == "1hmacepal" || pal == "1hpickpal" || pal == "1hstaffpal" || pal == "1hswordpal"))
                return true;
            if (ENABLE_FIGHTER_CLASS_ARMOR && pal.StartsWith("fighter"))
                return true;
            if (ENABLE_MAGE_CLASS_ARMOR && pal.StartsWith("mage"))
                return true;
            if (ENABLE_RANGER_CLASS_ARMOR && pal.StartsWith("ranger"))
                return true;
            return false;
        }

        private static bool IsWeaponType(string gcType)
        {
            string lower = gcType.ToLowerInvariant();
            return lower.Contains("axe") || lower.Contains("sword") ||
                   lower.Contains("mace") || lower.Contains("pick") ||
                   lower.Contains("staff") || lower.Contains("crossbow") ||
                   lower.Contains("gun") || lower.Contains("cannon");
        }

        private static bool IsArmorType(string gcType)
        {
            string lower = gcType.ToLowerInvariant();
            return lower.Contains("armor") || lower.Contains("boot") ||
                   lower.Contains("glove") || lower.Contains("helm") ||
                   lower.Contains("shoulder") || lower.Contains("pauldron") ||
                   lower.Contains("shield") || lower.Contains("buckler") ||
                   lower.Contains("body") || lower.Contains("chest");
        }

        private static bool IsJewelryType(string gcType)
        {
            string lower = gcType.ToLowerInvariant();
            return lower.Contains("ring") || lower.Contains("amulet");
        }

        private static int RollMerchantStockLevel(MerchantInventoryRuntimeData runtimeInv, System.Random random)
        {
            int minLevel = runtimeInv.minItemLevel > 0 ? runtimeInv.minItemLevel : 1;
            int maxLevel = runtimeInv.maxItemLevel > 0 ? runtimeInv.maxItemLevel : minLevel;
            if (maxLevel < minLevel)
                maxLevel = minLevel;
            if (maxLevel == minLevel)
                return minLevel;
            return random.Next(minLevel, maxLevel + 1);
        }

        private static void GenerateInventoryItems(MerchantRuntimeData merchant, MerchantInventoryRuntimeData runtimeInv, MerchantInventoryData invData, int playerLevel = 100)
        {
            runtimeInv.items.Clear();
            runtimeInv.generatedForLevel = playerLevel;

            // Base filters: regular -N suffix items, OR mythic items recognised by
            // IsEnabledMythicItem (mythics are named like "items.pal.1haxemythicpal.1haxemythic1"
            // — no -N suffix, so they previously failed this filter entirely).
            var safeItems = _sellableItems.Where(i =>
                (System.Text.RegularExpressions.Regex.IsMatch(i.gcType, @"-\d+$") || IsEnabledMythicItem(i.gcType)) &&
                i.gcType.IndexOf("PreBuilt", StringComparison.OrdinalIgnoreCase) < 0 &&
                i.gcType.IndexOf("PartialBuilt", StringComparison.OrdinalIgnoreCase) < 0 &&
                i.gcType.IndexOf("Seasonal", StringComparison.OrdinalIgnoreCase) < 0 &&
                i.gcType.IndexOf("WishingWell", StringComparison.OrdinalIgnoreCase) < 0 &&
                i.gcType.IndexOf("Boss", StringComparison.OrdinalIgnoreCase) < 0 &&
                i.gcType.IndexOf("Generated", StringComparison.OrdinalIgnoreCase) < 0 &&
                !i.gcType.EndsWith(".Visual", StringComparison.OrdinalIgnoreCase) &&
                !i.gcType.Contains("test", StringComparison.OrdinalIgnoreCase) &&
                (IsWeaponType(i.gcType) || IsArmorType(i.gcType))
            );

            int authoredMinItemLevel = runtimeInv.minItemLevel > 0 ? runtimeInv.minItemLevel : 1;
            int authoredMaxItemLevel = runtimeInv.maxItemLevel > 0 ? runtimeInv.maxItemLevel : Math.Max(authoredMinItemLevel, playerLevel);
            if (authoredMaxItemLevel < authoredMinItemLevel)
                authoredMaxItemLevel = authoredMinItemLevel;
            safeItems = safeItems.Where(i =>
            {
                int itemLevel = RarityHelper.GetItemLevel(i.gcType);
                return itemLevel <= authoredMaxItemLevel;
            });

            string ig = runtimeInv.itemGenerator ?? invData.itemGenerator ?? "";
            if (VerboseMerchantItemLogging)
                Debug.LogError($"[MerchantManager] IG FILTER: inventory='{runtimeInv.name}' ig='{ig}'");
            System.Func<string, bool> isMythicItem = (gc) => IsEnabledMythicItem(gc);
            // Single Random reused for IG weighting filter AND placement shuffle below.
            var random = CreateMerchantRandom();

            if (ig.Equals("MerchantWeaponIG", StringComparison.OrdinalIgnoreCase))
            {
                // Authored MerchantWeaponIG.gc has Rare (Chance=1) + Unique (Chance=20).
                // Honour the weighting: tier-4 (Rare/green) always eligible; tier-5
                // (Unique/purple) is gated to ~1/20 chance per candidate so it stays a
                // rare occurrence, matching the authored Chance ratio.
                safeItems = safeItems.Where(i =>
                {
                    if (isMythicItem(i.gcType)) return false;
                    if (!IsWeaponType(i.gcType)) return false;
                    int suffix = RarityHelper.GetTierFromGcType(i.gcType);
                    if (suffix == 4) return true;
                    if (suffix == 5) return random.Next(20) == 0;
                    return false;
                });
            }
            else if (ig.Equals("MerchantArmorIG", StringComparison.OrdinalIgnoreCase))
            {
                // Authored MerchantArmorIG.gc has Rare (Chance=1) + Unique (Chance=20).
                // Same weighted gating as MerchantWeaponIG above.
                safeItems = safeItems.Where(i =>
                {
                    if (isMythicItem(i.gcType)) return false;
                    if (!IsArmorType(i.gcType)) return false;
                    int suffix = RarityHelper.GetTierFromGcType(i.gcType);
                    if (suffix == 4) return true;
                    if (suffix == 5) return random.Next(20) == 0;
                    return false;
                });
            }
            else if (ig.Equals("MerchantTrashIG", StringComparison.OrdinalIgnoreCase))
            {
                safeItems = safeItems.Where(i =>
                {
                    if (isMythicItem(i.gcType)) return false;
                    int suffix = RarityHelper.GetTierFromGcType(i.gcType);
                    return suffix >= 2 && suffix <= 3;
                });
            }
            else if (ig.Equals("MerchantSuperiorIG", StringComparison.OrdinalIgnoreCase))
            {
                safeItems = safeItems.Where(i =>
                {
                    if (isMythicItem(i.gcType)) return false;
                    int suffix = RarityHelper.GetTierFromGcType(i.gcType);
                    return suffix == 2;
                });
            }
            else if (ig.Equals("MerchantSpecialEvent01IG", StringComparison.OrdinalIgnoreCase))
            {
                // Amazonian (MerchantSpecialEvent01IG) mainly sells Rare/Unique at the tab's
                // level range, with Mythic appearances as an occasional treat.
                //
                // Mythic items come in two flavours:
                //   - SELF-CONTAINED (e.g. 1HSwordMythic1 "Wrath"): inline Mod1..Mod5 blocks
                //     with Quality=MYTHIC. Render as rainbow when sold directly.
                //   - IG-STUB (e.g. 2HAxeMythic101 "Diabolical"): no inline mods — mods come
                //     from the IG → MG → ModPAL pipeline parsed by ItemStatDatabase at boot.
                //     We allow them through if that pipeline produced wire-mod entries; WriteItem
                //     appends a single Binder mod (Quality=MYTHIC) as a Phase 2 server-sent
                //     ItemModifier child so the client renders rainbow.
                safeItems = safeItems.Where(i =>
                {
                    if (isMythicItem(i.gcType))
                    {
                        string mkey = i.gcType.ToLowerInvariant();
                        if (mkey.StartsWith("items.pal.")) mkey = mkey.Substring(10);
                        if (!_mythicModSlots.TryGetValue(mkey, out int mslots)) return false;
                        // IG-stub: only allow if ItemStatDatabase has wire mods we can inject
                        if (mslots < 3 && DungeonRunners.Data.ItemStatDatabase.Instance.GetItemWireMods(mkey).Count == 0)
                            return false;
                        return random.Next(10) == 0;        // Mythic ~10% (occasional treat across tabs)
                    }
                    int suffix = RarityHelper.GetTierFromGcType(i.gcType);
                    if (suffix == 4) return true;            // Rare always (Chance=4 in authored)
                    if (suffix == 5) return random.Next(4) == 0;  // Unique ~25% (Chance=1, less common than Rare)
                    return false;
                });
            }

            // Reject any mythic item not in the lookup table — unknown byte count = crash
            // Also reject mythic items with modSlots <= 1 (zero GC mods = item pack/seasonal exclusives, not real equipment)
            safeItems = safeItems.Where(i =>
            {
                if (!IsEnabledMythicItem(i.gcType)) return true;
                if (i.gcType.IndexOf("MythicPAL", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                string key = i.gcType.ToLowerInvariant();
                if (key.StartsWith("items.pal.")) key = key.Substring(10);
                if (!_mythicModSlots.TryGetValue(key, out int slots)) return false;
                if (slots <= 0) return false;
                return true;
            });

            var safeList = safeItems.ToList();
            if (VerboseMerchantItemLogging)
                Debug.LogError($"[MERCHANT-DIAG] candidates inv='{runtimeInv.name}' ig={ig} count={safeList.Count} maxLvl={authoredMaxItemLevel} playerLvl={playerLevel}");
            if (VerboseMerchantItemLogging)
                Debug.LogError($"[MerchantManager] 📦 {runtimeInv.name} (ig={ig}): {safeList.Count} candidates after filtering");
            // LOG EVERY MYTHIC ITEM that passed filtering for this tab
            foreach (var dbgItem in safeList)
            {
                if (IsEnabledMythicItem(dbgItem.gcType))
                {
                    string dbgKey = dbgItem.gcType.ToLowerInvariant();
                    if (dbgKey.StartsWith("items.pal.")) dbgKey = dbgKey.Substring(10);
                    int dbgSlots = _mythicModSlots.TryGetValue(dbgKey, out int s) ? s : -1;
                    bool isW = IsWeaponType(dbgItem.gcType);
                    bool isA = IsArmorType(dbgItem.gcType);
                    if (VerboseMerchantItemLogging)
                        Debug.LogError($"[MYTHIC-IN-TAB] tab='{runtimeInv.name}' ig='{ig}' item={dbgItem.gcType} isWeapon={isW} isArmor={isA} modSlots={dbgSlots}");
                }
            }
            if (safeList.Count == 0) return;
            var available = safeList.OrderBy(x => random.Next()).ToList();
            bool[,] grid = new bool[invData.width, invData.height];
            foreach (var itemData in available)
            {
                var (w, h) = GetItemDimensions(itemData.gcType);
                int placeX = -1, placeY = -1;
                // FIXED: X-outer, Y-inner to match client's Inventory::findSlot algorithm
                for (int x = 0; x <= invData.width - w && placeX < 0; x++)
                {
                    for (int y = 0; y <= invData.height - h && placeX < 0; y++)
                    {
                        bool canPlace = true;
                        for (int dx = 0; dx < w && canPlace; dx++)
                            for (int dy = 0; dy < h && canPlace; dy++)
                                if (grid[x + dx, y + dy]) canPlace = false;
                        if (canPlace) { placeX = x; placeY = y; }
                    }
                }
                if (placeX >= 0)
                {
                    for (int dx = 0; dx < w; dx++)
                        for (int dy = 0; dy < h; dy++)
                            grid[placeX + dx, placeY + dy] = true;

                    // Rarity from tier suffix
                    int tier = RarityHelper.GetTierFromGcType(itemData.gcType);
                    var itemRarity = RarityHelper.GetRarityFromTier(tier);

                    // FIX: Mythic items have NO -N suffix → GetTierFromGcType returns 1 → Normal.
                    // Must cover BOTH IsMythicPALItem and IsEnabledMythicItem — some mythic variants
                    // pass IsEnabledMythicItem but not IsMythicPALItem and would get rarity=Normal,
                    // bypassing the membership restriction and using the wrong price multiplier.
                    if (RarityHelper.IsMythicPALItem(itemData.gcType) || IsEnabledMythicItem(itemData.gcType))
                    {
                        Debug.LogError($"[MYTHIC-PRICE-FIX] {itemData.gcType}: rarity {itemRarity} → Mythic");
                        itemRarity = ItemRarity.Mythic;
                    }

                    // USE SAME LEVEL AS WriteItem SENDS TO CLIENT
                    // Mythics roll within the tab's authored level range like every other item,
                    // then clamp to the configured mythic min/max safety bounds. (Previously this
                    // used playerLevel directly which caused all tabs of a multi-tab mythic vendor
                    // — e.g. Amazonian's Low/Medium/High/Max — to spawn mythics at the same level
                    // regardless of which tab they belonged to.)
                    bool isMythicGen = IsEnabledMythicItem(itemData.gcType);
                    int level;
                    if (isMythicGen)
                    {
                        int rolledLevel = RollMerchantStockLevel(runtimeInv, random);
                        int mythicMin = DungeonRunners.Core.ServerSettings.Get("mythicMinLevel", 15);
                        int mythicMax = DungeonRunners.Core.ServerSettings.Get("mythicMaxLevel", 100);
                        level = Math.Max(mythicMin, Math.Min(mythicMax, rolledLevel));
                    }
                    else
                    {
                        level = RollMerchantStockLevel(runtimeInv, random);
                    }

                    // Get GoldValue from DB (gc_gold_value), fallback to name-based
                    float baseGoldValue = itemData.gcGoldValue > 0 ? itemData.gcGoldValue : RarityHelper.GetBaseGoldValue(itemData.gcType);
                    uint finalPrice = RarityHelper.CalculateBuyPrice(level, itemRarity, baseGoldValue);

                    runtimeInv.items.Add(new MerchantItemRuntimeData
                    {
                        gcType = itemData.gcType,
                        id = merchant.nextItemId++,
                        inventoryX = placeX,
                        inventoryY = placeY,
                        quantity = 1,
                        level = level,
                        rarity = itemRarity,
                        price = finalPrice,
                        goldValue = baseGoldValue,
                        scaleMod = RarityHelper.GetRandomScaleMod(itemRarity)
                    });
                    if (isMythicGen)
                    {
                        string plKey = itemData.gcType.ToLowerInvariant();
                        if (plKey.StartsWith("items.pal.")) plKey = plKey.Substring(10);
                        int plSlots = _mythicModSlots.TryGetValue(plKey, out int ps) ? ps : -1;
                        if (VerboseMerchantItemLogging)
                            Debug.LogError($"[MYTHIC-PLACED] tab='{runtimeInv.name}' item={itemData.gcType} modSlots={plSlots} pos=({placeX},{placeY})");
                    }
                }
            }
            Debug.LogError($"[MerchantManager] ✅ Generated {runtimeInv.items.Count} items for '{runtimeInv.name}'");
        }

        private static System.Random CreateMerchantRandom()
        {
            lock (_merchantRandomLock)
                return new System.Random(_merchantRandom.Next());
        }

        #endregion




        /* private static ItemRarity GetTestRarity(string gcType)
         {
             if (gcType.EndsWith("-1")) return ItemRarity.Normal;      // White
             if (gcType.EndsWith("-2")) return ItemRarity.Superior;    // Green
             if (gcType.EndsWith("-3")) return ItemRarity.Magical;     // Blue
             if (gcType.EndsWith("-4")) return ItemRarity.Rare;        // Yellow
             if (gcType.EndsWith("-5")) return ItemRarity.Unique;      // Orange
             if (gcType.EndsWith("-6")) return ItemRarity.Mythic;      // Purple
             return ItemRarity.Rare;
         }*/
        #region Public API

        public static bool IsMerchant(string npcGcType)
        {
            if (!_initialized) Initialize();
            return _merchants.ContainsKey(npcGcType);
        }

        public static MerchantData GetMerchant(string npcGcType)
        {
            if (!_initialized) Initialize();
            _merchants.TryGetValue(npcGcType, out var merchant);
            return merchant;
        }

        public static MerchantRuntimeData GetRuntimeMerchant(string npcGcType)
        {
            if (!_initialized) Initialize();
            _runtimeMerchants.TryGetValue(npcGcType, out var merchant);
            return merchant;
        }

        #endregion

        #region Packet Writing

        public static void WriteMerchantComponent(LEWriter writer, string npcGcType, ushort npcId, ushort merchantId)
        {
            if (!_initialized) Initialize();

            // *** CHECK AND REGENERATE EXPIRED INVENTORIES ***
            // CheckAndRegenerateInventories(npcGcType);

            var runtimeMerchant = GetRuntimeMerchant(npcGcType);
            if (runtimeMerchant == null)
            {
                Debug.LogError($"[MerchantManager] ❌ No runtime merchant found for {npcGcType}");
                return;
            }

            if (VerboseMerchantItemLogging)
            {
                Debug.LogError($"[MerchantManager] ═══════════════════════════════════════════════════════════");
                Debug.LogError($"[MerchantManager] Writing Merchant component for {npcGcType}");
            }

            // OP: Create Component (0x32)
            int merchantCompStart = writer.Position;
            writer.WriteByte(0x32);
            writer.WriteUInt16(npcId);
            writer.WriteUInt16(merchantId);

            // GCType: 0xFF + "merchant" string
            writer.WriteByte(0xFF);
            writer.WriteCString("merchant");

            // hasInit = true
            writer.WriteByte(0x01);

            WriteMerchantInitPayload(writer, npcGcType, runtimeMerchant);

            if (VerboseMerchantItemLogging)
                Debug.LogError($"[MerchantManager] ✅ Merchant component written: {writer.Position - merchantCompStart} bytes total (pos {merchantCompStart}→{writer.Position})");
        }

        private static void WriteMerchantInitPayload(LEWriter writer, string npcGcType, MerchantRuntimeData runtimeMerchant, bool includeDynamicItems = true)
        {
            writer.WriteUInt32(0x000000FF);
            writer.WriteUInt32(0x00000000);

            int invCount = runtimeMerchant.inventories.Count;
            writer.WriteByte((byte)invCount);
            if (VerboseMerchantItemLogging)
                Debug.LogError($"[MerchantManager] Writing {invCount} inventories");

            foreach (var inventory in runtimeMerchant.inventories.OrderBy(inv => inv.id))
            {
                int invStartPos = writer.Position;
                WriteInventory(writer, inventory, npcGcType, includeDynamicItems);
                int invEndPos = writer.Position;
                if (VerboseMerchantItemLogging)
                    Debug.LogError($"[MerchantManager] WROTE '{inventory.name}' id={inventory.id} gcType='{inventory.gcType}' bytes={invEndPos - invStartPos} (pos {invStartPos}→{invEndPos})");
            }

            int resetTimeTicks = 0;
            foreach (var inv in runtimeMerchant.inventories)
            {
                if (!inv.staticContents)
                {
                    resetTimeTicks = GetTicksUntilRegeneration(npcGcType, inv.id);
                    if (VerboseMerchantItemLogging)
                        Debug.LogError($"[MerchantManager] SENDING timer to client: {resetTimeTicks / REFRESH_TIMER_TICKS_PER_SECOND:F2}s remaining");
                    break;
                }
            }

            if (resetTimeTicks > 0xFFFF) resetTimeTicks = 0xFFFF;

            writer.WriteByte(0x01);
            writer.WriteUInt16((ushort)resetTimeTicks);
            writer.WriteUInt16(0x000F);

            if (VerboseMerchantItemLogging)
                Debug.LogError($"[MerchantManager] ⏱️ Reset time: {resetTimeTicks / REFRESH_TIMER_TICKS_PER_SECOND:F2}s = {resetTimeTicks} ticks ({resetTimeTicks / REFRESH_TIMER_TICKS_PER_SECOND / 60.0:F1} min)");
        }

        private static void WriteInventory(LEWriter writer, MerchantInventoryRuntimeData inventory, string npcGcType, bool includeDynamicItems = true)
        {
            int invStart = writer.Position;
            writer.WriteByte(0xFF);
            writer.WriteCString(inventory.gcType.ToLowerInvariant());

            writer.WriteByte((byte)inventory.id);
            if (VerboseMerchantItemLogging)
                Debug.LogError($"[WriteInventory] START '{inventory.name}' gcType='{inventory.gcType}' id={inventory.id} static={inventory.staticContents} serverSends={inventory.serverSendsItems} items={inventory.items.Count} pos={invStart}");

            if (inventory.serverSendsItems)
            {
                // Server-sent static inventory (quest items).
                // Format from disassembly of ReadChildData<Item> (0x57F9E0):
                //   Phase 1: For each GC archetype child, call Item::readData (no GC type prefix!)
                //   Phase 2: Read count byte, create new items from GC type
                // We provide readData bytes for Phase 1 archetypes, then count=0 for Phase 2.
                // This gives each item a unique server-assigned ID (500+), avoiding the
                // ID 255 collision with the Consumables tab.
                writer.WriteByte(0x01);  // hasItems = true

                // Phase 1: readData bytes for each archetype item
                // Item::readData format (0x581710):
                //   uint32: ID        → item+0x68
                //   byte:   InvX      → item+0x80
                //   byte:   InvY      → item+0x81
                //   byte:   Quantity   → item+0x82
                //   byte:   Level      → item+0x7F
                //   byte:   flags      → item+0x83 (0=none, bit2=has uint16 trinket)
                //   ReadChildData<ItemModifier>: byte count (0 for quest items)
                foreach (var item in inventory.items)
                {
                    writer.WriteUInt32((uint)item.id);    // Unique ID (500+)
                    writer.WriteByte((byte)item.inventoryX);
                    writer.WriteByte((byte)item.inventoryY);
                    writer.WriteByte((byte)item.quantity);
                    writer.WriteByte(0x01);               // Level
                    writer.WriteByte(0x00);               // flags (no soulbound, no trinket)
                    writer.WriteByte(0x00);               // ReadChildData<ItemModifier> count = 0
                    if (VerboseMerchantItemLogging)
                        Debug.LogError($"[MerchantManager] SERVER-SENT quest item: {item.gcType} ID={item.id} at ({item.inventoryX},{item.inventoryY})");
                }

                // Phase 2: 0 additional server-created items
                writer.WriteByte(0x00);

                if (VerboseMerchantItemLogging)
                    Debug.LogError($"[MerchantManager] Inventory '{inventory.name}' (ID={inventory.id}) - SERVER-SENT ({inventory.items.Count} quest items with unique IDs)");
            }
            else if (inventory.staticContents || inventory.items.Count == 0 || (!includeDynamicItems && inventory.autoGenerateItems))
            {
                writer.WriteByte(0x00);  // hasItems = false — client loads from GC files
                if (VerboseMerchantItemLogging)
                    Debug.LogError($"[MerchantManager] Inventory '{inventory.name}' (ID={inventory.id}) - CLIENT-LOADED ({inventory.items.Count} items tracked server-side)");
            }
            else
            {
                writer.WriteByte(0x01);  // hasItems = true
                int itemCount = Math.Min(inventory.items.Count, 255);
                writer.WriteByte((byte)itemCount);
                if (VerboseMerchantItemLogging)
                    Debug.LogError($"[MerchantManager] WriteInventory '{inventory.name}': writing {itemCount} items");

                // ═══ PRE-WRITE VALIDATION: dump every item + modSlots so we can find the crasher ═══
                if (VerboseMerchantItemLogging)
                {
                    for (int v = 0; v < itemCount; v++)
                    {
                        var vi = inventory.items[v];
                        string vk = vi.gcType.ToLowerInvariant();
                        if (vk.StartsWith("items.pal.")) vk = vk.Substring(10);
                        int vSlots;
                        if (_mythicModSlots.TryGetValue(vk, out int vs)) vSlots = vs;
                        else
                        {
                            var vd = DatabaseLoader.FindItem(vk);
                            if (vd != null) vSlots = vd.modCount;
                            else vSlots = IsWeaponType(vk) ? 1 : 2;
                        }
                        // Apply same chain override as WriteItem
                        if (vk.StartsWith("chain") && !vk.Contains("shield") && !vk.Contains("mythic"))
                            vSlots = 1;
                        Debug.LogError($"[PRE-WRITE] #{v}: {vi.gcType} modSlots={vSlots} id={vi.id}");
                    }
                    Debug.LogError($"[PRE-WRITE] ═══ END DUMP ═══");
                }

                for (int idx = 0; idx < itemCount; idx++)
                {
                    WriteItem(writer, inventory.items[idx]);
                }

                int remainingSeconds = GetSecondsUntilRegeneration(npcGcType, inventory.id);
                if (VerboseMerchantItemLogging)
                    Debug.LogError($"[MerchantManager] Inventory '{inventory.name}': {itemCount} items, resets in {remainingSeconds}s");
            }
        }
        public static void ProcessRefreshes(
            Dictionary<uint, List<UnityGameServer.ZoneNPC>> zoneNPCs,
            Dictionary<int, RRConnection> connections,
            Action<RRConnection, byte, byte, byte[]> sendPacket,
            ref uint nextEntityId)
        {
            FlushPendingMerchantRefreshAdds(connections, sendPacket);
            var activeZoneIds = new HashSet<uint>(connections.Values.Where(conn => conn.IsConnected).Select(conn => conn.CurrentZoneId));

            foreach (var kvp in zoneNPCs)
            {
                uint zoneId = kvp.Key;
                if (!activeZoneIds.Contains(zoneId))
                    continue;

                foreach (var npc in kvp.Value)
                {
                    if (!npc.IsMerchant) continue;

                    if (!_runtimeMerchants.TryGetValue(npc.GCClass, out var runtimeMerchant))
                        continue;
                    if (!_merchants.TryGetValue(npc.GCClass, out var merchantData))
                        continue;

                    if (!TryRegenerateInventories(npc.GCClass, runtimeMerchant, merchantData, DateTime.UtcNow, out int refreshedViews, out int refreshedItems, out var removedItemIds))
                        continue;

                    Debug.LogError($"[MerchantManager] 🔄 Regenerated server merchant authority for {npc.GCClass}: views {refreshedViews}, items {refreshedItems}");
                    QueueMerchantRefreshAdd(zoneId, npc.GCClass, (ushort)npc.MerchantId, DateTime.UtcNow, removedItemIds);
                }
            }
        }

        private static void QueueMerchantRefreshAdd(uint zoneId, string npcGcClass, ushort componentId, DateTime now, List<uint> removedItemIds)
        {
            _pendingMerchantRefreshAdds.RemoveAll(p => p.zoneId == zoneId && p.componentId == componentId);
            _pendingMerchantRefreshAdds.Add(new PendingMerchantRefreshAdd
            {
                zoneId = zoneId,
                npcGcClass = npcGcClass,
                componentId = componentId,
                removedItemIds = removedItemIds != null ? removedItemIds.Distinct().ToList() : new List<uint>(),
                sendAfterUtc = now.AddSeconds(REFRESH_ADD_DELAY_SECONDS)
            });
            Debug.LogError($"[MerchantManager] ⏳ Queued merchant refresh for {npcGcClass} in {REFRESH_ADD_DELAY_SECONDS:0.###}s");
        }

        private static void FlushPendingMerchantRefreshAdds(
            Dictionary<int, RRConnection> connections,
            Action<RRConnection, byte, byte, byte[]> sendPacket)
        {
            var now = DateTime.UtcNow;
            for (int i = _pendingMerchantRefreshAdds.Count - 1; i >= 0; i--)
            {
                var pending = _pendingMerchantRefreshAdds[i];
                if (pending.sendAfterUtc > now)
                    continue;

                _pendingMerchantRefreshAdds.RemoveAt(i);

                if (!_runtimeMerchants.TryGetValue(pending.npcGcClass, out var runtimeMerchant))
                    continue;

                byte[] data = BuildMerchantInventoryRefreshPacket(pending.componentId, runtimeMerchant, pending.removedItemIds);
                if (data.Length <= 2)
                    continue;

                int sent = 0;
                foreach (var conn in connections.Values)
                {
                    if (conn.CurrentZoneId != pending.zoneId || !conn.IsConnected || !conn.IsSpawned || !conn.AllowFlush)
                        continue;

                    bool hasActiveRefresh = conn.HasActiveMerchantRefresh
                        && string.Equals(conn.ActiveMerchantNpcGcClass, pending.npcGcClass, StringComparison.OrdinalIgnoreCase);
                    sendPacket(conn, 0x01, 0x0F, data);
                    if (hasActiveRefresh)
                        ScheduleClientMerchantRefresh(conn, pending.npcGcClass, pending.componentId, now, true);
                    sent++;
                }

                Debug.LogError($"[MerchantManager] 📤 Sent merchant refresh for {pending.npcGcClass}: {data.Length} bytes to {sent} clients");
                if (VerboseMerchantItemLogging)
                {
                    int totalInv = runtimeMerchant.inventories.Count(inv => !inv.staticContents);
                    int totalItems = runtimeMerchant.inventories.Where(inv => !inv.staticContents).Sum(inv => inv.items.Count);
                    Debug.LogError($"[MERCHANT-DIAG] flushA npc={pending.npcGcClass} compId=0x{pending.componentId:X4} dataLen={data.Length} removed={pending.removedItemIds.Count} dynInv={totalInv} dynItems={totalItems} sentTo={sent}");
                }
            }
        }

        public static void ScheduleClientMerchantRefresh(RRConnection conn, string npcGcType, ushort componentId, DateTime now, bool forceReschedule = false)
        {
            if (conn == null || string.IsNullOrWhiteSpace(npcGcType) || componentId == 0)
                return;

            bool sameActiveRefresh = conn.HasActiveMerchantRefresh
                && conn.ActiveMerchantComponentId == componentId
                && string.Equals(conn.ActiveMerchantNpcGcClass, npcGcType, StringComparison.OrdinalIgnoreCase);

            if (!forceReschedule && sameActiveRefresh && (conn.ActiveMerchantRefreshReady || conn.ActiveMerchantRefreshDueUtc <= now))
            {
                conn.ActiveMerchantRefreshReady = true;
                Debug.LogError($"[MerchantManager] Preserving ready client merchant refresh for {npcGcType}; waiting for client clear boundary");
                return;
            }

            double delaySeconds = GetGeneratedRefreshDelaySeconds(npcGcType);
            DateTime dueUtc = now.AddSeconds(delaySeconds);
            if (!forceReschedule && sameActiveRefresh && conn.ActiveMerchantRefreshDueUtc > DateTime.MinValue && conn.ActiveMerchantRefreshDueUtc < dueUtc)
            {
                dueUtc = conn.ActiveMerchantRefreshDueUtc;
                delaySeconds = Math.Max(0.0, (dueUtc - now).TotalSeconds);
            }

            conn.ActiveMerchantNpcGcClass = npcGcType;
            conn.ActiveMerchantComponentId = componentId;
            conn.ActiveMerchantRefreshDueUtc = dueUtc;
            conn.ActiveMerchantRefreshReady = false;
            conn.HasActiveMerchantRefresh = true;

            Debug.LogError($"[MerchantManager] Scheduled client merchant refresh for {npcGcType} in {delaySeconds:0.###}s");
        }

        private static void FlushClientMerchantRefreshes(
            Dictionary<int, RRConnection> connections,
            Action<RRConnection, byte, byte, byte[]> sendPacket)
        {
            var now = DateTime.UtcNow;

            foreach (var conn in connections.Values)
            {
                if (!conn.IsConnected || !conn.IsSpawned || !conn.AllowFlush || !conn.HasActiveMerchantRefresh || conn.ActiveMerchantRefreshDueUtc > now)
                    continue;

                if (string.IsNullOrWhiteSpace(conn.ActiveMerchantNpcGcClass) || conn.ActiveMerchantComponentId == 0)
                {
                    conn.HasActiveMerchantRefresh = false;
                    continue;
                }

                FlushClientMerchantRefresh(conn, sendPacket);
            }
        }

        public static bool FlushClientMerchantRefreshOnClientBoundary(
            RRConnection conn,
            Action<RRConnection, byte, byte, byte[]> sendPacket)
        {
            if (conn == null || !conn.IsConnected || !conn.HasActiveMerchantRefresh)
                return false;

            // The client emits subMessage 0x22 with empty payload when its inventory-reset
            // timer expires — that's the cue to push fresh items back. Previously we only
            // marked the connection "ready" but no caller acted on it, so the shop appeared
            // empty until the player reopened it. Perform the regen + refresh inline here.
            var nowBoundary = DateTime.UtcNow;
            if (VerboseMerchantItemLogging)
            {
                double overdueSec = (nowBoundary - conn.ActiveMerchantRefreshDueUtc).TotalSeconds;
                Debug.LogError($"[MERCHANT-DIAG] boundary conn={conn.ConnId} npc={conn.ActiveMerchantNpcGcClass} dueUtcOverdue={overdueSec:F2}s ready={conn.ActiveMerchantRefreshReady}");
            }
            if (conn.ActiveMerchantRefreshDueUtc <= nowBoundary)
            {
                conn.ActiveMerchantRefreshReady = true;
                return FlushClientMerchantRefresh(conn, sendPacket);
            }

            return false;
        }

        private static bool FlushClientMerchantRefresh(
            RRConnection conn,
            Action<RRConnection, byte, byte, byte[]> sendPacket)
        {
            if (conn == null || !conn.IsConnected || !conn.HasActiveMerchantRefresh)
                return false;

            string npcGcType = conn.ActiveMerchantNpcGcClass;
            ushort componentId = conn.ActiveMerchantComponentId;
            if (string.IsNullOrWhiteSpace(npcGcType) || componentId == 0)
            {
                conn.HasActiveMerchantRefresh = false;
                conn.ActiveMerchantRefreshReady = false;
                return false;
            }

            var now = DateTime.UtcNow;
            if (!_runtimeMerchants.TryGetValue(npcGcType, out var runtimeMerchantBefore))
            {
                conn.HasActiveMerchantRefresh = false;
                conn.ActiveMerchantRefreshReady = false;
                return false;
            }

            var removedItemIds = CaptureDynamicInventoryItemIds(runtimeMerchantBefore);
            int preItemsB = runtimeMerchantBefore.inventories.Where(inv => !inv.staticContents).Sum(inv => inv.items.Count);
            ForceRegenerate(npcGcType, conn.PlayerLevel > 0 ? conn.PlayerLevel : 1);

            if (!_runtimeMerchants.TryGetValue(npcGcType, out var runtimeMerchant))
            {
                conn.HasActiveMerchantRefresh = false;
                conn.ActiveMerchantRefreshReady = false;
                if (VerboseMerchantItemLogging)
                    Debug.LogError($"[MERCHANT-DIAG] flushB-abort npc={npcGcType} reason=runtimeMissingPostRegen");
                return false;
            }

            int postItemsB = runtimeMerchant.inventories.Where(inv => !inv.staticContents).Sum(inv => inv.items.Count);
            byte[] data = BuildMerchantInventoryRefreshPacket(componentId, runtimeMerchant, removedItemIds);
            if (data.Length > 2)
            {
                sendPacket(conn, 0x01, 0x0F, data);
                Debug.LogError($"[MerchantManager] Sent client merchant refresh for {npcGcType}: {data.Length} bytes to {conn.LoginName ?? conn.ConnId.ToString()}");
            }
            if (VerboseMerchantItemLogging)
                Debug.LogError($"[MERCHANT-DIAG] flushB conn={conn.ConnId} npc={npcGcType} dataLen={data.Length} removed={removedItemIds.Count} itemsBefore={preItemsB} itemsAfter={postItemsB} playerLvl={conn.PlayerLevel}");

            ScheduleClientMerchantRefresh(conn, npcGcType, componentId, now, true);
            return true;
        }

        private static bool IsClientMerchantRefreshActive(RRConnection conn, string npcGcType)
        {
            if (conn == null || string.IsNullOrWhiteSpace(npcGcType))
                return false;

            if (!string.IsNullOrWhiteSpace(conn.PendingMerchantNpcGcClass)
                && string.Equals(conn.PendingMerchantNpcGcClass, npcGcType, StringComparison.OrdinalIgnoreCase))
                return true;

            return conn.HasActiveMerchantRefresh
                && string.Equals(conn.ActiveMerchantNpcGcClass, npcGcType, StringComparison.OrdinalIgnoreCase);
        }

        private static double GetGeneratedRefreshIntervalSeconds(string npcGcType)
        {
            if (!_runtimeMerchants.TryGetValue(npcGcType, out var runtimeMerchant))
                return DEFAULT_REFRESH_INTERVAL_SECONDS;

            foreach (var runtimeInv in runtimeMerchant.inventories)
            {
                if (!UsesGeneratedRefresh(runtimeInv))
                    continue;

                return runtimeInv.regenerateIntervalSeconds > 0
                    ? runtimeInv.regenerateIntervalSeconds
                    : DEFAULT_REFRESH_INTERVAL_SECONDS;
            }

            return DEFAULT_REFRESH_INTERVAL_SECONDS;
        }

        private static double GetGeneratedRefreshDelaySeconds(string npcGcType)
        {
            if (!_runtimeMerchants.TryGetValue(npcGcType, out var runtimeMerchant))
                return DEFAULT_REFRESH_INTERVAL_SECONDS + REFRESH_ADD_DELAY_SECONDS;

            foreach (var runtimeInv in runtimeMerchant.inventories)
            {
                if (!UsesGeneratedRefresh(runtimeInv))
                    continue;

                int ticks = GetTicksUntilRegeneration(npcGcType, runtimeInv.id);
                return (ticks / REFRESH_TIMER_TICKS_PER_SECOND) + REFRESH_ADD_DELAY_SECONDS;
            }

            return DEFAULT_REFRESH_INTERVAL_SECONDS + REFRESH_ADD_DELAY_SECONDS;
        }

        private static bool TryRegenerateInventories(string npcGcClass, MerchantRuntimeData runtimeMerchant, MerchantData merchantData, DateTime now, out int refreshedViews, out int refreshedItems, out List<uint> removedItemIds)
        {
            refreshedViews = 0;
            refreshedItems = 0;
            removedItemIds = new List<uint>();

            foreach (var runtimeInv in runtimeMerchant.inventories)
            {
                if (runtimeInv.staticContents || !runtimeInv.autoGenerateItems || string.IsNullOrWhiteSpace(runtimeInv.itemGenerator))
                    continue;

                string regenKey = $"{npcGcClass}_{runtimeInv.id}";
                if (!_lastRegeneration.TryGetValue(regenKey, out var lastRegen))
                    lastRegen = DateTime.MinValue;

                float intervalSeconds = runtimeInv.regenerateIntervalSeconds > 0 ? runtimeInv.regenerateIntervalSeconds : DEFAULT_REFRESH_INTERVAL_SECONDS;
                var elapsed = (now - lastRegen).TotalSeconds;
                if (elapsed < intervalSeconds)
                    continue;

                var invData = merchantData.inventories.FirstOrDefault(inv => inv.id == runtimeInv.id);
                if (invData == null)
                    continue;

                int preItemCount = runtimeInv.items.Count;
                Debug.LogError($"[MerchantManager] 🔄 REFRESHING inventory '{runtimeInv.name}' for {npcGcClass}");
                foreach (var item in runtimeInv.items)
                {
                    if (item.id >= 0)
                        removedItemIds.Add((uint)item.id);
                }
                GenerateInventoryItems(runtimeMerchant, runtimeInv, invData, runtimeInv.generatedForLevel > 0 ? runtimeInv.generatedForLevel : 100);
                _lastRegeneration[regenKey] = now;
                if (VerboseMerchantItemLogging)
                    Debug.LogError($"[MERCHANT-DIAG] regenA npc={npcGcClass} inv={runtimeInv.id} '{runtimeInv.name}' ig={runtimeInv.itemGenerator} lvl={runtimeInv.generatedForLevel} elapsed={elapsed:F1}s itemsBefore={preItemCount} itemsAfter={runtimeInv.items.Count}");
                refreshedViews++;
                refreshedItems += runtimeInv.items.Count;
            }

            return refreshedViews > 0;
        }

        private static List<uint> CaptureDynamicInventoryItemIds(MerchantRuntimeData runtimeMerchant)
        {
            var itemIds = new List<uint>();
            if (runtimeMerchant == null)
                return itemIds;

            foreach (var runtimeInv in runtimeMerchant.inventories)
            {
                if (!UsesGeneratedRefresh(runtimeInv))
                    continue;

                foreach (var item in runtimeInv.items)
                {
                    if (item.id >= 0)
                        itemIds.Add((uint)item.id);
                }
            }

            return itemIds.Distinct().ToList();
        }

        private static byte[] BuildMerchantInventoryAddPacket(ushort componentId, MerchantRuntimeData runtimeMerchant)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x07);
            WriteDynamicInventoryAddUpdates(writer, componentId, runtimeMerchant);
            writer.WriteByte(0x06);
            return writer.ToArray();
        }

        private static byte[] BuildMerchantInventoryRefreshPacket(ushort componentId, MerchantRuntimeData runtimeMerchant, IEnumerable<uint> removedItemIds)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x07);
            WriteDynamicInventoryRemoveUpdates(writer, componentId, removedItemIds);
            WriteDynamicInventoryAddUpdates(writer, componentId, runtimeMerchant);
            writer.WriteByte(0x06);
            return writer.ToArray();
        }

        private static void WriteDynamicInventoryRemoveUpdates(LEWriter writer, ushort componentId, IEnumerable<uint> itemIds)
        {
            if (itemIds == null)
                return;

            foreach (var itemId in itemIds.Distinct())
            {
                writer.WriteByte(0x35);
                writer.WriteUInt16(componentId);
                writer.WriteByte(0x1F);
                writer.WriteUInt32(itemId);
                writer.WriteByte(0x02);
                writer.WriteUInt32(0x00000000);
            }
        }

        private static void WriteDynamicInventoryAddUpdates(LEWriter writer, ushort componentId, MerchantRuntimeData runtimeMerchant)
        {
            foreach (var runtimeInv in runtimeMerchant.inventories.OrderBy(inv => inv.id))
            {
                if (runtimeInv.staticContents || !runtimeInv.autoGenerateItems || runtimeInv.items.Count == 0)
                    continue;

                foreach (var item in runtimeInv.items)
                {
                    writer.WriteByte(0x35);
                    writer.WriteUInt16(componentId);
                    writer.WriteByte(0x1E);
                    writer.WriteByte((byte)runtimeInv.id);
                    WriteItem(writer, item);
                    writer.WriteByte(0x02);
                    writer.WriteUInt32(0x00000000);
                }
            }
        }

        public static void HandleBuyItem(RRConnection conn, ushort componentId, ushort itemId,
        Dictionary<uint, List<UnityGameServer.ZoneNPC>> zoneNPCs,
        Dictionary<string, DungeonRunners.Data.GCObject> selectedCharacters,
        Action<RRConnection, byte, byte, byte[]> sendPacket,
        UnityGameServer server, byte buyByte1 = 0, byte buyByte2 = 0, bool isFree = false)
        {
            ushort targetId = itemId;
            Debug.LogError($"[MerchantManager] ═══════════════════════════════════════════");
            Debug.LogError($"[MerchantManager] CLIENT CLICKED: itemId={targetId} (0x{targetId:X4}), buyByte1=0x{buyByte1:X2}, buyByte2=0x{buyByte2:X2}");
            Debug.LogError($"[MerchantManager] Processing BUY: looking for item ID={targetId}");

            foreach (var kvp in zoneNPCs)
            {
                foreach (var npc in kvp.Value)
                {
                    if (!npc.IsMerchant || npc.MerchantId != componentId) continue;
                    if (!_runtimeMerchants.TryGetValue(npc.GCClass, out var runtimeMerchant))
                        continue;

                    foreach (var inv in runtimeMerchant.inventories)
                    {
                        MerchantItemRuntimeData item = null;
                        if (inv.staticContents && !inv.serverSendsItems)
                        {
                            // Client-loaded static: client sends 255-based index from GC data
                            int index = targetId - 255;
                            if (index >= 0 && index < inv.items.Count)
                                item = inv.items[index];
                        }
                        else
                        {
                            // Dynamic OR server-sent (hasItems=true): exact server-assigned ID
                            item = inv.items.FirstOrDefault(i => i.id == targetId);
                        }
                        if (item == null) continue;

                        var inv_final = inv;
                        var item_final = item;

                        Debug.LogError($"[MerchantManager] ✓ FOUND: {item_final.gcType} in '{inv_final.name}' (id={inv_final.id})");

                        if (!selectedCharacters.TryGetValue(conn.LoginName, out var gcObj))
                        {
                            Debug.LogError($"[MerchantManager] ❌ No character for {conn.LoginName}");
                            return;
                        }

                        var savedChar = DungeonRunners.Database.CharacterRepository.GetCharacter(gcObj.Id);
                        if (savedChar == null)
                        {
                            Debug.LogError($"[MerchantManager] ❌ No saved character");
                            return;
                        }

                        // Calculate price for consumables/quest items at buy time.
                        // Potions have ScaleToObserverLevel = true → price scales with player level.
                        // Town Portal does NOT → fixed price at level 1.
                        // Quest items → GoldValue 0 → min 1 gold (client shows 1).
                        // Equipment uses the pre-calculated price from init, recalculated here with membership prefix.
                        string memberPrefix = isFree ? "free_" : "member_";
                        uint price = item_final.price;
                        string gcLowerBuy = item_final.gcType.ToLowerInvariant();
                        bool isConsumableBuy = gcLowerBuy.Contains("consumable") || gcLowerBuy.Contains("potion")
                                            || gcLowerBuy.Contains("townportal");

                        // ── MEMBERSHIP RESTRICTION ──
                        // Rules come directly from GC ForceRequiresMembership flags:
                        //   MajorHealthPotion → ForceRequiresMembership = true
                        //   MajorManaPotion   → ForceRequiresMembership = true
                        //   MinorHealthPotion → no restriction (free players can buy)
                        //   MinorManaPotion   → no restriction (free players can buy)
                        // Equipment: Rare/Unique/Mythic are member-only per original game design.
                        if (isFree)
                        {
                            bool isMajorPotion = gcLowerBuy.Contains("majorhealthpotion") || gcLowerBuy.Contains("majormanapotion");
                            bool isMemberEquip = !isConsumableBuy && (
                                item_final.rarity == ItemRarity.Rare ||
                                item_final.rarity == ItemRarity.Unique ||
                                item_final.rarity == ItemRarity.Mythic);

                            if (isMajorPotion || isMemberEquip)
                            {
                                Debug.LogError($"[MerchantManager] ❌ MEMBERSHIP REQUIRED: {conn.LoginName} (free) tried to buy {item_final.gcType} (rarity={item_final.rarity})");
                                return;
                            }
                        }
                        if (isConsumableBuy)
                        {
                            // Use runtime PlayerState level — this is what the client sees.
                            // savedChar.level in the DB may be stale if the player leveled up
                            // during the session and it hasn't been saved yet.
                            var ps = server.GetPlayerState(conn.ConnId.ToString());
                            int playerLevel = ps != null && ps.Level > 0 ? ps.Level : (int)savedChar.level;
                            if (playerLevel < 1) playerLevel = 1;

                            float gcGold = 0.175f;
                            bool scaleToObserverLevel = true;

                            // Major potions have GoldValue=0.2 in GC, minor have 0.175
                            // Must check major BEFORE generic to avoid substring match
                            if (gcLowerBuy.Contains("majorhealthpotion"))
                                gcGold = 0.2f;
                            else if (gcLowerBuy.Contains("healthpotion"))
                                gcGold = 0.175f;
                            else if (gcLowerBuy.Contains("majormanapotion"))
                                gcGold = 0.2f;
                            else if (gcLowerBuy.Contains("manapotion"))
                                gcGold = 0.175f;
                            else if (gcLowerBuy.Contains("townportal"))
                            {
                                gcGold = 2.0f;
                                scaleToObserverLevel = false;
                            }

                            // ScaleToObserverLevel items use max(playerLevel, 3) as effective level.
                            // Town portal (no ScaleToObserverLevel): always level 1 → 100 gold
                            int effectiveLevel = scaleToObserverLevel ? Math.Max(playerLevel, 3) : 1;

                            // Client uses float math: (int)(goldPerLevel * level * goldValue * buyMod)
                            // Use free_ or member_ prefixed keys first, fall back to base key
                            string glvStr = ServerSettings.GetString(memberPrefix + "itemGoldValuePerLevel", null)
                                         ?? ServerSettings.GetString("itemGoldValuePerLevel", null);
                            float goldPerLevel = glvStr != null && float.TryParse(glvStr,
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float glvParsed)
                                ? glvParsed : 50f;

                            string bmStr = ServerSettings.GetString(memberPrefix + "itemBuyValueModifier", null)
                                        ?? ServerSettings.GetString("itemBuyValueModifier", null);
                            float buyMod = bmStr != null && float.TryParse(bmStr,
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float bmParsed)
                                ? bmParsed : 1.0f;

                            long unitPrice = Math.Max(1, (long)(goldPerLevel * effectiveLevel * gcGold * buyMod));

                            // Multiply by quantity for packs (x5 scrolls, x10 potions)
                            int qty = item_final.quantity > 0 ? item_final.quantity : 1;
                            price = (uint)(unitPrice * qty);

                            Debug.LogError($"[MerchantManager] Consumable price ({memberPrefix}): psLevel={ps?.Level}, dbLevel={savedChar.level}, effective={effectiveLevel}, gcGold={gcGold}, glv={goldPerLevel}, buyMod={buyMod}, unit={unitPrice}, qty={qty} → total={price}");
                        }
                        else if (gcLowerBuy.Contains("questitem"))
                        {
                            price = 1;  // Quest items: GoldValue=0, client shows min 1 gold
                        }
                        else
                        {
                            // Equipment: pre-calculated at merchant init with base keys.
                            // Recalculate here using the player's free_ or member_ prefix so the
                            // server charges exactly what the client displays.
                            if (item_final.goldValue > 0)
                            {
                                price = RarityHelper.CalculatePriceWithGoldValue(
                                    item_final.level, item_final.rarity, item_final.goldValue, memberPrefix);
                                Debug.LogError($"[MerchantManager] Equipment price ({memberPrefix}): level={item_final.level} rarity={item_final.rarity} gv={item_final.goldValue} → {price}");
                            }
                        }
                        Debug.LogError($"[MerchantManager] Item={item_final.gcType}, Price={price} gold, Rarity={item_final.rarity}, Level={item_final.level}");
                        if (RarityHelper.IsMythicPALItem(item_final.gcType))
                            Debug.LogError($"[BUY-MYTHIC] ████ {item_final.gcType}: price={price}, rarity={item_final.rarity}, level={item_final.level} ████");

                        Debug.LogError($"[MerchantManager] Player has {savedChar.gold} gold, needs {price}");
                        if (savedChar.gold < price)
                        {
                            Debug.LogError($"[MerchantManager] ❌ Not enough gold!");
                            return;
                        }
                        // Check inventory space BEFORE deducting gold
                        {
                            var checkDims = GetItemDimensions(item_final.gcType);
                            int cw = checkDims.width, ch = checkDims.height;
                            string cid = conn.ConnId.ToString();
                            bool hasSpace = false;
                            for (byte ry = 0; ry < 8 && !hasSpace; ry++)
                                for (byte cx = 0; cx < 10 && !hasSpace; cx++)
                                    if (!server.IsInventorySlotOccupied(cid, cx, ry, cw, ch))
                                        hasSpace = true;
                            if (!hasSpace)
                            {
                                Debug.LogError($"[MerchantManager] ❌ Inventory full! Buy rejected.");
                                return;
                            }
                        }
                        savedChar.gold -= price;
                        DungeonRunners.Database.CharacterRepository.SaveCharacter(savedChar);
                        Debug.LogError($"[MerchantManager] ✅ Deducted {price} gold. New balance: {savedChar.gold}");

                        // Only remove from merchant if NOT static (potions/quest items stay)
                        if (!inv_final.staticContents)
                        {
                            int itemIdToRemove = item_final.id;
                            int listIndex = inv_final.items.IndexOf(item_final);  // KEEP
                            inv_final.items.Remove(item_final);

                            var writer = new LEWriter();
                            writer.WriteByte(0x07);
                            writer.WriteByte(0x35);
                            writer.WriteUInt16(componentId);
                            writer.WriteByte(0x1F);
                            writer.WriteUInt32((uint)itemIdToRemove);

                            writer.WriteByte(0x02);
                            writer.WriteUInt32(0x00000000);
                            writer.WriteByte(0x06);

                            byte[] packet = writer.ToArray();
                            Debug.LogError($"[MerchantManager] Packet bytes: {BitConverter.ToString(packet)}");
                            Debug.LogError($"[MerchantManager] ████ REMOVE FROM: invID={inv.id} ('{inv.name}'), itemID={itemIdToRemove} ████");
                            sendPacket(conn, 0x01, 0x0F, packet);
                        }
                        else
                        {
                            Debug.LogError($"[MerchantManager] 📌 STATIC item - keeping in merchant inventory");
                        }

                        // Send gold update to player's UnitContainer
                        if (conn.UnitContainerId != 0)
                        {
                            var goldWriter = new LEWriter();
                            goldWriter.WriteByte(0x07);  // BeginStream
                            goldWriter.WriteByte(0x35);  // ComponentUpdate
                            goldWriter.WriteUInt16(conn.UnitContainerId);  // Player's UnitContainer
                            goldWriter.WriteByte(0x20);  // AddCurrency (fires 0x138A jingle unconditionally)
                            goldWriter.WriteInt32(-(int)price);  // negative = subtract via two's complement
                            goldWriter.WriteByte(0x00);  // CurrencySource
                            goldWriter.WriteUInt32(0x00000000);  // entityHandle (required by 0x20 parser)
                            goldWriter.WriteByte(0x01);  // notifyFlag (required by 0x20 parser)
                            server.WritePlayerEntitySynch(conn, goldWriter);
                            goldWriter.WriteByte(0x06);  // EndStream

                            byte[] goldPacket = goldWriter.ToArray();
                            Debug.LogError($"[MerchantManager] 💰 Sending gold update: -{price} gold to UnitContainer 0x{conn.UnitContainerId:X4}");
                            Debug.LogError($"[MerchantManager] Gold packet: {BitConverter.ToString(goldPacket)}");
                            sendPacket(conn, 0x01, 0x0F, goldPacket);
                        }
                        // ═══ ADD ITEM TO INVENTORY ═══
                        {
                            string buyGcType = item.gcType;
                            if (buyGcType.StartsWith("items.pal.", StringComparison.OrdinalIgnoreCase))
                                buyGcType = buyGcType.Substring(10);
                            // Map consumable GC definition paths to inventory gc_types
                            buyGcType = MapConsumableGcType(buyGcType);
                            buyGcType = buyGcType.ToLowerInvariant();

                            // Non-MythicPAL lookup table items need items.pal. prefix in client packets
                            string buyPacketGcType = buyGcType;
                            if (_mythicModSlots.ContainsKey(buyGcType) && !buyGcType.Contains("mythicpal"))
                                buyPacketGcType = "items.pal." + buyGcType;
                            // ItemPack → Major swap (hotbar recognition)
                            buyPacketGcType = DungeonRunners.Data.GCObject.GetPacketGCClassFor(buyPacketGcType);

                            int itemWidth = 2, itemHeight = 2;
                            var dims = GetItemDimensions(item_final.gcType);
                            itemWidth = dims.width;
                            itemHeight = dims.height;

                            string connId = conn.ConnId.ToString();

                            string gcLower2 = buyGcType.ToLower();
                            string nativeClass = "Armor";
                            if (gcLower2.Contains("consumable") || gcLower2.Contains("questitem") || gcLower2.Contains("ring") || gcLower2.Contains("amulet") || gcLower2.Contains("scroll") || gcLower2.Contains("potion"))
                                nativeClass = "Item";
                            else if (gcLower2.Contains("sword") || gcLower2.Contains("axe") || gcLower2.Contains("mace") || gcLower2.Contains("dagger") || gcLower2.Contains("hammer") || gcLower2.Contains("staff") || gcLower2.Contains("spear"))
                                nativeClass = "MeleeWeapon";
                            else if (gcLower2.Contains("bow") || gcLower2.Contains("gun") || gcLower2.Contains("crossbow"))
                                nativeClass = "RangedWeapon";

                            bool isQuestItem2 = buyGcType.Contains("questitem");

                            // Store buy price BEFORE save so it's included in character_inventory
                            SetBuyPrice(conn.ConnId.ToString(), buyGcType, price);
                            Debug.LogError($"[BUY] Stored buy_price={price} for {buyGcType}");

                            bool isConsumable = !isQuestItem2 &&
                                             (item.gcType.IndexOf("consumable", StringComparison.OrdinalIgnoreCase) >= 0
                                             || buyGcType.StartsWith("potionpal.", StringComparison.OrdinalIgnoreCase));

                            int buyQty = item.quantity > 0 ? item.quantity : 1;

                            if (isQuestItem2)
                            {
                                // Quest items: bare format, no ScaleMod, no stacking
                                byte slotX = 0, slotY = 0;
                                bool foundSlot = false;
                                for (byte row = 0; row < 8 && !foundSlot; row++)
                                    for (byte col = 0; col < 10 && !foundSlot; col++)
                                        if (!server.IsInventorySlotOccupied(connId, col, row, itemWidth, itemHeight))
                                        { slotX = col; slotY = row; foundSlot = true; }

                                if (!foundSlot)
                                {
                                    Debug.LogError($"[MerchantManager] Inventory full!");
                                    return;
                                }

                                uint slot = server.GetNextInventorySlot(connId);

                                var itemWriter = new LEWriter();
                                itemWriter.WriteByte(0x07);
                                itemWriter.WriteByte(0x35);
                                itemWriter.WriteUInt16(conn.UnitContainerId);
                                itemWriter.WriteByte(0x1E);
                                itemWriter.WriteByte(0x0B);
                                itemWriter.WriteByte(0xFF);
                                itemWriter.WriteCString(buyPacketGcType);
                                itemWriter.WriteUInt32(slot);
                                itemWriter.WriteByte(slotX);
                                itemWriter.WriteByte(slotY);
                                itemWriter.WriteByte(0x01);  // quantity
                                itemWriter.WriteByte(0x01);  // level
                                itemWriter.WriteByte(0x00);  // flags
                                itemWriter.WriteByte(0x00);  // modifier count = 0
                                itemWriter.WriteByte(0x02);
                                itemWriter.WriteUInt32(0x00000000);
                                itemWriter.WriteByte(0x06);
                                sendPacket(conn, 0x01, 0x0F, itemWriter.ToArray());

                                var newItem = new DungeonRunners.Data.GCObject { GCClass = buyPacketGcType, NativeClass = "Item" };
                                server.TrackInventoryItem(connId, slot, newItem, slotX, slotY);
                                server.OccupyInventorySlots(connId, slotX, slotY, itemWidth, itemHeight);
                                server.SetStackCount(connId, slot, 1);

                                Debug.LogError($"[MerchantManager] 📦 Quest item {buyGcType} at ({slotX},{slotY})");
                                server.SavePlayerInventoryPublic(conn);
                                // Notify QuestManager so item objectives update and NPC markers refresh
                                server.NotifyQuestItemAcquired(conn, buyGcType);
                            }
                            else if (isConsumable)
                            {
                                // GC MaxStackSize per type (from GC files):
                                // MajorHealthPotion / MajorManaPotion → 10
                                // MinorHealthPotion / MinorManaPotion → 5
                                // TownPortal                          → 5
                                // Other consumables                   → 5 (safe default)
                                int maxStack;
                                if (gcLower2.Contains("majorhealthpotion") || gcLower2.Contains("majormanapotion"))
                                    maxStack = 10;
                                else if (gcLower2.Contains("townportal"))
                                    maxStack = 5;
                                else if (gcLower2.Contains("healthpotion") || gcLower2.Contains("manapotion"))
                                    maxStack = isFree ? 5 : 10;  // members stack minor potions to 10
                                else
                                    maxStack = isFree ? 5 : 10;  // members stack other consumables to 10

                                // Search all inventory items for a PARTIAL stack of this type
                                // (count < maxStack). FindInventoryItemByGCClass returns the first
                                // match regardless of fullness — if it returns the full stack we'd
                                // fall through to new slot on EVERY subsequent purchase.
                                uint partialSlot = 0;
                                byte partialX = 0, partialY = 0;
                                int partialCount = 0;
                                bool foundPartial = false;

                                var allInv = server.GetAllInventoryItems(connId);
                                if (allInv != null)
                                {
                                    string searchGc = buyPacketGcType.ToLowerInvariant();
                                    Debug.LogError($"[STACK-SEARCH] Looking for partial stack of '{searchGc}' (max={maxStack}) among {allInv.Count} items");
                                    foreach (var invKvp in allInv)
                                    {
                                        string storedGc = invKvp.Value.Item1.GCClass.ToLowerInvariant();
                                        int cnt = server.GetStackCount(connId, invKvp.Key);
                                        Debug.LogError($"[STACK-SEARCH]   slot={invKvp.Key} gc='{storedGc}' count={cnt}");
                                        if (storedGc == searchGc && cnt < maxStack)
                                        {
                                            partialSlot = invKvp.Key;
                                            partialX = invKvp.Value.Item2;
                                            partialY = invKvp.Value.Item3;
                                            partialCount = cnt;
                                            foundPartial = true;
                                            Debug.LogError($"[STACK-SEARCH] ✓ Found partial stack at slot={partialSlot} count={cnt}");
                                            break;
                                        }
                                    }
                                    if (!foundPartial)
                                        Debug.LogError($"[STACK-SEARCH] No partial stack found — will create new slot");
                                }

                                bool addedToExisting = false;

                                if (foundPartial)
                                {
                                    int newCount = Math.Min(partialCount + buyQty, maxStack);

                                    // In-place update: remove then re-add with SAME slot ID and updated count
                                    var itemWriter = new LEWriter();
                                    itemWriter.WriteByte(0x07);
                                    itemWriter.WriteByte(0x35);
                                    itemWriter.WriteUInt16(conn.UnitContainerId);
                                    itemWriter.WriteByte(0x1F);
                                    itemWriter.WriteUInt32(partialSlot);
                                    itemWriter.WriteByte(0x02);
                                    itemWriter.WriteUInt32(0x00000000);
                                    itemWriter.WriteByte(0x35);
                                    itemWriter.WriteUInt16(conn.UnitContainerId);
                                    itemWriter.WriteByte(0x1E);
                                    itemWriter.WriteByte(0x0B);
                                    itemWriter.WriteByte(0xFF);
                                    itemWriter.WriteCString(buyPacketGcType);
                                    itemWriter.WriteUInt32(partialSlot);   // same slot ID
                                    itemWriter.WriteByte(partialX);
                                    itemWriter.WriteByte(partialY);
                                    itemWriter.WriteByte((byte)newCount);
                                    itemWriter.WriteByte(0x01);
                                    itemWriter.WriteByte(0x00);
                                    itemWriter.WriteByte(0x00);
                                    itemWriter.WriteByte(0x02);
                                    itemWriter.WriteUInt32(0x00000000);
                                    itemWriter.WriteByte(0x06);
                                    sendPacket(conn, 0x01, 0x0F, itemWriter.ToArray());

                                    server.SetStackCount(connId, partialSlot, newCount);
                                    Debug.LogError($"[MerchantManager] 📦 STACKED {buyGcType}: {partialCount} + {buyQty} = {newCount} at ({partialX},{partialY}) slot={partialSlot}");
                                    server.SavePlayerInventoryPublic(conn);
                                    addedToExisting = true;
                                }

                                if (!addedToExisting)
                                {
                                    // No existing stack, OR existing stack is full → create new stack
                                    byte slotX = 0, slotY = 0;
                                    bool foundSlot = false;
                                    for (byte row = 0; row < 8 && !foundSlot; row++)
                                        for (byte col = 0; col < 10 && !foundSlot; col++)
                                            if (!server.IsInventorySlotOccupied(connId, col, row, itemWidth, itemHeight))
                                            { slotX = col; slotY = row; foundSlot = true; }

                                    if (!foundSlot)
                                    {
                                        Debug.LogError($"[MerchantManager] Inventory full!");
                                        return;
                                    }

                                    uint slot = server.GetNextInventorySlot(connId);

                                    var itemWriter = new LEWriter();
                                    itemWriter.WriteByte(0x07);
                                    itemWriter.WriteByte(0x35);
                                    itemWriter.WriteUInt16(conn.UnitContainerId);
                                    itemWriter.WriteByte(0x1E);
                                    itemWriter.WriteByte(0x0B);
                                    itemWriter.WriteByte(0xFF);
                                    itemWriter.WriteCString(buyPacketGcType);
                                    itemWriter.WriteUInt32(slot);
                                    itemWriter.WriteByte(slotX);
                                    itemWriter.WriteByte(slotY);
                                    itemWriter.WriteByte((byte)buyQty);
                                    itemWriter.WriteByte(0x01);  // level
                                    itemWriter.WriteByte(0x00);  // flags
                                    itemWriter.WriteByte(0x00);  // modifier count = 0
                                    itemWriter.WriteByte(0x02);
                                    itemWriter.WriteUInt32(0x00000000);
                                    itemWriter.WriteByte(0x06);
                                    sendPacket(conn, 0x01, 0x0F, itemWriter.ToArray());

                                    var newItem = new DungeonRunners.Data.GCObject { GCClass = buyPacketGcType, NativeClass = "Item" };
                                    server.TrackInventoryItem(connId, slot, newItem, slotX, slotY);
                                    server.OccupyInventorySlots(connId, slotX, slotY, itemWidth, itemHeight);
                                    server.SetStackCount(connId, slot, buyQty);

                                    Debug.LogError($"[MerchantManager] 📦 NEW STACK {buyGcType} x{buyQty} at ({slotX},{slotY})");
                                    server.SavePlayerInventoryPublic(conn);
                                }
                            }
                            else
                            {
                                // Equipment: use WriteInitForInventory (handles ScaleMod)
                                byte slotX = 0, slotY = 0;
                                bool foundSlot = false;
                                for (byte row = 0; row < 8 && !foundSlot; row++)
                                    for (byte col = 0; col < 10 && !foundSlot; col++)
                                        if (!server.IsInventorySlotOccupied(connId, col, row, itemWidth, itemHeight))
                                        { slotX = col; slotY = row; foundSlot = true; }

                                if (!foundSlot)
                                {
                                    Debug.LogError($"[MerchantManager] Inventory full!");
                                    return;
                                }

                                uint slot = server.GetNextInventorySlot(connId);
                                var newItem = new DungeonRunners.Data.GCObject { GCClass = buyPacketGcType, NativeClass = nativeClass };
                                // Use the SAME ScaleMod that was shown in the merchant display
                                // This prevents stats from changing between shop preview and inventory
                                newItem.PresetScaleMod = item_final.scaleMod;
                                newItem.StoredRarity = (int)item_final.rarity;
                                newItem.StoredLevel = item_final.level > 0 ? item_final.level : RarityHelper.GetItemLevel(buyGcType);

                                var itemWriter = new LEWriter();
                                itemWriter.WriteByte(0x07);
                                itemWriter.WriteByte(0x35);
                                itemWriter.WriteUInt16(conn.UnitContainerId);
                                itemWriter.WriteByte(0x1E);
                                itemWriter.WriteByte(0x0B);
                                int actualItemLevel = item_final.level > 0 ? item_final.level : RarityHelper.GetItemLevel(buyGcType);
                                newItem.WriteInitForInventory(itemWriter, slotX, slotY, slot, actualItemLevel);
                                itemWriter.WriteByte(0x02);
                                itemWriter.WriteUInt32(0x00000000);
                                itemWriter.WriteByte(0x06);
                                sendPacket(conn, 0x01, 0x0F, itemWriter.ToArray());

                                server.TrackInventoryItem(connId, slot, newItem, slotX, slotY);
                                server.OccupyInventorySlots(connId, slotX, slotY, itemWidth, itemHeight);

                                Debug.LogError($"[MerchantManager] 📦 Added equipment {buyGcType} at ({slotX},{slotY})");
                                server.SavePlayerInventoryPublic(conn);
                            }
                        }

                        Debug.LogError($"[MerchantManager] ✅ BUY COMPLETE: {item.gcType} for {price} gold");
                        Debug.LogError($"[MerchantManager] ═══════════════════════════════════════════");
                        return;
                    }
                }
            }
            Debug.LogError($"[MerchantManager] ❌ No item found with ID={targetId}");
            Debug.LogError($"[MerchantManager] ═══════════════════════════════════════════");
        }

        /// <summary>
        /// Handle sell item — MM18.
        ///
        /// CRITICAL INSIGHT: MM13 sent TWO packets (0x20 AddCurrency + 0x29 ClearCursor).
        /// It crashed. But 0x29 is not a valid Container sub-opcode — THAT was the crasher.
        /// Sub-opcode 0x20 was NEVER tested in isolation!
        ///
        /// Container sub-opcode pattern (all proven except 0x20):
        ///   0x1E = AddItem (buy adds to inventory — proven)
        ///   0x1F = RemoveItem (buy removes from merchant — proven)
        ///   0x20 = AddCurrency (UNTESTED ALONE — MM13 crashed from the 0x29 that followed)
        ///   0x21 = RemoveCurrency (buy deducts gold — proven)
        ///
        /// MM13 also used synch=0x00000000 instead of real GetSynchValue(conn).
        ///
        /// MM18: Send ONLY two proven-format packets:
        ///   1. RemoveItem (0x1F) on UnitContainer — remove sold item
        ///   2. AddCurrency (0x20) on UnitContainer — add gold (same format as 0x21)
        /// Both use real synch value. NO 0x29 or other unproven opcodes.
        /// </summary>
        public static void HandleSellItem(RRConnection conn, ushort componentId, ushort itemId, ushort entityRef,
      Dictionary<uint, List<UnityGameServer.ZoneNPC>> zoneNPCs,
      Dictionary<string, DungeonRunners.Data.GCObject> selectedCharacters,
      Action<RRConnection, byte, byte, byte[]> sendPacket,
      PlayerState playerState,
      UnityGameServer server,
      uint synchValue)
        {
            Debug.LogError($"[SELL] ═══════════════════════════════════════════");
            Debug.LogError($"[SELL] itemId={itemId}, entityRef=0x{entityRef:X4}, merchantCid=0x{componentId:X4}");
            Debug.LogError($"[SELL] UC=0x{conn.UnitContainerId:X4}, synch=0x{synchValue:X8}");

            var activeItem = playerState?.ActiveItem;
            bool isShiftClick = (activeItem == null);
            DungeonRunners.Data.GCObject sellItem = activeItem;
            byte removeX = 0, removeY = 0;

            if (isShiftClick)
            {
                // Shift+Click sell: item is still in inventory, look it up by slot
                var invItem = server.GetInventoryItemBySlot(conn.ConnId.ToString(), (uint)itemId);
                if (invItem == null)
                {
                    Debug.LogError($"[SELL] Shift+Click: no item at slot {itemId}");
                    return;
                }
                sellItem = invItem.Value.item;
                removeX = invItem.Value.x;
                removeY = invItem.Value.y;
                Debug.LogError($"[SELL] Shift+Click selling: {sellItem.GCClass} from slot {itemId} at ({removeX},{removeY})");
            }
            else
            {
                Debug.LogError($"[SELL] Cursor selling: {activeItem.GCClass}");
            }

            // ── Calculate sell price (match client formula from disassembly at 0x59b700) ──
            // Client: rawGold = goldPerLevel * level * goldValue * qualityMod
            // Then multiply by SellMod (0.20) in Fixed32
            uint sellPrice = 1;
            string sellGcType = sellItem.GCClass.ToLowerInvariant();
            if (sellGcType.StartsWith("items.pal."))
                sellGcType = sellGcType.Substring(10);
            else if (sellGcType.StartsWith("items.consumables."))
                sellGcType = sellGcType.Substring(18);
            {
                int itemLevel = RarityHelper.GetItemLevel(sellItem.GCClass);
                if (itemLevel < 1) itemLevel = 1;

                // Get REAL GoldValue from gc_gold_value column (armor=4.0, boots=1.25, etc)
                float goldValue = 1.0f;
                if (_sellableItems != null)
                {
                    var sellable = _sellableItems.FirstOrDefault(s =>
                        string.Equals(s.gcType, sellGcType, StringComparison.OrdinalIgnoreCase));
                    if (sellable == null)
                        sellable = _sellableItems.FirstOrDefault(s =>
                            s.gcType.ToLower().Contains(sellGcType) || sellGcType.Contains(s.gcType.ToLower()));
                    if (sellable != null && sellable.gcGoldValue > 0)
                        goldValue = sellable.gcGoldValue;
                    else
                        goldValue = RarityHelper.GetBaseGoldValue(sellGcType);
                }
                else
                    goldValue = RarityHelper.GetBaseGoldValue(sellGcType);

                // Get actual rarity from tier suffix
                int tierSuffix = RarityHelper.GetTierFromGcType(sellGcType);
                var itemRarity = RarityHelper.GetRarityFromTier(tierSuffix);

                // FIX: MythicPAL items have NO -N suffix → tierSuffix=1 → Normal rarity.
                // Must force Mythic rarity and use playerLevel+3 (ScaleToObserverLevel from GC).
                bool isMythicSell = RarityHelper.IsMythicPALItem(sellGcType);
                int pLevel = (playerState != null && playerState.Level > 0) ? playerState.Level : 0;
                if (isMythicSell)
                {
                    itemRarity = ItemRarity.Mythic;
                    if (pLevel > 0)
                        itemLevel = pLevel + 3;
                    Debug.LogError($"[SELL] MYTHIC-FIX: {sellGcType} → rarity=Mythic, level={itemLevel} (playerLevel={pLevel}+3)");
                }

                // FIX: Client applies ItemLevelDelta to sell price too!
                // TTD-verified: Unique cannon base level=41, delta=-2 → client uses 39 → sell=3,620.
                // Server was using 41 → sell=3,806 (186g too high).
                // Mythic: delta=+3 then capped at playerLevel+5 inside CalculateSellPrice.
                int adjustedLevel = RarityHelper.GetEquipRequiredLevel(itemLevel, itemRarity);

                sellPrice = RarityHelper.CalculateSellPrice(adjustedLevel, goldValue, itemRarity, isMythicSell, pLevel);

                // ── EXPLOIT PREVENTION: cap sell price at item's actual buy price ──
                // Buy also uses adjustedLevel (same GetEquipRequiredLevel call)
                uint buyPrice = RarityHelper.CalculatePriceWithGoldValue(adjustedLevel, itemRarity, goldValue);
                if (sellPrice > buyPrice)
                {
                    Debug.LogError($"[SELL] ⚠️ CAPPED: sell={sellPrice} > buy={buyPrice} for level={itemLevel}→adj={adjustedLevel} rarity={itemRarity} gv={goldValue}. Capping to buy price.");
                    sellPrice = buyPrice;
                }

                Debug.LogError($"[SELL] Price: level={itemLevel}→adj={adjustedLevel}, goldValue={goldValue}, rarity={itemRarity}, mythicPath={isMythicSell}, pLevel={pLevel}, sell={sellPrice}, buy={buyPrice}");
            }

            // ── Save gold to DB ──
            if (!selectedCharacters.TryGetValue(conn.LoginName, out var gcObj)) return;
            var savedChar = DungeonRunners.Database.CharacterRepository.GetCharacter(gcObj.Id);
            if (savedChar == null) return;

            uint oldGold = savedChar.gold;
            savedChar.gold += sellPrice;
            DungeonRunners.Database.CharacterRepository.SaveCharacter(savedChar);
            Debug.LogError($"[SELL] DB gold: {oldGold} → {savedChar.gold} (+{sellPrice})");

            // ═══════════════════════════════════════════════════════════════════
            // PACKET FORMAT — derived from disassembly of DungeonRunners.exe
            //
            // Sub-opcode 0x29 — processClearActiveItem (0x58e0a0):
            //   Handler reads: 0 bytes
            //
            // Sub-opcode 0x20 — processAddCurrency (0x57d9e0):
            //   Handler reads: amount(4) + source(1) + entityHandle(4) + notifyFlag(1)
            //   Total: 10 bytes — NOT the same as 0x21 which only reads 5!
            // ═══════════════════════════════════════════════════════════════════
            if (conn.UnitContainerId != 0)
            {
                var writer = new LEWriter();
                writer.WriteByte(0x07);  // BeginStream

                if (isShiftClick)
                {
                    // Shift+Click: Remove item from inventory (0x1F)
                    writer.WriteByte(0x35);
                    writer.WriteUInt16(conn.UnitContainerId);
                    writer.WriteByte(0x1F);
                    writer.WriteUInt32((uint)itemId);
                    server.WritePlayerEntitySynch(conn, writer);
                }
                else
                {
                    // Regular sell: ClearActiveItem (0x29) — handler reads 0 bytes
                    writer.WriteByte(0x35);
                    writer.WriteUInt16(conn.UnitContainerId);
                    writer.WriteByte(0x29);
                    server.WritePlayerEntitySynch(conn, writer);
                }

                // Block 2: AddCurrency (0x20) — handler reads 10 bytes
                writer.WriteByte(0x35);
                writer.WriteUInt16(conn.UnitContainerId);
                writer.WriteByte(0x20);
                writer.WriteUInt32(sellPrice);       // [read 1] amount (4 bytes)
                writer.WriteByte(0x00);              // [read 2] CurrencySource (1 byte)
                writer.WriteUInt32(0x00000000);      // [read 3] entityHandle (4 bytes)
                writer.WriteByte(0x01);              // [read 4] notifyFlag (1 byte)
                server.WritePlayerEntitySynch(conn, writer);

                writer.WriteByte(0x06);  // EndStream

                byte[] sellPacket = writer.ToArray();
                Debug.LogError($"[SELL] Packet ({sellPacket.Length} bytes): {BitConverter.ToString(sellPacket)}");
                sendPacket(conn, 0x01, 0x0F, sellPacket);
            }

            // ── Server-side cleanup ──
            string connId = conn.ConnId.ToString();
            var removed = server.GetAndRemoveInventoryItem(connId, (uint)itemId);
            if (removed != null)
                Debug.LogError($"[SELL] Removed slot {itemId}: {removed.Value.item.GCClass}");
            else
                Debug.LogError($"[SELL] Slot {itemId} not in tracking");

            if (!isShiftClick)
                playerState.ActiveItem = null;
            server.SavePlayerInventoryPublic(conn);

            Debug.LogError($"[SELL] ✅ SOLD {sellItem.GCClass} for {sellPrice}g (DB: {savedChar.gold}g)");
            Debug.LogError($"[SELL] ═══════════════════════════════════════════");
        }








        /// <summary>
        /// Maps merchant GC definition paths to inventory gc_types.
        /// Consumables use different gc_types in the client inventory system than in GC files.
        /// </summary>
        private static string MapConsumableGcType(string gcType)
        {
            string lower = gcType.ToLowerInvariant();
            // Health potions — check MAJOR before generic (generic is a substring of major)
            if (lower.Contains("consumable_majorhealthpotion"))
                return "potionpal.healthpotion_itempack";
            if (lower.Contains("consumable_minorhealthpotion") || lower.Contains("consumable_healthpotion"))
                return "potionpal.healthpotion_noob";
            // Mana potions — check MAJOR before generic
            if (lower.Contains("consumable_majormanapotion"))
                return "potionpal.manapotion_itempack";
            if (lower.Contains("consumable_minormanapotion") || lower.Contains("consumable_manapotion"))
                return "potionpal.manapotion_noob";
            // Town Portal — keep full path, client uses it as-is
            if (lower.Contains("consumable_townportal"))
                return "items.consumables.Consumable_TownPortal";
            // No mapping needed
            return gcType;
        }


        private static void WriteItem(LEWriter writer, MerchantItemRuntimeData item)
        {
            bool verboseItemLogging = VerboseMerchantItemLogging;
            string gcTypeToSend = item.gcType;
            // Strip prefixes
            if (gcTypeToSend.StartsWith("items.pal.", StringComparison.OrdinalIgnoreCase))
                gcTypeToSend = gcTypeToSend.Substring(10);
            else if (gcTypeToSend.StartsWith("items.consumables.", StringComparison.OrdinalIgnoreCase))
                gcTypeToSend = gcTypeToSend.Substring(18);
            // ALWAYS lowercase
            gcTypeToSend = gcTypeToSend.ToLowerInvariant();

            bool isConsumable = item.gcType.IndexOf("consumable", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isQuestItem = item.gcType.IndexOf("QuestItem", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isSimpleItem = isConsumable || isQuestItem;  // No ScaleMod for these

            var itemData = DatabaseLoader.FindItem(gcTypeToSend);
            if (itemData == null)
            {
                Debug.LogError($"[WriteItem] ⚠️ NOT IN DB: {gcTypeToSend} (original: {item.gcType}) — using defaults");
            }
            int modSlots;
            if (isSimpleItem)
            {
                modSlots = itemData?.modCount ?? 1;
            }
            else if (_mythicModSlots.TryGetValue(gcTypeToSend, out int specialSlots))
            {
                modSlots = specialSlots;
                if (verboseItemLogging)
                    Debug.LogError($"[WriteItem] modSlots={modSlots} from lookup for {gcTypeToSend}");
            }
            else
            {
                if (itemData != null)
                {
                    modSlots = itemData.modCount;
                }
                else
                {
                    // Item NOT in DB — compute correct default from item type
                    // Weapons=1, chain armor (no SpeedM)=1, all other armor/shields=2
                    bool isFallbackWeapon = IsWeaponType(gcTypeToSend);
                    bool isFallbackChain = gcTypeToSend.StartsWith("chain") && !gcTypeToSend.Contains("shield");
                    modSlots = (isFallbackWeapon || isFallbackChain) ? 1 : 2;
                    Debug.LogError($"[WriteItem] ⚠️ NOT IN DB: {gcTypeToSend} — using type-based default modSlots={modSlots} (weapon={isFallbackWeapon})");
                }
                // Chain armor (not shield, not mythic) has NO SpeedM — DB says 2 but correct is 1
                if (gcTypeToSend.StartsWith("chain") && !gcTypeToSend.Contains("shield") && !gcTypeToSend.Contains("mythic"))
                    modSlots = 1;
                if (verboseItemLogging)
                    Debug.LogError($"[WriteItem] modSlots={modSlots} from {(itemData != null ? "DB" : "DEFAULT")} for {gcTypeToSend}");
            }
            // Safety: clamp modSlots to sane range
            if (modSlots < 0 || modSlots > 12)
            {
                Debug.LogError($"[WriteItem] ❌ BAD modSlots={modSlots} for {gcTypeToSend} — clamping to 2");
                modSlots = 2;
            }
            // Non-MythicPAL lookup table items need items.pal. prefix in client packets
            string packetGcType = gcTypeToSend;
            if (_mythicModSlots.ContainsKey(gcTypeToSend) && !gcTypeToSend.Contains("mythicpal"))
                packetGcType = "items.pal." + gcTypeToSend;
            // ItemPack → Major swap (hotbar recognition)
            packetGcType = DungeonRunners.Data.GCObject.GetPacketGCClassFor(packetGcType);

            if (verboseItemLogging)
            {
                Debug.LogError($"[WriteItem] ████ WRITING: {item.gcType} id={item.id} rarity={item.rarity} qty={item.quantity} scaleMod={item.scaleMod ?? "NULL"} modSlots={modSlots} ████");
                Debug.LogError($"[WriteItem] {gcTypeToSend}: isConsumable={isConsumable} isQuest={isQuestItem} modSlots={modSlots} dbFound={itemData != null}");
            }
            writer.WriteByte(0xFF);
            writer.WriteCString(packetGcType);
            writer.WriteUInt32((uint)item.id);
            writer.WriteByte((byte)item.inventoryX);
            writer.WriteByte((byte)item.inventoryY);
            writer.WriteByte((byte)item.quantity);
            int level = item.level > 0 ? item.level : (isSimpleItem ? 1 : RarityHelper.GetItemLevel(item.gcType));
            writer.WriteByte((byte)level);

            // NO flag byte in merchant path. The flag byte (RequiresMembership) is part of
            // Equipment::readInit / WriteInitForInventory — a DIFFERENT format.
            // Merchant items go through Container → ReadChildData<Item> → Item::readData,
            // which reads: ID, InvX, InvY, Qty, Level, then ReadChildData<ItemModifier>.
            // ReadChildData Phase 1 reads one byte per GC-defined child (= modSlots),
            // Phase 2 reads count byte for additional server-sent children.
            // Writing a flag byte here shifts everything by 1 → stream desync.

            // Mod bytes — count from DB
            for (int i = 0; i < modSlots; i++)
            {
                writer.WriteByte(0x00);
            }

            if (isConsumable)
            {
                writer.WriteByte(0x01);
                writer.WriteByte(0xFF);
                writer.WriteCString("mods.itemscale.normal");
                writer.WriteByte(0x03);
                writer.WriteByte(0x15);
                writer.WriteUInt32(0x11111111);
                if (verboseItemLogging)
                    Debug.LogError($"[WriteItem] CONSUMABLE: {gcTypeToSend}, modSlots={modSlots}");
            }
            else if (isQuestItem)
            {
                writer.WriteByte(0x00);
                if (verboseItemLogging)
                    Debug.LogError($"[WriteItem] QUEST: {gcTypeToSend}, modSlots={modSlots}");
            }
            else
            {
                // Check if this item has mod children in GC class (mythic/prebuilt/etc).
                // These items already have Mod1-Mod5+ baked into the GC definition.
                // If we write a ScaleMod child here, Item::readData tries to read it
                // as data for an existing GC-defined ItemModifier child → stream desync → crash.
                bool hasModChildren = gcTypeToSend.Contains("mythic") ||
                    gcTypeToSend.Contains("prebuilt") ||
                    gcTypeToSend.Contains("partialbuilt") ||
                    gcTypeToSend.Contains("seasonal") ||
                    gcTypeToSend.Contains("wishingwell") ||
                    gcTypeToSend.Contains("boss") ||
                    gcTypeToSend.Contains("generated");

                // Path B — non-mythic items can also have wire mods via the IG-stub pipeline
                // (Rare/Unique direct-Item IGs + Magic/Superior wrapper IGs). Try direct lookup
                // first, then wrapper-IG fallback keyed by rarity.
                var wireMods = DungeonRunners.Data.ItemStatDatabase.Instance.GetItemWireMods(gcTypeToSend);
                string wireSource = wireMods.Count > 0 ? "direct" : null;
                if (wireMods.Count == 0)
                {
                    wireMods = DungeonRunners.Data.ItemStatDatabase.Instance.GetWrapperIGWireMods(gcTypeToSend, item.rarity.ToString());
                    if (wireMods.Count > 0) wireSource = $"wrapper:{item.rarity}";
                }
                bool hasInjectableMods = DungeonRunners.Data.ItemStatDatabase.PathBEnabled && wireMods.Count > 0;

                if (hasModChildren || hasInjectableMods)
                {
                    if (wireMods.Count > 0)
                    {
                        // Pre-resolve hashes; skip any mod whose class isn't in the dict.
                        // Wire format: per-mod (0x04 + UInt32(DJB2(lowered)) + 0x00 flags) per the
                        // 2026-05-19 mythic IG-stub fix. Rarity-agnostic per Ghidra GCClassRegistry::readType.
                        var resolved = new List<uint>(wireMods.Count);
                        var skipped = new List<string>();
                        foreach (var (slot, modRef) in wireMods.OrderBy(w => w.Slot))
                        {
                            uint h = DungeonRunners.Data.ItemStatDatabase.Instance.GetGCClassHash(modRef);
                            if (h != 0) resolved.Add(h);
                            else skipped.Add(modRef);
                        }
                        if (resolved.Count == 0)
                        {
                            writer.WriteByte(0x00);
                            Debug.LogError($"[WriteItem] IG-INJECT (all mods missing from dict, count=0): gc={gcTypeToSend} rarity={item.rarity} src={wireSource} skipped=[{string.Join(",", skipped)}]");
                        }
                        else
                        {
                            writer.WriteByte((byte)resolved.Count);   // Phase 2 count
                            foreach (uint h in resolved)
                            {
                                writer.WriteByte(0x04);               // type tag: 4-byte class hash
                                writer.WriteUInt32(h);                // DJB2(lowered canonical name)
                                writer.WriteByte(0x00);               // ItemModifier flags=0
                            }
                            if (verboseItemLogging)
                            {
                                string hashes = string.Join(",", resolved.Select(x => $"0x{x:X8}"));
                                Debug.LogError($"[WriteItem] IG-INJECT: gc={gcTypeToSend} rarity={item.rarity} src={wireSource} modSlots={modSlots} mods={resolved.Count} skipped={skipped.Count} djb2=[{hashes}]");
                            }
                        }
                    }
                    else
                    {
                        // hasModChildren=true but no wire mods (e.g., partialbuilt with no IG-stub).
                        writer.WriteByte(0x00);
                        if (verboseItemLogging)
                            Debug.LogError($"[WriteItem] SPECIAL (0 children): {gcTypeToSend}, rarity={item.rarity}, modSlots={modSlots}, dbMod={itemData?.modCount}");
                    }
                }
                else
                {
                    // Regular -N suffix items not covered by Path B: write single ScaleMod child (legacy fallback).
                    string scaleMod = !string.IsNullOrEmpty(item.scaleMod) ? item.scaleMod : RarityHelper.GetRandomScaleMod(item.rarity);
                    if (verboseItemLogging)
                        Debug.LogError($"[WriteItem] EQUIP-SCALEMOD: {gcTypeToSend}, rarity={item.rarity}, ScaleMod={scaleMod}, modSlots={modSlots}, dbMod={itemData?.modCount}");
                    writer.WriteByte(0x01);
                    writer.WriteByte(0xFF);
                    writer.WriteCString(scaleMod);
                    writer.WriteByte(0x03);
                    writer.WriteByte(0x15);
                    writer.WriteUInt32(0x11111111);
                }
            }
        }

        #endregion
    }

    #region Data Classes

    [Serializable]
    public class SellableItem
    {
        public string gcType;
        public string name;
        public float goldValue;
        public float gcGoldValue;  // Real GoldValue from GC files (armor=4.0, boots=1.25, etc)
        public string rarity;
    }

    [Serializable]
    public class ItemDimensions
    {
        public int width;
        public int height;
    }

    [Serializable]
    public class SellableItemsWrapper
    {
        public List<SellableItem> Items;
    }

    public class PendingMerchantRefreshAdd
    {
        public uint zoneId;
        public string npcGcClass;
        public ushort componentId;
        public List<uint> removedItemIds = new List<uint>();
        public DateTime sendAfterUtc;
    }

    [Serializable]
    public class MerchantData
    {
        public string npcGcType;
        public string merchantGcType;
        public List<MerchantInventoryData> inventories = new List<MerchantInventoryData>();
    }

    [Serializable]
    public class MerchantInventoryData
    {
        public string name;
        public string gcType;
        public int id;
        public bool staticContents;
        public bool autoGenerateItems;
        public string itemGenerator;
        public int minItemLevel;
        public int maxItemLevel;
        public float regenerateIntervalSeconds;
        public string label;
        public int width;
        public int height;
        public List<MerchantItemData> items = new List<MerchantItemData>();
    }

    [Serializable]
    public class MerchantItemData
    {
        public string gcType;
        public int inventoryX;
        public int inventoryY;
        public int id;
        public int quantity = 1;
    }

    public class MerchantRuntimeData
    {
        public string npcGcType;
        public string merchantGcType;
        public int nextItemId = 0;  // Global counter for unique item IDs across all inventories
        public List<MerchantInventoryRuntimeData> inventories = new List<MerchantInventoryRuntimeData>();
    }

    public class MerchantInventoryRuntimeData
    {
        public string name;
        public string gcType;
        public int id;
        public string label;
        public int width;
        public int height;
        public bool staticContents;
        public bool serverSendsItems;  // Static inv sent with hasItems=true (unique IDs, avoids ID collision)
        public bool autoGenerateItems;
        public string itemGenerator;
        public int minItemLevel;
        public int maxItemLevel;
        public float regenerateIntervalSeconds;
        public int generatedForLevel;
        public List<MerchantItemRuntimeData> items = new List<MerchantItemRuntimeData>();
    }

    public class MerchantItemRuntimeData
    {
        public string gcType;
        public int inventoryX;
        public int inventoryY;
        public int id;
        public int quantity;
        public int level;  // item level for display (mythic = playerLevel+3, regular = PAL tier level)
        public ItemRarity rarity = ItemRarity.Rare;
        public uint price;  // pre-calculated base price (no free/member prefix applied)
        public float goldValue;  // raw GC gold value — used for buy-time per-player price recalculation
        public string scaleMod;  // Stored at generation time — reused on buy to prevent stat mismatch
    }
    public enum ItemRarity
    {
        Normal,
        Superior,
        Magical,
        Rare,
        Unique,
        Mythic
    }


    public static class RarityHelper
    {
        // EXACT Fixed32 values from GlobalKnobs.gc (as integers, divide by 256 to get float)
        private static readonly Dictionary<ItemRarity, int> QualityModifiersFixed32 = new Dictionary<ItemRarity, int>
    {
        { ItemRarity.Normal, 67 },      // 0.26171875
        { ItemRarity.Superior, 144 },   // 0.5625 (NOT round(0.56*256)=143!)
        { ItemRarity.Magical, 274 },    // 1.0703125
        { ItemRarity.Rare, 520 },       // 2.03125
        { ItemRarity.Unique, 1170 },    // 4.5703125
        { ItemRarity.Mythic, 9961 }     // 38.91015625
    };

        // Level deltas from GlobalKnobs.gc
        private static readonly Dictionary<ItemRarity, int> LevelDeltas = new Dictionary<ItemRarity, int>
    {
        { ItemRarity.Normal, -12 },
        { ItemRarity.Superior, -10 },
        { ItemRarity.Magical, -7 },
        { ItemRarity.Rare, -5 },
        { ItemRarity.Unique, -2 },
        { ItemRarity.Mythic, 3 }
    };

        // ScaleMod strings for each rarity
        private static readonly Dictionary<ItemRarity, string[]> ScaleMods = new Dictionary<ItemRarity, string[]>
    {
        { ItemRarity.Normal, new[] { "ScaleModPAL.Binder.Mod1" } },
        { ItemRarity.Superior, new[] { "ScaleModPAL.Superior.Mod1", "ScaleModPAL.Superior.Mod2", "ScaleModPAL.Superior.Mod3" } },
        { ItemRarity.Magical, new[] { "ScaleModPAL.Magic.Mod1", "ScaleModPAL.Magic.Mod2", "ScaleModPAL.Magic.Mod3", "ScaleModPAL.Magic.Mod4", "ScaleModPAL.Magic.Mod5", "ScaleModPAL.Magic.Mod6" } },
        { ItemRarity.Rare, new[] { "ScaleModPAL.Rare.Mod1", "ScaleModPAL.Rare.Mod2", "ScaleModPAL.Rare.Mod3", "ScaleModPAL.Rare.Mod4", "ScaleModPAL.Rare.Mod5" } },
        { ItemRarity.Unique, new[] { "ScaleModPAL.Unique.Mod0", "ScaleModPAL.Unique.Mod1", "ScaleModPAL.Unique.Mod2", "ScaleModPAL.Unique.Mod3", "ScaleModPAL.Unique.Mod4", "ScaleModPAL.Unique.Mod5", "ScaleModPAL.Unique.Mod6", "ScaleModPAL.Unique.Mod7", "ScaleModPAL.Unique.Mod8" } },
        { ItemRarity.Mythic, new[] { "ScaleModPAL.Rare.Mod1" } }
    };

        private static readonly (ItemRarity rarity, int weight)[] RarityWeights = new[]
        {
        (ItemRarity.Normal, 15),
        (ItemRarity.Superior, 25),
        (ItemRarity.Magical, 30),
        (ItemRarity.Rare, 20),
        (ItemRarity.Unique, 8),
        (ItemRarity.Mythic, 2)
    };

        private static System.Random _random = new System.Random();

        public static float GetPriceModifier(ItemRarity rarity)
        {
            return QualityModifiersFixed32.TryGetValue(rarity, out var fixed32) ? fixed32 / 256f : 1.0f;
        }

        /// <summary>
        /// Equip level requirement = itemLevel + levelDelta.
        /// Reads from server.cfg, falls back to GlobalKnobs.gc defaults.
        /// </summary>
        public static int GetEquipRequiredLevel(int itemLevel, ItemRarity rarity)
        {
            int delta;
            switch (rarity)
            {
                case ItemRarity.Normal: delta = (int)ServerSettings.GetFloat("itemLevelDeltaNormal", -12); break;
                case ItemRarity.Superior: delta = (int)ServerSettings.GetFloat("itemLevelDeltaSuperior", -10); break;
                case ItemRarity.Magical: delta = (int)ServerSettings.GetFloat("itemLevelDeltaMagical", -7); break;
                case ItemRarity.Rare: delta = (int)ServerSettings.GetFloat("itemLevelDeltaRare", -5); break;
                case ItemRarity.Unique: delta = (int)ServerSettings.GetFloat("itemLevelDeltaUnique", -2); break;
                case ItemRarity.Mythic: delta = (int)ServerSettings.GetFloat("itemLevelDeltaMythic", 3); break;
                default: delta = LevelDeltas.TryGetValue(rarity, out var ld) ? ld : 0; break;
            }
            return Math.Max(1, itemLevel + delta);
        }

        public static string GetRandomScaleMod(ItemRarity rarity)
        {
            if (ScaleMods.TryGetValue(rarity, out var mods) && mods.Length > 0)
                return mods[_random.Next(mods.Length)];
            return "ScaleModPAL.Rare.Mod1";
        }

        // Deterministic per-gcClass ScaleMod pick. Used by write paths that fire on every
        // zone-load / relog (INV-RESTORE, WriteInit*) to avoid "mods change on relog"
        // symptom when wire-mods aren't available for the item. Same gcClass+rarity always
        // returns the same mod across sessions.
        public static string GetDeterministicScaleMod(string gcClass, ItemRarity rarity)
        {
            if (ScaleMods.TryGetValue(rarity, out var mods) && mods.Length > 0)
            {
                uint h = 5381;
                if (!string.IsNullOrEmpty(gcClass))
                    foreach (char c in gcClass.ToLowerInvariant()) h = h * 33u + (uint)c;
                return mods[(int)(h % (uint)mods.Length)];
            }
            return "ScaleModPAL.Rare.Mod1";
        }

        public static ItemRarity GetRandomRarity()
        {
            int totalWeight = 0;
            foreach (var (_, weight) in RarityWeights)
                totalWeight += weight;

            int roll = _random.Next(totalWeight);
            int cumulative = 0;

            foreach (var (rarity, weight) in RarityWeights)
            {
                cumulative += weight;
                if (roll < cumulative)
                    return rarity;
            }
            return ItemRarity.Rare;
        }

        public static ItemRarity GetRarityFromTier(int tier)
        {
            return tier switch
            {
                1 => ItemRarity.Normal,
                2 => ItemRarity.Superior,
                3 => ItemRarity.Magical,
                4 => ItemRarity.Rare,
                5 => ItemRarity.Unique,
                _ => ItemRarity.Rare
            };
        }

        /// <summary>
        /// Detects MythicPAL items which have NO -N suffix and therefore fail
        /// GetTierFromGcType (returns 1 → Normal). These must be forced to Mythic rarity.
        /// Works with or without "items.pal." prefix, case-insensitive.
        /// </summary>
        public static bool IsMythicPALItem(string gcType)
        {
            if (string.IsNullOrEmpty(gcType)) return false;
            return gcType.IndexOf("mythicpal", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static int GetTierFromGcType(string gcType)
        {
            if (string.IsNullOrEmpty(gcType)) return 1;
            int dashIdx = gcType.LastIndexOf('-');
            if (dashIdx > 0 && dashIdx < gcType.Length - 1)
            {
                if (int.TryParse(gcType.Substring(dashIdx + 1), out int tier))
                    return tier;
            }
            return 1;
        }

        /// <summary>
        /// Get item level from GC class using PAL tier number (NOT suffix variant).
        /// GC format: items.pal.1HAxe2PAL.1HAxe2-5 → PAL tier=2 → level=11
        /// The suffix (-5) is a visual variant (1-12), the PAL number determines item level.
        /// Binary-verified: item level = (PAL_tier - 1) * 10 + 1
        /// REPLACES the broken ItemData.GetRequiredLevelFromGCClass which used suffix instead.
        /// </summary>
        public static int GetItemLevel(string gcType)
        {
            if (string.IsNullOrEmpty(gcType)) return 1;
            // Extract PAL tier: find digits immediately before LAST "PAL" (case-insensitive)
            // Must use LastIndexOf because gc_types start with "items.pal." which has "pal" too!
            int palIdx = gcType.LastIndexOf("PAL", StringComparison.OrdinalIgnoreCase);
            if (palIdx > 0)
            {
                // Walk backwards from "PAL" to find the tier number
                int numEnd = palIdx;
                int numStart = numEnd - 1;
                while (numStart >= 0 && char.IsDigit(gcType[numStart]))
                    numStart--;
                numStart++;
                if (numStart < numEnd)
                {
                    string tierStr = gcType.Substring(numStart, numEnd - numStart);
                    if (int.TryParse(tierStr, out int palTier))
                    {
                        if (palTier <= 1) return 1;
                        return (palTier - 1) * 10 + 1;
                    }
                }
            }
            return 1;
        }

        public static float GetBaseGoldValue(string gcType)
        {
            if (string.IsNullOrEmpty(gcType)) return 1.0f;
            string lower = gcType.ToLowerInvariant();
            if (lower.Contains("armor") || lower.Contains("robe")) return 4.0f;
            if (lower.Contains("shoulder") || lower.Contains("pauldron")) return 2.0f;
            if (lower.Contains("shield") || lower.Contains("buckler")) return 2.5f;
            // STAFF CHECK MUST BE BEFORE 2H CHECK: 2H staves inherit from BasePoleArm
            // which has NO GoldValue (defaults to 1.0), NOT from Base2HMelee (2.0).
            // Verified via TTD trace: 2HStaffMythic buy=91,438 matches GV=1.0 exactly.
            if (lower.Contains("staff")) return 1.0f;
            if (lower.Contains("2h") || lower.Contains("cannon") || lower.Contains("crossbow") || lower.Contains("rifle")) return 2.0f;
            if (lower.Contains("helm") || lower.Contains("hat") || lower.Contains("hood") || lower.Contains("cap")) return 1.5f;
            if (lower.Contains("boot") || lower.Contains("shoe") || lower.Contains("greave")) return 1.25f;
            if (lower.Contains("potion")) return 0.2f;
            return 1.0f;
        }

        public static uint CalculatePrice(int tier, ItemRarity rarity)
        {
            int level = tier <= 1 ? 1 : (tier - 1) * 10 + 1;
            return CalculatePriceWithGoldValue(level, rarity, 1.0f);
        }

        /// <summary>
        /// BUY price — reads ALL values from server.cfg with GlobalKnobs.gc defaults.
        /// Formula: goldPerLevel * adjustedLevel * goldValue * qualityMod * buyMod
        /// adjustedLevel = max(1, level + levelDelta[rarity])
        /// @set/@reload to change live. No rebuild needed.
        /// prefix: "free_" or "member_" — checked first, falls back to base key.
        /// </summary>
        public static uint CalculatePriceWithGoldValue(int level, ItemRarity rarity, float goldValue, string prefix = "")
        {
            // Level delta from server.cfg (same values used by GetEquipRequiredLevel)
            int adjustedLevel = GetEquipRequiredLevel(level, rarity);

            // Gold per level — checks prefix key first, then base key
            string glvStr = (!string.IsNullOrEmpty(prefix))
                ? (ServerSettings.GetString(prefix + "itemGoldValuePerLevel", null) ?? ServerSettings.GetString("itemGoldValuePerLevel", null))
                : ServerSettings.GetString("itemGoldValuePerLevel", null);
            int goldPerLevel = glvStr != null && float.TryParse(glvStr,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float glvF) ? (int)glvF : 50;

            // Quality modifier — checks prefix key first, then base key
            int qualityModFixed32 = GetQualityModFixed32(rarity, prefix);

            // Buy multiplier — checks prefix key first, then base key
            string bmStr = (!string.IsNullOrEmpty(prefix))
                ? (ServerSettings.GetString(prefix + "itemBuyValueModifier", null) ?? ServerSettings.GetString("itemBuyValueModifier", null))
                : ServerSettings.GetString("itemBuyValueModifier", null);
            float buyMod = bmStr != null && float.TryParse(bmStr,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float bmF) ? bmF : 1.0f;

            int goldValueFixed32 = (int)Math.Round(goldValue * 256);
            int buyModFixed32 = (int)Math.Round(buyMod * 256);

            long numerator = (long)goldPerLevel * adjustedLevel * goldValueFixed32 * qualityModFixed32;
            long price = numerator / 65536;

            // Apply buyMod (Fixed32 multiply)
            price = (price * buyModFixed32) / 256;

            return (uint)Math.Max(1, price);
        }

        /// <summary>
        /// Gets quality modifier as Fixed32 int.
        /// Reads from server.cfg with optional membership prefix (free_ or member_).
        /// Falls back to base key, then hardcoded binary-exact values.
        /// </summary>
        private static int GetQualityModFixed32(ItemRarity rarity, string prefix = "")
        {
            string key = null;
            switch (rarity)
            {
                case ItemRarity.Normal: key = "itemPriceModifierNormal"; break;
                case ItemRarity.Superior: key = "itemPriceModifierSuperior"; break;
                case ItemRarity.Magical: key = "itemPriceModifierMagical"; break;
                case ItemRarity.Rare: key = "itemPriceModifierRare"; break;
                case ItemRarity.Unique: key = "itemPriceModifierUnique"; break;
                case ItemRarity.Mythic: key = "itemPriceModifierMythic"; break;
            }

            if (key != null)
            {
                // Try prefixed key first (e.g. free_itemPriceModifierRare), then base key
                string lookupKey = (!string.IsNullOrEmpty(prefix)) ? prefix + key : key;
                float cfgVal = ServerSettings.GetFloat(lookupKey, -1f);
                if (cfgVal < 0 && !string.IsNullOrEmpty(prefix))
                    cfgVal = ServerSettings.GetFloat(key, -1f);  // fallback to base key
                if (cfgVal >= 0)
                    return (int)Math.Round(cfgVal * 256.0);
            }

            // Fallback to hardcoded Fixed32 from binary
            return QualityModifiersFixed32.TryGetValue(rarity, out var qm) ? qm : 256;
        }

        /// <summary>Alias for CalculatePriceWithGoldValue</summary>
        public static uint CalculateBuyPrice(int level, ItemRarity rarity, float goldValue)
        {
            return CalculatePriceWithGoldValue(level, rarity, goldValue);
        }

        /// <summary>
        /// SELL price — EXACT match to client binary Merchant::getSellValue + Item::getValue.
        /// Reverse-engineered from TTD trace of DungeonRunners.exe (March 2026).
        ///
        /// Client formula (from disassembly at 0x59B700 + 0x580BE0):
        ///   1. cappedLevel = min(itemLevel, playerLevel + 5)
        ///   2. getValue:
        ///      step1 = (goldPerLevel*256 * cappedLevel*256) >> 8   [Fixed32 imul+shrd]
        ///      modGV  = (goldValue*256 * qualityMod*256) >> 8      [Fixed32 imul+shrd]
        ///      step2 = (step1 * modGV) >> 8                        [Fixed32 imul+shrd]
        ///      getValue = step2 >> 8                               [sar eax,8 — TRUNCATES]
        ///   3. getSellValue:
        ///      valueQ8 = getValue << 8                             [shl esi,8 — PRECISION LOSS]
        ///      sellQ8  = (valueQ8 * sellMod) >> 8                  [Fixed32, sellMod=52]
        ///      sell    = sellQ8 >> 8                               [sar eax,8]
        ///
        /// Key discoveries from TTD:
        ///   - sellMod = 52 (0x34), NOT 51. RPGSettings[0xB4] = 0x34.
        ///   - Sell caps level at playerLevel+5 (lea eax,[ecx+5] at 0x580C72)
        ///   - getValue integer truncation then shl 8 back causes precision loss
        ///   - Verified: Mythic shield GV=2.5, playerLevel=41 → sell=45,444 EXACT
        /// </summary>
        public static uint CalculateSellPrice(int level, float goldValue, ItemRarity rarity = ItemRarity.Normal, bool isMythicPAL = false, int playerLevel = 0)
        {
            if (level < 1) level = 1;

            // ── Sell level cap: min(itemLevel, playerLevel + 5) ──
            // Client: lea eax,[ecx+5]; shl eax,8; cmp esi,eax; jle skip; mov esi,eax
            int sellLevel = level;
            if (playerLevel > 0)
            {
                int cap = playerLevel + 5;
                if (sellLevel > cap) sellLevel = cap;
            }
            // MythicPAL: client computes very high level from GC, capped to playerLevel+5
            if (isMythicPAL && playerLevel > 0)
                sellLevel = playerLevel + 5;

            int goldPerLevel = (int)ServerSettings.GetFloat("itemGoldValuePerLevel", 50f);
            int qualityModFixed32 = GetQualityModFixed32(rarity);
            int goldValueFixed32 = (int)Math.Round(goldValue * 256);

            // ── Item::getValue (0x580BE0) — exact Fixed32 chain ──
            // Step 1: goldPerLevel * level (both Q8.8, multiply + shrd 8)
            int gpl_Q8 = goldPerLevel * 256;
            int level_Q8 = sellLevel * 256;
            long step1 = ((long)gpl_Q8 * level_Q8) / 256;

            // Step 2: apply quality-modified goldValue (goldValue * qualityMod >> 8, then step1 * result >> 8)
            int modifiedGV = (int)((long)goldValueFixed32 * qualityModFixed32 / 256);
            long step2 = (step1 * modifiedGV) / 256;

            // Truncate to integer (sar eax, 8) — THIS TRUNCATION IS CRITICAL
            int getValueResult = (int)(step2 >> 8);
            if (getValueResult < 1) getValueResult = 1;

            // ── Merchant::getSellValue (0x59B700) ──
            // Convert back to Q8.8 (shl esi, 8) — PRECISION LOSS HERE
            long valueQ8 = (long)getValueResult * 256;

            // Fixed32 multiply by sellMod (RPGSettings[0xB4] = 52 = 0x34)
            // Client stores round_up(0.20 * 256) = 52, NOT round(51.2) = 51
            int sellModFixed32 = 52;
            long sellQ8 = (valueQ8 * sellModFixed32) / 256;

            // Convert to integer (sar eax, 8)
            int sellPrice = (int)(sellQ8 >> 8);
            return (uint)Math.Max(1, sellPrice);
        }
    }
    #endregion
}
