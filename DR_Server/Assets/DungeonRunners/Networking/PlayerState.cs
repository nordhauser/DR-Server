using DungeonRunners.Data;
using DungeonRunners.Core;
using UnityEngine;
using System;
using System.Collections.Generic;

namespace DungeonRunners.Networking
{
    public class PlayerState
    {
        public float PositionX { get; set; } = 100f;
        public float PositionY { get; set; } = 100f;
        public GCObject ActiveItem { get; set; }

        // Binary-verified: weapon damage multiplier from item DB (e.g. 0.79 for starter mace)
        // Used in damage formula: effectDamage = level × WeaponDamagePerLevel(10) × WeaponDamage
        public float WeaponDamage { get; set; } = 0.79f;
        public float WeaponDamageVolatility { get; set; } = 0.33f;
        public int WeaponLevel { get; set; } = 1;
        public string WeaponClass { get; set; } = "1HMELEE";
        public string WeaponCategory { get; set; } = "1HMACE";
        public int WeaponRange { get; set; } = 8;       // From weapons.json range field (1H=8, 2H=16, ranged=20)
        public float WeaponCooldown { get; set; } = 0f; // Native WeaponDesc CoolDown default
        public float WeaponSpeed { get; set; } = 105f;
        public bool WeaponUsesProjectile { get; set; } = false;
        public float WeaponProjectileSpeed { get; set; } = 0f;
        public float WeaponProjectileSize { get; set; } = 0f;
        public int WeaponBurstCount { get; set; } = 1;
        public int MeleeAttackRatingModPercent { get; set; } = 0;
        public float MeleeAttackSpeedModPercent { get; set; } = 0f;
        public float RangeAttackSpeedModPercent { get; set; } = 0f;
        public int MovementSpeedModPercent { get; private set; } = 0;
        public int MinMovementSpeedModValue { get; private set; } = 0;
        public float DamageTakenMod { get; set; } = 100f;
        public int ArmorDefenseRating { get; set; } = 0;

        // Base stats from FighterBase/RangerBase/MageBase.gc — all start at 10
        public int Strength { get; set; } = 10;
        public int Agility { get; set; } = 10;
        public int Intelligence { get; set; } = 10;  // ADD THIS LINE
        public int Toughness { get; set; } = 10;
        public int Power { get; set; } = 10;
        private const int BASE_STAT_VALUE = 10;
        private int _allocatedStrength = 0;
        private int _allocatedAgility = 0;
        private int _allocatedEndurance = 0;
        private int _allocatedIntellect = 0;

        private int _level = 1;
        private string _className = "Fighter";
        public int Level => _level;
        public string ClassName => _className;
        public uint Experience { get; set; } = 0;
        public uint Gold { get; set; } = 0;

        // Tables.gc Experience CurveTable — "Value = # of 1.0 monsters at your level required for next level"
        // These are the keyframes, we interpolate between them
        private static readonly (int level, float value)[] XPCurve = new[]
        {
            (2, 10f),
            (3, 25f),
            (4, 45f),
            (5, 65f),
            (100, 5000f)
        };

        public uint GetXPThreshold()
        {
            int targetLevel = _level + 1;
            if (targetLevel > GCDatabase.Instance.GetKnobInt("MaxLevel", 100)) return uint.MaxValue;

            // Linear interpolation on CurveTable, same as client
            float kills = 0;
            for (int i = 0; i < XPCurve.Length; i++)
            {
                if (targetLevel <= XPCurve[i].level)
                {
                    if (i == 0)
                    {
                        kills = XPCurve[i].value;
                    }
                    else
                    {
                        float t = (float)(targetLevel - XPCurve[i - 1].level) / (XPCurve[i].level - XPCurve[i - 1].level);
                        kills = XPCurve[i - 1].value + t * (XPCurve[i].value - XPCurve[i - 1].value);
                    }
                    break;
                }
            }
            if (kills == 0) kills = XPCurve[XPCurve.Length - 1].value;

            // GlobalKnobs.ExperienceMod = 5.0
            // Threshold IS the raw CurveTable value — ExperienceMod only applies to XP per kill
            // Binary at 0x4FAF85: imul eax, eax, 0x64 — CurveTable × 100
            return (uint)(kills * 100.0f);
        }
        // XP gained per kill — scales with monster level using same CurveTable
        // Binary at 0x4F8409: CurveTable(monsterLevel) × 50
        // Binary at 0x4F83AA: clamps monsterLevel to playerLevel, then divides
        // Result: ratio always ≤ 1.0, CurveTable always returns base value
        // Every kill within 5 levels = 500 XP. Mobs 5+ levels below = 0 XP.
        public static uint GetXPPerKill(int monsterLevel, int playerLevel)
        {
            if (monsterLevel <= playerLevel - 5)
                return 0;

            int effectiveLevel = Math.Min(monsterLevel, playerLevel);

            // Binary 0x42BFF0: Fixed32 divide
            // (effectiveLevel << 8) shifted left 8 more, divided by (playerLevel << 8)
            long num = (long)(effectiveLevel << 8) << 8;
            int den = playerLevel << 8;
            int ratioF32 = (int)(num / den);

            // Apply ratio to base 500 XP, convert from Fixed8.8
            uint xp = (uint)((ratioF32 * 500) >> 8);
            if (xp < 1) xp = 1;
            return xp;
        }

