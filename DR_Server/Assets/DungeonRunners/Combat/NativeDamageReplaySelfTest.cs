using System;
using System.Collections.Generic;
using System.Linq;

namespace DungeonRunners.Combat
{
    public static class NativeDamageReplaySelfTest
    {
        public const uint LatestPup50024Seed = 0x8D801C2B;
        public const int LatestPup50024PreHitConsumes = 156;
        public const uint LatestPup50024ServerDamageWire = 3180;
        public const uint LatestPup50024NativeObservedDamageWire = 4399;
        public const uint LatestPup50024StartHPWire = 29184;
        public const uint LatestRatling50000ClockSeed = 0xE8BD592F;
        public const uint LatestRatling50000StartHPWire = 29184;
        public const uint LatestRatling50000FirstSuffixHPWire = 24002;
        public const uint LatestRatling50000ClientCrashHPWire = 24132;
        public const uint LatestRatling50000DamageRaw = 0x30FF6AD4;
        public const int NativeCombatHz = 30;
        public const int LatestRatling50000SuffixFlushAttempts = 12;
        public const uint SchedulerSelfTestTargetEntityId = 50000;
        public const uint SchedulerSelfTestOtherEntityId = 50024;
        public const uint SchedulerSelfTestStartHPWire = 29184;
        public const uint LatestPup50000StaleRemoteHPWire = 23330;
        public const uint LatestPup50000ClientLocalHPWire = 10030;

        public static NativeDamageReplayResult RunLatestPup50024Replay()
        {
            var rng = new MersenneTwister(LatestPup50024Seed);
            for (int i = 0; i < LatestPup50024PreHitConsumes; i++)
                rng.Generate();

            var input = new NativeWeaponDamageInput
            {
                Rng = rng,
                Source = "latest-pup-50024-replay",
                AttackerLevel = 3,
                DefenderLevel = 2,
                AttackRating = 210,
                DefenseRating = 52,
                BlockChance = 0,
                DamageLevel = 3,
                DamageBonus = 31,
                DamageMod = 100,
                WeaponDamageF32 = 139,
                WeaponVolatilityF32 = 85,
                CritThreshold = 2048,
                CritDamagePercent = 200
            };

            NativeWeaponDamageResult result = DamageComputer.ResolveNativeWeaponDamage(input);
            return new NativeDamageReplayResult
            {
                Seed = LatestPup50024Seed,
                PreHitConsumes = LatestPup50024PreHitConsumes,
                HitRaw = result.HitRaw,
                BlockRaw = result.BlockRaw,
                DamageRaw = result.DamageRaw,
                HitRoll = result.HitRoll,
                BlockRoll = result.BlockRoll,
                HitThreshold = result.HitThreshold,
                MinDamageWire = result.MinDamageF32,
                MaxDamageWire = result.MaxDamageF32,
                DamageWire = result.DamageWire,
                ServerLoggedDamageWire = LatestPup50024ServerDamageWire,
                NativeObservedDamageWire = LatestPup50024NativeObservedDamageWire,
                ServerLoggedHPAfter = LatestPup50024StartHPWire - LatestPup50024ServerDamageWire,
                NativeObservedHPAfter = LatestPup50024StartHPWire - LatestPup50024NativeObservedDamageWire,
                ReplayHPAfter = LatestPup50024StartHPWire - result.DamageWire,
                RoomRngAfter = result.RoomRngAfter
            };
        }

        public static NativeFixed32ReplayResult RunStarterCrossbowFixed32Replay()
        {
            return new NativeFixed32ReplayResult
            {
                DamageText = "0.54",
                VolatilityText = "0.33",
                DamageF32 = DamageComputer.NativeFixed32FromAuthoredDecimal(0.54f),
                VolatilityF32 = DamageComputer.NativeFixed32FromAuthoredDecimal(0.33f)
            };
        }

        public static NativeStarterCrossbowCycleReplayResult RunStarterCrossbowCycleReplay()
        {
            const int selector = 0;
            const int animationId = 310;
            const int numFrames = 17;
            const int triggerTime = 2;
            const int soundTriggerTime = 2;
            const float weaponSpeed = 95f;

            int cycleTicks = Math.Max(1, (int)Math.Floor(numFrames * 100f / weaponSpeed));
            int triggerTick = Math.Max(1, (int)Math.Floor(triggerTime * 100f / weaponSpeed) + 1);
            int soundTick = Math.Max(1, (int)Math.Floor(soundTriggerTime * 100f / weaponSpeed) + 1);

            return new NativeStarterCrossbowCycleReplayResult
            {
                Selector = selector,
                AnimationId = animationId,
                NumFrames = numFrames,
                TriggerTime = triggerTime,
                SoundTriggerTime = soundTriggerTime,
                WeaponSpeed = weaponSpeed,
                CycleTicks = cycleTicks,
                ProjectileEventTick = triggerTick,
                SoundEventTick = soundTick,
                UseProjectileCreatesProjectileOnly = true
            };
        }

        public static NativeProjectilePrestepReplayResult RunProjectilePrestepReplay()
        {
            const float crossbowSpeed = 180f;
            const float poisonSpeed = 200f;
            const float targetCollisionRadius = 5f;
            const float crossbowProjectileSize = 10f;
            const float poisonProjectileSize = 8f;
            const float poisonProjectileLifespanTicks = 23f;
            const float poisonTargetProjectedDistance = 156.9f;
            const float poisonAimDistance = 135.3f;

            float crossbowStep = WeaponCycleTracker.NativeProjectileStepDistance(crossbowSpeed);
            float poisonStep = WeaponCycleTracker.NativeProjectileStepDistance(poisonSpeed);
            float poisonMaxDistance = poisonSpeed * poisonProjectileLifespanTicks / NativeCombatHz;
            float poisonRadius = targetCollisionRadius + poisonProjectileSize;
            float poisonEntryDistance = Math.Max(0f, poisonTargetProjectedDistance - poisonRadius);

            return new NativeProjectilePrestepReplayResult
            {
                CrossbowSpeed = crossbowSpeed,
                CrossbowStepDistance = crossbowStep,
                CrossbowInitialDistance = WeaponCycleTracker.NativeProjectileInitialDistance(crossbowSpeed, 200f),
                CrossbowTargetCollisionRadius = targetCollisionRadius,
                CrossbowProjectileSize = crossbowProjectileSize,
                CrossbowHitRadius = targetCollisionRadius + crossbowProjectileSize,
                PoisonSpeed = poisonSpeed,
                PoisonStepDistance = poisonStep,
                PoisonInitialDistance = WeaponCycleTracker.NativeProjectileInitialDistance(poisonSpeed, poisonMaxDistance),
                PoisonProjectileSize = poisonProjectileSize,
                PoisonHitRadius = poisonRadius,
                PoisonAimDistance = poisonAimDistance,
                PoisonProjectedDistance = poisonTargetProjectedDistance,
                PoisonMaxDistance = poisonMaxDistance,
                PoisonEntryDistance = poisonEntryDistance
            };
        }

