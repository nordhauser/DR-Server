using System;
using System.Linq;
using UnityEngine;
using DungeonRunners.Data;
using DungeonRunners.Networking;
using DungeonRunners.Core;

namespace DungeonRunners.Combat
{
    /// <summary>
    /// Binary-verified damage computation from DR.exe disassembly.
    /// ALL combat values now loaded from GC files via GCDatabase.
    /// 
    /// VERIFIED FROM BINARY (Session 18 re-analysis):
    /// - Fixed32 8.8 arithmetic (shift patterns at multiple call sites)
    /// - RNG #1 at 0x59804B: Generate() % 25700              (hit roll)
    /// - RNG #2 at 0x598133: (Generate() >> 8 & 0xFF) % 100 + 1  (block roll)
    /// - RNG #3 at 0x599011: Generate() % range + min         (damage roll, HIT only)
    /// - Weapon::playAttackSound at 0x598670 consumes Random::generator, not the room combat RNG
    ///
    /// MeleeWeapon::update (0x591980) per-cycle timeline (30 ticks):
    ///   Tick 29 (total-1): call 0x591B10 → Timer(6) → sends UseTarget TCP packet
    ///   Tick 10 (soundTick): call 0x598670 → Weapon::playAttackSound → global sound RNG consumed
    ///   Tick 15 (hitTick): vtable[0x128] → doHit → applyDamage → RNG #1-#3
    ///   Tick 0:            vtable[0xD8]  → attack cycle complete
    ///
    /// TOTAL ROOM RNG PER SWING: 2 (miss/block) or 3 (hit), plus the MeleeWeapon::use animation RNG.
    /// Weapon::playAttackSound at 0x598670 calls Random::generator then tests (al & 0x03).
    ///
    /// DAMAGE FORMULA (Weapon::applyDamage -> Weapon::computeDamageRange):
    ///   damageLevel = raw ApplyDamageContext+0x18 ushort copied from native weapon object field +0x88
    ///   damageBonus/damageMod = native Unit cached base + class/type + category fields
    ///   scaledDamage = (damageLevel + damageBonus) * weaponDamageMultiplier * damageMod
    ///   Apply DamageVolatility for min/max range; RNG picks value in [min, max]
    /// </summary>
    public static class DamageComputer
    {
        // ═══════════════════════════════════════════════════════════════
        // FIXED32 MATH — Binary verified from multiple call sites
        // Format: upper 24 bits integer, lower 8 bits fractional
        // ═══════════════════════════════════════════════════════════════

        public static int FromInt(int n) => n << 8;
        public static int FromFloat(float f) => (int)(f * 256f);
        public static int ToInt(int f) => f >> 8;
        public static float ToFloat(int f) => f / 256f;
        public static int FixedMul(int a, int b) => (int)(((long)a * (long)b) >> 8);

        /// <summary>Binary: rounds 0.5+ up, then clears fractional bits</summary>
        public static int RoundFixed32(int f)
        {
            if ((f & 0xFF) > 0x7E) f += 0x100;
            return f & ~0xFF; // clear lower 8 bits
        }

        public static int RollDamageRange(int minDmg, int maxDmg, uint raw)
        {
            int range = Math.Max(0, maxDmg - minDmg);
            return range > 0 ? (int)(raw % ((uint)range + 1u)) + minDmg : minDmg;
        }

        public static int RollSpellDamageRange(int minDmg, int maxDmg, uint raw)
        {
            int minHp = minDmg >> 8;
            int maxHp = maxDmg >> 8;
            if (minHp < 1) minHp = 1;
            if (maxHp < minHp) maxHp = minHp;

            int damageHp = minHp;
            int rangeHp = maxHp - minHp;
            if (rangeHp > 0)
                damageHp = (int)(raw % (uint)rangeHp) + minHp;

            return Math.Max(0x100, damageHp << 8);
        }

        /// <summary>
        /// Shared native Weapon::applyDamage path for player-owned and monster-owned weapon hits.
        /// Ghidra/PDB/i64 anchors: Weapon::applyDamage 0x00597E50, computeDamageRange 0x00598ED0,
        /// computeDamage 0x00598FD0. Room RNG order is hit, block, then damage only for a landed
        /// non-blocked hit; sound RNG is intentionally outside this stream.
        /// </summary>
        public static NativeWeaponDamageResult ResolveNativeWeaponDamage(NativeWeaponDamageInput input)
        {
            var result = new NativeWeaponDamageResult();
            if (input == null)
                return result;

            result.AttackRating = input.AttackRating;
            result.DefenseRating = input.DefenseRating;
            result.AttackerLevel = input.AttackerLevel;
            result.DefenderLevel = input.DefenderLevel;
            result.BlockChance = input.BlockChance;
            result.DamageLevel = input.DamageLevel;
            result.DamageBonus = input.DamageBonus;
            result.DamageMod = input.DamageMod;
            result.WeaponDamageF32 = input.WeaponDamageF32;
            result.WeaponVolatilityF32 = input.WeaponVolatilityF32;
            result.CritThreshold = input.CritThreshold;
            result.CritDamagePercent = input.CritDamagePercent;

            if (input.Rng == null)
            {
                result.Type = AttackResultType.Miss;
                result.ResultName = "NO-RNG";
                return result;
            }

            result.HitThreshold = ResolveNativeHitThreshold(
                input.AttackRating,
                input.DefenseRating,
                input.AttackerLevel,
                input.DefenderLevel);

            result.HitRaw = input.Rng.Generate();
            result.HitRoll = (int)(result.HitRaw % 25700u);

            result.BlockRaw = input.Rng.Generate();
            result.BlockRoll = (int)(((result.BlockRaw >> 8) & 0xFF) % 100) + 1;

            result.IsHit = result.HitRoll < result.HitThreshold;
            // Client (Weapon::applyDamage @ 0x598170): if (blockChance <= blockRoll) proceed to damage.
            // → client BLOCKS strictly when blockChance > blockRoll. At equality, NOT blocked.
            // Previous "<=" inverted behaviour at boundary blockRoll == blockChance.
            result.IsBlocked = result.IsHit && result.BlockRoll < input.BlockChance;

            if (!result.IsHit)
            {
                result.Type = AttackResultType.Miss;
                result.ResultName = "MISS";
                result.RoomRngAfter = input.Rng.CallsSinceReseed;
                return result;
            }

            if (result.IsBlocked)
            {
                result.Type = AttackResultType.Block;
                result.ResultName = "BLOCK";
                result.RoomRngAfter = input.Rng.CallsSinceReseed;
                return result;
            }

            ComputeNativeWeaponDamageRange(
                input.DamageLevel,
                input.DamageBonus,
                input.DamageMod,
                input.WeaponDamageF32,
                input.WeaponVolatilityF32,
                out int minDamage,
                out int maxDamage);
            result.MinDamageF32 = minDamage;
            result.MaxDamageF32 = maxDamage;

            result.DamageRaw = input.Rng.Generate();
            int damage = RollDamageRange(minDamage, maxDamage, result.DamageRaw);

            if (input.CritThreshold > 0 && result.HitRoll < input.CritThreshold)
            {
                result.IsCritical = true;
                int critPercent = input.CritDamagePercent > 0 ? input.CritDamagePercent : 200;
                damage = Math.Max(0x100, (damage * critPercent) / 100);
            }

            result.DamageF32 = damage;
            result.DamageWire = (uint)Math.Max(1, damage);
            result.Type = result.IsCritical ? AttackResultType.Critical : AttackResultType.Hit;
            result.ResultName = result.IsCritical ? "CRIT" : "HIT";
            result.RoomRngAfter = input.Rng.CallsSinceReseed;
            return result;
        }