        /// <summary>
        /// Raw CurveTable lookup for XP packet to client.
        /// Client's Hero::onAddExperience applies its own ExperienceMod scaling.
        /// Returns the base value from Tables.Experience CurveTable at the given level.
        /// </summary>
        public static uint GetBaseXPForLevel(int level)
        {
            // Linear interpolation on CurveTable, same as GetXPThreshold but at arbitrary level
            float kills = 0;
            for (int i = 0; i < XPCurve.Length; i++)
            {
                if (level <= XPCurve[i].level)
                {
                    if (i == 0)
                        kills = XPCurve[i].value;
                    else
                    {
                        float t = (float)(level - XPCurve[i - 1].level) / (XPCurve[i].level - XPCurve[i - 1].level);
                        kills = XPCurve[i - 1].value + t * (XPCurve[i].value - XPCurve[i - 1].value);
                    }
                    break;
                }
            }
            if (kills == 0) kills = XPCurve[XPCurve.Length - 1].value;
            // Return as Fixed32 8.8 (multiply by 256), then scale by ExperienceMod (5.0)
            // Binary at 0x4F8409: CurveTable(level) × 50 — base XP per monster
            return (uint)(kills * 50);
        }

        /// <summary>
        /// Reproduces the client's exact Fixed-point XP threshold calculation.
        /// Binary: HeroDesc::getRequiredExp @ 0x4FAF60: CurveTable::GetValue(level) >> 8, then * 100.
        /// Matches native thresholds: L2=1000, L3=2500, L4=4500, L5=6500.
        /// </summary>
        public static uint GetClientThreshold(int nextLevel)
        {
            const int MULTIPLIER = 100;

            int target = nextLevel << 8; // Fixed8.8

            for (int i = 0; i < XPCurve.Length; i++)
            {
                int lvFixed = (int)XPCurve[i].level << 8;
                int valFixed = (int)XPCurve[i].value << 8;

                if (target <= lvFixed)
                {
                    if (i == 0)
                        return (uint)((int)XPCurve[i].value * MULTIPLIER);

                    int prevLv = (int)XPCurve[i - 1].level << 8;
                    int prevVal = (int)XPCurve[i - 1].value << 8;
                    int delta = valFixed - prevVal;

                    // Client uses Fixed16.16 precision for t
                    int t = (int)((long)(target - prevLv) << 16) / (lvFixed - prevLv);
                    int interp = prevVal + (int)((long)delta * t >> 16);

                    return (uint)((interp >> 8) * MULTIPLIER);
                }
            }
            return (uint)((int)XPCurve[XPCurve.Length - 1].value * MULTIPLIER);
        }


        public bool AddExperience(uint xp)
        {
            Experience += xp;
            bool didLevel = false;
            uint needed = GetClientThreshold(_level + 1);
            while (Experience >= needed && _level < GCDatabase.Instance.GetKnobInt("MaxLevel", 100))
            {
                _level++;
                Experience -= needed;
                // Native Hero::onAddExperience refreshes runtime resources from the new level values.
                _baseHPWire = CalculateBaseHP();
                RecomputeClassPassiveHPBonus();
                _baseManaWire = CalculateMaxMana();
                _currentHPWire = MaxHPWire;
                _currentManaWire = MaxManaWire;
                HasClientHP = true;
                HasClientMana = true;
                ClearObservedClientHP();
                SetClientSyncHP(_currentHPWire);
                Debug.LogError($"[LEVEL-UP] Now level {_level}! HP full {_currentHPWire / 256}/{MaxHPWire / 256}, Mana full {_currentManaWire / 256}/{MaxManaWire / 256}. Next: {GetClientThreshold(_level + 1)} XP");
                didLevel = true;
                needed = GetClientThreshold(_level + 1);
            }
            return didLevel;
        }

        // heroHealthPerLevel: 16 * 256 = 4096 wire.
        private static uint HP_PER_LEVEL_WIRE => (uint)(GCDatabase.Instance.GetKnobInt("HeroHealthPerLevel", 16) * 256);
        private static int HP_PER_ENDURANCE => GCDatabase.Instance.GetKnobInt("HealthPerEndurance", 25);

        private uint _baseHPWire = 0;
        private uint _allocatedHPBonusWire = 0;
        private uint _equipmentHPBonusWire = 0;
        private uint _modifierHPBonusWire = 0;
        private int _passiveHPBonusWire = 0;
        private uint _currentHPWire = 0;
        private uint _clientSyncHPWire = 0;
        private float _clientSyncHPTime = -1f;
        private double _clientSyncHPCarry = 0d;
        private float _clientSyncRegenSuppressUntil = -1f;
        private bool _hasObservedClientHP = false;
        private uint _lastObservedClientHPWire = 0;
        private float _lastObservedClientHPTime = 0f;
        private string _lastObservedClientHPSource = null;
        private const float NativeDamageRegenSuppressSeconds = 10f;
        public const ushort NativeDamageRegenSuppressTicks = 300;
        public const ushort NativeManaRegenSuppressTicks = 0x96;
        private uint _baseManaWire = 0;
        private uint _equipmentManaBonusWire = 0;
        private int _passiveManaBonusWire = 0;
        private uint _currentManaWire = 0;