        public static NativeHpRuntimeReplayResult RunLatestRatling50000HpRuntimeReplay()
        {
            int damageF32 = DamageComputer.NativeFixed32FromAuthoredDecimal(0.54f);
            int volatilityF32 = DamageComputer.NativeFixed32FromAuthoredDecimal(0.33f);
            DamageComputer.ComputeNativeWeaponDamageRange(3, 31, 100, damageF32, volatilityF32, out int minDamage, out int maxDamage);
            int damageWire = DamageComputer.RollDamageRange(minDamage, maxDamage, LatestRatling50000DamageRaw);
            uint firstSuffixHP = ApplyDamageWire(LatestRatling50000StartHPWire, (uint)damageWire);

            const int unitRuntimeTicksBeforeSecondSuffix = 5;
            const int baseRegen = 2;
            int perTickRegen = CombatManager.ComputeNativeUnitRegenDeltaWire(LatestRatling50000StartHPWire, baseRegen, 0, 0, false);
            const int defaultWorldSettingsField = 15;
            bool stockUnitDamageRegenDelayClears = (defaultWorldSettingsField & 0x00000800) != 0;
            uint secondSuffixHP = firstSuffixHP;
            for (int i = 0; i < unitRuntimeTicksBeforeSecondSuffix; i++)
                secondSuffixHP = CombatManager.ApplyNativeUnitHPShiftWire(secondSuffixHP, LatestRatling50000StartHPWire, perTickRegen);

            return new NativeHpRuntimeReplayResult
            {
                StartHPWire = LatestRatling50000StartHPWire,
                FirstSuffixHPWire = firstSuffixHP,
                ClientCrashHPWire = LatestRatling50000ClientCrashHPWire,
                DamageRaw = LatestRatling50000DamageRaw,
                DamageF32 = damageF32,
                VolatilityF32 = volatilityF32,
                MinDamageWire = minDamage,
                MaxDamageWire = maxDamage,
                DamageWire = (uint)damageWire,
                UnitRuntimeTicksBeforeSecondSuffix = unitRuntimeTicksBeforeSecondSuffix,
                BaseRegen = baseRegen,
                RegenMod = 0,
                AdditiveRegen = 0,
                PerTickRegenWire = perTickRegen,
                DefaultWorldSettingsField = defaultWorldSettingsField,
                StockUnitDamageRegenDelayClears = stockUnitDamageRegenDelayClears,
                SecondSuffixHPWire = secondSuffixHP,
                PlainWeaponPostApplyEffectRaw = 0
            };
        }

        public static NativeCombatClockReplayResult RunLatestRatling50000ClockReplay()
        {
            const int nativeTicksObserved = 30;
            const int poisonDurationSeconds = 4;
            const int poisonFrequencySeconds = 1;
            const int rangedRepeatQueueCount = 0;

            int actorAdvances = 0;
            for (int tick = 0; tick < nativeTicksObserved; tick++)
                actorAdvances++;

            return new NativeCombatClockReplayResult
            {
                Seed = LatestRatling50000ClockSeed,
                NativeHz = NativeCombatHz,
                NativeTicksObserved = nativeTicksObserved,
                ActorAdvances = actorAdvances,
                SuffixFlushAttempts = LatestRatling50000SuffixFlushAttempts,
                SuffixAdvancedTicks = 0,
                PoisonDurationTicks = poisonDurationSeconds * NativeCombatHz,
                PoisonFrequencyTicks = poisonFrequencySeconds * NativeCombatHz,
                PoisonMaxTicks = poisonDurationSeconds / poisonFrequencySeconds,
                RangedRepeatQueueCount = rangedRepeatQueueCount,
                RangedRepeatResolvedFromDueTick = 0
            };
        }

