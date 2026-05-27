using System;
using System.Collections.Generic;
using UnityEngine;
using DungeonRunners.Networking;
using DungeonRunners.Networking.Sync;
using DungeonRunners.Core;
using DungeonRunners.Data;
using DungeonRunners.Utilities;
using System.Linq;
using System.IO;
namespace DungeonRunners.Combat
{
    public class CombatManager
    {
        private static CombatManager _instance;
        public static CombatManager Instance => _instance ??= new CombatManager();

        // Diagnostic runtime override for monster DamageBonus input to ResolveNativeWeaponDamage.
        // -1 = disabled (server uses 0 like before). Any value >= 0 = pass it as DamageBonus to
        // ComputeNativeWeaponDamageRange so we can empirically tune until client/server damage
        // rolls converge on the 666 NoHP combat-during-movement scenario. Set via @setdmgbonus N.
        public static int MonsterDamageBonusOverride = -1;

        // Off by default — 2026-05-25 Unity crash from log buffer pressure. This trace fires
        // ~1Hz with a foreach over all monsters; useful for combat scope debugging but spammy.
        public static bool VerboseCombatTick = false;

        // Off by default — 2026-05-25 Unity froze on combat from log spam. [MON-STATE] fired
        // ~584 lines per session (21% of log) with heavy string interpolation. The trace is
        // useful for AI/state debugging but not for HP-sync work. Re-enable per investigation.
        public static bool VerboseMonsterStateTrace = false;

        // PA1.3: route active-chase position update through the same UnitMoverSim that
        // already drives idle wander. Default false → legacy float math is authoritative.
        // Flip true (in ServerManager) once shadow-mode validation (PA1.4) confirms parity.
        public static bool UseUnitMoverForChase = false;

        // PA1.4: run UnitMoverSim chase in parallel with legacy each tick; log delta.
        // Authoritative position stays legacy until UseUnitMoverForChase=true. Default
        // false — flip true only during validation sessions to avoid log spam.
        public static bool EnableChaseMoverShadow = false;
        private const int CHASE_MOVER_DIFF_LOG_INTERVAL = 5;

        private Dictionary<uint, Monster> _activeMonsters = new Dictionary<uint, Monster>();
        private Dictionary<uint, CombatPlayer> _players = new Dictionary<uint, CombatPlayer>();
        private readonly List<uint> _nativeEntityOrder = new List<uint>();
        private readonly HashSet<uint> _nativeEntityOrderSet = new HashSet<uint>();
        private Dictionary<uint, float> _playerCombatAdvanceTime = new Dictionary<uint, float>();
        private List<RespawnEntry> _respawnQueue = new List<RespawnEntry>();
        private Dictionary<uint, uint> _monsterRuntimeHPWire = new Dictionary<uint, uint>();
        private Dictionary<uint, MonsterHpAuthorityState> _monsterHPAuthority = new Dictionary<uint, MonsterHpAuthorityState>();
        private HashSet<uint> _monsterRuntimeDamageCommitted = new HashSet<uint>();
        private Dictionary<uint, float> _monsterHPRegenLastTime = new Dictionary<uint, float>();
        private Dictionary<uint, float> _monsterHPRegenCarryWire = new Dictionary<uint, float>();
        private Dictionary<uint, ushort> _monsterHPRegenCooldownTicks = new Dictionary<uint, ushort>();
        private Dictionary<uint, float> _monsterManaRegenLastTime = new Dictionary<uint, float>();
        private Dictionary<uint, ushort> _monsterManaRegenCooldownTicks = new Dictionary<uint, ushort>();
        private Dictionary<uint, float> _monsterDeathUpdateAccum = new Dictionary<uint, float>();
        private Dictionary<uint, string> _monsterStateTraceSignatures = new Dictionary<uint, string>();
        private Dictionary<string, ActiveMonsterModifier> _activeMonsterModifiers = new Dictionary<string, ActiveMonsterModifier>(StringComparer.Ordinal);
        private Queue<PendingModifierKill> _pendingModifierKills = new Queue<PendingModifierKill>();
        private Dictionary<uint, float> _monsterFarTargetActionLogTime = new Dictionary<uint, float>();
        private bool _advancingMonsterModifiers;
        private float _lastCombatTraceSummaryTime;
        private uint _nativeCombatTick;
        private float _nativeCombatTime = -1f;
        private bool _hasCompletedNativeEntityUpdate;
        private uint _lastCompletedNativeEntityUpdateTick;
        private float _lastCompletedNativeEntityUpdateTime = -1f;
        private bool _hasCompletedNativeSubEntityUpdate;
        private uint _lastCompletedNativeSubEntityUpdateTick;
        private float _lastCompletedNativeSubEntityUpdateTime = -1f;
        private const float NATIVE_UNIT_TICK_INTERVAL = 1f / 30f;
        private const ushort NATIVE_DAMAGE_REGEN_COOLDOWN_TICKS = 300;
        private const int NATIVE_UNIT_REGEN_DIVISOR = 3000;
        private const int NATIVE_PERCENT_SCALE = 100;
        private const ushort NATIVE_STOCKUNIT_FADE_TICKS = 35;
        private const float NATIVE_CONTACT_RANGE_EPSILON = 1f / 16f;
        private const float NATIVE_DEFAULT_UNIT_PERCEPTION = 100f;
        private const float NATIVE_DEFAULT_UNIT_SCAN_FREQUENCY = 1f;
        private const float NATIVE_DEFAULT_UNIT_FLEE_RANGE = 0f;
        private const int NATIVE_DEFAULT_UNIT_COLLISION_BAND = 1;
        private const int NATIVE_DEFAULT_UNIT_COLLISION_PRIORITY = 0;
        private const bool NATIVE_DEFAULT_UNIT_AUTO_SCAN = false;
        private const bool NATIVE_DEFAULT_UNIT_AVOID_UNITS = true;
        private const bool NATIVE_DEFAULT_UNIT_TURN_BEFORE_MOVING = true;
        private const bool NATIVE_DEFAULT_UNIT_PLAYER_CONTROLLED = true;
        private const float NATIVE_DEFAULT_MONSTER_AGGRO_RANGE = 40f;
        private const float NATIVE_DEFAULT_MONSTER_SHOUT_RANGE = 50f;
        private const float NATIVE_DEFAULT_MONSTER_WANDER_RANGE = 100f;
        private const float NATIVE_DEFAULT_MONSTER_LEASH_RANGE = 0f;
        private const float NATIVE_DEFAULT_MONSTER_TELEPORT_FREQUENCY = 150f;
        private const float NATIVE_DEFAULT_MONSTER_TELEPORT_LIMBO_TIME = 60f;
        private const float NATIVE_DEFAULT_MONSTER_RETREAT_RANGE_SQUARED = 640000f;
        private const float NATIVE_DEFAULT_MONSTER_BASE_TIME = 30f;
        private const float NATIVE_DEFAULT_MONSTER_VARIABLE_TIME = 0f;
        private const bool NATIVE_DEFAULT_MONSTER_RETREATABLE = true;
        private const bool NATIVE_DEFAULT_MONSTER_LEASHED = false;
        private const bool NATIVE_DEFAULT_MONSTER_USE_IDLE_TIME = false;

        private class MonsterHpAuthorityState
        {
            public bool RuntimeInitialized;
            public uint RuntimeHPWire;
            public float LastClientHPReportTime;
            public uint LastClientHPReportWire;
            public uint LastPacketHPWire;
        }

        private class ActiveMonsterModifier
        {
            public uint TargetEntityId;
            public uint SourceEntityId;
            public PlayerState SourceState;
            public SpellData Spell;
            public int SkillLevel;
            public float NextTickTime;
            public float Frequency;
            public int MaxTicks;
            public int TicksApplied;
            public string ModifierKey;
        }

        public class MonsterModifierApplyResult
        {
            public bool AppliedModifier;
            public bool DamageApplied;
            public bool Died;
            public uint OldHPWire;
            public uint NewHPWire;
            public int TicksApplied;
            public string Reason;
        }

        public class PendingModifierKill
        {
            public uint SourceEntityId;
            public uint TargetEntityId;
            public string Source;
            public float NativeDamageTime;
        }

        public bool HasPendingModifierKills => _pendingModifierKills.Count > 0;
        public uint NativeCombatTick => _nativeCombatTick;
        public float NativeCombatTime => GetNativeCombatTime();
        public uint LastCompletedNativeEntityUpdateTick => _hasCompletedNativeEntityUpdate ? _lastCompletedNativeEntityUpdateTick : 0u;
        public float LastCompletedNativeEntityUpdateTime => _hasCompletedNativeEntityUpdate ? _lastCompletedNativeEntityUpdateTime : Mathf.Max(0f, GetNativeCombatTime() - NATIVE_UNIT_TICK_INTERVAL);
        public uint LastCompletedNativeSubEntityUpdateTick => _hasCompletedNativeSubEntityUpdate ? _lastCompletedNativeSubEntityUpdateTick : 0u;
        public float LastCompletedNativeSubEntityUpdateTime => _hasCompletedNativeSubEntityUpdate ? _lastCompletedNativeSubEntityUpdateTime : Mathf.Max(0f, GetNativeCombatTime() - NATIVE_UNIT_TICK_INTERVAL);

        public void SetNativeCombatClock(uint tick, float time, string source = null)
        {
            if (time < 0f) time = Time.time;
            bool backwards = _nativeCombatTime >= 0f && time + 0.0001f < _nativeCombatTime;
            if (backwards)
            {
                Debug.LogError($"[NATIVE-COMBAT-CLOCK] ignored backwards clock tick={tick} time={time:F3} currentTick={_nativeCombatTick} currentTime={_nativeCombatTime:F3} source={source ?? "unknown"}");
                return;
            }
            _nativeCombatTick = tick;
            _nativeCombatTime = time;
        }

        public void MarkNativeEntityUpdateCompleted(uint tick, float time, string source = null)
        {
            if (time < 0f) time = GetNativeCombatTime();
            if (_hasCompletedNativeEntityUpdate && time + 0.0001f < _lastCompletedNativeEntityUpdateTime)
            {
                Debug.LogError($"[NATIVE-VALIDATION-CUTOFF] ignored backwards completed tick={tick} time={time:F3} currentTick={_lastCompletedNativeEntityUpdateTick} currentTime={_lastCompletedNativeEntityUpdateTime:F3} source={source ?? "unknown"}");
                return;
            }

            _hasCompletedNativeEntityUpdate = true;
            _lastCompletedNativeEntityUpdateTick = tick;
            _lastCompletedNativeEntityUpdateTime = time;
            Debug.LogError($"[NATIVE-VALIDATION-CUTOFF] completedEntityUpdate tick={tick} time={time:F3} source={source ?? "unknown"}");
        }

        public void MarkNativeSubEntityUpdateCompleted(uint tick, float time, string source = null)
        {
            if (time < 0f) time = GetNativeCombatTime();
            if (_hasCompletedNativeSubEntityUpdate && time + 0.0001f < _lastCompletedNativeSubEntityUpdateTime)
            {
                Debug.LogError($"[NATIVE-VALIDATION-CUTOFF] ignored backwards completed subentity tick={tick} time={time:F3} currentTick={_lastCompletedNativeSubEntityUpdateTick} currentTime={_lastCompletedNativeSubEntityUpdateTime:F3} source={source ?? "unknown"}");
                return;
            }

            _hasCompletedNativeSubEntityUpdate = true;
            _lastCompletedNativeSubEntityUpdateTick = tick;
            _lastCompletedNativeSubEntityUpdateTime = time;
            Debug.LogError($"[NATIVE-VALIDATION-CUTOFF] completedSubEntityUpdate tick={tick} time={time:F3} source={source ?? "unknown"}");
        }

        public void GetNativeValidationCutoff(out uint tick, out float time)
        {
            if (_hasCompletedNativeSubEntityUpdate &&
                (!_hasCompletedNativeEntityUpdate || _lastCompletedNativeSubEntityUpdateTime > _lastCompletedNativeEntityUpdateTime + 0.0001f))
            {
                tick = _lastCompletedNativeSubEntityUpdateTick;
                time = _lastCompletedNativeSubEntityUpdateTime;
                return;
            }

            if (_hasCompletedNativeEntityUpdate)
            {
                tick = _lastCompletedNativeEntityUpdateTick;
                time = _lastCompletedNativeEntityUpdateTime;
                return;
            }

            tick = _nativeCombatTick > 0 ? _nativeCombatTick - 1u : 0u;
            time = Mathf.Max(0f, GetNativeCombatTime() - NATIVE_UNIT_TICK_INTERVAL);
        }

        public float GetNativeCombatTime()
        {
            return _nativeCombatTime >= 0f ? _nativeCombatTime : Time.time;
        }

        public PendingModifierKill DequeuePendingModifierKill()
        {
            return _pendingModifierKills.Count > 0 ? _pendingModifierKills.Dequeue() : null;
        }

        // Maps ANY component ID (EntityId, BehaviorId, SkillsId, etc.) to the monster's EntityId
        private Dictionary<uint, uint> _componentToEntityMap = new Dictionary<uint, uint>();

        // Maps client-sent target IDs to our server entity IDs (learned at runtime)
        private Dictionary<uint, uint> _clientToServerIdMap = new Dictionary<uint, uint>();

        private uint _nextMonsterId = 50000;
        private float? _avatarCombatRadius;

        public event Action<Monster> OnMonsterSpawned;
        public event Action<Monster> OnMonsterDespawned;
        // OnDamageDealt + OnEntityDeath removed 2026-05-27 (audit Phase 1):
        // events were declared but never .Invoke()'d AND never subscribed (only handlers
        // were dead-code in UGS emitting invalid 0x28 packets).
        public event Action<Monster> OnMonsterPositionChanged;
        public event Action<Monster, CombatPlayer, byte> OnMonsterAttackStarted;
        public event Action<Monster, CombatPlayer, bool, uint> OnMonsterAttackResolved;
        // OnMonsterDamagedByPlayer event removed Stage 0 cleanup 2026-05-27 alongside Path C refresh.
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
            Debug.LogError("[CombatManager] Room RNG pending native seed");
        }

        private void RegisterNativeEntityOrder(uint entityId)
        {
            if (entityId == 0 || !_nativeEntityOrderSet.Add(entityId))
                return;
            _nativeEntityOrder.Add(entityId);
        }

        private void UnregisterNativeEntityOrder(uint entityId)
        {
            if (!_nativeEntityOrderSet.Remove(entityId))
                return;
            _nativeEntityOrder.Remove(entityId);
        }

        public List<uint> GetNativeEntityOrderSnapshot()
        {
            return new List<uint>(_nativeEntityOrder);
        }

        public bool IsNativeMonsterEntity(uint entityId)
        {
            return _activeMonsters.ContainsKey(entityId);
        }

