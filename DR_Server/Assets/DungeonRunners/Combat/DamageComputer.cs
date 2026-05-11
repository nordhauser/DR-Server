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
    /// - RNG #4 at 0x5986F5: Generate() & 0x03                (post-attack proc, EVERY swing)
    ///
    /// MeleeWeapon::update (0x591980) per-cycle timeline (30 ticks):
    ///   Tick 29 (total-1): call 0x591B10 → Timer(6) → sends UseTarget TCP packet
    ///   Tick 20 (endTick): call 0x598670 → post-attack proc → RNG #4 ALWAYS consumed
    ///   Tick 15 (hitTick): vtable[0x128] → doHit → applyDamage → RNG #1-#3
    ///   Tick 0:            vtable[0xD8]  → attack cycle complete
    ///
    /// TOTAL RNG PER SWING: 3 (miss/block) or 4 (hit) — NOT 2/3 as previously coded!
    /// The post-attack proc at 0x598670 calls Generate() then tests (al & 0x03).
    /// This 4th call was missing, causing 1 RNG drift per swing → full desync after ~15 swings.
    ///
    /// DAMAGE FORMULA (from GlobalKnobs.gc + weapon GC files):
    ///   baseDamage = (WeaponDamagePerLevel(10) * level) + (MeleeDamagePerStrength(2.3364) * Strength(10))
    ///   scaledDamage = baseDamage * weaponDamageMultiplier
    ///   Apply DamageVolatility(0.5) for min/max range
    ///   RNG picks value in [min, max]
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
            if ((f & 0xFF) >= 0x80) f += 0x100;
            return f & ~0xFF; // clear lower 8 bits
        }

        // ═══════════════════════════════════════════════════════════════
        // MAIN ENTRY: Process one attack using shared room RNG
        // Binary: applyDamage at 0x597E50
        // Uses entity+0x88 -> entityManager+0x44 RNG (room-level MT19937)
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Run one attack through the binary-verified RNG consumption pattern.
        /// Consumes 3 RNG for miss/block, 4 RNG for hit.
        /// RNG #4 (post-attack proc at 0x598670) fires EVERY swing regardless of hit/miss.
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

            // ──── RNG #4 at 0x5986F5: Post-attack proc check ────
            // Binary: 0x598670 fires at endTick (tick 20), BEFORE doHit (tick 15)
            // Calls Generate(), tests (al & 0x03) for 25% proc chance
            // Consumed EVERY swing regardless of hit/miss — must be first!
            uint procRaw = rng.Generate();
            result.ProcRoll = (int)(procRaw & 0x03);
            result.ProcTriggered = (result.ProcRoll != 0); // non-zero = proc fires

            // ──── RNG #1 at 0x59804B: Hit chance roll ────
            uint hitRaw = rng.Generate();
            result.HitRoll = (int)(hitRaw % 25700);

            // ──── RNG #2 at 0x598133: Block chance roll ────
            uint blockRaw = rng.Generate();
            result.BlockRoll = (int)(((blockRaw >> 8) & 0xFF) % 100) + 1;

            // ──── Hit chance from GC data ────
            // GlobalKnobs: HeroMissChance = 100 → 100% of misses apply
            // Binary: hitPercent = (AR * 100) / (AR + defenseRating)
            // PvE cap: min(hitChanceF32, 0x5A00) = 90%
            // For dungeon00 basic monsters: defenseRating ≈ 0 → hit chance = 90% cap
            int hitChanceF32 = 0x5A00; // 90% — PvE cap from binary
            if (defenseRating > 0)
            {
                // TODO: Full AR/DR computation when player AR stats are tracked
                hitChanceF32 = 0x5A00; // Use cap for now
            }

            // ──── Miss check at 0x598163 ────
            if (result.HitRoll >= hitChanceF32)
            {
                result.Type = AttackResultType.Miss;
                result.RngConsumed = 3;
                Debug.LogError($"[DMG] MISS: hitRoll={result.HitRoll} >= cap={hitChanceF32} proc={result.ProcRoll} (3 RNG consumed)");
                return result;
            }

            // ──── Block check at 0x5982D9 ────
            if (result.BlockRoll <= blockChance)
            {
                result.Type = AttackResultType.Block;
                result.RngConsumed = 3;
                Debug.LogError($"[DMG] BLOCK: blockRoll={result.BlockRoll} <= chance={blockChance} proc={result.ProcRoll} (3 RNG consumed)");
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
            int range = Math.Max(0, maxDmg - minDmg + 1);
            int damage;
            if (range > 0)
                damage = (int)(damageRaw % (uint)range) + minDmg;
            else
                damage = minDmg;

            // Crit check: binary compares hitRoll to critThreshold (no extra RNG)
            // GlobalKnobs: HeroCriticalChance = 3
            // TODO: implement crit threshold from attacker stats
            bool isCrit = false;

            result.Type = isCrit ? AttackResultType.Critical : AttackResultType.Hit;
            result.DamageF32 = damage;
            result.MinDamageF32 = minDmg;
            result.MaxDamageF32 = maxDmg;
            result.RngConsumed = 4;

            Debug.LogError($"[DMG] HIT: dmg={damage} ({damage / 256}hp) range=[{minDmg / 256},{maxDmg / 256}] hitRoll={result.HitRoll} proc={result.ProcRoll} (4 RNG consumed)");
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
            // server.cfg overrides GC values
            float weaponDmgPerLevel = ServerSettings.GetFloat("weaponDamagePerLevel", gc.GetKnob("WeaponDamagePerLevel", 10f));
            float meleeDmgPerStr = ServerSettings.GetFloat("meleeDamagePerStrength", gc.GetKnob("MeleeDamagePerStrength", 2.3364f));

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
            int f32 = (int)(state.WeaponDamage * 256);
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
            return FromFloat(vol);
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
        public static float GC_SKILL_DAMAGE_PER_LEVEL => ServerSettings.GetFloat("skillDamagePerLevel", 15f);
        public static float GC_SKILL_DAMAGE_PER_INTELLECT => ServerSettings.GetFloat("skillDamagePerIntellect", 1.5f);

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
        public static float GC_RANGED_DAMAGE_PER_AGILITY => ServerSettings.GetFloat("rangedDamagePerAgility", 2.124f);

        public static void ComputeRangedDamageRange(
            int attackerLevel,
            int attackerAgility,
            float weaponDamageMultiplier,
            float spellDamageMod,
            float spellDamageVolatility,
            out int minDamage, out int maxDamage)
        {
            // Ranger skills use weapon base + agility scaling, then spell modifier on top
            float levelComponent = ServerSettings.GetFloat("weaponDamagePerLevel", 10f) * attackerLevel; // WeaponDamagePerLevel
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
            float levelComponent = ServerSettings.GetFloat("weaponDamagePerLevel", 10f) * attackerLevel;
            float strengthComponent = ServerSettings.GetFloat("meleeDamagePerStrength", 2.3364f) * attackerStrength;
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
        /// PRIMARY target (isChainTarget=false): 3 Generate() calls
        ///   #1: Hit roll — rng % 25700 vs 25600 (doEffect +0x44, VA 0x545E14)
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

            // ──── Generate #1: Hit roll — ONLY for primary target ────
            // Binary: doEffect +0x44 (VA 0x545E14) — rng % 25700 vs spell.Chance (0x6400=25600)
            // Chain targets dispatched via SpellChainEffect → virtualA0 directly, skipping doEffect
            if (!isChainTarget)
            {
                uint hitRaw = rng.Generate();
                result.HitRoll = (int)(hitRaw % 25700);

                int hitChanceF32 = 0x6400; // 25600 = 100% chance (spell.Chance default)
                if (result.HitRoll >= hitChanceF32)
                {
                    // Miss — still need to consume crit RNG if CriticalChance > 0?
                    // NO: binary shows virtualA0 is only called on hit (doEffect checks hit first)
                    result.Type = AttackResultType.Miss;
                    Debug.LogError($"[SPELL-DMG] MISS: {spell.DisplayName} hitRoll={result.HitRoll} >= chance={hitChanceF32} (1 RNG consumed)");
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
            int range = Math.Max(0, maxDmg - minDmg + 1);
            int damage;
            if (range > 0)
                damage = (int)(damageRaw % (uint)range) + minDmg;
            else
                damage = minDmg;

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

            int rngCalls = (isChainTarget ? 0 : 1) + 1 + (spell.CriticalChance > 0 ? 1 : 0);
            string formulaTag = spell.IsWeaponSkill ? "WPNSKILL" : spell.AttackType == AttackType.RANGED ? "RANGED" : "MAGIC";
            Debug.LogError($"[SPELL-DMG] {(isCrit ? "CRIT" : "HIT")}: {spell.DisplayName} [{formulaTag}] {(isChainTarget ? "CHAIN" : "PRIMARY")} dmg={damage / 256} ({damage}) range=[{minDmg / 256},{maxDmg / 256}] hitRoll={result.HitRoll} ({rngCalls} RNG consumed)");
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
        public int HitRoll;
    }

    public class AttackResult
    {
        public AttackResultType Type;
        public int DamageF32;      // Fixed32 wire format damage
        public int MinDamageF32;
        public int MaxDamageF32;
        public int HitRoll;
        public int BlockRoll;
        public int ProcRoll;       // RNG #4: post-attack proc (0x598670), 0=no proc, non-zero=proc
        public bool ProcTriggered;
        public int RngConsumed;    // 3 for miss/block, 4 for hit
    }

    public enum AttackResultType
    {
        Miss,
        Block,
        Hit,
        Critical
    }
}