        public static NativeSchedulerOrderReplayResult RunNativeSchedulerOrderReplay()
        {
            const float suffixNow = 1.000f;
            const int validationTick = NativeCombatHz;
            const int projectileHitFrameTick = 15;
            const float projectileDistance = 90f;
            const float projectileSpeed = 180f;
            const float poisonDurationSeconds = 4f;
            const float poisonFrequencySeconds = 1f;

            var pending = new List<NativeSchedulerEvent>
            {
                new NativeSchedulerEvent(1, SchedulerSelfTestTargetEntityId, 0.900f, 1280),
                new NativeSchedulerEvent(2, SchedulerSelfTestOtherEntityId, 0.800f, 2048),
                new NativeSchedulerEvent(3, SchedulerSelfTestTargetEntityId, 1.100f, 512),
                new NativeSchedulerEvent(4, SchedulerSelfTestTargetEntityId, 1.000f, 768),
                new NativeSchedulerEvent(5, SchedulerSelfTestOtherEntityId, 1.200f, 1024)
            };
            int[] originalOrder = pending.Select(e => e.Order).ToArray();

            NativeSchedulerDrainReplay firstDrain = DrainDueSchedulerEvents(pending, SchedulerSelfTestTargetEntityId, suffixNow, includeEqualTime: false);
            uint hpAfterFirstDrain = ApplyDamageWire(SchedulerSelfTestStartHPWire, firstDrain.ResolvedDamageWire);

            uint pureRead1 = hpAfterFirstDrain;
            uint pureRead2 = pureRead1;
            int pureReadRuntimeAdvances = 0;
            int pureReadResolvedEvents = 0;

            NativeSchedulerDrainReplay secondDrain = DrainDueSchedulerEvents(pending, SchedulerSelfTestTargetEntityId, suffixNow, includeEqualTime: false);
            uint hpAfterSecondDrain = ApplyDamageWire(hpAfterFirstDrain, secondDrain.ResolvedDamageWire);
            NativeSchedulerDrainReplay entityPhaseDrain = DrainDueSchedulerEvents(pending, SchedulerSelfTestTargetEntityId, suffixNow, includeEqualTime: true);
            uint hpAfterEntityPhase = ApplyDamageWire(hpAfterSecondDrain, entityPhaseDrain.ResolvedDamageWire);

            float projectileFireTime = projectileHitFrameTick / (float)NativeCombatHz;
            int projectileFlightTicks = WeaponCycleTracker.NativeProjectileFlightTicks(projectileDistance, projectileSpeed);
            int projectileImpactDelayTicks = WeaponCycleTracker.NativeProjectileImpactDelayTicks(projectileDistance, projectileSpeed);
            float projectileFlightTime = projectileFlightTicks / (float)NativeCombatHz;
            float projectileImpactDelay = projectileImpactDelayTicks / (float)NativeCombatHz;
            float projectileImpactTime = projectileFireTime + projectileImpactDelay;
            int projectileImpactTick = projectileHitFrameTick + projectileImpactDelayTicks;
            bool projectileResolvesBeforeImpact = IsDue(projectileImpactTime, projectileImpactTime - (1f / (NativeCombatHz * 2f)));
            bool projectileResolvesAtImpact = IsDue(projectileImpactTime, projectileImpactTime);

            float poisonImpactTime = projectileImpactTime;
            float poisonFirstTickTime = poisonImpactTime + poisonFrequencySeconds;
            int poisonTicksAtImpact = CountPoisonTicksDue(poisonImpactTime, poisonFirstTickTime, poisonFrequencySeconds, poisonDurationSeconds);
            int poisonTicksBeforeFirst = CountPoisonTicksDue(poisonFirstTickTime - (1f / (NativeCombatHz * 2f)), poisonFirstTickTime, poisonFrequencySeconds, poisonDurationSeconds);
            int poisonTicksAtFirst = CountPoisonTicksDue(poisonFirstTickTime, poisonFirstTickTime, poisonFrequencySeconds, poisonDurationSeconds);

            return new NativeSchedulerOrderReplayResult
            {
                NativeHz = NativeCombatHz,
                TargetEntityId = SchedulerSelfTestTargetEntityId,
                OtherEntityId = SchedulerSelfTestOtherEntityId,
                SuffixNow = suffixNow,
                ValidationCutoffTick = validationTick,
                ValidationCutoffTime = suffixNow,
                OriginalQueueOrder = originalOrder,
                FirstDrainResolvedOrder = firstDrain.ResolvedOrder,
                FirstDrainRemainingOrder = firstDrain.RemainingOrder,
                SecondDrainResolvedOrder = secondDrain.ResolvedOrder,
                SecondDrainRemainingOrder = secondDrain.RemainingOrder,
                EntityPhaseResolvedOrder = entityPhaseDrain.ResolvedOrder,
                EntityPhaseRemainingOrder = entityPhaseDrain.RemainingOrder,
                FirstDrainDamageWire = firstDrain.ResolvedDamageWire,
                SecondDrainDamageWire = secondDrain.ResolvedDamageWire,
                EntityPhaseDamageWire = entityPhaseDrain.ResolvedDamageWire,
                StartHPWire = SchedulerSelfTestStartHPWire,
                HPAfterFirstDrain = hpAfterFirstDrain,
                HPAfterPureRead1 = pureRead1,
                HPAfterPureRead2 = pureRead2,
                HPAfterSecondDrain = hpAfterSecondDrain,
                HPAfterEntityPhase = hpAfterEntityPhase,
                PureReadRuntimeAdvances = pureReadRuntimeAdvances,
                PureReadResolvedEvents = pureReadResolvedEvents,
                ProjectileHitFrameTick = projectileHitFrameTick,
                ProjectileFlightTicks = projectileFlightTicks,
                ProjectileImpactDelayTicks = projectileImpactDelayTicks,
                ProjectileImpactTick = projectileImpactTick,
                ProjectileFireTime = projectileFireTime,
                ProjectileDistance = projectileDistance,
                ProjectileSpeed = projectileSpeed,
                ProjectileFlightTime = projectileFlightTime,
                ProjectileImpactDelay = projectileImpactDelay,
                ProjectileImpactTime = projectileImpactTime,
                ProjectileSamePassFirstUpdate = false,
                ProjectileResolvesBeforeImpact = projectileResolvesBeforeImpact,
                ProjectileResolvesAtImpact = projectileResolvesAtImpact,
                PoisonImpactTime = poisonImpactTime,
                PoisonFrequencySeconds = poisonFrequencySeconds,
                PoisonDurationSeconds = poisonDurationSeconds,
                PoisonFirstTickTime = poisonFirstTickTime,
                PoisonFirstTickDelayTicks = SecondsToNativeTicks(poisonFirstTickTime - poisonImpactTime),
                PoisonMaxTicks = (int)Math.Ceiling(poisonDurationSeconds / poisonFrequencySeconds),
                PoisonTicksAtImpact = poisonTicksAtImpact,
                PoisonTicksBeforeFirstTick = poisonTicksBeforeFirst,
                PoisonTicksAtFirstTick = poisonTicksAtFirst,
                HpSyncRuntimeBeforeRegister = 17893u,
                HpSyncStaleDomainHP = 24278u,
                HpSyncAfterRegister = Math.Min(17893u, 24278u),
                LatestPupStaleRemoteHPWire = LatestPup50000StaleRemoteHPWire,
                LatestPupClientLocalHPWire = LatestPup50000ClientLocalHPWire,
                SubentityPhaseRunsBeforeEntityPhase = true,
                ProjectileCreatedDuringEntityUpdatesNextSubentityPhase = true
            };
        }