        // All equipment stat bonuses (human-readable values, not wire format)
        // Populated by CalculateEquipmentBonuses from ItemStatDatabase
        public Dictionary<string, int> EquipmentStats { get; private set; } = new Dictionary<string, int>();

        private int _regenFactor = 0;
        private ushort _regenCooldown = 0;
        private int _manaRegenFactor = 0;
        private ushort _manaRegenCooldown = 0;

        private uint CalculateBaseHP()
        {
            return ClassPassiveData.CalculateNativeHPWire(_level, BASE_STAT_VALUE + Math.Max(0, _allocatedEndurance), 0);
        }

        // 2026-05-22 — bridge between _baseHPWire (no-passive native formula) and the
        // per-class passive delta from ClassPassiveData.CalculateHPBonusWire. Must run
        // at every site that recomputes _baseHPWire (Initialize, SetAllocatedStats, level-up).
        private void RecomputeClassPassiveHPBonus()
        {
            int bonus = ClassPassiveData.CalculateHPBonusWire(_className, _level, _allocatedEndurance);
            _passiveHPBonusWire = bonus;
        }

        private static uint ClampWire(long wire)
        {
            if (wire <= 0) return 0;
            if (wire >= uint.MaxValue) return uint.MaxValue;
            return (uint)wire;
        }

        public void SetCurrentMana(uint wireMana, string source = null, bool applyNativeCooldown = true)
        {
            uint oldMana = _currentManaWire;
            _currentManaWire = Math.Min(wireMana, MaxManaWire);
            HasClientMana = true;
            if (applyNativeCooldown && _currentManaWire < oldMana)
                _manaRegenCooldown = NativeManaRegenSuppressTicks;
            if (_currentManaWire != oldMana)
                Debug.LogError($"[MANA] source={source ?? "SetCurrentMana"} mana={oldMana}->{_currentManaWire}/{MaxManaWire} cooldown={_manaRegenCooldown}");
        }
        private uint CalculateMaxMana()
        {
            uint baseMana = 44800;  // 175 * 256 (verified: 175 + 9*5 = 220 at level 10)
            uint manaPerLevel = (uint)(GCDatabase.Instance.GetKnobInt("PowerPerLevel", 5) * 256);
            return baseMana + (uint)(_level - 1) * manaPerLevel;
        }

        public uint Op12HP => MaxHPWire;
        public uint Op12MaxHP => MaxHPWire;
        public uint SynchHP
        {
            get
            {
                if (!HasClientSyncHP) return _currentHPWire;
                uint maxHP = MaxHPWire;
                uint current = maxHP > 0 ? Math.Min(_currentHPWire, maxHP) : _currentHPWire;
                uint sync = maxHP > 0 ? Math.Min(_clientSyncHPWire, maxHP) : _clientSyncHPWire;
                return HasClientHP && current < sync ? current : sync;
            }
        }

        public uint MaxHPWire => ClampWire((long)_baseHPWire + _allocatedHPBonusWire + _equipmentHPBonusWire + _modifierHPBonusWire + _passiveHPBonusWire);
        public uint CurrentHPWire => _currentHPWire;

        // Stage 2 of combat simulation: typed alias for the server's simulated player HP.
        // Today _currentHPWire IS the simulator output (populated by PlayerState.TakeRuntimeDamage,
        // called from CombatManager.ResolveMonsterAttackDamage). SimulatedHPWire exists so log
        // sites can name the intent explicitly when comparing vs LastObservedClientHPWire.
        public uint SimulatedHPWire => _currentHPWire;

        // Stage 2 diagnostic — last server-simulated monster hit on this player. Populated by
        // CombatManager.ResolveMonsterAttackDamage. Read by UnityGameServer.LogSimulatorDelta
        // when the client reports HP, so we can attribute the delta to a specific swing.
        public float LastSimMonsterAttackNativeTime { get; internal set; } = -1f;
        public uint  LastSimMonsterAttackDamageWire { get; internal set; } = 0;
        public uint  LastSimMonsterAttackMonsterId { get; internal set; } = 0;
        public string LastSimMonsterAttackResult { get; internal set; } = null;  // HIT / BLOCK / MISS / IMMUNE
        public int   LastSimMonsterAttackRoomRngPos { get; internal set; } = 0;

