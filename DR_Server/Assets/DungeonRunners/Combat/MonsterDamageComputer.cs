using System;
using UnityEngine;

namespace DungeonRunners.Combat
{
    /// <summary>
    /// Section 10 task S10.3 — server-side mirror of <c>Weapon::applyDamage @ 0x00597e50</c>
    /// for monster→player attacks. Reproduces the client's hit/block/crit/damage rolls
    /// in the same order from the shared MT19937 (<see cref="CombatManager.SyncedRandom"/>).
    ///
    /// <para>
    /// Spec source: <c>WORK/AUDIT_COMBAT/10b_FORMULA_SPEC.md</c>. All field offsets and
    /// arithmetic are from Ghidra decompile of the 9 helper functions.
    /// </para>
    ///
    /// <para>
    /// <b>Unit field caching is approximated.</b> The client caches AR/DR/etc. from
    /// authored <c>.gc</c> Description fields into unit offsets (+0xF0, +0x12C, ...)
    /// at spawn time via <c>Unit::readInit</c> or similar. We don't yet have that
    /// decompile, so this implementation uses simple multipliers (level × authored
    /// fraction). Divergence from client will surface in the diagnostic logs;
    /// expect to refine after x32dbg roll-by-roll capture.
    /// </para>
    /// </summary>
    public static class MonsterDamageComputer
    {
        /// <summary>One swing's outcome — recorded for diagnostic logging.</summary>
        public struct SwingResult
        {
            public bool Hit;
            public bool Blocked;
            public bool Crit;
            public int Damage;        // final damage in wire units (256 = 1 hp)

            // Diagnostics — exact roll values + scaled chances for x32dbg comparison
            public uint R1Hit;        // raw r1 from RNG
            public uint R2Block;      // raw r2 from RNG
            public uint R3Damage;     // raw r3 from RNG (0 if no damage roll)
            public int HitRoll;       // r1 % 25700
            public int HitChanceScaled;
            public int BlockRoll;     // ((r2 >> 8) & 0xff) % 100 + 1
            public int BlockChance;
            public int CritChance;
            public int AttackerAR;
            public int TargetDR;
            public int DamageMin;
            public int DamageMax;
            public byte AttackStyle;
            public byte DamageType;
        }