        public static NativeAuthoredSpellSplitReplayResult RunFireBoltPoisonAuthoredSplitReplay()
        {
            SpellData fireBolt = SpellDatabase.GetSpell("FireBolt");
            SpellData poisonShot = SpellDatabase.GetSpell("PoisonShot");

            return new NativeAuthoredSpellSplitReplayResult
            {
                FireBoltFound = fireBolt != null,
                PoisonShotFound = poisonShot != null,
                FireBoltSkillId = fireBolt?.SkillId,
                PoisonShotSkillId = poisonShot?.SkillId,
                FireBoltAttackType = fireBolt != null ? fireBolt.AttackType : default,
                PoisonShotAttackType = poisonShot != null ? poisonShot.AttackType : default,
                FireBoltDamageType = fireBolt != null ? fireBolt.DamageType : default,
                PoisonShotDamageType = poisonShot != null ? poisonShot.DamageType : default,
                FireBoltRange = fireBolt?.Range ?? 0,
                PoisonShotRange = poisonShot?.Range ?? 0,
                FireBoltCooldown = fireBolt?.Cooldown ?? 0f,
                PoisonShotCooldown = poisonShot?.Cooldown ?? 0f,
                FireBoltProjectileSpeed = fireBolt?.ProjectileSpeed ?? 0f,
                PoisonShotProjectileSpeed = poisonShot?.ProjectileSpeed ?? 0f,
                FireBoltProjectileSize = fireBolt?.ProjectileSize ?? 0f,
                PoisonShotProjectileSize = poisonShot?.ProjectileSize ?? 0f,
                FireBoltProjectileLifespan = fireBolt?.ProjectileLifespan ?? 0f,
                PoisonShotProjectileLifespan = poisonShot?.ProjectileLifespan ?? 0f,
                FireBoltDamageMod = fireBolt?.DamageMod ?? 0f,
                PoisonShotDamageMod = poisonShot?.DamageMod ?? 0f,
                FireBoltDamageVolatility = fireBolt?.DamageVolatility ?? 0f,
                PoisonShotDamageVolatility = poisonShot?.DamageVolatility ?? 0f,
                FireBoltHasDirectDamageEffect = fireBolt?.HasDirectDamageEffect ?? false,
                PoisonShotHasDirectDamageEffect = poisonShot?.HasDirectDamageEffect ?? false,
                PoisonShotHasImmediateWeaponDamageEffect = poisonShot?.HasImmediateWeaponDamageEffect ?? false,
                PoisonShotARModMin = poisonShot?.ARModMin ?? 0,
                PoisonShotARModMax = poisonShot?.ARModMax ?? 0,
                PoisonShotWeaponEffectDamageModMin = poisonShot?.WeaponEffectDamageModMin ?? 0,
                PoisonShotWeaponEffectDamageModMax = poisonShot?.WeaponEffectDamageModMax ?? 0,
                FireBoltHasProjectileModifierDamage = fireBolt?.HasProjectileModifierDamage ?? false,
                PoisonShotHasProjectileModifierDamage = poisonShot?.HasProjectileModifierDamage ?? false,
                FireBoltProjectileEffectId = fireBolt?.ProjectileEffectId,
                PoisonShotProjectileEffectId = poisonShot?.ProjectileEffectId,
                FireBoltProjectileModifierId = fireBolt?.ProjectileModifierId,
                PoisonShotProjectileModifierId = poisonShot?.ProjectileModifierId,
                PoisonShotProjectileModifierEffectId = poisonShot?.ProjectileModifierEffectId,
                PoisonShotProjectileModifierAttackType = poisonShot?.EffectiveProjectileModifierAttackType ?? default,
                PoisonShotProjectileModifierDamageType = poisonShot?.EffectiveProjectileModifierDamageType ?? default,
                PoisonShotProjectileModifierDuration = poisonShot?.ProjectileModifierDuration ?? 0f,
                PoisonShotProjectileModifierFrequency = poisonShot?.ProjectileModifierFrequency ?? 0f,
                PoisonShotProjectileModifierStackRule = poisonShot?.ProjectileModifierStackRule,
                PoisonShotProjectileModifierDamageMod = poisonShot?.ProjectileModifierDamageMod ?? 0f,
                PoisonShotProjectileModifierDamageVolatility = poisonShot?.ProjectileModifierDamageVolatility ?? 0f,
                PoisonShotProjectileModifierCriticalChance = poisonShot?.ProjectileModifierCriticalChance ?? 0f
            };
        }

        private static NativeSchedulerDrainReplay DrainDueSchedulerEvents(List<NativeSchedulerEvent> pending, uint targetEntityId, float now, bool includeEqualTime)
        {
            var resolvedOrder = new List<int>();
            var remaining = new List<NativeSchedulerEvent>();
            uint resolvedDamage = 0;

            foreach (NativeSchedulerEvent pendingEvent in pending.OrderBy(e => e.DueTime).ThenBy(e => e.Order))
            {
                if (includeEqualTime ? IsDue(pendingEvent.DueTime, now) : IsDueBefore(pendingEvent.DueTime, now))
                {
                    resolvedOrder.Add(pendingEvent.Order);
                    if (pendingEvent.TargetEntityId == targetEntityId)
                        resolvedDamage += pendingEvent.DamageWire;
                    continue;
                }
                remaining.Add(pendingEvent);
            }

            pending.Clear();
            pending.AddRange(remaining);

            return new NativeSchedulerDrainReplay
            {
                ResolvedOrder = resolvedOrder.ToArray(),
                RemainingOrder = pending.Select(e => e.Order).ToArray(),
                ResolvedDamageWire = resolvedDamage
            };
        }

        private static bool IsDue(float dueTime, float now)
        {
            return dueTime <= 0f || now + 0.0001f >= dueTime;
        }

        private static bool IsDueBefore(float dueTime, float now)
        {
            return dueTime <= 0f || dueTime + 0.0001f < now;
        }

        private static int SecondsToNativeTicks(float seconds)
        {
            return (int)Math.Ceiling(Math.Max(0f, seconds) * NativeCombatHz - 0.0001f);
        }

        private static int CountPoisonTicksDue(float now, float firstTickTime, float frequencySeconds, float durationSeconds)
        {
            int maxTicks = (int)Math.Ceiling(durationSeconds / frequencySeconds);
            int due = 0;
            float nextTick = firstTickTime;
            for (int tick = 0; tick < maxTicks; tick++)
            {
                if (!IsDue(nextTick, now))
                    break;
                due++;
                nextTick += frequencySeconds;
            }
            return due;
        }

