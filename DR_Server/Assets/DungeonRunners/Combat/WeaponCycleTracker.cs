using System;
using System.Collections.Generic;
using UnityEngine;
using DungeonRunners.Data;
using DungeonRunners.Networking;
using DungeonRunners.Core;

namespace DungeonRunners.Combat
{
    /// <summary>
    /// Tracks weapon cycle timing so combat RNG fires at the correct tick positions.
    /// 
    /// MeleeWeapon::update at 0x591980:
    ///   Default animation positions: total=30(0x1E), proc=10(0xA), hit=15(0xF)
    ///   Normalized by CoolDown field [edi+0x90] (CoolDown*100):
    ///     position = (default * 100) / CoolDown_field
    ///   For 1.75s weapons: [0x90]=175, total=17, proc=5, hit=8
    ///   
    ///   UseTarget fires at elapsed=1 (countdown=total-1)
    ///   Proc fires at elapsed=proc_position  
    ///   Hit fires at elapsed=hit_position
    ///
    ///   From UseTarget arrival (elapsed=1):
    ///     Ticks to proc = proc_position - 1
    ///     Ticks to hit  = hit_position - 1
    ///
    /// For 1.75s weapons (all player melee from GC):
    ///   TICKS_TO_PROC = 5 - 1 = 4
    ///   TICKS_TO_HIT  = 8 - 1 = 7
    ///   Full cycle = 17 server ticks
    ///
    /// </summary>
    public class WeaponCycleTracker
    {
        private static WeaponCycleTracker _instance;
        public static WeaponCycleTracker Instance => _instance ??= new WeaponCycleTracker();

        private Dictionary<string, WeaponCycle> _activeCycles = new Dictionary<string, WeaponCycle>();
        private Queue<CompletedAttack> _completedAttacks = new Queue<CompletedAttack>();

        // Binary 0x591980: defaults 30/10/15 normalized by CoolDown*100
        // For 1.75s (all player weapons): (10*100)/175=5, (15*100)/175=8
        // From UseTarget (elapsed=1): proc at 5-1=4, hit at 8-1=7
        private const int TICKS_TO_PROC = 4;
        private const int TICKS_TO_HIT = 7;
        private const int DEFAULT_FULL_CYCLE = 17;

        public void RegisterAttack(string connKey, ushort targetId, Monster monster,
            PlayerState playerState, RRConnection conn, bool canStartNow = true, float distance = 0f, float allowedRange = 0f)
        {
            if (!_activeCycles.TryGetValue(connKey, out var cycle))
            {
                cycle = new WeaponCycle();
                _activeCycles[connKey] = cycle;
            }

            bool sameMonster = cycle.Monster != null && monster != null && cycle.Monster.EntityId == monster.EntityId;
            bool sameTarget = cycle.TargetId == targetId && sameMonster;
            if ((cycle.IsActive || cycle.AwaitingContact) && sameTarget)
            {
                cycle.Monster = monster;
                cycle.PlayerState = playerState;
                cycle.Connection = conn;
                cycle.Distance = distance;
                cycle.ContactRange = allowedRange;
                if (canStartNow && cycle.AwaitingContact)
                {
                    BeginCycle(connKey, cycle, monster, targetId);
                    Debug.LogError($"[WEAPON-CYCLE] {connKey} → CONTACT cycle on {monster.Name} dist={distance:F1} range={allowedRange:F1}");
                }
                else
                {
                    string mode = cycle.AwaitingContact ? "approach" : "continuation swing";
                    Debug.LogError($"[WEAPON-CYCLE] {connKey} → {mode} on {monster.Name}");
                }
                return;
            }

            cycle.TargetId = targetId;
            cycle.Monster = monster;
            cycle.PlayerState = playerState;
            cycle.Connection = conn;
            cycle.TickCounter = 0;
            ResetSwingRngState(cycle);
            cycle.SwingCount = 0;
            cycle.Distance = distance;
            cycle.ContactRange = allowedRange;
            cycle.LastTickTime = Time.time;
            cycle.CycleStartTime = 0f;

            if (canStartNow)
            {
                BeginCycle(connKey, cycle, monster, targetId);
                Debug.LogError($"[WEAPON-CYCLE] {connKey} → NEW cycle on {monster.Name} (ID:{targetId})");
            }
            else
            {
                cycle.IsActive = false;
                cycle.AwaitingContact = true;
                Debug.LogError($"[WEAPON-CYCLE] {connKey} → APPROACH intent on {monster.Name} (ID:{targetId}) dist={distance:F1} range={allowedRange:F1}");
            }
        }