        /// <summary>
        /// Compute one swing's outcome. Consumes 2 or 3 RNG calls from
        /// <see cref="CombatManager.SyncedRandom"/> matching the client's order.
        /// </summary>
        public static SwingResult ComputeSwing(MonsterUnitStats attacker, PlayerUnitStats target, MersenneTwister rng)
        {
            var result = default(SwingResult);
            byte attackStyle = attacker.AttackStyle;
            byte damageType = attacker.WeaponDamageType;
            result.AttackStyle = attackStyle;
            result.DamageType = damageType;

            // ── AR / DR / hitChance ──
            int weaponAR = GetWeaponSpecificAR(attackStyle, attacker);
            int weaponARMOD = GetWeaponSpecificARMOD(attackStyle, attacker);
            int baseAR = attacker.BaseAttackRating;
            int baseARMOD = attacker.BaseAttackRatingMod;
            // ApplyDamageContext.field_0 — usually 0 for mob basic attacks.
            int contextField0 = 0;
            int AR = ((weaponAR + baseAR) * (weaponARMOD + baseARMOD + contextField0 + 100)) / 100;
            if (AR < 0) AR = 0;

            int DR = ComputeDefenseRating(attackStyle, target);
            int hitChance = (AR + DR != 0) ? (AR * 100) / (AR + DR) : 0;

            // Discriminator adjustment (+0x314). For mob→player, attacker is mob, target is player.
            // Per spec: hitChance_scaled = hitChance × 256 − ((targetDiscrim − attackerDiscrim) × 5)
            // Stat-parity fix 2026-05-30: RAW discriminator (NOT *0x100). The client applies the delta as
            // (rawDelta * 0x500) >> 8 == rawDelta * 5 (Ghidra), and the section-10 spec says the same. The old
            // *0x100 overweighted the term 256x -> ~+10% hit AND a phantom ~10% mob crit (mobDisc2 vs playerDisc0),
            // both making the server over-damage vs the client. Hit term below does *5; crit term does *0x500>>8;
            // both are correct with raw values.
            int discrimAttacker = attacker.Discriminator;
            int discrimTarget = target.Discriminator;
            int hitChanceScaled = hitChance * 256 - ((discrimTarget - discrimAttacker) * 5);
            if (hitChanceScaled < 0x0A00) hitChanceScaled = 0x0A00; // PvE floor 10%

            result.AttackerAR = AR;
            result.TargetDR = DR;
            result.HitChanceScaled = hitChanceScaled;

            // ── Roll 1: HIT ──
            result.R1Hit = rng.Generate();
            int hitRoll = (int)(result.R1Hit % 0x6464);
            result.HitRoll = hitRoll;

            // ── Roll 2: BLOCK (always consumed) ──
            result.R2Block = rng.Generate();
            int blockChance = target.BlockChance;
            int blockRoll = (int)(((result.R2Block >> 8) & 0xFF) % 100 + 1);
            result.BlockChance = blockChance;
            result.BlockRoll = blockRoll;

            bool hit = hitRoll < hitChanceScaled;
            bool blocked = hit && blockRoll <= blockChance;

            result.Hit = hit;
            result.Blocked = blocked;

            if (!hit || blocked) return result;

            // ── Crit (reuses r1) ──
            int critChance = ComputeCriticalChance(attackStyle, attacker);
            // discriminator delta bias
            int discrimDelta = discrimAttacker - discrimTarget;
            critChance += (int)(((long)discrimDelta * 0x500) >> 8);
            if (critChance > 0x5A00) critChance = 0x5A00;
            if (critChance < 0) critChance = 0;
            result.CritChance = critChance;
            bool crit = hitRoll < critChance;
            result.Crit = crit;

            // ── Damage computation ──
            ushort dmgMod = ComputeDamageMod(attackStyle, damageType, attacker);
            ushort dmgBonus = ComputeDamageBonus(attackStyle, damageType, attacker);
            ComputeDamageRange(attacker, dmgMod, dmgBonus, out int min, out int max);
            result.DamageMin = min;
            result.DamageMax = max;

            // ── Roll 3: DAMAGE ──
            result.R3Damage = rng.Generate();
            int range = ((max >> 8) - (min >> 8)) + 1;
            if (range <= 0) range = 1;
            int damage = (int)(result.R3Damage % (uint)range) * 0x100 + (min & ~0xFF);

            if (crit) damage = (attacker.CritMultiplier * damage) / 100;
            if (damage < 0x100) damage = 0x100;

            result.Damage = damage;
            return result;
        }

        // ─── Helper ports (literal from spec) ──────────────────────────────

        public static int ComputeDefenseRating(byte attackStyle, PlayerUnitStats target)
        {
            int dr = target.BaseDefenseRating;
            int drMod = target.BaseDefenseRatingMod;
            switch (attackStyle)
            {
                case 1: case 5: case 6: case 8:
                    dr += target.MeleeDefenseRating;
                    drMod += target.MeleeDefenseRatingMod;
                    break;
                case 3: case 9: case 13:
                    dr += target.RangedDefenseRating;
                    drMod += target.RangedDefenseRatingMod;
                    break;
            }
            int result = ((drMod + 100) * dr) / 100;
            return result < 0 ? 0 : result;
        }

        public static int GetWeaponSpecificAR(byte attackStyle, MonsterUnitStats attacker)
        {
            switch (attackStyle)
            {
                case 1: return attacker.MeleeAR;
                case 3: case 9: case 13: return attacker.RangedAR;
                case 5: return attacker.Style5AR + attacker.MeleeAR;
                case 6: case 8: return attacker.Style6AR + attacker.MeleeAR;
                default: return 0;
            }
        }

        public static int GetWeaponSpecificARMOD(byte attackStyle, MonsterUnitStats attacker)
        {
            switch (attackStyle)
            {
                case 1: return attacker.MeleeARMod;
                case 3: case 9: case 13: return attacker.RangedARMod;
                case 5: return attacker.Style5ARMod + attacker.MeleeARMod;
                case 6: case 8: return attacker.Style6ARMod + attacker.MeleeARMod;
                default: return 0;
            }
        }