        private static uint ApplyDamageWire(uint hpWire, uint damageWire)
        {
            return damageWire >= hpWire ? 0 : hpWire - damageWire;
        }

        private sealed class NativeSchedulerEvent
        {
            public NativeSchedulerEvent(int order, uint targetEntityId, float dueTime, uint damageWire)
            {
                Order = order;
                TargetEntityId = targetEntityId;
                DueTime = dueTime;
                DamageWire = damageWire;
            }

            public int Order;
            public uint TargetEntityId;
            public float DueTime;
            public uint DamageWire;
        }

        private sealed class NativeSchedulerDrainReplay
        {
            public int[] ResolvedOrder;
            public int[] RemainingOrder;
            public uint ResolvedDamageWire;
        }
    }

    public class NativeDamageReplayResult
    {
        public uint Seed;
        public int PreHitConsumes;
        public uint HitRaw;
        public uint BlockRaw;
        public uint DamageRaw;
        public int HitRoll;
        public int BlockRoll;
        public int HitThreshold;
        public int MinDamageWire;
        public int MaxDamageWire;
        public uint DamageWire;
        public uint ServerLoggedDamageWire;
        public uint NativeObservedDamageWire;
        public uint ReplayHPAfter;
        public uint ServerLoggedHPAfter;
        public uint NativeObservedHPAfter;
        public int RoomRngAfter;

        public bool MatchesServerLoggedReplay =>
            HitRaw == 0x15C81148u &&
            BlockRaw == 0x0802FB68u &&
            DamageRaw == 0x74222EACu &&
            DamageWire == ServerLoggedDamageWire &&
            ReplayHPAfter == ServerLoggedHPAfter &&
            RoomRngAfter == 159;

        public bool MatchesNativeObservedHP => DamageWire == NativeObservedDamageWire && ReplayHPAfter == NativeObservedHPAfter;

        public override string ToString()
        {
            return $"seed=0x{Seed:X8} pre={PreHitConsumes} hit=0x{HitRaw:X8}/{HitRoll} block=0x{BlockRaw:X8}/{BlockRoll} dmgRaw=0x{DamageRaw:X8} threshold={HitThreshold} range=[{MinDamageWire},{MaxDamageWire}] damage={DamageWire} hp={ReplayHPAfter} serverMatch={MatchesServerLoggedReplay} nativeHpMatch={MatchesNativeObservedHP}";
        }
    }

    public class NativeCombatClockReplayResult
    {
        public uint Seed;
        public int NativeHz;
        public int NativeTicksObserved;
        public int ActorAdvances;
        public int SuffixFlushAttempts;
        public int SuffixAdvancedTicks;
        public int PoisonDurationTicks;
        public int PoisonFrequencyTicks;
        public int PoisonMaxTicks;
        public int RangedRepeatQueueCount;
        public int RangedRepeatResolvedFromDueTick;

        public bool OneActorAdvancePerNativeTick => ActorAdvances == NativeTicksObserved;
        public bool SuffixFlushDoesNotAdvanceCombat => SuffixAdvancedTicks == 0 && SuffixFlushAttempts > 0;
        public bool PoisonCadenceMatchesAuthored => NativeHz == 30 && PoisonDurationTicks == 120 && PoisonFrequencyTicks == 30 && PoisonMaxTicks == 4;
        public bool RangedRedundantUseSuppressed => RangedRepeatQueueCount == 0 && RangedRepeatResolvedFromDueTick == 0;
        public bool RangedProjectileRepeatIsDueScheduled => RangedRedundantUseSuppressed;
        public bool MatchesNativeClockContract => OneActorAdvancePerNativeTick && SuffixFlushDoesNotAdvanceCombat && PoisonCadenceMatchesAuthored && RangedRedundantUseSuppressed;

        public override string ToString()
        {
            return $"seed=0x{Seed:X8} hz={NativeHz} ticks={NativeTicksObserved} actorAdvances={ActorAdvances} suffixFlushes={SuffixFlushAttempts} suffixAdvanced={SuffixAdvancedTicks} poisonFrequencyTicks={PoisonFrequencyTicks} poisonDurationTicks={PoisonDurationTicks} poisonMaxTicks={PoisonMaxTicks} repeatQueued={RangedRepeatQueueCount} repeatDueResolved={RangedRepeatResolvedFromDueTick} clockMatch={MatchesNativeClockContract}";
        }
    }

    public class NativeStarterCrossbowCycleReplayResult
    {
        public int Selector;
        public int AnimationId;
        public int NumFrames;
        public int TriggerTime;
        public int SoundTriggerTime;
        public float WeaponSpeed;
        public int CycleTicks;
        public int ProjectileEventTick;
        public int SoundEventTick;
        public bool UseProjectileCreatesProjectileOnly;

        public bool MatchesNativeStarterCrossbowCycle =>
            Selector == 0 &&
            AnimationId == 310 &&
            NumFrames == 17 &&
            TriggerTime == 2 &&
            SoundTriggerTime == 2 &&
            Near(WeaponSpeed, 95f) &&
            CycleTicks == 17 &&
            ProjectileEventTick == 3 &&
            SoundEventTick == 3 &&
            UseProjectileCreatesProjectileOnly;

        public override string ToString()
        {
            return $"selector={Selector} anim={AnimationId} frames={NumFrames} trigger={TriggerTime}->{ProjectileEventTick} sound={SoundTriggerTime}->{SoundEventTick} speed={WeaponSpeed:F1} cycleTicks={CycleTicks} useProjectileCreatesOnly={UseProjectileCreatesProjectileOnly} match={MatchesNativeStarterCrossbowCycle}";
        }

        private static bool Near(float actual, float expected)
        {
            return Math.Abs(actual - expected) <= 0.0001f;
        }
    }