        public Monster GetActiveTarget(string playerKey)
        {
            if (_activeCycles.TryGetValue(playerKey, out var cycle) && (cycle.IsActive || cycle.AwaitingContact))
                return cycle.Monster;
            return null;
        }

        public void TickAll(MersenneTwister rng)
        {
            foreach (var kvp in _activeCycles)
            {
                TickCycle(kvp.Key, kvp.Value, rng);
            }
        }

        public void FlushMonsterBeforeSync(Monster monster, MersenneTwister rng)
        {
            if (monster == null || rng == null) return;
            foreach (var kvp in _activeCycles)
            {
                var cycle = kvp.Value;
                if (cycle == null || cycle.Monster == null || monster == null || cycle.Monster.EntityId != monster.EntityId) continue;
                cycle.Monster = monster;
                if (!cycle.IsActive && !cycle.AwaitingContact) continue;
                float interval = cycle.AwaitingContact ? 1f / 30f : GetCycleTickInterval(cycle);
                if (cycle.LastTickTime <= 0f || Time.time - cycle.LastTickTime >= interval)
                    TickCycle(kvp.Key, cycle, rng);
            }
        }

        private float GetCycleTickInterval(WeaponCycle cycle)
        {
            float cooldown = cycle?.PlayerState != null ? cycle.PlayerState.WeaponCooldown : 1.75f;
            if (cooldown <= 0.1f) cooldown = 1.75f;
            return Mathf.Max(1f / 30f, cooldown / DEFAULT_FULL_CYCLE);
        }

        private void BeginCycle(string connKey, WeaponCycle cycle, Monster monster, ushort targetId)
        {
            cycle.IsActive = true;
            cycle.AwaitingContact = false;
            cycle.TargetId = targetId;
            cycle.Monster = monster;
            cycle.TickCounter = 0;
            ResetSwingRngState(cycle);
            cycle.LastTickTime = Time.time;
            cycle.CycleStartTime = Time.time;
            ConsumeNativeUseRng(connKey, cycle, CombatManager.Instance.RoomRng);
        }

        private void ResetSwingRngState(WeaponCycle cycle)
        {
            if (cycle == null) return;
            cycle.ProcFired = false;
            cycle.HitFired = false;
            cycle.AttackSoundFired = false;
            cycle.UseRngConsumed = false;
            cycle.UseRaw = 0;
            cycle.AttackSoundSelectRaw = 0;
            cycle.AttackSoundGateRaw = 0;
            cycle.ImpactSoundSelectRaw = 0;
        }

        private void ConsumeNativeUseRng(string connKey, WeaponCycle cycle, MersenneTwister rng)
        {
            if (cycle == null || rng == null || cycle.UseRngConsumed) return;
            cycle.UseRaw = rng.Generate();
            cycle.UseRngConsumed = true;
            Debug.LogError($"[RNG-COMBAT] {connKey} MeleeWeapon::use useRaw=0x{cycle.UseRaw:X8} animBit={cycle.UseRaw & 1u} rngPos={rng.CallsSinceReseed}");
        }