        public static int ComputeCriticalChance(byte attackStyle, MonsterUnitStats attacker)
        {
            int result = attacker.BaseCriticalChance << 8;
            int addend = 0;
            switch (attackStyle)
            {
                case 1: addend = attacker.MeleeCritChance << 8; break;
                case 3: case 9: case 13: addend = attacker.RangedCritChance << 8; break;
                case 5: addend = (attacker.MeleeCritChance + attacker.Style5CritChance) << 8; break;
                case 6: case 8: addend = (attacker.MeleeCritChance + attacker.Style6CritChance) << 8; break;
            }
            int term = (int)((long)(addend << 8) / 0x6400);
            result += (int)(((long)result * term) >> 8);
            if (result < 0) result = 0;
            if (result > 0x6400) result = 0x6400;
            return result;
        }

        public static ushort ComputeDamageBonus(byte attackStyle, byte damageType, MonsterUnitStats attacker)
        {
            int bonus = attacker.BaseDamageBonus & 0xFFFF;
            switch (attackStyle)
            {
                case 1: bonus += attacker.MeleeDamageBonus; break;
                case 3: case 9: case 13: bonus += attacker.RangedDamageBonus; break;
                case 5: bonus += attacker.Style5DamageBonus + attacker.MeleeDamageBonus; break;
                case 6: case 8: bonus += attacker.Style6DamageBonus + attacker.MeleeDamageBonus; break;
            }
            if (damageType < 8) bonus += attacker.DamageTypeBonus[damageType];
            return (ushort)bonus;
        }

        public static ushort ComputeDamageMod(byte attackStyle, byte damageType, MonsterUnitStats attacker)
        {
            int result = (attacker.BaseDamageMod + 0) * 256;  // dmgBonus_param=0 for monster basic attack
            switch (attackStyle)
            {
                case 1: result += attacker.MeleeDamageMod * 256; break;
                case 3: case 9: case 13: result += attacker.RangedDamageMod * 256; break;
                case 5: result += (attacker.Style5DamageMod + attacker.MeleeDamageMod) * 256; break;
                case 6: case 8: result += (attacker.Style6DamageMod + attacker.MeleeDamageMod) * 256; break;
            }
            if (damageType < 8) result += attacker.DamageTypeMod[damageType] * 256;
            result += 0x6400;  // +100 percent base
            if (result < 0) result = 0;
            return (ushort)(((long)result * attacker.DamageModScale) >> 16);
        }

        /// <summary>
        /// C6 — port of client <c>Unit::onQueryApplyDamage @ 0x0050b9c0</c>. Applies
        /// DamageTakenMod (unit[+0x2C4]) and damage-type elemental resistance
        /// (unit[+0xC8 + resIndex*4] for damage types 3..7) to the inbound damage
        /// before the HP write. Returns the adjusted damage; sets <paramref name="resisted"/>
        /// when the elemental check fully nullified the damage.
        ///
        /// <para>
        /// Client formula (decompiled, exact):
        /// <code>
        /// if (DamageTakenMod != 100): damage = damage * DamageTakenMod / 100
        /// switch (damageType):
        ///   case 3..7: resMult = unit[+0xC8 + resIndex*4]
        ///     if (resMult != 100):
        ///       if (resMult &lt; 1): damage = 0; flags |= RESISTED
        ///       else: damage = damage * resMult / 100
        ///   default (0..2): no elemental resistance
        /// </code>
        /// </para>
        ///
        /// <para>
        /// Server stores resists as percent-resisted (.gc convention: 0 = none, 100 = immune)
        /// while the client stores them as a damage multiplier (100 = full damage, 0 = immune).
        /// Inversion happens here: <c>resMult = 100 - percentResisted</c>. The <c>&lt; 1</c>
        /// branch then triggers naturally when authored resist ≥ 100.
        /// </para>
        ///
        /// <para>
        /// CheckDamageResist (chance-based, gated by event flag bit 8) is NOT ported here —
        /// it's modifier-driven and belongs to C7 (shields / reflect). Bit 8 is never set on
        /// vanilla mob basic attacks.
        /// </para>
        /// </summary>
        public static uint OnQueryApplyDamage(uint damageWire, byte damageType, PlayerUnitStats target, out bool resisted)
        {
            resisted = false;
            if (damageWire == 0 || target == null) return damageWire;

            long damage = damageWire;

            // Step 1 — DamageTakenMod
            if (target.DamageTakenMod != 100)
            {
                damage = (damage * target.DamageTakenMod) / 100;
            }

            // Step 2 — elemental resistance (types 3..7 only)
            int authoredResist;
            switch (damageType)
            {
                case 3: authoredResist = target.FireResist; break;
                case 4: authoredResist = target.IceResist; break;
                case 5: authoredResist = target.PoisonResist; break;
                case 6: authoredResist = target.DivineResist; break;
                case 7: authoredResist = target.ShadowResist; break;
                default:
                    return damage < 0 ? 0u : (uint)damage;  // physical types: no elemental res
            }

            // Invert .gc percent-resisted → client damage multiplier
            int resMult = 100 - authoredResist;
            if (resMult != 100)
            {
                if (resMult < 1)
                {
                    resisted = true;
                    return 0u;
                }
                damage = (damage * resMult) / 100;
            }

            return damage < 0 ? 0u : (uint)damage;
        }