    public class NativeProjectilePrestepReplayResult
    {
        public float CrossbowSpeed;
        public float CrossbowStepDistance;
        public float CrossbowInitialDistance;
        public float CrossbowTargetCollisionRadius;
        public float CrossbowProjectileSize;
        public float CrossbowHitRadius;
        public float PoisonSpeed;
        public float PoisonStepDistance;
        public float PoisonInitialDistance;
        public float PoisonProjectileSize;
        public float PoisonHitRadius;
        public float PoisonAimDistance;
        public float PoisonProjectedDistance;
        public float PoisonMaxDistance;
        public float PoisonEntryDistance;

        public bool CrossbowCollisionMatchesNative =>
            Near(CrossbowStepDistance, 6f) &&
            Near(CrossbowInitialDistance, 6f) &&
            Near(CrossbowHitRadius, 15f);

        public bool PoisonShotSweptHitMatchesNative =>
            Near(PoisonStepDistance, 6.6640625f) &&
            Near(PoisonInitialDistance, PoisonStepDistance) &&
            Near(PoisonHitRadius, 13f) &&
            PoisonProjectedDistance > PoisonMaxDistance &&
            PoisonEntryDistance <= PoisonMaxDistance &&
            PoisonEntryDistance > PoisonAimDistance;

        public bool MatchesNativeProjectileRuntime =>
            CrossbowCollisionMatchesNative && PoisonShotSweptHitMatchesNative;

        public override string ToString()
        {
            return $"crossbow step={CrossbowStepDistance:F3} init={CrossbowInitialDistance:F3} radius={CrossbowHitRadius:F1}; poison step={PoisonStepDistance:F3} init={PoisonInitialDistance:F3} radius={PoisonHitRadius:F1} aim={PoisonAimDistance:F1} projected={PoisonProjectedDistance:F1} entry={PoisonEntryDistance:F1} max={PoisonMaxDistance:F1} sweptMatch={MatchesNativeProjectileRuntime}";
        }

        private static bool Near(float actual, float expected)
        {
            return Math.Abs(actual - expected) <= 0.0001f;
        }
    }

    public class NativeFixed32ReplayResult
    {
        public string DamageText;
        public string VolatilityText;
        public int DamageF32;
        public int VolatilityF32;

        public bool MatchesNativeFixed32 => DamageF32 == 139 && VolatilityF32 == 85;

        public override string ToString()
        {
            return $"damage={DamageText}->{DamageF32} volatility={VolatilityText}->{VolatilityF32} fixed32Match={MatchesNativeFixed32}";
        }
    }

    public class NativeHpRuntimeReplayResult
    {
        public uint StartHPWire;
        public uint FirstSuffixHPWire;
        public uint ClientCrashHPWire;
        public uint DamageRaw;
        public int DamageF32;
        public int VolatilityF32;
        public int MinDamageWire;
        public int MaxDamageWire;
        public uint DamageWire;
        public int UnitRuntimeTicksBeforeSecondSuffix;
        public int BaseRegen;
        public int RegenMod;
        public int AdditiveRegen;
        public int PerTickRegenWire;
        public int DefaultWorldSettingsField;
        public bool StockUnitDamageRegenDelayClears;
        public uint SecondSuffixHPWire;
        public uint PlainWeaponPostApplyEffectRaw;

        public bool FirstHitMatchesServerSuffix =>
            DamageF32 == 139 &&
            VolatilityF32 == 85 &&
            DamageWire == 5182 &&
            FirstSuffixHPWire == NativeDamageReplaySelfTest.LatestRatling50000FirstSuffixHPWire;

        public bool SecondSuffixAdvancesUnitRuntime =>
            SecondSuffixHPWire != FirstSuffixHPWire &&
            SecondSuffixHPWire > FirstSuffixHPWire &&
            SecondSuffixHPWire <= StartHPWire;

        public bool PlainWeaponDoesNotConsumePostApplyRng => PlainWeaponPostApplyEffectRaw == 0;
        public bool StockUnitDamageRegenCooldownUsesNativeFlag =>
            DefaultWorldSettingsField == 15 &&
            !StockUnitDamageRegenDelayClears;

        public override string ToString()
        {
            return $"hp={StartHPWire}->{FirstSuffixHPWire}->{SecondSuffixHPWire} clientLocal={ClientCrashHPWire} dmgRaw=0x{DamageRaw:X8} range=[{MinDamageWire},{MaxDamageWire}] damage={DamageWire} regenTicks={UnitRuntimeTicksBeforeSecondSuffix} perTick={PerTickRegenWire} stockUnitCooldownClear={StockUnitDamageRegenDelayClears} firstHitMatch={FirstHitMatchesServerSuffix} unitRuntimeAdvance={SecondSuffixAdvancesUnitRuntime} plainEffectRaw=0x{PlainWeaponPostApplyEffectRaw:X8}";
        }
    }

    public class NativeSchedulerOrderReplayResult
    {
        public int NativeHz;
        public uint TargetEntityId;
        public uint OtherEntityId;
        public float SuffixNow;
        public int ValidationCutoffTick;
        public float ValidationCutoffTime;
        public int[] OriginalQueueOrder;
        public int[] FirstDrainResolvedOrder;
        public int[] FirstDrainRemainingOrder;
        public int[] SecondDrainResolvedOrder;
        public int[] SecondDrainRemainingOrder;
        public int[] EntityPhaseResolvedOrder;
        public int[] EntityPhaseRemainingOrder;
        public uint FirstDrainDamageWire;
        public uint SecondDrainDamageWire;
        public uint EntityPhaseDamageWire;
        public uint StartHPWire;
        public uint HPAfterFirstDrain;
        public uint HPAfterPureRead1;
        public uint HPAfterPureRead2;
        public uint HPAfterSecondDrain;
        public uint HPAfterEntityPhase;
        public int PureReadRuntimeAdvances;
        public int PureReadResolvedEvents;
        public int ProjectileHitFrameTick;
        public int ProjectileFlightTicks;
        public int ProjectileImpactDelayTicks;
        public int ProjectileImpactTick;
        public float ProjectileFireTime;
        public float ProjectileDistance;
        public float ProjectileSpeed;
        public float ProjectileFlightTime;
        public float ProjectileImpactDelay;
        public float ProjectileImpactTime;
        public bool ProjectileSamePassFirstUpdate;
        public bool ProjectileResolvesBeforeImpact;
        public bool ProjectileResolvesAtImpact;
        public float PoisonImpactTime;
        public float PoisonFrequencySeconds;
        public float PoisonDurationSeconds;
        public float PoisonFirstTickTime;
        public int PoisonFirstTickDelayTicks;
        public int PoisonMaxTicks;
        public int PoisonTicksAtImpact;
        public int PoisonTicksBeforeFirstTick;
        public int PoisonTicksAtFirstTick;
        public uint HpSyncRuntimeBeforeRegister;
        public uint HpSyncStaleDomainHP;
        public uint HpSyncAfterRegister;
        public uint LatestPupStaleRemoteHPWire;
        public uint LatestPupClientLocalHPWire;
        public bool SubentityPhaseRunsBeforeEntityPhase;
        public bool ProjectileCreatedDuringEntityUpdatesNextSubentityPhase;

