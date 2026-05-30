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
    ///   Default animation positions: total=30(0x1E), sound=10(0xA), hit=15(0xF)
    ///   Normalized by WeaponSpeed field [edi+0x90]:
    ///     position = (default * 100) / WeaponSpeed
    ///   For starter Small Club: [0x90]=105, total=28, proc=9, hit=14
    ///   
    ///   Animation fires at elapsed=1 (countdown=total-1)
    ///   Sound fires at elapsed=sound_position
    ///   Hit fires at elapsed=hit_position
    ///
    ///   At countdown zero, native stop() ends the active weapon use
    ///
    /// For WeaponSpeed 105:
    ///   TICKS_TO_SOUND = 9
    ///   TICKS_TO_HIT  = 14
    ///   Full cycle = 28 server ticks
    ///
    /// </summary>
    public class WeaponCycleTracker
    {
        private static WeaponCycleTracker _instance;
        public static WeaponCycleTracker Instance => _instance ??= new WeaponCycleTracker();

        private Dictionary<string, WeaponCycle> _activeCycles = new Dictionary<string, WeaponCycle>();
        private Queue<CompletedAttack> _completedAttacks = new Queue<CompletedAttack>();
        private readonly List<PendingProjectileHit> _activeProjectiles = new List<PendingProjectileHit>();
        private long _nextProjectileSequence;

        // Binary 0x591980: fallback 30/10/15 normalized by WeaponSpeed, overridden by AnimationList.
        // For starter Small Club: (10*100)/105=9, (15*100)/105=14
        // From UseTarget: sound at 9, hit at 14
        private const int DEFAULT_TOTAL_TICKS = 30;
        private const int DEFAULT_SOUND_POSITION = 10;
        private const int DEFAULT_HIT_POSITION = 15;
        private const float NATIVE_UPDATE_TICK = 1f / 30f;
        public const float NativeUpdateTickSeconds = NATIVE_UPDATE_TICK;
        private const float NATIVE_CONTACT_RANGE_EPSILON = 1f / 16f;
        private const int MAX_PENDING_REPEAT_USES = 16;
        private static readonly string[] PLAYER_ANIMATION_LIST_PATHS =
        {
            "avatar.races.humanmale.HumanMaleAnimations",
            "avatar.races.humanfemale.HumanFemaleAnimations",
            "HumanMaleAnimations",
            "HumanFemaleAnimations"
        };
        private float Now => CombatManager.Instance.NativeCombatTime;
        public int PendingProjectileEventCount => _activeProjectiles.Count;

        public static int NativeTickIndexFromTime(float time)
        {
            if (time <= 0f) return 0;
            return Mathf.Max(0, Mathf.FloorToInt((time / NATIVE_UPDATE_TICK) + 0.0001f));
        }

        public static int NativeProjectileFlightTicks(float distance, float speed)
        {
            int stepFixed8 = Mathf.Max(1, Mathf.FloorToInt(Mathf.Max(1f, speed) * 256f / 30f));
            int distanceFixed8 = Mathf.Max(0, Mathf.CeilToInt(Mathf.Max(0f, distance) * 256f));
            return Math.Max(1, Mathf.CeilToInt(distanceFixed8 / (float)stepFixed8 - 0.0001f));
        }

        public static float NativeProjectileFlightSeconds(float distance, float speed)
        {
            return NativeProjectileFlightTicks(distance, speed) * NATIVE_UPDATE_TICK;
        }

        public static int NativeProjectileImpactDelayTicks(float distance, float speed)
        {
            return NativeProjectileFlightTicks(distance, speed);
        }

        public static float NativeProjectileImpactDelaySeconds(float distance, float speed)
        {
            return NativeProjectileImpactDelayTicks(distance, speed) * NATIVE_UPDATE_TICK;
        }

        private static int NativeDrainTickFromTime(float time)
        {
            if (time <= 0f) return 0;
            return Mathf.Max(0, Mathf.FloorToInt((time / NATIVE_UPDATE_TICK) + 0.0001f));
        }

        public static float NativeProjectileStepDistance(float speed)
        {
            int stepFixed8 = Mathf.Max(1, Mathf.FloorToInt(Mathf.Max(1f, speed) * 256f / 30f));
            return stepFixed8 / 256f;
        }

        public static float NativeProjectileInitialDistance(float speed, float maxDistance)
        {
            if (maxDistance <= 0f) return 0f;
            return Mathf.Min(maxDistance, NativeProjectileStepDistance(speed));
        }

        private static NativeWeaponDamageInput CreatePlayerNativeWeaponDamageInput(MersenneTwister rng, PlayerState state, Monster monster, string source)
        {
            int attackerLevel = Math.Max(0, state?.Level ?? 1);
            int defenderLevel = Math.Max(0, monster?.Level ?? attackerLevel);
            return new NativeWeaponDamageInput
            {
                Rng = rng,
                Source = source,
                AttackerLevel = attackerLevel,
                DefenderLevel = defenderLevel,
                AttackRating = DamageComputer.ResolveNativeAvatarAttackRating(state),
                DefenseRating = DamageComputer.ResolveNativeMonsterDefenseRating(monster),
                BlockChance = 0,
                DamageLevel = DamageComputer.ResolveNativeLevelDamageBonus(state),
                DamageBonus = DamageComputer.ResolveNativeWeaponDamageBonus(state),
                DamageMod = DamageComputer.ResolveNativeDamageMod(state),
                WeaponDamageF32 = DamageComputer.GetWeaponBaseDamageF32(state),
                WeaponVolatilityF32 = DamageComputer.GetWeaponVolatilityF32(state),
                CritThreshold = DamageComputer.ResolveNativeCriticalThreshold(state, monster),
                CritDamagePercent = DamageComputer.ResolveNativeCriticalDamagePercent(state)
            };
        }

        private static NativeWeaponDamageInput CloneNativeWeaponDamageInput(NativeWeaponDamageInput input, MersenneTwister rng, string source)
        {
            if (input == null) return null;
            return new NativeWeaponDamageInput
            {
                Rng = rng,
                Source = source,
                AttackerLevel = input.AttackerLevel,
                DefenderLevel = input.DefenderLevel,
                AttackRating = input.AttackRating,
                DefenseRating = input.DefenseRating,
                BlockChance = input.BlockChance,
                DamageLevel = input.DamageLevel,
                DamageBonus = input.DamageBonus,
                DamageMod = input.DamageMod,
                WeaponDamageF32 = input.WeaponDamageF32,
                WeaponVolatilityF32 = input.WeaponVolatilityF32,
                CritThreshold = input.CritThreshold,
                CritDamagePercent = input.CritDamagePercent
            };
        }

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
                if (cycle.IsActive && canStartNow)
                {
                    if (IsNativeRangedCycle(cycle))
                    {
                        Debug.LogError($"[WEAPON-CYCLE] {connKey} redundant ranged UseTarget on {monster.Name} activeTick={cycle.TickCounter} pendingRepeat={cycle.PendingRepeatUses} native=UseTarget::IsRedundant");
                        return;
                    }
                    QueueNativeRepeatUse(connKey, cycle, monster);
                }
                if (canStartNow && cycle.AwaitingContact)
                {
                    cycle.ServerApproachOnly = false;
                    float now = Now;
                    if (!IsNativeUseReady(cycle, now))
                    {
                        if (IsNativeRangedCycle(cycle))
                        {
                            Debug.LogError($"[WEAPON-CYCLE] {connKey} redundant ranged UseTarget while awaiting contact on {monster.Name} nextIn={cycle.NextUseTime - now:F2} native=UseTarget::IsRedundant");
                        }
                        else
                        {
                            QueueNativeRepeatUse(connKey, cycle, monster);
                        }
                        cycle.IsActive = false;
                        cycle.AwaitingContact = true;
                        cycle.LastTickTime = now;
                        Debug.LogError($"[WEAPON-CYCLE] {connKey} → cooldown hold on {monster.Name} nextIn={cycle.NextUseTime - now:F2}");
                        return;
                    }
                    BeginCycle(connKey, cycle, monster, targetId, now);
                    Debug.LogError($"[WEAPON-CYCLE] {connKey} → CONTACT cycle on {monster.Name} dist={distance:F1} range={allowedRange:F1}");
                }
                else
                {
                    string mode = cycle.AwaitingContact ? "approach" : "continuation swing";
                    Debug.LogError($"[WEAPON-CYCLE] {connKey} → {mode} on {monster.Name} pendingRepeat={cycle.PendingRepeatUses}");
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
            cycle.PendingRepeatUses = 0;
            cycle.Distance = distance;
            cycle.ContactRange = allowedRange;
            cycle.ServerApproachOnly = !canStartNow;
            cycle.ContactHoldLogged = false;
            cycle.LastTickTime = Now;
            cycle.CycleStartTime = 0f;

            if (canStartNow)
            {
                float now = Now;
                if (!IsNativeUseReady(cycle, now))
                {
                    cycle.IsActive = false;
                    cycle.AwaitingContact = true;
                    cycle.ServerApproachOnly = false;
                    cycle.LastTickTime = now;
                    Debug.LogError($"[WEAPON-CYCLE] {connKey} → cooldown hold on {monster.Name} (ID:{targetId}) nextIn={cycle.NextUseTime - now:F2}");
                    return;
                }
                BeginCycle(connKey, cycle, monster, targetId, now);
                Debug.LogError($"[WEAPON-CYCLE] {connKey} → NEW cycle on {monster.Name} (ID:{targetId})");
            }
            else
            {
                cycle.IsActive = false;
                cycle.AwaitingContact = true;
                Debug.LogError($"[WEAPON-CYCLE] {connKey} → APPROACH intent on {monster.Name} (ID:{targetId}) dist={distance:F1} range={allowedRange:F1}");
            }
        }

        private void QueueNativeRepeatUse(string connKey, WeaponCycle cycle, Monster monster)
        {
            if (cycle == null || monster == null || !monster.IsAlive) return;
            int before = cycle.PendingRepeatUses;
            cycle.PendingRepeatUses = Math.Min(MAX_PENDING_REPEAT_USES, cycle.PendingRepeatUses + 1);
            bool projectile = IsNativeProjectileRangedCycle(cycle);
            if (cycle.PendingRepeatUses != before)
                Debug.LogError($"[WEAPON-CYCLE] {connKey} queued repeat UseTarget on {monster.Name} pending={cycle.PendingRepeatUses} projectile={projectile}");
            else
                Debug.LogError($"[WEAPON-CYCLE] {connKey} repeat UseTarget queue saturated on {monster.Name} pending={cycle.PendingRepeatUses} projectile={projectile}");
        }

        public Monster GetActiveTarget(string playerKey)
        {
            if (_activeCycles.TryGetValue(playerKey, out var cycle) && (cycle.IsActive || cycle.AwaitingContact))
                return cycle.Monster;
            return null;
        }

        public void TickAll(MersenneTwister rng)
        {
            TickAll(rng, Now);
        }

        public void TickAll(MersenneTwister rng, float tickNow)
        {
            foreach (var kvp in _activeCycles)
            {
                AdvanceCycleToNow(kvp.Key, kvp.Value, rng, tickNow);
            }
        }

        public void TickPlayerEntity(uint playerEntityId, MersenneTwister rng, float tickNow)
        {
            foreach (var kvp in _activeCycles)
            {
                var cycle = kvp.Value;
                if (cycle?.Connection?.Avatar == null || cycle.Connection.Avatar.Id != playerEntityId)
                    continue;
                AdvanceCycleToNow(kvp.Key, cycle, rng, tickNow);
                return;
            }
        }

        private void AdvanceCycleToNow(string connKey, WeaponCycle cycle, MersenneTwister rng, float now)
        {
            if (cycle == null) return;
            if (!cycle.IsActive && !cycle.AwaitingContact) return;
            float interval = GetCycleTickInterval(cycle);
            if (cycle.LastTickTime > 0f && now - cycle.LastTickTime + 0.0001f < interval) return;
            float before = cycle.LastTickTime;
            bool wasActive = cycle.IsActive;
            bool wasAwaiting = cycle.AwaitingContact;
            TickCycle(connKey, cycle, rng, now);
            if (before > 0f && cycle.LastTickTime > 0f && now - before > interval + 0.0001f)
            {
                Debug.LogError($"[NATIVE-COMBAT-CLOCK] playerWeaponCycle skippedCatchUp conn={connKey} active={wasActive} awaiting={wasAwaiting} last={before:F3}->{cycle.LastTickTime:F3} now={now:F3} interval={interval:F3}");
            }
        }

        public void FlushPlayerEntityBeforeSynch(uint playerEntityId, MersenneTwister rng, float now, string source)
        {
            if (playerEntityId == 0) return;
            foreach (var kvp in _activeCycles)
            {
                var cycle = kvp.Value;
                if (cycle?.Connection?.Avatar == null || cycle.Connection.Avatar.Id != playerEntityId)
                    continue;
                int beforeTick = cycle.TickCounter;
                uint beforeHP = cycle.Monster != null ? CombatManager.Instance.PeekMonsterCurrentHPWire(cycle.Monster) : 0u;
                uint afterHP = cycle.Monster != null ? CombatManager.Instance.PeekMonsterCurrentHPWire(cycle.Monster) : beforeHP;
                Debug.LogError($"[PRE-SUFFIX-DUE-DRAIN] source={source ?? "unknown"} player={playerEntityId} weaponCycle active={cycle.IsActive} awaiting={cycle.AwaitingContact} tick={beforeTick}->{cycle.TickCounter} target={cycle.Monster?.EntityId ?? 0} hp={beforeHP}->{afterHP} nativeNow={now:F3} projectileDrain=0/0 globalPending={_activeProjectiles.Count} phase=validation-readonly");
                return;
            }
        }

        public WeaponCycleFlushResult FlushMonsterEntityBeforeSynch(uint monsterEntityId, MersenneTwister rng, float now, string source)
        {
            var result = new WeaponCycleFlushResult
            {
                TargetEntityId = monsterEntityId,
                PendingBefore = _activeProjectiles.Count
            };
            if (monsterEntityId == 0)
            {
                result.PendingAfter = _activeProjectiles.Count;
                return result;
            }

            Monster monster = CombatManager.Instance.GetMonster(monsterEntityId);
            result.BeforeHPWire = monster != null ? CombatManager.Instance.PeekMonsterCurrentHPWire(monster) : 0u;

            foreach (var kvp in _activeCycles)
            {
                var cycle = kvp.Value;
                if (cycle?.Monster == null || cycle.Monster.EntityId != monsterEntityId)
                    continue;

                result.HadTargetCycle = true;
                int beforeTick = cycle.TickCounter;
                uint beforeHP = CombatManager.Instance.PeekMonsterCurrentHPWire(cycle.Monster);
                uint afterHP = CombatManager.Instance.PeekMonsterCurrentHPWire(cycle.Monster);
                result.CycleTicks += Math.Max(0, cycle.TickCounter - beforeTick);
                Debug.LogError($"[PRE-SUFFIX-DUE-DRAIN] source={source ?? "unknown"} monster={monsterEntityId} weaponCycle player={(cycle.Connection?.Avatar != null ? cycle.Connection.Avatar.Id : 0)} active={cycle.IsActive} awaiting={cycle.AwaitingContact} tick={beforeTick}->{cycle.TickCounter} hp={beforeHP}->{afterHP} nativeNow={now:F3} phase=validation-readonly");
            }

            result.PendingAfter = _activeProjectiles.Count;
            monster = CombatManager.Instance.GetMonster(monsterEntityId);
            result.AfterHPWire = monster != null ? CombatManager.Instance.PeekMonsterCurrentHPWire(monster) : result.BeforeHPWire;
            return result;
        }

        public NativeDueDrainSummary TickProjectileEntityPhase(MersenneTwister rng, float tickNow, string source = null)
        {
            var summary = UpdateActiveProjectileSubEntities(rng, tickNow, source ?? "ProjectileEntityUpdate", null);
            if (summary.Drained > 0 || summary.Stopped)
            {
                int nowTick = NativeDrainTickFromTime(tickNow);
                Debug.LogError($"[PROJECTILE-ENTITY] source={source ?? "unknown"} now={tickNow:F3} nowTick={nowTick} drained={summary.Drained} pending={summary.PendingBefore}->{summary.PendingAfter} stopped={summary.Stopped} nextDueTick={summary.NextDueTick} nextDue={summary.NextDueTime:F3}");
            }
            return summary;
        }

        private float GetCycleTickInterval(WeaponCycle cycle)
        {
            return NATIVE_UPDATE_TICK;
        }

        private int GetNativeSpeedField(WeaponCycle cycle)
        {
            float speed = cycle?.PlayerState != null && cycle.PlayerState.WeaponSpeed > 0f ? cycle.PlayerState.WeaponSpeed : 105f;
            float speedPct = DamageComputer.ResolveNativeWeaponAttackSpeedPct(cycle?.PlayerState);
            float scale = 1f + (speedPct / 100f);
            if (scale < 0.05f) scale = 0.05f;
            speed *= scale;
            if (speed <= 1f) speed = 105f;
            int field = Mathf.RoundToInt(speed);
            return Math.Max(1, field);
        }

        private int GetNativeTickPosition(WeaponCycle cycle, int defaultPosition)
        {
            return Math.Max(1, (defaultPosition * 100) / GetNativeSpeedField(cycle));
        }

        private int GetNativeCycleTickCount(WeaponCycle cycle, int defaultPosition)
        {
            return Math.Max(1, (defaultPosition * 100) / GetNativeSpeedField(cycle));
        }

        private int GetNativeCycleTicks(WeaponCycle cycle)
        {
            int totalFrames = cycle != null && cycle.AttackTotalFrames > 0 ? cycle.AttackTotalFrames : DEFAULT_TOTAL_TICKS;
            return GetNativeCycleTickCount(cycle, totalFrames);
        }

        private int GetNativeHitTick(WeaponCycle cycle)
        {
            int hitFrame = cycle != null && cycle.AttackHitFrame > 0 ? cycle.AttackHitFrame : DEFAULT_HIT_POSITION;
            return GetNativeTickPosition(cycle, hitFrame);
        }

        private int GetNativeSoundTick(WeaponCycle cycle)
        {
            int soundFrame = cycle != null && cycle.AttackSoundFrame > 0 ? cycle.AttackSoundFrame : DEFAULT_SOUND_POSITION;
            return GetNativeTickPosition(cycle, soundFrame);
        }

        private int GetNativeHitEventTick(WeaponCycle cycle)
        {
            int tick = GetNativeHitTick(cycle);
            return IsNativeRangedCycle(cycle) ? Math.Min(GetNativeCycleTicks(cycle), tick + 1) : tick;
        }

        private int GetNativeSoundEventTick(WeaponCycle cycle)
        {
            int tick = GetNativeSoundTick(cycle);
            return IsNativeRangedCycle(cycle) ? Math.Min(GetNativeCycleTicks(cycle), tick + 1) : tick;
        }

        private float GetNativeCooldownSeconds(WeaponCycle cycle)
        {
            int ticks = GetNativeCooldownTicks(cycle);
            return ticks * NATIVE_UPDATE_TICK;
        }

        private int GetNativeCooldownTicks(WeaponCycle cycle)
        {
            if (IsNativeRangedCycle(cycle))
                return Math.Max(1, GetNativeCycleTicks(cycle) * Math.Max(1, cycle.PlayerState.WeaponBurstCount));
            return DamageComputer.ResolveNativeBasicAttackCooldownTicks(cycle?.PlayerState);
        }

        private bool IsNativeUseReady(WeaponCycle cycle, float now)
        {
            return cycle == null || cycle.NextUseTime <= 0f || now + 0.0001f >= cycle.NextUseTime;
        }

        private void BeginCycle(string connKey, WeaponCycle cycle, Monster monster, ushort targetId)
        {
            BeginCycle(connKey, cycle, monster, targetId, Now);
        }

        private void BeginCycle(string connKey, WeaponCycle cycle, Monster monster, ushort targetId, float now)
        {
            cycle.IsActive = true;
            cycle.AwaitingContact = false;
            cycle.ServerApproachOnly = false;
            cycle.ContactHoldLogged = false;
            cycle.TargetId = targetId;
            cycle.Monster = monster;
            cycle.TickCounter = 0;
            ResetSwingRngState(cycle);
            cycle.LastTickTime = now;
            cycle.CycleStartTime = now;
            ConsumeNativeUseRng(connKey, cycle, CombatManager.Instance.RoomRng);
            ResolveNativeAttackFrames(cycle);
            float cooldownSeconds = GetNativeCooldownSeconds(cycle);
            cycle.NextUseTime = now + cooldownSeconds;
            Debug.LogError($"[WEAPON-CYCLE-RATE] {connKey} anim={cycle.AttackAnimationId} frames total={cycle.AttackTotalFrames} hit={cycle.AttackHitFrame} sound={cycle.AttackSoundFrame} speed={GetNativeSpeedField(cycle)} speedPct={DamageComputer.ResolveNativeWeaponAttackSpeedPct(cycle.PlayerState):F2} cooldownTicks={GetNativeCooldownTicks(cycle)} cooldown={cooldownSeconds:F3}s readyAt={cycle.NextUseTime:F3} class={cycle.PlayerState?.WeaponClass ?? "unknown"} category={cycle.PlayerState?.WeaponCategory ?? "unknown"} useProjectile={cycle.PlayerState?.WeaponUsesProjectile ?? false} burst={cycle.PlayerState?.WeaponBurstCount ?? 1}");
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
            cycle.AttackSoundRepeatRaw = 0;
            cycle.ImpactSoundRaw = 0;
        }

        private void ResolveNativeAttackFrames(WeaponCycle cycle)
        {
            if (cycle == null)
                return;

            cycle.AttackTotalFrames = DEFAULT_TOTAL_TICKS;
            cycle.AttackHitFrame = DEFAULT_HIT_POSITION;
            cycle.AttackSoundFrame = DEFAULT_SOUND_POSITION;
            cycle.AttackAnimationId = ResolveNativeAttackAnimationId(cycle);

            var animations = ResolvePlayerAnimationList();
            if (animations?.AnonymousChildren == null)
                return;

            int animationId = cycle.AttackAnimationId;
            foreach (var animation in animations.AnonymousChildren)
            {
                if (animation.GetInt("ID", 0) != animationId)
                    continue;

                cycle.AttackTotalFrames = Math.Max(1, animation.GetInt("NumFrames", DEFAULT_TOTAL_TICKS));
                cycle.AttackHitFrame = Math.Max(1, animation.GetInt("TriggerTime", DEFAULT_HIT_POSITION));
                cycle.AttackSoundFrame = Math.Max(1, animation.GetInt("SoundTriggerTime", DEFAULT_SOUND_POSITION));
                return;
            }
        }

        private GCNode ResolvePlayerAnimationList()
        {
            var db = GCDatabase.Instance;
            if (db == null) return null;

            foreach (string path in PLAYER_ANIMATION_LIST_PATHS)
            {
                var animations = db.ResolveWithInheritance(path);
                if (animations?.AnonymousChildren != null && animations.AnonymousChildren.Count > 0)
                    return animations;
            }

            return null;
        }

        private int ResolveNativeAttackAnimationId(WeaponCycle cycle)
        {
            string weaponClass = cycle?.PlayerState?.WeaponClass ?? string.Empty;
            if (cycle?.PlayerState != null && DamageComputer.IsNativeRangedWeapon(cycle.PlayerState))
            {
                string weaponCategory = cycle.PlayerState.WeaponCategory ?? string.Empty;
                int selector = 10;
                if (ContainsIgnoreCase(weaponCategory, "CANNON") || ContainsIgnoreCase(weaponClass, "CANNON"))
                    return 1310;
                if (ContainsIgnoreCase(weaponCategory, "1H") ||
                    ContainsIgnoreCase(weaponClass, "1HRANGED") ||
                    ContainsIgnoreCase(weaponClass, "1HCROSSBOW") ||
                    ContainsIgnoreCase(weaponClass, "1HGUN"))
                    return 900 + selector;
                return 300 + selector;
            }

            int baseId;
            if (weaponClass.Equals("HTH", StringComparison.OrdinalIgnoreCase))
                baseId = 110;
            else if (weaponClass.Equals("2HMELEE", StringComparison.OrdinalIgnoreCase))
                baseId = 610;
            else if (weaponClass.Equals("POLEARM", StringComparison.OrdinalIgnoreCase))
                baseId = 810;
            else
                baseId = 510;

            int variant = cycle?.AttackAnimationIndex ?? 0;
            return baseId + variant;
        }

        private static bool ContainsIgnoreCase(string value, string token)
        {
            return !string.IsNullOrEmpty(value) &&
                !string.IsNullOrEmpty(token) &&
                value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsNativeRangedCycle(WeaponCycle cycle)
        {
            return cycle?.PlayerState != null && DamageComputer.IsNativeRangedWeapon(cycle.PlayerState);
        }

        private static bool IsNativeProjectileRangedCycle(WeaponCycle cycle)
        {
            return IsNativeRangedCycle(cycle) &&
                cycle.PlayerState.WeaponUsesProjectile &&
                cycle.PlayerState.WeaponProjectileSpeed > 0f &&
                cycle.PlayerState.WeaponProjectileSize > 0f;
        }

        private void ConsumeNativeUseRng(string connKey, WeaponCycle cycle, MersenneTwister rng)
        {
            if (cycle == null || rng == null || cycle.UseRngConsumed) return;
            if (cycle.PlayerState != null && DamageComputer.IsNativeRangedWeapon(cycle.PlayerState))
            {
                cycle.UseRaw = 0;
                cycle.UseRngConsumed = true;
                Debug.LogError($"[RNG-COMBAT] {connKey} RangedWeapon::use no room RNG class={cycle.PlayerState.WeaponClass} rngPos={rng.CallsSinceReseed}");
                return;
            }
            Debug.LogError($"[RNG-AUDIT] before-player-use seed=0x{rng.LastSeed:X8} rngPos={rng.CallsSinceReseed} {WanderSimulator.Instance.DescribeSchedule()}");
            cycle.UseRaw = rng.Generate();
            cycle.UseRngConsumed = true;
            uint previousAnim = cycle.AttackAnimationIndex;
            cycle.AttackAnimationIndex = (byte)(((cycle.UseRaw & 1u) + previousAnim + 1u) % 3u);
            Debug.LogError($"[RNG-COMBAT] {connKey} MeleeWeapon::use useRaw=0x{cycle.UseRaw:X8} animBit={cycle.UseRaw & 1u} anim={previousAnim}->{cycle.AttackAnimationIndex} rngPos={rng.CallsSinceReseed}");
        }

        private void TickCycle(string connKey, WeaponCycle cycle, MersenneTwister rng, float now)
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
                    cycle.LastTickTime = now;
                    return;
                }
                cycle.Distance = dist;
                cycle.ContactRange = range;
                if (!IsNativeUseReady(cycle, now))
                {
                    cycle.LastTickTime = now;
                    return;
                }
                bool wasServerApproachOnly = cycle.ServerApproachOnly;
                if (wasServerApproachOnly)
                {
                    uint avatarId = cycle.Connection?.Avatar != null ? (uint)cycle.Connection.Avatar.Id : 0u;
                    if (avatarId != 0)
                    {
                        CombatManager.Instance.SetPlayerActiveClientAttack(avatarId, true, cycle.Monster.EntityId);
                        CombatManager.Instance.EngageMonsterFromClientAction(cycle.Monster, avatarId);
                    }
                }
                BeginCycle(connKey, cycle, cycle.Monster, cycle.TargetId, now);
                string contactMode = wasServerApproachOnly ? "CONTACT cycle from approach" : "CONTACT cycle";
                Debug.LogError($"[WEAPON-CYCLE] {connKey} → {contactMode} on {cycle.Monster.Name} dist={dist:F1} range={range:F1}");
            }

            if (!cycle.IsActive) return;

            float tickInterval = GetCycleTickInterval(cycle);
            if (cycle.LastTickTime > 0f && now - cycle.LastTickTime + 0.0001f < tickInterval) return;
            float tickNow = now;
            if (cycle.LastTickTime > 0f)
            {
                cycle.LastTickTime += tickInterval;
                tickNow = cycle.LastTickTime;
            }
            else
            {
                cycle.LastTickTime = tickNow;
            }
            cycle.TickCounter++;

                if (cycle.TickCounter == GetNativeSoundEventTick(cycle) && !cycle.ProcFired)
                {
                    cycle.ProcFired = true;
                    cycle.AttackSoundFired = true;
                    cycle.AttackSoundGateRaw = NativeRandomStreams.GenerateGlobalSound();
                    cycle.AttackSoundSelectRaw = 0;
                    cycle.AttackSoundRepeatRaw = (cycle.AttackSoundGateRaw & 3u) == 0 ? cycle.AttackSoundGateRaw : 0;
                    string roomRngPos = rng != null ? rng.CallsSinceReseed.ToString() : "n/a";
                    Debug.LogError($"[WEAPON-CYCLE] {connKey} SOUND tick={cycle.TickCounter} nativeGlobalSoundRng=True soundGate=0x{cycle.AttackSoundGateRaw:X8} repeat={(cycle.AttackSoundRepeatRaw != 0)} globalSoundRngPos={NativeRandomStreams.GlobalSoundCalls} roomRngPos={roomRngPos}");
                }

                if (cycle.TickCounter == GetNativeHitEventTick(cycle) && !cycle.HitFired)
                {
                    cycle.HitFired = true;
                    cycle.SwingCount++;

                    ConsumeNativeUseRng(connKey, cycle, rng);
                    uint useRaw = cycle.UseRaw;

                    if (IsNativeProjectileRangedCycle(cycle))
                    {
                        QueueNativeProjectileHit(connKey, cycle, tickNow);
                    }
                    else
                    {
                    NativeWeaponDamageInput damageInput = CreatePlayerNativeWeaponDamageInput(rng, cycle.PlayerState, cycle.Monster, "WeaponCycle");
                    Debug.LogError($"[RNG-COMBAT] melee swing#{cycle.SwingCount} START seed=0x{rng.LastSeed:X8} rngPosBefore={rng.CallsSinceReseed}");
                    NativeWeaponDamageResult damageResult = DamageComputer.ResolveNativeWeaponDamage(damageInput);
                    Debug.LogError($"[RNG-COMBAT] melee swing#{cycle.SwingCount} END rngPosAfter={rng.CallsSinceReseed}");
                    uint hitRaw = damageResult.HitRaw;
                    int hitRoll = damageResult.HitRoll;
                    uint blockRaw = damageResult.BlockRaw;
                    int blockRoll = damageResult.BlockRoll;
                    int attackRating = damageResult.AttackRating;
                    int defenseRating = damageResult.DefenseRating;
                    int attackerLevel = damageResult.AttackerLevel;
                    int hitDefenderLevel = damageResult.DefenderLevel;
                    int hitChanceF32 = damageResult.HitThreshold;
                    bool isHit = damageResult.IsHit;
                    bool isBlocked = damageResult.IsBlocked;

                    Debug.LogError($"[RNG-COMBAT] swing#{cycle.SwingCount} seed=0x{rng.LastSeed:X8} rngPos={rng.CallsSinceReseed} useRaw=0x{useRaw:X8} soundSelect=0x{cycle.AttackSoundSelectRaw:X8} soundGate=0x{cycle.AttackSoundGateRaw:X8} soundRepeat=0x{cycle.AttackSoundRepeatRaw:X8} hitRaw=0x{hitRaw:X8} blockRaw=0x{blockRaw:X8} hitRoll={hitRoll} threshold={hitChanceF32} ar={attackRating} dr={defenseRating} levels={attackerLevel}->{hitDefenderLevel} isHit={isHit}");

                    int damage = 0;
                    uint damageRaw = 0;
                    uint damageWire = 0;
                    int weaponDmg = 0;
                    int volatility = 0;
                    int levelDamageBonus = 0;
                    int damageBonus = 0;
                    int damageMod = 0;
                    int minDmg = 0;
                    int maxDmg = 0;
                    int critThreshold = 0;
                    int critPercent = 0;
                    string weaponClass = cycle.PlayerState?.WeaponClass ?? "unknown";
                    string statSource = DamageComputer.ResolveNativeWeaponStatSource(cycle.PlayerState);
                    bool isCritical = false;
                    uint oldHPWire = CombatManager.Instance.PeekMonsterCurrentHPWire(cycle.Monster);
                    uint newHPWire = oldHPWire;
                    bool applied = false;
                    bool killed = false;
                    int actualDamage = 0;
                    int appliedDamage = 0;
                    uint damageProcRaw = 0;
                    uint effectRaw = 0;
                    uint impactSoundRaw = 0;
                    weaponDmg = damageInput.WeaponDamageF32;
                    volatility = damageInput.WeaponVolatilityF32;
                    levelDamageBonus = damageInput.DamageLevel;
                    damageBonus = damageInput.DamageBonus;
                    damageMod = damageInput.DamageMod;
                    minDmg = damageResult.MinDamageF32;
                    maxDmg = damageResult.MaxDamageF32;
                    critThreshold = damageInput.CritThreshold;
                    critPercent = damageInput.CritDamagePercent;
                    isCritical = damageResult.IsCritical;
                    if (isHit && !isBlocked)
                    {
                        damageRaw = damageResult.DamageRaw;
                        damage = damageResult.DamageF32;

                        Debug.LogError($"[RNG-COMBAT] dmgRaw=0x{damageRaw:X8} weaponClass={weaponClass} statSource={statSource} dmg={damage / 256} range=[{minDmg / 256},{maxDmg / 256}] rangeWire=[{minDmg},{maxDmg}] rolledWire={damage} bonus={damageBonus} damageMod={damageMod} crit={isCritical} critThreshold={critThreshold} critPct={critPercent} str={cycle.PlayerState.Strength} agi={cycle.PlayerState.Agility} level={cycle.PlayerState.Level} weaponLevel={cycle.PlayerState.WeaponLevel} levelBonus={levelDamageBonus} weapon={DamageComputer.ToFloat(weaponDmg):F2} vol={DamageComputer.ToFloat(volatility):F2} roomRng={rng.CallsSinceReseed}");

                        damageWire = damageResult.DamageWire;
                        actualDamage = (int)((damageWire + 255) / 256);
                        uint playerEntityId = cycle.Connection?.Avatar != null ? (uint)cycle.Connection.Avatar.Id : 0u;

                        CombatManager.Instance.NotifyMonsterDamagedByPlayer(cycle.Monster, playerEntityId, "damage");
                        float nativeHitTime = cycle.CycleStartTime > 0f
                            ? cycle.CycleStartTime + (GetCycleTickInterval(cycle) * GetNativeHitEventTick(cycle))
                            : tickNow;
                        applied = CombatManager.Instance.ApplyNativePlayerDamageToMonsterWire(cycle.Monster, damageWire, $"WeaponCycle-HIT swing={cycle.SwingCount}", out oldHPWire, out newHPWire, out killed, nativeHitTime);
                        effectRaw = 0;
                        cycle.ImpactSoundRaw = impactSoundRaw;
                        appliedDamage = (int)((oldHPWire > newHPWire ? oldHPWire - newHPWire : 0) + 255) / 256;

                        // C4 skeleton (plan vivid-marinating-pixel, 2026-05-28).
                        // Client behavior (audit §1.4 of WORK/AUDIT_HP_VALIDATION_PVE_VS_PVP_2026-05-28.md
                        // and Unit::onApplyDamage @ 0x0050BE50): per non-killing hit, roll
                        // random%100; if 0 (1% chance), apply CC effect by damage-ratio:
                        //   ≥50% → KnockDown, ≥25% → KnockBack, ≥10% → Stun, <10% → none.
                        // Each effect gated by CheckStunResist on target.
                        //
                        // Skeleton: log the damage-ratio + which effect WOULD fire if RNG
                        // rolls 0. No RNG consumed (avoids room-RNG-stream desync until
                        // x32dbg verification proves the consume order). No state set,
                        // no broadcast, no resist check. Real wiring blocked on a Phase B
                        // x32dbg session to confirm RNG order matches client.
                        if (applied && !killed && cycle.Monster != null && cycle.Monster.MaxHPWire > 0)
                        {
                            long damageRatioPct = (long)damageWire * 100L / cycle.Monster.MaxHPWire;
                            string wouldFire =
                                damageRatioPct >= 50 ? "KNOCKDOWN" :
                                damageRatioPct >= 25 ? "KNOCKBACK" :
                                damageRatioPct >= 10 ? "STUN" :
                                "NONE";
                            Debug.LogError(
                                $"[CC-CHECK] player={connKey} target={cycle.Monster.Name}#{cycle.Monster.EntityId} " +
                                $"swing={cycle.SwingCount} lane=melee damageWire={damageWire} maxHPWire={cycle.Monster.MaxHPWire} " +
                                $"damageRatioPct={damageRatioPct}% wouldFire={wouldFire} " +
                                $"(skeleton — no RNG consumed; full wiring pending x32dbg parity)");
                        }

                        Debug.LogError($"[WEAPON-CYCLE] {connKey} {(isCritical ? "CRIT" : "HIT")}: {actualDamage} dmgWire={damageWire} applied={applied} appliedDamage={appliedDamage} on {cycle.Monster.Name} HP={oldHPWire}->{newHPWire} proc=0x{damageProcRaw:X8} impact=0x{impactSoundRaw:X8} effect=0x{effectRaw:X8} rngPos={rng.CallsSinceReseed} [swing #{cycle.SwingCount}]");
                        if (killed && cycle.Monster != null)
                        {
                            _completedAttacks.Enqueue(new CompletedAttack
                            {
                                ConnKey = connKey,
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
                        CombatManager.Instance.LogMonsterClientVisibleSwingNoDamage(cycle.Monster, $"WeaponCycle-{resultType} swing={cycle.SwingCount}");
                        Debug.LogError($"[WEAPON-CYCLE] {connKey} {resultType}: hitRoll={hitRoll} blockRoll={blockRoll} on {cycle.Monster.Name} [swing #{cycle.SwingCount}]");
                    }
                    string combatResult = isHit ? (isBlocked ? "BLOCK" : (isCritical ? "CRIT" : "HIT")) : "MISS";
                    Debug.LogError($"[PLAYER-HIT-DETAIL] player={connKey} target={cycle.Monster.EntityId}/{cycle.Monster.BehaviorId} seed=0x{rng.LastSeed:X8} swing={cycle.SwingCount} tick={cycle.TickCounter} rngAfter={rng.CallsSinceReseed} useRaw=0x{useRaw:X8} soundGate=0x{cycle.AttackSoundGateRaw:X8} soundSelect=0x{cycle.AttackSoundSelectRaw:X8} soundRepeat=0x{cycle.AttackSoundRepeatRaw:X8} hitRaw=0x{hitRaw:X8} blockRaw=0x{blockRaw:X8} dmgRaw=0x{damageRaw:X8} effectRaw=0x{effectRaw:X8} impact=0x{impactSoundRaw:X8} hitRoll={hitRoll} threshold={hitChanceF32} critThreshold={critThreshold} critPct={critPercent} blockRoll={blockRoll} ar={attackRating} dr={defenseRating} levels={attackerLevel}->{hitDefenderLevel} result={combatResult} weaponClass={weaponClass} statSource={statSource} weaponDamage={DamageComputer.ToFloat(weaponDmg):F4} vol={DamageComputer.ToFloat(volatility):F4} weaponLevel={cycle.PlayerState.WeaponLevel} levelBonus={levelDamageBonus} bonus={damageBonus} mod={damageMod} rangeWire=[{minDmg},{maxDmg}] rolledWire={damageWire} applied={applied} hp={oldHPWire}->{newHPWire} exact=weapon-stat-source");
                    Debug.LogError($"[COMBAT-EVENT] actor=player player={connKey} actorId={(cycle.Connection?.Avatar != null ? cycle.Connection.Avatar.Id : 0)} target=monster targetId={cycle.Monster.EntityId} behaviorId={cycle.Monster.BehaviorId} result={combatResult} damageWire={damageWire} appliedDamage={appliedDamage} hp={oldHPWire}->{newHPWire} hitRaw=0x{hitRaw:X8} hitRoll={hitRoll} threshold={hitChanceF32} blockRaw=0x{blockRaw:X8} blockRoll={blockRoll} blockChance=0 damageRaw=0x{damageRaw:X8} effectRaw=0x{effectRaw:X8} resist=0 critThreshold={critThreshold} critPct={critPercent} rngAfter={rng.CallsSinceReseed}");
                    }
                }

                if (cycle.TickCounter >= GetNativeCycleTicks(cycle))
                {
                    bool repeatQueued = cycle.PendingRepeatUses > 0 && cycle.Monster != null && cycle.Monster.IsAlive;
                    bool consumedActiveUseTarget = false;
                    cycle.TickCounter = 0;
                    ResetSwingRngState(cycle);
                    cycle.CycleStartTime = 0f;
                    cycle.LastTickTime = tickNow;
                    cycle.IsActive = false;
                    cycle.ServerApproachOnly = false;
                    cycle.ContactHoldLogged = false;
                    if (repeatQueued)
                    {
                        cycle.PendingRepeatUses--;
                        if (HasNativePlayerMeleeContact(cycle, out float repeatDist, out float repeatRange))
                        {
                            cycle.Distance = repeatDist;
                            cycle.ContactRange = repeatRange;
                            if (IsNativeUseReady(cycle, tickNow))
                            {
                                BeginCycle(connKey, cycle, cycle.Monster, cycle.TargetId, tickNow);
                                Debug.LogError($"[WEAPON-CYCLE] {connKey} → REPEAT cycle on {cycle.Monster.Name} dist={repeatDist:F1} range={repeatRange:F1} pending={cycle.PendingRepeatUses}");
                            }
                            else
                            {
                                cycle.AwaitingContact = true;
                                Debug.LogError($"[WEAPON-CYCLE] {connKey} → REPEAT cooldown hold on {cycle.Monster.Name} nextIn={cycle.NextUseTime - tickNow:F2} pending={cycle.PendingRepeatUses}");
                            }
                        }
                        else
                        {
                            cycle.Distance = repeatDist;
                            cycle.ContactRange = repeatRange;
                            cycle.AwaitingContact = true;
                            Debug.LogError($"[WEAPON-CYCLE] {connKey} → REPEAT awaiting contact on {cycle.Monster.Name} dist={repeatDist:F1} range={repeatRange:F1} pending={cycle.PendingRepeatUses}");
                        }
                        return;
                    }
                    cycle.AwaitingContact = false;
                    consumedActiveUseTarget = ClearConsumedUseTarget(cycle);
                    Debug.LogError($"[WEAPON-CYCLE] {connKey} → STOP cycle on {cycle.Monster?.Name ?? "monster"} consumedUseTarget={consumedActiveUseTarget} awaiting next UseTarget");
                }
        }


        private void QueueNativeProjectileHit(string connKey, WeaponCycle cycle, float fireTime)
        {
            if (cycle == null || cycle.Connection == null || cycle.PlayerState == null || cycle.Monster == null)
                return;

            float startX = cycle.Connection.PlayerPosX;
            float startY = cycle.Connection.PlayerPosY;
            float targetX = cycle.Monster.PosX;
            float targetY = cycle.Monster.PosY;
            float pathDistance = Distance2D(startX, startY, targetX, targetY);
            if (pathDistance <= 0.001f)
                pathDistance = 0.001f;

            float speed = Mathf.Max(1f, cycle.PlayerState.WeaponProjectileSpeed);
            int fireTick = NativeTickIndexFromTime(fireTime);
            int flightTicks = NativeProjectileFlightTicks(pathDistance, speed);
            int impactDelayTicks = NativeProjectileImpactDelayTicks(pathDistance, speed);
            int dueTick = fireTick + impactDelayTicks;
            float dueTime = dueTick * NATIVE_UPDATE_TICK;
            float delay = Mathf.Max(0f, dueTime - fireTime);
            float maxRange = cycle.PlayerState.WeaponRange > 0f
                ? Mathf.Max(cycle.PlayerState.WeaponRange, pathDistance)
                : pathDistance;
            float projectileSize = Mathf.Max(0f, cycle.PlayerState.WeaponProjectileSize);
            float maxDistance = Mathf.Max(pathDistance + projectileSize, maxRange + projectileSize);
            int maxLifetimeTicks = Math.Max(1, NativeProjectileFlightTicks(maxDistance, speed));
            float stepDistance = NativeProjectileStepDistance(speed);
            float initialDistance = NativeProjectileInitialDistance(speed, maxDistance);
            var pending = new PendingProjectileHit
            {
                Sequence = ++_nextProjectileSequence,
                ConnKey = connKey,
                Connection = cycle.Connection,
                PlayerState = cycle.PlayerState,
                Monster = cycle.Monster,
                RequestedTargetId = cycle.Monster.EntityId,
                TargetId = cycle.Monster.EntityId,
                BehaviorId = cycle.Monster.BehaviorId,
                Swing = cycle.SwingCount,
                Tick = cycle.TickCounter,
                FireTime = fireTime,
                DueTime = dueTime,
                FireNativeTick = fireTick,
                FlightTicks = flightTicks,
                ImpactDelayTicks = impactDelayTicks,
                DueNativeTick = dueTick,
                HitDistance = pathDistance,
                PathDistance = pathDistance,
                UseRaw = cycle.UseRaw,
                AttackSoundSelectRaw = cycle.AttackSoundSelectRaw,
                AttackSoundGateRaw = cycle.AttackSoundGateRaw,
                AttackSoundRepeatRaw = cycle.AttackSoundRepeatRaw,
                DamageInput = CreatePlayerNativeWeaponDamageInput(CombatManager.Instance.RoomRng, cycle.PlayerState, cycle.Monster, "RangedProjectileSnapshot"),
                StartX = startX,
                StartY = startY,
                TargetX = targetX,
                TargetY = targetY,
                WorldBlocked = false,
                ProjectileSpeed = speed,
                ProjectileSize = projectileSize,
                StepDistance = stepDistance,
                InitialDistance = initialDistance,
                CurrentDistance = initialDistance,
                MaxDistance = maxDistance,
                MaxLifetimeTicks = maxLifetimeTicks,
                LastUpdateNativeTick = fireTick,
                UpdatesCompleted = initialDistance > 0f ? 1 : 0,
                ImpactResolved = false
            };
            _activeProjectiles.Add(pending);
            Debug.LogError($"[RANGED-PROJECTILE] {connKey} create-subentity target={cycle.Monster.Name}#{cycle.Monster.EntityId} requested={cycle.Monster.Name}#{cycle.Monster.EntityId} seq={pending.Sequence} swing={cycle.SwingCount} hitFrameTick={cycle.TickCounter} fireTick={fireTick} firstUpdateTick={fireTick + 1} flightTicks={flightTicks} impactDelayTicks={impactDelayTicks} dueTick={dueTick} hitDist={pathDistance:F2} pathDist={pathDistance:F2} speed={speed:F1} step={stepDistance:F3} initPreStep={initialDistance:F3} size={projectileSize:F1} maxDist={maxDistance:F2} maxLife={maxLifetimeTicks} delay={delay:F3}s due={dueTime:F3} useProjectile=True collision=subentity-swept");
        }

        private bool TryResolveProjectileTarget(WeaponCycle cycle, float startX, float startY, float targetX, float targetY, float pathDistance, out Monster hitMonster, out float hitDistance, out bool worldBlocked)
        {
            hitMonster = null;
            hitDistance = pathDistance;
            worldBlocked = false;
            if (cycle == null || cycle.Monster == null || cycle.PlayerState == null) return false;

            string zoneName = !string.IsNullOrWhiteSpace(cycle.Monster.ZoneName)
                ? cycle.Monster.ZoneName
                : cycle.Connection?.CurrentZoneName;
            PathMap pathMap = !string.IsNullOrWhiteSpace(zoneName) ? PathMapManager.Instance.GetPathMap(zoneName) : null;
            if (pathMap != null && !pathMap.CanReachPoint(startX, startY, targetX, targetY))
                worldBlocked = true;

            float dx = targetX - startX;
            float dy = targetY - startY;
            float lenSq = dx * dx + dy * dy;
            if (lenSq <= 0.001f) return false;

            float projectileSize = Mathf.Max(0f, cycle.PlayerState.WeaponProjectileSize);
            float scanRange = Mathf.Max(pathDistance + projectileSize + 20f, cycle.ContactRange + projectileSize + 20f);
            Monster best = null;
            float bestAlong = float.MaxValue;
            foreach (var candidate in CombatManager.Instance.GetMonstersInRange(startX, startY, scanRange))
            {
                if (candidate == null || !candidate.IsAlive) continue;
                if (!string.IsNullOrWhiteSpace(zoneName) &&
                    !string.IsNullOrWhiteSpace(candidate.ZoneName) &&
                    !string.Equals(candidate.ZoneName, zoneName, StringComparison.OrdinalIgnoreCase))
                    continue;

                float cx = candidate.PosX - startX;
                float cy = candidate.PosY - startY;
                float t = Mathf.Clamp01((cx * dx + cy * dy) / lenSq);
                float along = t * pathDistance;
                if (along > pathDistance + projectileSize) continue;
                float closestX = startX + dx * t;
                float closestY = startY + dy * t;
                float miss = Distance2D(candidate.PosX, candidate.PosY, closestX, closestY);
                float radius = Mathf.Max(0f, candidate.CollisionRadius) + projectileSize;
                if (miss > radius) continue;

                bool candidateWorldBlocked = pathMap != null && !pathMap.CanReachPoint(startX, startY, closestX, closestY);

                if (along < bestAlong)
                {
                    best = candidate;
                    bestAlong = along;
                    worldBlocked = candidateWorldBlocked;
                }
            }

            if (best == null)
                return false;

            hitMonster = best;
            hitDistance = Mathf.Max(0f, bestAlong);
            return true;
        }

        private NativeDueDrainSummary ResolveDueProjectileHits(MersenneTwister rng, float now, string source)
        {
            return UpdateActiveProjectileSubEntities(rng, now, source, null);
        }

        public NativeDueDrainSummary DrainDueProjectileImpacts(MersenneTwister rng, float now, string source = null)
        {
            return UpdateActiveProjectileSubEntities(rng, now, source ?? "DrainDueProjectileImpacts", null);
        }

        public NativeDueDrainSummary DrainDueProjectileImpactsForMonster(uint monsterEntityId, MersenneTwister rng, float now, string source = null)
        {
            return UpdateActiveProjectileSubEntities(rng, now, source ?? "DrainDueProjectileImpactsForMonster", pending =>
            {
                uint pendingMonsterId = 0u;
                if (pending?.Monster != null)
                    pendingMonsterId = pending.Monster.EntityId;
                else if (pending != null)
                    pendingMonsterId = pending.TargetId;
                return monsterEntityId != 0 && pendingMonsterId == monsterEntityId;
            });
        }

        public NativeDueDrainSummary DrainDueProjectileImpactsForPlayer(uint playerEntityId, MersenneTwister rng, float now, string source = null)
        {
            return UpdateActiveProjectileSubEntities(rng, now, source ?? "DrainDueProjectileImpactsForPlayer", pending =>
            {
                uint pendingPlayerId = pending?.Connection?.Avatar != null
                    ? (uint)pending.Connection.Avatar.Id
                    : 0u;
                return playerEntityId != 0 && pendingPlayerId == playerEntityId;
            });
        }

        private NativeDueDrainSummary UpdateActiveProjectileSubEntities(MersenneTwister rng, float now, string source, Predicate<PendingProjectileHit> countPredicate)
        {
            int nowTick = NativeDrainTickFromTime(now);
            int matched = 0;
            var summary = new NativeDueDrainSummary
            {
                PendingBefore = _activeProjectiles.Count
            };

            if (_activeProjectiles.Count == 0)
            {
                summary.PendingAfter = 0;
                return summary;
            }

            if (rng == null)
            {
                summary.PendingAfter = _activeProjectiles.Count;
                summary.Stopped = true;
                var next = _activeProjectiles[0];
                summary.NextDueTick = Math.Max(0, next.LastUpdateNativeTick + 1);
                summary.NextDueTime = summary.NextDueTick * NATIVE_UPDATE_TICK;
                Debug.LogError($"[RANGED-PROJECTILE] active projectile update missing room RNG source={source ?? "unknown"} pending={summary.PendingBefore}");
                return summary;
            }

            _activeProjectiles.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));
            for (int i = 0; i < _activeProjectiles.Count;)
            {
                PendingProjectileHit pending = _activeProjectiles[i];
                if (pending == null)
                {
                    _activeProjectiles.RemoveAt(i);
                    continue;
                }

                if (nowTick <= pending.LastUpdateNativeTick)
                {
                    i++;
                    continue;
                }

                float beforeDistance = pending.CurrentDistance;
                float afterDistance = Mathf.Min(pending.MaxDistance, beforeDistance + Mathf.Max(0.001f, pending.StepDistance));
                pending.UpdatesCompleted++;
                pending.LastUpdateNativeTick = nowTick;

                if (TryResolveProjectileTargetAlongSegment(pending, beforeDistance, afterDistance, out Monster impactMonster, out float impactDistance, out bool impactWorldBlocked))
                {
                    pending.Monster = impactMonster;
                    pending.TargetId = impactMonster.EntityId;
                    pending.BehaviorId = impactMonster.BehaviorId;
                    pending.HitDistance = impactDistance;
                    pending.CurrentDistance = impactDistance;
                    pending.DueNativeTick = nowTick;
                    pending.DueTime = now;
                    pending.WorldBlocked = impactWorldBlocked;
                    pending.ImpactResolved = true;
                    if (countPredicate == null || countPredicate(pending))
                        matched++;
                    ResolveProjectileDamage(pending, rng, now);
                    _activeProjectiles.RemoveAt(i);
                    summary.Drained++;
                    continue;
                }

                pending.CurrentDistance = afterDistance;
                if (pending.CurrentDistance + 0.0001f >= pending.MaxDistance || pending.UpdatesCompleted >= pending.MaxLifetimeTicks)
                {
                    Debug.LogError($"[RANGED-PROJECTILE] {pending.ConnKey} subentity expired no-hit requested={pending.Monster?.Name ?? "monster"}#{pending.TargetId} seq={pending.Sequence} swing={pending.Swing} current={pending.CurrentDistance:F2}/{pending.MaxDistance:F2} updates={pending.UpdatesCompleted}/{pending.MaxLifetimeTicks} source={source ?? "unknown"}");
                    if (pending.Monster != null)
                        CombatManager.Instance.LogMonsterClientVisibleSwingNoDamage(pending.Monster, $"RangedProjectile-expired-no-hit swing={pending.Swing}");
                    _activeProjectiles.RemoveAt(i);
                    continue;
                }

                i++;
            }
            summary.MatchingDrained = matched;
            summary.PendingAfter = _activeProjectiles.Count;
            if (_activeProjectiles.Count > 0)
            {
                var next = _activeProjectiles[0];
                summary.NextDueTick = Math.Max(0, next.LastUpdateNativeTick + 1);
                summary.NextDueTime = summary.NextDueTick * NATIVE_UPDATE_TICK;
            }

            if (summary.Drained > 0 || summary.Stopped)
            {
                Debug.LogError($"[RANGED-PROJECTILE-DUE] source={source ?? "unknown"} now={now:F3} nowTick={nowTick} drained={summary.Drained} matching={summary.MatchingDrained} pending={summary.PendingBefore}->{summary.PendingAfter} stopped={summary.Stopped} nextDueTick={summary.NextDueTick} nextDue={summary.NextDueTime:F3} runtime=subentity-swept");
            }

            return summary;
        }

        private bool TryResolveProjectileTargetAlongSegment(PendingProjectileHit pending, float segmentStart, float segmentEnd, out Monster hitMonster, out float hitDistance, out bool worldBlocked)
        {
            hitMonster = null;
            hitDistance = segmentEnd;
            worldBlocked = pending != null && pending.WorldBlocked;
            if (pending == null || pending.PlayerState == null)
                return false;

            float dx = pending.TargetX - pending.StartX;
            float dy = pending.TargetY - pending.StartY;
            float lenSq = dx * dx + dy * dy;
            if (lenSq <= 0.001f)
                return false;

            float pathDistance = Mathf.Sqrt(lenSq);
            float dirX = dx / pathDistance;
            float dirY = dy / pathDistance;
            float projectileSize = Mathf.Max(0f, pending.ProjectileSize);
            float scanRange = Mathf.Max(segmentEnd + projectileSize + 20f, pending.HitDistance + projectileSize + 20f);
            string zoneName = !string.IsNullOrWhiteSpace(pending.Monster?.ZoneName)
                ? pending.Monster.ZoneName
                : pending.Connection?.CurrentZoneName;
            PathMap pathMap = !string.IsNullOrWhiteSpace(zoneName) ? PathMapManager.Instance.GetPathMap(zoneName) : null;

            Monster best = null;
            float bestAlong = float.MaxValue;
            float bestDistSq = float.MaxValue;
            bool bestBlocked = worldBlocked;
            foreach (var candidate in CombatManager.Instance.GetMonstersInRange(pending.StartX, pending.StartY, scanRange))
            {
                if (candidate == null || !candidate.IsAlive) continue;
                if (CombatManager.Instance.PeekMonsterCurrentHPWire(candidate) == 0) continue;
                if (!string.IsNullOrWhiteSpace(zoneName)
                    && !string.IsNullOrWhiteSpace(candidate.ZoneName)
                    && !string.Equals(candidate.ZoneName, zoneName, StringComparison.OrdinalIgnoreCase))
                    continue;

                CombatManager.Instance.TryGetMonsterWanderClientVisiblePosition(candidate, out float candidateX, out float candidateY);
                float cx = candidateX - pending.StartX;
                float cy = candidateY - pending.StartY;
                float projected = (cx * dirX) + (cy * dirY);
                float radius = Mathf.Max(0f, candidate.CollisionRadius) + projectileSize;
                if (projected + radius < segmentStart || projected - radius > segmentEnd)
                    continue;

                float closestAlong = Mathf.Clamp(projected, segmentStart, segmentEnd);
                float closestX = pending.StartX + (dirX * closestAlong);
                float closestY = pending.StartY + (dirY * closestAlong);
                float missX = candidateX - closestX;
                float missY = candidateY - closestY;
                float distSq = (missX * missX) + (missY * missY);
                float radiusSq = radius * radius;
                if (distSq > radiusSq)
                    continue;

                float entryOffset = Mathf.Sqrt(Mathf.Max(0f, radiusSq - distSq));
                float impactAlong = Mathf.Clamp(projected - entryOffset, segmentStart, segmentEnd);
                bool candidateWorldBlocked = pathMap != null && !pathMap.CanReachPoint(pending.StartX, pending.StartY, pending.StartX + (dirX * impactAlong), pending.StartY + (dirY * impactAlong));
                if (impactAlong < bestAlong || (Mathf.Abs(impactAlong - bestAlong) <= 0.0001f && distSq < bestDistSq))
                {
                    best = candidate;
                    bestAlong = impactAlong;
                    bestDistSq = distSq;
                    bestBlocked = candidateWorldBlocked;
                }
            }

            if (best == null)
                return false;

            CombatManager.Instance.SyncMonsterWanderClientVisiblePosition(best, "ProjectileChecker-subentity-hit");
            hitMonster = best;
            hitDistance = Mathf.Max(0f, bestAlong);
            worldBlocked = bestBlocked;
            Debug.LogError($"[RANGED-PROJECTILE] {pending.ConnKey} subentity impact seq={pending.Sequence} target={best.Name}#{best.EntityId} swing={pending.Swing} segment={segmentStart:F2}->{segmentEnd:F2} hitDist={hitDistance:F2} radius={(Mathf.Max(0f, best.CollisionRadius) + pending.ProjectileSize):F2} worldBlocked={worldBlocked}");
            return true;
        }

        private void ResolveProjectileDamage(PendingProjectileHit pending, MersenneTwister rng, float now)
        {
            if (pending == null || pending.Monster == null || pending.PlayerState == null)
                return;

            bool sameTarget;
            if (!pending.ImpactResolved)
            {
                if (!TryResolveProjectileTargetAtImpact(pending, out Monster impactMonster, out float impactDistance, out bool impactWorldBlocked))
                {
                    Debug.LogError($"[RANGED-PROJECTILE] {pending.ConnKey} impact no-hit requested={pending.Monster?.Name ?? "monster"}#{pending.TargetId} swing={pending.Swing} path=({pending.StartX:F1},{pending.StartY:F1})->({pending.TargetX:F1},{pending.TargetY:F1}) hitDist={pending.HitDistance:F2} dueTick={pending.DueNativeTick}");
                    CombatManager.Instance.LogMonsterClientVisibleSwingNoDamage(pending.Monster, $"RangedProjectile-impact-no-hit swing={pending.Swing}");
                    return;
                }

                sameTarget = pending.RequestedTargetId == 0 || impactMonster.EntityId == pending.RequestedTargetId;
                pending.Monster = impactMonster;
                pending.TargetId = impactMonster.EntityId;
                pending.BehaviorId = impactMonster.BehaviorId;
                pending.HitDistance = impactDistance;
                pending.WorldBlocked = impactWorldBlocked;
            }
            else
            {
                sameTarget = pending.RequestedTargetId == 0 || pending.Monster.EntityId == pending.RequestedTargetId;
            }


            NativeWeaponDamageInput damageInput = sameTarget
                ? CloneNativeWeaponDamageInput(pending.DamageInput, rng, "RangedProjectile")
                : null;
            if (damageInput == null)
                damageInput = CreatePlayerNativeWeaponDamageInput(rng, pending.PlayerState, pending.Monster, "RangedProjectile");
            Debug.LogError($"[RNG-COMBAT] projectile swing#{pending.Swing} START seed=0x{rng.LastSeed:X8} rngPosBefore={rng.CallsSinceReseed}");
            NativeWeaponDamageResult damageResult = DamageComputer.ResolveNativeWeaponDamage(damageInput);
            Debug.LogError($"[RNG-COMBAT] projectile swing#{pending.Swing} END rngPosAfter={rng.CallsSinceReseed}");
            uint hitRaw = damageResult.HitRaw;
            int hitRoll = damageResult.HitRoll;
            uint blockRaw = damageResult.BlockRaw;
            int blockRoll = damageResult.BlockRoll;
            int attackRating = damageResult.AttackRating;
            int defenseRating = damageResult.DefenseRating;
            int attackerLevel = damageResult.AttackerLevel;
            int hitDefenderLevel = damageResult.DefenderLevel;
            int hitChanceF32 = damageResult.HitThreshold;
            bool isHit = damageResult.IsHit;
            bool isBlocked = damageResult.IsBlocked;

            Debug.LogError($"[RNG-COMBAT] projectile swing#{pending.Swing} seed=0x{rng.LastSeed:X8} rngPos={rng.CallsSinceReseed} useRaw=0x{pending.UseRaw:X8} soundSelect=0x{pending.AttackSoundSelectRaw:X8} soundGate=0x{pending.AttackSoundGateRaw:X8} soundRepeat=0x{pending.AttackSoundRepeatRaw:X8} hitRaw=0x{hitRaw:X8} blockRaw=0x{blockRaw:X8} hitRoll={hitRoll} threshold={hitChanceF32} ar={attackRating} dr={defenseRating} levels={attackerLevel}->{hitDefenderLevel} isHit={isHit}");

            int damage = 0;
            uint damageRaw = 0;
            uint damageWire = 0;
            int weaponDmg = 0;
            int volatility = 0;
            int levelDamageBonus = 0;
            int damageBonus = 0;
            int damageMod = 0;
            int minDmg = 0;
            int maxDmg = 0;
            int critThreshold = 0;
            int critPercent = 0;
            string weaponClass = pending.PlayerState.WeaponClass ?? "unknown";
            string statSource = DamageComputer.ResolveNativeWeaponStatSource(pending.PlayerState);
            bool isCritical = false;
            uint oldHPWire = CombatManager.Instance.PeekMonsterCurrentHPWire(pending.Monster);
            uint newHPWire = oldHPWire;
            bool applied = false;
            bool killed = false;
            int actualDamage = 0;
            int appliedDamage = 0;
            uint effectRaw = 0;
            uint impactSoundRaw = 0;
            weaponDmg = damageInput.WeaponDamageF32;
            volatility = damageInput.WeaponVolatilityF32;
            levelDamageBonus = damageInput.DamageLevel;
            damageBonus = damageInput.DamageBonus;
            damageMod = damageInput.DamageMod;
            minDmg = damageResult.MinDamageF32;
            maxDmg = damageResult.MaxDamageF32;
            critThreshold = damageInput.CritThreshold;
            critPercent = damageInput.CritDamagePercent;
            isCritical = damageResult.IsCritical;

            if (isHit && !isBlocked)
            {
                damageRaw = damageResult.DamageRaw;
                damage = damageResult.DamageF32;

                Debug.LogError($"[RNG-COMBAT] projectile dmgRaw=0x{damageRaw:X8} weaponClass={weaponClass} statSource={statSource} dmg={damage / 256} range=[{minDmg / 256},{maxDmg / 256}] rangeWire=[{minDmg},{maxDmg}] rolledWire={damage} bonus={damageBonus} damageMod={damageMod} crit={isCritical} critThreshold={critThreshold} critPct={critPercent} str={pending.PlayerState.Strength} agi={pending.PlayerState.Agility} level={pending.PlayerState.Level} weaponLevel={pending.PlayerState.WeaponLevel} levelBonus={levelDamageBonus} weapon={DamageComputer.ToFloat(weaponDmg):F2} vol={DamageComputer.ToFloat(volatility):F2} roomRng={rng.CallsSinceReseed}");

                damageWire = damageResult.DamageWire;
                actualDamage = (int)((damageWire + 255) / 256);
                uint playerEntityId = pending.Connection?.Avatar != null ? (uint)pending.Connection.Avatar.Id : 0u;

                CombatManager.Instance.NotifyMonsterDamagedByPlayer(pending.Monster, playerEntityId, "projectile-damage");
                applied = CombatManager.Instance.ApplyNativePlayerDamageToMonsterWire(pending.Monster, damageWire, $"RangedProjectile-HIT swing={pending.Swing}", out oldHPWire, out newHPWire, out killed, now);
                effectRaw = 0;
                appliedDamage = (int)((oldHPWire > newHPWire ? oldHPWire - newHPWire : 0) + 255) / 256;

                Debug.LogError($"[RANGED-PROJECTILE] {pending.ConnKey} {(isCritical ? "CRIT" : "HIT")}: {actualDamage} dmgWire={damageWire} applied={applied} appliedDamage={appliedDamage} target={pending.Monster.Name}#{pending.Monster.EntityId} HP={oldHPWire}->{newHPWire} impact=0x{impactSoundRaw:X8} effect=0x{effectRaw:X8} rngPos={rng.CallsSinceReseed} swing={pending.Swing} hitDist={pending.HitDistance:F2} flightTicks={pending.FlightTicks} dueTick={pending.DueNativeTick} delay={(pending.DueTime - pending.FireTime):F3}s worldBlocked={pending.WorldBlocked}");

                // C4 skeleton (mirror of melee CC-CHECK at line ~755).
                // Ranged hits also fire CC rolls in the client per Unit::onApplyDamage.
                // Log-only, no RNG consumed, no behavior change.
                if (applied && !killed && pending.Monster != null && pending.Monster.MaxHPWire > 0)
                {
                    long damageRatioPct = (long)damageWire * 100L / pending.Monster.MaxHPWire;
                    string wouldFire =
                        damageRatioPct >= 50 ? "KNOCKDOWN" :
                        damageRatioPct >= 25 ? "KNOCKBACK" :
                        damageRatioPct >= 10 ? "STUN" :
                        "NONE";
                    Debug.LogError(
                        $"[CC-CHECK] player={pending.ConnKey} target={pending.Monster.Name}#{pending.Monster.EntityId} " +
                        $"swing={pending.Swing} lane=ranged damageWire={damageWire} maxHPWire={pending.Monster.MaxHPWire} " +
                        $"damageRatioPct={damageRatioPct}% wouldFire={wouldFire} " +
                        $"(skeleton — no RNG consumed; full wiring pending x32dbg parity)");
                }
                if (killed)
                {
                    _completedAttacks.Enqueue(new CompletedAttack
                    {
                        ConnKey = pending.ConnKey,
                        Connection = pending.Connection,
                        Monster = pending.Monster,
                        DamageDealt = appliedDamage,
                        Killed = true
                    });
                }
            }
            else
            {
                string resultType = !isHit ? "MISS" : "BLOCK";
                CombatManager.Instance.LogMonsterClientVisibleSwingNoDamage(pending.Monster, $"RangedProjectile-{resultType} swing={pending.Swing}");
                Debug.LogError($"[RANGED-PROJECTILE] {pending.ConnKey} {resultType}: hitRoll={hitRoll} blockRoll={blockRoll} target={pending.Monster.Name}#{pending.Monster.EntityId} swing={pending.Swing}");
            }

            string combatResult = isHit ? (isBlocked ? "BLOCK" : (isCritical ? "CRIT" : "HIT")) : "MISS";
            Debug.LogError($"[PLAYER-HIT-DETAIL] player={pending.ConnKey} target={pending.Monster.EntityId}/{pending.BehaviorId} seed=0x{rng.LastSeed:X8} projectile=True swing={pending.Swing} tick={pending.Tick} fireTick={pending.FireNativeTick} flightTicks={pending.FlightTicks} impactDelayTicks={pending.ImpactDelayTicks} dueTick={pending.DueNativeTick} rngAfter={rng.CallsSinceReseed} useRaw=0x{pending.UseRaw:X8} soundGate=0x{pending.AttackSoundGateRaw:X8} soundSelect=0x{pending.AttackSoundSelectRaw:X8} soundRepeat=0x{pending.AttackSoundRepeatRaw:X8} hitRaw=0x{hitRaw:X8} blockRaw=0x{blockRaw:X8} dmgRaw=0x{damageRaw:X8} effectRaw=0x{effectRaw:X8} impact=0x{impactSoundRaw:X8} hitRoll={hitRoll} threshold={hitChanceF32} critThreshold={critThreshold} critPct={critPercent} blockRoll={blockRoll} ar={attackRating} dr={defenseRating} levels={attackerLevel}->{hitDefenderLevel} result={combatResult} weaponClass={weaponClass} statSource={statSource} weaponDamage={DamageComputer.ToFloat(weaponDmg):F4} vol={DamageComputer.ToFloat(volatility):F4} weaponLevel={pending.PlayerState.WeaponLevel} levelBonus={levelDamageBonus} bonus={damageBonus} mod={damageMod} rangeWire=[{minDmg},{maxDmg}] rolledWire={damageWire} applied={applied} hp={oldHPWire}->{newHPWire} exact=projectile-weapon-stat-source");
            Debug.LogError($"[COMBAT-EVENT] actor=player player={pending.ConnKey} actorId={(pending.Connection?.Avatar != null ? pending.Connection.Avatar.Id : 0)} target=monster targetId={pending.Monster.EntityId} behaviorId={pending.BehaviorId} result={combatResult} projectile=True damageWire={damageWire} appliedDamage={appliedDamage} hp={oldHPWire}->{newHPWire} hitRaw=0x{hitRaw:X8} hitRoll={hitRoll} threshold={hitChanceF32} blockRaw=0x{blockRaw:X8} blockRoll={blockRoll} blockChance=0 damageRaw=0x{damageRaw:X8} effectRaw=0x{effectRaw:X8} resist=0 critThreshold={critThreshold} critPct={critPercent} rngAfter={rng.CallsSinceReseed}");
        }

        private bool TryResolveProjectileTargetAtImpact(PendingProjectileHit pending, out Monster hitMonster, out float hitDistance, out bool worldBlocked)
        {
            hitMonster = null;
            hitDistance = pending != null ? pending.HitDistance : 0f;
            worldBlocked = pending != null && pending.WorldBlocked;
            if (pending == null || pending.PlayerState == null || pending.Monster == null)
                return false;

            string zoneName = !string.IsNullOrWhiteSpace(pending.Monster.ZoneName)
                ? pending.Monster.ZoneName
                : pending.Connection?.CurrentZoneName;
            PathMap pathMap = !string.IsNullOrWhiteSpace(zoneName) ? PathMapManager.Instance.GetPathMap(zoneName) : null;

            float dx = pending.TargetX - pending.StartX;
            float dy = pending.TargetY - pending.StartY;
            float lenSq = dx * dx + dy * dy;
            if (lenSq <= 0.001f)
                return false;

            float pathDistance = Mathf.Sqrt(lenSq);
            float projectileSize = Mathf.Max(0f, pending.PlayerState.WeaponProjectileSize);
            float scanRange = Mathf.Max(pathDistance + projectileSize + 20f, pending.HitDistance + projectileSize + 20f);
            Monster best = null;
            float bestAlong = float.MaxValue;
            bool bestBlocked = worldBlocked;

            foreach (var candidate in CombatManager.Instance.GetMonstersInRange(pending.StartX, pending.StartY, scanRange))
            {
                if (candidate == null || !candidate.IsAlive) continue;
                if (!string.IsNullOrWhiteSpace(zoneName)
                    && !string.IsNullOrWhiteSpace(candidate.ZoneName)
                    && !string.Equals(candidate.ZoneName, zoneName, StringComparison.OrdinalIgnoreCase))
                    continue;

                float cx = candidate.PosX - pending.StartX;
                float cy = candidate.PosY - pending.StartY;
                float t = Mathf.Clamp01((cx * dx + cy * dy) / lenSq);
                float along = t * pathDistance;
                if (along > pathDistance + projectileSize) continue;
                float closestX = pending.StartX + dx * t;
                float closestY = pending.StartY + dy * t;
                float miss = Distance2D(candidate.PosX, candidate.PosY, closestX, closestY);
                float radius = Mathf.Max(0f, candidate.CollisionRadius) + projectileSize;
                if (miss > radius) continue;

                bool candidateWorldBlocked = pathMap != null && !pathMap.CanReachPoint(pending.StartX, pending.StartY, closestX, closestY);
                if (along < bestAlong)
                {
                    best = candidate;
                    bestAlong = along;
                    bestBlocked = candidateWorldBlocked;
                }
            }

            if (best == null)
                return false;

            hitMonster = best;
            hitDistance = Mathf.Max(0f, bestAlong);
            worldBlocked = bestBlocked;
            return true;
        }

        private static float Distance2D(float x1, float y1, float x2, float y2)
        {
            float dx = x2 - x1;
            float dy = y2 - y1;
            return Mathf.Sqrt(dx * dx + dy * dy);
        }

        private bool ClearConsumedUseTarget(WeaponCycle cycle)
        {
            if (cycle == null || cycle.Connection == null) return false;
            if (!cycle.Connection.HasActiveUseTarget || cycle.Connection.ActiveUseTargetId != cycle.TargetId) return false;

            cycle.Connection.HasActiveUseTarget = false;
            cycle.Connection.ActiveUseTargetId = 0;
            cycle.Connection.ActiveUseTargetFlags = 0;

            uint avatarId = cycle.Connection.Avatar != null ? (uint)cycle.Connection.Avatar.Id : 0u;
            if (avatarId != 0)
                CombatManager.Instance.SetPlayerActiveClientAttack(avatarId, false);
            return true;
        }

        private bool HasNativePlayerMeleeContact(WeaponCycle cycle, out float distance, out float range)
        {
            distance = float.MaxValue;
            range = 0f;
            if (cycle == null || cycle.Connection == null || cycle.Monster == null) return false;
            float dx = cycle.Monster.PosX - cycle.Connection.PlayerPosX;
            float dy = cycle.Monster.PosY - cycle.Connection.PlayerPosY;
            distance = Mathf.Sqrt(dx * dx + dy * dy);
            range = IsNativeProjectileRangedCycle(cycle)
                ? CombatManager.Instance.ResolvePlayerRangedProjectileRange(cycle.PlayerState, cycle.Monster)
                : CombatManager.Instance.ResolvePlayerMeleeNativeContactRange(cycle.PlayerState, cycle.Monster);
            return range > 0f && distance <= range + NATIVE_CONTACT_RANGE_EPSILON;
        }

        public CompletedAttack DequeueKill()
        {
            return _completedAttacks.Count > 0 ? _completedAttacks.Dequeue() : null;
        }

        public bool HasPendingKills => _completedAttacks.Count > 0;

        public void ClearConnection(string connKey)
        {
            _activeCycles.Remove(connKey);
            if (!string.IsNullOrEmpty(connKey) && _activeProjectiles.Count > 0)
            {
                _activeProjectiles.RemoveAll(pending =>
                    pending != null && string.Equals(pending.ConnKey, connKey, StringComparison.Ordinal));
            }
        }

        public void Clear()
        {
            _activeCycles.Clear();
            _completedAttacks.Clear();
            _activeProjectiles.Clear();
            _nextProjectileSequence = 0;
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
        public uint AttackSoundRepeatRaw;
        public uint ImpactSoundRaw;
        public byte AttackAnimationIndex;
        public int SwingCount;
        public int PendingRepeatUses;
        public bool AwaitingContact;
        public bool ServerApproachOnly;
        public bool ContactHoldLogged;
        public float Distance;
        public float ContactRange;
        public float LastTickTime;
        public float NextUseTime;
        public int AttackTotalFrames;
        public int AttackHitFrame;
        public int AttackSoundFrame;
        public int AttackAnimationId;
    }

    public class CompletedAttack
    {
        public string ConnKey;
        public RRConnection Connection;
        public Monster Monster;
        public bool Killed;
        public int DamageDealt;
    }

    public class WeaponCycleFlushResult
    {
        public uint TargetEntityId;
        public uint BeforeHPWire;
        public uint AfterHPWire;
        public int PendingBefore;
        public int PendingAfter;
        public int ProjectilesResolved;
        public int CycleTicks;
        public bool HadTargetCycle;
    }

    public class PendingProjectileHit
    {
        public long Sequence;
        public string ConnKey;
        public RRConnection Connection;
        public PlayerState PlayerState;
        public Monster Monster;
        public uint RequestedTargetId;
        public uint TargetId;
        public uint BehaviorId;
        public int Swing;
        public int Tick;
        public float FireTime;
        public float DueTime;
        public int FireNativeTick;
        public int FlightTicks;
        public int ImpactDelayTicks;
        public int DueNativeTick;
        public float HitDistance;
        public float PathDistance;
        public bool WorldBlocked;
        public uint UseRaw;
        public uint AttackSoundSelectRaw;
        public uint AttackSoundGateRaw;
        public uint AttackSoundRepeatRaw;
        public NativeWeaponDamageInput DamageInput;
        public float StartX;
        public float StartY;
        public float TargetX;
        public float TargetY;
        public float ProjectileSpeed;
        public float ProjectileSize;
        public float StepDistance;
        public float InitialDistance;
        public float CurrentDistance;
        public float MaxDistance;
        public int MaxLifetimeTicks;
        public int LastUpdateNativeTick;
        public int UpdatesCompleted;
        public bool ImpactResolved;
    }

    public enum NativeDueEventDisposition
    {
        Consumed,
        KeepAndStop
    }

    public class NativeDueDrainSummary
    {
        public int PendingBefore;
        public int PendingAfter;
        public int Drained;
        public int MatchingDrained;
        public bool Stopped;
        public int NextDueTick = -1;
        public float NextDueTime = -1f;
    }

    public class NativeDueEvent<T>
    {
        public int DueTick;
        public float DueTime;
        public long Sequence;
        public T Payload;
    }

    public class NativeDueEventScheduler<T>
    {
        private readonly List<NativeDueEvent<T>> _events = new List<NativeDueEvent<T>>();
        private long _nextSequence;

        public int Count => _events.Count;

        public long Schedule(int dueTick, float dueTime, T payload)
        {
            var scheduled = new NativeDueEvent<T>
            {
                DueTick = Math.Max(0, dueTick),
                DueTime = dueTime,
                Sequence = ++_nextSequence,
                Payload = payload
            };

            int insertAt = _events.Count;
            for (int i = 0; i < _events.Count; i++)
            {
                if (Compare(scheduled, _events[i]) < 0)
                {
                    insertAt = i;
                    break;
                }
            }
            _events.Insert(insertAt, scheduled);
            return scheduled.Sequence;
        }

        public NativeDueDrainSummary DrainDue(int upToTick, float upToTime, Func<NativeDueEvent<T>, NativeDueEventDisposition> drain)
        {
            var summary = new NativeDueDrainSummary
            {
                PendingBefore = _events.Count
            };

            while (_events.Count > 0)
            {
                var scheduled = _events[0];
                if (!IsDue(scheduled, upToTick, upToTime))
                    break;

                _events.RemoveAt(0);
                NativeDueEventDisposition disposition = drain != null
                    ? drain(scheduled)
                    : NativeDueEventDisposition.Consumed;

                if (disposition == NativeDueEventDisposition.KeepAndStop)
                {
                    _events.Insert(0, scheduled);
                    summary.Stopped = true;
                    break;
                }

                summary.Drained++;
            }

            summary.PendingAfter = _events.Count;
            if (_events.Count > 0)
            {
                summary.NextDueTick = _events[0].DueTick;
                summary.NextDueTime = _events[0].DueTime;
            }
            return summary;
        }

        public int RemoveWhere(Predicate<T> predicate)
        {
            if (predicate == null || _events.Count == 0)
                return 0;

            int removed = 0;
            for (int i = _events.Count - 1; i >= 0; i--)
            {
                if (!predicate(_events[i].Payload))
                    continue;

                _events.RemoveAt(i);
                removed++;
            }
            return removed;
        }

        public void Clear()
        {
            _events.Clear();
        }

        private static bool IsDue(NativeDueEvent<T> scheduled, int upToTick, float upToTime)
        {
            if (scheduled.DueTick < upToTick)
                return true;
            if (scheduled.DueTick > upToTick)
                return false;
            return scheduled.DueTime <= upToTime + 0.0001f;
        }

        private static int Compare(NativeDueEvent<T> left, NativeDueEvent<T> right)
        {
            int tick = left.DueTick.CompareTo(right.DueTick);
            if (tick != 0) return tick;
            int time = left.DueTime.CompareTo(right.DueTime);
            if (time != 0) return time;
            return left.Sequence.CompareTo(right.Sequence);
        }
    }
}