        private void TickCycle(string connKey, WeaponCycle cycle, MersenneTwister rng)
        {
            if (cycle == null) return;
            if (cycle.Monster == null || !cycle.Monster.IsAlive)
            {
                cycle.IsActive = false;
                cycle.AwaitingContact = false;
                return;
            }

            if (cycle.AwaitingContact)
            {
                if (cycle.Connection != null && cycle.Connection.HasActiveUseTarget && cycle.Connection.ActiveUseTargetId != cycle.TargetId)
                {
                    cycle.IsActive = false;
                    cycle.AwaitingContact = false;
                    return;
                }
                if (!HasNativePlayerMeleeContact(cycle, out float dist, out float range))
                {
                    cycle.Distance = dist;
                    cycle.ContactRange = range;
                    cycle.LastTickTime = Time.time;
                    return;
                }
                cycle.Distance = dist;
                cycle.ContactRange = range;
                BeginCycle(connKey, cycle, cycle.Monster, cycle.TargetId);
                Debug.LogError($"[WEAPON-CYCLE] {connKey} → CONTACT cycle on {cycle.Monster.Name} dist={dist:F1} range={range:F1}");
            }

            if (!cycle.IsActive) return;

            float tickInterval = GetCycleTickInterval(cycle);
            if (cycle.LastTickTime > 0f && Time.time - cycle.LastTickTime < tickInterval) return;
            float tickNow = Time.time;
            cycle.LastTickTime = tickNow;
            cycle.TickCounter++;

                if (cycle.TickCounter == TICKS_TO_PROC && !cycle.ProcFired)
                {
                    cycle.ProcFired = true;
                    cycle.AttackSoundFired = true;
                    if (rng != null)
                    {
                        cycle.AttackSoundSelectRaw = rng.Generate();
                        cycle.AttackSoundGateRaw = rng.Generate();
                        Debug.LogError($"[WEAPON-CYCLE] {connKey} SOUND tick={cycle.TickCounter} soundSelect=0x{cycle.AttackSoundSelectRaw:X8} soundGate=0x{cycle.AttackSoundGateRaw:X8} repeat={(cycle.AttackSoundGateRaw & 3u) == 0u} rngPos={rng.CallsSinceReseed}");
                    }
                    else
                    {
                        Debug.LogError($"[WEAPON-CYCLE] {connKey} SOUND tick={cycle.TickCounter} rng=null");
                    }
                }

                if (cycle.TickCounter == TICKS_TO_HIT && !cycle.HitFired)
                {
                    cycle.HitFired = true;
                    cycle.SwingCount++;

                    ConsumeNativeUseRng(connKey, cycle, rng);
                    uint useRaw = cycle.UseRaw;

                    uint hitRaw = rng.Generate();
                    int hitRoll = (int)(hitRaw % 25700);

                    uint blockRaw = rng.Generate();
                    int blockRoll = (int)(((blockRaw >> 8) & 0xFF) % 100) + 1;

                    int hitChanceF32 = 0x5A00;
                    bool isHit = hitRoll < hitChanceF32;
                    bool isBlocked = isHit && blockRoll <= 0;

                    Debug.LogError($"[RNG-COMBAT] swing#{cycle.SwingCount} seed=0x{rng.LastSeed:X8} rngPos={rng.CallsSinceReseed} useRaw=0x{useRaw:X8} soundSelect=0x{cycle.AttackSoundSelectRaw:X8} soundGate=0x{cycle.AttackSoundGateRaw:X8} hitRaw=0x{hitRaw:X8} blockRaw=0x{blockRaw:X8} hitRoll={hitRoll} isHit={isHit}");

                    int damage = 0;
                    if (isHit && !isBlocked)
                    {
                        int weaponDmg = DamageComputer.GetWeaponBaseDamageF32(cycle.PlayerState);
                        int volatility = DamageComputer.GetWeaponVolatilityF32(cycle.PlayerState);

                        var gc = GCDatabase.Instance;
                        float weaponDmgPerLevel = ServerSettings.GetFloat("weaponDamagePerLevel", gc.GetKnob("WeaponDamagePerLevel", 10f));
                        float meleeDmgPerStr = ServerSettings.GetFloat("meleeDamagePerStrength", gc.GetKnob("MeleeDamagePerStrength", 2.3364f));
                        float playerStrength = (float)cycle.PlayerState.Strength;
                        int weaponLevel = Math.Max(1, cycle.PlayerState.WeaponLevel);
                        float baseDamage = (weaponDmgPerLevel * weaponLevel) + (meleeDmgPerStr * playerStrength);
                        int baseDmgF32 = DamageComputer.FromFloat(baseDamage);
                        int normalized = DamageComputer.FixedMul(baseDmgF32, weaponDmg);
                        if (normalized < 0x100) normalized = 0x100;
                        int spread = DamageComputer.FixedMul(normalized, volatility);
                        int minDmg = DamageComputer.RoundFixed32(normalized - spread);
                        int maxDmg = DamageComputer.RoundFixed32(normalized + spread);
                        if (minDmg < 0x100) minDmg = 0x100;
                        if (maxDmg < 0x100) maxDmg = 0x100;
                        minDmg = (minDmg >> 8) << 8;
                        maxDmg = (maxDmg >> 8) << 8;

                        uint damageRaw = rng.Generate();
                        int range = Math.Max(0, maxDmg - minDmg + 1);
                        if (range > 0)
                            damage = (int)(damageRaw % (uint)range) + minDmg;
                        else
                            damage = minDmg;

                        Debug.LogError($"[RNG-COMBAT] dmgRaw=0x{damageRaw:X8} dmg={damage / 256} range=[{minDmg / 256},{maxDmg / 256}] str={cycle.PlayerState.Strength} level={cycle.PlayerState.Level} weaponLevel={weaponLevel} weapon={DamageComputer.ToFloat(weaponDmg):F2} vol={DamageComputer.ToFloat(volatility):F2} roomRng={rng.CallsSinceReseed}");

                        uint damageWire = (uint)Math.Max(1, damage);
                        uint oldHPWire = cycle.Monster.CurrentHPWire;
                        int actualDamage = (int)((damageWire + 255) / 256);
                        uint playerEntityId = cycle.Connection?.Avatar != null ? (uint)cycle.Connection.Avatar.Id : 0u;

                        CombatManager.Instance.NotifyMonsterDamagedByPlayer(cycle.Monster, playerEntityId, "damage");
                        float nativeHitTime = cycle.CycleStartTime > 0f
                            ? cycle.CycleStartTime + (GetCycleTickInterval(cycle) * TICKS_TO_HIT)
                            : tickNow;
                        bool applied = CombatManager.Instance.ApplyNativePlayerDamageToMonsterWire(cycle.Monster, damageWire, $"WeaponCycle-HIT swing={cycle.SwingCount}", out oldHPWire, out uint newHPWire, out bool killed, nativeHitTime);
                        int appliedDamage = (int)((oldHPWire > newHPWire ? oldHPWire - newHPWire : 0) + 255) / 256;

                        Debug.LogError($"[WEAPON-CYCLE] {connKey} HIT: {actualDamage} dmgWire={damageWire} applied={applied} appliedDamage={appliedDamage} on {cycle.Monster.Name} HP={oldHPWire}->{newHPWire} [swing #{cycle.SwingCount}]");
                        if (applied && rng != null)
                        {
                            cycle.ImpactSoundSelectRaw = rng.Generate();
                            Debug.LogError($"[RNG-COMBAT] swing#{cycle.SwingCount} impactSoundSelect=0x{cycle.ImpactSoundSelectRaw:X8} rngPos={rng.CallsSinceReseed}");
                        }
                        if (killed && cycle.Monster != null)
                        {
                            _completedAttacks.Enqueue(new CompletedAttack
                            {
                                Connection = cycle.Connection,
                                Monster = cycle.Monster,
                                DamageDealt = appliedDamage,
                                Killed = true
                            });
                            cycle.IsActive = false;
                        }
                    }
                    else
                    {
                        string resultType = !isHit ? "MISS" : "BLOCK";
                        Debug.LogError($"[WEAPON-CYCLE] {connKey} {resultType}: hitRoll={hitRoll} blockRoll={blockRoll} on {cycle.Monster.Name} [swing #{cycle.SwingCount}]");
                    }
                }

                if (cycle.TickCounter >= DEFAULT_FULL_CYCLE)
                {
                    cycle.TickCounter = 0;
                    ResetSwingRngState(cycle);
                    cycle.CycleStartTime = Time.time;
                    ConsumeNativeUseRng(connKey, cycle, rng);
                    Debug.LogError($"[WEAPON-CYCLE] {connKey} → NEXT cycle on {cycle.Monster?.Name ?? "monster"}");
                }
        }