        /// <summary>
        /// C7 — port of client reflect/thorns logic inside <c>Unit::onApplyDamage @ 0x0050BE50</c>.
        /// When a unit takes damage with <paramref name="eventKind"/> != 4, the unit's reflect
        /// fields determine how much damage to push back to the attacker.
        ///
        /// <para>
        /// Formula (exact Ghidra port):
        /// <code>
        /// if (eventKind == 4) return 0   // already reflected — no recursion
        /// reflectPct = base
        /// if (eventKind == 1) reflectPct += meleeBonus
        /// elif (eventKind == 2) reflectPct += rangedBonus
        /// if (bothPlayers) reflectPct /= 10  // PvP divide (out of scope here)
        /// if (reflectPct &lt;= 0) return 0
        /// reflected = (incoming * reflectPct) / 100
        /// </code>
        /// </para>
        ///
        /// <para>
        /// Event-kind mapping from <c>Weapon::applyDamage</c>:
        ///  - 1 = melee
        ///  - 2 = ranged
        ///  - 3 = spell (no separate offset — uses base only)
        ///  - 4 = "already a reflected damage" sentinel (prevents infinite ping-pong)
        ///  - 0 / other = base only
        /// </para>
        /// </summary>
        public static uint ComputeReflectedDamage(uint incomingDamageWire, byte eventKind,
            int baseReflectPct, int meleeReflectBonusPct, int rangedReflectBonusPct)
        {
            if (eventKind == 4 || incomingDamageWire == 0) return 0;
            int reflectPct = baseReflectPct;
            if (eventKind == 1) reflectPct += meleeReflectBonusPct;
            else if (eventKind == 2) reflectPct += rangedReflectBonusPct;
            if (reflectPct <= 0) return 0;
            long reflected = ((long)incomingDamageWire * reflectPct) / 100;
            return reflected > 0 ? (uint)reflected : 0;
        }

        /// <summary>
        /// Map a MonsterDamageComputer attackStyle byte to the client's event-kind value.
        /// AttackStyle 1 / 5 / 6 / 8 are melee variants → kind 1. AttackStyle 3 / 9 / 13 are
        /// ranged → kind 2. Default → 0 (base reflect only).
        /// </summary>
        public static byte EventKindFromAttackStyle(byte attackStyle)
        {
            switch (attackStyle)
            {
                case 1: case 5: case 6: case 8: return 1;  // melee
                case 3: case 9: case 13: return 2;          // ranged
                default: return 0;
            }
        }