        public void RecordSimMonsterAttack(uint monsterId, string result, uint damageWire, float nativeTime, int rngPos)
        {
            LastSimMonsterAttackMonsterId = monsterId;
            LastSimMonsterAttackResult = result;
            LastSimMonsterAttackDamageWire = damageWire;
            LastSimMonsterAttackNativeTime = nativeTime;
            LastSimMonsterAttackRoomRngPos = rngPos;
        }
        public uint MaxManaWire => ClampWire((long)_baseManaWire + _equipmentManaBonusWire + _passiveManaBonusWire);
        public uint CurrentManaWire => _currentManaWire;
        public uint MaxHPWireWithoutPassives => ClampWire((long)_baseHPWire + _allocatedHPBonusWire + _equipmentHPBonusWire + _modifierHPBonusWire);
        public uint MaxManaWireWithoutPassives => ClampWire((long)_baseManaWire + _equipmentManaBonusWire);
        public uint AllocatedHPBonusWire => _allocatedHPBonusWire;
        public uint EquipmentHPBonusWire => _equipmentHPBonusWire;
        public uint EquipmentManaBonusWire => _equipmentManaBonusWire;
        public uint ModifierHPBonusWire => _modifierHPBonusWire;
        public int PassiveHPBonusWire => _passiveHPBonusWire;
        public int PassiveManaBonusWire => _passiveManaBonusWire;
        public int AllocatedEndurance => _allocatedEndurance;
        public byte UpdateNumber = 0;
        public bool IsDamageImmune { get; set; } = false;
        public bool IsZoneSpawnDamageImmune { get; set; } = false;
        public bool HasAnyDamageImmunity => IsDamageImmune || IsZoneSpawnDamageImmune;
        public bool IsInvisible { get; set; } = false;
        public bool HasClientHP { get; private set; } = false;
        public bool HasClientSyncHP { get; private set; } = false;
        public bool HasClientMana { get; private set; } = false;
        public bool HasObservedClientHP => _hasObservedClientHP;
        public uint LastObservedClientHPWire => _lastObservedClientHPWire;
        public float LastObservedClientHPTime => _lastObservedClientHPTime;
        public string LastObservedClientHPSource => _lastObservedClientHPSource;
        public bool HasPreservableHP => HasClientHP || HasClientSyncHP || _hasObservedClientHP;
        public uint AvatarHP
        {
            get => _currentHPWire;
            set
            {
                _currentHPWire = MaxHPWire > 0 ? Math.Min(value, MaxHPWire) : value;
                HasClientHP = true;
                SetClientSyncHP(_currentHPWire);
            }
        }

        public PlayerState()
        {
            ActiveItem = null;
        }

        public void InitializeStats(string className, int level)
        {
            _className = className ?? "Fighter";
            _level = Math.Max(1, level);
            _allocatedStrength = 0;
            _allocatedAgility = 0;
            _allocatedEndurance = 0;
            _allocatedIntellect = 0;
            _baseHPWire = CalculateBaseHP();
            RecomputeClassPassiveHPBonus();
            _allocatedHPBonusWire = 0;
            _equipmentHPBonusWire = 0;
            _modifierHPBonusWire = 0;
            _passiveHPBonusWire = 0;
            MeleeAttackRatingModPercent = 0;
            MeleeAttackSpeedModPercent = 0f;
            RangeAttackSpeedModPercent = 0f;
            MovementSpeedModPercent = 0;
            MinMovementSpeedModValue = 0;
            _currentHPWire = _baseHPWire;
            _clientSyncHPWire = _currentHPWire;
            _clientSyncHPTime = -1f;
            _clientSyncHPCarry = 0d;
            _clientSyncRegenSuppressUntil = -1f;
            _regenFactor = Mathf.RoundToInt(ResolveClientSyncHPRegenPerSecond());
            _regenCooldown = 0;
            _manaRegenFactor = Mathf.RoundToInt(ResolveClientSyncManaRegenPerSecond());
            _manaRegenCooldown = 0;
            _hasObservedClientHP = false;
            _lastObservedClientHPWire = 0;
            _lastObservedClientHPTime = 0f;
            _lastObservedClientHPSource = null;
            _baseManaWire = CalculateMaxMana();
            _equipmentManaBonusWire = 0;
            _passiveManaBonusWire = 0;
            _currentManaWire = _baseManaWire;
            HasClientHP = false;  // Reset so RecalculateCurrentHP sets HP = MaxHP (with equipment)
            HasClientSyncHP = false;
            HasClientMana = false;
            EquipmentStats.Clear();
            Debug.LogError($"[PLAYERSTATE] INITIALIZED: {_className} Level {_level} | BaseHP={_baseHPWire} BaseMana={_baseManaWire}");
        }

        public void ApplyAllocatedStats(int strength, int agility, int endurance, int intellect)
        {
            bool preserveNativeRegenClock = HasClientSyncHP && _clientSyncHPTime >= 0f;
            if (preserveNativeRegenClock)
                AdvanceClientSyncHP(Time.time, "ApplyAllocatedStats-pre");

            _allocatedStrength = Math.Max(0, strength);
            _allocatedAgility = Math.Max(0, agility);
            _allocatedEndurance = Math.Max(0, endurance);
            _allocatedIntellect = Math.Max(0, intellect);
            int finalStrength = BASE_STAT_VALUE + _allocatedStrength;
            int finalAgility = BASE_STAT_VALUE + _allocatedAgility;
            int finalEndurance = BASE_STAT_VALUE + _allocatedEndurance;
            int finalIntellect = BASE_STAT_VALUE + _allocatedIntellect;
            Strength = Math.Max(1, finalStrength);
            Agility = Math.Max(1, finalAgility);
            Toughness = Math.Max(1, finalEndurance);
            Intelligence = Math.Max(1, finalIntellect);
            Power = Intelligence;
            _baseHPWire = CalculateBaseHP();
            RecomputeClassPassiveHPBonus();
            _allocatedHPBonusWire = 0;

            if (HasClientHP || _hasObservedClientHP)
            {
                if (_currentHPWire == 0 && _hasObservedClientHP)
                    _currentHPWire = _lastObservedClientHPWire;
                if (_currentHPWire > MaxHPWire)
                    _currentHPWire = MaxHPWire;
            }
            else
            {
                _currentHPWire = MaxHPWire;
            }

            SetClientSyncHP(_currentHPWire, resetNativeTickClock: !preserveNativeRegenClock);
            Debug.LogError($"[ALLOC-STATS] STR={strength}->{Strength} AGI={agility}->{Agility} END={endurance}->{Toughness} INT={intellect}->{Intelligence} AllocHP={_allocatedHPBonusWire} MaxHP={MaxHPWire} CurrentHP={_currentHPWire} preserveRegenClock={preserveNativeRegenClock}");
            LogFullState("ALLOC-STATS-FINAL");
        }