        public static int ResolveNativeMeleeDamageBonus(PlayerState state)
        {
            int strength = Math.Max(0, state?.Strength ?? 10);
            var gc = GCDatabase.Instance;
            float meleeDmgPerStr = gc.GetKnob("MeleeDamagePerStrength", 2.3364f);
            int bonus = Mathf.FloorToInt(meleeDmgPerStr * strength);
            return Math.Max(0, Math.Min(0xFFFF, bonus));
        }

        public static bool IsNativeRangedWeapon(PlayerState state)
        {
            if (state == null) return false;
            string weaponClass = state.WeaponClass ?? string.Empty;
            return ContainsIgnoreCase(weaponClass, "RANGED") ||
                   ContainsIgnoreCase(weaponClass, "BOW") ||
                   ContainsIgnoreCase(weaponClass, "CROSSBOW") ||
                   state.WeaponRange > 16;
        }

        public static string ResolveNativeWeaponStatSource(PlayerState state)
        {
            return IsNativeRangedWeapon(state) ? "UnitCache/RangedDamagePerAgility" : "UnitCache/MeleeDamagePerStrength";
        }

        public static float ResolveNativeWeaponAttackSpeedPct(PlayerState state)
        {
            if (state == null) return 0f;
            return IsNativeRangedWeapon(state) ? state.RangeAttackSpeedModPercent : state.MeleeAttackSpeedModPercent;
        }

        public static int ApplyNativeAttackSpeedPctToTicks(int ticks, float pct)
        {
            if (ticks <= 0 || Math.Abs(pct) < 0.0001f) return Math.Max(0, ticks);
            double scale = 1.0d + (pct / 100.0d);
            if (scale < 0.05d) scale = 0.05d;
            int adjusted = (int)Math.Floor((ticks / scale) + 0.5d);
            return Math.Max(1, adjusted);
        }

        public static int ResolveNativeBasicAttackCooldownTicks(PlayerState state)
        {
            int ticks = 0;
            float cooldown = state != null ? state.WeaponCooldown : 0f;
            if (cooldown > 0f)
                ticks = RoundPositiveToInt(cooldown * 30f);

            if (ticks <= 0)
            {
                ticks = 30;
                float weaponSpeed = state != null && state.WeaponSpeed > 0f ? state.WeaponSpeed : 100f;
                int adjusted = RoundPositiveToInt(ticks * (weaponSpeed / 100f));
                if (adjusted > 0) ticks = adjusted;
            }

            ticks = ApplyNativeAttackSpeedPctToTicks(ticks, ResolveNativeWeaponAttackSpeedPct(state));
            return Math.Max(1, ticks);
        }

        private static int RoundPositiveToInt(float value)
        {
            if (value <= 0f) return 0;
            return (int)Math.Floor(value + 0.5f);
        }

        public static int ResolveNativeWeaponDamageBonus(PlayerState state)
        {
            if (state == null) return 0;
            bool ranged = IsNativeRangedWeapon(state);
            int bonus = ResolveNativeUnitBaseDamageBonus(state);
            bonus += ResolveNativeUnitWeaponClassDamageBonus(state, ranged);
            bonus += ResolveNativeUnitWeaponCategoryDamageBonus(state);
            return ClampNativeUShort(bonus);
        }

        private static bool ContainsIgnoreCase(string value, string needle)
        {
            return !string.IsNullOrEmpty(value) &&
                   !string.IsNullOrEmpty(needle) &&
                   value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static int ResolveNativeLevelDamageBonus(PlayerState state)
        {
            return ClampNativeUShort(state != null ? Math.Max(0, state.Level) : 0);
        }

        public static int ResolveNativeDamageMod(PlayerState state)
        {
            bool ranged = IsNativeRangedWeapon(state);
            int damagePct = ResolveNativeUnitBaseDamageModPct(state);
            damagePct += ResolveNativeUnitWeaponClassDamageModPct(state, ranged);
            damagePct += ResolveNativeUnitWeaponCategoryDamageModPct(state);
            int damageModPct = 100 + damagePct;
            if (damageModPct < 0) damageModPct = 0;
            int dpsModifierF32 = FromFloat(GCDatabase.Instance.GetKnob("DPSModifier", 1f));
            damageModPct = (int)(((long)(damageModPct << 8) * dpsModifierF32) >> 16);
            return ClampNativeUShort(damageModPct);
        }

        private static float ResolveNativeClassDamageStatMod(PlayerState state, string descKey, float fallback)
        {
            string classBase = state?.ClassName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(classBase)) return fallback;
            classBase = classBase.Trim();
            if (!classBase.EndsWith("Base", StringComparison.OrdinalIgnoreCase))
                classBase += "Base";

            var node = GCDatabase.Instance.ResolveWithInheritance(classBase) ??
                       GCDatabase.Instance.ResolveWithInheritance($"avatar.classes.{classBase}");
            var desc = node?.GetChild("Description") ?? node;
            if (desc == null) return fallback;
            return desc.GetFloat(descKey, fallback);
        }

        private static int ResolveNativeUnitBaseDamageBonus(PlayerState state)
        {
            return GetEquipmentStat(state, "DAMAGE_BONUS", "DAMAGEBONUS");
        }

