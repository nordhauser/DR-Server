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
        public float WeaponDamage { get; set; } = 1.0f;
        public float WeaponDamageVolatility { get; set; } = 0.5f;
        public int WeaponLevel { get; set; } = 1;
        public string WeaponClass { get; set; } = "1HMELEE";
        public int WeaponRange { get; set; } = 8;       // From weapons.json range field (1H=8, 2H=16, ranged=20)
        public float WeaponCooldown { get; set; } = 1.75f; // From GC MeleeWeapon CoolDown default

        // Base stats from FighterBase/RangerBase/MageBase.gc — all start at 10
        public int Strength { get; set; } = 10;
        public int Agility { get; set; } = 10;
        public int Intelligence { get; set; } = 10;  // ADD THIS LINE
        public int Toughness { get; set; } = 10;
        public int Power { get; set; } = 10;

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
            if (targetLevel > ServerSettings.Get("maxLevel", 100)) return uint.MaxValue;

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
        /// Binary: CurveTable::Interpolate (Fixed16.16 precision) → >> 8 → * multiplier + 1
        /// Matches client thresholds: L2=51, L3=126, L4=226, L5=326, L6=581, L7=841, L8=1101...
        /// </summary>
        public static uint GetClientThreshold(int nextLevel)
        {
            const int MULTIPLIER = 5; // Patched from 100 in binary @ 0x4FAF85

            int target = nextLevel << 8; // Fixed8.8

            for (int i = 0; i < XPCurve.Length; i++)
            {
                int lvFixed = (int)XPCurve[i].level << 8;
                int valFixed = (int)XPCurve[i].value << 8;

                if (target <= lvFixed)
                {
                    if (i == 0)
                        return (uint)((int)XPCurve[i].value * MULTIPLIER + 1);

                    int prevLv = (int)XPCurve[i - 1].level << 8;
                    int prevVal = (int)XPCurve[i - 1].value << 8;
                    int delta = valFixed - prevVal;

                    // Client uses Fixed16.16 precision for t
                    int t = (int)((long)(target - prevLv) << 16) / (lvFixed - prevLv);
                    int interp = prevVal + (int)((long)delta * t >> 16);

                    return (uint)((interp >> 8) * MULTIPLIER + 1);
                }
            }
            return (uint)((int)XPCurve[XPCurve.Length - 1].value * MULTIPLIER + 1);
        }


        public bool AddExperience(uint xp)
        {
            Experience += xp;
            bool didLevel = false;
            uint needed = GetClientThreshold(_level + 1);
            while (Experience >= needed && _level < ServerSettings.Get("maxLevel", 100))
            {
                _level++;
                Experience -= needed;
                // Recalculate base HP and max mana for new level
                _baseHPWire = CalculateBaseHP();
                _baseManaWire = CalculateMaxMana();
                // Refill HP and MP to max on level-up (matches client Hero::onAddExperience)
                _currentHPWire = MaxHPWire;
                _currentManaWire = MaxManaWire;
                SetClientSyncHP(_currentHPWire);
                Debug.LogError($"[LEVEL-UP] Now level {_level}! HP refilled to {MaxHPWire / 256}, Mana refilled. Next: {GetClientThreshold(_level + 1)} XP");
                didLevel = true;
                needed = GetClientThreshold(_level + 1);
            }
            return didLevel;
        }

        private const uint BASE_HP_WIRE = 51200;
        // heroHealthPerLevel: 16 * 256 = 4096 wire. ServerSettings override.
        private static uint HP_PER_LEVEL_WIRE => (uint)(ServerSettings.Get("heroHealthPerLevel", 16) * 256);
        // healthPerEndurance from server.cfg
        private static int HP_PER_ENDURANCE => ServerSettings.Get("healthPerEndurance", 25);

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
        private const float NativeDamageRegenSuppressSeconds = 10f;
        private const ushort NativeDamageRegenSuppressTicks = 300;
        private uint _baseManaWire = 0;
        private uint _equipmentManaBonusWire = 0;
        private int _passiveManaBonusWire = 0;
        private uint _currentManaWire = 0;

        // All equipment stat bonuses (human-readable values, not wire format)
        // Populated by CalculateEquipmentBonuses from ItemStatDatabase
        public Dictionary<string, int> EquipmentStats { get; private set; } = new Dictionary<string, int>();

        private int _regenFactor = 0;
        private ushort _regenCooldown = 0;

        private uint CalculateBaseHP()
        {
            return BASE_HP_WIRE + (uint)(_level - 1) * HP_PER_LEVEL_WIRE;
        }

        private static uint ClampWire(long wire)
        {
            if (wire <= 0) return 0;
            if (wire >= uint.MaxValue) return uint.MaxValue;
            return (uint)wire;
        }

        public void SetCurrentMana(uint wireMana)
        {
            _currentManaWire = Math.Min(wireMana, MaxManaWire);
            HasClientMana = true;
        }
        private uint CalculateMaxMana()
        {
            uint baseMana = 44800;  // 175 * 256 (verified: 175 + 9*5 = 220 at level 10)
            uint manaPerLevel = (uint)(ServerSettings.Get("powerPerLevel", 5) * 256);
            return baseMana + (uint)(_level - 1) * manaPerLevel;
        }

        public uint Op12HP => MaxHPWire;
        public uint Op12MaxHP => MaxHPWire;
        public uint SynchHP => HasClientSyncHP ? _clientSyncHPWire : _currentHPWire;

        public uint MaxHPWire => ClampWire((long)_baseHPWire + _allocatedHPBonusWire + _equipmentHPBonusWire + _modifierHPBonusWire + _passiveHPBonusWire);
        public uint CurrentHPWire => _currentHPWire;
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
        public byte UpdateNumber = 0;
        public bool IsDamageImmune { get; set; } = false;
        public bool IsZoneSpawnDamageImmune { get; set; } = false;
        public bool HasAnyDamageImmunity => IsDamageImmune || IsZoneSpawnDamageImmune;
        public bool IsInvisible { get; set; } = false;
        public bool HasClientHP { get; private set; } = false;
        public bool HasClientSyncHP { get; private set; } = false;
        public bool HasClientMana { get; private set; } = false;
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
            _baseHPWire = CalculateBaseHP();
            _allocatedHPBonusWire = 0;
            _equipmentHPBonusWire = 0;
            _modifierHPBonusWire = 0;
            _passiveHPBonusWire = 0;
            _currentHPWire = _baseHPWire;
            _clientSyncHPWire = _currentHPWire;
            _clientSyncHPTime = -1f;
            _clientSyncHPCarry = 0d;
            _clientSyncRegenSuppressUntil = -1f;
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
            uint oldMaxHP = MaxHPWire;
            bool wasAtFullHP = _currentHPWire >= oldMaxHP;
            int finalStrength = 10 + Math.Max(0, strength);
            int finalAgility = 10 + Math.Max(0, agility);
            int finalEndurance = 10 + Math.Max(0, endurance);
            int finalIntellect = 10 + Math.Max(0, intellect);
            string classKey = (_className ?? "Fighter").Trim();
            if (classKey.EndsWith("Base", StringComparison.OrdinalIgnoreCase))
                classKey = classKey.Substring(0, classKey.Length - 4);
            if (classKey.Equals("Warlock", StringComparison.OrdinalIgnoreCase))
                classKey = "Mage";
            if (ClassPassiveData.Passives.TryGetValue(classKey, out var passive))
            {
                finalStrength += passive.StrengthMod;
                finalAgility += passive.AgilityMod;
                finalEndurance += passive.EnduranceMod;
                finalIntellect += passive.IntellectMod;
            }
            Strength = Math.Max(1, finalStrength);
            Agility = Math.Max(1, finalAgility);
            Toughness = Math.Max(1, finalEndurance);
            Intelligence = Math.Max(1, finalIntellect);
            Power = Intelligence;
            _allocatedHPBonusWire = CalculateAllocatedEnduranceHPBonusWire(endurance);

            if (!HasClientHP || wasAtFullHP || _currentHPWire > MaxHPWire)
                _currentHPWire = MaxHPWire;

            SetClientSyncHP(_currentHPWire);
            Debug.LogError($"[ALLOC-STATS] STR={strength}->{Strength} AGI={agility}->{Agility} END={endurance}->{Toughness} INT={intellect}->{Intelligence} AllocHP={_allocatedHPBonusWire} MaxHP={MaxHPWire} CurrentHP={_currentHPWire}");
        }

        private uint CalculateAllocatedEnduranceHPBonusWire(int endurance)
        {
            if (endurance <= 0) return 0;
            double hpPerEndurance = HP_PER_ENDURANCE;
            double multiplier = ResolveHealthPerEnduranceMultiplier();
            long hp = (long)Math.Floor(endurance * hpPerEndurance * multiplier + 0.5d);
            return ClampWire(hp * 256L);
        }

        private double ResolveHealthPerEnduranceMultiplier()
        {
            string classKey = (_className ?? "Fighter").Trim();
            if (classKey.EndsWith("Base", StringComparison.OrdinalIgnoreCase))
                classKey = classKey.Substring(0, classKey.Length - 4);
            if (classKey.Equals("Warlock", StringComparison.OrdinalIgnoreCase))
                classKey = "Mage";
            if (ClassPassiveData.Passives.TryGetValue(classKey, out var passive))
                return Math.Max(0d, (100d + passive.HealthPerEnduranceMod) / 100d);
            return 1d;
        }

        public void AddTotalHealthBonus(int hpBonus)
        {
            uint wireBonus = (uint)(hpBonus * 256);
            _equipmentHPBonusWire += wireBonus;
        }

        public void AddEnduranceBonus(int enduranceBonus)
        {
            int hpFromEndurance = enduranceBonus * HP_PER_ENDURANCE;
            uint wireBonus = (uint)(hpFromEndurance * 256);
            _equipmentHPBonusWire += wireBonus;
        }

        public void AddModifierHPBonus(uint wireBonus)
        {
            _modifierHPBonusWire += wireBonus;
        }

        public void SetPassiveBonuses(int hpWireBonus, int manaWireBonus)
        {
            _passiveHPBonusWire = hpWireBonus;
            _passiveManaBonusWire = manaWireBonus;
            if (HasClientHP)
            {
                if (_currentHPWire > MaxHPWire) _currentHPWire = MaxHPWire;
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
            EquipmentStats.Clear();
        }

        public void AddManaBonus(int manaBonus)
        {
            uint wireBonus = (uint)(manaBonus * 256);
            _equipmentManaBonusWire += wireBonus;
        }

        // Intellect → Mana conversion: 17 mana per intellect point (verified from client)
        public void AddIntellectManaBonus(int intellectBonus)
        {
            int manaFromInt = intellectBonus * 17;
            uint wireBonus = (uint)(manaFromInt * 256);
            _equipmentManaBonusWire += wireBonus;
            Debug.LogError($"[MANA-INT] INT+{intellectBonus} → +{manaFromInt} mana (wire +{wireBonus})");
        }

        public void RecalculateCurrentHP()
        {
            uint newMaxHP = MaxHPWire;
            if (HasClientHP)
            {
                if (_currentHPWire > newMaxHP)
                    _currentHPWire = newMaxHP;
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
            if (HasAnyDamageImmunity)
            {
                Debug.LogError($"[TAKEDAMAGE] Immune: {wireAmount} ignored at hp={_currentHPWire}");
                return;
            }
            Debug.LogError($"[TAKEDAMAGE] Before: {_currentHPWire}, Subtracting: {wireAmount}");
            _currentHPWire = wireAmount > _currentHPWire ? 0 : _currentHPWire - wireAmount;
            HasClientHP = true;
            SetClientSyncHP(_currentHPWire);
            _clientSyncRegenSuppressUntil = Time.time + NativeDamageRegenSuppressSeconds;
            _regenCooldown = NativeDamageRegenSuppressTicks;
            Debug.LogError($"[TAKEDAMAGE] After: {_currentHPWire}");
        }

        public void Heal(uint wireAmount)
        {
            _currentHPWire = Math.Min(_currentHPWire + wireAmount, MaxHPWire);
            HasClientHP = true;
            SetClientSyncHP(_currentHPWire);
        }

        public void SetCurrentHP(uint wireHP)
        {
            _currentHPWire = Math.Min(wireHP, MaxHPWire);
            HasClientHP = true;
            SetClientSyncHP(_currentHPWire);
        }

        public void SetClientReportedHP(uint wireHP)
        {
            uint clampedHP = Math.Min(wireHP, MaxHPWire);
            uint previousHP = _currentHPWire;
            uint previousSyncHP = HasClientSyncHP ? _clientSyncHPWire : _currentHPWire;
            bool hpWasLowered = clampedHP < previousHP || clampedHP < previousSyncHP;
            _currentHPWire = clampedHP;
            HasClientHP = true;
            SetClientSyncHP(_currentHPWire);
            _clientSyncRegenSuppressUntil = hpWasLowered
                ? Time.time + NativeDamageRegenSuppressSeconds
                : _clientSyncHPTime;
            if (hpWasLowered)
                _regenCooldown = NativeDamageRegenSuppressTicks;
        }

        public void RestoreToFull()
        {
            _currentHPWire = MaxHPWire;
            _currentManaWire = MaxManaWire;
            SetClientSyncHP(_currentHPWire);
        }
        private void SetClientSyncHP(uint wireHP)
        {
            uint maxHP = MaxHPWire;
            _clientSyncHPWire = maxHP > 0 ? Math.Min(wireHP, maxHP) : wireHP;
            HasClientSyncHP = true;
            _clientSyncHPTime = Time.time;
            _clientSyncHPCarry = 0d;
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
            float globalRegen = ServerSettings.GetFloat("heroHealthRegen", GCDatabase.Instance.GetKnob("HeroHealthRegen", 2f));
            return Math.Max(0f, classRegen * globalRegen);
        }

        public void AdvanceClientSyncHP(float now)
        {
            if (!HasClientSyncHP)
            {
                SetClientSyncHP(_currentHPWire);
                return;
            }

            uint maxHP = MaxHPWire;
            if (maxHP == 0)
            {
                _clientSyncHPTime = now;
                _clientSyncHPCarry = 0d;
                return;
            }
            if (maxHP > 0 && _clientSyncHPWire > maxHP)
                _clientSyncHPWire = maxHP;
            if (_currentHPWire > maxHP)
                _currentHPWire = maxHP;
            if (now <= _clientSyncHPTime || _currentHPWire >= maxHP || _regenFactor == 0)
            {
                _clientSyncHPTime = now;
                _clientSyncHPCarry = 0d;
                return;
            }

            const double nativeTickSeconds = 1d / 30d;
            double elapsed = now - _clientSyncHPTime + _clientSyncHPCarry;
            int ticks = (int)(elapsed / nativeTickSeconds);
            if (ticks <= 0)
            {
                _clientSyncHPCarry = elapsed;
                _clientSyncHPTime = now;
                return;
            }

            for (int i = 0; i < ticks && _currentHPWire < maxHP; i++)
            {
                if (_regenCooldown > 0)
                {
                    _regenCooldown--;
                    continue;
                }

                long regenDelta = ((long)_regenFactor * maxHP) / 3000L + 1L;
                long newHP = (long)_currentHPWire + regenDelta;
                _currentHPWire = newHP > maxHP ? maxHP : (uint)newHP;
            }

            _clientSyncHPWire = _currentHPWire;
            _clientSyncHPCarry = elapsed - ticks * nativeTickSeconds;
            _clientSyncHPTime = now;
        }
        public void SetRegenFactor(int hitPointRegen, int hitPointRegenMod = 0, int hitPointRegenBonus = 0)
        {
            _regenFactor = (hitPointRegenMod + 100) * hitPointRegen / 100 + hitPointRegenBonus;
            Debug.LogError($"[REGEN] regenFactor={_regenFactor}");
        }

        public void SetRegenCooldown(ushort ticks)
        {
            _regenCooldown = ticks;
        }

        public void RunRegenTick()
        {
            uint maxHP = MaxHPWire;
            if (_currentHPWire >= maxHP || _regenFactor == 0)
                return;

            if (_regenCooldown > 0)
            {
                _regenCooldown--;
                return;
            }

            long numerator = (long)_regenFactor * (long)maxHP;
            int regenDelta = (int)(numerator / 3000L);
            long newHP = (long)_currentHPWire + (long)regenDelta + 1;

            if (newHP > maxHP) newHP = maxHP;
            if (newHP < 0) newHP = 0;
            _currentHPWire = (uint)newHP;
            SetClientSyncHP(_currentHPWire);
        }

        public bool IsRegenComplete => _currentHPWire >= MaxHPWire || _regenFactor == 0;
        public void LogFullState(string context)
        {
            Debug.LogError($"[PLAYERSTATE-{context}] {_className} L{_level} | Base:{_baseHPWire} + Alloc:{_allocatedHPBonusWire} + Equip:{_equipmentHPBonusWire} + Mod:{_modifierHPBonusWire} + Passive:{_passiveHPBonusWire} = {SynchHP}");
        }
    }
}