        public void AddTotalHealthBonus(int hpBonus)
        {
            uint wireBonus = (uint)(hpBonus * 256);
            _equipmentHPBonusWire += wireBonus;
        }

        public void AddEnduranceBonus(int enduranceBonus)
        {
            // Equipment endurance HP scales through the class HealthPerEnduranceMod the same way
            // base+allocated endurance does. Ranger gets +10%, Fighter +50%, Mage -25%. Without
            // this multiplier the server under-counts equipment HP for Fighter/Ranger and over-counts
            // for Mage. Rounding uses banker's (round-half-to-even) to match client: at L3 a 1-end
            // weapon gives 27.5 HP → 28, at L16 an 11-end weapon gives 302.5 → 302. Float math
            // throughout to keep precision (int truncation diverges from the client at .5 boundaries).
            double hpFromEndurance = (double)enduranceBonus * HP_PER_ENDURANCE;
            var passive = DungeonRunners.Data.ClassPassiveData.Passives.TryGetValue(_className, out var p) ? p : null;
            int mod = passive?.HealthPerEnduranceMod ?? 0;
            if (mod != 0)
                hpFromEndurance = hpFromEndurance * (100 + mod) / 100.0;
            int hpInt = (int)Math.Round(hpFromEndurance, MidpointRounding.ToEven);
            uint wireBonus = (uint)(hpInt * 256);
            _equipmentHPBonusWire += wireBonus;
        }

        public void AddModifierHPBonus(uint wireBonus)
        {
            _modifierHPBonusWire += wireBonus;
        }

        public void SetPassiveBonuses(int hpWireBonus, int manaWireBonus, int meleeAttackRatingModPercent = 0, float meleeAttackSpeedModPercent = 0f, float rangeAttackSpeedModPercent = 0f, int strengthMod = 0, int agilityMod = 0, int enduranceMod = 0, int intellectMod = 0)
        {
            _passiveHPBonusWire = hpWireBonus;
            _passiveManaBonusWire = manaWireBonus;
            MeleeAttackRatingModPercent = meleeAttackRatingModPercent;
            MeleeAttackSpeedModPercent = meleeAttackSpeedModPercent;
            RangeAttackSpeedModPercent = rangeAttackSpeedModPercent;
            Strength = Math.Max(1, BASE_STAT_VALUE + _allocatedStrength + strengthMod);
            Agility = Math.Max(1, BASE_STAT_VALUE + _allocatedAgility + agilityMod);
            Toughness = Math.Max(1, BASE_STAT_VALUE + _allocatedEndurance + enduranceMod);
            Intelligence = Math.Max(1, BASE_STAT_VALUE + _allocatedIntellect + intellectMod);
            Power = Intelligence;
            if (HasClientHP || _hasObservedClientHP)
            {
                if (_currentHPWire == 0 && _hasObservedClientHP)
                    _currentHPWire = _lastObservedClientHPWire;
                if (_currentHPWire > MaxHPWire) _currentHPWire = MaxHPWire;
                HasClientHP = true;
            }
            else
            {
                _currentHPWire = MaxHPWire;
            }
            SetClientSyncHP(_currentHPWire);
            if (HasClientMana)
            {
                if (_currentManaWire > MaxManaWire) _currentManaWire = MaxManaWire;
            }
            else
            {
                _currentManaWire = MaxManaWire;
            }
        }

        public void ApplyMaxHPModifier(uint wireBonusToAdd)
        {
            uint oldMax = MaxHPWire;
            _modifierHPBonusWire += wireBonusToAdd;
            Debug.LogError($"[HP-MOD] MaxHP: {oldMax} -> {MaxHPWire} (CurrentHP stays {_currentHPWire})");
        }

        public void RemoveMaxHPModifier(uint wireBonusToRemove)
        {
            _modifierHPBonusWire = wireBonusToRemove > _modifierHPBonusWire
                ? 0 : _modifierHPBonusWire - wireBonusToRemove;
            if (_currentHPWire > MaxHPWire) _currentHPWire = MaxHPWire;
            SetClientSyncHP(_currentHPWire);
            Debug.LogError($"[HP-MOD] MaxHP now {MaxHPWire} (CurrentHP={_currentHPWire})");
        }

        public void ClearEquipmentBonuses()
        {
            _equipmentHPBonusWire = 0;
            _modifierHPBonusWire = 0;
            _equipmentManaBonusWire = 0;
            WeaponDamage = 0.79f;
            WeaponDamageVolatility = 0.33f;
            WeaponLevel = 1;
            WeaponClass = "1HMELEE";
            WeaponCategory = "1HMACE";
            WeaponRange = 8;
            WeaponCooldown = 0f;
            WeaponSpeed = 105f;
            WeaponUsesProjectile = false;
            WeaponProjectileSpeed = 0f;
            WeaponProjectileSize = 0f;
            WeaponBurstCount = 1;
            ArmorDefenseRating = 0;
            EquipmentStats.Clear();
        }