        private static int ResolveNativeUnitWeaponClassDamageBonus(PlayerState state, bool ranged)
        {
            int bonus;
            if (ranged)
            {
                int agility = Math.Max(0, state?.Agility ?? 10);
                float rangedDmgPerAgi = GCDatabase.Instance.GetKnob("RangedDamagePerAgility", 2.124f);
                float rangedDmgMod = ResolveNativeClassDamageStatMod(state, "RangedDamagePerAgilityMod", 1f);
                bonus = Mathf.FloorToInt(rangedDmgPerAgi * agility * rangedDmgMod);
                bonus += GetEquipmentStat(state, "RANGE_DAMAGE_BONUS", "RANGED_DAMAGE_BONUS", "RANGEDAMAGEBONUS", "RANGEDDAMAGEBONUS");
            }
            else
            {
                int strength = Math.Max(0, state?.Strength ?? 10);
                float meleeDmgPerStr = GCDatabase.Instance.GetKnob("MeleeDamagePerStrength", 2.3364f);
                float meleeDmgMod = ResolveNativeClassDamageStatMod(state, "MeleeDamagePerStrengthMod", 1f);
                bonus = Mathf.FloorToInt(meleeDmgPerStr * strength * meleeDmgMod);
                bonus += GetEquipmentStat(state, "MELEE_DAMAGE_BONUS", "MELEEDAMAGEBONUS");
            }

            return bonus;
        }

        private static int ResolveNativeUnitWeaponCategoryDamageBonus(PlayerState state)
        {
            string category = CanonicalStatName(state?.WeaponCategory);
            if (string.IsNullOrEmpty(category)) return 0;
            return GetEquipmentStat(state, category + "_DAMAGE_BONUS", category + "DAMAGEBONUS");
        }

        private static int ResolveNativeUnitBaseDamageModPct(PlayerState state)
        {
            return GetEquipmentStat(state, "DAMAGE_MOD", "DAMAGEMOD", "DAMAGE_PCT", "DAMAGEPCT");
        }

        private static int ResolveNativeUnitWeaponClassDamageModPct(PlayerState state, bool ranged)
        {
            if (ranged)
                return GetEquipmentStat(state, "RANGE_DAMAGE_MOD", "RANGED_DAMAGE_MOD", "RANGEDAMAGEMOD", "RANGEDDAMAGEMOD");
            return GetEquipmentStat(state, "MELEE_DAMAGE_MOD", "MELEEDAMAGEMOD");
        }

        private static int ResolveNativeUnitWeaponCategoryDamageModPct(PlayerState state)
        {
            string category = CanonicalStatName(state?.WeaponCategory);
            if (string.IsNullOrEmpty(category)) return 0;
            return GetEquipmentStat(state, category + "_DAMAGE_MOD", category + "DAMAGEMOD", category + "_DAMAGE_PCT", category + "DAMAGEPCT");
        }

        private static int ClampNativeUShort(int value)
        {
            return Math.Max(0, Math.Min(0xFFFF, value));
        }

        private static int GetEquipmentStat(PlayerState state, params string[] names)
        {
            if (state?.EquipmentStats == null || state.EquipmentStats.Count == 0 || names == null) return 0;
            int total = 0;
            foreach (var kvp in state.EquipmentStats)
            {
                string key = CanonicalStatName(kvp.Key);
                for (int i = 0; i < names.Length; i++)
                {
                    if (key == CanonicalStatName(names[i]))
                    {
                        total += kvp.Value;
                        break;
                    }
                }
            }
            return total;
        }

        private static string CanonicalStatName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            return value.Replace("_", string.Empty).Replace(" ", string.Empty).Trim().ToUpperInvariant();
        }

        public static int ResolveNativeCriticalThreshold(PlayerState state, Monster defender)
        {
            int baseCrit = Mathf.FloorToInt(GCDatabase.Instance.GetKnob("HeroCriticalChance", 3f));
            int threshold = baseCrit << 8;
            int attackerLevel = Math.Max(0, state?.Level ?? 1);
            int defenderLevel = Math.Max(0, defender?.Level ?? attackerLevel);
            int levelDelta = attackerLevel - defenderLevel;
            if (levelDelta > 0)
                threshold += levelDelta * 0x500;
            if (threshold > 0x5A00) threshold = 0x5A00;
            if (threshold < 0) threshold = 0;
            return threshold;
        }

        public static int ResolveNativeCriticalDamagePercent(PlayerState state)
        {
            return 200;
        }

        public static int ResolveNativeAvatarAttackRating(PlayerState state)
        {
            if (state == null) return 0;
            float attackPerAgility = GCDatabase.Instance.GetKnob("AttackRatingPerAgility", 14f);
            int baseRating = Math.Max(0, Mathf.RoundToInt(state.Agility * attackPerAgility));
            int modPercent = IsNativeRangedWeapon(state) ? 0 : Math.Max(-100, state.MeleeAttackRatingModPercent);
            return Math.Max(0, (baseRating * (100 + modPercent)) / 100);
        }

        public static int ResolveNativeMonsterDefenseRating(Monster monster)
        {
            return ResolveNativeMonsterCurveRating("MonsterDefenseRating", monster);
        }

        public static int ResolveNativeMonsterAttackRating(Monster monster)
        {
            return ResolveNativeMonsterCurveRating("MonsterAttackRating", monster, true);
        }

        private static int ResolveNativeMonsterCurveRating(string curveName, Monster monster, bool attack = false)
        {
            if (monster == null) return 0;
            float authored = attack ? monster.AttackRating : monster.DefenseRating;
            int authoredF32 = FromFloat(Mathf.Max(0f, authored));
            if (authoredF32 <= 0) return 0;
            int fallbackF32 = 0;
            if (string.Equals(curveName, "MonsterAttackRating", StringComparison.OrdinalIgnoreCase))
                fallbackF32 = FromFloat(100f + ((Mathf.Clamp(monster.Level, 1, 110) - 1f) * (32800f - 100f) / 109f));
            else if (string.Equals(curveName, "MonsterDefenseRating", StringComparison.OrdinalIgnoreCase))
                fallbackF32 = FromFloat(Mathf.Clamp(monster.Level, 1, 110) <= 15f
                    ? 35f + ((Mathf.Clamp(monster.Level, 1, 110) - 1f) * (287f - 35f) / 14f)
                    : 287f + ((Mathf.Clamp(monster.Level, 1, 110) - 15f) * (3087f - 287f) / 95f));
            int tableF32 = GCDatabase.Instance.GetCurveValueFixed32(curveName, Mathf.Clamp(monster.Level, 1, 110), fallbackF32);
            int rating = (int)(((long)authoredF32 * tableF32) >> 16);
            return (ushort)rating;
        }