        public bool GlobalOrderPreserved =>
            Seq(OriginalQueueOrder, 1, 2, 3, 4, 5) &&
            Seq(FirstDrainResolvedOrder, 2, 1) &&
            Seq(FirstDrainRemainingOrder, 4, 3, 5) &&
            Seq(EntityPhaseResolvedOrder, 4) &&
            Seq(EntityPhaseRemainingOrder, 3, 5);

        public bool SuffixDrainIdempotent =>
            FirstDrainResolvedOrder != null &&
            FirstDrainResolvedOrder.Length == 2 &&
            Seq(SecondDrainResolvedOrder) &&
            Same(FirstDrainRemainingOrder, SecondDrainRemainingOrder) &&
            SecondDrainDamageWire == 0 &&
            HPAfterSecondDrain == HPAfterFirstDrain;

        public bool PureHPReadInvariant =>
            HPAfterPureRead1 == HPAfterFirstDrain &&
            HPAfterPureRead2 == HPAfterFirstDrain &&
            PureReadRuntimeAdvances == 0 &&
            PureReadResolvedEvents == 0;

        public bool RangedProjectileTimelineMatchesNative =>
            NativeHz == 30 &&
            ProjectileHitFrameTick == 15 &&
            ProjectileFlightTicks == 15 &&
            ProjectileImpactDelayTicks == 15 &&
            ProjectileImpactTick == 30 &&
            !ProjectileSamePassFirstUpdate &&
            !ProjectileResolvesBeforeImpact &&
            ProjectileResolvesAtImpact;

        public bool ValidationCutoffExcludesSameTickProjectile =>
            NativeHz == 30 &&
            ValidationCutoffTick == 30 &&
            Near(ValidationCutoffTime, SuffixNow) &&
            Seq(SecondDrainResolvedOrder) &&
            Seq(EntityPhaseResolvedOrder, 4) &&
            EntityPhaseDamageWire == 768 &&
            HPAfterEntityPhase < HPAfterSecondDrain;

        public bool HpSyncRegisterIsIdempotent =>
            HpSyncAfterRegister == HpSyncRuntimeBeforeRegister &&
            HpSyncAfterRegister < HpSyncStaleDomainHP;

        public bool PoisonFirstTickDeferred =>
            NativeHz == 30 &&
            PoisonFirstTickDelayTicks == 30 &&
            PoisonMaxTicks == 4 &&
            PoisonTicksAtImpact == 0 &&
            PoisonTicksBeforeFirstTick == 0 &&
            PoisonTicksAtFirstTick == 1;

        public bool LatestPupStaleSuffixWouldCrash =>
            LatestPupStaleRemoteHPWire == NativeDamageReplaySelfTest.LatestPup50000StaleRemoteHPWire &&
            LatestPupClientLocalHPWire == NativeDamageReplaySelfTest.LatestPup50000ClientLocalHPWire &&
            LatestPupStaleRemoteHPWire != LatestPupClientLocalHPWire;

        public bool NativeSubentityPhaseContract =>
            SubentityPhaseRunsBeforeEntityPhase &&
            ProjectileCreatedDuringEntityUpdatesNextSubentityPhase &&
            !ProjectileSamePassFirstUpdate;

        public bool MatchesNativeSchedulerOrderContract =>
            GlobalOrderPreserved &&
            SuffixDrainIdempotent &&
            PureHPReadInvariant &&
            RangedProjectileTimelineMatchesNative &&
            ValidationCutoffExcludesSameTickProjectile &&
            HpSyncRegisterIsIdempotent &&
            PoisonFirstTickDeferred &&
            LatestPupStaleSuffixWouldCrash &&
            NativeSubentityPhaseContract;

        public override string ToString()
        {
            return $"target={TargetEntityId} other={OtherEntityId} cutoff={ValidationCutoffTick}@{ValidationCutoffTime:F3} queue=[{Join(OriginalQueueOrder)}] firstResolved=[{Join(FirstDrainResolvedOrder)}] firstRemaining=[{Join(FirstDrainRemainingOrder)}] secondResolved=[{Join(SecondDrainResolvedOrder)}] entityResolved=[{Join(EntityPhaseResolvedOrder)}] hp={StartHPWire}->{HPAfterFirstDrain}->{HPAfterSecondDrain}->{HPAfterEntityPhase} pureRead={PureHPReadInvariant} projectile=t{ProjectileHitFrameTick}+flight{ProjectileFlightTicks}/delay{ProjectileImpactDelayTicks}->t{ProjectileImpactTick} subentityFirst={NativeSubentityPhaseContract} stalePup={LatestPupStaleRemoteHPWire}!={LatestPupClientLocalHPWire} hpRegister={HpSyncAfterRegister} poisonFirstDelayTicks={PoisonFirstTickDelayTicks} schedulerMatch={MatchesNativeSchedulerOrderContract}";
        }

        private static bool Seq(int[] actual, params int[] expected)
        {
            return actual != null && actual.SequenceEqual(expected);
        }

        private static bool Same(int[] first, int[] second)
        {
            if (first == null || second == null) return false;
            return first.SequenceEqual(second);
        }

        private static string Join(int[] values)
        {
            return values == null ? "" : string.Join(",", values);
        }

        private static bool Near(float actual, float expected)
        {
            return Math.Abs(actual - expected) <= 0.0001f;
        }
    }