        public void SetMovementSpeedModifiers(int speedModPercent, int minSpeedModValue)
        {
            MovementSpeedModPercent = speedModPercent;
            MinMovementSpeedModValue = Math.Max(0, minSpeedModValue);
        }

        public void AddArmorDefenseRating(int defenseRating)
        {
            if (defenseRating > 0)
                ArmorDefenseRating += defenseRating;
        }

        public void AddManaBonus(int manaBonus)
        {
            uint wireBonus = (uint)(manaBonus * 256);
            _equipmentManaBonusWire += wireBonus;
        }

        // Intellect → Mana conversion: 17 mana per intellect point (verified from client)
        public void AddIntellectManaBonus(int intellectBonus)
        {
            int manaFromInt = intellectBonus * GCDatabase.Instance.GetKnobInt("PowerPerIntellect", 17);
            uint wireBonus = (uint)(manaFromInt * 256);
            _equipmentManaBonusWire += wireBonus;
            Debug.LogError($"[MANA-INT] INT+{intellectBonus} → +{manaFromInt} mana (wire +{wireBonus})");
        }

        public void RecalculateCurrentHP()
        {
            uint newMaxHP = MaxHPWire;
            if (HasClientHP || _hasObservedClientHP)
            {
                if (_currentHPWire == 0 && _hasObservedClientHP)
                    _currentHPWire = _lastObservedClientHPWire;
                if (_currentHPWire > newMaxHP)
                    _currentHPWire = newMaxHP;
                HasClientHP = true;
                Debug.LogError($"[HP-FINAL] Kept client HP: {_currentHPWire} (max={newMaxHP}, SynchHP={SynchHP})");
            }
            else
            {
                _currentHPWire = newMaxHP;
                Debug.LogError($"[HP-FINAL] base={_baseHPWire} + allocated={_allocatedHPBonusWire} + equip={_equipmentHPBonusWire} + mod={_modifierHPBonusWire} + passive={_passiveHPBonusWire} = {_currentHPWire} (SynchHP={SynchHP})");
            }
            SetClientSyncHP(_currentHPWire);
        }

        public void TakeDamage(uint wireAmount)
        {
            ApplyDamage(wireAmount, true, null, false);
        }

        public void TakeDamage(uint wireAmount, float nativeDamageTime, bool advanceBeforeDamage = false)
        {
            ApplyDamage(wireAmount, true, nativeDamageTime, advanceBeforeDamage);
        }

        public void TakeRuntimeDamage(uint wireAmount)
        {
            ApplyDamage(wireAmount, false, null, false);
        }

        public void TakeRuntimeDamage(uint wireAmount, float nativeDamageTime, bool advanceBeforeDamage = false)
        {
            ApplyDamage(wireAmount, false, nativeDamageTime, advanceBeforeDamage);
        }

        private void ApplyDamage(uint wireAmount, bool updateClientSync, float? nativeDamageTime, bool advanceBeforeDamage)
        {
            if (advanceBeforeDamage && (nativeDamageTime.HasValue || _clientSyncHPTime >= 0f))
                AdvanceClientSyncHP(nativeDamageTime ?? _clientSyncHPTime, updateClientSync ? "TakeDamage-pre" : "TakeRuntimeDamage-pre");
            if (HasAnyDamageImmunity)
            {
                Debug.LogError($"[TAKEDAMAGE] Immune: {wireAmount} ignored at hp={_currentHPWire}");
                return;
            }
            uint adjustedWireAmount = ApplyDamageTakenMod(wireAmount, DamageTakenMod);
            Debug.LogError($"[TAKEDAMAGE] Before: {_currentHPWire}, Subtracting: {adjustedWireAmount}");
            _currentHPWire = adjustedWireAmount > _currentHPWire ? 0 : _currentHPWire - adjustedWireAmount;
            HasClientHP = true;
            if (updateClientSync)
                SetClientSyncHP(_currentHPWire, resetNativeTickClock: nativeDamageTime.HasValue, nativeTime: nativeDamageTime);
            ApplyNativeDamageRegenCooldown(nativeDamageTime);
            Debug.LogError($"[TAKEDAMAGE] After: {_currentHPWire}");
        }

        private static uint ApplyDamageTakenMod(uint wireAmount, float damageTakenMod)
        {
            if (wireAmount == 0) return 0;
            if (damageTakenMod < 1f) return 0;
            double scaled = wireAmount * (double)damageTakenMod / 100.0;
            if (scaled >= uint.MaxValue) return uint.MaxValue;
            return (uint)scaled;
        }

        public void Heal(uint wireAmount)
        {
            _currentHPWire = Math.Min(_currentHPWire + wireAmount, MaxHPWire);
            HasClientHP = true;
            ClearObservedClientHP();
            SetClientSyncHP(_currentHPWire);
        }

        public void SetCurrentHP(uint wireHP, bool applyNativeDamageCooldown = false)
        {
            _currentHPWire = Math.Min(wireHP, MaxHPWire);
            HasClientHP = true;
            ClearObservedClientHP();
            SetClientSyncHP(_currentHPWire);
            if (applyNativeDamageCooldown && _currentHPWire < MaxHPWire)
                ApplyNativeDamageRegenCooldown();
        }