        public static int ResolveNativeHitThreshold(int attackRating, int defenseRating, int attackerLevel, int defenderLevel)
        {
            int attack = Math.Max(0, attackRating);
            int defense = Math.Max(0, defenseRating);
            int chancePercent = attack + defense == 0 ? 0 : (attack * 100) / (attack + defense);
            int threshold = chancePercent << 8;
            int levelDelta = Math.Max(0, Math.Min(110, defenderLevel)) - Math.Max(0, Math.Min(110, attackerLevel));
            threshold -= levelDelta * 0x500;
            if (threshold < 0x0A00) threshold = 0x0A00;
            return threshold;
        }

        public static void ComputeNativeWeaponDamageRange(int damageLevel, int damageBonus, int damageMod, int weaponDamageF32, int volatilityF32, out int minDamage, out int maxDamage)
        {
            if (damageLevel < 0) damageLevel = 0;
            if (damageBonus < 0) damageBonus = 0;
            if (damageMod < 0) damageMod = 0;
            if (weaponDamageF32 <= 0) weaponDamageF32 = 0x100;
            if (volatilityF32 < 0) volatilityF32 = 0;

            int normalized = FixedMul((damageLevel + damageBonus) << 8, weaponDamageF32);
            normalized = (int)(((long)normalized * ((long)damageMod << 8)) / 0x6400L);
            if (normalized < 0x100) normalized = 0x100;

            int spread = FixedMul(normalized, volatilityF32);
            minDamage = RoundFixed32(normalized - spread);
            maxDamage = RoundFixed32(normalized + spread);
            if (minDamage < 0x100) minDamage = 0x100;
            if (maxDamage < 0x100) maxDamage = 0x100;
            minDamage = (minDamage >> 8) << 8;
            maxDamage = (maxDamage >> 8) << 8;
            if (maxDamage < minDamage) maxDamage = minDamage;
        }

        // ═══════════════════════════════════════════════════════════════
        // MAIN ENTRY: Process one attack using shared room RNG
        // Binary: applyDamage at 0x597E50
        // Uses entity+0x88 -> entityManager+0x44 RNG (room-level MT19937)
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Run one attack through the binary-verified RNG consumption pattern.
        /// Consumes 2 room RNG for miss/block, 3 room RNG for hit.
        /// Weapon::playAttackSound uses the native global sound RNG, not this room RNG.
        /// </summary>
        public static AttackResult ProcessAttack(
            MersenneTwister rng,
            int attackerLevel,
            int defenderLevel,
            int weaponBaseDamageF32, // Fixed32 — weapon Damage multiplier (1.0 = 0x100)
            int weaponVolatilityF32, // Fixed32 — weapon DamageVolatility (0.5 = 0x80)
            int defenseRating,       // defender — computeDefenseRating result
            int blockChance)         // defender — 0 for monsters without shields
        {
            var result = new AttackResult();

            // ──── Global sound RNG at 0x598670: playAttackSound repeat gate ────
            // Native uses Random::generator @ 0x00932FF8, so this must not advance room RNG.
            uint procRaw = NativeRandomStreams.GenerateGlobalSound();
            result.ProcRoll = (int)(procRaw & 0x03);
            result.ProcTriggered = result.ProcRoll == 0;

            // ──── RNG #1 at 0x59804B: Hit chance roll ────
            uint hitRaw = rng.Generate();
            result.HitRoll = (int)(hitRaw % 25700);

            // ──── RNG #2 at 0x598133: Block chance roll ────
            uint blockRaw = rng.Generate();
            result.BlockRoll = (int)(((blockRaw >> 8) & 0xFF) % 100) + 1;

            int attackRating = Mathf.RoundToInt(GCDatabase.Instance.GetKnob("AttackRatingPerAgility", 14f) * 10f);
            int hitChanceF32 = ResolveNativeHitThreshold(attackRating, defenseRating, attackerLevel, defenderLevel);

            // ──── Miss check at 0x598163 ────
            if (result.HitRoll >= hitChanceF32)
            {
                result.Type = AttackResultType.Miss;
                result.RngConsumed = 2;
                Debug.LogError($"[DMG] MISS: hitRoll={result.HitRoll} >= cap={hitChanceF32} proc={result.ProcRoll} (2 room RNG consumed)");
                return result;
            }

            // ──── Block check at 0x5982D9 ────
            // Client (Weapon::applyDamage @ 0x598170): if (blockChance <= blockRoll) proceed to damage.
            // I.e. client BLOCKS when blockChance > blockRoll (strict). At the equality boundary the
            // client does NOT block (proceeds to damage). Use strict < to match — previous <=
            // inverted behaviour at blockRoll == blockChance.
            if (result.BlockRoll < blockChance)
            {
                result.Type = AttackResultType.Block;
                result.RngConsumed = 2;
                Debug.LogError($"[DMG] BLOCK: blockRoll={result.BlockRoll} < chance={blockChance} proc={result.ProcRoll} (2 room RNG consumed)");
                return result;
            }

            // ──── HIT: Compute damage from GC values ────
            ComputeDamageRange(
                attackerLevel, defenderLevel,
                weaponBaseDamageF32, weaponVolatilityF32,
                out int minDmg, out int maxDmg);

            // Binary at 0x598FD0: floor to integer part before range calc
            minDmg = (minDmg >> 8) << 8;
            maxDmg = (maxDmg >> 8) << 8;

            // ──── RNG #3 at 0x599011: Damage roll ────
            uint damageRaw = rng.Generate();
            int damage = RollDamageRange(minDmg, maxDmg, damageRaw);

            // Crit check: binary compares hitRoll to critThreshold (no extra RNG)
            // GlobalKnobs: HeroCriticalChance = 3
            // TODO: implement crit threshold from attacker stats
            bool isCrit = false;

            result.Type = isCrit ? AttackResultType.Critical : AttackResultType.Hit;
            result.DamageF32 = damage;
            result.MinDamageF32 = minDmg;
            result.MaxDamageF32 = maxDmg;
            result.RngConsumed = 3;

            Debug.LogError($"[DMG] HIT: dmg={damage} ({damage / 256}hp) range=[{minDmg / 256},{maxDmg / 256}] hitRoll={result.HitRoll} proc={result.ProcRoll} (3 room RNG consumed)");
            return result;
        }

