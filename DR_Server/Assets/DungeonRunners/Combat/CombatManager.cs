using System;
using System.Collections.Generic;
using UnityEngine;
using DungeonRunners.Networking;
using DungeonRunners.Core;
using DungeonRunners.Data;
using System.Linq;
using System.IO;
namespace DungeonRunners.Combat
{
    public class CombatManager
    {
        private static CombatManager _instance;
        public static CombatManager Instance => _instance ??= new CombatManager();

        private Dictionary<uint, Monster> _activeMonsters = new Dictionary<uint, Monster>();
        private Dictionary<uint, CombatPlayer> _players = new Dictionary<uint, CombatPlayer>();
        private Dictionary<uint, float> _playerCombatAdvanceTime = new Dictionary<uint, float>();
        private List<RespawnEntry> _respawnQueue = new List<RespawnEntry>();
        private Dictionary<uint, uint> _monsterRuntimeHPWire = new Dictionary<uint, uint>();
        private HashSet<uint> _monsterRuntimeDamageCommitted = new HashSet<uint>();
        private Dictionary<uint, float> _monsterHPRegenLastTime = new Dictionary<uint, float>();
        private Dictionary<uint, float> _monsterHPRegenCarryWire = new Dictionary<uint, float>();

        // Maps ANY component ID (EntityId, BehaviorId, SkillsId, etc.) to the monster's EntityId
        private Dictionary<uint, uint> _componentToEntityMap = new Dictionary<uint, uint>();

        // Maps client-sent target IDs to our server entity IDs (learned at runtime)
        private Dictionary<uint, uint> _clientToServerIdMap = new Dictionary<uint, uint>();

        private uint _nextMonsterId = 50000;
        private float? _avatarCombatRadius;

        public event Action<Monster> OnMonsterSpawned;
        public event Action<uint> OnMonsterDespawned;
        public event Action<DamageEvent> OnDamageDealt;
        public event Action<uint, uint> OnEntityDeath;
        public event Action<Monster> OnMonsterPositionChanged;
        public event Action<Monster, CombatPlayer, byte> OnMonsterAttackStarted;

        // OnMonsterAttack and OnMonsterAggro REMOVED.
        // Client is authoritative on combat — sends type 9 for aggro.
        // Server-side aggro used monster.PosX which was always spawn position (never updated).