        public void SetClientReportedHP(uint wireHP, bool updateRuntimeHP = true)
        {
            ObserveClientHP(wireHP, "SetClientReportedHP");
        }

        public void ObserveClientHP(uint wireHP, string source)
        {
            uint clampedHP = Math.Min(wireHP, MaxHPWire);
            _hasObservedClientHP = true;
            _lastObservedClientHPWire = clampedHP;
            _lastObservedClientHPTime = Time.time;
            _lastObservedClientHPSource = source ?? "unknown";
            HasClientHP = true;
        }

        public void RestoreToFull()
        {
            _currentHPWire = MaxHPWire;
            _currentManaWire = MaxManaWire;
            HasClientHP = true;
            HasClientMana = true;
            _regenCooldown = 0;
            _manaRegenCooldown = 0;
            ClearObservedClientHP();
            SetClientSyncHP(_currentHPWire);
        }

        private void ClearObservedClientHP()
        {
            _hasObservedClientHP = false;
            _lastObservedClientHPWire = 0;
            _lastObservedClientHPTime = 0f;
            _lastObservedClientHPSource = null;
        }
        private void SetClientSyncHP(uint wireHP, bool resetNativeTickClock = true, float? nativeTime = null)
        {
            uint maxHP = MaxHPWire;
            _clientSyncHPWire = maxHP > 0 ? Math.Min(wireHP, maxHP) : wireHP;
            HasClientSyncHP = true;
            if (resetNativeTickClock)
            {
                _clientSyncHPTime = nativeTime ?? Time.time;
                _clientSyncHPCarry = 0d;
            }
        }

        private float ResolveClientSyncHPRegenPerSecond()
        {
            float classRegen = 1f;
            var gc = GCDatabase.Instance;
            if (gc != null && gc.IsLoaded)
            {
                string classBase = (_className ?? "Fighter").Trim();
                if (!classBase.EndsWith("Base", StringComparison.OrdinalIgnoreCase))
                    classBase += "Base";
                var classNode = gc.ResolveWithInheritance(classBase) ?? gc.ResolveWithInheritance($"avatar.classes.{classBase}");
                var desc = classNode?.GetChild("Description");
                if (desc != null)
                    classRegen = desc.GetFloat("HealthRegen", classRegen);
            }
            float globalRegen = GCDatabase.Instance.GetKnob("HeroHealthRegen", 2f);
            return Math.Max(0f, classRegen * globalRegen);
        }

        private float ResolveClientSyncManaRegenPerSecond()
        {
            float classRegen = 1f;
            var gc = GCDatabase.Instance;
            if (gc != null && gc.IsLoaded)
            {
                string classBase = (_className ?? "Fighter").Trim();
                if (!classBase.EndsWith("Base", StringComparison.OrdinalIgnoreCase))
                    classBase += "Base";
                var classNode = gc.ResolveWithInheritance(classBase) ?? gc.ResolveWithInheritance($"avatar.classes.{classBase}");
                var desc = classNode?.GetChild("Description");
                if (desc != null)
                {
                    classRegen = desc.GetFloat("ManaRegen", classRegen);
                    classRegen = desc.GetFloat("PowerRegen", classRegen);
                }
            }
            float globalRegen = GCDatabase.Instance.GetKnob("HeroPowerRegen", 3f);
            return Math.Max(0f, classRegen * globalRegen);
        }