        // ═══════════════════════════════════════════════════════════════
        // computeDamageRange — Uses GlobalKnobs.gc values
        //
        // Formula from GC files:
        //   baseDamage = (WeaponDamagePerLevel * level) + (MeleeDamagePerStrength * Strength)
        //   scaled = baseDamage * weaponDamageMultiplier
        //   Apply DamageVolatility for spread
        //
        // GlobalKnobs.gc values:
        //   WeaponDamagePerLevel = 10
        //   MeleeDamagePerStrength = 2.3364
        // FighterBase.gc:
        //   Strength = 10 (base, no level scaling for now)
        // ═══════════════════════════════════════════════════════════════

        private static void ComputeDamageRange(
            int attackerLevel, int defenderLevel,
            int weaponBaseDamageF32, int weaponVolatilityF32,
            out int minDamage, out int maxDamage)
        {
            // Pull values from GC database — same source as client binary
            var gc = GCDatabase.Instance;
            float weaponDmgPerLevel = gc.GetKnob("WeaponDamagePerLevel", 10f);
            float meleeDmgPerStr = gc.GetKnob("MeleeDamagePerStrength", 2.3364f);

            // Player base strength from FighterBase.gc
            float playerStrength = 10f;
            var fighterBase = gc.GetNode("FighterBase");
            if (fighterBase != null)
            {
                var desc = fighterBase.GetChild("Description");
                if (desc != null)
                    playerStrength = desc.GetFloat("Strength", 10f);
            }

            // Formula: baseDamage = (WDpL × level) + (MeleeDmgPerStr × Strength)
            //          scaled = baseDamage × weaponMultiplier
            float levelComponent = weaponDmgPerLevel * attackerLevel;
            float strengthComponent = meleeDmgPerStr * playerStrength;
            float baseDamage = levelComponent + strengthComponent;

            int baseDmgF32 = FromFloat(baseDamage);
            int normalized = FixedMul(baseDmgF32, weaponBaseDamageF32);

            if (normalized < 0x100) normalized = 0x100;

            // DamageVolatility from weapon GC
            int spread = FixedMul(normalized, weaponVolatilityF32);

            minDamage = normalized - spread;
            maxDamage = normalized + spread;

            minDamage = RoundFixed32(minDamage);
            maxDamage = RoundFixed32(maxDamage);
            if (minDamage < 0x100) minDamage = 0x100;
            if (maxDamage < 0x100) maxDamage = 0x100;

            Debug.LogError($"[DMG] DamageRange: level={attackerLevel} str={playerStrength} base={baseDamage:F1} (lvl={levelComponent:F1}+str={strengthComponent:F1}) wpnMult={ToFloat(weaponBaseDamageF32):F2} vol={ToFloat(weaponVolatilityF32):F2} → [{minDamage / 256},{maxDamage / 256}]");
        }

        /// <summary>
        /// Get weapon damage multiplier from PlayerState.WeaponDamage (loaded from weapons.json).
        /// This is a MULTIPLIER (e.g. 1.0), not absolute damage.
        /// The actual damage comes from: (WeaponDamagePerLevel * level + MeleeDamagePerStrength * str) * this
        /// </summary>
        public static int GetWeaponBaseDamageF32(PlayerState state)
        {
            // WeaponDamage loaded from weapons.json at spawn/pickup (e.g. 1.0 for base sword)
            int f32 = NativeFixed32FromAuthoredDecimal(state.WeaponDamage);
            Debug.LogError($"[DMG] Weapon multiplier: {state.WeaponDamage} (F32: 0x{f32:X})");
            return f32;
        }

        /// <summary>
        /// Get weapon volatility from GC data.
        /// MeleeUnitWeapon.gc: DamageVolatility = 0.5
        /// Base1HMelee.gc: inherits from MeleeWeapon (DamageVolatility not set → use default 0.5)
        /// </summary>
        public static int GetWeaponVolatilityF32(PlayerState state)
        {
            float vol = state != null ? state.WeaponDamageVolatility : 0.5f;
            vol = Mathf.Clamp(vol, 0f, 0.95f);
            return NativeFixed32FromAuthoredDecimal(vol);
        }

        public static int NativeFixed32FromAuthoredDecimal(float value)
        {
            return Mathf.CeilToInt(value * 256f);
        }

        // ═══════════════════════════════════════════════════════════════
        // SPELL DAMAGE — Uses SkillDamagePerLevel + SkillDamagePerIntellect
        //
        // Formula from GC files (mirrors melee but different knobs):
        //   baseDamage = (SkillDamagePerLevel(15) × level) + (SkillDamagePerIntellect(1.5) × Intellect)
        //   scaled = baseDamage × spell.DamageMod
        //   spread = scaled × spell.DamageVolatility
        //   damage = RNG in [scaled - spread, scaled + spread]
        //
        // Spells fire INSTANTLY on UseTarget (no weapon cycle tick delay).
        // Client sends 0x50 with flags=101 for spells vs flags=10 for melee.
        // ═══════════════════════════════════════════════════════════════

        // GC Avatar base knobs for spell damage — wired to server.cfg
        public static float GC_SKILL_DAMAGE_PER_LEVEL => GCDatabase.Instance.GetKnob("SkillDamagePerLevel", 15f);
        public static float GC_SKILL_DAMAGE_PER_INTELLECT => GCDatabase.Instance.GetKnob("SkillDamagePerIntellect", 1.5f);

        /// <summary>
        /// Compute spell damage range using GC formula.
        /// Same Fixed32 math as melee but with spell-specific knobs.
        /// </summary>
        public static void ComputeSpellDamageRange(
            int attackerLevel,
            int attackerIntellect,
            float spellDamageMod,
            float spellDamageVolatility,
            out int minDamage, out int maxDamage)
        {
            // Formula: baseDamage = (SkillDmgPerLevel × level) + (SkillDmgPerIntellect × Intellect)
            float levelComponent = GC_SKILL_DAMAGE_PER_LEVEL * attackerLevel;
            float intellectComponent = GC_SKILL_DAMAGE_PER_INTELLECT * attackerIntellect;
            float baseDamage = levelComponent + intellectComponent;

            int baseDmgF32 = FromFloat(baseDamage);
            int spellModF32 = FromFloat(spellDamageMod);
            int normalized = FixedMul(baseDmgF32, spellModF32);

            if (normalized < 0x100) normalized = 0x100;

            int volF32 = FromFloat(spellDamageVolatility);
            int spread = FixedMul(normalized, volF32);

            minDamage = normalized - spread;
            maxDamage = normalized + spread;

            minDamage = RoundFixed32(minDamage);
            maxDamage = RoundFixed32(maxDamage);
            if (minDamage < 0x100) minDamage = 0x100;
            if (maxDamage < 0x100) maxDamage = 0x100;

            // Floor to integer part before range calc (same as melee at 0x598FD0)
            minDamage = (minDamage >> 8) << 8;
            maxDamage = (maxDamage >> 8) << 8;

            Debug.LogError($"[SPELL-DMG] DamageRange: level={attackerLevel} int={attackerIntellect} base={baseDamage:F1} (lvl={levelComponent:F1}+int={intellectComponent:F1}) spellMod={spellDamageMod:F2} vol={spellDamageVolatility:F2} → [{minDamage / 256},{maxDamage / 256}]");
        }