        public static void ComputeDamageRange(MonsterUnitStats attacker, ushort dmgMod, ushort dmgBonus, out int min, out int max)
        {
            // x32dbg disasm of computeDamageRange @ 0x00598ED0 + 1 swing capture against
            // a level-6 Warg pup proved the formula:
            //   prod1 = (dmgMod + level) × 256 × weapon.Damage
            //   prod2 = (prod1 >> 8) × (ECX << 8)       where ECX = WeaponDamagePerLevel (=10 in GlobalKnobs)
            //   base  = (prod2 >> 8) × 256 / 25600       (the 24-bit shift before IDIV is effectively /100)
            //   spread = base × weapon.Volatility / 256
            //
            // For Warg pup L6 (dmgMod=50): base = 56 × 256 × 10 / 100 = 1434, spread = 717.
            // Range = [768, 2048] = [3, 8] HP. Matched observation 4, 6, 8 HP.
            int level = attacker.Level;
            int wpnDmg = attacker.WeaponDamageFixed;      // weaponDesc[+0xEC] (Fixed32, default 256 = 1.0)
            int wpnDmgPerLvl = attacker.WeaponDamagePerLevel;  // ECX factor (GlobalKnobs.WeaponDamagePerLevel = 10)
            int volatility = attacker.WeaponVolatilityFixed;   // weaponDesc[+0xF0]

            long prod1 = (long)((dmgMod & 0xFFFF) * 256 + level * 256) * wpnDmg;
            int prod1_shifted = (int)(prod1 >> 8);

            long prod2 = (long)prod1_shifted * (wpnDmgPerLvl << 8);
            int prod2_shifted = (int)(prod2 >> 8);

            // Decompile shows SHRD/SAR/IDIV by 25600 — effective op is `prod2_shifted × 256 / 25600`
            // which simplifies to `prod2_shifted / 100`.
            int baseValue = prod2_shifted / 100;

            long t3 = (long)baseValue * volatility;
            int spread = (int)(t3 >> 8);

            min = baseValue - spread;
            max = baseValue + spread;

            if ((min & 0xFF) > 0x7E) min += 0x100;
            min &= ~0xFF;
            if ((max & 0xFF) > 0x7E) max += 0x100;
            max &= ~0xFF;

            if (min < 0x100) min = 0x100;
            if (max < 0x100) max = 0x100;
        }
    }

    /// <summary>
    /// Monster combat stats — the cached unit-field values used by the damage formula.
    /// Populated from <see cref="MonsterAttackProfile"/> + global knobs + monster level.
    ///
    /// Field naming maps to the +0x100..+0x300 region of the client's Unit struct
    /// (see <c>10b_FORMULA_SPEC.md</c>). Per-attack-style fields are 0 unless the mob
    /// has special weapon bonuses (rare for vanilla mobs).
    /// </summary>
    public sealed class MonsterUnitStats
    {
        public int Level;
        public byte AttackStyle;          // weaponDesc[+0xD5]: 1 = melee basic; 3 = ranged; etc.
        public byte WeaponDamageType;     // weaponDesc[+0xE8]: 0=SLASHING, 1=PIERCING, ...
        public ushort Discriminator;      // unit[+0x314]: per-mob RNG offset, server-controlled

        // unit[+0x100..+0x118]
        public int BaseDamageMod;         // +0x100
        public int BaseCriticalChance;    // +0x104 (e.g. 6 from GlobalKnobs.MonsterCriticalChance)
        public int CritMultiplier;        // +0x118 (×100 — e.g. 150 = 1.5×)

        // unit[+0x12C / +0x130] — defensive (mob's own DR; not used for monster→player but stored)
        public int BaseDefenseRating;     // +0x12C
        public int BaseDefenseRatingMod;  // +0x130
        public int BlockChance;           // +0x138 (player will have this; mob = 0)

        // unit[+0xF0 / +0xF4] — attack rating
        public int BaseAttackRating;      // +0xF0 = AttackRating × level scale
        public int BaseAttackRatingMod;   // +0xF4
        public int BaseDamageBonus;       // +0xFC

        // unit[+0x174..+0x190] — style=1 (melee basic) cached fields
        public int MeleeAR;               // +0x174
        public int MeleeARMod;            // +0x178
        public int MeleeDamageBonus;      // +0x180
        public int MeleeDamageMod;        // +0x184
        public int MeleeCritChance;       // +0x188
        public int MeleeDefenseRating;    // +0x18C
        public int MeleeDefenseRatingMod; // +0x190