        public void AdvanceClientSyncHP(float now, string source = null)
        {
            if (!HasClientSyncHP)
            {
                SetClientSyncHP(_currentHPWire, nativeTime: now);
                return;
            }

            uint maxHP = MaxHPWire;
            uint maxMana = MaxManaWire;
            if (maxHP > 0 && _clientSyncHPWire > maxHP)
                _clientSyncHPWire = maxHP;
            if (_currentHPWire > maxHP)
                _currentHPWire = maxHP;
            if (_currentManaWire > maxMana)
                _currentManaWire = maxMana;

            uint oldCurrentHP = _currentHPWire;
            uint oldSyncHP = _clientSyncHPWire;
            uint oldMana = _currentManaWire;
            ushort oldHPCooldown = _regenCooldown;
            ushort oldManaCooldown = _manaRegenCooldown;

            if (maxHP > 0)
            {
                uint runtimeHP = Math.Min(maxHP, _currentHPWire);
                _currentHPWire = runtimeHP;
                _clientSyncHPWire = runtimeHP;
            }

            if (maxHP == 0 || (_clientSyncHPWire == 0 && _currentHPWire == 0))
            {
                _clientSyncHPTime = now;
                _clientSyncHPCarry = 0d;
                return;
            }

            if (_clientSyncHPTime < 0f)
            {
                _clientSyncHPTime = now;
                _clientSyncHPCarry = 0d;
                if (oldCurrentHP != _currentHPWire || oldSyncHP != _clientSyncHPWire)
                    Debug.LogError($"[PLAYER-REGEN] source={source ?? "init"} hp={oldCurrentHP}->{_currentHPWire} sync={oldSyncHP}->{_clientSyncHPWire} mana={oldMana}->{_currentManaWire}/{maxMana} hpCooldown={oldHPCooldown}->{_regenCooldown} manaCooldown={oldManaCooldown}->{_manaRegenCooldown} ticks=0 hpFactor={_regenFactor} manaFactor={_manaRegenFactor}");
                return;
            }

            const double nativeTickSeconds = 1d / 30d;
            double elapsed = now - _clientSyncHPTime + _clientSyncHPCarry;
            int ticks = (int)(elapsed / nativeTickSeconds);
            if (ticks <= 0)
            {
                _clientSyncHPCarry = elapsed;
                _clientSyncHPTime = now;
                if (oldCurrentHP != _currentHPWire || oldSyncHP != _clientSyncHPWire)
                    Debug.LogError($"[PLAYER-REGEN] source={source ?? "sync"} hp={oldCurrentHP}->{_currentHPWire} sync={oldSyncHP}->{_clientSyncHPWire} mana={oldMana}->{_currentManaWire}/{maxMana} hpCooldown={oldHPCooldown}->{_regenCooldown} manaCooldown={oldManaCooldown}->{_manaRegenCooldown} ticks=0 hpFactor={_regenFactor} manaFactor={_manaRegenFactor}");
                return;
            }

            if (_manaRegenFactor == 0)
                _manaRegenFactor = Mathf.RoundToInt(ResolveClientSyncManaRegenPerSecond());

            uint runtime = _currentHPWire;
            uint mana = _currentManaWire;
            for (int i = 0; i < ticks; i++)
            {
                if (_regenCooldown > 0)
                    _regenCooldown--;
                if (_manaRegenCooldown > 0)
                    _manaRegenCooldown--;

                if (runtime > 0 && runtime < maxHP && _regenCooldown == 0 && _regenFactor > 0)
                {
                    long regenDelta = ((long)_regenFactor * maxHP) / 3000L + 1L;
                    runtime = regenDelta >= maxHP - runtime ? maxHP : runtime + (uint)regenDelta;
                }

                if (maxMana > 0 && mana < maxMana && _manaRegenCooldown == 0 && _manaRegenFactor > 0)
                {
                    long regenDelta = ((long)_manaRegenFactor * maxMana) / 3000L + 1L;
                    mana = regenDelta >= maxMana - mana ? maxMana : mana + (uint)regenDelta;
                }
            }

            _currentHPWire = runtime;
            _clientSyncHPWire = runtime;
            _currentManaWire = mana;
            if (_currentHPWire > 0)
            {
                HasClientHP = true;
                HasClientSyncHP = true;
            }
            if (oldMana != _currentManaWire)
                HasClientMana = true;
            _clientSyncHPCarry = elapsed - ticks * nativeTickSeconds;
            _clientSyncHPTime = now;
            if (oldCurrentHP != _currentHPWire
                || oldSyncHP != _clientSyncHPWire
                || oldMana != _currentManaWire
                || (oldHPCooldown > 0 && _regenCooldown == 0)
                || (oldManaCooldown > 0 && _manaRegenCooldown == 0))
            {
                Debug.LogError($"[PLAYER-REGEN] source={source ?? "tick"} hp={oldCurrentHP}->{_currentHPWire} sync={oldSyncHP}->{_clientSyncHPWire} mana={oldMana}->{_currentManaWire}/{maxMana} hpCooldown={oldHPCooldown}->{_regenCooldown} manaCooldown={oldManaCooldown}->{_manaRegenCooldown} ticks={ticks} hpFactor={_regenFactor} manaFactor={_manaRegenFactor}");
            }
        }
        public void SetRegenFactor(int hitPointRegen, int hitPointRegenMod = 0, int hitPointRegenBonus = 0)
        {
            _regenFactor = (hitPointRegenMod + 100) * hitPointRegen / 100 + hitPointRegenBonus;
            _manaRegenFactor = Mathf.RoundToInt(ResolveClientSyncManaRegenPerSecond());
            Debug.LogError($"[REGEN] hpRegenFactor={_regenFactor} manaRegenFactor={_manaRegenFactor}");
        }

        public void SetRegenCooldown(ushort ticks)
        {
            _regenCooldown = ticks;
        }

        public void ApplyNativeDamageRegenCooldown(float? nativeDamageTime = null)
        {
            float cooldownStart = nativeDamageTime ?? (_clientSyncHPTime >= 0f ? _clientSyncHPTime : -NativeDamageRegenSuppressSeconds);
            _clientSyncRegenSuppressUntil = cooldownStart + NativeDamageRegenSuppressSeconds;
            _regenCooldown = NativeDamageRegenSuppressTicks;
            Debug.LogError($"[PLAYER-REGEN-COOLDOWN] hpCooldown={_regenCooldown} suppressUntil={_clientSyncRegenSuppressUntil:F3} hp={_currentHPWire}/{MaxHPWire}");
        }

        public void RunRegenTick()
        {
            AdvanceClientSyncHP(Time.time, "RunRegenTick");
        }

        public bool IsRegenComplete => (_currentHPWire >= MaxHPWire || _regenFactor == 0)
            && (_currentManaWire >= MaxManaWire || _manaRegenFactor == 0);
        public void LogFullState(string context)
        {
            Debug.LogError($"[PLAYERSTATE-{context}] {_className} L{_level} | Base:{_baseHPWire} + Alloc:{_allocatedHPBonusWire} + Equip:{_equipmentHPBonusWire} + Mod:{_modifierHPBonusWire} + Passive:{_passiveHPBonusWire} = {SynchHP}");
        }
    }
}