        // ═══════════════════════════════════════════════════════════════
        // RANGED DAMAGE — Uses RangedDamagePerAgility (Ranger skills)
        //
        // Formula from GC:
        //   baseDamage = (WeaponDamagePerLevel(10) × level) + (RangedDamagePerAgility(2.124) × Agility)
        //   scaled = baseDamage × spell.DamageMod
        // ═══════════════════════════════════════════════════════════════
        public static float GC_RANGED_DAMAGE_PER_AGILITY => GCDatabase.Instance.GetKnob("RangedDamagePerAgility", 2.124f);

        public static void ComputeRangedDamageRange(
            int attackerLevel,
            int attackerAgility,
            float weaponDamageMultiplier,
            float spellDamageMod,
            float spellDamageVolatility,
            out int minDamage, out int maxDamage)
        {
            // Ranger skills use weapon base + agility scaling, then spell modifier on top
            float levelComponent = GCDatabase.Instance.GetKnob("WeaponDamagePerLevel", 10f) * attackerLevel; // WeaponDamagePerLevel
            float agilityComponent = GC_RANGED_DAMAGE_PER_AGILITY * attackerAgility;
            float baseDamage = levelComponent + agilityComponent;

            int baseDmgF32 = FromFloat(baseDamage);
            int weaponF32 = FromFloat(weaponDamageMultiplier);
            int weaponScaled = FixedMul(baseDmgF32, weaponF32);

            // Then apply spell's DamageMod on top of weapon damage
            int spellModF32 = FromFloat(spellDamageMod);
            int normalized = FixedMul(weaponScaled, spellModF32);
            if (normalized < 0x100) normalized = 0x100;

            int volF32 = FromFloat(spellDamageVolatility);
            int spread = FixedMul(normalized, volF32);

            minDamage = normalized - spread;
            maxDamage = normalized + spread;
            minDamage = RoundFixed32(minDamage);
            maxDamage = RoundFixed32(maxDamage);
            if (minDamage < 0x100) minDamage = 0x100;
            if (maxDamage < 0x100) maxDamage = 0x100;
            minDamage = (minDamage >> 8) << 8;
            maxDamage = (maxDamage >> 8) << 8;

            Debug.LogError($"[RANGED-DMG] DamageRange: level={attackerLevel} agi={attackerAgility} base={baseDamage:F1} wpn={weaponDamageMultiplier:F2} spellMod={spellDamageMod:F2} vol={spellDamageVolatility:F2} → [{minDamage / 256},{maxDamage / 256}]");
        }

        // ═══════════════════════════════════════════════════════════════
        // WEAPON SKILL DAMAGE — For Butcher/Cleave (SpellWeaponDamageEffect)
        //
        // Uses normal melee damage, then applies skill-level modifier:
        //   weaponDamage = (WeaponDamagePerLevel × level + MeleeDamagePerStrength × STR) × weaponMult
        //   skillMod = (DamageModMin + skillLevel × DamageModInc) / 100
        //   finalDamage = weaponDamage × skillMod
        // ═══════════════════════════════════════════════════════════════

        public static void ComputeWeaponSkillDamageRange(
            int attackerLevel,
            int attackerStrength,
            float weaponDamageMultiplier,
            float weaponVolatility,
            int skillLevel,
            int damageModMin, int damageModMax, int damageModInc,
            out int minDamage, out int maxDamage)
        {
            // Base melee damage (same as WeaponCycleTracker)
            float levelComponent = GCDatabase.Instance.GetKnob("WeaponDamagePerLevel", 10f) * attackerLevel;
            float strengthComponent = GCDatabase.Instance.GetKnob("MeleeDamagePerStrength", 2.3364f) * attackerStrength;
            float baseDamage = levelComponent + strengthComponent;

            int baseDmgF32 = FromFloat(baseDamage);
            int weaponF32 = FromFloat(weaponDamageMultiplier);
            int normalized = FixedMul(baseDmgF32, weaponF32);
            if (normalized < 0x100) normalized = 0x100;

            // Apply skill-level modifier
            int rawMod = damageModMin + (skillLevel * damageModInc);
            if (damageModMax > 0 && rawMod > damageModMax) rawMod = damageModMax;
            float skillModifier = (100f + rawMod) / 100f; // DamageModMin=-60 at level 1 = 40% damage
            int skillModF32 = FromFloat(skillModifier);
            normalized = FixedMul(normalized, skillModF32);
            if (normalized < 0x100) normalized = 0x100;

            int volF32 = FromFloat(weaponVolatility);
            int spread = FixedMul(normalized, volF32);

            minDamage = normalized - spread;
            maxDamage = normalized + spread;
            minDamage = RoundFixed32(minDamage);
            maxDamage = RoundFixed32(maxDamage);
            if (minDamage < 0x100) minDamage = 0x100;
            if (maxDamage < 0x100) maxDamage = 0x100;
            minDamage = (minDamage >> 8) << 8;
            maxDamage = (maxDamage >> 8) << 8;

            Debug.LogError($"[WPNSKILL-DMG] DamageRange: level={attackerLevel} str={attackerStrength} wpn={weaponDamageMultiplier:F2} skillLvl={skillLevel} mod={rawMod}({skillModifier:F2}x) → [{minDamage / 256},{maxDamage / 256}]");
        }