        public bool IsNativePlayerEntity(uint entityId)
        {
            return _players.ContainsKey(entityId);
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
                    _monsterHPAuthority.Remove(eid);
                    _monsterRuntimeDamageCommitted.Remove(eid);
                    _monsterHPRegenLastTime.Remove(eid);
                    _monsterHPRegenCarryWire.Remove(eid);
                    _monsterHPRegenCooldownTicks.Remove(eid);
                    _monsterManaRegenLastTime.Remove(eid);
                    _monsterManaRegenCooldownTicks.Remove(eid);
                    _monsterStateTraceSignatures.Remove(eid);
                    _monsterFarTargetActionLogTime.Remove(eid);
                    RemoveMonsterModifiersForTarget(eid, "ClearZoneMobs");
                    WanderSimulator.Instance.UnregisterEntity(eid);
                    UnregisterNativeEntityOrder(eid);
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
            RegisterNativeEntityOrder(entityId);
            _playerCombatAdvanceTime[entityId] = GetNativeCombatTime();
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

        public bool SyncMonsterWanderClientVisiblePosition(Monster monster, string source)
        {
            if (monster == null || !monster.IsAlive || monster.AggroTriggered)
                return false;
            if (!TryGetMonsterWanderClientVisiblePosition(monster, out float visualX, out float visualY))
                return false;

            float delta = Distance2D(monster.PosX, monster.PosY, visualX, visualY);
            if (delta <= 0.001f)
                return false;

            float oldX = monster.PosX;
            float oldY = monster.PosY;
            monster.PosX = visualX;
            monster.PosY = visualY;
            Debug.LogError($"[WANDER-SYNC] {monster.Name}#{monster.EntityId} source={source ?? "unknown"} authoritative=({oldX:F1},{oldY:F1})->clientVisible=({visualX:F1},{visualY:F1}) delta={delta:F1}");
            return true;
        }

        public bool TryGetMonsterWanderClientVisiblePosition(Monster monster, out float visualX, out float visualY)
        {
            visualX = monster != null ? monster.PosX : 0f;
            visualY = monster != null ? monster.PosY : 0f;
            if (monster == null || !monster.IsAlive || monster.AggroTriggered)
                return false;
            return WanderSimulator.Instance.TryGetClientVisiblePosition(monster.EntityId, out visualX, out visualY);
        }

        public void EngageMonsterFromClientAction(Monster monster, uint playerEntityId)
        {
            if (monster == null || !monster.IsAlive) return;
            if (monster.DeathPendingClientConfirmation) return;
            if (!_players.TryGetValue(playerEntityId, out var player) || player == null) return;
            float allowedRange = ResolveMonsterEffectiveAttackRange(monster);
            float monsterX = monster.PosX;
            float monsterY = monster.PosY;
            TryGetMonsterWanderClientVisiblePosition(monster, out monsterX, out monsterY);
            float dist = Distance2D(monsterX, monsterY, player.PosX, player.PosY);
            float contactRange = ResolveNativeClientContactRange(monster, player, allowedRange);
            bool inNativeContact = contactRange > 0f && dist <= contactRange + NATIVE_CONTACT_RANGE_EPSILON;
            float aggroRange = ResolveMonsterTargetSearchRange(monster, false);
            bool inAuthoredAggro = aggroRange > 0f && dist <= aggroRange + NATIVE_CONTACT_RANGE_EPSILON;
            float nativeNow = GetNativeCombatTime();
            bool alreadyTargetingPlayer = monster.AggroTriggered && monster.TargetId == player.EntityId;
            bool contactWasActive = monster.CombatContactTargetId == player.EntityId && monster.CombatContactUntil > nativeNow;
            if (!alreadyTargetingPlayer && !inAuthoredAggro && !inNativeContact)
            {
                if (monster.CombatContactTargetId == player.EntityId)
                {
                    monster.CombatContactTargetId = 0;
                    monster.CombatContactUntil = 0f;
                }
                Debug.LogError($"[AGGRO-OBSERVE] client intent outside aggro {monster.Name}#{monster.EntityId}->{player.Name} dist={dist:F1} aggro={aggroRange:F1} nativeRange={contactRange:F1} action=log-only");
                return;
            }
            SyncMonsterWanderClientVisiblePosition(monster, "client-action");
            bool attackPathClear = IsMonsterAttackPathClear(monster, player, inNativeContact ? "client-contact" : null);
            if (!attackPathClear)
            {
                inNativeContact = false;
                ClearMonsterCombatContact(monster, player);
            }
            AggroMonster(monster, player, "client", false);
            if (inNativeContact)
            {
                monster.CombatContactTargetId = player.EntityId;
                monster.CombatContactUntil = nativeNow + Mathf.Max(0.25f, ResolveMonsterAttackWindup(monster) + 0.25f);
                if (!contactWasActive)
                    Debug.LogError($"[MON-CONTACT] {monster.Name}#{monster.EntityId}->{player.Name} dist={dist:F1} range={allowedRange:F1} nativeRange={contactRange:F1}");
            }
            else if (monster.CombatContactTargetId == player.EntityId)
            {
                monster.CombatContactTargetId = 0;
                monster.CombatContactUntil = 0f;
            }
            TraceMonsterState(monster, "client-action", player, dist, allowedRange, inNativeContact ? "native-contact" : "client-intent");
        }

        private MonsterHpAuthorityState GetMonsterHPAuthority(Monster monster)
        {
            if (monster == null) return null;
            if (!_monsterHPAuthority.TryGetValue(monster.EntityId, out var state))
            {
                state = new MonsterHpAuthorityState();
                _monsterHPAuthority[monster.EntityId] = state;
            }

            if (!state.RuntimeInitialized)
            {
                uint hp = 0;
                if (!_monsterRuntimeHPWire.TryGetValue(monster.EntityId, out hp))
                    hp = monster.IsAlive ? monster.CurrentHPWire : 0;
                state.RuntimeHPWire = hp > monster.MaxHPWire ? monster.MaxHPWire : hp;
                state.RuntimeInitialized = true;
            }

            if (monster.LastClientHPReportTime > state.LastClientHPReportTime)
            {
                state.LastClientHPReportTime = monster.LastClientHPReportTime;
                state.LastClientHPReportWire = monster.LastClientHPReportWire;
            }
            SyncMonsterHPAuthority(monster, state);
            return state;
        }

        private void SyncMonsterHPAuthority(Monster monster, MonsterHpAuthorityState state)
        {
            if (monster == null || state == null) return;
            if (!state.RuntimeInitialized)
            {
                state.RuntimeHPWire = monster.IsAlive ? monster.CurrentHPWire : 0;
                state.RuntimeInitialized = true;
            }
            if (state.RuntimeHPWire > monster.MaxHPWire)
                state.RuntimeHPWire = monster.MaxHPWire;
            _monsterRuntimeHPWire[monster.EntityId] = state.RuntimeHPWire;
            SyncMonsterHPFields(monster, state);
            HpSyncService.Instance.RegisterMonster(monster);
            if (_activeMonsters.TryGetValue(monster.EntityId, out var active) && active != monster)
            {
                SyncMonsterHPFields(active, state);
                HpSyncService.Instance.RegisterMonster(active);
            }
        }

        private void SyncMonsterHPFields(Monster monster, MonsterHpAuthorityState state)
        {
            if (monster == null || state == null) return;
            monster.CurrentHPWire = state.RuntimeHPWire;
            monster.LastClientHPReportTime = state.LastClientHPReportTime;
            monster.LastClientHPReportWire = state.LastClientHPReportWire;
        }

        private uint PeekRuntimeMonsterHPWire(Monster monster)
        {
            var state = GetMonsterHPAuthority(monster);
            return state != null ? state.RuntimeHPWire : 0;
        }

        private int ResolveMonsterHealthRegenFactor(Monster monster)
        {
            float authoredGlobal = GCDatabase.Instance.GetKnob("MonsterHealthRegen", 2f);
            float authoredUnit = monster != null && monster.HasAuthoredHealthRegen ? Mathf.Max(0f, monster.HealthRegen) : 1f;
            return Mathf.Max(0, Mathf.RoundToInt(authoredGlobal * authoredUnit));
        }

        private int ResolveMonsterHealthRegenModPct(Monster monster)
        {
            return 0;
        }

        private int ResolveMonsterAdditiveHealthRegen(Monster monster)
        {
            return 0;
        }

        internal static int ComputeNativeUnitRegenDeltaWire(uint maxHPWire, int baseRegen, int regenModPct, int additiveRegen, bool cooldownActive)
        {
            if (maxHPWire == 0) return 0;
            long regen;
            long bonus = additiveRegen;
            if (cooldownActive)
            {
                if (bonus == 0) return 0;
                regen = bonus;
            }
            else
            {
                regen = (((long)regenModPct + NATIVE_PERCENT_SCALE) * baseRegen) / NATIVE_PERCENT_SCALE + bonus;
            }

            long delta = (regen * maxHPWire) / NATIVE_UNIT_REGEN_DIVISOR;
            if (!cooldownActive)
                delta += 1;
            if (delta > int.MaxValue) return int.MaxValue;
            if (delta < int.MinValue) return int.MinValue;
            return (int)delta;
        }

        internal static uint ApplyNativeUnitHPShiftWire(uint hpWire, uint maxHPWire, int deltaWire)
        {
            long shifted = (long)hpWire + deltaWire;
            if (shifted <= 0) return 0;
            if (shifted >= maxHPWire) return maxHPWire;
            return (uint)shifted;
        }

        private int ComputeNativeMonsterHealthRegenDeltaWire(Monster monster, bool cooldownActive)
        {
            if (monster == null) return 0;
            return ComputeNativeUnitRegenDeltaWire(
                monster.MaxHPWire,
                ResolveMonsterHealthRegenFactor(monster),
                ResolveMonsterHealthRegenModPct(monster),
                ResolveMonsterAdditiveHealthRegen(monster),
                cooldownActive);
        }

        private void ResetMonsterHPRegenClock(Monster monster, float now)
        {
            if (monster == null) return;
            if (now < 0f) now = GetNativeCombatTime();
            _monsterHPRegenLastTime[monster.EntityId] = now;
            _monsterHPRegenCarryWire[monster.EntityId] = 0f;
        }

        private int ResolveMonsterManaRegenFactor(Monster monster)
        {
            float authoredGlobal = GCDatabase.Instance.GetKnob("MonsterPowerRegen", 2f);
            float authoredUnit = monster != null && monster.HasAuthoredManaRegen ? Mathf.Max(0f, monster.ManaRegen) : 1f;
            return Mathf.Max(0, Mathf.RoundToInt(authoredGlobal * authoredUnit));
        }

        private uint ComputeNativeMonsterManaRegenDeltaWire(Monster monster)
        {
            if (monster == null || monster.MaxManaWire == 0) return 0;
            int regenFactor = ResolveMonsterManaRegenFactor(monster);
            if (regenFactor <= 0) return 0;
            long delta = ((long)regenFactor * monster.MaxManaWire) / 3000L + 1L;
            if (delta <= 0) return 0;
            return delta > uint.MaxValue ? uint.MaxValue : (uint)delta;
        }

        private void ResetMonsterManaRegenClock(Monster monster, float now)
        {
            if (monster == null) return;
            if (now < 0f) now = GetNativeCombatTime();
            _monsterManaRegenLastTime[monster.EntityId] = now;
        }

        private uint ApplyNativeMonsterHealthRegen(Monster monster, string source, float now = -1f)
        {
            if (monster == null) return 0;
            uint hp = PeekRuntimeMonsterHPWire(monster);
            if (now < 0f) now = GetNativeCombatTime();
            if (!monster.IsAlive || hp == 0 || monster.MaxHPWire == 0)
            {
                ResetMonsterHPRegenClock(monster, now);
                _monsterHPRegenCooldownTicks.Remove(monster.EntityId);
                return hp;
            }
            if (hp >= monster.MaxHPWire && ResolveMonsterAdditiveHealthRegen(monster) >= 0)
            {
                ResetMonsterHPRegenClock(monster, now);
                _monsterHPRegenCooldownTicks.Remove(monster.EntityId);
                return hp;
            }

            // Block regen while the monster is in active combat. The existing 10-second
            // damage-cooldown (NATIVE_DAMAGE_REGEN_COOLDOWN_TICKS) wasn't enough: if the
            // player engages a mob but stops hitting it for 10s (e.g. while dodging the
            // mob's attacks), regen kicks in and the mob's HP climbs back up. Path C
            // refreshes the client every regen tick, so the player sees the mob's HP bar
            // visibly growing — appears as "HP reset to full" if the climb is big enough.
            // Verified via server.log 2026-05-25: mob 50000 had +1040 wire regen between
            // mob-attack ticks during sustained combat. Solution: don't regen at all
            // while AggroTriggered or in Combat/Attacking state. Mob still regens during
            // the chase home (Return state) or once aggro fully drops.
            MonsterState s = monster.State;
            if (monster.AggroTriggered
                || s == MonsterState.Combat
                || s == MonsterState.Attacking
                || s == MonsterState.Chase)
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

            int ticks = Mathf.FloorToInt(elapsed / NATIVE_UNIT_TICK_INTERVAL);
            if (ticks <= 0) return hp;

            _monsterHPRegenLastTime[monster.EntityId] = lastTime + ticks * NATIVE_UNIT_TICK_INTERVAL;

            uint oldHP = hp;
            _monsterHPRegenCooldownTicks.TryGetValue(monster.EntityId, out ushort cooldown);
            int regenFactor = ResolveMonsterHealthRegenFactor(monster);
            int regenMod = ResolveMonsterHealthRegenModPct(monster);
            int additiveRegen = ResolveMonsterAdditiveHealthRegen(monster);
            for (int i = 0; i < ticks && (hp < monster.MaxHPWire || additiveRegen < 0); i++)
            {
                if (cooldown > 0)
                    cooldown--;

                int regenWire = ComputeNativeMonsterHealthRegenDeltaWire(monster, cooldown > 0);
                if (regenWire == 0) continue;
                hp = ApplyNativeUnitHPShiftWire(hp, monster.MaxHPWire, regenWire);
                if (hp == 0) break;
            }

            if (cooldown > 0)
                _monsterHPRegenCooldownTicks[monster.EntityId] = cooldown;
            else
                _monsterHPRegenCooldownTicks.Remove(monster.EntityId);

            if (oldHP == hp) return hp;

            if (hp >= monster.MaxHPWire)
            {
                hp = monster.MaxHPWire;
                _monsterRuntimeDamageCommitted.Remove(monster.EntityId);
                _monsterHPRegenCarryWire[monster.EntityId] = 0f;
            }

            var state = GetMonsterHPAuthority(monster);
            state.RuntimeHPWire = hp;
            state.RuntimeInitialized = true;
            SyncMonsterHPAuthority(monster, state);
            Debug.LogError($"[MON-REGEN] {monster.Name}#{monster.EntityId} source={source ?? "unknown"} hp={oldHP / 256f:F2}->{hp / 256f:F2}/{monster.MaxHPWire / 256f:F2} ticks={ticks} cooldown={cooldown} base={regenFactor} mod={regenMod} additive={additiveRegen}");
            return hp;
        }

        private void ApplyNativeMonsterManaRegen(Monster monster, string source, float now = -1f)
        {
            if (monster == null) return;
            if (now < 0f) now = GetNativeCombatTime();
            if (!monster.IsAlive || monster.MaxManaWire == 0)
            {
                ResetMonsterManaRegenClock(monster, now);
                _monsterManaRegenCooldownTicks.Remove(monster.EntityId);
                return;
            }
            if (monster.CurrentManaWire > monster.MaxManaWire)
                monster.CurrentManaWire = monster.MaxManaWire;
            if (monster.CurrentManaWire >= monster.MaxManaWire)
            {
                ResetMonsterManaRegenClock(monster, now);
                _monsterManaRegenCooldownTicks.Remove(monster.EntityId);
                return;
            }
            if (ResolveMonsterManaRegenFactor(monster) <= 0)
            {
                ResetMonsterManaRegenClock(monster, now);
                return;
            }
            if (!_monsterManaRegenLastTime.TryGetValue(monster.EntityId, out float lastTime) || lastTime <= 0f)
            {
                _monsterManaRegenLastTime[monster.EntityId] = now;
                return;
            }

            float elapsed = now - lastTime;
            if (elapsed <= 0f) return;
            int ticks = Mathf.FloorToInt(elapsed / NATIVE_UNIT_TICK_INTERVAL);
            if (ticks <= 0) return;
            _monsterManaRegenLastTime[monster.EntityId] = lastTime + ticks * NATIVE_UNIT_TICK_INTERVAL;

            uint oldMana = monster.CurrentManaWire;
            uint mana = oldMana;
            _monsterManaRegenCooldownTicks.TryGetValue(monster.EntityId, out ushort cooldown);
            for (int i = 0; i < ticks && mana < monster.MaxManaWire; i++)
            {
                if (cooldown > 0)
                    cooldown--;
                if (cooldown > 0)
                    continue;

                uint regenWire = ComputeNativeMonsterManaRegenDeltaWire(monster);
                if (regenWire == 0) continue;
                mana = regenWire >= monster.MaxManaWire - mana ? monster.MaxManaWire : mana + regenWire;
            }

            if (cooldown > 0)
                _monsterManaRegenCooldownTicks[monster.EntityId] = cooldown;
            else
                _monsterManaRegenCooldownTicks.Remove(monster.EntityId);

            if (oldMana == mana) return;
            monster.CurrentManaWire = mana;
            Debug.LogError($"[MON-MANA-REGEN] {monster.Name}#{monster.EntityId} source={source ?? "unknown"} mana={oldMana / 256f:F2}->{mana / 256f:F2}/{monster.MaxManaWire / 256f:F2} ticks={ticks} cooldown={cooldown} factor={ResolveMonsterManaRegenFactor(monster)}");
        }

        private uint ApplyNativeMonsterVitalsRegen(Monster monster, string source, float now = -1f)
        {
            uint hp = ApplyNativeMonsterHealthRegen(monster, source, now);
            ApplyNativeMonsterManaRegen(monster, source, now);
            return hp;
        }

        public uint AdvanceMonsterVitalsToNativeTime(Monster monster, float now, string source = null)
        {
            if (now < 0f) now = GetNativeCombatTime();
            return monster != null ? ApplyNativeMonsterVitalsRegen(monster, source ?? "AdvanceMonsterVitalsToNativeTime", now) : 0u;
        }

        public uint AdvanceMonsterRuntimeBeforeSync(Monster monster, float now, string source = null)
        {
            if (now < 0f) now = GetNativeCombatTime();
            uint hp = AdvanceMonsterVitalsToNativeTime(monster, now, source ?? "AdvanceMonsterRuntimeBeforeSync");
            if (!_advancingMonsterModifiers)
                AdvanceMonsterModifierRuntime(_roomRng, now, source ?? "AdvanceMonsterRuntimeBeforeSync");
            return monster != null ? PeekRuntimeMonsterHPWire(monster) : hp;
        }

        private uint GetRuntimeMonsterHPWire(Monster monster, string source = null, float now = -1f)
        {
            return PeekRuntimeMonsterHPWire(monster);
        }

        private static string BuildMonsterModifierKey(uint targetEntityId, uint sourceEntityId, SpellData spell)
        {
            string modifierId = spell?.ProjectileModifierId;
            if (string.IsNullOrEmpty(modifierId))
                modifierId = spell?.ProjectileModifierEffectId ?? "modifier";
            return $"{targetEntityId}:{sourceEntityId}:{modifierId}";
        }

        private void RemoveMonsterModifiersForTarget(uint targetEntityId, string source)
        {
            if (targetEntityId == 0 || _activeMonsterModifiers.Count == 0) return;
            var keys = _activeMonsterModifiers
                .Where(kvp => kvp.Value != null && kvp.Value.TargetEntityId == targetEntityId)
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (string key in keys)
                _activeMonsterModifiers.Remove(key);
            if (keys.Count > 0)
                Debug.LogError($"[POISON-SHOT-MOD] remove target={targetEntityId} count={keys.Count} source={source ?? "unknown"}");
        }

        public MonsterModifierApplyResult ApplyProjectileModifierFromSpell(Monster target, uint sourceEntityId, PlayerState sourceState, SpellData spell, MersenneTwister rng, int skillLevel, float now, string source)
        {
            var result = new MonsterModifierApplyResult
            {
                Reason = source ?? "spell-mod",
                OldHPWire = target != null ? PeekRuntimeMonsterHPWire(target) : 0,
                NewHPWire = target != null ? PeekRuntimeMonsterHPWire(target) : 0
            };

            if (target == null || spell == null || !spell.HasProjectileModifierDamage)
            {
                result.Reason = "missing-target-or-modifier";
                return result;
            }
            if (!target.IsAlive || PeekRuntimeMonsterHPWire(target) == 0)
            {
                result.Reason = "target-dead";
                return result;
            }
            if (sourceState == null)
            {
                result.Reason = "missing-source-state";
                return result;
            }
            if (rng == null)
            {
                result.Reason = "missing-rng";
                return result;
            }

            float applyTime = now >= 0f ? now : GetNativeCombatTime();
            float frequency = Mathf.Max(NATIVE_UNIT_TICK_INTERVAL, spell.ProjectileModifierFrequency);
            float duration = Mathf.Max(frequency, spell.ProjectileModifierDuration);
            int maxTicks = Mathf.Max(1, Mathf.CeilToInt(duration / frequency));
            float firstTickTime = applyTime + frequency;
            string key = BuildMonsterModifierKey(target.EntityId, sourceEntityId, spell);
            bool replace = _activeMonsterModifiers.ContainsKey(key);
            _activeMonsterModifiers[key] = new ActiveMonsterModifier
            {
                TargetEntityId = target.EntityId,
                SourceEntityId = sourceEntityId,
                SourceState = sourceState,
                Spell = spell,
                SkillLevel = skillLevel,
                NextTickTime = firstTickTime,
                Frequency = frequency,
                MaxTicks = maxTicks,
                TicksApplied = 0,
                ModifierKey = key
            };

            result.AppliedModifier = true;
            result.NewHPWire = PeekRuntimeMonsterHPWire(target);
            Debug.LogError($"[POISON-SHOT-MOD] {(replace ? "replace" : "add")} target={target.Name}#{target.EntityId} source={sourceEntityId} modifier={spell.ProjectileModifierId} effect={spell.ProjectileModifierEffectId} duration={duration:F2}s frequency={frequency:F2}s maxTicks={maxTicks} stack={spell.ProjectileModifierStackRule ?? "UNKNOWN"} firstTick=deferred impactTime={applyTime:F3} firstTickAt={firstTickTime:F3} hp={result.OldHPWire}->{result.NewHPWire} rngBefore={rng.CallsSinceReseed} source={source ?? "unknown"}");
            return result;
        }

        public MonsterModifierApplyResult AdvanceMonsterModifierRuntimeForTarget(uint targetEntityId, MersenneTwister rng, float now, string source)
        {
            return AdvanceMonsterModifierRuntime(rng, now, source, targetEntityId);
        }

        private MonsterModifierApplyResult AdvanceMonsterModifierRuntime(MersenneTwister rng, float now, string source, uint onlyTargetEntityId = 0)
        {
            var aggregate = new MonsterModifierApplyResult
            {
                Reason = source ?? "modifier-runtime"
            };
            if (_advancingMonsterModifiers || _activeMonsterModifiers.Count == 0 || rng == null)
                return aggregate;
            if (now < 0f) now = GetNativeCombatTime();

            _advancingMonsterModifiers = true;
            try
            {
                var keys = _activeMonsterModifiers.Keys.ToList();
                foreach (string key in keys)
                {
                    if (!_activeMonsterModifiers.TryGetValue(key, out var mod) || mod == null)
                        continue;
                    if (onlyTargetEntityId != 0 && mod.TargetEntityId != onlyTargetEntityId)
                        continue;
                    if (!_activeMonsters.TryGetValue(mod.TargetEntityId, out var monster) || monster == null || !monster.IsAlive || PeekRuntimeMonsterHPWire(monster) == 0)
                    {
                        _activeMonsterModifiers.Remove(key);
                        Debug.LogError($"[POISON-SHOT-MOD] remove target={mod.TargetEntityId} modifier={mod.ModifierKey} source={source ?? "unknown"} reason=RemoveOnDeathOrMissing");
                        continue;
                    }
                    if (mod.SourceState == null || mod.Spell == null)
                    {
                        _activeMonsterModifiers.Remove(key);
                        Debug.LogError($"[POISON-SHOT-MOD] remove target={mod.TargetEntityId} modifier={mod.ModifierKey} source={source ?? "unknown"} reason=missing-source-state");
                        continue;
                    }
                    if (now + 0.0001f < mod.NextTickTime)
                        continue;

                    while (now + 0.0001f >= mod.NextTickTime && mod.TicksApplied < mod.MaxTicks && monster.IsAlive && PeekRuntimeMonsterHPWire(monster) > 0)
                    {
                        int tickIndex = mod.TicksApplied + 1;
                        var damage = DamageComputer.ProcessProjectileModifierTick(
                            rng,
                            mod.SourceState.Level,
                            mod.SourceState.Intelligence,
                            mod.SourceState.Agility,
                            mod.SourceState.Strength,
                            mod.SourceState.WeaponDamage,
                            mod.SourceState.WeaponDamageVolatility,
                            mod.Spell,
                            monster,
                            mod.SkillLevel,
                            DamageComputer.ResolveNativeCriticalDamagePercent(mod.SourceState));

                        if (damage.Type == AttackResultType.Miss || damage.DamageF32 <= 0)
                        {
                            Debug.LogError($"[POISON-SHOT-TICK] target={monster.Name}#{monster.EntityId} tick={tickIndex}/{mod.MaxTicks} result={damage.Type} damageWire=0 hp={PeekRuntimeMonsterHPWire(monster)} source={source ?? "unknown"} rngAfter={rng.CallsSinceReseed}");
                        }
                        else
                        {
                            NotifyMonsterDamagedByPlayer(monster, mod.SourceEntityId, "modifier-tick");
                            bool applied = ApplyNativePlayerDamageToMonsterWire(
                                monster,
                                (uint)damage.DamageF32,
                                "SPELL-MOD",
                                out uint oldHPWire,
                                out uint newHPWire,
                                out bool died,
                                mod.NextTickTime);
                            uint effectRaw = applied
                                ? ConsumeNativeOnApplyDamageEffectRng(rng, "player-spell-mod", monster.EntityId, monster.Name, oldHPWire, newHPWire, monster.MaxHPWire, (uint)damage.DamageF32, source ?? "modifier-tick")
                                : 0;
                            string resultName = damage.Type.ToString().ToUpperInvariant();
                            Debug.LogError($"[POISON-SHOT-TICK] target={monster.Name}#{monster.EntityId} source={mod.SourceEntityId} tick={tickIndex}/{mod.MaxTicks} result={resultName} damageWire={damage.DamageF32} hp={oldHPWire}->{newHPWire} range=[{damage.MinDamageF32},{damage.MaxDamageF32}] damageRaw=0x{damage.DamageRaw:X8} effectRaw=0x{effectRaw:X8} due={mod.NextTickTime:F3} now={now:F3} applied={applied} died={died} rngAfter={rng.CallsSinceReseed}");
                            Debug.LogError($"[COMBAT-EVENT] actor=player-spell-mod actorId={mod.SourceEntityId} target=monster targetId={monster.EntityId} result={resultName} damageWire={damage.DamageF32} hp={oldHPWire}->{newHPWire} range=[{damage.MinDamageF32},{damage.MaxDamageF32}] damageRaw=0x{damage.DamageRaw:X8} effectRaw=0x{effectRaw:X8} spell={mod.Spell.DisplayName} rngAfter={rng.CallsSinceReseed} marker=SPELL-MOD");
                            aggregate.DamageApplied |= applied;
                            aggregate.Died |= died;
                            if (aggregate.TicksApplied == 0)
                                aggregate.OldHPWire = oldHPWire;
                            aggregate.NewHPWire = newHPWire;
                            if (died)
                            {
                                _pendingModifierKills.Enqueue(new PendingModifierKill
                                {
                                    SourceEntityId = mod.SourceEntityId,
                                    TargetEntityId = monster.EntityId,
                                    Source = source ?? "modifier-tick",
                                    NativeDamageTime = mod.NextTickTime
                                });
                            }
                        }

                        mod.TicksApplied++;
                        aggregate.TicksApplied++;
                        mod.NextTickTime += mod.Frequency;
                        if (!monster.IsAlive || PeekRuntimeMonsterHPWire(monster) == 0)
                            break;
                    }

                    if (mod.TicksApplied >= mod.MaxTicks || !monster.IsAlive || PeekRuntimeMonsterHPWire(monster) == 0)
                    {
                        _activeMonsterModifiers.Remove(key);
                        Debug.LogError($"[POISON-SHOT-MOD] complete target={monster.Name}#{monster.EntityId} source={mod.SourceEntityId} ticks={mod.TicksApplied}/{mod.MaxTicks} hp={PeekRuntimeMonsterHPWire(monster)} source={source ?? "unknown"}");
                    }
                }
            }
            finally
            {
                _advancingMonsterModifiers = false;
            }

            return aggregate;
        }

        private void SetRuntimeMonsterHPWire(Monster monster, uint hp, bool committedDamage, float regenClockTime = -1f, string source = "SET")
        {
            if (monster == null) return;
            if (hp > monster.MaxHPWire) hp = monster.MaxHPWire;
            var state = GetMonsterHPAuthority(monster);
            uint oldHP = state.RuntimeHPWire;
            state.RuntimeHPWire = hp;
            state.RuntimeInitialized = true;
            if (hp == 0 || hp >= monster.MaxHPWire)
            {
                monster.DeathPendingClientConfirmation = false;
                monster.DeathPendingSince = 0f;
            }
            if (committedDamage && hp > 0 && hp < monster.MaxHPWire)
            {
                _monsterRuntimeDamageCommitted.Add(monster.EntityId);
                ushort cooldown = ResolveNativeDamageRegenCooldownTicks(monster);
                if (cooldown > 0)
                    _monsterHPRegenCooldownTicks[monster.EntityId] = cooldown;
                else
                    _monsterHPRegenCooldownTicks.Remove(monster.EntityId);
                Debug.LogError($"[MON-REGEN-COOLDOWN] entity={monster.EntityId} name={monster.Name} cooldown={cooldown} stockUnitDelayClear={ShouldClearStockUnitDamageRegenDelay(monster)} source={source ?? "unknown"}");
            }
            else if (hp == 0 || (!committedDamage && hp >= monster.MaxHPWire))
            {
                _monsterRuntimeDamageCommitted.Remove(monster.EntityId);
                _monsterHPRegenCooldownTicks.Remove(monster.EntityId);
            }
            SyncMonsterHPAuthority(monster, state);
            ResetMonsterHPRegenClock(monster, regenClockTime);
            if (oldHP != hp)
                Debug.LogError($"[MON-HP-CANON] write entity={monster.EntityId} old={oldHP} new={hp} committed={committedDamage} source={source ?? "unknown"}");
        }

        private ushort ResolveNativeDamageRegenCooldownTicks(Monster monster)
        {
            return ShouldClearStockUnitDamageRegenDelay(monster) ? (ushort)0 : NATIVE_DAMAGE_REGEN_COOLDOWN_TICKS;
        }

        private bool ShouldClearStockUnitDamageRegenDelay(Monster monster)
        {
            return monster != null && ResolveNativeStockUnitDamageRegenDelayClearFlag();
        }

        private bool ResolveNativeStockUnitDamageRegenDelayClearFlag()
        {
            int nativeWorldSettingsField = GCDatabase.Instance.GetKnobInt("MinLevelForWorldChat", 15);
            return (nativeWorldSettingsField & 0x00000800) != 0;
        }

        public uint GetMonsterCurrentHPWire(Monster monster)
        {
            return GetRuntimeMonsterHPWire(monster, "HP-READ");
        }

        public uint GetMonsterCurrentHPWire(Monster monster, string source)
        {
            return GetRuntimeMonsterHPWire(monster, source);
        }

        public uint GetMonsterCurrentHPWire(Monster monster, string source, float now)
        {
            return GetRuntimeMonsterHPWire(monster, source, now);
        }

        public uint PeekMonsterCurrentHPWire(Monster monster)
        {
            return PeekRuntimeMonsterHPWire(monster);
        }

        private static bool IsRuntimeMonsterHPSuffixContext(SyncContext context)
        {
            return context == SyncContext.MonsterAction
                || context == SyncContext.MonsterMove
                || context == SyncContext.MonsterDamage;
        }

        public string DescribeMonsterHPAuthority(Monster monster)
        {
            if (monster == null) return "monster=<null>";
            var state = GetMonsterHPAuthority(monster);
            if (state == null) return $"{monster.Name}#{monster.EntityId} state=<null>";

            float observedAge = state.LastClientHPReportTime > 0f ? Time.time - state.LastClientHPReportTime : -1f;
            bool dirty = _monsterRuntimeDamageCommitted.Contains(monster.EntityId);
            return $"{monster.Name}#{monster.EntityId} runtime={state.RuntimeHPWire}/{monster.MaxHPWire} dirty={dirty} client={state.LastClientHPReportWire} clientAge={observedAge:F3} lastPacket={state.LastPacketHPWire}";
        }

        public bool TryResolveMonsterSynchronizedHP(Monster monster, string packetName, out uint hpWire)
        {
            return TryResolveMonsterSynchronizedHP(monster, SyncContext.Unknown, packetName, out hpWire, out _);
        }

        public bool TryResolveMonsterSynchronizedHP(Monster monster, SyncContext context, string packetName, out uint hpWire, out string reason)
        {
            return TryResolveMonsterSynchronizedHP(monster, context, packetName, -1f, out hpWire, out reason);
        }

        public bool TryResolveMonsterSynchronizedHP(Monster monster, SyncContext context, string packetName, float now, out uint hpWire, out string reason)
        {
            hpWire = 0;
            reason = "missing-monster";
            if (monster == null) return false;
            uint serverHPWire = PeekRuntimeMonsterHPWire(monster);
            var state = GetMonsterHPAuthority(monster);
            if (state == null)
            {
                reason = "missing-monster-hp-state";
                return false;
            }

            bool dirtyRuntimeHP = _monsterRuntimeDamageCommitted.Contains(monster.EntityId)
                && serverHPWire > 0
                && serverHPWire < monster.MaxHPWire;
            bool exactClientObservedHP = state.LastClientHPReportTime > 0f
                && state.LastClientHPReportWire == serverHPWire;

            hpWire = serverHPWire;
            state.LastPacketHPWire = hpWire;
            SyncMonsterHPAuthority(monster, state);
            reason = exactClientObservedHP ? "client-confirmed-hp" : dirtyRuntimeHP ? "server-runtime-dirty-hp" : "server-runtime-hp";
            return true;
        }

        public void RecordMonsterHPObservation(Monster monster, uint hpWire, string source)
        {
            if (monster == null) return;
            if (hpWire > monster.MaxHPWire) hpWire = monster.MaxHPWire;
            var state = GetMonsterHPAuthority(monster);
            if (state == null) return;
            state.LastClientHPReportTime = Time.time;
            state.LastClientHPReportWire = hpWire;
            SyncMonsterHPAuthority(monster, state);
            Debug.LogError($"[MON-HP-CLIENT] observed {monster.Name}#{monster.EntityId} hp={hpWire / 256f:F2}/{monster.MaxHPWire / 256f:F2} source={source ?? "unknown"}");
            if (HpSyncService.Instance.TryResolveMonsterOwner(monster, out HpOwnerRef owner))
                HpSyncService.Instance.ObserveClientHpReport(owner, hpWire, HpSyncService.ClassifyReportSource(source), source ?? "client-monster-hp", true, out _);
        }

        public void RecordMonsterOutboundHP(Monster monster, uint hpWire, string source)
        {
            if (monster == null) return;
            if (hpWire > monster.MaxHPWire) hpWire = monster.MaxHPWire;
            var state = GetMonsterHPAuthority(monster);
            if (state == null) return;
            state.LastPacketHPWire = hpWire;
            SyncMonsterHPAuthority(monster, state);
            HpSyncService.Instance.RecordMonsterOutboundHP(monster, hpWire, source ?? "outbound");
        }

        public void SetMonsterHPWire(Monster monster, uint hp, bool committedDamage = false, string source = "SET")
        {
            SetRuntimeMonsterHPWire(monster, hp, committedDamage, -1f, source);
        }

        public void NotifyMonsterDamagedByPlayer(Monster monster, uint playerEntityId, string reason)
        {
            if (monster == null || !monster.IsAlive || monster.DeathPendingClientConfirmation || playerEntityId == 0) return;
            if (!_players.TryGetValue(playerEntityId, out var player) || player == null || !player.IsAlive) return;
            AggroMonster(monster, player, reason, false);
        }

        public bool IsMonsterDeathPendingClientConfirmation(Monster monster)
        {
            return monster != null && monster.DeathPendingClientConfirmation;
        }

        public void MarkMonsterNativeDead(Monster monster, string source)
        {
            MarkMonsterNativeDead(monster, source, true);
        }

        private void MarkMonsterNativeDead(Monster monster, string source, bool mirrorActive)
        {
            if (monster == null) return;

            bool hadRuntimeState = monster.IsAlive
                || monster.State != MonsterState.Dead
                || monster.TargetId != 0
                || monster.AggroTriggered
                || monster.AggroSent
                || monster.AlertSourceEntityId != 0
                || monster.AttackPending
                || monster.AttackSoundPending
                || monster.AttackClientVisible
                || monster.AttackNativeContactOnly
                || monster.CombatContactTargetId != 0;

            monster.DeathPendingClientConfirmation = false;
            monster.DeathPendingSince = 0f;
            monster.IsAlive = false;
            monster.State = MonsterState.Dead;
            monster.ClearTarget();
            monster.AggroTriggered = false;
            monster.AggroSent = false;
            monster.AttackPending = false;
            monster.AttackSoundPending = false;
            monster.AttackClientVisible = false;
            monster.AttackNativeContactOnly = false;
            monster.AttackHitResolved = false;
            monster.AttackStartedTime = 0f;
            monster.AttackCommitTime = 0f;
            monster.AttackSoundTime = 0f;
            monster.AttackEndTime = 0f;
            monster.AttackUseRaw = 0;
            monster.UsePrimaryActiveSkillThisAttack = false;
            monster.AttackCommitTargetX = 0f;
            monster.AttackCommitTargetY = 0f;
            monster.AttackSearchTargetId = 0;
            monster.CombatContactTargetId = 0;
            monster.CombatContactUntil = 0f;
            _monsterFarTargetActionLogTime.Remove(monster.EntityId);
            RemoveMonsterModifiersForTarget(monster.EntityId, source ?? "death");
            WanderSimulator.Instance.UnregisterEntity(monster.EntityId);
            MonsterAttackController.Instance.ClearTarget(monster.EntityId);

            if (hadRuntimeState)
                Debug.LogError($"[MON-DEATH-STATE] cleared action/move target for {monster.Name}#{monster.EntityId} source={source ?? "unknown"}");

            if (mirrorActive && _activeMonsters.TryGetValue(monster.EntityId, out var active) && active != monster)
                MarkMonsterNativeDead(active, source, false);
        }

        private void MarkMonsterDeathPendingClientConfirmation(Monster monster, string source)
        {
            if (monster == null) return;
            bool keepPendingNativeAttack = monster.AttackPending
                && monster.AttackNativeContactOnly
                && !monster.AttackHitResolved
                && monster.AttackCommitTime > 0f
                && monster.TargetId != 0;
            monster.DeathPendingClientConfirmation = true;
            monster.DeathPendingSince = GetNativeCombatTime();
            if (!keepPendingNativeAttack)
            {
                monster.AttackPending = false;
                monster.AttackClientVisible = false;
                monster.AttackNativeContactOnly = false;
                monster.AttackSoundPending = false;
                monster.AttackHitResolved = false;
                monster.AttackStartedTime = 0f;
                monster.AttackEndTime = 0f;
                monster.AttackCommitTime = 0f;
                monster.AttackSoundTime = 0f;
                monster.CombatContactTargetId = 0;
                monster.CombatContactUntil = 0f;
                monster.TargetId = 0;
                monster.AlertSourceEntityId = 0;
                monster.AggroTriggered = false;
                if (monster.IsAlive)
                    monster.State = MonsterState.Idle;
            }
            Debug.LogError($"[MON-DEATH-GATE] pending client death confirmation {monster.Name}#{monster.EntityId} source={source ?? "unknown"} hp={GetRuntimeMonsterHPWire(monster, "death-pending")} keepNativeAttack={keepPendingNativeAttack}");
        }

        public void BeginNativeMonsterDeathLifecycle(Monster monster, string source)
        {
            if (monster == null) return;
            MarkMonsterNativeDead(monster, source);
            if (monster.NativeDeathLifecycleActive)
                return;

            monster.NativeDeathLifecycleActive = true;
            monster.NativeDeathRemoveSent = false;
            monster.NativeDeathState = 7;
            monster.NativeCorpseTicksRemaining = monster.CorpseLingerTicks;
            monster.NativeFadeTicksRemaining = 0;
            _monsterDeathUpdateAccum[monster.EntityId] = 0f;
            WanderSimulator.Instance.UnregisterEntity(monster.EntityId);
            Debug.LogError($"[MON-DEATH-LIFECYCLE] state=7 corpse {monster.Name}#{monster.EntityId} ticks={monster.NativeCorpseTicksRemaining} source={source ?? "unknown"}");
        }

        private void ProcessNativeDeathLifecycles(float deltaTime)
        {
            if (deltaTime <= 0f || _activeMonsters.Count == 0)
                return;

            var monsterIds = new List<uint>(_activeMonsters.Keys);
            foreach (uint entityId in monsterIds)
            {
                if (!_activeMonsters.TryGetValue(entityId, out var monster))
                    continue;
                if (!monster.NativeDeathLifecycleActive || monster.NativeDeathRemoveSent)
                    continue;

                _monsterDeathUpdateAccum.TryGetValue(entityId, out float accum);
                accum += deltaTime;
                int ticks = Mathf.Min(256, Mathf.FloorToInt(accum / NATIVE_UNIT_TICK_INTERVAL));
                if (ticks <= 0)
                {
                    _monsterDeathUpdateAccum[entityId] = accum;
                    continue;
                }

                accum -= ticks * NATIVE_UNIT_TICK_INTERVAL;
                _monsterDeathUpdateAccum[entityId] = accum;

                for (int i = 0; i < ticks; i++)
                {
                    if (monster.NativeDeathState == 7)
                    {
                        if (monster.NativeCorpseTicksRemaining > 0)
                            monster.NativeCorpseTicksRemaining--;
                        if (monster.NativeCorpseTicksRemaining == 0)
                        {
                            monster.NativeDeathState = 9;
                            monster.NativeFadeTicksRemaining = NATIVE_STOCKUNIT_FADE_TICKS;
                            Debug.LogError($"[MON-DEATH-LIFECYCLE] state=9 fade {monster.Name}#{monster.EntityId} ticks={monster.NativeFadeTicksRemaining}");
                        }
                        continue;
                    }

                    if (monster.NativeDeathState == 9)
                    {
                        if (monster.NativeFadeTicksRemaining > 0)
                            monster.NativeFadeTicksRemaining--;
                        if (monster.NativeFadeTicksRemaining == 0)
                        {
                            monster.NativeDeathRemoveSent = true;
                            float respawnDelaySeconds = monster.RespawnRateTicks * NATIVE_UNIT_TICK_INTERVAL;
                            Debug.LogError($"[MON-DEATH-LIFECYCLE] remove {monster.Name}#{monster.EntityId} after corpse/fade autoRespawn={monster.AutoRespawn} respawnTicks={monster.RespawnRateTicks}");
                            DespawnMonster(entityId, monster.AutoRespawn, respawnDelaySeconds);
                            break;
                        }
                    }
                }
            }
        }

        public void LogMonsterClientVisibleSwingNoDamage(Monster monster, string reason)
        {
            if (monster == null || !monster.IsAlive) return;
            uint hp = GetRuntimeMonsterHPWire(monster, "SWING-NO-DAMAGE");
            Debug.LogError($"[MON-HP-TRUTH] NO-DAMAGE {monster.Name}#{monster.EntityId} hp={hp / 256f:F2}/{monster.MaxHPWire / 256f:F2} reason={reason ?? "unknown"}");
        }

        private void RecordMonsterHPReport(Monster monster, MonsterHpAuthorityState state, uint hpWire)
        {
            if (monster == null || state == null) return;
            state.LastClientHPReportTime = Time.time;
            state.LastClientHPReportWire = hpWire;
            SyncMonsterHPAuthority(monster, state);
        }

        public bool ApplyNativePlayerDamageToMonsterWire(Monster monster, uint damageWire, string source, out uint oldHPWire, out uint newHPWire, out bool died, float nativeDamageTime = -1f)
        {
            oldHPWire = 0;
            newHPWire = 0;
            died = false;
            if (monster == null || !monster.IsAlive || damageWire == 0) return false;

            float damageTime = nativeDamageTime >= 0f ? nativeDamageTime : GetNativeCombatTime();
            uint preAdvanceHP = PeekRuntimeMonsterHPWire(monster);
            AdvanceMonsterVitalsToNativeTime(monster, damageTime, $"PRE-DAMAGE:{source ?? "damage"}");
            oldHPWire = PeekRuntimeMonsterHPWire(monster);
            newHPWire = oldHPWire;
            if (preAdvanceHP != oldHPWire)
                Debug.LogError($"[MON-HP-TRUTH] PRE-DAMAGE {monster.Name}#{monster.EntityId} hp={preAdvanceHP}->{oldHPWire}/{monster.MaxHPWire} nativeDamageTime={damageTime:F3} source={source ?? "unknown"}");

            uint adjustedDamageWire = ApplyDamageTakenModWire(damageWire, monster.DamageTakenMod);
            newHPWire = adjustedDamageWire >= oldHPWire ? 0u : oldHPWire - adjustedDamageWire;
            SetRuntimeMonsterHPWire(monster, newHPWire, true, damageTime, source ?? "damage");

            if (newHPWire == 0)
            {
                MarkMonsterNativeDead(monster, source);
                died = true;
            }

            Debug.LogError($"[MONSTER-DAMAGE] source={source ?? "unknown"} target={monster.Name}#{monster.EntityId} damageWire={damageWire} adjustedWire={adjustedDamageWire} hp={oldHPWire}->{newHPWire}/{monster.MaxHPWire} died={died} nativeDamageTime={damageTime:F3}");
            Debug.LogError($"[MON-HP-TRUTH] COMPUTED {monster.Name}#{monster.EntityId} source={source ?? "unknown"} hp={oldHPWire / 256f:F2}->{newHPWire / 256f:F2}/{monster.MaxHPWire / 256f:F2} dmg={damageWire / 256f:F2}");

            return true;
        }

        public bool ObserveClientMonsterHP(Monster monster, uint clientHPWire, string source)
        {
            if (monster == null) return false;
            const uint toleranceWire = 5u * 256u;
            if (clientHPWire > monster.MaxHPWire + toleranceWire)
            {
                Debug.LogError($"[{source}] Monster HP rejected: {monster.Name}#{monster.EntityId} client={clientHPWire / 256f:F2} max={monster.MaxHPWire / 256f:F2}");
                return false;
            }

            var state = GetMonsterHPAuthority(monster);
            if (state == null) return false;
            uint observedHPWire = clientHPWire > monster.MaxHPWire ? monster.MaxHPWire : clientHPWire;
            if (HpSyncService.Instance.TryResolveMonsterOwner(monster, out HpOwnerRef owner))
                HpSyncService.Instance.ObserveClientHpReport(owner, observedHPWire, HpSyncService.ClassifyReportSource(source), source ?? "client-monster-hp", true, out _);
            RecordMonsterHPReport(monster, state, observedHPWire);
            Debug.LogError($"[{source}] Monster HP observed only: {monster.Name}#{monster.EntityId} client={observedHPWire / 256f:F2} server={state.RuntimeHPWire / 256f:F2}/{monster.MaxHPWire / 256f:F2} wire={observedHPWire}");
            return false;
        }

        public bool CanSendMonsterSynchronizedHP(Monster monster, string packetName)
        {
            if (monster == null) return false;
            return TryResolveMonsterSynchronizedHP(monster, packetName, out _);
        }

        private bool AggroMonster(Monster monster, CombatPlayer player, string reason, bool alignForCombat)
        {
            if (monster == null || player == null || !monster.IsAlive || monster.DeathPendingClientConfirmation) return false;
            bool firstAggro = !monster.AggroTriggered || monster.TargetId != player.EntityId;
            monster.AggroTriggered = true;
            monster.TargetId = player.EntityId;
            monster.AlertSourceEntityId = 0;
            monster.State = MonsterState.Combat;
            if (alignForCombat) AlignMonsterForClientCombat(monster, player);
            MonsterAttackController.Instance.SetTarget(monster.EntityId, player.EntityId);
            if (firstAggro)
            {
                WanderSimulator.Instance.UnregisterEntity(monster.EntityId);
                monster.AttackPending = false;
                monster.AttackSoundPending = false;
                Debug.LogError($"[SERVER-AGGRO] {monster.Name} -> {player.Name} reason={reason}");
                // OnMonsterAggro invocation removed 2026-05-27 (audit Phase 1) — event had no subscribers.
                PropagateMonsterShout(monster, player);
            }
            TraceMonsterState(monster, "aggro", player, Distance2D(monster.PosX, monster.PosY, player.PosX, player.PosY), ResolveMonsterEffectiveAttackRange(monster), reason);
            return firstAggro;
        }

        private void PropagateMonsterShout(Monster source, CombatPlayer player)
        {
            if (source == null || player == null || source.ShoutRange <= 0f) return;
            float shoutSq = source.ShoutRange * source.ShoutRange;
            PathMap pathMap = !string.IsNullOrWhiteSpace(source.ZoneName) ? PathMapManager.Instance.GetPathMap(source.ZoneName) : null;
            foreach (var monster in _activeMonsters.Values)
            {
                if (monster == source || !monster.IsAlive || monster.AggroTriggered || monster.TargetId != 0) continue;
                if (!string.Equals(monster.ZoneName, source.ZoneName, StringComparison.OrdinalIgnoreCase)) continue;

                float dx = monster.PosX - source.PosX;
                float dy = monster.PosY - source.PosY;
                float distSq = dx * dx + dy * dy;
                if (distSq > shoutSq) continue;
                if (pathMap != null
                    && pathMap.TryCanReachPoint(source.PosX, source.PosY, monster.PosX, monster.PosY, out bool canAssistReach)
                    && !canAssistReach) continue;

                if (!HasNativeAlertEncounterRelation(monster, source, out string relation))
                {
                    Debug.LogError($"[SERVER-SHOUT] source={source.EntityId} target={monster.EntityId} shout={source.ShoutRange:F1} dist={Mathf.Sqrt(distSq):F1} action=ignored relation={relation} sourceGroup={source.EncounterGroupKey ?? ""} targetGroup={monster.EncounterGroupKey ?? ""}");
                    continue;
                }

                monster.AlertSourceEntityId = source.EntityId;
                Debug.LogError($"[SERVER-SHOUT] source={source.EntityId} target={monster.EntityId} shout={source.ShoutRange:F1} dist={Mathf.Sqrt(distSq):F1} action=alert-source relation={relation} sourceTarget={source.TargetId} sourceGroup={source.EncounterGroupKey ?? ""} targetGroup={monster.EncounterGroupKey ?? ""}");
                TraceMonsterState(monster, "alert", player, Mathf.Sqrt(distSq), ResolveMonsterEffectiveAttackRange(monster), $"source={source.EntityId} relation={relation}");
            }
        }

        private void ProcessMonsterAssistAlerts(Monster onlyMonster = null)
        {
            foreach (var monster in SelectMonsters(onlyMonster))
                TryAssistFromAlertSource(monster, "assist-update");
        }

        private bool TryAssistFromAlertSource(Monster monster, string source)
        {
            if (monster == null || !monster.IsAlive || monster.DeathPendingClientConfirmation)
                return false;
            if (monster.AlertSourceEntityId == 0 || monster.AggroTriggered || monster.TargetId != 0)
                return false;
            if (!_activeMonsters.TryGetValue(monster.AlertSourceEntityId, out var alertSource) || alertSource == null || !alertSource.IsAlive)
            {
                Debug.LogError($"[SERVER-ASSIST] target={monster.Name}#{monster.EntityId} source={monster.AlertSourceEntityId} action=clear reason=missing-alert-source");
                monster.AlertSourceEntityId = 0;
                return false;
            }
            if (!HasNativeAlertEncounterRelation(monster, alertSource, out string relation))
            {
                Debug.LogError($"[SERVER-ASSIST] source={alertSource.EntityId} target={monster.EntityId} action=clear relation={relation} sourceGroup={alertSource.EncounterGroupKey ?? ""} targetGroup={monster.EncounterGroupKey ?? ""}");
                monster.AlertSourceEntityId = 0;
                return false;
            }
            if (alertSource.TargetId == 0 || !_players.TryGetValue(alertSource.TargetId, out var target) || target == null || !target.IsAlive || target.PlayerState == null)
            {
                Debug.LogError($"[SERVER-ASSIST] source={alertSource.EntityId} target={monster.EntityId} action=clear relation={relation} reason=no-source-target sourceTarget={alertSource.TargetId}");
                monster.AlertSourceEntityId = 0;
                return false;
            }
            if (target.PlayerState.IsZoneSpawnDamageImmune || (target.PlayerState.CurrentHPWire == 0 && target.PlayerState.SynchHP == 0))
                return false;

            monster.AggroTriggered = true;
            monster.TargetId = target.EntityId;
            monster.State = MonsterState.Combat;
            WanderSimulator.Instance.UnregisterEntity(monster.EntityId);
            monster.AttackPending = false;
            monster.AttackSoundPending = false;
            Debug.LogError($"[SERVER-ASSIST] source={alertSource.Name}#{alertSource.EntityId} target={monster.Name}#{monster.EntityId} copiedTarget={target.Name}#{target.EntityId} relation={relation} source={source ?? "unknown"}");
            TraceMonsterState(monster, "assist", target, Distance2D(monster.PosX, monster.PosY, target.PosX, target.PosY), ResolveMonsterEffectiveAttackRange(monster), $"source={alertSource.EntityId} relation={relation}");
            return true;
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
            UnregisterNativeEntityOrder(entityId);
            _playerCombatAdvanceTime.Remove(entityId);
            foreach (var monster in _activeMonsters.Values)
            {
                if (monster == null) continue;
                bool touchesPlayer = monster.TargetId == entityId || monster.CombatContactTargetId == entityId;
                if (monster.TargetId == entityId)
                {
                    monster.TargetId = 0;
                    MonsterAttackController.Instance.ClearTarget(monster.EntityId);
                }
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

        public int RoomRngCallsSinceReseed => _roomRng?.CallsSinceReseed ?? 0;

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
            // Client's processRandomSeed @ 0x005da870 applies +0x44 before passing to
            // Random::seed. Server must mirror this transform to keep the MT19937 state
            // aligned with the client (otherwise every roll diverges).
            uint mtSeed = seed + 0x44;
            _roomRng = new MersenneTwister(mtSeed);
            _roomRngInitialized = true;
            Debug.LogError($"[ROOM-RNG] ★ Room RNG initialized with wireSeed=0x{seed:X8} mtSeed=0x{mtSeed:X8}");
            Debug.LogError($"[RNG-SEED] room initialize wire=0x{seed:X8} mt=0x{mtSeed:X8} rngPos=0 monsters={_activeMonsters.Count} players={_players.Count}");
        }

        public void AdvanceRoomRng(int count, string source)
        {
            if (!_roomRngInitialized || _roomRng == null || count <= 0)
            {
                Debug.LogError($"[ROOM-RNG] advance skipped count={count} source={source ?? "unknown"} initialized={_roomRngInitialized} hasRng={_roomRng != null} seed=0x{_roomSeed:X8}");
                return;
            }

            int before = _roomRng.CallsSinceReseed;
            for (int i = 0; i < count; i++)
                _roomRng.Generate();
            Debug.LogError($"[ROOM-RNG] advanced {count} native calls source={source ?? "unknown"} seed=0x{_roomSeed:X8} pos={before}->{_roomRng.CallsSinceReseed}");
        }

        /// <summary>
        /// Re-seed the existing room RNG in-place. Called every 4th tick (~132ms).
        /// Binary-proven: 0x5DF0CB calls Random::seed on EntityManager+0x44
        /// using timeGetTime() as the seed. This re-seeds the EXISTING object,
        /// not creating a new one.
        /// </summary>
        public void ReseedRoomRng(uint seed)
        {
            uint previousSeed = _roomSeed;
            int previousPos = _roomRng?.CallsSinceReseed ?? 0;
            _roomSeed = seed;
            // Apply +0x44 transform to match client's processRandomSeed (see InitializeRoomRng).
            uint mtSeed = seed + 0x44;
            if (_roomRng == null)
            {
                _roomRng = new MersenneTwister(mtSeed);
                _roomRngInitialized = true;
            }
            else
            {
                _roomRng.Seed(mtSeed);
            }
            Debug.LogError($"[RNG-SEED] room reseed previous=0x{previousSeed:X8} wire=0x{seed:X8} mt=0x{mtSeed:X8} previousPos={previousPos} rngPos={_roomRng?.CallsSinceReseed ?? 0}");
        }
        /// <summary>Legacy alias for old code paths.</summary>
        public void InitializeRandomSeed(uint seed)
        {
            if (_roomRngInitialized)
            {
                if (_roomSeed != seed)
                    Debug.LogError($"[ROOM-RNG] Ignored legacy reseed request seed=0x{seed:X8} current=0x{_roomSeed:X8} rngPos={_roomRng?.CallsSinceReseed ?? 0}");
                return;
            }
            InitializeRoomRng(seed);
        }
        // OnMonsterAggro event removed 2026-05-27 (audit Phase 1) — comment at :238 said
        // "REMOVED" but the event was only half-deleted; no subscribers in WORK tree.
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

        public void FlushPlayerCombatBeforeSync(uint playerEntityId, float deltaTime, string source = null, float nativeNowOverride = -1f)
        {
            if (playerEntityId == 0) return;
            float nativeNow = nativeNowOverride >= 0f ? nativeNowOverride : GetNativeCombatTime();
            float nativeDelta = 0f;
            float elapsed = 0f;
            int dueTicks = 0;
            int consumedTicks = 0;
            float previousAdvanceTime = nativeNow;
            float nextAdvanceTime = nativeNow;
            if (deltaTime > 0f)
            {
                nativeDelta = ResolvePlayerCombatAdvanceDelta(playerEntityId, deltaTime, out elapsed, out dueTicks, out consumedTicks, out previousAdvanceTime, out nextAdvanceTime);
            }
            else if (!_playerCombatAdvanceTime.ContainsKey(playerEntityId))
            {
                _playerCombatAdvanceTime[playerEntityId] = nativeNow;
            }
            TracePlayerPreSuffixCombatAdvance(playerEntityId, source ?? "FlushPlayerCombatBeforeSync", deltaTime, nativeDelta, elapsed, dueTicks, consumedTicks, previousAdvanceTime, nextAdvanceTime, nativeNow);
            AdvanceMonsterModifierRuntime(_roomRng, nativeNow, source ?? "FlushPlayerCombatBeforeSync");
            ProcessMonsterAttacks(0f, playerEntityId, false, null, nativeNow);
        }

        private float ResolvePlayerCombatAdvanceDelta(uint playerEntityId, float deltaTime, out float elapsed, out int dueTicks, out int consumedTicks, out float previousAdvanceTime, out float nextAdvanceTime)
        {
            float now = GetNativeCombatTime();
            elapsed = 0f;
            dueTicks = 0;
            consumedTicks = 0;
            previousAdvanceTime = now;
            nextAdvanceTime = now;

            if (deltaTime > 0f)
            {
                elapsed = deltaTime;
                dueTicks = Mathf.Max(1, Mathf.CeilToInt(deltaTime / NATIVE_UNIT_TICK_INTERVAL));
                consumedTicks = dueTicks;
                _playerCombatAdvanceTime[playerEntityId] = now;
                return deltaTime;
            }

            if (!_playerCombatAdvanceTime.TryGetValue(playerEntityId, out previousAdvanceTime) || previousAdvanceTime <= 0f || previousAdvanceTime > now)
            {
                previousAdvanceTime = now;
                _playerCombatAdvanceTime[playerEntityId] = previousAdvanceTime;
                nextAdvanceTime = previousAdvanceTime;
                return 0f;
            }

            elapsed = Mathf.Max(0f, now - previousAdvanceTime);
            dueTicks = Mathf.FloorToInt((elapsed + 0.0001f) / NATIVE_UNIT_TICK_INTERVAL);
            if (dueTicks <= 0)
            {
                nextAdvanceTime = previousAdvanceTime;
                return 0f;
            }

            consumedTicks = dueTicks;
            float nativeDelta = consumedTicks * NATIVE_UNIT_TICK_INTERVAL;
            nextAdvanceTime = previousAdvanceTime + nativeDelta;
            if (nextAdvanceTime > now)
                nextAdvanceTime = now;
            _playerCombatAdvanceTime[playerEntityId] = nextAdvanceTime;
            return nativeDelta;
        }

        private void TracePlayerPreSuffixCombatAdvance(uint playerEntityId, string source, float requestedDelta, float nativeDelta, float elapsed, int dueTicks, int consumedTicks, float previousAdvanceTime, float nextAdvanceTime, float nativeNow)
        {
            if (!_players.TryGetValue(playerEntityId, out var player) || player == null)
                return;

            foreach (var monster in _activeMonsters.Values)
            {
                if (monster == null || !monster.AggroTriggered || !monster.IsAlive)
                    continue;
                bool targetsPlayer = monster.TargetId == playerEntityId;
                bool contactsPlayer = monster.CombatContactTargetId == playerEntityId && nativeNow <= monster.CombatContactUntil;
                if (!targetsPlayer && !contactsPlayer)
                    continue;

                float dist = Distance2D(monster.PosX, monster.PosY, player.PosX, player.PosY);
                float allowedRange = ResolveMonsterEffectiveAttackRange(monster);
                string action = nativeDelta > 0f ? "advance" : "due-drain";
                Debug.LogError($"[PRE-SUFFIX-DUE-DRAIN] action={action} source={source ?? "unknown"} player={player.Name}#{player.EntityId} monster={monster.Name}#{monster.EntityId} behavior={monster.BehaviorId} unit={monster.UnitId} gc='{monster.GCType}' spawnGc='{monster.SpawnGCType}' state={monster.State} target={monster.TargetId} pending={monster.AttackPending} hitResolved={monster.AttackHitResolved} clientVisible={monster.AttackClientVisible} nativeContact={monster.AttackNativeContactOnly} contactTarget={monster.CombatContactTargetId} dist={dist:F1} range={allowedRange:F1} requestedDelta={requestedDelta:F3} nativeDelta={nativeDelta:F3} elapsed={elapsed:F3} dueTicks={dueTicks} consumedTicks={consumedTicks} clock={previousAdvanceTime:F3}->{nextAdvanceTime:F3} nativeNow={nativeNow:F3} lastAttack={monster.LastAttackTime:F3} commit={monster.AttackCommitTime:F3} end={monster.AttackEndTime:F3} pathGate=native");
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
            return "dungeon_specific";
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

        public Monster SpawnMonster(string gcType, float posX, float posY, float posZ, float heading = 0f, string zoneName = null, string encounterGroupKey = null, float encounterDifficulty = 1f, string spawnGcTypeOverride = null)
        {
            var creatureData = DatabaseLoader.FindCreature(gcType);
            if (creatureData == null)
            {
                Debug.LogError($"[Combat] CREATURE NOT FOUND: '{gcType}'");
                return null;
            }

            string spawnGcType = !string.IsNullOrEmpty(spawnGcTypeOverride)
                ? spawnGcTypeOverride
                : ResolveDungeonCreaturePath(zoneName, creatureData.gcType);
            string zoneBehaviourType = GetBehaviorForZone(zoneName);
            GCNode authoredCreature = ResolveAuthoredCreatureNode(spawnGcType, creatureData.gcType);
            string spawnBehaviourType = ResolveSpawnBehaviourType(zoneBehaviourType, spawnGcType, creatureData.gcType);
            GCNode authoredDesc = authoredCreature?.GetChild("Description") ?? authoredCreature;
            GCNode authoredWeapon = GetAuthoredWeaponDescription(authoredCreature);
            GCNode authoredBehavior = ResolveAuthoredBehaviorNode(spawnBehaviourType);
            var spawnManipulators = BuildSpawnManipulators(spawnGcType, creatureData.gcType, authoredCreature, creatureData.manipulators);
            string creatureDifficulty = GetAuthoredString(authoredDesc, "CreatureDifficulty", creatureData.creatureDifficulty);
            float unitDifficulty = GetAuthoredFloat(authoredDesc, "Difficulty", MonsterHealthTable.GetDifficultyModifier(creatureDifficulty));
            float maxHealth = GetAuthoredFloat(authoredDesc, "MaxHealth", creatureData.maxHealth);
            float perceptionRange = GetAuthoredBehaviorFloat(authoredBehavior, authoredCreature, "Perception", NATIVE_DEFAULT_UNIT_PERCEPTION);
            float aggroFallback = NATIVE_DEFAULT_MONSTER_AGGRO_RANGE;
            float attackCooldown = GetAuthoredFloat(authoredWeapon, "CoolDown", GetAttackCooldownFromManipulators(creatureData));
            float attackLeadDelay = GetAuthoredAttackLeadDelay(authoredDesc, Mathf.Max(0.1f, attackCooldown * 0.5f), out float attackSoundLeadDelay);
            string attackType = GetAuthoredBehaviorString(authoredBehavior, authoredCreature, "AttackType", "0");
            string idleAction = GetAuthoredBehaviorString(authoredBehavior, authoredCreature, "IdleAction", "3");
            string logicType = GetAuthoredBehaviorString(authoredBehavior, authoredCreature, "LogicType", "0");
            string attackStyle = GetAuthoredBehaviorString(authoredBehavior, authoredCreature, "AttackStyle", "0");
            bool retreatable = GetAuthoredBehaviorBool(authoredBehavior, authoredCreature, "Retreatable", NATIVE_DEFAULT_MONSTER_RETREATABLE);
            bool leashed = GetAuthoredBehaviorBool(authoredBehavior, authoredCreature, "Leashed", NATIVE_DEFAULT_MONSTER_LEASHED);
            bool useIdleTime = GetAuthoredBehaviorBool(authoredBehavior, authoredCreature, "UseIdleTime", NATIVE_DEFAULT_MONSTER_USE_IDLE_TIME);
            bool autoScan = GetAuthoredBehaviorBool(authoredBehavior, authoredCreature, "AutoScan", NATIVE_DEFAULT_UNIT_AUTO_SCAN);
            bool avoidUnits = GetAuthoredBehaviorBool(authoredBehavior, authoredCreature, "AvoidUnits", NATIVE_DEFAULT_UNIT_AVOID_UNITS);
            bool turnBeforeMoving = GetAuthoredBehaviorBool(authoredBehavior, authoredCreature, "TurnBeforeMoving", NATIVE_DEFAULT_UNIT_TURN_BEFORE_MOVING);
            bool playerControlled = GetAuthoredBehaviorBool(authoredBehavior, authoredCreature, "PlayerControlled", NATIVE_DEFAULT_UNIT_PLAYER_CONTROLLED);
            int collisionBand = GetAuthoredBehaviorInt(authoredBehavior, authoredCreature, "CollisionBand", NATIVE_DEFAULT_UNIT_COLLISION_BAND);
            int collisionPriority = GetAuthoredBehaviorInt(authoredBehavior, authoredCreature, "CollisionPriority", NATIVE_DEFAULT_UNIT_COLLISION_PRIORITY);
            float scanFrequency = GetAuthoredBehaviorFloat(authoredBehavior, authoredCreature, "ScanFrequency", NATIVE_DEFAULT_UNIT_SCAN_FREQUENCY);
            float fleeRange = GetAuthoredBehaviorFloat(authoredBehavior, authoredCreature, "FleeRange", NATIVE_DEFAULT_UNIT_FLEE_RANGE);
            float retreatRangeSquared = GetAuthoredBehaviorFloat(authoredBehavior, authoredCreature, "RetreatRangeSquared", NATIVE_DEFAULT_MONSTER_RETREAT_RANGE_SQUARED);
            float teleportFrequency = GetAuthoredBehaviorFloat(authoredBehavior, authoredCreature, "TeleportFrequency", NATIVE_DEFAULT_MONSTER_TELEPORT_FREQUENCY);
            float teleportLimboTime = GetAuthoredBehaviorFloat(authoredBehavior, authoredCreature, "TeleportLimboTime", NATIVE_DEFAULT_MONSTER_TELEPORT_LIMBO_TIME);
            float baseTime = GetAuthoredBehaviorFloat(authoredBehavior, authoredCreature, "BaseTime", NATIVE_DEFAULT_MONSTER_BASE_TIME);
            float variableTime = GetAuthoredBehaviorFloat(authoredBehavior, authoredCreature, "VariableTime", NATIVE_DEFAULT_MONSTER_VARIABLE_TIME);
            float leashRange = GetAuthoredBehaviorFloat(authoredBehavior, authoredCreature, "LeashRange", NATIVE_DEFAULT_MONSTER_LEASH_RANGE);

            if (!_roomRngInitialized)
            {
                Debug.LogError($"[ROOM-RNG] Blocked monster spawn before native seed gcType='{gcType}' zone='{zoneName}'");
                return null;
            }

            // S10h 2026-05-27: prefer rank-based level when the spawn GC path matches
            // retail's `world.dungeon00.mob.melee0N.rankM` aliasing pattern. From video
            // evidence in dungeon00 (zone range 1..5): rank1=L1, rank2=L4, rank3=L5.
            // Falls back to legacy (tier + zoneBase) when no rank info is available.
            byte rankLevel = ResolveMobLevelFromRank(spawnGcType, zoneName);
            byte calculatedLevel;
            if (rankLevel > 0)
            {
                calculatedLevel = rankLevel;
                Debug.LogError($"[Combat] Level calc: rank-based spawn='{spawnGcType}' zone='{zoneName}' = {calculatedLevel}");
            }
            else
            {
                byte tierLevel = GetLevelForTier(creatureDifficulty);
                byte zoneBase = GetZoneBaseLevel(zoneName);
                calculatedLevel = (byte)Math.Min(110, tierLevel + zoneBase);
                Debug.LogError($"[Combat] Level calc: tier={creatureDifficulty}({tierLevel}) + zone={zoneName}({zoneBase}) = {calculatedLevel}");
            }

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
                Difficulty = unitDifficulty,
                ExperienceDifficulty = encounterDifficulty,

                MaxHPWire = MonsterHealthTable.CalculateHPWire(calculatedLevel, unitDifficulty, maxHealth),
                CurrentHPWire = MonsterHealthTable.CalculateHPWire(calculatedLevel, unitDifficulty, maxHealth),


                MaxManaWire = (uint)(creatureData.manaPoints * 256),
                CurrentManaWire = (uint)(creatureData.manaPoints * 256),
                BaseDamage = creatureData.baseDamage,
                AttackRating = GetAuthoredFloat(authoredDesc, "AttackRating", creatureData.AttackRatingF),
                DamageMod = GetAuthoredFloat(authoredDesc, "DamageMod", creatureData.DamageModF),
                DamageTakenMod = GetAuthoredFloat(authoredDesc, "DamageTakenMod", 100f),
                DefenseRating = GetAuthoredFloat(authoredDesc, "DefenseRating", creatureData.DefenseRatingF),
                CritChance = GetAuthoredFloat(authoredDesc, "CriticalChance", creatureData.CritChanceF),
                DivineResist = GetAuthoredFloat(authoredDesc, "DivineResist", creatureData.DivineResistF),
                FireResist = GetAuthoredFloat(authoredDesc, "FireResist", creatureData.FireResistF),
                IceResist = GetAuthoredFloat(authoredDesc, "IceResist", creatureData.IceResistF),
                PoisonResist = GetAuthoredFloat(authoredDesc, "PoisonResist", creatureData.PoisonResistF),
                ShadowResist = GetAuthoredFloat(authoredDesc, "ShadowResist", creatureData.ShadowResistF),
                HealthRegen = GetAuthoredFloat(authoredDesc, "HealthRegen", 0f),
                HasAuthoredHealthRegen = authoredDesc != null && authoredDesc.HasProperty("HealthRegen"),
                ManaRegen = GetAuthoredFloat(authoredDesc, "ManaRegen", GetAuthoredFloat(authoredDesc, "PowerRegen", 0f)),
                HasAuthoredManaRegen = authoredDesc != null && (authoredDesc.HasProperty("ManaRegen") || authoredDesc.HasProperty("PowerRegen")),
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
                ShoutRange = GetAuthoredBehaviorFloat(authoredBehavior, authoredCreature, "ShoutRange", NATIVE_DEFAULT_MONSTER_SHOUT_RANGE),
                LeashRange = leashRange,
                AttackRange = GetAuthoredAttackRange(authoredDesc, authoredWeapon, creatureData),
                ClientSyncTolerance = GetAuthoredFloat(authoredWeapon, "ClientSyncTolerance", GetManipulatorFloat(creatureData, "ClientSyncTolerance", 10f)),
                CollisionRadius = GetAuthoredFloat(authoredDesc, "CollisionRadius", 5f),
                AttackType = attackType,
                IdleAction = idleAction,
                LogicType = logicType,
                AttackStyle = attackStyle,
                Retreatable = retreatable,
                Leashed = leashed,
                UseIdleTime = useIdleTime,
                AutoScan = autoScan,
                AvoidUnits = avoidUnits,
                TurnBeforeMoving = turnBeforeMoving,
                PlayerControlled = playerControlled,
                CollisionBand = collisionBand,
                CollisionPriority = collisionPriority,
                ScanFrequency = scanFrequency,
                FleeRange = fleeRange,
                RetreatRangeSquared = retreatRangeSquared,
                TeleportFrequency = teleportFrequency,
                TeleportLimboTime = teleportLimboTime,
                BaseTime = baseTime,
                VariableTime = variableTime,
                CorpseLingerTicks = (ushort)Mathf.Clamp(GetAuthoredInt(authoredDesc, "CorpseLingerTime", 900), 0, ushort.MaxValue),
                AutoRespawn = GetAuthoredBool(authoredCreature, "AutoRespawn", GetAuthoredBool(authoredDesc, "AutoRespawn", false)),
                RespawnRateTicks = (ushort)Mathf.Clamp(GetAuthoredInt(authoredCreature, "RespawnRate", GetAuthoredInt(authoredDesc, "RespawnRate", 3600)), 0, ushort.MaxValue),
                AttackSpeed = GetAuthoredFloat(authoredDesc, "AttackSpeed", 1f),
                AttackCooldown = attackCooldown,
                AttackLeadDelay = attackLeadDelay,
                AttackSoundLeadDelay = attackSoundLeadDelay,
                HasAttackSound = HasAuthoredAttackSound(authoredDesc),
                AttackWeaponSoundCount = CountAuthoredSounds(authoredDesc, "WEAPONATTACK"),
                AttackRepeatSoundCount = CountAuthoredSounds(authoredDesc, "ATTACK"),
                AttackTotalFrames = GetAuthoredAttackFrames(authoredDesc, "NumFrames", 30),
                AttackHitFrames = GetAuthoredAttackFrames(authoredDesc, "TriggerTime", 15),
                AttackSoundFrames = GetAuthoredAttackFrames(authoredDesc, "SoundTriggerTime", 10),
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
            RegisterNativeEntityOrder(entityId);
            SetRuntimeMonsterHPWire(monster, monster.CurrentHPWire, false, Time.time, "SPAWN");
            monster.RngSeed = _roomSeed;
            ConfigureMonsterPrimaryActiveSkill(monster);
            Debug.LogError($"[Combat] Monster {monster.Name} using room RNG seed: 0x{_roomSeed:X8}");

            _componentToEntityMap[entityId] = entityId;
            _componentToEntityMap[behaviorId] = entityId;
            _componentToEntityMap[skillsId] = entityId;
            _componentToEntityMap[manipulatorsId] = entityId;
            _componentToEntityMap[modifiersId] = entityId;
            _componentToEntityMap[unitId] = entityId;

            if (ShouldRegisterWander(idleAction, zoneName))
                WanderSimulator.Instance.RegisterMonster(monster, true);

            // PA1.3 hoist: every mob gets a UnitMoverSim, not just wander-registered ones.
            // Dungeon mobs with IdleAction=STAND/GUARD wouldn't go through WanderSimulator,
            // but they still need a Mover for chase to route through ProcessMonsterMovement's
            // UnitMoverSim path. WanderSimulator's RegisterMonster may have already created
            // one — only construct if not already assigned.
            if (monster.Mover == null)
            {
                var pathMap = !string.IsNullOrEmpty(monster.ZoneName)
                    ? PathMapManager.Instance.GetPathMap(monster.ZoneName)
                    : null;
                monster.Mover = new UnitMoverSim
                {
                    PosX = Utilities.Fixed32.FromFloat(monster.PosX),
                    PosY = Utilities.Fixed32.FromFloat(monster.PosY),
                    PosZ = Utilities.Fixed32.Zero,
                    PathMap = pathMap,
                    Pathfinder = pathMap != null ? new Core.Pathfinder(pathMap) : null,
                    ArriveRadius = Utilities.Fixed32.FromInt(5),
                };
                Debug.LogError($"[CHASE-MOVER-CUTOVER] Provisioned Mover for {monster.Name}#{monster.EntityId} zone={monster.ZoneName} pathMap={(pathMap != null)} pos=({monster.PosX:F1},{monster.PosY:F1})");
            }

            int nativeAttackRating = DamageComputer.ResolveNativeMonsterAttackRating(monster);
            int nativeDefenseRating = DamageComputer.ResolveNativeMonsterDefenseRating(monster);
            float monsterDamageTable = ResolveMonsterDamageTable(monster.Level);
            float effectiveWeaponDamage = monster.WeaponDamage > 0f ? monster.WeaponDamage : 1f;
            float effectiveDamage = monsterDamageTable * ResolveMonsterDamageModifier(monster) * effectiveWeaponDamage;
            Debug.LogError($"[Combat] SPAWNED: {monster.Name} (ID:{entityId}) Level:{calculatedLevel} HP:{monster.MaxHP} DMG:{creatureData.baseDamage} UnitDifficulty={monster.Difficulty:F2} DamageMod={monster.DamageMod:F2} EncounterDifficulty={monster.ExperienceDifficulty:F2} EffectiveDamageMod={ResolveMonsterDamageModifier(monster):F2}");
            Debug.LogError($"[SPAWN-AUDIT] id={entityId} name='{monster.Name}' baseGc='{monster.GCType}' spawnGc='{monster.SpawnGCType}' zone='{zoneName}' group='{monster.EncounterGroupKey}' level={monster.Level} hpWire={monster.MaxHPWire} manaWire={monster.MaxManaWire} hpRegen={monster.HealthRegen:F3} manaRegen={monster.ManaRegen:F3} hpRegenFactor={ResolveMonsterHealthRegenFactor(monster)} manaRegenFactor={ResolveMonsterManaRegenFactor(monster)} unitDiff={monster.Difficulty:F2} encounterDiff={monster.ExperienceDifficulty:F2} attackRatingAuth={monster.AttackRating:F3} attackRatingNative={nativeAttackRating} defenseRatingAuth={monster.DefenseRating:F3} defenseRatingNative={nativeDefenseRating} crit={monster.CritChance:F2} damageTable={monsterDamageTable:F3} damageMod={monster.DamageMod:F3} weaponDamage={monster.WeaponDamage:F3} volatility={monster.DamageVolatility:F3} effectiveDamage={effectiveDamage:F3} aggro={monster.AggroRange:F1} wander={monster.WanderRange:F1} pos=({monster.PosX:F1},{monster.PosY:F1},{monster.PosZ:F1})");
            Debug.LogError($"[Combat]   ComponentIDs: Entity={entityId}, Behavior={behaviorId}, Skills={skillsId}, Manip={manipulatorsId}, Mods={modifiersId}, Unit={unitId}");
            Debug.LogError($"[Combat]   Position: ({posX:F1}, {posY:F1}, {posZ:F1}) PerceptionRange={monster.PerceptionRange} AggroRange={monster.AggroRange} ShoutRange={monster.ShoutRange} LeashRange={monster.LeashRange} AttackRange={monster.AttackRange} SyncTolerance={monster.ClientSyncTolerance} CollisionRadius={monster.CollisionRadius} AttackSpeed={monster.AttackSpeed} Cooldown={monster.AttackCooldown} WalkSpeed={monster.WalkSpeed} WanderRange={monster.WanderRange} Group={monster.EncounterGroupKey}");
            Debug.LogError($"[Combat]   NativeAI: AttackType={monster.AttackType} IdleAction={monster.IdleAction} LogicType={monster.LogicType} AttackStyle={monster.AttackStyle} Retreatable={monster.Retreatable} Leashed={monster.Leashed} UseIdleTime={monster.UseIdleTime} AutoScan={monster.AutoScan} AvoidUnits={monster.AvoidUnits} TurnBeforeMoving={monster.TurnBeforeMoving} PlayerControlled={monster.PlayerControlled} CollisionBand={monster.CollisionBand} CollisionPriority={monster.CollisionPriority} ScanFrequency={monster.ScanFrequency} FleeRange={monster.FleeRange} RetreatRangeSquared={monster.RetreatRangeSquared} TeleportFrequency={monster.TeleportFrequency} TeleportLimboTime={monster.TeleportLimboTime} BaseTime={monster.BaseTime} VariableTime={monster.VariableTime}");
            TraceMonsterState(monster, "spawn", null, -1f, monster.AttackRange, "spawn");

            OnMonsterSpawned?.Invoke(monster);
            RegisterMonsterForServerCombat(monster);
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

        private void ConfigureMonsterPrimaryActiveSkill(Monster monster)
        {
            if (monster == null) return;
            monster.PrimaryActiveSkillPath = null;
            monster.PrimaryActiveSkillId = 10;
            monster.PrimaryActiveSkillRange = 0f;
            monster.PrimaryActiveSkillCooldownSeconds = 0f;
            monster.PrimaryActiveSkillCooldownTicks = 0;
            monster.PrimaryActiveSkillCooldownRemainingTicks = 0;
            monster.PrimaryActiveSkillCooldownLastTime = GetNativeCombatTime();
            monster.PrimaryActiveSkillAnimationId = 0;
            monster.PrimaryActiveSkillEffect = null;
            monster.PrimaryActiveSkillCastModifier = null;
            monster.UsePrimaryActiveSkillThisAttack = false;

            if (monster.Manipulators == null) return;
            foreach (var manipulator in monster.Manipulators.Values)
            {
                if (!IsNativePrimaryActiveSkillManipulator(manipulator))
                    continue;

                monster.PrimaryActiveSkillPath = manipulator.gcType;
                monster.PrimaryActiveSkillId = GetManipulatorByte(manipulator, "ID", 10);
                monster.PrimaryActiveSkillRange = GetManipulatorFloat(manipulator, "Range", 0f);
                monster.PrimaryActiveSkillCooldownSeconds = Mathf.Max(0f, GetManipulatorFloat(manipulator, "CoolDown", 0f));
                monster.PrimaryActiveSkillCooldownTicks = (ushort)Mathf.Clamp(Mathf.RoundToInt(monster.PrimaryActiveSkillCooldownSeconds * 30f), 0, ushort.MaxValue);
                monster.PrimaryActiveSkillAnimationId = GetManipulatorInt(manipulator, "AnimationID", 0);
                monster.PrimaryActiveSkillEffect = GetManipulatorString(manipulator, "Effect", null);
                monster.PrimaryActiveSkillCastModifier = GetManipulatorString(manipulator, "CastModifier", null);
                Debug.LogError($"[MON-SKILL] build {monster.Name}#{monster.EntityId} path={monster.PrimaryActiveSkillPath} id={monster.PrimaryActiveSkillId} range={monster.PrimaryActiveSkillRange:F1} cooldown={monster.PrimaryActiveSkillCooldownSeconds:F2}s/{monster.PrimaryActiveSkillCooldownTicks}ticks anim={monster.PrimaryActiveSkillAnimationId} effect={monster.PrimaryActiveSkillEffect ?? "none"} castMod={monster.PrimaryActiveSkillCastModifier ?? "none"}");
                return;
            }
        }

        private bool IsNativePrimaryActiveSkillManipulator(ManipulatorData manipulator)
        {
            if (manipulator == null || string.IsNullOrWhiteSpace(manipulator.gcType))
                return false;
            if (!IsActiveSkillManipulatorPath(manipulator.gcType))
                return false;
            if (TryGetManipulatorBool(manipulator, "IsPrimaryAttack", out bool primaryFromManipulator))
                return primaryFromManipulator;

            var node = GCDatabase.Instance?.ResolveWithInheritance(manipulator.gcType);
            var desc = node?.GetChild("Description") ?? node;
            return desc != null && desc.GetBool("IsPrimaryAttack", false);
        }

        private bool IsActiveSkillManipulatorPath(string gcType)
        {
            var gc = GCDatabase.Instance;
            string current = gcType;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (!string.IsNullOrWhiteSpace(current) && visited.Add(current))
            {
                if (current.Equals("ActiveSkill", StringComparison.OrdinalIgnoreCase)
                    || current.EndsWith(".ActiveSkill", StringComparison.OrdinalIgnoreCase))
                    return true;

                var node = gc?.Resolve(current);
                current = node?.Extends;
            }
            return !string.IsNullOrEmpty(gcType) && gcType.StartsWith("skills.", StringComparison.OrdinalIgnoreCase);
        }

        private void CopyManipulatorProperties(ManipulatorData data, GCNode node)
        {
            if (data == null || node == null) return;
            foreach (var kvp in node.Properties)
                data.properties[kvp.Key] = kvp.Value;
        }

        private static float GetManipulatorFloat(ManipulatorData manipulator, string property, float fallback)
        {
            if (manipulator?.properties == null || !manipulator.properties.TryGetValue(property, out string raw))
                return fallback;
            return float.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float value)
                ? value
                : fallback;
        }

        private static int GetManipulatorInt(ManipulatorData manipulator, string property, int fallback)
        {
            if (manipulator?.properties == null || !manipulator.properties.TryGetValue(property, out string raw))
                return fallback;
            return int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int value)
                ? value
                : fallback;
        }

        private static byte GetManipulatorByte(ManipulatorData manipulator, string property, byte fallback)
        {
            int value = GetManipulatorInt(manipulator, property, fallback);
            return (byte)Mathf.Clamp(value, byte.MinValue, byte.MaxValue);
        }

        private static string GetManipulatorString(ManipulatorData manipulator, string property, string fallback)
        {
            if (manipulator?.properties == null || !manipulator.properties.TryGetValue(property, out string raw))
                return fallback;
            return string.IsNullOrWhiteSpace(raw) ? fallback : raw.Trim().Trim('"');
        }

        private static bool TryGetManipulatorBool(ManipulatorData manipulator, string property, out bool value)
        {
            value = false;
            if (manipulator?.properties == null || !manipulator.properties.TryGetValue(property, out string raw))
                return false;
            raw = raw?.Trim().Trim('"');
            if (bool.TryParse(raw, out value))
                return true;
            if (int.TryParse(raw, out int intValue))
            {
                value = intValue != 0;
                return true;
            }
            return false;
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

        private int GetAuthoredInt(GCNode node, string property, int fallback)
        {
            return node != null && node.HasProperty(property) ? node.GetInt(property, fallback) : fallback;
        }

        private bool GetAuthoredBool(GCNode node, string property, bool fallback)
        {
            return node != null && node.HasProperty(property) ? node.GetBool(property, fallback) : fallback;
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

        private int GetAuthoredBehaviorInt(GCNode behaviorNode, GCNode creatureNode, string property, int fallback)
        {
            var behaviorDesc = ResolveAuthoredBehaviorDescription(behaviorNode);
            if (behaviorDesc != null && behaviorDesc.HasProperty(property))
                return behaviorDesc.GetInt(property, fallback);
            var creatureDesc = GetAuthoredBehaviorDescription(creatureNode);
            return GetAuthoredInt(creatureDesc, property, fallback);
        }

        private bool GetAuthoredBehaviorBool(GCNode behaviorNode, GCNode creatureNode, string property, bool fallback)
        {
            var behaviorDesc = ResolveAuthoredBehaviorDescription(behaviorNode);
            if (behaviorDesc != null && behaviorDesc.HasProperty(property))
                return behaviorDesc.GetBool(property, fallback);
            var creatureDesc = GetAuthoredBehaviorDescription(creatureNode);
            return GetAuthoredBool(creatureDesc, property, fallback);
        }

        private bool ShouldRegisterWander(string idleAction, string zoneName)
        {
            if (!string.IsNullOrEmpty(idleAction))
            {
                if (IsWanderIdleAction(idleAction))
                    return true;
                if (idleAction.Equals("FOLLOW", StringComparison.OrdinalIgnoreCase))
                    return false;
                if (idleAction.Equals("GUARD", StringComparison.OrdinalIgnoreCase) ||
                    idleAction.Equals("NOTHING", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return false;
        }

        private static bool IsWanderIdleAction(string idleAction)
        {
            return !string.IsNullOrEmpty(idleAction) &&
                   idleAction.Equals("WANDER", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasNativeEncounterObject(Monster monster)
        {
            return monster != null && !string.IsNullOrEmpty(monster.EncounterGroupKey);
        }

        private static bool HasNativeAlertEncounterRelation(Monster listener, Monster source, out string relation)
        {
            relation = "invalid";
            if (listener == null || source == null)
                return false;
            if (!HasNativeEncounterObject(listener))
            {
                relation = "listener-no-encounter";
                return true;
            }
            if (!HasNativeEncounterObject(source))
            {
                relation = "source-no-encounter";
                return false;
            }
            if (string.Equals(listener.EncounterGroupKey, source.EncounterGroupKey, StringComparison.OrdinalIgnoreCase))
            {
                relation = "same-encounter";
                return true;
            }
            relation = "different-encounter";
            return false;
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
            return NATIVE_DEFAULT_MONSTER_WANDER_RANGE;
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

        private int[] GetAuthoredAttackFrames(GCNode authoredDesc, string property, int fallback)
        {
            int[] values = { fallback, fallback, fallback };
            string animationsPath = GetAuthoredString(authoredDesc, "Animations", "");
            if (string.IsNullOrEmpty(animationsPath)) return values;
            var animations = GCDatabase.Instance.ResolveWithInheritance(animationsPath);
            if (animations?.AnonymousChildren == null) return values;
            foreach (var animation in animations.AnonymousChildren)
            {
                int id = animation.GetInt("ID", 0);
                int index = id >= 110 && id <= 112 ? id - 110 : (id >= 510 && id <= 512 ? id - 510 : -1);
                if (index < 0 || index >= values.Length) continue;
                int value = animation.GetInt(property, fallback);
                if (value > 0) values[index] = value;
            }
            return values;
        }

        private int CountAuthoredSounds(GCNode authoredDesc, string soundId)
        {
            string soundsPath = GetAuthoredString(authoredDesc, "Sounds", "");
            if (string.IsNullOrEmpty(soundsPath)) return 0;
            var sounds = GCDatabase.Instance.ResolveWithInheritance(soundsPath);
            if (sounds?.AnonymousChildren == null) return 0;
            foreach (var sound in sounds.AnonymousChildren)
            {
                string current = sound.GetString("SoundId", "");
                if (!string.Equals(current, soundId, StringComparison.OrdinalIgnoreCase)) continue;
                string list = sound.GetString("Sounds", "");
                if (string.IsNullOrWhiteSpace(list)) return 0;
                int count = 0;
                foreach (string item in list.Split(','))
                {
                    if (!string.IsNullOrWhiteSpace(item.Trim().Trim('"'))) count++;
                }
                return count;
            }
            return 0;
        }

        private bool HasAuthoredAttackSound(GCNode authoredDesc)
        {
            return CountAuthoredSounds(authoredDesc, "ATTACK") > 0 || CountAuthoredSounds(authoredDesc, "WEAPONATTACK") > 0;
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
                var state = GetMonsterHPAuthority(kvp.Value);
                state.LastClientHPReportTime = 0f;
                state.LastClientHPReportWire = kvp.Value.MaxHPWire;
                SyncMonsterHPAuthority(kvp.Value, state);
                kvp.Value.UseTargetCount = 0;
                kvp.Value.AlertSourceEntityId = 0;
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
        public void DespawnMonster(uint entityId, bool allowRespawn = true, float respawnDelaySeconds = 30f)
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
            _monsterHPAuthority.Remove(entityId);
            _monsterRuntimeDamageCommitted.Remove(entityId);
            _monsterHPRegenLastTime.Remove(entityId);
            _monsterHPRegenCarryWire.Remove(entityId);
            _monsterHPRegenCooldownTicks.Remove(entityId);
            _monsterManaRegenLastTime.Remove(entityId);
            _monsterManaRegenCooldownTicks.Remove(entityId);
            _monsterDeathUpdateAccum.Remove(entityId);
            _monsterStateTraceSignatures.Remove(entityId);

            WanderSimulator.Instance.UnregisterEntity(entityId);
            MonsterAttackController.Instance.Unregister(entityId);
            OnMonsterDespawned?.Invoke(monster);
            UnregisterNativeEntityOrder(entityId);
            _activeMonsters.Remove(entityId);

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
                    EncounterDifficulty = monster.ExperienceDifficulty,
                    RespawnTime = Time.time + Mathf.Max(0f, respawnDelaySeconds)
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

        public float GetMonsterEffectiveAttackRange(Monster monster)
        {
            return ResolveMonsterEffectiveAttackRange(monster);
        }

        public float ResolvePlayerMeleeRange(PlayerState state, Monster monster)
        {
            float weaponRange = state != null && state.WeaponRange > 0 ? state.WeaponRange : monster != null ? monster.AttackRange : 0f;
            float monsterRadius = monster != null ? Mathf.Max(0f, monster.CollisionRadius) : 0f;
            return Mathf.Max(1f, weaponRange) + ResolveAvatarCombatRadius() + monsterRadius;
        }

        public float ResolvePlayerMeleeNativeContactRange(PlayerState state, Monster monster)
        {
            return ResolvePlayerMeleeRange(state, monster);
        }

        public float ResolvePlayerRangedProjectileRange(PlayerState state, Monster monster)
        {
            float range = ResolvePlayerMeleeRange(state, monster);
            if (state == null || !DamageComputer.IsNativeRangedWeapon(state) || !state.WeaponUsesProjectile)
                return range;

            float projectileSize = Mathf.Max(0f, state.WeaponProjectileSize);
            float firstTickTravel = state.WeaponProjectileSpeed > 0f ? Mathf.Max(0f, state.WeaponProjectileSpeed) * NATIVE_UNIT_TICK_INTERVAL : 0f;
            return range + projectileSize + firstTickTravel;
        }

        private float ResolveNativeClientContactRange(Monster monster, CombatPlayer player, float allowedRange)
        {
            if (monster == null || player == null) return 0f;
            return allowedRange;
        }

        private bool IsMonsterAttackPathClear(Monster monster, CombatPlayer target, string source)
        {
            if (monster == null || target == null) return false;
            if (string.IsNullOrWhiteSpace(monster.ZoneName)) return true;
            PathMap pathMap = PathMapManager.Instance.GetPathMap(monster.ZoneName);
            if (pathMap == null) return true;
            if (!pathMap.TryCanReachPoint(monster.PosX, monster.PosY, target.PosX, target.PosY, out bool clear))
            {
                if (!string.IsNullOrEmpty(source))
                    Debug.LogError($"[MON-LOS] {monster.Name}#{monster.EntityId}->{target.Name} pathCoverage=False source={source} path=({monster.PosX:F1},{monster.PosY:F1})->({target.PosX:F1},{target.PosY:F1}) action=native-unblocked");
                return true;
            }
            if (!clear && !string.IsNullOrEmpty(source))
                Debug.LogError($"[MON-LOS] {monster.Name}#{monster.EntityId}->{target.Name} worldBlocked=True source={source} path=({monster.PosX:F1},{monster.PosY:F1})->({target.PosX:F1},{target.PosY:F1})");
            return clear;
        }

        private void ClearMonsterCombatContact(Monster monster, CombatPlayer target)
        {
            if (monster == null || target == null) return;
            if (monster.CombatContactTargetId != target.EntityId) return;
            monster.CombatContactTargetId = 0;
            monster.CombatContactUntil = 0f;
        }

        private bool IsNativeClientCombatContact(Monster monster, CombatPlayer target, float dist, float allowedRange)
        {
            if (!IsMonsterAttackPathClear(monster, target, null))
            {
                ClearMonsterCombatContact(monster, target);
                return false;
            }
            if (HasCombatContact(monster, target)) return true;
            if (monster == null || target == null || !target.HasActiveClientAttack) return false;
            if (target.ActiveClientAttackTargetId != 0 && target.ActiveClientAttackTargetId != monster.EntityId) return false;
            float contactRange = ResolveNativeClientContactRange(monster, target, allowedRange);
            return contactRange > 0f && dist <= contactRange + NATIVE_CONTACT_RANGE_EPSILON;
        }

        private bool HasNativeMonsterTargetAction(Monster monster, CombatPlayer target, float dist)
        {
            if (monster == null || target == null) return false;
            if (!monster.AggroTriggered || monster.TargetId != target.EntityId) return false;
            float targetRange = ResolveMonsterTargetSearchRange(monster, true);
            return targetRange <= 0f || dist <= targetRange + NATIVE_CONTACT_RANGE_EPSILON;
        }

        private bool HasMonsterWeaponRuntimeReach(Monster monster, CombatPlayer target, float dist, float allowedRange, bool nativeClientContact)
        {
            if (monster == null || target == null || allowedRange <= 0f) return false;
            if (dist <= allowedRange + NATIVE_CONTACT_RANGE_EPSILON) return true;
            if (nativeClientContact) return true;
            return HasCombatContact(monster, target);
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
            int attackIndex = monster != null ? Mathf.Clamp(monster.AttackAnimationIndex, 0, 2) : 0;
            totalFrames = monster?.AttackTotalFrames != null && monster.AttackTotalFrames.Length > attackIndex ? monster.AttackTotalFrames[attackIndex] : 30;
            hitFrame = monster?.AttackHitFrames != null && monster.AttackHitFrames.Length > attackIndex ? monster.AttackHitFrames[attackIndex] : 15;
            soundFrame = monster?.AttackSoundFrames != null && monster.AttackSoundFrames.Length > attackIndex ? monster.AttackSoundFrames[attackIndex] : 10;
            if (totalFrames <= 0) totalFrames = 30;
            if (hitFrame <= 0) hitFrame = 15;
            if (soundFrame <= 0) soundFrame = 10;
        }

        private float ResolveMonsterAttackFrameSeconds(Monster monster, int frame)
        {
            float authoredSpeed = GCDatabase.Instance.GetKnob("MonsterAttackSpeed", 100f);
            float attackSpeed = authoredSpeed;
            if (monster != null)
                attackSpeed *= Mathf.Max(0.01f, monster.AttackSpeed);
            int speedField = Mathf.Max(1, Mathf.RoundToInt(attackSpeed));
            int ticks = Mathf.Max(1, (frame * 100) / speedField);
            return ticks / 30f;
        }

        private float ResolveMonsterAttackCooldownSeconds(Monster monster)
        {
            float cooldown = monster != null ? monster.AttackCooldown : 1.75f;
            int ticks = Mathf.Max(1, Mathf.FloorToInt(Mathf.Max(0.01f, cooldown) * 30f));
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

        private void ConsumeMonsterAttackSearchRng(Monster monster, CombatPlayer target, string reason)
        {
            if (monster == null || target == null) return;
            if (monster.AttackSearchTargetId == target.EntityId) return;
            monster.AttackSearchTargetId = target.EntityId;
            monster.AttackSearchRaw = 0;
            monster.AttackSearchTieRaw = 0;
            string rngPos = _roomRng != null ? _roomRng.CallsSinceReseed.ToString() : "n/a";
            Debug.LogError($"[MON-AI-RNG] {monster.Name}#{monster.EntityId}->{target.Name} search=none tie=none reason={reason} rngPos={rngPos}");
        }

        private void ConsumeMonsterAttackSoundRng(Monster monster)
        {
            if (monster == null || !monster.AttackSoundPending) return;
            monster.AttackSoundPending = false;
            uint soundRaw = NativeRandomStreams.GenerateGlobalSound();
            monster.AttackSoundRaw = soundRaw;
            monster.AttackSoundGateRaw = soundRaw;
            monster.AttackSoundRepeatRaw = (soundRaw & 3u) == 0 ? soundRaw : 0;
            string roomRngPos = _roomRng != null ? _roomRng.CallsSinceReseed.ToString() : "n/a";
            Debug.LogError($"[MON-ATTACK] {monster.Name} sound nativeGlobalSoundRng=True raw=0x{soundRaw:X8} repeat={(monster.AttackSoundRepeatRaw != 0)} weaponSounds={monster.AttackWeaponSoundCount} attackSounds={monster.AttackRepeatSoundCount} globalSoundRngPos={NativeRandomStreams.GlobalSoundCalls} roomRngPos={roomRngPos}");
        }

        public uint ConsumeNativeOnApplyDamageEffectRng(MersenneTwister rng, string actor, uint targetId, string targetName, uint oldHPWire, uint newHPWire, uint targetMaxHPWire, uint damageWire, string source)
        {
            if (rng == null) return 0;
            if (damageWire == 0 || oldHPWire <= newHPWire || newHPWire == 0)
                return 0;

            uint gateRaw = rng.Generate();
            int gateRoll = (int)(gateRaw % 100u);
            uint appliedWire = oldHPWire - newHPWire;
            int severity = targetMaxHPWire > 0
                ? (int)Math.Min(int.MaxValue, ((long)appliedWire * 100L) / targetMaxHPWire)
                : 0;
            uint resistRaw = 0;
            if (gateRoll == 0 && severity >= 10)
                resistRaw = rng.Generate();
            Debug.LogError($"[RNG-COMBAT] Unit::onApplyDamage effect actor={actor} target={targetName}#{targetId} source={source} gateRaw=0x{gateRaw:X8} gateRoll={gateRoll} severity={severity} resistRaw=0x{resistRaw:X8} hp={oldHPWire}->{newHPWire}/{targetMaxHPWire} appliedWire={appliedWire} damageWire={damageWire} rngAfter={rng.CallsSinceReseed}");
            return gateRaw;
        }

        public void FlushPlayerAttackCommitsBeforeSync(uint playerEntityId)
        {
            if (playerEntityId == 0) return;
            ProcessMonsterAttacks(0f, playerEntityId, false, null, GetNativeCombatTime());
        }

        public void CancelMonsterPendingAttack(Monster monster, string reason)
        {
            if (monster == null) return;
            bool hadPending = monster.AttackPending || monster.AttackSoundPending || monster.AttackClientVisible;
            monster.AttackPending = false;
            monster.AttackSoundPending = false;
            monster.AttackClientVisible = false;
            monster.AttackNativeContactOnly = false;
            monster.AttackHitResolved = false;
            monster.AttackStartedTime = 0f;
            monster.AttackCommitTime = 0f;
            monster.AttackSoundTime = 0f;
            monster.AttackEndTime = 0f;
            monster.AttackUseRaw = 0;
            monster.AttackCommitTargetX = 0f;
            monster.AttackCommitTargetY = 0f;
            monster.CombatContactTargetId = 0;
            monster.CombatContactUntil = 0f;
            if (monster.State == MonsterState.Attacking)
                monster.State = MonsterState.Combat;
            if (hadPending)
                Debug.LogError($"[MON-ATTACK] canceled pending attack {monster.Name}#{monster.EntityId} reason={reason}");
            TraceMonsterState(monster, "attack-cancel", null, -1f, -1f, reason);
        }

        public void DelayMonsterAttackRetry(Monster monster, string reason)
        {
            if (monster == null) return;
            float cooldown = ResolveMonsterAttackCooldownSeconds(monster);
            monster.LastAttackTime = GetNativeCombatTime() + cooldown;
            Debug.LogError($"[MON-ATTACK] retry delayed {monster.Name}#{monster.EntityId} reason={reason} cooldown={cooldown:F2}");
        }

        private bool ShouldLogFarTargetAction(Monster monster, float now)
        {
            if (monster == null) return false;
            if (!_monsterFarTargetActionLogTime.TryGetValue(monster.EntityId, out float last) || now - last >= 1f)
            {
                _monsterFarTargetActionLogTime[monster.EntityId] = now;
                return true;
            }
            return false;
        }

        private void TraceFarTargetAction(Monster monster, CombatPlayer target, float dist, float allowedRange, string source)
        {
            if (!ShouldLogFarTargetAction(monster, GetNativeCombatTime())) return;
            float targetRange = ResolveMonsterTargetSearchRange(monster, true);
            Debug.LogError($"[MON-ATTACK-REACH] target-action-only {monster.Name}#{monster.EntityId}->{target?.Name ?? "player"} source={source ?? "unknown"} dist={dist:F1} meleeRange={allowedRange:F1} targetRange={targetRange:F1} action=target-only-no-damage");
            TraceMonsterState(monster, "target-action-only", target, dist, allowedRange, source ?? "reach");
        }

        public bool HasPendingClientVisibleMonsterAttack(uint playerEntityId)
        {
            if (playerEntityId == 0) return false;
            float nativeNow = GetNativeCombatTime();
            foreach (var monster in _activeMonsters.Values)
            {
                if (monster == null) continue;
                if (!monster.IsAlive) continue;
                bool targetsPlayer = monster.TargetId == playerEntityId;
                bool contactsPlayer = monster.CombatContactTargetId == playerEntityId && nativeNow <= monster.CombatContactUntil;
                if (!_players.TryGetValue(playerEntityId, out var player) || player == null || !player.IsAlive || player.PlayerState == null) continue;
                float dist = Distance2D(monster.PosX, monster.PosY, player.PosX, player.PosY);
                bool attackPathClear = IsMonsterAttackPathClear(monster, player, null);
                if (!attackPathClear) continue;
                float allowedRange = ResolveMonsterEffectiveAttackRange(monster);
                if (allowedRange <= 0f) continue;
                bool nativeClientContact = IsNativeClientCombatContact(monster, player, dist, allowedRange);
                bool weaponRuntimeReach = HasMonsterWeaponRuntimeReach(monster, player, dist, allowedRange, nativeClientContact);
                bool visibleCommitTolerance = monster.AttackClientVisible && AttackCommitTargetStillValid(monster, player);
                if (monster.AttackPending && !monster.AttackHitResolved && (targetsPlayer || contactsPlayer) && (weaponRuntimeReach || monster.UsePrimaryActiveSkillThisAttack || visibleCommitTolerance))
                    return true;
                if (contactsPlayer)
                    return true;
                if (!targetsPlayer || !monster.AggroTriggered) continue;
                if (weaponRuntimeReach)
                    return true;
            }
            return false;
        }

        public bool FlushPlayerHPRuntimeBeforeSync(uint playerEntityId, string source, out uint hpWire, out bool unsafeAttack, float nativeNowOverride = -1f)
        {
            hpWire = 0;
            unsafeAttack = false;
            if (playerEntityId == 0) return true;
            if (!_players.TryGetValue(playerEntityId, out var target) || target == null || target.PlayerState == null) return true;
            float nativeNow = nativeNowOverride >= 0f ? nativeNowOverride : GetNativeCombatTime();
            target.PlayerState.AdvanceClientSyncHP(nativeNow, source ?? "FlushPlayerHPRuntimeBeforeSync");
            hpWire = target.PlayerState.SynchHP;

            foreach (var monster in _activeMonsters.Values)
            {
                if (monster == null) continue;
                bool targetsPlayer = monster.TargetId == playerEntityId;
                bool contactsPlayer = monster.CombatContactTargetId == playerEntityId && nativeNow <= monster.CombatContactUntil;
                if (!monster.AttackPending || monster.AttackHitResolved || (!targetsPlayer && !contactsPlayer)) continue;
                if (target.PlayerState.IsZoneSpawnDamageImmune)
                {
                    CancelMonsterPendingAttack(monster, $"{source}-target_zone_spawn_invulnerability");
                    continue;
                }
                if (target.PlayerState.CurrentHPWire == 0 && target.PlayerState.SynchHP == 0)
                {
                    target.IsAlive = false;
                    CancelMonsterPendingAttack(monster, $"{source}-target_dead");
                    continue;
                }
                if (monster.DeathPendingClientConfirmation
                    && !(monster.AttackNativeContactOnly && monster.AttackPending && !monster.AttackHitResolved))
                {
                    CancelMonsterPendingAttack(monster, $"{source}-monster_death_pending_client_confirmation");
                    continue;
                }

                float allowedRange = ResolveMonsterEffectiveAttackRange(monster);
                if (allowedRange <= 0f)
                {
                    unsafeAttack = true;
                    continue;
                }
                float dist = Distance2D(monster.PosX, monster.PosY, target.PosX, target.PosY);
                bool attackPathClear = IsMonsterAttackPathClear(monster, target, $"{source}-target-clear");
                if (!attackPathClear)
                {
                    CancelMonsterPendingAttack(monster, $"{source}-world_blocked");
                    if (monster.IsAlive)
                        monster.State = MonsterState.Chase;
                    Debug.LogError($"[MON-DAMAGE-SYNC] {monster.Name}#{monster.EntityId}->{target.Name} cancel worldBlocked source={source} dist={dist:F1} range={allowedRange:F1}");
                    OnMonsterAttackResolved?.Invoke(monster, target, false, target.PlayerState.CurrentHPWire);
                    hpWire = target.PlayerState.SynchHP;
                    continue;
                }
                bool nativeClientContact = IsNativeClientCombatContact(monster, target, dist, allowedRange);
                bool weaponRuntimeReach = HasMonsterWeaponRuntimeReach(monster, target, dist, allowedRange, nativeClientContact);
                bool visibleCommitTolerance = monster.AttackClientVisible && AttackCommitTargetStillValid(monster, target);
                if (!weaponRuntimeReach && !monster.UsePrimaryActiveSkillThisAttack && !visibleCommitTolerance)
                {
                    CancelMonsterPendingAttack(monster, $"{source}-weapon_range_pending dist={dist:F1} range={allowedRange:F1} targetRange={ResolveMonsterTargetSearchRange(monster, true):F1}");
                    if (monster.IsAlive)
                        monster.State = MonsterState.Chase;
                    continue;
                }

                if (!monster.AttackClientVisible && !monster.AttackNativeContactOnly && !nativeClientContact)
                {
                    if (monster.AttackCommitTime > 0f || nativeNow - monster.AttackStartedTime > 0.25f)
                        CancelMonsterPendingAttack(monster, $"{source}-hidden_unseen_attack");
                    hpWire = target.PlayerState.SynchHP;
                    continue;
                }

                if (!monster.AttackClientVisible && monster.AttackNativeContactOnly)
                    Debug.LogError($"[PLAYER-HP-NATIVE-CONTACT] {source} resolving hidden native contact {monster.Name}#{monster.EntityId}->{target.Name} dist={dist:F1} range={allowedRange:F1} commit={monster.AttackCommitTime:F2}");
                else if (!monster.AttackClientVisible)
                {
                    CancelMonsterPendingAttack(monster, $"{source}-unsent_hidden_native_attack");
                    hpWire = target.PlayerState.SynchHP;
                    continue;
                }

                if (_roomRng == null)
                {
                    unsafeAttack = true;
                    continue;
                }
                if (monster.AttackCommitTime <= 0f)
                    ArmMonsterRuntimeAttack(monster, target, "HP-SYNC-ARM", nativeNow);
                if (monster.AttackSoundPending && nativeNow >= monster.AttackSoundTime)
                    ConsumeMonsterAttackSoundRng(monster);
                if (nativeNow < monster.AttackCommitTime)
                {
                    hpWire = target.PlayerState.SynchHP;
                    unsafeAttack = true;
                    Debug.LogError($"[PLAYER-HP-NATIVE-PENDING] {source} keeping current HP before native hit frame {monster.Name}#{monster.EntityId}->{target.Name} now={nativeNow:F3} commit={monster.AttackCommitTime:F3} hp={hpWire / 256f:F2}");
                    continue;
                }
                if (monster.AttackSoundPending)
                    ConsumeMonsterAttackSoundRng(monster);
                monster.AttackHitResolved = true;
                if (monster.AttackEndTime < nativeNow)
                    monster.AttackEndTime = nativeNow;
                float commitDelta = Distance2D(monster.AttackCommitTargetX, monster.AttackCommitTargetY, target.PosX, target.PosY);
                bool attackClientVisible = monster.AttackClientVisible;
                bool commitTargetStillValid = attackPathClear
                    && (weaponRuntimeReach
                    || monster.UsePrimaryActiveSkillThisAttack
                    || (attackClientVisible && AttackCommitTargetStillValid(monster, target)));
                if (!commitTargetStillValid)
                {
                    monster.AttackPending = false;
                    monster.AttackSoundPending = false;
                    monster.CombatContactTargetId = 0;
                    monster.CombatContactUntil = 0f;
                    monster.AttackClientVisible = false;
                    monster.AttackNativeContactOnly = false;
                    monster.AttackHitResolved = false;
                    monster.AttackStartedTime = 0f;
                    monster.AttackEndTime = 0f;
                    monster.AttackCommitTime = 0f;
                    monster.AttackSoundTime = 0f;
                    if (monster.IsAlive)
                        monster.State = MonsterState.Chase;
                    Debug.LogError($"[MON-DAMAGE-SYNC] {monster.Name}#{monster.EntityId}->{target.Name} cancel movedOut source={source} dist={dist:F1} commitDelta={commitDelta:F1} monsterRange={allowedRange:F1} nativeContact={nativeClientContact} clientVisible={attackClientVisible}");
                    OnMonsterAttackResolved?.Invoke(monster, target, false, target.PlayerState.CurrentHPWire);
                    hpWire = target.PlayerState.SynchHP;
                    continue;
                }

                ResolveMonsterAttackDamage(monster, target, dist, "MON-DAMAGE-SYNC", source);
                monster.AttackPending = false;
                monster.AttackClientVisible = false;
                monster.AttackNativeContactOnly = false;
                monster.AttackSoundPending = false;
                monster.AttackHitResolved = false;
                monster.AttackStartedTime = 0f;
                monster.AttackEndTime = 0f;
                monster.AttackCommitTime = 0f;
                monster.AttackSoundTime = 0f;
                if (monster.IsAlive)
                    monster.State = MonsterState.Combat;
                hpWire = target.PlayerState.SynchHP;
            }

            return !unsafeAttack;
        }

        private bool AttackCommitTargetStillValid(Monster monster, CombatPlayer target)
        {
            if (monster == null || target == null) return false;
            float tolerance = monster.ClientSyncTolerance > 0f ? monster.ClientSyncTolerance : 10f;
            return Distance2D(monster.AttackCommitTargetX, monster.AttackCommitTargetY, target.PosX, target.PosY) <= tolerance;
        }

        private bool HasCombatContact(Monster monster, CombatPlayer target)
        {
            return monster != null && target != null && monster.CombatContactTargetId == target.EntityId && GetNativeCombatTime() <= monster.CombatContactUntil;
        }

        private float ResolveMonsterDamageTable(byte level)
        {
            float lv = Mathf.Clamp(level, 1, 110);
            float fallback = lv <= 25f
                ? 12.7f + (lv - 1f) * (86.625f - 12.7f) / 24f
                : 86.625f + (lv - 25f) * (372.12f - 86.625f) / 85f;
            return GCDatabase.Instance.GetCurveValue("MonsterDamage", level, fallback);
        }

        private int ResolveMonsterAttackRating(Monster monster)
        {
            return DamageComputer.ResolveNativeMonsterAttackRating(monster);
        }

        private static uint ApplyDamageTakenModWire(uint damageWire, float damageTakenMod)
        {
            if (damageWire == 0) return 0;
            if (damageTakenMod < 1f) return 0;
            double scaled = damageWire * (double)damageTakenMod / 100.0;
            if (scaled >= uint.MaxValue) return uint.MaxValue;
            return (uint)scaled;
        }

        private int ResolveAvatarDefenseRating(PlayerState state, Monster attacker)
        {
            if (state == null) return 0;
            float defensePerStrength = GCDatabase.Instance.GetKnob("DefenseRatingPerStrength", 14f);
            // Bug 1 fix 2026-05-27: PlayerState.Strength only carries (BASE + allocated + passive)
            // — it does NOT include gear's STRENGTH contribution. The client's character sheet
            // displays effective STR including equipment, so client's DR formula uses the gear-
            // adjusted value too. Diagnostic 2026-05-27 showed server STR=5 vs sheet STR=6 (Ranger
            // passive=-5, gear contributed +1). Add gear STR here so server's DR computation
            // matches what client computes. Same bug likely affects AR (attack rating per AGI)
            // and any other stat-based formula — flagged for follow-up but not addressed in this
            // narrow fix.
            int gearStrength = GetEquipmentStat(state, "STRENGTH");
            int effectiveStrength = state.Strength + gearStrength;
            int strBase = Mathf.RoundToInt(effectiveStrength * defensePerStrength);
            int rating = strBase + state.ArmorDefenseRating;
            int modifier = GetEquipmentStat(state, "DEFENSE_RATING_MOD");
            string weaponClass = ResolveMonsterWeaponClass(attacker);
            int slotRating, slotModifier;
            if (IsRangedWeaponClass(weaponClass))
            {
                slotRating = GetEquipmentStat(state, "RANGE_DEFENSE_RATING");
                slotModifier = GetEquipmentStat(state, "RANGE_DEFENSE_RATING_MOD");
            }
            else
            {
                slotRating = GetEquipmentStat(state, "MELEE_DEFENSE_RATING");
                slotModifier = GetEquipmentStat(state, "MELEE_DEFENSE_RATING_MOD");
            }
            rating += slotRating;
            modifier += slotModifier;
            int finalDR = Mathf.Max(0, (int)(((long)Mathf.Max(0, rating) * (modifier + 100)) / 100));

            // Stage 3 diagnostic — dump server's DR breakdown so it can be compared to the
            // value the client shows on the character sheet. Fires once per monster swing.
            int generic = GetEquipmentStat(state, "DEFENSE_RATING");
            Debug.LogError(
                $"[PLAYER-DR-DETAIL] attacker={attacker?.Name ?? "?"}#{attacker?.EntityId ?? 0} " +
                $"weaponClass={weaponClass} " +
                $"STR={state.Strength}+gear{gearStrength}={effectiveStrength}×{defensePerStrength:F1}={strBase} " +
                $"armorDR={state.ArmorDefenseRating} " +
                $"slot[{(IsRangedWeaponClass(weaponClass) ? "RANGE" : "MELEE")}]={slotRating} " +
                $"DEFENSE_RATING(generic_unused)={generic} " +
                $"DEFENSE_RATING_MOD={GetEquipmentStat(state, "DEFENSE_RATING_MOD")} " +
                $"slotMod={slotModifier} " +
                $"→ rating={rating} mod={modifier} finalDR={finalDR}");
            return finalDR;
        }

        private static int GetEquipmentStat(PlayerState state, string key)
        {
            if (state?.EquipmentStats == null || string.IsNullOrEmpty(key)) return 0;
            return state.EquipmentStats.TryGetValue(key, out int value) ? value : 0;
        }

        private static string ResolveMonsterWeaponClass(Monster monster)
        {
            if (monster?.Manipulators != null &&
                monster.Manipulators.TryGetValue("primaryweapon", out var weapon) &&
                weapon?.properties != null &&
                weapon.properties.TryGetValue("WeaponClass", out var weaponClass) &&
                !string.IsNullOrWhiteSpace(weaponClass))
                return weaponClass;

            return monster?.BehaviourType?.IndexOf("ranged", StringComparison.OrdinalIgnoreCase) >= 0 ? "1HRANGED" : "HTH";
        }

        private static bool IsRangedWeaponClass(string weaponClass)
        {
            if (string.IsNullOrEmpty(weaponClass)) return false;
            return weaponClass.Equals("1HRANGED", StringComparison.OrdinalIgnoreCase)
                || weaponClass.Equals("2HRANGED", StringComparison.OrdinalIgnoreCase)
                || weaponClass.Equals("2HCANNON", StringComparison.OrdinalIgnoreCase);
        }

        private int ResolveAvatarBlockChance(PlayerState state)
        {
            if (state == null || state.EquipmentStats == null) return 0;
            return state.EquipmentStats.TryGetValue("BLOCK", out int block) ? Mathf.Clamp(block, 0, 100) : 0;
        }

        private int ResolveHitThreshold(int attackRating, int defenseRating, int attackerLevel, int defenderLevel)
        {
            return DamageComputer.ResolveNativeHitThreshold(attackRating, defenseRating, attackerLevel, defenderLevel);
        }

        private uint ResolveMonsterDamageWire(Monster monster, uint damageRaw, out int minDamage, out int maxDamage, out int averageDamage)
        {
            float baseDamage = ResolveMonsterDamageTable(monster.Level);
            float damageMod = ResolveMonsterDamageModifier(monster);
            float weaponScale = monster.WeaponDamage > 0f ? monster.WeaponDamage : 1f;
            float damage = baseDamage * damageMod * weaponScale;
            int normalized = Mathf.RoundToInt(Mathf.Max(1f, damage) * 256f);
            if (normalized < 0x100) normalized = 0x100;

            int volatility = DamageComputer.FromFloat(Mathf.Clamp(monster.DamageVolatility, 0f, 0.95f));
            int spread = DamageComputer.FixedMul(normalized, volatility);
            minDamage = DamageComputer.RoundFixed32(normalized - spread);
            maxDamage = DamageComputer.RoundFixed32(normalized + spread);
            if (minDamage < 0x100) minDamage = 0x100;
            if (maxDamage < 0x100) maxDamage = 0x100;
            if (maxDamage < minDamage) maxDamage = minDamage;

            averageDamage = normalized;
            return (uint)DamageComputer.RollDamageRange(minDamage, maxDamage, damageRaw);
        }

        private NativeWeaponDamageInput CreateMonsterNativeWeaponDamageInput(Monster monster, CombatPlayer target, MersenneTwister rng, string source)
        {
            int attackerLevel = monster != null ? Mathf.Clamp(monster.Level, 0, 110) : 1;
            int defenderLevel = target?.PlayerState != null ? Mathf.Clamp(target.PlayerState.Level, 0, 110) : attackerLevel;
            float baseDamage = monster != null ? ResolveMonsterDamageTable(monster.Level) : 1f;
            float damageMod = monster != null ? ResolveMonsterDamageModifier(monster) : 1f;
            float weaponScale = monster != null && monster.WeaponDamage > 0f ? monster.WeaponDamage : 1f;

            // Monster weapon volatility: hardcoded 0.5 to match the client. The client does NOT
            // propagate Manipulators-block overrides like
            // `Whisker_Broodling_Weapons.UnarmedWeapon.DamageVolatility = 0.4` — it uses the
            // parent class default (`creatures.base.weapons.melee` and `creatures.base.weapons.ranged`,
            // both at 0.5). Verified 2026-05-25 via x32dbg @ Weapon::computeDamageRange on a live
            // Whisker Ratling: [EDX+0xF0] = 0x80 (= 0.5) despite the authored 0.4 override. Without
            // this match, each Rat hit drifts ~1 HP from client, surfacing as visual HP desync on
            // movement-packet suffix HP even when no Validate kick fires.
            float volatility = 0.5f;

            // Use FloorToInt (not RoundToInt). Verified via x32dbg @ Weapon::computeDamageRange:
            // the client passes the curve value truncated, not rounded — so for level-2 Warg Pup
            // with curve(2) ≈ 15.78, the client uses 15, not the rounded 16. Server was
            // overstating damageLevel by 1 unit each hit, causing a tiny per-hit divergence
            // that the avatar Validate could catch on movement packets. Confirmed 2026-05-25.
            int damageLevel = Mathf.Max(1, Mathf.FloorToInt(baseDamage));
            int damageBonus = MonsterDamageBonusOverride >= 0 ? MonsterDamageBonusOverride : 0;
            int damageModInt = Mathf.Max(0, Mathf.RoundToInt(damageMod * 100f));
            int weaponDamageF32 = DamageComputer.FromFloat(weaponScale);
            int volatilityF32 = DamageComputer.FromFloat(volatility);

            // Diagnostic gated on the override — only emit when the admin is actively tuning. Default
            // (override = -1) is silent: no recompute, no log line. Without this gate the per-attack
            // log + recompute crashed Unity from log-buffer pressure when multiple mobs were engaged.
            if (MonsterDamageBonusOverride >= 0)
            {
                DamageComputer.ComputeNativeWeaponDamageRange(damageLevel, damageBonus, damageModInt, weaponDamageF32, volatilityF32, out int minF32, out int maxF32);
                Debug.LogError($"[DMG-INPUT] {(monster?.Name ?? "<null>")}#{monster?.EntityId ?? 0u} src={source} lvl={monster?.Level ?? 0} damageLevel={damageLevel} damageBonus={damageBonus} damageMod={damageModInt} weaponDamageF32=0x{weaponDamageF32:X4} volF32=0x{volatilityF32:X4} range=[{minF32 / 256f:F2},{maxF32 / 256f:F2}] HP");
            }

            return new NativeWeaponDamageInput
            {
                Rng = rng,
                Source = source,
                AttackerLevel = attackerLevel,
                DefenderLevel = defenderLevel,
                AttackRating = ResolveMonsterAttackRating(monster),
                DefenseRating = ResolveAvatarDefenseRating(target?.PlayerState, monster),
                BlockChance = ResolveAvatarBlockChance(target?.PlayerState),
                DamageLevel = damageLevel,
                DamageBonus = damageBonus,
                DamageMod = damageModInt,
                WeaponDamageF32 = weaponDamageF32,
                WeaponVolatilityF32 = volatilityF32,
                CritThreshold = 0,
                CritDamagePercent = 0
            };
        }

        private void ResolveMonsterAttackDamage(Monster monster, CombatPlayer target, float dist, string marker, string source)
        {
            float nativeNow = GetNativeCombatTime();
            target.PlayerState.AdvanceClientSyncHP(nativeNow, $"{marker}-pre-damage");
            NativeWeaponDamageInput damageInput = CreateMonsterNativeWeaponDamageInput(monster, target, _roomRng, source);
            NativeWeaponDamageResult damageResult = DamageComputer.ResolveNativeWeaponDamage(damageInput);
            int attackRating = damageResult.AttackRating;
            int defenseRating = damageResult.DefenseRating;
            int defenderLevel = damageResult.DefenderLevel;
            int attackerLevel = damageResult.AttackerLevel;
            int hitThreshold = damageResult.HitThreshold;
            float hitChance = hitThreshold / 256f;
            uint hitRaw = damageResult.HitRaw;
            uint blockRaw = damageResult.BlockRaw;
            int hitRoll = damageResult.HitRoll;
            int blockRoll = damageResult.BlockRoll;
            int blockChance = damageResult.BlockChance;
            bool hit = damageResult.IsHit;
            bool blocked = damageResult.IsBlocked;
            if (hit && !blocked)
            {
                uint damageRaw = damageResult.DamageRaw;
                uint damageWire = damageResult.DamageWire;
                int minDamage = damageResult.MinDamageF32;
                int maxDamage = damageResult.MaxDamageF32;
                int averageDamage = (minDamage + maxDamage) / 2;
                if (target.PlayerState.HasAnyDamageImmunity)
                {
                    Debug.LogError($"[{marker}] {monster.Name}#{monster.EntityId}->{target.Name} immune source={source} dmg={damageWire / 256f:F2} hp={target.PlayerState.CurrentHPWire / 256f:F2}/{target.PlayerState.MaxHPWire / 256f:F2} range=[{minDamage / 256f:F2},{maxDamage / 256f:F2}] avg={averageDamage / 256f:F2} ar={attackRating} dr={defenseRating} levels={attackerLevel}->{defenderLevel} chance={hitChance:F1} rngPos={_roomRng.CallsSinceReseed} anim={monster.AttackAnimationIndex} use=0x{monster.AttackUseRaw:X8} sound=0x{monster.AttackSoundRaw:X8} soundGate=0x{monster.AttackSoundGateRaw:X8} soundRepeat=0x{monster.AttackSoundRepeatRaw:X8} hit=0x{hitRaw:X8}/{hitRoll} block=0x{blockRaw:X8}/{blockRoll}/{blockChance} dmgRaw=0x{damageRaw:X8} dist={dist:F1}");
                    Debug.LogError($"[PLAYER-DAMAGE] source=monster attacker={monster.Name}#{monster.EntityId} target={target.Name}#{target.EntityId} result=IMMUNE damageWire={damageWire} hp={target.PlayerState.CurrentHPWire}->{target.PlayerState.CurrentHPWire}/{target.PlayerState.MaxHPWire} marker={marker} source={source} rngSeed=0x{_roomSeed:X8} rngPos={_roomRng.CallsSinceReseed}");
                    Debug.LogError($"[COMBAT-EVENT] actor=monster actorId={monster.EntityId} target=player targetId={target.EntityId} result=IMMUNE damageWire={damageWire} hp={target.PlayerState.CurrentHPWire}->{target.PlayerState.CurrentHPWire} hitRaw=0x{hitRaw:X8} hitRoll={hitRoll} threshold={hitThreshold} blockRaw=0x{blockRaw:X8} blockRoll={blockRoll} blockChance={blockChance} damageRaw=0x{damageRaw:X8} resist=1 rngAfter={_roomRng.CallsSinceReseed} marker={marker} source={source}");
                    target.PlayerState.RecordSimMonsterAttack(monster.EntityId, "IMMUNE", damageWire, nativeNow, _roomRng.CallsSinceReseed);
                    OnMonsterAttackResolved?.Invoke(monster, target, false, target.PlayerState.CurrentHPWire);
                }
                else
                {
                    uint currentHPWire = target.PlayerState.CurrentHPWire;
                    // Use TakeRuntimeDamage (not TakeDamage) so SynchHP does NOT immediately advance to
                    // the damaged value. The server applies damage instantly but the CLIENT only applies
                    // it ~200ms later when the mob's attack animation reaches its hit frame and the
                    // client's MeleeWeapon::doHit @ 0x005921C0 chain runs locally. During that window
                    // the suffix HP must equal client's still-pre-damage [+0x2F0] for Validate to pass.
                    // HasPendingClientVisibleMonsterAttack (HpSyncService gating in TryResolvePlayerSynchronizedHP)
                    // keeps SynchHP frozen until the attack resolves server-side, which approximately
                    // tracks the client's animation hit timing — at which point AdvanceClientSyncHP
                    // catches SynchHP up to CurrentHPWire on the next outbound packet.
                    target.PlayerState.TakeRuntimeDamage(damageWire);
                    uint newHPWire = target.PlayerState.CurrentHPWire;
                    target.IsAlive = newHPWire > 0;
                    uint effectRaw = 0;
                    Debug.LogError($"[{marker}] {monster.Name}#{monster.EntityId}->{target.Name} HIT source={source} dmg={damageWire / 256f:F2} hp={currentHPWire / 256f:F2}->{newHPWire / 256f:F2}/{target.PlayerState.MaxHPWire / 256f:F2} range=[{minDamage / 256f:F2},{maxDamage / 256f:F2}] avg={averageDamage / 256f:F2} ar={attackRating} dr={defenseRating} levels={attackerLevel}->{defenderLevel} chance={hitChance:F1} rngPos={_roomRng.CallsSinceReseed} anim={monster.AttackAnimationIndex} use=0x{monster.AttackUseRaw:X8} sound=0x{monster.AttackSoundRaw:X8} soundGate=0x{monster.AttackSoundGateRaw:X8} soundRepeat=0x{monster.AttackSoundRepeatRaw:X8} hit=0x{hitRaw:X8}/{hitRoll} block=0x{blockRaw:X8}/{blockRoll}/{blockChance} dmgRaw=0x{damageRaw:X8} dist={dist:F1}");
                    Debug.LogError($"[PLAYER-DAMAGE] source=monster attacker={monster.Name}#{monster.EntityId} target={target.Name}#{target.EntityId} result=HIT damageWire={damageWire} hp={currentHPWire}->{newHPWire}/{target.PlayerState.MaxHPWire} marker={marker} source={source} hitRaw=0x{hitRaw:X8} blockRaw=0x{blockRaw:X8} damageRaw=0x{damageRaw:X8} effectRaw=0x{effectRaw:X8} rngSeed=0x{_roomSeed:X8} rngPos={_roomRng.CallsSinceReseed}");
                    Debug.LogError($"[COMBAT-EVENT] actor=monster actorId={monster.EntityId} target=player targetId={target.EntityId} result=HIT damageWire={damageWire} hp={currentHPWire}->{newHPWire} hitRaw=0x{hitRaw:X8} hitRoll={hitRoll} threshold={hitThreshold} blockRaw=0x{blockRaw:X8} blockRoll={blockRoll} blockChance={blockChance} damageRaw=0x{damageRaw:X8} effectRaw=0x{effectRaw:X8} resist=0 rngAfter={_roomRng.CallsSinceReseed} marker={marker} source={source}");
                    target.PlayerState.RecordSimMonsterAttack(monster.EntityId, "HIT", damageWire, nativeNow, _roomRng.CallsSinceReseed);
                    OnMonsterAttackResolved?.Invoke(monster, target, true, newHPWire);
                }
            }
            else if (blocked)
            {
                Debug.LogError($"[{marker}] {monster.Name}#{monster.EntityId}->{target.Name} block source={source} ar={attackRating} dr={defenseRating} levels={attackerLevel}->{defenderLevel} chance={hitChance:F1} rngPos={_roomRng.CallsSinceReseed} anim={monster.AttackAnimationIndex} use=0x{monster.AttackUseRaw:X8} sound=0x{monster.AttackSoundRaw:X8} soundGate=0x{monster.AttackSoundGateRaw:X8} soundRepeat=0x{monster.AttackSoundRepeatRaw:X8} hit=0x{hitRaw:X8}/{hitRoll} block=0x{blockRaw:X8}/{blockRoll}/{blockChance} dist={dist:F1}");
                Debug.LogError($"[PLAYER-DAMAGE] source=monster attacker={monster.Name}#{monster.EntityId} target={target.Name}#{target.EntityId} result=BLOCK damageWire=0 hp={target.PlayerState.CurrentHPWire}->{target.PlayerState.CurrentHPWire}/{target.PlayerState.MaxHPWire} marker={marker} source={source} hitRaw=0x{hitRaw:X8} blockRaw=0x{blockRaw:X8} rngSeed=0x{_roomSeed:X8} rngPos={_roomRng.CallsSinceReseed}");
                Debug.LogError($"[COMBAT-EVENT] actor=monster actorId={monster.EntityId} target=player targetId={target.EntityId} result=BLOCK damageWire=0 hp={target.PlayerState.CurrentHPWire}->{target.PlayerState.CurrentHPWire} hitRaw=0x{hitRaw:X8} hitRoll={hitRoll} threshold={hitThreshold} blockRaw=0x{blockRaw:X8} blockRoll={blockRoll} blockChance={blockChance} resist=0 rngAfter={_roomRng.CallsSinceReseed} marker={marker} source={source}");
                target.PlayerState.RecordSimMonsterAttack(monster.EntityId, "BLOCK", 0, nativeNow, _roomRng.CallsSinceReseed);
                OnMonsterAttackResolved?.Invoke(monster, target, false, target.PlayerState.CurrentHPWire);
            }
            else
            {
                Debug.LogError($"[{marker}] {monster.Name}#{monster.EntityId}->{target.Name} miss source={source} ar={attackRating} dr={defenseRating} levels={attackerLevel}->{defenderLevel} chance={hitChance:F1} rngPos={_roomRng.CallsSinceReseed} anim={monster.AttackAnimationIndex} use=0x{monster.AttackUseRaw:X8} sound=0x{monster.AttackSoundRaw:X8} soundGate=0x{monster.AttackSoundGateRaw:X8} soundRepeat=0x{monster.AttackSoundRepeatRaw:X8} hit=0x{hitRaw:X8}/{hitRoll} threshold={hitThreshold} block=0x{blockRaw:X8}/{blockRoll}/{blockChance} dist={dist:F1}");
                Debug.LogError($"[PLAYER-DAMAGE] source=monster attacker={monster.Name}#{monster.EntityId} target={target.Name}#{target.EntityId} result=MISS damageWire=0 hp={target.PlayerState.CurrentHPWire}->{target.PlayerState.CurrentHPWire}/{target.PlayerState.MaxHPWire} marker={marker} source={source} hitRaw=0x{hitRaw:X8} blockRaw=0x{blockRaw:X8} rngSeed=0x{_roomSeed:X8} rngPos={_roomRng.CallsSinceReseed}");
                Debug.LogError($"[COMBAT-EVENT] actor=monster actorId={monster.EntityId} target=player targetId={target.EntityId} result=MISS damageWire=0 hp={target.PlayerState.CurrentHPWire}->{target.PlayerState.CurrentHPWire} hitRaw=0x{hitRaw:X8} hitRoll={hitRoll} threshold={hitThreshold} blockRaw=0x{blockRaw:X8} blockRoll={blockRoll} blockChance={blockChance} resist=0 rngAfter={_roomRng.CallsSinceReseed} marker={marker} source={source}");
                target.PlayerState.RecordSimMonsterAttack(monster.EntityId, "MISS", 0, nativeNow, _roomRng.CallsSinceReseed);
                OnMonsterAttackResolved?.Invoke(monster, target, false, target.PlayerState.CurrentHPWire);
            }
        }

        private float ResolveMonsterDamageModifier(Monster monster)
        {
            if (monster == null) return 1f;
            float damageMod = monster.DamageMod > 0f ? monster.DamageMod : 1f;
            return damageMod;
        }

        private IEnumerable<Monster> SelectMonsters(Monster onlyMonster)
        {
            if (onlyMonster != null)
            {
                yield return onlyMonster;
                yield break;
            }

            foreach (var monster in _activeMonsters.Values)
                yield return monster;
        }

        private uint PeekMonsterHPWireForTrace(Monster monster)
        {
            if (monster == null) return 0;
            if (_monsterHPAuthority.TryGetValue(monster.EntityId, out var authority) && authority.RuntimeInitialized)
                return authority.RuntimeHPWire;
            if (_monsterRuntimeHPWire.TryGetValue(monster.EntityId, out uint runtimeHP))
                return runtimeHP;
            return monster.CurrentHPWire;
        }

        private void TraceMonsterState(Monster monster, string phase, CombatPlayer target = null, float dist = -1f, float range = -1f, string reason = null)
        {
            if (!VerboseMonsterStateTrace) return;
            if (monster == null) return;

            uint hp = PeekMonsterHPWireForTrace(monster);
            string targetText = target != null ? $"{target.Name}#{target.EntityId}" : (monster.TargetId != 0 ? monster.TargetId.ToString() : "none");
            string signature = $"{monster.State}|{monster.IsAlive}|{monster.AggroTriggered}|{monster.TargetId}|{monster.AlertSourceEntityId}|{monster.AttackPending}|{monster.AttackClientVisible}|{monster.AttackNativeContactOnly}|{monster.AttackHitResolved}|{monster.UsePrimaryActiveSkillThisAttack}|{monster.DeathPendingClientConfirmation}|{monster.NativeDeathState}|{hp}|{monster.CurrentManaWire}|{Mathf.RoundToInt(monster.PosX * 10f)}|{Mathf.RoundToInt(monster.PosY * 10f)}|{monster.CombatContactTargetId}|{Mathf.RoundToInt(monster.AttackCommitTime * 1000f)}|{Mathf.RoundToInt(monster.AttackEndTime * 1000f)}|{monster.PrimaryActiveSkillCooldownRemainingTicks}";
            if (_monsterStateTraceSignatures.TryGetValue(monster.EntityId, out var previous) && previous == signature)
                return;

            _monsterStateTraceSignatures[monster.EntityId] = signature;
            string distText = dist >= 0f ? $"{dist:F1}" : "n/a";
            string rangeText = range >= 0f ? $"{range:F1}" : "n/a";
            Debug.LogError($"[MON-STATE] phase={phase ?? "unknown"} monster={monster.Name}#{monster.EntityId} behavior={monster.BehaviorId} unit={monster.UnitId} gc='{monster.GCType}' spawnGc='{monster.SpawnGCType}' zone='{monster.ZoneName}' state={monster.State} alive={monster.IsAlive} aggro={monster.AggroTriggered} target={targetText} alertSource={monster.AlertSourceEntityId} deathPending={monster.DeathPendingClientConfirmation} nativeDeath={monster.NativeDeathState} hp={hp}/{monster.MaxHPWire} mana={monster.CurrentManaWire}/{monster.MaxManaWire} pos=({monster.PosX:F1},{monster.PosY:F1},{monster.PosZ:F1}) dist={distText} range={rangeText} pending={monster.AttackPending} clientVisible={monster.AttackClientVisible} nativeContactOnly={monster.AttackNativeContactOnly} hitResolved={monster.AttackHitResolved} session={monster.AttackSessionId} commit={monster.AttackCommitTime:F3} end={monster.AttackEndTime:F3} contactTarget={monster.CombatContactTargetId} contactUntil={monster.CombatContactUntil:F3} skill={monster.PrimaryActiveSkillPath ?? "none"} useSkill={monster.UsePrimaryActiveSkillThisAttack} skillCd={monster.PrimaryActiveSkillCooldownRemainingTicks}/{monster.PrimaryActiveSkillCooldownTicks} rngSeed=0x{_roomSeed:X8} rngPos={_roomRng?.CallsSinceReseed ?? 0} reason={reason ?? "state-change"}");
        }

        private void TraceCombatTick(string phase, float deltaTime, bool allowNewAttacks, Monster onlyMonster = null)
        {
            if (!VerboseCombatTick) return;

            float now = GetNativeCombatTime();
            if (now - _lastCombatTraceSummaryTime < 1f)
                return;

            _lastCombatTraceSummaryTime = now;
            int alive = 0;
            int aggro = 0;
            int pending = 0;
            int attacking = 0;
            int deathPending = 0;
            foreach (var monster in SelectMonsters(onlyMonster))
            {
                if (monster == null) continue;
                if (monster.IsAlive) alive++;
                if (monster.AggroTriggered) aggro++;
                if (monster.AttackPending) pending++;
                if (monster.State == MonsterState.Attacking) attacking++;
                if (monster.DeathPendingClientConfirmation || monster.NativeDeathLifecycleActive) deathPending++;
            }

            string scope = onlyMonster != null ? onlyMonster.EntityId.ToString() : "all";
            Debug.LogError($"[COMBAT-TICK] phase={phase ?? "update"} dt={deltaTime:F3} scope={scope} players={_players.Count} monsters={_activeMonsters.Count} alive={alive} aggro={aggro} pending={pending} attacking={attacking} deathPending={deathPending} allowNew={allowNewAttacks} rngReady={_roomRngInitialized} roomSeed=0x{_roomSeed:X8} rngPos={_roomRng?.CallsSinceReseed ?? 0}");
        }

        // Stage 2 diagnostic: throttle PROXIMITY-* logs to once per second per (mob, reason)
        private static readonly Dictionary<string, float> _proxiLogLastEmitTime = new Dictionary<string, float>();
        private static float _proxiTickLastEmitTime = -1f;
        private const float PROXI_LOG_INTERVAL_S = 1.0f;
        private const float PROXI_TICK_LOG_INTERVAL_S = 1.0f;
        private const float PROXI_NEAR_MISS_FACTOR = 3.0f;  // log mobs whose closest player is within 3× aggro range

        private static bool ShouldEmitProxiLog(string key, float now)
        {
            if (!_proxiLogLastEmitTime.TryGetValue(key, out float last) || (now - last) >= PROXI_LOG_INTERVAL_S)
            {
                _proxiLogLastEmitTime[key] = now;
                return true;
            }
            return false;
        }

        private void ProcessProximityAggro(uint playerEntityId = 0, Monster onlyMonster = null)
        {
            // Stage 2 diagnostic: throttled tick-entry log so we can confirm Update is invoking us
            float diagNow = Time.time;

            // [PLAYER-POS] diagnostic — log server's tracked player position every second per player.
            // Lets us correlate user's actual movement to server's view. If server's view stops
            // updating while the user is walking, that confirms position-tracking lag as a separate
            // root cause from the alerted-range issue.
            if (ShouldEmitProxiLog("__playerpos__", diagNow))
            {
                foreach (var p in _players.Values)
                {
                    if (p == null) continue;
                    Debug.LogError(
                        $"[PLAYER-POS] {p.Name}#{p.EntityId} " +
                        $"pos=({p.PosX:F1},{p.PosY:F1}) " +
                        $"alive={p.IsAlive} hp={(p.PlayerState != null ? p.PlayerState.CurrentHPWire : 0)}/{(p.PlayerState != null ? p.PlayerState.MaxHPWire : 0)} " +
                        $"immune={(p.PlayerState != null ? p.PlayerState.IsZoneSpawnDamageImmune : false)} " +
                        $"t={diagNow:F2}");
                }
            }

            int totalMobs = 0;
            int eligibleMobs = 0;
            int skipAggroTriggered = 0;
            int skipHasTarget = 0;
            int skipDead = 0;
            int skipNoRange = 0;
            int aggroAcquired = 0;

            if (_players.Count == 0)
            {
                if (diagNow - _proxiTickLastEmitTime >= PROXI_TICK_LOG_INTERVAL_S)
                {
                    _proxiTickLastEmitTime = diagNow;
                    Debug.LogError($"[PROXIMITY-TICK] skip reason=no-players-registered playerDict={_players.Count}");
                }
                return;
            }

            var pathMaps = new Dictionary<string, PathMap>(StringComparer.OrdinalIgnoreCase);
            foreach (var monster in SelectMonsters(onlyMonster))
            {
                totalMobs++;
                if (!monster.IsAlive) { skipDead++; continue; }
                if (monster.AggroTriggered) { skipAggroTriggered++; continue; }
                if (monster.TargetId != 0) { skipHasTarget++; continue; }

                TryGetMonsterWanderClientVisiblePosition(monster, out float monsterX, out float monsterY);
                PathMap pathMap = null;
                if (!string.IsNullOrWhiteSpace(monster.ZoneName))
                {
                    if (!pathMaps.TryGetValue(monster.ZoneName, out pathMap))
                    {
                        pathMap = PathMapManager.Instance.GetPathMap(monster.ZoneName);
                        pathMaps[monster.ZoneName] = pathMap;
                    }
                }
                float range = ResolveMonsterTargetSearchRange(monster, false);
                if (range <= 0f) { skipNoRange++; continue; }
                eligibleMobs++;

                CombatPlayer nearest = null;
                float nearestSq = float.MaxValue;
                float rangeSq = range * range;
                CombatPlayer absoluteNearest = null;
                float absoluteNearestSq = float.MaxValue;
                string nearMissReason = null;
                foreach (var player in _players.Values)
                {
                    if (playerEntityId != 0 && player.EntityId != playerEntityId) continue;
                    if (player == null || !player.IsAlive || player.PlayerState == null) continue;

                    float dx0 = player.PosX - monsterX;
                    float dy0 = player.PosY - monsterY;
                    float distSq0 = dx0 * dx0 + dy0 * dy0;
                    if (distSq0 < absoluteNearestSq) { absoluteNearest = player; absoluteNearestSq = distSq0; }

                    if (player.PlayerState.IsZoneSpawnDamageImmune)
                    { if (nearMissReason == null) nearMissReason = "spawn-immune"; continue; }
                    if (player.PlayerState.CurrentHPWire == 0 && player.PlayerState.SynchHP == 0)
                    { if (nearMissReason == null) nearMissReason = "player-zero-hp"; continue; }
                    float dx = player.PosX - monsterX;
                    float dy = player.PosY - monsterY;
                    float distSq = dx * dx + dy * dy;
                    if (distSq > rangeSq)
                    { if (nearMissReason == null) nearMissReason = "out-of-range"; continue; }
                    if (distSq >= nearestSq) continue;
                    if (pathMap != null
                        && pathMap.TryCanReachPoint(monsterX, monsterY, player.PosX, player.PosY, out bool canAggroReach)
                        && !canAggroReach)
                    { if (nearMissReason == null) nearMissReason = "pathmap-unreachable"; continue; }
                    nearest = player;
                    nearestSq = distSq;
                }

                if (nearest != null)
                {
                    aggroAcquired++;
                    SyncMonsterWanderClientVisiblePosition(monster, "proximity-acquire");
                    AggroMonster(monster, nearest, "proximity", false);
                    PropagateAlertShout(monster, "aggro-acquired");
                    if (ShouldEmitProxiLog($"aggro-{monster.EntityId}", diagNow))
                        Debug.LogError($"[PROXIMITY-AGGRO] {monster.Name}#{monster.EntityId} acquired player={nearest.Name}#{nearest.EntityId} dist={(float)System.Math.Sqrt(nearestSq):F1} range={range:F1} alerted={monster.AlertedByShout}");
                }
                else if (absoluteNearest != null && nearMissReason != null && absoluteNearestSq <= (PROXI_NEAR_MISS_FACTOR * range) * (PROXI_NEAR_MISS_FACTOR * range))
                {
                    if (ShouldEmitProxiLog($"miss-{monster.EntityId}-{nearMissReason}", diagNow))
                    {
                        float distActual = (float)System.Math.Sqrt(absoluteNearestSq);
                        Debug.LogError($"[PROXIMITY-MISS] {monster.Name}#{monster.EntityId} reason={nearMissReason} closestPlayer={absoluteNearest.Name}#{absoluteNearest.EntityId} dist={distActual:F1} range={range:F1} mobPos=({monsterX:F1},{monsterY:F1}) playerPos=({absoluteNearest.PosX:F1},{absoluteNearest.PosY:F1})");
                    }
                }
            }

            if (diagNow - _proxiTickLastEmitTime >= PROXI_TICK_LOG_INTERVAL_S)
            {
                _proxiTickLastEmitTime = diagNow;
                Debug.LogError($"[PROXIMITY-TICK] players={_players.Count} mobs={totalMobs} eligible={eligibleMobs} aggro'd-already={skipAggroTriggered} hasTarget={skipHasTarget} dead={skipDead} noRange={skipNoRange} acquired-this-tick={aggroAcquired}");
            }
        }

        // Shout propagation — when a mob aggros, alert nearby mobs within ShoutRange.
        // Mirrors retail's "mob shouts when engaging combat" mechanic. Per Ghidra
        // MonsterBehavior2::AreEnemiesNearby, the alerted flag at [Desc+0x86] widens
        // the scan to [+0x80] (ShoutRange) instead of [+0x88] (AggroRange). Alerted
        // mobs become eligible to aggro the player on their next proximity tick if
        // player is within ShoutRange. The alert is one-hop (doesn't re-propagate to
        // avoid the entire zone aggroing from a single trigger).
        private void PropagateAlertShout(Monster shouter, string reason)
        {
            if (shouter == null || shouter.ShoutRange <= 0f) return;
            float shoutRangeSq = shouter.ShoutRange * shouter.ShoutRange;
            int alertedCount = 0;
            foreach (var other in _activeMonsters.Values)
            {
                if (other == null || other == shouter) continue;
                if (!other.IsAlive) continue;
                if (other.AggroTriggered) continue;          // already in combat
                if (other.AlertedByShout) continue;          // already alerted (one-hop only)
                if (!string.Equals(other.ZoneName, shouter.ZoneName, StringComparison.OrdinalIgnoreCase)) continue;
                float dx = other.PosX - shouter.PosX;
                float dy = other.PosY - shouter.PosY;
                float distSq = dx * dx + dy * dy;
                if (distSq > shoutRangeSq) continue;
                other.AlertedByShout = true;
                alertedCount++;
            }
            if (alertedCount > 0)
                Debug.LogError($"[ALERT-SHOUT] {shouter.Name}#{shouter.EntityId} reason={reason} shoutRange={shouter.ShoutRange:F0} alerted={alertedCount}");
        }

        // Two-stage aggro v2 2026-05-27: Ghidra MonsterBehavior2::AreEnemiesNearby uses
        // [Desc+0x88] (AggroRange ~20) by default and [Desc+0x80] (ShoutRange ~300) when
        // the mob's runtime alert byte ([Desc+0x86]==2) is set. Alert is set when an
        // adjacent mob shouts (= aggros and propagates to neighbors within ShoutRange).
        // First-pass uses bare AggroRange (matches retail's "player must walk into melee"
        // behavior). When a mob aggros, ApplyAlertShout() propagates the alert to nearby
        // mobs (mirrors retail shout mechanic). Confirmed via 2026-05-26 user gameplay
        // reference video: mobs aggro at melee range, then nearby mobs alert and chain-aggro.
        private float ResolveMonsterTargetSearchRange(Monster monster, bool hasPathMap)
        {
            if (monster == null) return 0f;
            // Alerted mobs (received a shout from a neighbor) use the wider ShoutRange.
            // We use ShoutRange (~300) here, not PerceptionRange (~500) — the [Desc+0x80]
            // field is most plausibly ShoutRange per .gc inheritance + spawn log values.
            if (monster.AlertedByShout && monster.ShoutRange > 0f)
                return monster.ShoutRange;
            // Default: tight AggroRange. Player must walk into melee to trigger initial aggro.
            if (monster.AggroRange > 0f) return monster.AggroRange;
            return 0f;
        }

        private void ProcessMonsterMovement(float deltaTime)
        {
            ProcessMonsterMovement(deltaTime, 0);
        }

        private void ProcessMonsterMovement(float deltaTime, uint playerEntityId)
        {
            ProcessMonsterMovement(deltaTime, playerEntityId, null, true);
        }

        private void ProcessMonsterMovement(float deltaTime, uint playerEntityId, Monster onlyMonster)
        {
            ProcessMonsterMovement(deltaTime, playerEntityId, onlyMonster, true);
        }

        private void ProcessMonsterMovement(float deltaTime, uint playerEntityId, Monster onlyMonster, bool emitPositionChanged)
        {
            if (deltaTime <= 0f) return;
            float nativeNow = GetNativeCombatTime();
            var pathMaps = new Dictionary<string, PathMap>(StringComparer.OrdinalIgnoreCase);
            foreach (var monster in SelectMonsters(onlyMonster))
            {
                if (!monster.IsAlive || !monster.AggroTriggered || monster.TargetId == 0) continue;
                if (playerEntityId != 0 && monster.TargetId != playerEntityId) continue;
                if (!_players.TryGetValue(monster.TargetId, out var target) || target == null || !target.IsAlive || target.PlayerState == null) continue;
                if (target.PlayerState.CurrentHPWire == 0 && target.PlayerState.SynchHP == 0) continue;
                if (monster.AttackPending) continue;

                float allowedRange = ResolveMonsterEffectiveAttackRange(monster);
                if (allowedRange <= 0f) continue;

                float dx = target.PosX - monster.PosX;
                float dy = target.PosY - monster.PosY;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);
                PathMap pathMap = null;
                if (!string.IsNullOrWhiteSpace(monster.ZoneName))
                {
                    if (!pathMaps.TryGetValue(monster.ZoneName, out pathMap))
                    {
                        pathMap = PathMapManager.Instance.GetPathMap(monster.ZoneName);
                        pathMaps[monster.ZoneName] = pathMap;
                    }
                }
                if (pathMap != null
                    && pathMap.TryCanReachPoint(monster.PosX, monster.PosY, target.PosX, target.PosY, out bool canMoveReach)
                    && !canMoveReach)
                {
                    ClearMonsterCombatContact(monster, target);
                    if (monster.State == MonsterState.Combat)
                        monster.State = MonsterState.Chase;
                    TraceMonsterState(monster, "movement", target, dist, allowedRange, "path-blocked");
                    continue;
                }
                if (dist <= allowedRange + NATIVE_CONTACT_RANGE_EPSILON || dist <= 0.001f)
                {
                    if (monster.State == MonsterState.Chase)
                        monster.State = MonsterState.Combat;
                    monster.CombatContactTargetId = target.EntityId;
                    monster.CombatContactUntil = nativeNow + Mathf.Max(0.25f, ResolveMonsterAttackWindup(monster) + 0.25f);
                    TraceMonsterState(monster, "movement", target, dist, allowedRange, "contact");
                    continue;
                }

                float speed = ResolveMonsterMovementSpeed(monster);
                if (speed <= 0f)
                {
                    monster.State = MonsterState.Chase;
                    TraceMonsterState(monster, "movement", target, dist, allowedRange, "no-speed");
                    continue;
                }

                float step = Mathf.Min(Mathf.Max(0f, dist - allowedRange), speed * deltaTime);
                if (step <= 0f) continue;

                float prevMobX = monster.PosX;
                float prevMobY = monster.PosY;

                // PA1.3: legacy chase math — plain float, straight-line, no obstacle routing.
                float legacyNewX = prevMobX + dx / dist * step;
                float legacyNewY = prevMobY + dy / dist * step;
                float legacyHeading = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg;

                // PA1.3: optional UnitMoverSim chase — Fixed32, Pathfinder-aware (if a PathMap
                // is attached), collision-slide. Runs only when the cutover flag is on OR
                // shadow mode is on. Sync'd from authoritative monster.PosX/Y each tick so
                // it operates as a pure compute step rather than carrying drift.
                float moverNewX = legacyNewX, moverNewY = legacyNewY;
                bool moverRan = false;
                if (monster.Mover != null && (UseUnitMoverForChase || EnableChaseMoverShadow))
                {
                    var mover = monster.Mover;
                    mover.PosX = Fixed32.FromFloat(prevMobX);
                    mover.PosY = Fixed32.FromFloat(prevMobY);
                    // PA1.5b: Pass the LEGACY-CLAMPED step as the mover's per-tick
                    // Speed, not the raw speed*dt. This makes the mover converge on
                    // attack range at the same rate as the legacy `step = min(...)`
                    // computation, eliminating the 2-unit overshoot at the arrive
                    // boundary that the prior session captured.
                    mover.Speed = Fixed32.FromFloat(step);

                    int targetXRaw = Fixed32.FromFloat(target.PosX).RawValue;
                    int targetYRaw = Fixed32.FromFloat(target.PosY).RawValue;
                    int arriveRadiusRaw = Fixed32.FromFloat(allowedRange).RawValue;
                    int replanThresholdRaw = Fixed32.FromFloat(3f).RawValue;
                    const byte replanCooldownTicks = 10;

                    mover.SetChaseTarget(targetXRaw, targetYRaw, arriveRadiusRaw,
                                         replanCooldownTicks, replanThresholdRaw);
                    mover.UpdateMovement();

                    moverNewX = mover.PosX.ToFloat();
                    moverNewY = mover.PosY.ToFloat();
                    moverRan = true;
                }

                // PA1.4: shadow-mode delta log (only when shadow is on AND cutover is off).
                if (moverRan && EnableChaseMoverShadow && !UseUnitMoverForChase
                    && (_nativeCombatTick % CHASE_MOVER_DIFF_LOG_INTERVAL == 0))
                {
                    float ddx = legacyNewX - moverNewX;
                    float ddy = legacyNewY - moverNewY;
                    float diff = Mathf.Sqrt(ddx * ddx + ddy * ddy);
                    Debug.LogError($"[CHASE-MOVER-DIFF] {monster.Name}#{monster.EntityId} " +
                                   $"legacy=({legacyNewX:F1},{legacyNewY:F1}) " +
                                   $"mover=({moverNewX:F1},{moverNewY:F1}) " +
                                   $"delta={diff:F2} dist={dist:F1} range={allowedRange:F1} step={step:F2}");
                }

                // Authoritative write — cutover routes through mover; default uses legacy.
                if (moverRan && UseUnitMoverForChase)
                {
                    monster.PosX = moverNewX;
                    monster.PosY = moverNewY;
                }
                else
                {
                    monster.PosX = legacyNewX;
                    monster.PosY = legacyNewY;
                }
                monster.Heading = legacyHeading;
                monster.State = MonsterState.Chase;

                float remaining = dist - step;
                if (remaining <= allowedRange + NATIVE_CONTACT_RANGE_EPSILON)
                {
                    monster.State = MonsterState.Combat;
                    monster.CombatContactTargetId = target.EntityId;
                    monster.CombatContactUntil = nativeNow + Mathf.Max(0.25f, ResolveMonsterAttackWindup(monster) + 0.25f);
                }
                if (emitPositionChanged)
                    OnMonsterPositionChanged?.Invoke(monster);
                TraceMonsterState(monster, "movement", target, remaining, allowedRange, "move");

                // Stage 3 diagnostic — log chase progress so we can correlate with what user sees
                // on screen. If server's mob is closing faster than client's visible mob, that's a
                // source of extra-damage divergence (server starts attacking before client's mob
                // is in range). Throttled to 1/sec per (mob, target) pair.
                string chaseKey = $"chase-{monster.EntityId}-{target.EntityId}";
                if (ShouldEmitProxiLog(chaseKey, Time.time))
                {
                    Debug.LogError(
                        $"[CHASE-TICK] {monster.Name}#{monster.EntityId} → player#{target.EntityId} " +
                        $"mob=({prevMobX:F1},{prevMobY:F1})→({monster.PosX:F1},{monster.PosY:F1}) " +
                        $"player=({target.PosX:F1},{target.PosY:F1}) " +
                        $"dist={dist:F1}→{remaining:F1} step={step:F1} speed={speed:F1} " +
                        $"attackRange={allowedRange:F1} state={monster.State} dt={deltaTime:F3}");
                }
            }
        }

        // Chase-context speed resolver. Called only from ProcessMonsterMovement and from the
        // projectile-impact predictor — both assume the mob is moving toward a combat target,
        // not wandering. The authored `.gc` data carries TWO speeds per mob:
        //   * `Speed`     → loaded into Monster.MoveSpeed via GetAuthoredMoveSpeed (chase/run)
        //   * `WalkSpeed` → loaded into Monster.WalkSpeed via GetAuthoredWalkSpeed (idle wander)
        // Previously this returned WalkSpeed first, which made server's chase 2.2× slower than
        // the client's (Pup: 25 walk vs 55 chase). Result: server's mob never closed to melee
        // range while client's mob was actively swinging on the player — the simulator's first
        // HIT lagged client's by ~1.6-2.0 seconds on a typical aggro acquisition. Diagnostic
        // evidence: [CHASE-TICK] logs 2026-05-26 showed server mob at dist=50+ while popup
        // confirmed client locally took damage in melee.
        // WanderSimulator handles its own walk speed directly via monster.WalkSpeed; it does
        // NOT route through this function.
        private static float ResolveMonsterMovementSpeed(Monster monster)
        {
            if (monster == null) return 0f;
            if (monster.MoveSpeed > 0f) return monster.MoveSpeed;   // chase/run speed (= authored Speed)
            if (monster.WalkSpeed > 0f) return monster.WalkSpeed;   // fallback for mobs missing Speed
            return 0f;
        }

        public float GetMonsterMovementSpeed(Monster monster)
        {
            return ResolveMonsterMovementSpeed(monster);
        }

        private void AdvanceMonsterPrimarySkillCooldown(Monster monster, float now)
        {
            if (monster == null || monster.PrimaryActiveSkillCooldownRemainingTicks == 0)
            {
                if (monster != null && monster.PrimaryActiveSkillCooldownLastTime <= 0f)
                    monster.PrimaryActiveSkillCooldownLastTime = now;
                return;
            }
            if (monster.PrimaryActiveSkillCooldownLastTime <= 0f)
            {
                monster.PrimaryActiveSkillCooldownLastTime = now;
                return;
            }

            int ticks = Mathf.FloorToInt((now - monster.PrimaryActiveSkillCooldownLastTime) / NATIVE_UNIT_TICK_INTERVAL);
            if (ticks <= 0) return;
            ushort oldTicks = monster.PrimaryActiveSkillCooldownRemainingTicks;
            monster.PrimaryActiveSkillCooldownRemainingTicks = ticks >= oldTicks ? (ushort)0 : (ushort)(oldTicks - ticks);
            monster.PrimaryActiveSkillCooldownLastTime += ticks * NATIVE_UNIT_TICK_INTERVAL;
            if (oldTicks != monster.PrimaryActiveSkillCooldownRemainingTicks)
                Debug.LogError($"[MON-SKILL-CD] advance {monster.Name}#{monster.EntityId} skill={monster.PrimaryActiveSkillPath ?? "none"} ticks={oldTicks}->{monster.PrimaryActiveSkillCooldownRemainingTicks} elapsedTicks={ticks}");
        }

        private void SelectMonsterPrimarySkillForAttack(Monster monster, CombatPlayer target, float dist, float fallbackRange, string source)
        {
            if (monster == null) return;
            monster.UsePrimaryActiveSkillThisAttack = false;
            if (string.IsNullOrWhiteSpace(monster.PrimaryActiveSkillPath))
                return;

            AdvanceMonsterPrimarySkillCooldown(monster, GetNativeCombatTime());
            float skillRange = monster.PrimaryActiveSkillRange > 0f ? monster.PrimaryActiveSkillRange : fallbackRange;
            if (monster.PrimaryActiveSkillCooldownRemainingTicks > 0)
            {
                Debug.LogError($"[MON-SKILL] validate fallback=cooldown {monster.Name}#{monster.EntityId}->{target?.Name ?? "unknown"} skill={monster.PrimaryActiveSkillPath} remaining={monster.PrimaryActiveSkillCooldownRemainingTicks}/{monster.PrimaryActiveSkillCooldownTicks} dist={dist:F1} range={skillRange:F1} source={source}");
                return;
            }
            if (dist > skillRange + NATIVE_CONTACT_RANGE_EPSILON)
            {
                Debug.LogError($"[MON-SKILL] validate fallback=range {monster.Name}#{monster.EntityId}->{target?.Name ?? "unknown"} skill={monster.PrimaryActiveSkillPath} dist={dist:F1} range={skillRange:F1} source={source}");
                return;
            }

            monster.UsePrimaryActiveSkillThisAttack = true;
            Debug.LogError($"[MON-SKILL] validate use {monster.Name}#{monster.EntityId}->{target?.Name ?? "unknown"} skill={monster.PrimaryActiveSkillPath} id={monster.PrimaryActiveSkillId} dist={dist:F1} range={skillRange:F1} cooldownTicks={monster.PrimaryActiveSkillCooldownTicks} source={source}");
        }

        public void CommitMonsterPrimarySkillUse(Monster monster, string source)
        {
            if (monster == null || !monster.UsePrimaryActiveSkillThisAttack || string.IsNullOrWhiteSpace(monster.PrimaryActiveSkillPath))
                return;
            if (monster.PrimaryActiveSkillCooldownTicks > 0)
            {
                monster.PrimaryActiveSkillCooldownRemainingTicks = monster.PrimaryActiveSkillCooldownTicks;
                monster.PrimaryActiveSkillCooldownLastTime = GetNativeCombatTime();
            }
            Debug.LogError($"[MON-SKILL-CD] set {monster.Name}#{monster.EntityId} skill={monster.PrimaryActiveSkillPath} ticks={monster.PrimaryActiveSkillCooldownRemainingTicks} source={source ?? "unknown"}");
        }

        private void ProcessMonsterAttacks(float deltaTime)
        {
            ProcessMonsterAttacks(deltaTime, 0);
        }

        private void ProcessMonsterAttacks(float deltaTime, uint playerEntityId, bool allowNewAttacks = true)
        {
            ProcessMonsterAttacks(deltaTime, playerEntityId, allowNewAttacks, null);
        }

        private void ProcessMonsterAttacks(float deltaTime, uint playerEntityId, bool allowNewAttacks, Monster onlyMonster, float nativeNow = -1f)
        {
            if (_roomRng == null) return;
            float now = nativeNow >= 0f ? nativeNow : GetNativeCombatTime();

            foreach (var monster in SelectMonsters(onlyMonster))
            {
                AdvanceMonsterPrimarySkillCooldown(monster, now);
                TryAssistFromAlertSource(monster, "attack-loop");
                bool pendingClientVisibleAttack = monster.AttackPending && monster.AttackClientVisible;
                bool pendingRuntimeAttack = monster.AttackPending && monster.AttackCommitTime > 0f;
                if (!monster.AggroTriggered) continue;
                if (monster.DeathPendingClientConfirmation
                    && !(pendingRuntimeAttack && monster.AttackNativeContactOnly && !monster.AttackHitResolved))
                {
                    CancelMonsterPendingAttack(monster, "monster_death_pending_client_confirmation");
                    continue;
                }
                if (!monster.IsAlive && !pendingClientVisibleAttack && !pendingRuntimeAttack)
                {
                    monster.AttackPending = false;
                    monster.AttackSoundPending = false;
                    monster.AttackHitResolved = false;
                    monster.AttackStartedTime = 0f;
                    monster.AttackEndTime = 0f;
                    monster.AttackCommitTime = 0f;
                    monster.AttackSoundTime = 0f;
                    continue;
                }
                if (playerEntityId != 0 && monster.TargetId != playerEntityId) continue;
                if (!_players.TryGetValue(monster.TargetId, out var target) || !target.IsAlive || target.PlayerState == null) continue;
                if (target.PlayerState.IsZoneSpawnDamageImmune)
                {
                    CancelMonsterPendingAttack(monster, "target_zone_spawn_invulnerability");
                    continue;
                }
                if (target.PlayerState.CurrentHPWire == 0 && target.PlayerState.SynchHP == 0)
                {
                    target.IsAlive = false;
                    CancelMonsterPendingAttack(monster, "target_dead");
                    continue;
                }
                float allowedRange = ResolveMonsterEffectiveAttackRange(monster);
                if (allowedRange <= 0f) continue;
                float dist = Distance2D(monster.PosX, monster.PosY, target.PosX, target.PosY);
                bool attackPathClear = IsMonsterAttackPathClear(monster, target, null);
                bool nativeClientContact = IsNativeClientCombatContact(monster, target, dist, allowedRange);
                bool nativeTargetAction = HasNativeMonsterTargetAction(monster, target, dist);
                bool weaponRuntimeReach = HasMonsterWeaponRuntimeReach(monster, target, dist, allowedRange, nativeClientContact);
                TraceMonsterState(monster, "attack-loop", target, dist, allowedRange, monster.AttackPending ? "pending" : "ready");

                if (monster.AttackPending)
                {
                    if (!attackPathClear)
                    {
                        CancelMonsterPendingAttack(monster, $"world_blocked dist={dist:F1} range={allowedRange:F1}");
                        if (monster.IsAlive)
                            monster.State = MonsterState.Chase;
                        Debug.LogError($"[MON-DAMAGE] {monster.Name}#{monster.EntityId}->{target.Name} cancel worldBlocked dist={dist:F1} monsterRange={allowedRange:F1}");
                        OnMonsterAttackResolved?.Invoke(monster, target, false, target.PlayerState.CurrentHPWire);
                        continue;
                    }
                    if (!monster.AttackClientVisible)
                    {
                        if (dist > allowedRange + NATIVE_CONTACT_RANGE_EPSILON && !nativeClientContact)
                        {
                            CancelMonsterPendingAttack(monster, $"stale_deferred_contact dist={dist:F1} range={allowedRange:F1}");
                            if (monster.IsAlive)
                                monster.State = MonsterState.Chase;
                            continue;
                        }
                        if (monster.IsAlive && monster.AttackCommitTime <= 0f)
                        {
                            SelectMonsterPrimarySkillForAttack(monster, target, dist, allowedRange, "pending-start");
                            if (!weaponRuntimeReach && !monster.UsePrimaryActiveSkillThisAttack)
                            {
                                if (nativeTargetAction)
                                    TraceFarTargetAction(monster, target, dist, allowedRange, "pending-start");
                                DelayMonsterAttackRetry(monster, "weapon_range_pending_start");
                                CancelMonsterPendingAttack(monster, $"weapon_range_pending_start dist={dist:F1} range={allowedRange:F1} targetRange={ResolveMonsterTargetSearchRange(monster, true):F1}");
                                if (monster.IsAlive)
                                    monster.State = MonsterState.Chase;
                                continue;
                            }
                            int handlerCount = OnMonsterAttackStarted?.GetInvocationList().Length ?? 0;
                            Debug.LogError($"[MON-ATTACK] dispatch start {monster.Name}->{target.Name} session={monster.AttackSessionId} handlers={handlerCount} dist={dist:F1} range={allowedRange:F1}");
                            OnMonsterAttackStarted?.Invoke(monster, target, monster.AttackSessionId);
                            if (!monster.AttackPending)
                            {
                                Debug.LogError($"[MON-ATTACK] {monster.Name}->{target.Name} START canceled session={monster.AttackSessionId}");
                                continue;
                            }
                        }
                        else if (monster.AttackCommitTime <= 0f)
                        {
                            CancelMonsterPendingAttack(monster, "dead_unarmed");
                            continue;
                        }
                        if (monster.AttackClientVisible)
                        {
                            ArmMonsterClientVisibleAttack(monster, target, now);
                            continue;
                        }
                        if (!monster.AttackClientVisible && monster.AttackNativeContactOnly && nativeClientContact)
                        {
                            ArmMonsterRuntimeAttack(monster, target, "NATIVE-CONTACT", now);
                        }
                        else if (!monster.AttackClientVisible)
                        {
                            DelayMonsterAttackRetry(monster, "unsent_native_monster_attack");
                            CancelMonsterPendingAttack(monster, "unsent_native_monster_attack");
                            Debug.LogError($"[MON-ATTACK] {monster.Name}->{target.Name} canceled unsent native UseTarget session={monster.AttackSessionId} dist={dist:F1} range={allowedRange:F1} nativeContact={nativeClientContact}");
                            continue;
                        }
                    }
                    if (monster.AttackHitResolved)
                    {
                        if (now < monster.AttackEndTime) continue;
                        monster.AttackPending = false;
                        monster.AttackClientVisible = false;
                        monster.AttackNativeContactOnly = false;
                        monster.AttackSoundPending = false;
                        monster.AttackHitResolved = false;
                        monster.AttackStartedTime = 0f;
                        monster.AttackEndTime = 0f;
                        monster.AttackCommitTime = 0f;
                        monster.AttackSoundTime = 0f;
                        if (monster.IsAlive)
                            monster.State = MonsterState.Combat;
                        TraceMonsterState(monster, "attack-complete", target, dist, allowedRange, "end");
                        continue;
                    }
                    if (monster.AttackSoundPending && now >= monster.AttackSoundTime)
                        ConsumeMonsterAttackSoundRng(monster);
                    if (now < monster.AttackCommitTime) continue;
                    if (monster.AttackSoundPending)
                        ConsumeMonsterAttackSoundRng(monster);
                    monster.AttackHitResolved = true;
                    if (monster.AttackEndTime < now)
                        monster.AttackEndTime = now;
                    float commitDelta = Distance2D(monster.AttackCommitTargetX, monster.AttackCommitTargetY, target.PosX, target.PosY);
                    bool attackClientVisible = monster.AttackClientVisible;
                    bool commitTargetStillValid = attackPathClear
                        && (weaponRuntimeReach
                        || monster.UsePrimaryActiveSkillThisAttack
                        || (attackClientVisible && AttackCommitTargetStillValid(monster, target)));
                    if (!commitTargetStillValid)
                    {
                        monster.AttackPending = false;
                        monster.AttackSoundPending = false;
                        monster.CombatContactTargetId = 0;
                        monster.CombatContactUntil = 0f;
                        monster.AttackClientVisible = false;
                        monster.AttackNativeContactOnly = false;
                        monster.AttackHitResolved = false;
                        monster.AttackStartedTime = 0f;
                        monster.AttackEndTime = 0f;
                        monster.AttackCommitTime = 0f;
                        monster.AttackSoundTime = 0f;
                        if (monster.IsAlive)
                            monster.State = MonsterState.Chase;
                        Debug.LogError($"[MON-DAMAGE] {monster.Name}#{monster.EntityId}->{target.Name} cancel movedOut dist={dist:F1} commitDelta={commitDelta:F1} monsterRange={allowedRange:F1} nativeContact={nativeClientContact} clientVisible={attackClientVisible}");
                        OnMonsterAttackResolved?.Invoke(monster, target, false, target.PlayerState.CurrentHPWire);
                        continue;
                    }
                    if (!attackClientVisible && !monster.AttackNativeContactOnly && !nativeClientContact)
                    {
                        monster.AttackPending = false;
                        monster.AttackSoundPending = false;
                        monster.CombatContactTargetId = 0;
                        monster.CombatContactUntil = 0f;
                        monster.AttackClientVisible = false;
                        monster.AttackNativeContactOnly = false;
                        monster.AttackHitResolved = false;
                        monster.AttackStartedTime = 0f;
                        monster.AttackEndTime = 0f;
                        monster.AttackCommitTime = 0f;
                        monster.AttackSoundTime = 0f;
                        if (monster.IsAlive)
                            monster.State = MonsterState.Chase;
                        Debug.LogError($"[MON-DAMAGE] {monster.Name}#{monster.EntityId}->{target.Name} cancel noNativeAttack dist={dist:F1} commitDelta={commitDelta:F1} monsterRange={allowedRange:F1}");
                        OnMonsterAttackResolved?.Invoke(monster, target, false, target.PlayerState.CurrentHPWire);
                        continue;
                    }
                    ResolveMonsterAttackDamage(monster, target, dist, "MON-DAMAGE", "ProcessMonsterAttacks");
                    continue;
                }
                else
                {
                    if (!monster.IsAlive) continue;
                    if (!allowNewAttacks) continue;
                    if (!attackPathClear)
                    {
                        ClearMonsterCombatContact(monster, target);
                        continue;
                    }
                    SelectMonsterPrimarySkillForAttack(monster, target, dist, allowedRange, "new-start");
                    if (!weaponRuntimeReach && !monster.UsePrimaryActiveSkillThisAttack)
                    {
                        if (nativeTargetAction)
                            TraceFarTargetAction(monster, target, dist, allowedRange, "new-start");
                        if (monster.State == MonsterState.Combat)
                            monster.State = MonsterState.Chase;
                        continue;
                    }
                    if (now < monster.LastAttackTime) continue;
                    monster.AttackPending = true;
                    monster.AttackClientVisible = false;
                    monster.AttackNativeContactOnly = false;
                    monster.AttackHitResolved = false;
                    monster.AttackSoundRaw = 0;
                    monster.AttackSoundGateRaw = 0;
                    monster.AttackSoundRepeatRaw = 0;
                    monster.AttackUseRaw = 0;
                    monster.AttackStartedTime = now;
                    monster.AttackCommitTime = 0f;
                    monster.AttackSoundTime = 0f;
                    monster.AttackEndTime = 0f;
                    monster.AttackSoundPending = false;
                    monster.AttackCommitTargetX = target.PosX;
                    monster.AttackCommitTargetY = target.PosY;
                    monster.State = MonsterState.Attacking;
                    monster.AttackSessionId++;
                    if (monster.AttackSessionId == 0) monster.AttackSessionId = 1;
                    int handlerCount = OnMonsterAttackStarted?.GetInvocationList().Length ?? 0;
                    Debug.LogError($"[MON-ATTACK] dispatch start {monster.Name}->{target.Name} session={monster.AttackSessionId} handlers={handlerCount} dist={dist:F1} range={allowedRange:F1}");
                    TraceMonsterState(monster, "attack-start", target, dist, allowedRange, "dispatch");
                    OnMonsterAttackStarted?.Invoke(monster, target, monster.AttackSessionId);
                    if (monster.AttackPending && monster.AttackClientVisible)
                        ArmMonsterClientVisibleAttack(monster, target, now);
                    else if (monster.AttackPending && monster.AttackNativeContactOnly && nativeClientContact)
                    {
                        if (monster.AttackCommitTime <= 0f)
                            ArmMonsterRuntimeAttack(monster, target, "NATIVE-CONTACT", now);
                        Debug.LogError($"[MON-ATTACK] {monster.Name}->{target.Name} START kept native contact runtime session={monster.AttackSessionId} dist={dist:F1} range={allowedRange:F1}");
                    }
                    else if (monster.AttackPending)
                    {
                        DelayMonsterAttackRetry(monster, "unsent_native_monster_attack_start");
                        CancelMonsterPendingAttack(monster, "unsent_native_monster_attack_start");
                        Debug.LogError($"[MON-ATTACK] {monster.Name}->{target.Name} START canceled unsent native UseTarget session={monster.AttackSessionId} nativeContact={nativeClientContact}");
                    }
                    else
                        Debug.LogError($"[MON-ATTACK] {monster.Name}->{target.Name} START canceled session={monster.AttackSessionId}");
                    continue;
                }
            }
        }

        private void ArmMonsterClientVisibleAttack(Monster monster, CombatPlayer target, float nativeNow = -1f)
        {
            ArmMonsterRuntimeAttack(monster, target, "START", nativeNow);
        }

        private void ArmMonsterRuntimeAttack(Monster monster, CombatPlayer target, string marker, float nativeNow = -1f)
        {
            if (monster == null || target == null) return;
            float now = nativeNow >= 0f ? nativeNow : GetNativeCombatTime();
            if (monster.AttackStartedTime <= 0f)
                monster.AttackStartedTime = now;
            if (monster.AttackCommitTime > 0f)
                return;
            AdvanceMonsterAttackAnimation(monster);
            float windup = ResolveMonsterAttackWindup(monster);
            ResolveMonsterAttackAnimationFrames(monster, out int totalFrames, out int hitFrame, out int soundFrame);
            float startTime = monster.AttackStartedTime > 0f ? monster.AttackStartedTime : now;
            monster.AttackCommitTime = startTime + windup;
            monster.AttackSoundTime = startTime + ResolveMonsterAttackSoundDelay(monster, windup);
            monster.AttackEndTime = startTime + Mathf.Max(windup, ResolveMonsterAttackFrameSeconds(monster, totalFrames));
            monster.LastAttackTime = startTime + ResolveMonsterAttackCooldownSeconds(monster);
            monster.AttackHitResolved = false;
            monster.AttackSoundPending = monster.HasAttackSound || monster.AttackWeaponSoundCount > 0 || monster.AttackRepeatSoundCount > 0;
            Debug.LogError($"[MON-ATTACK] {monster.Name}->{target.Name} {marker} anim={monster.AttackAnimationIndex} session={monster.AttackSessionId} use=0x{monster.AttackUseRaw:X8} frames={totalFrames}/{hitFrame}/{soundFrame} soundAt={monster.AttackSoundTime:F3} hitAt={monster.AttackCommitTime:F3} endAt={monster.AttackEndTime:F3} weaponSounds={monster.AttackWeaponSoundCount} attackSounds={monster.AttackRepeatSoundCount}");
            TraceMonsterState(monster, "attack-arm", target, -1f, ResolveMonsterEffectiveAttackRange(monster), marker);
        }

        public void Update(float deltaTime)
        {
            Update(deltaTime, true);
        }

        public void Update(float deltaTime, bool allowNewMonsterAttacks)
        {
            TraceCombatTick("Update", deltaTime, allowNewMonsterAttacks);
            ProcessProximityAggro();
            ProcessMonsterAssistAlerts();
            ProcessMonsterMovement(deltaTime);
            float nativeNow = GetNativeCombatTime();
            AdvanceMonsterModifierRuntime(_roomRng, nativeNow, "Update");
            ProcessMonsterAttacks(deltaTime, 0, allowNewMonsterAttacks, null, nativeNow);
            UpdateNativeMaintenance(deltaTime);
        }

        // ── Section 10d: server-side mob→player combat tick ────────────────
        // Drives MonsterAttackController. Aggregates ticks at 30Hz to match the
        // sim's native rate. Provider+accumulator are lazy/state-only so legacy
        // damage paths are unaffected (controller's EnableServerMobDamage flag is
        // false by default, so this only emits [MOB-SWING] logs until validated).

        private CombatPlayerDamageTargetProvider _mobDamageTargetProvider;
        private float _mobCombatTickAccumulator;

        public bool TryGetCombatPlayerForController(uint entityId, out CombatPlayer player)
            => _players.TryGetValue(entityId, out player);

        private void RunServerMobCombatTick(float deltaTime)
        {
            if (!_roomRngInitialized || _roomRng == null) return;
            int activeCount = MonsterAttackController.Instance.ActiveMobCount;
            int attackingCount = MonsterAttackController.Instance.AttackingMobCount;

            if (VerboseMobCtrlTick)
            {
                float now = Time.time;
                if (now - _mobCtrlDiagLastTime >= 3f)
                {
                    _mobCtrlDiagLastTime = now;
                    Debug.LogError($"[MOB-CTRL-DIAG] active={activeCount} attacking={attackingCount} rngReady={_roomRngInitialized}");
                }
            }

            if (activeCount == 0) return;

            if (_mobDamageTargetProvider == null)
                _mobDamageTargetProvider = new CombatPlayerDamageTargetProvider(this);

            _mobCombatTickAccumulator += deltaTime;
            int ticks = Mathf.FloorToInt(_mobCombatTickAccumulator / NATIVE_UNIT_TICK_INTERVAL);
            if (ticks <= 0) return;
            _mobCombatTickAccumulator -= ticks * NATIVE_UNIT_TICK_INTERVAL;

            for (int i = 0; i < ticks; i++)
                MonsterAttackController.Instance.Tick(_roomRng, _mobDamageTargetProvider);
        }

        private void RegisterMonsterForServerCombat(Monster monster)
        {
            if (monster == null) return;
            if (!MonsterAttackData.Instance.TryGetProfile(monster.GCType, out var profile))
            {
                Debug.LogError($"[MOB-CTRL-REGISTER] no profile for gc='{monster.GCType}' entity={monster.EntityId} — skip");
                return;
            }
            // S10g+S10e: pass monster's rank-resolved authored stats (the spawn pipeline
            // walks the melee01.rank1 etc. inheritance, so monster.AttackRating reflects
            // rank1's 0.15 override, not the base creature's 1.0). The Builder uses these
            // with the MonsterCurves curve lookups to match client-side computeAttributes.
            var stats = MonsterUnitStatsBuilder.Build(profile, monster.Level,
                damageModOverride: monster.DamageMod,
                attackRatingOverride: monster.AttackRating,
                defenseRatingOverride: monster.DefenseRating);
            int monsterAttackSpeedKnob = MonsterAttackData.Instance.MonsterAttackSpeed;
            float weaponCoolDownSec = profile.WeaponCoolDown > 0f ? profile.WeaponCoolDown : monster.AttackCooldown;
            float attackSpeedScalar = profile.AttackSpeed > 0f ? profile.AttackSpeed : monster.AttackSpeed;
            // S12: pass monster.AttackRange so the controller can range-gate hallucinated swings
            // when mob is aggro'd but not actually in melee range of the player.
            MonsterAttackController.Instance.Register(monster.EntityId, stats, weaponCoolDownSec, attackSpeedScalar, monsterAttackSpeedKnob, monster.AttackRange);
            Debug.LogError($"[MOB-CTRL-REGISTER] OK gc='{monster.GCType}' entity={monster.EntityId} level={monster.Level} authAR={monster.AttackRating:F2} authDR={monster.DefenseRating:F2} authDmgMod={monster.DamageMod:F2} cool={weaponCoolDownSec:F2}s atkSpeed={attackSpeedScalar:F2} knob={monsterAttackSpeedKnob} stats.AR={stats.BaseAttackRating} stats.DR={stats.BaseDefenseRating} stats.DmgMod={stats.BaseDamageMod}");
        }

        // Throttled diagnostic — fires every ~3 seconds if any mob is aggro'd, so we
        // can see whether the controller has registered mobs and a target without
        // spamming the log per-tick. Set false to silence after diagnosis.
        public static bool VerboseMobCtrlTick = true;
        private float _mobCtrlDiagLastTime;

        public void UpdateNativeMonsterEntity(uint entityId, float deltaTime, bool allowNewMonsterAttacks)
        {
            UpdateNativeMonsterEntity(entityId, deltaTime, allowNewMonsterAttacks, GetNativeCombatTime());
        }

        public void UpdateNativeMonsterEntity(uint entityId, float deltaTime, bool allowNewMonsterAttacks, float nativeNow)
        {
            if (!_activeMonsters.TryGetValue(entityId, out var monster))
                return;
            TraceCombatTick("UpdateNativeMonsterEntity", deltaTime, allowNewMonsterAttacks, monster);
            ProcessProximityAggro(0, monster);
            ProcessMonsterAssistAlerts(monster);
            ProcessMonsterMovement(deltaTime, 0, monster);
            AdvanceMonsterModifierRuntimeForTarget(entityId, _roomRng, nativeNow, "UpdateNativeMonsterEntity");
            ProcessMonsterAttacks(deltaTime, 0, allowNewMonsterAttacks, monster, nativeNow);
        }

        public void UpdateNativeMaintenance(float deltaTime)
        {
            RunServerMobCombatTick(deltaTime);
            ProcessNativeDeathLifecycles(deltaTime);
            for (int i = _respawnQueue.Count - 1; i >= 0; i--)
            {
                if (Time.time >= _respawnQueue[i].RespawnTime)
                {
                    SpawnMonster(_respawnQueue[i].GCType, _respawnQueue[i].PosX, _respawnQueue[i].PosY, _respawnQueue[i].PosZ, _respawnQueue[i].Heading, _respawnQueue[i].ZoneName, _respawnQueue[i].EncounterGroupKey, _respawnQueue[i].EncounterDifficulty);
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
            _monsterHPAuthority.Clear();
            _monsterRuntimeDamageCommitted.Clear();
            _monsterHPRegenLastTime.Clear();
            _monsterHPRegenCarryWire.Clear();
            _monsterHPRegenCooldownTicks.Clear();
            _monsterManaRegenLastTime.Clear();
            _monsterManaRegenCooldownTicks.Clear();
            _monsterStateTraceSignatures.Clear();
            _activeMonsterModifiers.Clear();
            _pendingModifierKills.Clear();
            _monsterFarTargetActionLogTime.Clear();
            _hasCompletedNativeEntityUpdate = false;
            _lastCompletedNativeEntityUpdateTick = 0;
            _lastCompletedNativeEntityUpdateTime = -1f;
            _hasCompletedNativeSubEntityUpdate = false;
            _lastCompletedNativeSubEntityUpdateTick = 0;
            _lastCompletedNativeSubEntityUpdateTime = -1f;
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
                    // S10g 2026-05-27: reverted (n+1)*4+1 hypothesis. Two YouTube videos
                    // confirm Dew Valley mobs are L1 (yellow for L1 player, green for L2 player).
                    // The x32dbg-captured +0x10C=6 byte is NOT mob level — it's something else.
                    // baseAR=60 comes from `auth=1.0 × curve(disc=2)=15360 >> 16` independently
                    // of level. dungeon00 base level stays at 1 to match retail.
                    return (byte)(dungeonNum * 4 + 1);
                }
            }

            return 1;
        }

        // S10h 2026-05-27: rank-based level lookup. Retail encodes mob level via the
        // `rankN` suffix on aliased GC paths like `world.dungeon00.mob.melee01.rank1`.
        // Confirmed by YouTube videos:
        //   dungeon00_level01 spawns rank1 → mob L1 (zone MinLevel)
        //   dungeon00_level02 spawns rank2 → mob L4 (zone MaxLevel - 1)
        //   dungeon00_level03 spawns rank3 → mob L5 (zone MaxLevel)
        // dungeon00 zone range is 1..5 in `.zone` file. Other dungeons untested —
        // returns 0 to fall back to legacy logic for non-dungeon00 zones.
        private byte ResolveMobLevelFromRank(string spawnGcType, string zoneName)
        {
            if (string.IsNullOrEmpty(spawnGcType) || string.IsNullOrEmpty(zoneName)) return 0;
            string spawnLower = spawnGcType.ToLower();
            string zoneLower = zoneName.ToLower();

            // Only dungeon00 mapping is video-verified so far. Other dungeons fall back.
            if (!zoneLower.Contains("dungeon00")) return 0;

            // Parse rankN from path tail like "...melee01.rank1"
            int rankIdx = spawnLower.LastIndexOf(".rank");
            if (rankIdx < 0 || rankIdx + 5 >= spawnLower.Length) return 0;
            char digit = spawnLower[rankIdx + 5];
            if (digit < '1' || digit > '3') return 0;

            // dungeon00 hardcoded mapping (zone MinLevel=1, MaxLevel=5):
            switch (digit)
            {
                case '1': return 1;   // MinLevel
                case '2': return 4;   // MaxLevel - 1
                case '3': return 5;   // MaxLevel
                default: return 0;
            }
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
            public float EncounterDifficulty;
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