        public uint AllocateComponentId()
        {
            return _nextMonsterId++;
        }
        public CombatManager()
        {
            Debug.LogError("[CombatManager] Initialized");
            // InitializeRoomRng((uint)System.Environment.TickCount);
            Debug.LogError($"[CombatManager] Room RNG initialized with seed 0x{_roomSeed:X8}");
        }
        public IEnumerable<Monster> GetMonstersInZone(string zoneName)
        {
            return _activeMonsters.Values.Where(m => string.Equals(m.ZoneName, zoneName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Removes all monsters tagged with the given zone name (or any instance of it).
        /// Matches both exact "dungeon00_level01" and instanced "dungeon00_level01_inst0".
        /// Used when @behavior changes mode so mobs respawn with new behavior on next zone entry.
        /// </summary>
        public int ClearZoneMobs(string zoneName)
        {
            var toRemove = _activeMonsters.Values
                .Where(m => m.ZoneName != null && (
                    string.Equals(m.ZoneName, zoneName, StringComparison.OrdinalIgnoreCase) ||
                    m.ZoneName.StartsWith(zoneName + "_inst", StringComparison.OrdinalIgnoreCase)))
                .Select(m => m.EntityId)
                .ToList();

            foreach (uint eid in toRemove)
            {
                if (_activeMonsters.TryGetValue(eid, out var mon))
                {
                    _componentToEntityMap.Remove(mon.BehaviorId);
                    _componentToEntityMap.Remove(mon.SkillsId);
                    _componentToEntityMap.Remove(mon.ManipulatorsId);
                    _componentToEntityMap.Remove(mon.ModifiersId);
                    _componentToEntityMap.Remove(mon.UnitId);
                    _componentToEntityMap.Remove(eid);
                    _monsterRuntimeHPWire.Remove(eid);
                    _monsterRuntimeDamageCommitted.Remove(eid);
                    _monsterHPRegenLastTime.Remove(eid);
                    _monsterHPRegenCarryWire.Remove(eid);
                    WanderSimulator.Instance.UnregisterEntity(eid);
                }
                _activeMonsters.Remove(eid);
            }

            Debug.LogError($"[BEHAVIOR] ClearZoneMobs('{zoneName}'): removed {toRemove.Count} monsters");
            return toRemove.Count;
        }
        public CombatPlayer RegisterPlayer(uint entityId, string name, PlayerState state, float posX, float posY)
        {
            var player = new CombatPlayer
            {
                EntityId = entityId,
                Name = name,
                PlayerState = state,
                PosX = posX,
                PosY = posY,
                IsAlive = true
            };

            _players[entityId] = player;
            _playerCombatAdvanceTime[entityId] = Time.time;
            Debug.LogError($"[Combat] Registered player {name} (ID:{entityId}) at pos=({posX:F1}, {posY:F1})");
            return player;
        }
        public string DumpPlayerIds()
        {
            var ids = string.Join(", ", _players.Keys);
            return $"[{ids}] (count={_players.Count})";
        }
        public void UpdatePlayerPosition(uint entityId, float posX, float posY)
        {
            if (_players.TryGetValue(entityId, out var player))
            {
                player.PosX = posX;
                player.PosY = posY;
            }
        }
        public void EngageMonsterFromClientAction(Monster monster, uint playerEntityId)
        {
            if (monster == null || !monster.IsAlive) return;
            if (!_players.TryGetValue(playerEntityId, out var player) || player == null) return;
            AggroMonster(monster, player, "client", false);
            float allowedRange = ResolveMonsterEffectiveAttackRange(monster);
            float dist = Distance2D(monster.PosX, monster.PosY, player.PosX, player.PosY);
            float contactRange = ResolveNativeClientContactRange(monster, player, allowedRange);
            bool inNativeContact = contactRange > 0f && dist <= contactRange;
            if (inNativeContact)
            {
                monster.CombatContactTargetId = player.EntityId;
                monster.CombatContactUntil = Time.time + Mathf.Max(0.25f, ResolveMonsterAttackWindup(monster) + 0.25f);
                Debug.LogError($"[MON-CONTACT] {monster.Name}#{monster.EntityId}->{player.Name} dist={dist:F1} range={allowedRange:F1} nativeRange={contactRange:F1}");
            }
            else if (monster.CombatContactTargetId == player.EntityId)
            {
                monster.CombatContactTargetId = 0;
                monster.CombatContactUntil = 0f;
            }
        }

        private uint PeekRuntimeMonsterHPWire(Monster monster)
        {
            if (monster == null) return 0;
            if (!_monsterRuntimeHPWire.TryGetValue(monster.EntityId, out uint hp))
            {
                hp = monster.CurrentHPWire;
                _monsterRuntimeHPWire[monster.EntityId] = hp;
            }
            if (hp > monster.MaxHPWire)
            {
                hp = monster.MaxHPWire;
                _monsterRuntimeHPWire[monster.EntityId] = hp;
            }
            if (monster.CurrentHPWire != hp) monster.CurrentHPWire = hp;
            if (_activeMonsters.TryGetValue(monster.EntityId, out var active) && active != monster && active.CurrentHPWire != hp)
                active.CurrentHPWire = hp;
            return hp;
        }

        private void SyncMonsterHPField(Monster monster, uint hp)
        {
            if (monster == null) return;
            if (hp > monster.MaxHPWire) hp = monster.MaxHPWire;
            monster.CurrentHPWire = hp;
            if (_activeMonsters.TryGetValue(monster.EntityId, out var active) && active != monster)
                active.CurrentHPWire = hp;
        }

        private float ResolveMonsterHealthRegenHPPerSecond()
        {
            float authoredGlobal = GCDatabase.Instance.GetKnob("MonsterHealthRegen", 2f);
            return Mathf.Max(0f, ServerSettings.GetFloat("monsterHealthRegen", authoredGlobal));
        }

        private void ResetMonsterHPRegenClock(Monster monster, float now)
        {
            if (monster == null) return;
            if (now < 0f) now = Time.time;
            _monsterHPRegenLastTime[monster.EntityId] = now;
            _monsterHPRegenCarryWire[monster.EntityId] = 0f;
        }

        private uint ApplyNativeMonsterHealthRegen(Monster monster, string source, float now = -1f)
        {
            if (monster == null) return 0;
            uint hp = PeekRuntimeMonsterHPWire(monster);
            if (now < 0f) now = Time.time;
            if (!monster.IsAlive || hp == 0 || monster.MaxHPWire == 0)
            {
                ResetMonsterHPRegenClock(monster, now);
                return hp;
            }
            if (hp >= monster.MaxHPWire)
            {
                ResetMonsterHPRegenClock(monster, now);
                return hp;
            }

            float regenHP = ResolveMonsterHealthRegenHPPerSecond();
            if (regenHP <= 0f)
            {
                ResetMonsterHPRegenClock(monster, now);
                return hp;
            }

            if (!_monsterHPRegenLastTime.TryGetValue(monster.EntityId, out float lastTime) || lastTime <= 0f)
            {
                _monsterHPRegenLastTime[monster.EntityId] = now;
                return hp;
            }

            float elapsed = now - lastTime;
            if (elapsed <= 0f) return hp;

            _monsterHPRegenCarryWire.TryGetValue(monster.EntityId, out float carryWire);
            float regenWireFloat = regenHP * 256f * elapsed + carryWire;
            uint regenWire = (uint)Mathf.Floor(regenWireFloat);
            float nextCarry = regenWireFloat - regenWire;

            _monsterHPRegenLastTime[monster.EntityId] = now;
            _monsterHPRegenCarryWire[monster.EntityId] = nextCarry;

            if (regenWire == 0) return hp;

            uint newHP = hp + regenWire;
            if (newHP >= monster.MaxHPWire)
            {
                newHP = monster.MaxHPWire;
                _monsterRuntimeDamageCommitted.Remove(monster.EntityId);
                _monsterHPRegenCarryWire[monster.EntityId] = 0f;
            }

            _monsterRuntimeHPWire[monster.EntityId] = newHP;
            SyncMonsterHPField(monster, newHP);
            Debug.LogError($"[MON-REGEN] {monster.Name}#{monster.EntityId} source={source ?? "unknown"} hp={hp / 256f:F2}->{newHP / 256f:F2}/{monster.MaxHPWire / 256f:F2} elapsed={elapsed:F3}s regen={regenWire / 256f:F2}");
            return newHP;
        }

        private uint GetRuntimeMonsterHPWire(Monster monster, string source = null, float now = -1f)
        {
            return ApplyNativeMonsterHealthRegen(monster, source, now);
        }

        private void SetRuntimeMonsterHPWire(Monster monster, uint hp, bool committedDamage, float regenClockTime = -1f, string source = "SET")
        {
            if (monster == null) return;
            if (hp > monster.MaxHPWire) hp = monster.MaxHPWire;
            uint oldHP = PeekRuntimeMonsterHPWire(monster);
            _monsterRuntimeHPWire[monster.EntityId] = hp;
            if (committedDamage && hp < monster.MaxHPWire)
                _monsterRuntimeDamageCommitted.Add(monster.EntityId);
            else if (!committedDamage && hp >= monster.MaxHPWire)
                _monsterRuntimeDamageCommitted.Remove(monster.EntityId);
            SyncMonsterHPField(monster, hp);
            ResetMonsterHPRegenClock(monster, regenClockTime);
            if (oldHP != hp)
                Debug.LogError($"[MON-HP-CANON] write entity={monster.EntityId} old={oldHP} new={hp} committed={committedDamage} source={source ?? "unknown"}");
        }

        public uint GetMonsterCurrentHPWire(Monster monster)
        {
            return GetRuntimeMonsterHPWire(monster, "HP-READ");
        }

        public uint GetMonsterCurrentHPWire(Monster monster, string source)
        {
            return GetRuntimeMonsterHPWire(monster, source);
        }

        public uint PeekMonsterCurrentHPWire(Monster monster)
        {
            return PeekRuntimeMonsterHPWire(monster);
        }

        public void SetMonsterHPWire(Monster monster, uint hp, bool committedDamage = false, string source = "SET")
        {
            SetRuntimeMonsterHPWire(monster, hp, committedDamage, -1f, source);
        }

        public void NotifyMonsterDamagedByPlayer(Monster monster, uint playerEntityId, string reason)
        {
            if (monster == null || !monster.IsAlive || playerEntityId == 0) return;
            if (!_players.TryGetValue(playerEntityId, out var player) || player == null || !player.IsAlive) return;
            AggroMonster(monster, player, reason, false);
        }

        public void MarkMonsterHPDirty(Monster monster, string reason)
        {
            if (monster == null || !monster.IsAlive) return;
            uint hp = GetRuntimeMonsterHPWire(monster, "DIRTY");
            monster.ClientHPDirty = true;
            monster.ClientHPDirtySince = Time.time;
            monster.ClientHPDirtyReason = reason ?? "unknown";
            monster.ClientHPDirtyStartWire = hp;
            monster.ClientHPDirtyAllowSameConfirmAt = Time.time + 2.5f;
            Debug.LogError($"[MON-HP-TRUTH] DIRTY {monster.Name}#{monster.EntityId} hp={hp / 256f:F2}/{monster.MaxHPWire / 256f:F2} reason={monster.ClientHPDirtyReason}");
        }

        public bool ApplyNativePlayerDamageToMonsterWire(Monster monster, uint damageWire, string source, out uint oldHPWire, out uint newHPWire, out bool died, float nativeDamageTime = -1f)
        {
            oldHPWire = GetRuntimeMonsterHPWire(monster, source, nativeDamageTime);
            newHPWire = oldHPWire;
            died = false;
            if (monster == null || !monster.IsAlive || damageWire == 0) return false;

            newHPWire = damageWire >= oldHPWire ? 0u : oldHPWire - damageWire;
            SetRuntimeMonsterHPWire(monster, newHPWire, true, nativeDamageTime, source ?? "damage");
            monster.ClientHPDirty = false;
            monster.ClientHPDirtySince = 0f;
            monster.ClientHPDirtyReason = null;
            monster.ClientHPDirtyStartWire = 0;
            monster.ClientHPDirtyAllowSameConfirmAt = 0f;
            monster.SuppressedMonsterHPSyncPackets = 0;
            monster.LastClientHPReportTime = Time.time;
            monster.LastClientHPReportWire = newHPWire;

            if (newHPWire == 0)
            {
                monster.IsAlive = false;
                monster.State = MonsterState.Dead;
                monster.CombatContactTargetId = 0;
                monster.CombatContactUntil = 0f;
                if (_activeMonsters.TryGetValue(monster.EntityId, out var active) && active != monster)
                {
                    active.IsAlive = false;
                    active.State = MonsterState.Dead;
                    active.CombatContactTargetId = 0;
                    active.CombatContactUntil = 0f;
                }
                died = true;
            }

            Debug.LogError($"[MON-HP-TRUTH] COMPUTED {monster.Name}#{monster.EntityId} source={source ?? "unknown"} hp={oldHPWire / 256f:F2}->{newHPWire / 256f:F2}/{monster.MaxHPWire / 256f:F2} dmg={damageWire / 256f:F2}");
            return true;
        }

        private bool IsTrustedMonsterHPReportSource(string source)
        {
            if (string.IsNullOrEmpty(source)) return false;
            return source.StartsWith("DLL-HP", StringComparison.Ordinal)
                || source.StartsWith("HP-SYNC", StringComparison.Ordinal)
                || source.StartsWith("SEND-UPDATE", StringComparison.Ordinal)
                || source.StartsWith("MONSTER-MOVE-HP", StringComparison.Ordinal)
                || source.StartsWith("MONSTER-SM-HP", StringComparison.Ordinal)
                || source.StartsWith("ENTITY-SYNC", StringComparison.Ordinal);
        }

        private bool IsNativeMonsterRegenIncrease(Monster monster, uint oldHPWire, uint clientHPWire, string source)
        {
            if (monster == null || clientHPWire <= oldHPWire || oldHPWire >= monster.MaxHPWire) return false;
            if (!IsTrustedMonsterHPReportSource(source)) return false;
            float rate = ResolveMonsterHealthRegenHPPerSecond();
            float elapsed = 0.25f;
            if (monster.LastClientHPReportTime > 0f)
                elapsed = Mathf.Max(elapsed, Time.time - monster.LastClientHPReportTime + 0.25f);
            float computedAllowed = rate * 256f * Mathf.Clamp(elapsed, 0.25f, 4f) + 5f * 256f;
            uint allowedIncrease = (uint)Mathf.CeilToInt(computedAllowed);
            return clientHPWire - oldHPWire <= allowedIncrease;
        }

        public bool AcceptClientMonsterHP(Monster monster, uint clientHPWire, string source)
        {
            if (monster == null) return false;
            const uint toleranceWire = 5u * 256u;
            if (clientHPWire > monster.MaxHPWire + toleranceWire)
            {
                Debug.LogError($"[{source}] Monster HP rejected: {monster.Name}#{monster.EntityId} client={clientHPWire / 256f:F2} max={monster.MaxHPWire / 256f:F2}");
                return false;
            }

            uint oldHPWire = GetRuntimeMonsterHPWire(monster, source);
            bool nativeRegenIncrease = IsNativeMonsterRegenIncrease(monster, oldHPWire, clientHPWire, source);
            if (_monsterRuntimeDamageCommitted.Contains(monster.EntityId) && oldHPWire < monster.MaxHPWire && clientHPWire > oldHPWire && !nativeRegenIncrease)
            {
                Debug.LogError($"[{source}] Monster HP increase rejected after committed damage: {monster.Name}#{monster.EntityId} client={clientHPWire / 256f:F2} server={oldHPWire / 256f:F2}/{monster.MaxHPWire / 256f:F2}");
                monster.LastClientHPReportTime = Time.time;
                monster.LastClientHPReportWire = clientHPWire;
                return false;
            }
            if (monster.ClientHPDirty)
            {
                uint dirtyStart = monster.ClientHPDirtyStartWire != 0 ? monster.ClientHPDirtyStartWire : oldHPWire;
                bool clientReportsDamage = clientHPWire < dirtyStart || clientHPWire < oldHPWire;
                bool sameOrHigherThanDirtyStart = clientHPWire >= dirtyStart;
                bool trustedHPReport = IsTrustedMonsterHPReportSource(source);

                if (!clientReportsDamage && sameOrHigherThanDirtyStart)
                {
                    if (!trustedHPReport)
                    {
                        Debug.LogError($"[{source}] Monster HP report held while dirty: {monster.Name}#{monster.EntityId} client={clientHPWire / 256f:F2} dirtyStart={dirtyStart / 256f:F2} server={oldHPWire / 256f:F2} sourceNotTrustedForSameConfirm reason={monster.ClientHPDirtyReason}");
                        monster.LastClientHPReportTime = Time.time;
                        monster.LastClientHPReportWire = clientHPWire;
                        return false;
                    }

                    Debug.LogError($"[{source}] Monster HP trusted while dirty: {monster.Name}#{monster.EntityId} client={clientHPWire / 256f:F2} dirtyStart={dirtyStart / 256f:F2} server={oldHPWire / 256f:F2} reason={monster.ClientHPDirtyReason}");
                }

                if (clientHPWire > oldHPWire && clientReportsDamage == false && oldHPWire < monster.MaxHPWire && !nativeRegenIncrease)
                {
                    Debug.LogError($"[{source}] Monster HP increase rejected while dirty: {monster.Name}#{monster.EntityId} client={clientHPWire / 256f:F2} server={oldHPWire / 256f:F2} reason={monster.ClientHPDirtyReason}");
                    monster.LastClientHPReportTime = Time.time;
                    monster.LastClientHPReportWire = clientHPWire;
                    return false;
                }
            }

            SetRuntimeMonsterHPWire(monster, clientHPWire, false, -1f, source ?? "client-report");
            monster.ClientHPDirty = false;
            monster.ClientHPDirtySince = 0f;
            monster.ClientHPDirtyReason = null;
            monster.ClientHPDirtyStartWire = 0;
            monster.ClientHPDirtyAllowSameConfirmAt = 0f;
            monster.SuppressedMonsterHPSyncPackets = 0;
            monster.LastClientHPReportTime = Time.time;
            monster.LastClientHPReportWire = clientHPWire;

            if (oldHPWire != clientHPWire)
                Debug.LogError($"[{source}] Monster HP accepted: {monster.Name}#{monster.EntityId} {oldHPWire / 256f:F2}->{clientHPWire / 256f:F2}/{monster.MaxHPWire / 256f:F2} wire={clientHPWire} nativeRegenIncrease={nativeRegenIncrease}");
            else
                Debug.LogError($"[{source}] Monster HP confirmed: {monster.Name}#{monster.EntityId} {clientHPWire / 256f:F2}/{monster.MaxHPWire / 256f:F2} wire={clientHPWire}");
            return true;
        }

        public bool CanSendMonsterSynchronizedHP(Monster monster, string packetName)
        {
            if (monster == null) return false;
            uint serverHPWire = GetRuntimeMonsterHPWire(monster, packetName);
            if (!monster.ClientHPDirty) return true;
            float age = Time.time - monster.ClientHPDirtySince;
            Debug.LogError($"[{packetName}] allowing monster HP sync while dirty for {monster.Name}#{monster.EntityId}: serverHP={serverHPWire / 256f:F2}/{monster.MaxHPWire / 256f:F2} dirtyFor={age:F2}s reason={monster.ClientHPDirtyReason}");
            return true;
        }

        public bool IsDirtyMonsterComponent(ushort componentId, out Monster monster)
        {
            monster = GetMonster(componentId)
                   ?? GetMonsterByComponent(componentId)
                   ?? GetMonsterByBehaviorId(componentId)
                   ?? GetMonsterBySkillsId(componentId)
                   ?? GetMonsterByManipulatorsId(componentId);
            return monster != null && monster.ClientHPDirty;
        }

        private bool AggroMonster(Monster monster, CombatPlayer player, string reason, bool alignForCombat)
        {
            if (monster == null || player == null || !monster.IsAlive) return false;
            bool firstAggro = !monster.AggroTriggered || monster.TargetId != player.EntityId;
            monster.AggroTriggered = true;
            monster.TargetId = player.EntityId;
            monster.State = MonsterState.Combat;
            if (alignForCombat) AlignMonsterForClientCombat(monster, player);
            if (firstAggro)
            {
                WanderSimulator.Instance.UnregisterEntity(monster.EntityId);
                monster.AttackPending = false;
                monster.AttackSoundPending = false;
                float cooldown = Mathf.Max(0.1f, monster.AttackCooldown);
                monster.LastAttackTime = Time.time - cooldown;
                Debug.LogError($"[SERVER-AGGRO] {monster.Name} -> {player.Name} reason={reason}");
                OnMonsterAggro?.Invoke(monster, player);
                PropagateMonsterShout(monster, player);
            }
            return firstAggro;
        }

        private void PropagateMonsterShout(Monster source, CombatPlayer player)
        {
            if (source == null || player == null || source.ShoutRange <= 0f || string.IsNullOrEmpty(source.EncounterGroupKey)) return;
            if (PlayerHasIncomingAttacker(player.EntityId, source.EntityId)) return;
            float shoutSq = source.ShoutRange * source.ShoutRange;
            foreach (var monster in _activeMonsters.Values)
            {
                if (monster == source || !monster.IsAlive || monster.AggroTriggered || monster.TargetId != 0) continue;
                if (!string.Equals(monster.EncounterGroupKey, source.EncounterGroupKey, StringComparison.OrdinalIgnoreCase)) continue;
                float dx = monster.SpawnPosX - source.SpawnPosX;
                float dy = monster.SpawnPosY - source.SpawnPosY;
                float distSq = dx * dx + dy * dy;
                if (distSq > shoutSq) continue;
                monster.AggroTriggered = true;
                monster.TargetId = player.EntityId;
                monster.State = MonsterState.Combat;
                WanderSimulator.Instance.UnregisterEntity(monster.EntityId);
                monster.AttackPending = false;
                monster.AttackSoundPending = false;
                float cooldown = Mathf.Max(0.1f, monster.AttackCooldown);
                monster.LastAttackTime = Time.time - cooldown;
                Debug.LogError($"[SERVER-SHOUT] source={source.EntityId} target={monster.EntityId} shout={source.ShoutRange:F1} spawnDistSq={distSq:F1} group={source.EncounterGroupKey}");
                OnMonsterAggro?.Invoke(monster, player);
            }
        }

        private bool PlayerHasIncomingAttacker(uint playerEntityId, uint exceptEntityId = 0)
        {
            foreach (var monster in _activeMonsters.Values)
            {
                if (monster == null || !monster.IsAlive || !monster.AggroTriggered) continue;
                if (exceptEntityId != 0 && monster.EntityId == exceptEntityId) continue;
                if (monster.TargetId == playerEntityId) return true;
            }
            return false;
        }

        private void AlignMonsterForClientCombat(Monster monster, CombatPlayer player)
        {
            float allowedRange = ResolveMonsterEffectiveAttackRange(monster);
            if (allowedRange <= 0f) return;

            float dx = monster.PosX - player.PosX;
            float dy = monster.PosY - player.PosY;
            float dist = Mathf.Sqrt(dx * dx + dy * dy);
            if (dist <= allowedRange && dist > 0.001f) return;

            if (dist <= 0.001f)
            {
                float headingRad = monster.Heading * Mathf.Deg2Rad;
                dx = Mathf.Cos(headingRad);
                dy = Mathf.Sin(headingRad);
                dist = Mathf.Sqrt(dx * dx + dy * dy);
            }

            if (dist <= 0.001f)
            {
                dx = 1f;
                dy = 0f;
                dist = 1f;
            }

            float contactRange = Mathf.Max(1f, allowedRange - 1f);
            monster.PosX = player.PosX + dx / dist * contactRange;
            monster.PosY = player.PosY + dy / dist * contactRange;
        }
        public IEnumerable<Monster> GetAllMonsters()
        {
            return _activeMonsters.Values;
        }
        public void UnregisterPlayer(uint entityId)
        {
            _players.Remove(entityId);
            _playerCombatAdvanceTime.Remove(entityId);
            foreach (var monster in _activeMonsters.Values)
            {
                if (monster == null) continue;
                bool touchesPlayer = monster.TargetId == entityId || monster.CombatContactTargetId == entityId;
                if (monster.TargetId == entityId) monster.TargetId = 0;
                if (monster.CombatContactTargetId == entityId) monster.CombatContactTargetId = 0;
                if (touchesPlayer || monster.AttackPending)
                {
                    monster.AttackPending = false;
                    monster.AttackSoundPending = false;
                    monster.AttackClientVisible = false;
                    monster.AttackCommitTime = 0f;
                    monster.AttackSoundTime = 0f;
                    monster.CombatContactUntil = 0f;
                }
            }
            Debug.LogError($"[COMBAT-LIFECYCLE] unregistered player {entityId} and cleared monster targeting state");
        }
        // ═══════════════════════════════════════════════════════════════
        // ROOM-LEVEL RNG — Binary-proven: ALL combat in a room shares ONE Random.
        // Combat code at 0x54935D: [target+0x88]+0x44 = parent(room) entity's Random.
        // Opcode 0x0C in the dispatch loop (0x5DA460) seeds the room entity's +0x44.
        // The dispatch loop runs on the ClientEntityManager (room entity), so
        // every 0x0C in any spawn packet seeds the SAME room-level Random.
        // ═══════════════════════════════════════════════════════════════
        private MersenneTwister _roomRng;
        private uint _roomSeed;
        private bool _roomRngInitialized = false;

        /// <summary>The single shared RNG for all combat in this room/zone.</summary>
        public MersenneTwister RoomRng => _roomRng;

        /// <summary>The seed used for the room RNG. Sent via opcode 0x0C in spawn packets.</summary>
        public uint RoomSeed => _roomSeed;

        /// <summary>Whether the room RNG has been initialized.</summary>
        public bool IsRoomRngReady => _roomRngInitialized;

        // Legacy alias — kept so existing code that references SyncedRandom still compiles
        public MersenneTwister SyncedRandom => _roomRng;
        public uint RandomSeed => _roomSeed;

        /// <summary>
        /// Initialize the single room-level RNG. Call ONCE when the zone/room loads,
        /// BEFORE any monsters spawn. The same seed must go into every spawn packet
        /// via opcode 0x0C so the client's room entity Random matches.
        /// </summary>
        public void InitializeRoomRng(uint seed)
        {
            _roomSeed = seed;
            _roomRng = new MersenneTwister(seed);
            _roomRngInitialized = true;
            Debug.LogError($"[ROOM-RNG] ★ Room RNG initialized with seed: 0x{seed:X8}");
        }
        /// <summary>
        /// Re-seed the existing room RNG in-place. Called every 4th tick (~132ms).
        /// Binary-proven: 0x5DF0CB calls Random::seed on EntityManager+0x44
        /// using timeGetTime() as the seed. This re-seeds the EXISTING object,
        /// not creating a new one.
        /// </summary>
        public void ReseedRoomRng(uint seed)
        {
            _roomSeed = seed;
            if (_roomRng == null)
            {
                _roomRng = new MersenneTwister(seed);
                _roomRngInitialized = true;
            }
            else
            {
                _roomRng.Seed(seed);
            }
        }
        /// <summary>Legacy alias for old code paths.</summary>
        public void InitializeRandomSeed(uint seed)
        {
            if (_roomRngInitialized)
            {
                ReseedRoomRng(seed);
                return;
            }
            InitializeRoomRng(seed);
        }
        public event Action<Monster, CombatPlayer> OnMonsterAggro;
        public CombatPlayer GetPlayer(uint entityId)
        {
            return _players.TryGetValue(entityId, out var p) ? p : null;
        }

        public void SetPlayerActiveClientAttack(uint entityId, bool active, uint targetId = 0)
        {
            if (_players.TryGetValue(entityId, out var player) && player != null)
            {
                player.HasActiveClientAttack = active;
                player.ActiveClientAttackTargetId = active ? targetId : 0;
            }
        }

        public void FlushPlayerCombatBeforeSync(uint playerEntityId, float deltaTime)
        {
            if (playerEntityId == 0) return;
            float now = Time.time;
            float elapsed = deltaTime > 0f ? deltaTime : 0f;
            if (_playerCombatAdvanceTime.TryGetValue(playerEntityId, out float last) && last > 0f)
                elapsed = Mathf.Max(elapsed, now - last);
            _playerCombatAdvanceTime[playerEntityId] = now;
            elapsed = Mathf.Clamp(elapsed, 0f, 1.0f);
            ProcessProximityAggro();
            if (elapsed <= 0f)
            {
                ProcessMonsterAttacks(0f, playerEntityId);
                return;
            }
            float remaining = elapsed;
            int guard = 0;
            while (remaining > 0f && guard++ < 64)
            {
                float step = Mathf.Min(remaining, 1f / 30f);
                ProcessMonsterMovement(step, playerEntityId);
                ProcessMonsterAttacks(step, playerEntityId);
                remaining -= step;
            }
        }

        public IEnumerable<CombatPlayer> GetAllPlayers()
        {
            return _players.Values;
        }
        private static Dictionary<string, string> _zoneBehaviors;

        private void LoadZoneBehaviors()
        {
            _zoneBehaviors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                // Read from SQLite zone_behaviors table
                using (var conn = DungeonRunners.Database.GameDatabase.GetConnection())
                using (var reader = DungeonRunners.Database.GameDatabase.ExecuteReader(conn,
                    "SELECT zone_name, behavior_mode FROM zone_behaviors WHERE enabled = 1"))
                {
                    while (reader.Read())
                    {
                        string zone = reader.GetString(0);
                        string mode = reader.GetString(1);
                        _zoneBehaviors[zone] = mode;
                    }
                }
                Debug.LogError($"[CombatManager] Loaded {_zoneBehaviors.Count} zone behaviors from SQLite");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[CombatManager] Failed to load zone behaviors from SQLite: {ex.Message}");
            }
        }

        public string GetBehaviorMode(string zoneName)
        {
            if (string.IsNullOrEmpty(zoneName)) return ServerSettings.GetString("defaultBehavior", "dungeon_specific").ToLowerInvariant();

            // Strip instance suffix: "dungeon00_level01_inst2147483649" → "dungeon00_level01"
            string lookupName = zoneName;
            int instIdx = zoneName.IndexOf("_inst", StringComparison.OrdinalIgnoreCase);
            if (instIdx > 0)
                lookupName = zoneName.Substring(0, instIdx);

            try
            {
                using (var conn = DungeonRunners.Database.GameDatabase.GetConnection())
                {
                    object result = DungeonRunners.Database.GameDatabase.ExecuteScalar(conn,
                        "SELECT behavior_mode FROM zone_behaviors WHERE zone_name = @z AND enabled = 1",
                        ("@z", lookupName));
                    if (result != null)
                    {
                        string mode = result.ToString().ToLower();
                        Debug.LogError($"[BEHAVIOR] GetBehaviorMode('{zoneName}' → lookup '{lookupName}') → '{mode}' from SQLite");
                        return mode;
                    }
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[BEHAVIOR] SQLite error for '{lookupName}': {ex.Message}");
            }
            string defaultMode = ServerSettings.GetString("defaultBehavior", "dungeon_specific").ToLowerInvariant();
            Debug.LogError($"[BEHAVIOR] GetBehaviorMode('{zoneName}' → lookup '{lookupName}') → '{defaultMode}' (config fallback, not in DB)");
            return defaultMode;
        }

        private string GetBehaviorForZone(string zoneName)
        {
            string mode = GetBehaviorMode(zoneName);
            if (mode == "guard")
            {
                Debug.LogError($"[BEHAVIOR] Zone '{zoneName}' → GUARD → using world.dungeon09.mob.base.oneoff_behavior");
                return "world.dungeon09.mob.base.oneoff_behavior";
            }
            Debug.LogError($"[BEHAVIOR] Zone '{zoneName}' → '{mode}' → using authored creature behavior");
            return null;  // wander and dungeon_specific use creature's own type
        }

        private string ResolveSpawnBehaviourType(string zoneBehaviourType, string spawnGcType, string baseGcType)
        {
            if (!string.IsNullOrEmpty(zoneBehaviourType))
                return zoneBehaviourType;
            return ResolveAuthoredChildPath(spawnGcType, "Behavior") ?? ResolveAuthoredChildPath(baseGcType, "Behavior");
        }

        public Monster SpawnMonster(string gcType, float posX, float posY, float posZ, float heading = 0f, string zoneName = null, string encounterGroupKey = null, float encounterDifficulty = 1f)
        {
            var creatureData = DatabaseLoader.FindCreature(gcType);
            if (creatureData == null)
            {
                Debug.LogError($"[Combat] CREATURE NOT FOUND: '{gcType}'");
                return null;
            }

            string spawnGcType = ResolveDungeonCreaturePath(zoneName, creatureData.gcType);
            string zoneBehaviourType = GetBehaviorForZone(zoneName);
            GCNode authoredCreature = ResolveAuthoredCreatureNode(spawnGcType, creatureData.gcType);
            string spawnBehaviourType = ResolveSpawnBehaviourType(zoneBehaviourType, spawnGcType, creatureData.gcType);
            GCNode authoredDesc = authoredCreature?.GetChild("Description") ?? authoredCreature;
            GCNode authoredWeapon = GetAuthoredWeaponDescription(authoredCreature);
            GCNode authoredBehavior = ResolveAuthoredBehaviorNode(spawnBehaviourType);
            var spawnManipulators = BuildSpawnManipulators(spawnGcType, creatureData.gcType, authoredCreature, creatureData.manipulators);
            string creatureDifficulty = GetAuthoredString(authoredDesc, "CreatureDifficulty", creatureData.creatureDifficulty);
            float maxHealth = GetAuthoredFloat(authoredDesc, "MaxHealth", creatureData.maxHealth);
            float perceptionRange = GetAuthoredBehaviorFloat(authoredBehavior, authoredCreature, "Perception", 0f);
            float aggroFallback = perceptionRange > 0f ? perceptionRange : GetAggroRangeForTier(creatureDifficulty);
            float attackCooldown = GetAuthoredFloat(authoredWeapon, "CoolDown", GetAttackCooldownFromManipulators(creatureData));
            float attackLeadDelay = GetAuthoredAttackLeadDelay(authoredDesc, Mathf.Max(0.1f, attackCooldown * 0.5f), out float attackSoundLeadDelay);

            // Calculate level from tier + zone base
            byte tierLevel = GetLevelForTier(creatureDifficulty);
            byte zoneBase = GetZoneBaseLevel(zoneName);
            byte calculatedLevel = (byte)Math.Min(100, tierLevel + zoneBase);
            Debug.LogError($"[Combat] Level calc: tier={creatureDifficulty}({tierLevel}) + zone={zoneName}({zoneBase}) = {calculatedLevel}");

            uint entityId = _nextMonsterId++;
            uint behaviorId = _nextMonsterId++;
            uint skillsId = _nextMonsterId++;
            uint manipulatorsId = _nextMonsterId++;
            uint modifiersId = _nextMonsterId++;
            uint unitId = _nextMonsterId++;

            var monster = new Monster
            {
                EntityId = entityId,
                BehaviorId = behaviorId,
                SkillsId = skillsId,
                ManipulatorsId = manipulatorsId,
                ModifiersId = modifiersId,
                UnitId = unitId,

                GCType = creatureData.gcType,
                SpawnGCType = spawnGcType,
                BehaviourType = creatureData.behaviourType,
                Name = GetAuthoredString(authoredDesc, "Label", creatureData.name),
                Faction = creatureData.faction,

                CreatureType = creatureData.creatureType,
                Element = creatureData.element,
                Tier = creatureDifficulty,
                Level = calculatedLevel,
                Difficulty = MonsterHealthTable.GetDifficultyModifier(creatureDifficulty),
                ExperienceDifficulty = encounterDifficulty,

                MaxHPWire = MonsterHealthTable.CalculateHPWire(calculatedLevel, creatureDifficulty, maxHealth),
                CurrentHPWire = MonsterHealthTable.CalculateHPWire(calculatedLevel, creatureDifficulty, maxHealth),


                MaxManaWire = (uint)(creatureData.manaPoints * 256),
                CurrentManaWire = (uint)(creatureData.manaPoints * 256),
                BaseDamage = creatureData.baseDamage,
                AttackRating = GetAuthoredFloat(authoredDesc, "AttackRating", creatureData.AttackRatingF),
                DamageMod = GetAuthoredFloat(authoredDesc, "DamageMod", creatureData.DamageModF),
                DefenseRating = GetAuthoredFloat(authoredDesc, "DefenseRating", creatureData.DefenseRatingF),
                CritChance = GetAuthoredFloat(authoredDesc, "CriticalChance", creatureData.CritChanceF),
                DivineResist = GetAuthoredFloat(authoredDesc, "DivineResist", creatureData.DivineResistF),
                FireResist = GetAuthoredFloat(authoredDesc, "FireResist", creatureData.FireResistF),
                IceResist = GetAuthoredFloat(authoredDesc, "IceResist", creatureData.IceResistF),
                PoisonResist = GetAuthoredFloat(authoredDesc, "PoisonResist", creatureData.PoisonResistF),
                ShadowResist = GetAuthoredFloat(authoredDesc, "ShadowResist", creatureData.ShadowResistF),
                DamageVolatility = GetAuthoredFloat(authoredWeapon, "DamageVolatility", GetManipulatorFloat(creatureData, "DamageVolatility", 0.5f)),
                WeaponDamage = GetAuthoredFloat(authoredWeapon, "Damage", GetManipulatorFloat(creatureData, "Damage", 1.0f)),
                PosX = posX,
                PosY = posY,
                PosZ = posZ,
                SpawnPosX = posX,
                SpawnPosY = posY,
                SpawnPosZ = posZ,
                Heading = heading,

                PerceptionRange = perceptionRange,
                AggroRange = GetAuthoredBehaviorFloat(authoredBehavior, authoredCreature, "AgroRange", aggroFallback),
                ShoutRange = GetAuthoredBehaviorFloat(authoredBehavior, authoredCreature, "ShoutRange", 0f),
                LeashRange = GetAuthoredLeashRange(authoredBehavior, authoredCreature, creatureDifficulty),
                AttackRange = GetAuthoredAttackRange(authoredDesc, authoredWeapon, creatureData),
                ClientSyncTolerance = GetAuthoredFloat(authoredWeapon, "ClientSyncTolerance", GetManipulatorFloat(creatureData, "ClientSyncTolerance", 10f)),
                CollisionRadius = GetAuthoredFloat(authoredDesc, "CollisionRadius", 5f),
                AttackCooldown = attackCooldown,
                AttackLeadDelay = attackLeadDelay,
                AttackSoundLeadDelay = attackSoundLeadDelay,
                HasAttackSound = HasAuthoredAttackSound(authoredDesc),
                MoveSpeed = GetAuthoredMoveSpeed(authoredDesc, creatureData),
                WalkSpeed = GetAuthoredWalkSpeed(authoredCreature, authoredDesc, creatureData),
                WanderRange = GetAuthoredWanderRange(authoredBehavior, authoredCreature),

                Manipulators = spawnManipulators,

                State = MonsterState.Idle,
                IsAlive = true,
                SpawnTime = Time.time,
                ZoneName = zoneName,
                EncounterGroupKey = encounterGroupKey,
                SpawnBehaviourType = spawnBehaviourType
            };

            _activeMonsters[entityId] = monster;

            _activeMonsters[entityId] = monster;
            SetRuntimeMonsterHPWire(monster, monster.CurrentHPWire, false, Time.time, "SPAWN");
            // Room RNG seed is set on the monster for inclusion in spawn packet,
            // but ALL combat uses the shared CombatManager.RoomRng, not per-monster RNG.
            // The seed goes into the spawn packet's opcode 0x0C which seeds the
            // CLIENT's room entity Random (the same entity for all monsters).
            if (!_roomRngInitialized)
            {
                // First monster spawn — initialize room RNG now
                uint seed = (uint)(Environment.TickCount ^ (entityId * 2654435761u));
                InitializeRoomRng(seed);
            }
            monster.RngSeed = _roomSeed;  // All monsters carry the SAME room seed
            Debug.LogError($"[Combat] Monster {monster.Name} using room RNG seed: 0x{_roomSeed:X8}");

            _componentToEntityMap[entityId] = entityId;
            _componentToEntityMap[behaviorId] = entityId;
            _componentToEntityMap[skillsId] = entityId;
            _componentToEntityMap[manipulatorsId] = entityId;
            _componentToEntityMap[modifiersId] = entityId;
            _componentToEntityMap[unitId] = entityId;

            // Register with WanderSimulator — must be in spawn order to match client entity tick order
            if (ShouldRegisterWander(authoredBehavior, authoredCreature, zoneName))
                WanderSimulator.Instance.RegisterMonster(monster, true);

            Debug.LogError($"[Combat] SPAWNED: {monster.Name} (ID:{entityId}) Level:{calculatedLevel} HP:{monster.MaxHP} DMG:{creatureData.baseDamage}");
            Debug.LogError($"[Combat]   ComponentIDs: Entity={entityId}, Behavior={behaviorId}, Skills={skillsId}, Manip={manipulatorsId}, Mods={modifiersId}, Unit={unitId}");
            Debug.LogError($"[Combat]   Position: ({posX:F1}, {posY:F1}, {posZ:F1}) PerceptionRange={monster.PerceptionRange} AggroRange={monster.AggroRange} ShoutRange={monster.ShoutRange} LeashRange={monster.LeashRange} AttackRange={monster.AttackRange} SyncTolerance={monster.ClientSyncTolerance} CollisionRadius={monster.CollisionRadius} Cooldown={monster.AttackCooldown} WalkSpeed={monster.WalkSpeed} WanderRange={monster.WanderRange} Group={monster.EncounterGroupKey}");

            OnMonsterSpawned?.Invoke(monster);
            return monster;
        }
        // Family prefix → mob family name. Uses StartsWith (case-insensitive) so ALL
        // creature variants (.grunt, .grunt1hmelee, .champion, .championranged, .hero,
        // .hero1hmelee, .heroranged, .boss, .gruntlookout, .championlookout, etc.) map
        // to the same dungeon mob family. Previously the map required exact-match on
        // ".Basic.Grunt" which left every other variant untranslated → quest targets
        // in the "world.dungeonXX.mob.meleeYY" namespace never tracked. This is why
        // the Dew Valley Infestation (Q01_a2, Slay Whisker Ratlings) wasn't ticking.
        private static readonly (string prefix, string family)[] _familyPrefixes = new (string, string)[]
        {
            // ── dungeon00 (Dew Valley) ──
            ( "creatures.forestCreatures.Warg.Basic.Pup",   "melee01" ), // Dew Valley Pups
            ( "creatures.forestCreatures.Warg.Basic.Grunt", "melee02" ), // Dew Valley Wolves
            ( "creatures.whiskers.broodling.basic",         "melee03" ), // Whisker Ratlings
            ( "creatures.whiskers.blademaster.basic",       "melee04" ), // Whisker Blademasters
        };

        private string ResolveDungeonCreaturePath(string zoneName, string baseGcType)
        {
            if (string.IsNullOrEmpty(zoneName) || string.IsNullOrEmpty(baseGcType))
                return null;

            // Strip instance suffix: "dungeon00_level01_inst2147483649" → "dungeon00_level01"
            string lookupZone = zoneName;
            int instIdx = zoneName.IndexOf("_inst", StringComparison.OrdinalIgnoreCase);
            if (instIdx > 0)
                lookupZone = zoneName.Substring(0, instIdx);

            // Extract "dungeonNN" prefix and level number.
            // Only run for zones that look like "dungeonNN_levelMM[...]".
            if (!lookupZone.StartsWith("dungeon", StringComparison.OrdinalIgnoreCase))
                return null;
            int underscoreIdx = lookupZone.IndexOf('_');
            if (underscoreIdx <= 0) return null;
            string dungeonPrefix = lookupZone.Substring(0, underscoreIdx); // "dungeon00"

            int lvlIdx = lookupZone.IndexOf("_level", StringComparison.OrdinalIgnoreCase);
            if (lvlIdx < 0 || lvlIdx + 8 > lookupZone.Length) return null;
            string numStr = lookupZone.Substring(lvlIdx + 6, 2);
            if (!int.TryParse(numStr, out int levelNum) || levelNum < 1 || levelNum > 3)
                return null;
            int rank = levelNum; // level01→rank1, level02→rank2, level03→rank3

            // Special case: this exact creature is rendered as Rattle Tooth (the
            // dungeon00 unique boss) via MapToBaseGCType in CombatPackets.cs which
            // maps it to "world.dungeon00.mob.boss". Return null so SpawnGCType
            // stays unset and BuildMonsterSpawnPacket falls through to the raw
            // GCType → MapToBaseGCType translation. Without this exception my
            // prefix table grabs it as a regular melee03 ratling and the @boss
            // command spawns a rat instead of the boss.
            if (baseGcType.Equals("creatures.whiskers.broodling.basic.champion", StringComparison.OrdinalIgnoreCase))
                return null;

            // Prefix-match against family table
            foreach (var fam in _familyPrefixes)
            {
                if (baseGcType.StartsWith(fam.prefix, StringComparison.OrdinalIgnoreCase))
                    return $"world.{dungeonPrefix}.mob.{fam.family}.rank{rank}";
            }

            return null;
        }

        private GCNode ResolveAuthoredCreatureNode(string spawnGcType, string baseGcType)
        {
            var gc = GCDatabase.Instance;
            if (gc == null || !gc.IsLoaded) return null;

            GCNode node = null;
            if (!string.IsNullOrEmpty(spawnGcType))
                node = gc.ResolveWithInheritance(spawnGcType);
            if (node == null && !string.IsNullOrEmpty(baseGcType))
                node = gc.ResolveWithInheritance(baseGcType);
            return node;
        }

        private GCNode ResolveAuthoredBehaviorNode(string behaviorGcType)
        {
            var gc = GCDatabase.Instance;
            if (gc == null || !gc.IsLoaded || string.IsNullOrEmpty(behaviorGcType)) return null;
            return gc.ResolveWithInheritance(behaviorGcType);
        }

        private string ResolveAuthoredChildPath(string rootPath, string childPath)
        {
            if (string.IsNullOrEmpty(rootPath) || string.IsNullOrEmpty(childPath)) return null;
            var gc = GCDatabase.Instance;
            if (gc == null || !gc.IsLoaded) return null;

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string currentPath = rootPath;
            while (!string.IsNullOrEmpty(currentPath) && visited.Add(currentPath))
            {
                var node = gc.Resolve(currentPath);
                if (node == null) return null;
                if (RawChildPathExists(node, childPath))
                    return currentPath + "." + childPath;
                currentPath = node.Extends;
            }
            return null;
        }

        private bool RawChildPathExists(GCNode node, string childPath)
        {
            if (node == null || string.IsNullOrEmpty(childPath)) return false;
            var current = node;
            foreach (string part in childPath.Split('.'))
            {
                if (current == null || !current.Children.TryGetValue(part, out current))
                    return false;
            }
            return true;
        }

        private Dictionary<string, ManipulatorData> BuildSpawnManipulators(string spawnGcType, string baseGcType, GCNode authoredCreature, Dictionary<string, ManipulatorData> fallback)
        {
            var result = new Dictionary<string, ManipulatorData>(StringComparer.OrdinalIgnoreCase);
            var manipulators = authoredCreature?.GetChild("Manipulators");
            if (manipulators != null)
            {
                var primaryWeapon = manipulators.GetChild("PrimaryWeapon");
                if (primaryWeapon != null)
                {
                    string primaryWeaponPath = ResolveManipulatorRuntimeType(primaryWeapon);
                    if (!string.IsNullOrEmpty(primaryWeaponPath))
                        result["primaryweapon"] = CreateSpawnManipulator(primaryWeaponPath, primaryWeapon);
                }

                int skillIndex = 1;
                if (manipulators.AnonymousChildren != null)
                {
                    foreach (var child in manipulators.AnonymousChildren)
                    {
                        if (skillIndex > 2) break;
                        if (child == null || !IsSkillManipulatorPath(child.Extends)) continue;
                        result[$"skill{skillIndex++}"] = CreateSpawnManipulator(child.Extends, child);
                    }
                }

                foreach (var kvp in manipulators.Children)
                {
                    if (skillIndex > 2) break;
                    if (kvp.Key.Equals("PrimaryWeapon", StringComparison.OrdinalIgnoreCase)) continue;
                    string manipulatorPath = ResolveManipulatorRuntimeType(kvp.Value);
                    if (string.IsNullOrEmpty(manipulatorPath))
                        manipulatorPath = ResolveAuthoredChildPath(spawnGcType, "Manipulators." + kvp.Key) ?? ResolveAuthoredChildPath(baseGcType, "Manipulators." + kvp.Key);
                    if (string.IsNullOrEmpty(manipulatorPath) && IsSkillManipulatorPath(kvp.Value.Extends))
                        manipulatorPath = kvp.Value.Extends;
                    if (string.IsNullOrEmpty(manipulatorPath)) continue;
                    if (!IsSkillManipulatorPath(manipulatorPath) && !IsSkillManipulatorPath(kvp.Value.Extends)) continue;
                    result[$"skill{skillIndex++}"] = CreateSpawnManipulator(manipulatorPath, kvp.Value);
                }
            }
            return result.Count > 0 ? result : fallback;
        }

        private string ResolveManipulatorRuntimeType(GCNode manipulatorNode)
        {
            if (manipulatorNode == null) return null;
            if (!string.IsNullOrEmpty(manipulatorNode.Extends))
                return manipulatorNode.Extends;
            return null;
        }

        private bool IsSkillManipulatorPath(string path)
        {
            return !string.IsNullOrEmpty(path) && path.StartsWith("skills.", StringComparison.OrdinalIgnoreCase);
        }

        private ManipulatorData CreateSpawnManipulator(string gcType, GCNode node)
        {
            var data = new ManipulatorData { gcType = gcType };
            CopyManipulatorProperties(data, ResolveAuthoredWeaponDescription(node));
            CopyManipulatorProperties(data, node);
            CopyManipulatorProperties(data, node?.GetChild("Description"));
            return data;
        }

        private void CopyManipulatorProperties(ManipulatorData data, GCNode node)
        {
            if (data == null || node == null) return;
            foreach (var kvp in node.Properties)
                data.properties[kvp.Key] = kvp.Value;
        }

        private GCNode GetAuthoredWeaponDescription(GCNode creatureNode)
        {
            var manipulators = creatureNode?.GetChild("Manipulators");
            var weapon = manipulators?.GetChild("PrimaryWeapon");
            return ResolveAuthoredWeaponDescription(weapon);
        }

        private GCNode GetAuthoredBehaviorDescription(GCNode creatureNode)
        {
            var behavior = creatureNode?.GetChild("Behavior");
            return ResolveAuthoredBehaviorDescription(behavior);
        }

        private GCNode ResolveAuthoredBehaviorDescription(GCNode behaviorNode)
        {
            return ResolveAuthoredBehaviorDescription(behaviorNode, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        private GCNode ResolveAuthoredBehaviorDescription(GCNode behaviorNode, HashSet<string> visited)
        {
            if (behaviorNode == null) return null;
            string key = behaviorNode.Name + "|" + (behaviorNode.Extends ?? "");
            if (!visited.Add(key)) return behaviorNode.GetChild("Description") ?? behaviorNode;

            GCNode baseDescription = null;
            if (!string.IsNullOrEmpty(behaviorNode.Extends))
            {
                var baseBehavior = GCDatabase.Instance?.ResolveWithInheritance(behaviorNode.Extends);
                baseDescription = ResolveAuthoredBehaviorDescription(baseBehavior, visited);
            }

            var description = behaviorNode.GetChild("Description") ?? behaviorNode;
            return baseDescription == null ? description : MergeAuthoredNodes(baseDescription, description);
        }

        private GCNode ResolveAuthoredWeaponDescription(GCNode weaponNode)
        {
            return ResolveAuthoredWeaponDescription(weaponNode, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        private GCNode ResolveAuthoredWeaponDescription(GCNode weaponNode, HashSet<string> visited)
        {
            if (weaponNode == null) return null;
            string key = weaponNode.Name + "|" + (weaponNode.Extends ?? "");
            if (!visited.Add(key)) return weaponNode.GetChild("Description") ?? weaponNode;

            GCNode baseDescription = null;
            if (!string.IsNullOrEmpty(weaponNode.Extends))
            {
                var baseWeapon = GCDatabase.Instance?.ResolveWithInheritance(weaponNode.Extends);
                baseDescription = ResolveAuthoredWeaponDescription(baseWeapon, visited);
            }

            var description = weaponNode.GetChild("Description") ?? weaponNode;
            return baseDescription == null ? description : MergeAuthoredNodes(baseDescription, description);
        }

        private GCNode MergeAuthoredNodes(GCNode parent, GCNode child)
        {
            if (parent == null) return child;
            if (child == null) return parent;

            var merged = new GCNode
            {
                Name = child.Name,
                Extends = child.Extends ?? parent.Extends,
                IsStatic = child.IsStatic || parent.IsStatic,
                IsAnonymous = child.IsAnonymous || parent.IsAnonymous,
                SourceFile = child.SourceFile
            };

            foreach (var kvp in parent.Properties) merged.Properties[kvp.Key] = kvp.Value;
            foreach (var kvp in child.Properties) merged.Properties[kvp.Key] = kvp.Value;

            foreach (var kvp in parent.Children) merged.Children[kvp.Key] = kvp.Value;
            foreach (var kvp in child.Children)
            {
                merged.Children[kvp.Key] = merged.Children.TryGetValue(kvp.Key, out var existing)
                    ? MergeAuthoredNodes(existing, kvp.Value)
                    : kvp.Value;
            }

            foreach (var entry in parent.AnonymousChildren) merged.AnonymousChildren.Add(entry);
            foreach (var entry in child.AnonymousChildren) merged.AnonymousChildren.Add(entry);

            return merged;
        }

        private float GetAuthoredFloat(GCNode node, string property, float fallback)
        {
            return node != null && node.HasProperty(property) ? node.GetFloat(property, fallback) : fallback;
        }

        private string GetAuthoredString(GCNode node, string property, string fallback)
        {
            return node != null && node.HasProperty(property) ? node.GetString(property, fallback) : fallback;
        }

        private float GetAuthoredBehaviorFloat(GCNode creatureNode, string property, float fallback)
        {
            var desc = GetAuthoredBehaviorDescription(creatureNode);
            return GetAuthoredFloat(desc, property, fallback);
        }

        private float GetAuthoredBehaviorFloat(GCNode behaviorNode, GCNode creatureNode, string property, float fallback)
        {
            var behaviorDesc = ResolveAuthoredBehaviorDescription(behaviorNode);
            if (behaviorDesc != null && behaviorDesc.HasProperty(property))
                return behaviorDesc.GetFloat(property, fallback);
            return GetAuthoredBehaviorFloat(creatureNode, property, fallback);
        }

        private string GetAuthoredBehaviorString(GCNode behaviorNode, GCNode creatureNode, string property, string fallback)
        {
            var behaviorDesc = ResolveAuthoredBehaviorDescription(behaviorNode);
            if (behaviorDesc != null && behaviorDesc.HasProperty(property))
                return behaviorDesc.GetString(property, fallback);
            var creatureDesc = GetAuthoredBehaviorDescription(creatureNode);
            return GetAuthoredString(creatureDesc, property, fallback);
        }

        private bool ShouldRegisterWander(GCNode behaviorNode, GCNode creatureNode, string zoneName)
        {
            string idleAction = GetAuthoredString(creatureNode, "IdleAction", "");
            if (string.IsNullOrEmpty(idleAction))
                idleAction = GetAuthoredBehaviorString(behaviorNode, creatureNode, "IdleAction", "");
            if (!string.IsNullOrEmpty(idleAction))
            {
                if (idleAction.Equals("WANDER", StringComparison.OrdinalIgnoreCase) ||
                    idleAction.Equals("FOLLOW", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (idleAction.Equals("GUARD", StringComparison.OrdinalIgnoreCase) ||
                    idleAction.Equals("NOTHING", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return GetBehaviorMode(zoneName) != "guard";
        }

        private float GetAuthoredLeashRange(GCNode behaviorNode, GCNode creatureNode, string tier)
        {
            if (TryGetAuthoredLeashRange(ResolveAuthoredBehaviorDescription(behaviorNode), out float leashRange))
                return leashRange;
            if (TryGetAuthoredLeashRange(GetAuthoredBehaviorDescription(creatureNode), out leashRange))
                return leashRange;
            return GetLeashRangeForTier(tier);
        }

        private bool TryGetAuthoredLeashRange(GCNode behaviorDesc, out float leashRange)
        {
            leashRange = 0f;
            if (behaviorDesc == null) return false;
            if (behaviorDesc.HasProperty("Leashed") && !behaviorDesc.GetBool("Leashed", true))
                return true;
            if (behaviorDesc.HasProperty("LeashRange"))
            {
                leashRange = behaviorDesc.GetFloat("LeashRange", 0f);
                return true;
            }
            return false;
        }

        private float GetAuthoredMoveSpeed(GCNode authoredDesc, CreatureData creature)
        {
            float speed = GetAuthoredFloat(authoredDesc, "Speed", float.NaN);
            if (!float.IsNaN(speed)) return speed;
            return GetMoveSpeedFromCreature(creature);
        }

        private float GetAuthoredWalkSpeed(GCNode authoredCreature, GCNode authoredDesc, CreatureData creature)
        {
            float speed = GetAuthoredFloat(authoredDesc, "WalkSpeed", float.NaN);
            if (!float.IsNaN(speed)) return speed;
            speed = GetAuthoredFloat(authoredCreature, "WalkSpeed", float.NaN);
            if (!float.IsNaN(speed)) return speed;
            speed = GetAuthoredFloat(GCDatabase.Instance?.ResolveWithInheritance("creatures.base.UnitStock"), "WalkSpeed", float.NaN);
            if (!float.IsNaN(speed)) return speed;
            return Mathf.Min(GetMoveSpeedFromCreature(creature), 25f);
        }

        private float GetAuthoredWanderRange(GCNode behaviorNode, GCNode creatureNode)
        {
            float range = GetAuthoredFloat(creatureNode, "WanderRange", float.NaN);
            if (!float.IsNaN(range)) return range;
            range = GetAuthoredBehaviorFloat(behaviorNode, creatureNode, "WanderRange", float.NaN);
            if (!float.IsNaN(range)) return range;
            return 0f;
        }

        private float GetAuthoredAttackRange(GCNode authoredDesc, GCNode authoredWeapon, CreatureData creature)
        {
            float weaponRange = GetAuthoredFloat(authoredWeapon, "Range", float.NaN);
            if (!float.IsNaN(weaponRange)) return weaponRange;
            float attackRange = GetAuthoredFloat(authoredDesc, "AttackRange", float.NaN);
            if (!float.IsNaN(attackRange)) return attackRange;
            return GetAttackRangeFromManipulators(creature);
        }

        private float GetAuthoredAttackLeadDelay(GCNode authoredDesc, float fallback, out float soundLeadDelay)
        {
            soundLeadDelay = Mathf.Max(0.1f, fallback * (10f / 15f));
            string animationsPath = GetAuthoredString(authoredDesc, "Animations", "");
            if (string.IsNullOrEmpty(animationsPath)) return fallback;

            var animations = GCDatabase.Instance.ResolveWithInheritance(animationsPath);
            if (animations == null || animations.AnonymousChildren == null || animations.AnonymousChildren.Count == 0) return fallback;

            float firstTrigger = float.NaN;
            foreach (var animation in animations.AnonymousChildren)
            {
                float trigger = animation.GetFloat("TriggerTime", float.NaN);
                if (float.IsNaN(trigger) || trigger <= 0f) continue;
                if (float.IsNaN(firstTrigger)) firstTrigger = trigger;
                int id = animation.GetInt("ID", 0);
                if (id >= 110 && id <= 119)
                {
                    float soundTrigger = animation.GetFloat("SoundTriggerTime", float.NaN);
                    if (!float.IsNaN(soundTrigger) && soundTrigger > 0f)
                        soundLeadDelay = Mathf.Max(0.1f, soundTrigger / 30f);
                    return Mathf.Max(0.1f, trigger / 30f);
                }
            }

            return float.IsNaN(firstTrigger) ? fallback : Mathf.Max(0.1f, firstTrigger / 30f);
        }

        private bool HasAuthoredAttackSound(GCNode authoredDesc)
        {
            string soundsPath = GetAuthoredString(authoredDesc, "Sounds", "");
            if (string.IsNullOrEmpty(soundsPath)) return false;
            var sounds = GCDatabase.Instance.ResolveWithInheritance(soundsPath);
            if (sounds?.AnonymousChildren == null) return false;
            foreach (var sound in sounds.AnonymousChildren)
            {
                string soundId = sound.GetString("SoundId", "");
                if (!string.Equals(soundId, "ATTACK", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(soundId, "WEAPONATTACK", StringComparison.OrdinalIgnoreCase))
                    continue;
                return !string.IsNullOrWhiteSpace(sound.GetString("Sounds", ""));
            }
            return false;
        }

        private float GetManipulatorFloat(CreatureData creature, string propName, float fallback)
        {
            if (creature.manipulators == null) return fallback;
            foreach (var manip in creature.manipulators.Values)
            {
                if (manip.properties != null && manip.properties.TryGetValue(propName, out string val))
                {
                    if (float.TryParse(val, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float result))
                        return result;
                }
            }
            return fallback;
        }
        public List<Monster> SpawnFactionGroup(string faction, string tier, float centerX, float centerY, float centerZ, int count, float radius = 10f, string zoneName = null)
        {
            var spawned = new List<Monster>();
            var creatures = DatabaseLoader.GetCreaturesByFaction(faction);

            if (!string.IsNullOrEmpty(tier))
                creatures = creatures.FindAll(c => c.tier.Equals(tier, StringComparison.OrdinalIgnoreCase));

            if (creatures.Count == 0)
            {
                Debug.LogError($"[Combat] No creatures for faction '{faction}' tier '{tier}'");
                return spawned;
            }

            for (int i = 0; i < count; i++)
            {
                var randomCreature = creatures[UnityEngine.Random.Range(0, creatures.Count)];
                float angle = UnityEngine.Random.Range(0f, 360f) * Mathf.Deg2Rad;
                float dist = UnityEngine.Random.Range(0f, radius);
                float px = centerX + Mathf.Cos(angle) * dist;
                float pz = centerZ + Mathf.Sin(angle) * dist;

                var monster = SpawnMonster(randomCreature.gcType, px, centerY, pz, angle * Mathf.Rad2Deg, zoneName);
                if (monster != null)
                    spawned.Add(monster);
            }

            return spawned;
        }
        public Monster GetMonsterByComponent(uint componentId)
        {
            if (_componentToEntityMap.TryGetValue(componentId, out uint entityId))
                return GetMonster(entityId);
            return null;
        }
        public void ResetAllMonsters()
        {
            foreach (var kvp in _activeMonsters)
            {
                kvp.Value.IsAlive = true;
                SetRuntimeMonsterHPWire(kvp.Value, kvp.Value.MaxHPWire, false, Time.time, "RESET");
                kvp.Value.ClientHPDirty = false;
                kvp.Value.ClientHPDirtySince = 0f;
                kvp.Value.ClientHPDirtyReason = null;
                kvp.Value.ClientHPDirtyStartWire = 0;
                kvp.Value.ClientHPDirtyAllowSameConfirmAt = 0f;
                kvp.Value.SuppressedMonsterHPSyncPackets = 0;
                kvp.Value.LastClientHPReportTime = 0f;
                kvp.Value.LastClientHPReportWire = kvp.Value.CurrentHPWire;
                kvp.Value.UseTargetCount = 0;
            }
            Debug.LogError($"[Combat] Reset {_activeMonsters.Count} monsters for zone transition");
        }
        /// <summary>
        /// Returns the component offset (0=Entity, 1=Behavior, 2=Skills, 3=Manipulators, 4=Modifiers, 5=Unit)
        /// Returns -1 if the CID is not a known monster component.
        /// </summary>
        public int GetComponentOffset(uint componentId)
        {
            if (_componentToEntityMap.TryGetValue(componentId, out uint entityId))
                return (int)(componentId - entityId);
            return -1;
        }
        public void DespawnMonster(uint entityId, bool allowRespawn = true)
        {
            if (!_activeMonsters.TryGetValue(entityId, out var monster))
                return;

            _componentToEntityMap.Remove(monster.EntityId);
            _componentToEntityMap.Remove(monster.BehaviorId);
            _componentToEntityMap.Remove(monster.SkillsId);
            _componentToEntityMap.Remove(monster.ManipulatorsId);
            _componentToEntityMap.Remove(monster.ModifiersId);
            _componentToEntityMap.Remove(monster.UnitId);
            _monsterRuntimeHPWire.Remove(entityId);
            _monsterRuntimeDamageCommitted.Remove(entityId);
            _monsterHPRegenLastTime.Remove(entityId);
            _monsterHPRegenCarryWire.Remove(entityId);

            _activeMonsters.Remove(entityId);
            WanderSimulator.Instance.UnregisterEntity(entityId);
            OnMonsterDespawned?.Invoke(entityId);

            if (allowRespawn)
            {
                _respawnQueue.Add(new RespawnEntry
                {
                    GCType = monster.GCType,
                    ZoneName = monster.ZoneName,
                    PosX = monster.SpawnPosX,
                    PosY = monster.SpawnPosY,
                    PosZ = monster.SpawnPosZ,
                    Heading = monster.Heading,
                    EncounterGroupKey = monster.EncounterGroupKey,
                    RespawnTime = Time.time + 30f
                });
            }
        }
        public void RemoveComponentMapping(uint componentId)
        {
            _componentToEntityMap.Remove(componentId);
        }

        public void AddComponentMapping(uint componentId, uint entityId)
        {
            _componentToEntityMap[componentId] = entityId;
        }
        public Monster GetMonster(uint entityId)
        {
            return _activeMonsters.TryGetValue(entityId, out var m) ? m : null;
        }

        public bool IsMonster(uint entityId)
        {
            return _activeMonsters.ContainsKey(entityId);
        }

        public IEnumerable<Monster> GetActiveMonsters() => _activeMonsters.Values;

        public Monster GetNearestMonster(float posX, float posY, float maxRange = 50f)
        {
            Monster nearest = null;
            float nearestDist = maxRange * maxRange;

            Debug.LogError($"[GetNearest] Searching for monster near ({posX:F1},{posY:F1}) range={maxRange} monsters={_activeMonsters.Count}");

            foreach (var monster in _activeMonsters.Values)
            {
                if (!IsMonsterCombatSelectable(monster)) continue;

                float dx = monster.PosX - posX;
                float dy = monster.PosY - posY;
                float distSq = dx * dx + dy * dy;
                float dist = Mathf.Sqrt(distSq);

                Debug.LogError($"[GetNearest]   {monster.Name} pos=({monster.PosX:F1},{monster.PosY:F1}) dist={dist:F1}");

                if (distSq < nearestDist)
                {
                    nearestDist = distSq;
                    nearest = monster;
                }
            }

            if (nearest != null)
                Debug.LogError($"[GetNearest] → Found: {nearest.Name} at ({nearest.PosX:F1},{nearest.PosY:F1})");
            else
                Debug.LogError($"[GetNearest] → NONE within range {maxRange}");

            return nearest;
        }

        private bool IsMonsterCombatSelectable(Monster monster)
        {
            return monster != null && monster.IsAlive && GetRuntimeMonsterHPWire(monster, "SELECT") > 0;
        }

        public Monster FindMonsterForTarget(ushort clientTargetId, float playerPosX, float playerPosY)
        {
            Debug.LogError($"[Combat] FindMonsterForTarget target={clientTargetId} player=({playerPosX:F1}, {playerPosY:F1}) active={_activeMonsters.Count}");

            var monster = GetMonster(clientTargetId);
            if (monster != null)
            {
                if (!IsMonsterCombatSelectable(monster))
                {
                    Debug.LogError($"[Combat] Target {clientTargetId} resolved to inactive monster {monster.Name} alive={monster.IsAlive} HP={monster.CurrentHPWire / 256}/{monster.MaxHPWire / 256}");
                    return null;
                }
                Debug.LogError($"[Combat] Found monster by EntityId: {clientTargetId}");
                return monster;
            }

            if (_componentToEntityMap.TryGetValue(clientTargetId, out uint entityId))
            {
                monster = GetMonster(entityId);
                if (monster != null)
                {
                    if (!IsMonsterCombatSelectable(monster))
                    {
                        Debug.LogError($"[Combat] Target component {clientTargetId} resolved to inactive monster {monster.Name} alive={monster.IsAlive} HP={monster.CurrentHPWire / 256}/{monster.MaxHPWire / 256}");
                        return null;
                    }
                    Debug.LogError($"[Combat] Found monster by ComponentId: {clientTargetId} -> EntityId: {entityId}");
                    return monster;
                }
            }

            if (_clientToServerIdMap.TryGetValue(clientTargetId, out uint serverId))
            {
                monster = GetMonster(serverId);
                if (monster != null)
                {
                    if (!IsMonsterCombatSelectable(monster))
                    {
                        Debug.LogError($"[Combat] Learned target {clientTargetId} resolved to inactive monster {monster.Name} alive={monster.IsAlive} HP={monster.CurrentHPWire / 256}/{monster.MaxHPWire / 256}");
                        return null;
                    }
                    Debug.LogError($"[Combat] Found monster by learned ClientId: {clientTargetId} -> EntityId: {serverId}");
                    return monster;
                }
            }

            Debug.LogError($"[Combat] Target {clientTargetId} not found by any ID, checking nearby monster only");
            monster = GetNearestMonster(playerPosX, playerPosY, 30f);

            if (monster != null)
            {
                _clientToServerIdMap[clientTargetId] = monster.EntityId;
                Debug.LogError($"[Combat] LEARNED MAPPING: ClientId {clientTargetId} -> EntityId {monster.EntityId} ({monster.Name})");
            }

            return monster;
        }

        public DamageResult ApplyDamage(uint attackerId, uint defenderId, int damageAmount)
        {
            var monster = GetMonster(defenderId);
            uint damageWire = (uint)Math.Max(1, damageAmount) * 256u;
            bool applied = ApplyNativePlayerDamageToMonsterWire(monster, damageWire, "ApplyDamage", out uint oldHPWire, out uint newHPWire, out bool died);
            if (!applied)
                return new DamageResult { Success = false };
            int appliedDamage = (int)(((oldHPWire > newHPWire ? oldHPWire - newHPWire : 0u) + 255u) / 256u);

            return new DamageResult
            {
                Success = true,
                DamageDealt = appliedDamage,
                IsCritical = false,
                DefenderDied = died,
                NewHPWire = newHPWire
            };
        }

        private float Distance2D(float x1, float y1, float x2, float y2)
        {
            float dx = x2 - x1;
            float dy = y2 - y1;
            return Mathf.Sqrt(dx * dx + dy * dy);
        }

        private float ResolveMonsterEffectiveAttackRange(Monster monster)
        {
            if (monster == null || monster.AttackRange <= 0f) return 0f;
            return monster.AttackRange + Mathf.Max(0f, monster.CollisionRadius) + ResolveAvatarCombatRadius();
        }

        public float ResolvePlayerMeleeRange(PlayerState state, Monster monster)
        {
            float weaponRange = state != null && state.WeaponRange > 0 ? state.WeaponRange : monster != null ? monster.AttackRange : 0f;
            float monsterRadius = monster != null ? Mathf.Max(0f, monster.CollisionRadius) : 0f;
            return Mathf.Max(1f, weaponRange) + 10f + ResolveAvatarCombatRadius() + monsterRadius;
        }

        public float ResolvePlayerMeleeNativeContactRange(PlayerState state, Monster monster)
        {
            return ResolvePlayerMeleeRange(state, monster);
        }

        private float ResolveNativeClientContactRange(Monster monster, CombatPlayer player, float allowedRange)
        {
            if (monster == null || player == null) return 0f;
            float playerRange = ResolvePlayerMeleeRange(player.PlayerState, monster);
            float tolerance = monster.ClientSyncTolerance > 0f ? monster.ClientSyncTolerance : 10f;
            return Mathf.Max(allowedRange, playerRange) + tolerance;
        }

        private bool IsNativeClientCombatContact(Monster monster, CombatPlayer target, float dist, float allowedRange)
        {
            if (HasCombatContact(monster, target)) return true;
            if (monster == null || target == null || !target.HasActiveClientAttack) return false;
            if (target.ActiveClientAttackTargetId != 0 && target.ActiveClientAttackTargetId != monster.EntityId) return false;
            float contactRange = ResolveNativeClientContactRange(monster, target, allowedRange);
            return contactRange > 0f && dist <= contactRange;
        }

        private float ResolveAvatarCombatRadius()
        {
            if (_avatarCombatRadius.HasValue) return _avatarCombatRadius.Value;
            float radius = 3f;
            var avatar = GCDatabase.Instance?.ResolveWithInheritance("avatar.base.avatar");
            var desc = avatar?.GetChild("Description") ?? avatar;
            radius = GetAuthoredFloat(desc, "CollisionRadius", radius);
            var bounds = avatar?.GetChild("Object")?.GetChild("Description") ?? avatar?.GetChild("Object");
            if (bounds != null)
            {
                float boundsRadius = Mathf.Max(
                    Mathf.Abs(GetAuthoredFloat(bounds, "MinX", 0f)),
                    Mathf.Abs(GetAuthoredFloat(bounds, "MaxX", 0f)),
                    Mathf.Abs(GetAuthoredFloat(bounds, "MinY", 0f)),
                    Mathf.Abs(GetAuthoredFloat(bounds, "MaxY", 0f)));
                if (boundsRadius > 0f) radius = boundsRadius;
            }
            _avatarCombatRadius = Mathf.Max(0f, radius);
            return _avatarCombatRadius.Value;
        }

        private float ResolveMonsterAttackWindup(Monster monster)
        {
            ResolveMonsterAttackAnimationFrames(monster, out int totalFrames, out int hitFrame, out int soundFrame);
            return Mathf.Max(1f / 30f, ResolveMonsterAttackFrameSeconds(monster, hitFrame));
        }

        private float ResolveMonsterAttackSoundDelay(Monster monster, float windup)
        {
            ResolveMonsterAttackAnimationFrames(monster, out int totalFrames, out int hitFrame, out int soundFrame);
            float soundDelay = ResolveMonsterAttackFrameSeconds(monster, soundFrame);
            return Mathf.Clamp(soundDelay, 1f / 30f, Mathf.Max(1f / 30f, windup));
        }

        private void ResolveMonsterAttackAnimationFrames(Monster monster, out int totalFrames, out int hitFrame, out int soundFrame)
        {
            byte attackIndex = monster != null ? monster.AttackAnimationIndex : (byte)0;
            if (attackIndex == 2)
            {
                totalFrames = 43;
                hitFrame = 26;
                soundFrame = 12;
                return;
            }

            totalFrames = 30;
            hitFrame = 15;
            soundFrame = 10;
        }

        private float ResolveMonsterAttackFrameSeconds(Monster monster, int frame)
        {
            float cooldown = monster != null ? Mathf.Max(0.1f, monster.AttackCooldown) : 1.75f;
            int cooldownField = Mathf.Max(1, Mathf.RoundToInt(cooldown * 100f));
            int ticks = Mathf.Max(1, (frame * 100) / cooldownField);
            return ticks / 30f;
        }

        private void AdvanceMonsterAttackAnimation(Monster monster)
        {
            if (monster == null || _roomRng == null) return;
            uint useRaw = _roomRng.Generate();
            uint previous = monster.AttackAnimationIndex;
            monster.AttackUseRaw = useRaw;
            monster.AttackAnimationIndex = (byte)(((useRaw & 1u) + previous + 1u) % 3u);
        }

        private void ConsumeMonsterAttackSoundRng(Monster monster)
        {
            if (monster == null || !monster.AttackSoundPending) return;
            monster.AttackSoundPending = false;
            if (_roomRng != null)
            {
                uint soundRaw = _roomRng.Generate();
                monster.AttackSoundRaw = soundRaw;
                Debug.LogError($"[MON-ATTACK] {monster.Name} soundRng=0x{soundRaw:X8} rngPos={_roomRng.CallsSinceReseed}");
            }
            else
            {
                Debug.LogError($"[MON-ATTACK] {monster.Name} soundRng unavailable");
            }
        }

        private bool AttackCommitTargetStillValid(Monster monster, CombatPlayer target)
        {
            if (monster == null || target == null) return false;
            float tolerance = monster.ClientSyncTolerance > 0f ? monster.ClientSyncTolerance : 10f;
            return Distance2D(monster.AttackCommitTargetX, monster.AttackCommitTargetY, target.PosX, target.PosY) <= tolerance;
        }

        private bool HasCombatContact(Monster monster, CombatPlayer target)
        {
            return monster != null && target != null && monster.CombatContactTargetId == target.EntityId && Time.time <= monster.CombatContactUntil;
        }

        private float ResolveMonsterDamageTable(byte level)
        {
            float lv = Mathf.Clamp(level, 1, 110);
            if (lv <= 25f)
                return 12.7f + (lv - 1f) * (86.625f - 12.7f) / 24f;
            return 86.625f + (lv - 25f) * (372.12f - 86.625f) / 85f;
        }

        private int ResolveMonsterAttackRating(Monster monster)
        {
            float level = Mathf.Clamp(monster.Level, 1, 110);
            float tableRating = 100f + ((level - 1f) * (32800f - 100f) / 109f);
            float rating = monster.AttackRating > 10f ? monster.AttackRating : tableRating * Mathf.Max(0f, monster.AttackRating);
            return Mathf.Max(0, Mathf.RoundToInt(rating));
        }

        private int ResolveAvatarDefenseRating(PlayerState state)
        {
            if (state == null) return 0;
            float defensePerStrength = ServerSettings.GetFloat("defenseRatingPerStrength", GCDatabase.Instance.GetKnob("DefenseRatingPerStrength", 14f));
            return Mathf.Max(0, Mathf.RoundToInt(state.Strength * defensePerStrength));
        }

        private int ResolveHitThreshold(int attackRating, int defenseRating, int attackerLevel, int defenderLevel)
        {
            int attack = Mathf.Max(0, attackRating);
            int defense = Mathf.Max(0, defenseRating);
            int chancePercent = attack + defense == 0 ? 0 : (attack * 100) / (attack + defense);
            int threshold = chancePercent << 8;
            int levelDelta = Mathf.Clamp(defenderLevel, 0, 110) - Mathf.Clamp(attackerLevel, 0, 110);
            threshold -= levelDelta * 0x500;
            if (threshold < 0x0A00) threshold = 0x0A00;
            if (threshold > 0x6464) threshold = 0x6464;
            return threshold;
        }

        private uint ResolveMonsterDamageWire(Monster monster, uint damageRaw, out int minDamage, out int maxDamage, out int averageDamage)
        {
            float weaponDamagePerLevel = ServerSettings.GetFloat("weaponDamagePerLevel", GCDatabase.Instance.GetKnob("WeaponDamagePerLevel", 10f));
            float baseDamage = weaponDamagePerLevel * Mathf.Clamp(monster.Level, 1, 110);
            int normalized = DamageComputer.FromFloat(baseDamage);
            normalized = DamageComputer.FixedMul(normalized, DamageComputer.FromFloat(Mathf.Max(0.01f, monster.WeaponDamage)));
            normalized = DamageComputer.FixedMul(normalized, DamageComputer.FromFloat(monster.DamageMod > 0f ? monster.DamageMod : 1f));
            if (normalized < 0x100) normalized = 0x100;

            int volatility = DamageComputer.FromFloat(Mathf.Clamp(monster.DamageVolatility, 0f, 0.95f));
            int spread = DamageComputer.FixedMul(normalized, volatility);
            minDamage = DamageComputer.RoundFixed32(normalized - spread);
            maxDamage = DamageComputer.RoundFixed32(normalized + spread);
            if (minDamage < 0x100) minDamage = 0x100;
            if (maxDamage < 0x100) maxDamage = 0x100;
            if (maxDamage < minDamage) maxDamage = minDamage;

            averageDamage = normalized;
            uint range = (uint)Math.Max(0, maxDamage - minDamage + 1);
            return (uint)minDamage + (range > 0 ? damageRaw % range : 0u);
        }

        private void ProcessProximityAggro()
        {
            if (_players.Count == 0) return;
            var pathMaps = new Dictionary<string, PathMap>(StringComparer.OrdinalIgnoreCase);
            foreach (var monster in _activeMonsters.Values)
            {
                PathMap pathMap = null;
                if (!string.IsNullOrWhiteSpace(monster.ZoneName))
                {
                    if (!pathMaps.TryGetValue(monster.ZoneName, out pathMap))
                    {
                        pathMap = PathMapManager.Instance.GetPathMap(monster.ZoneName);
                        pathMaps[monster.ZoneName] = pathMap;
                    }
                }
                float range = ResolveMonsterTargetSearchRange(monster, pathMap != null);
                if (!monster.IsAlive || monster.AggroTriggered || monster.TargetId != 0 || range <= 0f) continue;

                CombatPlayer nearest = null;
                float nearestSq = float.MaxValue;
                float rangeSq = range * range;
                foreach (var player in _players.Values)
                {
                    if (player == null || !player.IsAlive || player.PlayerState == null || player.PlayerState.CurrentHPWire == 0) continue;
                    float dx = player.PosX - monster.PosX;
                    float dy = player.PosY - monster.PosY;
                    float distSq = dx * dx + dy * dy;
                    if (distSq > rangeSq || distSq >= nearestSq) continue;
                    if (pathMap != null && !pathMap.CanReachPoint(monster.PosX, monster.PosY, player.PosX, player.PosY)) continue;
                    nearest = player;
                    nearestSq = distSq;
                }

                if (nearest != null)
                {
                    AggroMonster(monster, nearest, "proximity", false);
                }
            }
        }

        private float ResolveMonsterTargetSearchRange(Monster monster, bool hasPathMap)
        {
            if (monster == null) return 0f;
            if (monster.AggroRange > 0f) return monster.AggroRange;
            return monster.PerceptionRange;
        }

        private void ProcessMonsterMovement(float deltaTime)
        {
            ProcessMonsterMovement(deltaTime, 0);
        }

        private void ProcessMonsterMovement(float deltaTime, uint playerEntityId)
        {
            if (deltaTime <= 0f) return;
            foreach (var monster in _activeMonsters.Values)
            {
                if (!monster.IsAlive || !monster.AggroTriggered || monster.TargetId == 0) continue;
                if (playerEntityId != 0 && monster.TargetId != playerEntityId) continue;
                if (!_players.TryGetValue(monster.TargetId, out var target) || target == null || !target.IsAlive || target.PlayerState == null) continue;
                if (target.PlayerState.CurrentHPWire == 0) continue;
                if (monster.AttackPending) continue;

                float allowedRange = ResolveMonsterEffectiveAttackRange(monster);
                if (allowedRange <= 0f) continue;
                float speed = monster.MoveSpeed;
                if (speed <= 0f) continue;

                float dx = target.PosX - monster.PosX;
                float dy = target.PosY - monster.PosY;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);
                if (dist <= allowedRange || dist <= 0.001f)
                {
                    if (monster.State == MonsterState.Chase)
                        monster.State = MonsterState.Combat;
                    monster.CombatContactTargetId = target.EntityId;
                    monster.CombatContactUntil = Time.time + Mathf.Max(0.25f, ResolveMonsterAttackWindup(monster) + 0.25f);
                    continue;
                }

                float step = Mathf.Min(dist - allowedRange, speed * deltaTime);
                if (step <= 0f) continue;
                monster.PosX += dx / dist * step;
                monster.PosY += dy / dist * step;
                monster.Heading = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg;
                monster.State = MonsterState.Chase;
                OnMonsterPositionChanged?.Invoke(monster);
            }
        }

        private void ProcessMonsterAttacks(float deltaTime)
        {
            ProcessMonsterAttacks(deltaTime, 0);
        }

        private void ProcessMonsterAttacks(float deltaTime, uint playerEntityId)
        {
            if (_roomRng == null) return;

            foreach (var monster in _activeMonsters.Values)
            {
                if (!monster.IsAlive || !monster.AggroTriggered) continue;
                if (playerEntityId != 0 && monster.TargetId != playerEntityId) continue;
                if (!_players.TryGetValue(monster.TargetId, out var target) || !target.IsAlive || target.PlayerState == null) continue;
                if (target.PlayerState.CurrentHPWire == 0)
                {
                    target.IsAlive = false;
                    monster.AttackPending = false;
                    monster.AttackSoundPending = false;
                    continue;
                }

                float allowedRange = ResolveMonsterEffectiveAttackRange(monster);
                if (allowedRange <= 0f) continue;
                float dist = Distance2D(monster.PosX, monster.PosY, target.PosX, target.PosY);
                bool nativeClientContact = IsNativeClientCombatContact(monster, target, dist, allowedRange);
                float cooldown = Mathf.Max(0.1f, monster.AttackCooldown);

                if (monster.AttackPending)
                {
                    if (monster.AttackSoundPending && Time.time >= monster.AttackSoundTime)
                        ConsumeMonsterAttackSoundRng(monster);
                    if (Time.time < monster.AttackCommitTime) continue;
                    if (monster.AttackSoundPending)
                        ConsumeMonsterAttackSoundRng(monster);
                    monster.AttackPending = false;
                    monster.AttackSoundPending = false;
                    monster.State = MonsterState.Combat;
                    float commitDelta = Distance2D(monster.AttackCommitTargetX, monster.AttackCommitTargetY, target.PosX, target.PosY);
                    bool commitTargetStillValid = dist <= allowedRange;
                    if (!commitTargetStillValid)
                    {
                        monster.CombatContactTargetId = 0;
                        monster.CombatContactUntil = 0f;
                        monster.AttackClientVisible = false;
                        monster.State = MonsterState.Chase;
                        Debug.LogError($"[MON-DAMAGE] {monster.Name}#{monster.EntityId}->{target.Name} cancel movedOut dist={dist:F1} commitDelta={commitDelta:F1} monsterRange={allowedRange:F1} nativeContact={nativeClientContact} clientVisible={monster.AttackClientVisible}");
                        continue;
                    }
                    if (!monster.AttackClientVisible && !nativeClientContact)
                    {
                        monster.CombatContactTargetId = 0;
                        monster.CombatContactUntil = 0f;
                        monster.State = MonsterState.Chase;
                        Debug.LogError($"[MON-DAMAGE] {monster.Name}#{monster.EntityId}->{target.Name} cancel noNativeAttack dist={dist:F1} commitDelta={commitDelta:F1} monsterRange={allowedRange:F1}");
                        continue;
                    }
                    int attackRating = ResolveMonsterAttackRating(monster);
                    int defenseRating = ResolveAvatarDefenseRating(target.PlayerState);
                    int defenderLevel = Mathf.Clamp(target.PlayerState.Level - 1, 0, 110);
                    int attackerLevel = Mathf.Clamp(monster.Level, 0, 110);
                    int hitThreshold = ResolveHitThreshold(attackRating, defenseRating, attackerLevel, defenderLevel);
                    float hitChance = hitThreshold / 256f;
                    uint hitRaw = _roomRng.Generate();
                    uint blockRaw = _roomRng.Generate();
                    int hitRoll = (int)(hitRaw % 25700u);
                    bool hit = hitRoll < hitThreshold;
                    if (hit)
                    {
                        uint damageRaw = _roomRng.Generate();
                        uint damageWire = ResolveMonsterDamageWire(monster, damageRaw, out int minDamage, out int maxDamage, out int averageDamage);
                        if (target.PlayerState.HasAnyDamageImmunity)
                        {
                            Debug.LogError($"[MON-DAMAGE] {monster.Name}#{monster.EntityId}->{target.Name} immune dmg={damageWire / 256f:F2} hp={target.PlayerState.CurrentHPWire / 256f:F2}/{target.PlayerState.MaxHPWire / 256f:F2} range=[{minDamage / 256f:F2},{maxDamage / 256f:F2}] avg={averageDamage / 256f:F2} ar={attackRating} dr={defenseRating} levels={attackerLevel}->{defenderLevel} chance={hitChance:F1} rngPos={_roomRng.CallsSinceReseed} anim={monster.AttackAnimationIndex} use=0x{monster.AttackUseRaw:X8} sound=0x{monster.AttackSoundRaw:X8} hit=0x{hitRaw:X8}/{hitRoll} block=0x{blockRaw:X8} dmgRaw=0x{damageRaw:X8} dist={dist:F1}");
                        }
                        else
                        {
                            uint currentHPWire = target.PlayerState.CurrentHPWire;
                            target.PlayerState.TakeDamage(damageWire);
                            uint newHPWire = target.PlayerState.CurrentHPWire;
                            target.IsAlive = newHPWire > 0;
                            Debug.LogError($"[MON-DAMAGE] {monster.Name}#{monster.EntityId}->{target.Name} HIT dmg={damageWire / 256f:F2} hp={currentHPWire / 256f:F2}->{newHPWire / 256f:F2}/{target.PlayerState.MaxHPWire / 256f:F2} range=[{minDamage / 256f:F2},{maxDamage / 256f:F2}] avg={averageDamage / 256f:F2} ar={attackRating} dr={defenseRating} levels={attackerLevel}->{defenderLevel} chance={hitChance:F1} rngPos={_roomRng.CallsSinceReseed} anim={monster.AttackAnimationIndex} use=0x{monster.AttackUseRaw:X8} sound=0x{monster.AttackSoundRaw:X8} hit=0x{hitRaw:X8}/{hitRoll} block=0x{blockRaw:X8} dmgRaw=0x{damageRaw:X8} dist={dist:F1}");
                        }
                    }
                    else
                    {
                        Debug.LogError($"[MON-DAMAGE] {monster.Name}#{monster.EntityId}->{target.Name} miss ar={attackRating} dr={defenseRating} levels={attackerLevel}->{defenderLevel} chance={hitChance:F1} rngPos={_roomRng.CallsSinceReseed} anim={monster.AttackAnimationIndex} use=0x{monster.AttackUseRaw:X8} sound=0x{monster.AttackSoundRaw:X8} hit=0x{hitRaw:X8}/{hitRoll} threshold={hitThreshold} block=0x{blockRaw:X8} dist={dist:F1}");
                    }
                    continue;
                }
                else
                {
                    if (dist > allowedRange && !nativeClientContact) continue;
                    if (Time.time < monster.LastAttackTime) continue;
                    AdvanceMonsterAttackAnimation(monster);
                    float windup = ResolveMonsterAttackWindup(monster);
                    monster.AttackPending = true;
                    monster.AttackClientVisible = nativeClientContact;
                    monster.AttackSoundRaw = 0;
                    monster.AttackCommitTime = Time.time + windup;
                    monster.AttackSoundTime = Time.time + ResolveMonsterAttackSoundDelay(monster, windup);
                    monster.AttackSoundPending = true;
                    monster.AttackCommitTargetX = target.PosX;
                    monster.AttackCommitTargetY = target.PosY;
                    monster.LastAttackTime = Time.time + cooldown;
                    monster.State = MonsterState.Attacking;
                    monster.AttackSessionId++;
                    if (monster.AttackSessionId == 0) monster.AttackSessionId = 1;
                    OnMonsterAttackStarted?.Invoke(monster, target, monster.AttackSessionId);
                    Debug.LogError($"[MON-ATTACK] {monster.Name}->{target.Name} START anim={monster.AttackAnimationIndex} use=0x{monster.AttackUseRaw:X8} soundAt={monster.AttackSoundTime:F3} hitAt={monster.AttackCommitTime:F3}");
                    continue;
                }
            }
        }

        public void Update(float deltaTime)
        {
            ProcessMonsterMovement(deltaTime);
            ProcessProximityAggro();
            ProcessMonsterAttacks(deltaTime);
            for (int i = _respawnQueue.Count - 1; i >= 0; i--)
            {
                if (Time.time >= _respawnQueue[i].RespawnTime)
                {
                    SpawnMonster(_respawnQueue[i].GCType, _respawnQueue[i].PosX, _respawnQueue[i].PosY, _respawnQueue[i].PosZ, _respawnQueue[i].Heading, _respawnQueue[i].ZoneName, _respawnQueue[i].EncounterGroupKey);
                    _respawnQueue.RemoveAt(i);
                }
            }
        }

        public void ClearAll()
        {
            foreach (var id in new List<uint>(_activeMonsters.Keys))
                DespawnMonster(id, false);
            _respawnQueue.Clear();
            _players.Clear();
            _playerCombatAdvanceTime.Clear();
            _monsterRuntimeHPWire.Clear();
            _monsterRuntimeDamageCommitted.Clear();
            _monsterHPRegenLastTime.Clear();
            _monsterHPRegenCarryWire.Clear();
        }

        private float GetAggroRangeForTier(string tier)
        {
            return 30f;
        }

        private byte GetLevelForTier(string tier)
        {
            if (string.IsNullOrEmpty(tier))
                return 1;

            return tier.ToUpper() switch
            {
                "FODDER" => 0,
                "RECRUIT" => 1,
                "VETERAN" => 2,
                "CHAMPION" => 4,
                "HERO" => 6,
                "WARMONGER" => 8,
                _ => 1
            };
        }

        private byte GetZoneBaseLevel(string zoneName)
        {
            if (string.IsNullOrEmpty(zoneName)) return 1;

            string lower = zoneName.ToLower();

            if (lower.Contains("tutorial")) return 1;

            if (lower.StartsWith("dungeon") && lower.Length >= 9)
            {
                if (int.TryParse(lower.Substring(7, 2), out int dungeonNum))
                {
                    // dungeon00 = 1, dungeon01 = 5, dungeon02 = 9, etc.
                    return (byte)(dungeonNum * 4 + 1);
                }
            }

            return 1;
        }

        private float GetLeashRangeForTier(string tier)
        {
            return tier?.ToLower() switch
            {
                "grunt" => 25f,
                "champion" => 30f,
                "hero" => 35f,
                "boss" => 50f,
                _ => 30f
            };
        }

        private float GetAttackCooldownForTier(string tier)
        {
            return tier?.ToLower() switch
            {
                "grunt" => 2.0f,
                "champion" => 1.5f,
                "hero" => 1.2f,
                "boss" => 1.0f,
                _ => 1.5f
            };
        }

        private float GetAttackRangeFromManipulators(CreatureData creature)
        {
            if (creature.manipulators != null &&
                creature.manipulators.TryGetValue("primaryweapon", out var weapon) &&
                weapon.properties != null &&
                weapon.properties.TryGetValue("Range", out string rangeStr))
            {
                if (float.TryParse(rangeStr, out float range))
                {
                    Debug.LogError($"[CombatManager] {creature.gcType} AttackRange from DB: {range}");
                    return range;
                }
            }

            if (creature.behaviourType?.Contains("caster") == true)
                return 150f;

            if (creature.behaviourType?.Contains("ranged") == true)
                return 90f;

            return 8f;
        }

        private float GetAttackCooldownFromManipulators(CreatureData creature)
        {
            if (creature.manipulators != null &&
                creature.manipulators.TryGetValue("primaryweapon", out var weapon) &&
                weapon.properties != null &&
                weapon.properties.TryGetValue("CoolDown", out string cdStr))
            {
                if (float.TryParse(cdStr, out float cooldown))
                {
                    Debug.LogError($"[CombatManager] {creature.gcType} AttackCooldown from DB: {cooldown}");
                    return cooldown;
                }
            }

            return GetAttackCooldownForTier(creature.tier);
        }

        public Monster GetMonsterByBehaviorId(uint behaviorId)
        {
            foreach (var monster in _activeMonsters.Values)
            {
                if (monster.BehaviorId == behaviorId)
                    return monster;
            }
            return null;
        }

        public Monster GetMonsterByManipulatorsId(uint manipulatorsId)
        {
            foreach (var monster in _activeMonsters.Values)
            {
                if (monster.ManipulatorsId == manipulatorsId)
                    return monster;
            }
            return null;
        }

        public Monster GetMonsterBySkillsId(uint skillsId)
        {
            foreach (var monster in _activeMonsters.Values)
            {
                if (monster.SkillsId == skillsId)
                    return monster;
            }
            return null;
        }

        private float GetMoveSpeedFromCreature(CreatureData creature)
        {
            if (!string.IsNullOrEmpty(creature.speed) &&
                float.TryParse(creature.speed, out float speed))
            {
                float serverSpeed = speed;
                Debug.LogError($"[CombatManager] {creature.gcType} MoveSpeed from DB: {speed} -> {serverSpeed}");
                return serverSpeed;
            }

            return 30f;
        }
        public List<Monster> GetMonstersInRange(float x, float y, float range)
        {
            var result = new List<Monster>();
            float rangeSq = range * range;
            foreach (var monster in GetAllMonsters())
            {
                if (!monster.IsAlive) continue;
                float dx = monster.PosX - x;
                float dy = monster.PosY - y;
                if (dx * dx + dy * dy <= rangeSq)
                    result.Add(monster);
            }
            return result;
        }
        private class RespawnEntry
        {
            public string GCType;
            public string ZoneName;
            public float PosX, PosY, PosZ;
            public float Heading;
            public string EncounterGroupKey;
            public float RespawnTime;
        }
    }

    public class CombatPlayer
    {
        public uint EntityId;
        public string Name;
        public PlayerState PlayerState;
        public float PosX, PosY;
        public bool IsAlive = true;
        public bool HasActiveClientAttack;
        public uint ActiveClientAttackTargetId;
    }

    public class DamageEvent
    {
        public uint AttackerId;
        public uint DefenderId;
        public int DamageAmount;
        public uint DamageWire;
        public bool IsCritical;
        public float PosX, PosY, PosZ;
    }

    public class DamageResult
    {
        public bool Success;
        public int DamageDealt;
        public bool IsCritical;
        public bool DefenderDied;
        public uint NewHPWire;
    }
}