        private bool HasNativePlayerMeleeContact(WeaponCycle cycle, out float distance, out float range)
        {
            distance = float.MaxValue;
            range = 0f;
            if (cycle == null || cycle.Connection == null || cycle.Monster == null) return false;
            float dx = cycle.Monster.PosX - cycle.Connection.PlayerPosX;
            float dy = cycle.Monster.PosY - cycle.Connection.PlayerPosY;
            distance = Mathf.Sqrt(dx * dx + dy * dy);
            range = CombatManager.Instance.ResolvePlayerMeleeNativeContactRange(cycle.PlayerState, cycle.Monster);
            return range > 0f && distance <= range;
        }

        public CompletedAttack DequeueKill()
        {
            return _completedAttacks.Count > 0 ? _completedAttacks.Dequeue() : null;
        }

        public bool HasPendingKills => _completedAttacks.Count > 0;

        public void ClearConnection(string connKey)
        {
            _activeCycles.Remove(connKey);
        }

        public void Clear()
        {
            _activeCycles.Clear();
            _completedAttacks.Clear();
        }
    }

    public class WeaponCycle
    {
        public bool IsActive;
        public ushort TargetId;
        public Monster Monster;
        public PlayerState PlayerState;
        public RRConnection Connection;
        public int TickCounter;
        public float CycleStartTime;
        public bool ProcFired;
        public bool HitFired;
        public bool AttackSoundFired;
        public bool UseRngConsumed;
        public uint UseRaw;
        public uint AttackSoundSelectRaw;
        public uint AttackSoundGateRaw;
        public uint ImpactSoundSelectRaw;
        public int SwingCount;
        public bool AwaitingContact;
        public float Distance;
        public float ContactRange;
        public float LastTickTime;
    }

    public class CompletedAttack
    {
        public string ConnKey;
        public RRConnection Connection;
        public Monster Monster;
        public bool Killed;
        public int DamageDealt;
    }
}