        /// <summary>
        /// Process one spell attack with binary-proven RNG consumption pattern.
        ///
        /// PRIMARY target (isChainTarget=false): 1-3 Generate() calls
        ///   Optional #1: SpellEffect::CheckChance RNG only when Chance < 0x6400 (VA 0x545FF0)
        ///   #2: Damage roll — rng % range + min (virtualA0 +0xC8, VA 0x54FDE8)
        ///   #3: Crit roll — rng % 25700 vs threshold (virtualA0 +0x270, VA 0x54FF90)
        ///       ONLY if spell.CriticalChance > 0; skipped otherwise
        ///
        /// CHAIN target (isChainTarget=true): 2 Generate() calls
        ///   Chain dispatch (SpellChainEffect 0x54AC70) calls virtualA0 DIRECTLY,
        ///   NOT through doEffect — so NO hit roll Generate.
        ///   #1: Damage roll
        ///   #2: Crit roll (if CriticalChance > 0)
        ///
        /// Shadow Lightning total: 3 + (5 × 2) = 13 calls per cast.
        /// </summary>
        public static SpellAttackResult ProcessSpellAttack(
            MersenneTwister rng,
            int attackerLevel,
            int attackerIntellect,
            int attackerAgility,
            int attackerStrength,
            float weaponDamageMultiplier,
            float weaponVolatility,
            SpellData spell,
            Monster target,
            int skillLevel = 1,
            bool isChainTarget = false)
        {
            var result = new SpellAttackResult();
            result.Spell = spell;
            result.HitRoll = -1;

            if (spell == null || !spell.HasDirectDamageEffect)
            {
                result.Type = AttackResultType.Miss;
                Debug.LogError($"[SPELL-DMG] NO-DIRECT-DAMAGE: {spell?.DisplayName ?? "unknown"} directDamage=False modifierDamage={spell?.HasProjectileModifierDamage ?? false}");
                return result;
            }

            // Binary: SpellEffect::CheckChance only consumes RNG when Chance < 0x6400.
            // Chain targets dispatched via SpellChainEffect -> virtualA0 directly, skipping doEffect.
            int chanceF32 = Math.Min(0x6400, Math.Max(0, spell.ChanceF32));
            bool consumedChanceRng = false;
            if (!isChainTarget && chanceF32 < 0x6400)
            {
                uint hitRaw = rng.Generate();
                consumedChanceRng = true;
                result.HitRoll = (int)(hitRaw % 25700);

                if (result.HitRoll >= chanceF32)
                {
                    // Miss — still need to consume crit RNG if CriticalChance > 0?
                    // NO: binary shows virtualA0 is only called on hit (doEffect checks hit first)
                    result.Type = AttackResultType.Miss;
                    Debug.LogError($"[SPELL-DMG] MISS: {spell.DisplayName} hitRoll={result.HitRoll} >= chance={chanceF32} (1 RNG consumed)");
                    return result;
                }
            }

            // ──── Compute damage range based on formula type (no RNG) ────
            int minDmg, maxDmg;

            if (spell.IsWeaponSkill)
            {
                ComputeWeaponSkillDamageRange(
                    attackerLevel, attackerStrength,
                    weaponDamageMultiplier, weaponVolatility,
                    skillLevel,
                    spell.SkillDamageModMin, spell.SkillDamageModMax, spell.SkillDamageModInc,
                    out minDmg, out maxDmg);
            }
            else if (spell.AttackType == AttackType.RANGED)
            {
                ComputeRangedDamageRange(
                    attackerLevel, attackerAgility,
                    weaponDamageMultiplier,
                    spell.DamageMod, spell.DamageVolatility,
                    out minDmg, out maxDmg);
            }
            else
            {
                ComputeSpellDamageRange(
                    attackerLevel, attackerIntellect,
                    spell.DamageMod, spell.DamageVolatility,
                    out minDmg, out maxDmg);
            }

            // ──── Generate #2: Damage roll (virtualA0 +0xC8, VA 0x54FDE8) — ALWAYS ────
            uint damageRaw = rng.Generate();
            int damage = spell.IsWeaponSkill
                ? RollDamageRange(minDmg, maxDmg, damageRaw)
                : RollSpellDamageRange(minDmg, maxDmg, damageRaw);

            // ──── Resist check based on damage element (no RNG) ────
            float resist = GetMonsterResist(target, spell.DamageType);
            if (resist > 0)
            {
                int resistReduction = FixedMul(damage, FromFloat(resist / 100f));
                damage -= resistReduction;
                if (damage < 0x100) damage = 0x100;
                Debug.LogError($"[SPELL-DMG] Resist: {spell.DamageType} {resist}% reduces by {resistReduction / 256}");
            }

            // ──── Generate #3: Crit roll (virtualA0 +0x270, VA 0x54FF90) ────
            // SEPARATE Generate() — NOT reusing hitRoll!
            // Binary: cmp [ebx+0x74], 0; jle skip — ONLY when CriticalChance > 0
            // Threshold: unit stats + spell.CriticalChance via Fixed32, capped at 0x5A00 (90%)
            // On crit: damage *= [unit+0x118]/100, sets flag [edi+0x41] |= 0x01
            bool isCrit = false;
            if (spell.CriticalChance > 0)
            {
                uint critRaw = rng.Generate();
                int critRoll = (int)(critRaw % 25700);

                // Threshold from spell.CriticalChance (0.25 = 25%)
                // Binary scale: 25600 (0x6400) = 100%
                // TODO: add unit stats [esi+0x10C] + [esi+0x170] when attacker stats are tracked
                int critThreshold = (int)(spell.CriticalChance * 25600);
                if (critThreshold > 23040) critThreshold = 23040; // Cap at 90% (0x5A00)

                if (critRoll < critThreshold)
                {
                    isCrit = true;
                    damage = FixedMul(damage, FromFloat(1.5f)); // 150% crit multiplier
                }
                Debug.LogError($"[SPELL-DMG] CritRoll: {critRoll} vs threshold={critThreshold} ({spell.CriticalChance * 100:F0}%) → {(isCrit ? "CRIT" : "no crit")}");
            }

            result.Type = isCrit ? AttackResultType.Critical : AttackResultType.Hit;
            result.DamageF32 = damage;
            result.MinDamageF32 = minDmg;
            result.MaxDamageF32 = maxDmg;
            result.DamageRaw = damageRaw;

            int rngCalls = (consumedChanceRng ? 1 : 0) + 1 + (spell.CriticalChance > 0 ? 1 : 0);
            string formulaTag = spell.IsWeaponSkill ? "WPNSKILL" : spell.AttackType == AttackType.RANGED ? "RANGED" : "MAGIC";
            string rollTag = spell.IsWeaponSkill ? "weapon-fixed" : "spell-hp";
            Debug.LogError($"[SPELL-DMG] {(isCrit ? "CRIT" : "HIT")}: {spell.DisplayName} [{formulaTag}] {(isChainTarget ? "CHAIN" : "PRIMARY")} dmg={damage / 256} ({damage}) range=[{minDmg / 256},{maxDmg / 256}] dmgRaw=0x{damageRaw:X8} roll={rollTag} hitRoll={result.HitRoll} ({rngCalls} RNG consumed)");
            return result;
        }