        // unit[+0x198..+0x1B4] — style=3/9/13 (ranged) cached fields
        public int RangedAR;              // +0x198
        public int RangedARMod;           // +0x19C
        public int RangedDamageBonus;     // +0x1A4
        public int RangedDamageMod;       // +0x1A8
        public int RangedCritChance;      // +0x1AC
        public int RangedDefenseRating;   // +0x1B0
        public int RangedDefenseRatingMod;// +0x1B4

        // unit[+0x1BC..+0x1D0] — style=5 variant
        public int Style5AR, Style5ARMod, Style5DamageBonus, Style5DamageMod, Style5CritChance;
        // unit[+0x1D4..+0x1E8] — style=6/8 variant
        public int Style6AR, Style6ARMod, Style6DamageBonus, Style6DamageMod, Style6CritChance;

        // unit[+0x230..+0x298] — per-damage-type bonus arrays (8 types each)
        public int[] DamageTypeBonus = new int[8];   // +0x234, 0x240, 0x24C, 0x258, 0x268, 0x278, 0x288, 0x298
        public int[] DamageTypeMod = new int[8];     // +0x230, 0x23C, 0x248, 0x254, 0x264, 0x274, 0x284, 0x294

        // C7 — reflect (thorns). Mobs typically don't reflect (no equipment), but some
        // bosses may carry authored ReflectDamage values. Default 0 for vanilla mobs.
        public int BaseReflectPct;           // unit[+0x134]
        public int MeleeReflectBonusPct;     // unit[+0x194]
        public int RangedReflectBonusPct;    // unit[+0x1B8]

        // unit[+0x300]
        public int DamageModScale = 256;  // 256 = 1.0× (no scale)

        // Weapon-side
        public int WeaponDamageFixed = 256;  // weaponDesc[+0xEC] — Damage in Fixed32 (256 = 1.0)
        public int WeaponDamagePerLevel;     // ECX factor in computeDamageRange — GlobalKnobs.WeaponDamagePerLevel (=10)
        public int WeaponVolatilityFixed;    // weaponDesc[+0xF0] — Volatility in Fixed32 (128 = 0.5)
    }

    /// <summary>Player target stats consumed by <see cref="MonsterDamageComputer"/>.</summary>
    public sealed class PlayerUnitStats
    {
        public int BaseDefenseRating;        // unit[+0x12C]
        public int BaseDefenseRatingMod;     // unit[+0x130]
        public int BlockChance;              // unit[+0x138]
        public ushort Discriminator;         // unit[+0x314]

        public int MeleeDefenseRating;       // +0x18C
        public int MeleeDefenseRatingMod;    // +0x190
        public int RangedDefenseRating;      // +0x1B0
        public int RangedDefenseRatingMod;   // +0x1B4

        // C6 — damage-side modifiers, applied via OnQueryApplyDamage before the HP write.
        // Semantics match the authored .gc convention used by Monster: 0 = no resistance,
        // 100 = full immunity. The helper inverts to the client's [+0x2DC..+0x2EC] multiplier
        // form (multiplier = 100 - resist) before applying the formula.
        public int DamageTakenMod = 100;     // unit[+0x2C4] — 100 = take full damage
        public int FireResist;               // damage type 3 (Fire) -> unit[+0x2DC] inverted
        public int IceResist;                // damage type 4 (Cold/Ice) -> unit[+0x2E0]
        public int PoisonResist;             // damage type 5 (Lightning/Poison) -> unit[+0x2E4]
        public int DivineResist;             // damage type 6 (Holy/Divine) -> unit[+0x2E8]
        public int ShadowResist;             // damage type 7 (Dark/Shadow) -> unit[+0x2EC]

        // C7 — reflect (thorns). Populated from equipment modifiers (MeleeDamageReflectB,
        // RangeDamageReflectB, DamageReflectBonus). Aggregated at attribute-compute time
        // into unit[+0x134/+0x194/+0x1B8]. Vanilla L1 player = 0 across the board.
        // Default unit value when no equipment provides it: 0.
        public int BaseReflectPct;           // unit[+0x134] — applies to all damage kinds
        public int MeleeReflectBonusPct;     // unit[+0x194] — added to base when event.kind==1 (melee)
        public int RangedReflectBonusPct;    // unit[+0x1B8] — added to base when event.kind==2 (ranged)
    }
}