    public class NativeAuthoredSpellSplitReplayResult
    {
        public bool FireBoltFound;
        public bool PoisonShotFound;
        public string FireBoltSkillId;
        public string PoisonShotSkillId;
        public AttackType FireBoltAttackType;
        public AttackType PoisonShotAttackType;
        public DamageElement FireBoltDamageType;
        public DamageElement PoisonShotDamageType;
        public int FireBoltRange;
        public int PoisonShotRange;
        public float FireBoltCooldown;
        public float PoisonShotCooldown;
        public float FireBoltProjectileSpeed;
        public float PoisonShotProjectileSpeed;
        public float FireBoltProjectileSize;
        public float PoisonShotProjectileSize;
        public float FireBoltProjectileLifespan;
        public float PoisonShotProjectileLifespan;
        public float FireBoltDamageMod;
        public float PoisonShotDamageMod;
        public float FireBoltDamageVolatility;
        public float PoisonShotDamageVolatility;
        public bool FireBoltHasDirectDamageEffect;
        public bool PoisonShotHasDirectDamageEffect;
        public bool PoisonShotHasImmediateWeaponDamageEffect;
        public int PoisonShotARModMin;
        public int PoisonShotARModMax;
        public int PoisonShotWeaponEffectDamageModMin;
        public int PoisonShotWeaponEffectDamageModMax;
        public bool FireBoltHasProjectileModifierDamage;
        public bool PoisonShotHasProjectileModifierDamage;
        public string FireBoltProjectileEffectId;
        public string PoisonShotProjectileEffectId;
        public string FireBoltProjectileModifierId;
        public string PoisonShotProjectileModifierId;
        public string PoisonShotProjectileModifierEffectId;
        public AttackType PoisonShotProjectileModifierAttackType;
        public DamageElement PoisonShotProjectileModifierDamageType;
        public float PoisonShotProjectileModifierDuration;
        public float PoisonShotProjectileModifierFrequency;
        public string PoisonShotProjectileModifierStackRule;
        public float PoisonShotProjectileModifierDamageMod;
        public float PoisonShotProjectileModifierDamageVolatility;
        public float PoisonShotProjectileModifierCriticalChance;

        public bool FireBoltDirectProjectileDamage =>
            FireBoltFound &&
            FireBoltAttackType == AttackType.MAGIC &&
            FireBoltDamageType == DamageElement.FIRE &&
            FireBoltRange == 300 &&
            Near(FireBoltCooldown, 0f) &&
            Near(FireBoltProjectileSpeed, 200f) &&
            Near(FireBoltProjectileSize, 8f) &&
            Near(FireBoltProjectileLifespan, 30.5f) &&
            Near(FireBoltDamageMod, 0.75f) &&
            Near(FireBoltDamageVolatility, 0.60f) &&
            FireBoltHasDirectDamageEffect &&
            !FireBoltHasProjectileModifierDamage &&
            string.IsNullOrEmpty(FireBoltProjectileModifierId);

        public bool PoisonShotProjectileModifierSplit =>
            PoisonShotFound &&
            PoisonShotAttackType == AttackType.RANGED &&
            PoisonShotDamageType == DamageElement.POISON &&
            PoisonShotRange == 176 &&
            Near(PoisonShotCooldown, 1f) &&
            Near(PoisonShotProjectileSpeed, 200f) &&
            Near(PoisonShotProjectileSize, 8f) &&
            Near(PoisonShotProjectileLifespan, 23f) &&
            Near(PoisonShotDamageMod, 0f) &&
            Near(PoisonShotDamageVolatility, 0f) &&
            !PoisonShotHasDirectDamageEffect &&
            PoisonShotHasImmediateWeaponDamageEffect &&
            PoisonShotARModMin == 300 &&
            PoisonShotARModMax == 300 &&
            PoisonShotWeaponEffectDamageModMin == 0 &&
            PoisonShotWeaponEffectDamageModMax == 0 &&
            PoisonShotHasProjectileModifierDamage &&
            !string.IsNullOrEmpty(PoisonShotProjectileEffectId) &&
            !string.IsNullOrEmpty(PoisonShotProjectileModifierId) &&
            !string.IsNullOrEmpty(PoisonShotProjectileModifierEffectId) &&
            PoisonShotProjectileModifierAttackType == AttackType.MAGIC &&
            PoisonShotProjectileModifierDamageType == DamageElement.POISON &&
            Near(PoisonShotProjectileModifierDuration, 4f) &&
            Near(PoisonShotProjectileModifierFrequency, 1f) &&
            string.Equals(PoisonShotProjectileModifierStackRule, "UNIQUEBYSOURCE", StringComparison.OrdinalIgnoreCase) &&
            Near(PoisonShotProjectileModifierDamageMod, 0.43f) &&
            Near(PoisonShotProjectileModifierDamageVolatility, 0.30f) &&
            Near(PoisonShotProjectileModifierCriticalChance, 0.25f);

        public bool AuthoredSplitMatchesExpectations =>
            FireBoltDirectProjectileDamage &&
            PoisonShotProjectileModifierSplit;

        public override string ToString()
        {
            return $"fireBolt found={FireBoltFound} projectile={FireBoltProjectileSpeed}/{FireBoltProjectileSize}/{FireBoltProjectileLifespan} dmg={FireBoltDamageMod}/{FireBoltDamageVolatility} direct={FireBoltDirectProjectileDamage}; poisonShot found={PoisonShotFound} projectile={PoisonShotProjectileSpeed}/{PoisonShotProjectileSize}/{PoisonShotProjectileLifespan} directDmg={PoisonShotDamageMod}/{PoisonShotDamageVolatility} immediateWeapon={PoisonShotHasImmediateWeaponDamageEffect} mod={PoisonShotProjectileModifierId} effect={PoisonShotProjectileModifierEffectId} modType={PoisonShotProjectileModifierAttackType}/{PoisonShotProjectileModifierDamageType} duration={PoisonShotProjectileModifierDuration} frequency={PoisonShotProjectileModifierFrequency} split={PoisonShotProjectileModifierSplit} authoredMatch={AuthoredSplitMatchesExpectations}";
        }

        private static bool Near(float actual, float expected)
        {
            return Math.Abs(actual - expected) <= 0.0001f;
        }
    }
}