        public static SpellAttackResult ProcessProjectileModifierTick(
            MersenneTwister rng,
            int attackerLevel,
            int attackerIntellect,
            int attackerAgility,
            int attackerStrength,
            float weaponDamageMultiplier,
            float weaponVolatility,
            SpellData spell,
            Monster target,
            int skillLevel = 1,
            int criticalDamagePercent = 200)
        {
            var result = new SpellAttackResult
            {
                Spell = spell,
                HitRoll = -1
            };

            if (rng == null || spell == null || target == null)
            {
                result.Type = AttackResultType.Miss;
                return result;
            }

            float damageMod = spell.ProjectileModifierDamageMod > 0f
                ? spell.ProjectileModifierDamageMod
                : spell.DamageMod;
            float damageVolatility = spell.ProjectileModifierDamageVolatility > 0f
                ? spell.ProjectileModifierDamageVolatility
                : spell.DamageVolatility;
            float critChance = spell.ProjectileModifierCriticalChance;
            AttackType modifierAttackType = spell.EffectiveProjectileModifierAttackType;
            DamageElement modifierDamageType = spell.EffectiveProjectileModifierDamageType;

            int minDmg;
            int maxDmg;
            if (modifierAttackType == AttackType.RANGED)
            {
                ComputeRangedDamageRange(
                    attackerLevel,
                    attackerAgility,
                    weaponDamageMultiplier,
                    damageMod,
                    damageVolatility,
                    out minDmg,
                    out maxDmg);
            }
            else
            {
                ComputeSpellDamageRange(
                    attackerLevel,
                    attackerIntellect,
                    damageMod,
                    damageVolatility,
                    out minDmg,
                    out maxDmg);
            }

            uint damageRaw = rng.Generate();
            int damage = RollSpellDamageRange(minDmg, maxDmg, damageRaw);

            float resist = GetMonsterResist(target, modifierDamageType);
            if (resist > 0)
            {
                int resistReduction = FixedMul(damage, FromFloat(resist / 100f));
                damage -= resistReduction;
                if (damage < 0x100) damage = 0x100;
                Debug.LogError($"[SPELL-MOD-DMG] Resist: {modifierDamageType} {resist}% reduces by {resistReduction / 256}");
            }

            bool isCrit = false;
            if (critChance > 0f)
            {
                uint critRaw = rng.Generate();
                int critRoll = (int)(critRaw % 25700);
                int critThreshold = (int)(critChance * 25600);
                if (critThreshold > 23040) critThreshold = 23040;
                if (critRoll < critThreshold)
                {
                    isCrit = true;
                    int critPercent = Math.Max(100, criticalDamagePercent);
                    damage = (int)(((long)damage * critPercent) / 100L);
                }
                Debug.LogError($"[SPELL-MOD-DMG] CritRoll: {critRoll} vs threshold={critThreshold} ({critChance * 100:F0}%) critPct={Math.Max(100, criticalDamagePercent)} -> {(isCrit ? "CRIT" : "no crit")}");
            }

            result.Type = isCrit ? AttackResultType.Critical : AttackResultType.Hit;
            result.DamageF32 = damage;
            result.MinDamageF32 = minDmg;
            result.MaxDamageF32 = maxDmg;
            result.DamageRaw = damageRaw;
            result.CritDamagePercent = Math.Max(100, criticalDamagePercent);

            Debug.LogError($"[SPELL-MOD-DMG] {(isCrit ? "CRIT" : "HIT")}: {spell.DisplayName} modifier [{spell.ProjectileModifierEffectId ?? "modifier"}] atk={modifierAttackType} dmgType={modifierDamageType} dmg={damage / 256} ({damage}) range=[{minDmg / 256},{maxDmg / 256}] dmgRaw=0x{damageRaw:X8} mod={damageMod:F2} vol={damageVolatility:F2} critPct={Math.Max(100, criticalDamagePercent)}");
            return result;
        }

        /// <summary>Get monster's resist value for a given damage element.</summary>
        private static float GetMonsterResist(Monster monster, DamageElement element)
        {
            return element switch
            {
                DamageElement.DIVINE => monster.DivineResist,
                DamageElement.FIRE => monster.FireResist,
                DamageElement.ICE => monster.IceResist,
                DamageElement.POISON => monster.PoisonResist,
                DamageElement.SHADOW => monster.ShadowResist,
                _ => 0f
            };
        }
    }

    public class SpellAttackResult
    {
        public AttackResultType Type;
        public SpellData Spell;
        public int DamageF32;
        public int MinDamageF32;
        public int MaxDamageF32;
        public uint DamageRaw;
        public int HitRoll;
        public int CritDamagePercent;
    }

    public class NativeWeaponDamageInput
    {
        public MersenneTwister Rng;
        public int AttackerLevel;
        public int DefenderLevel;
        public int AttackRating;
        public int DefenseRating;
        public int BlockChance;
        public int DamageLevel;
        public int DamageBonus;
        public int DamageMod;
        public int WeaponDamageF32;
        public int WeaponVolatilityF32;
        public int CritThreshold;
        public int CritDamagePercent;
        public string Source;
    }

    public class NativeWeaponDamageResult
    {
        public AttackResultType Type = AttackResultType.Miss;
        public string ResultName = "MISS";
        public uint HitRaw;
        public uint BlockRaw;
        public uint DamageRaw;
        public int HitRoll;
        public int BlockRoll;
        public int HitThreshold;
        public int AttackRating;
        public int DefenseRating;
        public int AttackerLevel;
        public int DefenderLevel;
        public int BlockChance;
        public int DamageLevel;
        public int DamageBonus;
        public int DamageMod;
        public int WeaponDamageF32;
        public int WeaponVolatilityF32;
        public int MinDamageF32;
        public int MaxDamageF32;
        public int DamageF32;
        public uint DamageWire;
        public int CritThreshold;
        public int CritDamagePercent;
        public bool IsHit;
        public bool IsBlocked;
        public bool IsCritical;
        public int RoomRngAfter;
    }

    public class AttackResult
    {
        public AttackResultType Type;
        public int DamageF32;      // Fixed32 wire format damage
        public int MinDamageF32;
        public int MaxDamageF32;
        public int HitRoll;
        public int BlockRoll;
        public int ProcRoll;       // Global sound repeat gate at 0x598670; repeat when zero
        public bool ProcTriggered;
        public int RngConsumed;    // Room RNG only: 2 for miss/block, 3 for hit
    }

    public enum AttackResultType
    {
        Miss,
        Block,
        Hit,
        Critical
    }
}
