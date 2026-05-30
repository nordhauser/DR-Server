using System.Collections.Generic;
using UnityEngine;

namespace DungeonRunners.Combat
{
    /// <summary>
    /// Section 10 task S10.4 — per-mob attack-swing controller. Driven from the
    /// CombatManager 30Hz tick. When a mob has aggro on a player and the swing
    /// cooldown ticks down to zero, calls <see cref="MonsterDamageComputer.ComputeSwing"/>
    /// and emits the result.
    ///
    /// <para>
    /// <b>v1 scope:</b> the controller computes swings + emits diagnostic logs. It does
    /// NOT yet apply damage to the player's HP — that's S10.5 (<see cref="PlayerState"/>
    /// integration). For now the controller is a "shadow" of what a future authoritative
    /// damage pipeline would do.
    /// </para>
    ///
    /// <para>
    /// <b>Swing period approximation:</b> the audit spec says the period =
    /// <c>animationDurationTicks × 100 / attackSpeedMod256</c>, where animation duration
    /// is per (mob class, weapon) from the AnimationList .gc subtree. We don't have the
    /// AnimationList parser yet, so v1 approximates via weapon CoolDown:
    /// <code>periodTicks = (CoolDown_sec × 30) / (AttackSpeed × MonsterAttackSpeed/100)</code>
    /// For Abba_Labba (AttackSpeed=0.8, CoolDown=1.75): periodTicks ≈ 65 ticks ≈ 2.2 sec.
    /// </para>
    /// </summary>
    public sealed class MonsterAttackController
    {
        private static MonsterAttackController _instance;
        public static MonsterAttackController Instance => _instance ??= new MonsterAttackController();

        public sealed class MobCombatState
        {
            public uint MobEntityId;
            public MonsterUnitStats Stats;
            public int SwingPeriodTicks;
            public int CooldownTicks;
            public uint TargetPlayerEntityId;       // 0 = no target
            public int SwingCount;
            public int LastDamageDealt;
            public float AttackRangeSquared;        // weapon attack range² for the range gate (0 = unset)
            public int OutOfRangeSkipCount;         // diagnostic counter for skipped swings
            public int LastSwingRngPos;             // B4.2 diag: _roomRng.CallsSinceReseed after the last swing for this mob
        }

        private readonly Dictionary<uint, MobCombatState> _states =
            new Dictionary<uint, MobCombatState>();

        /// <summary>
        /// Section 10 task S10.5: when true, swings actually apply damage to the player's
        /// PlayerState via <see cref="IDamageTargetProvider.ApplyDamage"/>. Default false —
        /// shadow-mode while we validate roll-by-roll against client captures. Flip to true
        /// once <c>[MOB-SWING]</c> logs prove the server's damage values match the client's.
        /// </summary>
        public static bool EnableServerMobDamage = true;

        public int ActiveMobCount => _states.Count;

        /// <summary>Number of registered mobs that have an aggro target.</summary>
        public int AttackingMobCount
        {
            get
            {
                int n = 0;
                foreach (var s in _states.Values) if (s.TargetPlayerEntityId != 0) n++;
                return n;
            }
        }

        /// <summary>Register a mob for combat-tick attention.</summary>
        public void Register(uint mobEntityId, MonsterUnitStats stats, float weaponCoolDownSec, float attackSpeedScalar, int monsterAttackSpeedKnob, float attackRange = 0f)
        {
            float speedRatio = (attackSpeedScalar <= 0f ? 1f : attackSpeedScalar)
                             * (monsterAttackSpeedKnob <= 0 ? 1f : monsterAttackSpeedKnob / 100f);
            if (speedRatio <= 0f) speedRatio = 1f;
            int period = Mathf.Max(1, Mathf.RoundToInt(weaponCoolDownSec * 30f / speedRatio));

            // Pre-square the attack range so Tick avoids sqrt on the hot path.
            // 0 = unset (no range gate; behaves like pre-S11.2 — swings whenever target set).
            // B4.4 fix (2026-05-28): include CombatManager.NATIVE_CONTACT_RANGE_EPSILON (1/16)
            // tolerance to match the legacy contact check. Without it, mobs settling exactly at
            // contact-range failed the controller's strict `distSq > rangeSq` by sub-unit
            // floating-point drift — e.g. range=14, distSq=196.1 > 196.0 → silent SKIP forever.
            float rangeWithEpsilon = attackRange + 1f / 16f;
            float attackRangeSquared = attackRange > 0f ? rangeWithEpsilon * rangeWithEpsilon : 0f;

            // 2026-05-29 CLOBBER FIX: RegisterMonsterForServerCombat fires repeatedly for the
            // same mob (spawn + WanderSimulator.RegisterMonster paths; observed ×7 per mob).
            // The old code overwrote _states[id] with a fresh state every time, resetting
            // TargetPlayerEntityId->0 and CooldownTicks->period. A re-register landing AFTER
            // AggroMonster's SetTarget() silently wiped the aggro target, so Tick saw target==0
            // and skipped — producing 0 swings AND 0 skips forever (no server mob damage ->
            // client/server HP divergence -> SyncErrorRespawnDialog). Re-registration must
            // refresh config WITHOUT disturbing an in-progress engagement.
            if (_states.TryGetValue(mobEntityId, out var existing))
            {
                existing.Stats = stats;
                existing.SwingPeriodTicks = period;
                existing.AttackRangeSquared = attackRangeSquared;
                // PRESERVE TargetPlayerEntityId + CooldownTicks (the live engagement state).
                return;
            }

            _states[mobEntityId] = new MobCombatState
            {
                MobEntityId = mobEntityId,
                Stats = stats,
                SwingPeriodTicks = period,
                CooldownTicks = period,    // first swing waits one full period
                TargetPlayerEntityId = 0,
                AttackRangeSquared = attackRangeSquared,
            };
        }

        public void Unregister(uint mobEntityId) => _states.Remove(mobEntityId);

        public void SetTarget(uint mobEntityId, uint targetPlayerEntityId)
        {
            if (_states.TryGetValue(mobEntityId, out var s))
                s.TargetPlayerEntityId = targetPlayerEntityId;
        }

        public void ClearTarget(uint mobEntityId)
        {
            if (_states.TryGetValue(mobEntityId, out var s))
                s.TargetPlayerEntityId = 0;
        }

        public bool TryGetState(uint mobEntityId, out MobCombatState state) =>
            _states.TryGetValue(mobEntityId, out state);

        /// <summary>
        /// Drive all registered mobs by one tick. Returns the number of swings that
        /// fired this tick (typically 0; bursts during simultaneous engagements).
        /// </summary>
        public int Tick(MersenneTwister rng, IDamageTargetProvider targets)
        {
            int swings = 0;
            // P1: when client-event-replay is active, the server replays per-mob swings from the
            // client's reported events (CombatManager.ApplyClientSwingEvent); MAC's own cooldown-
            // driven swinging would double-apply, so yield entirely.
            if (CombatManager.UseClientEventReplay) return 0;
            foreach (var s in _states.Values)
            {
                if (s.TargetPlayerEntityId == 0) continue;
                if (s.CooldownTicks > 0)
                {
                    s.CooldownTicks--;
                    continue;
                }

                // Cooldown expired: swing now
                if (!targets.TryGetTarget(s.TargetPlayerEntityId, out var targetStats))
                {
                    // Target left zone / died — drop target
                    s.TargetPlayerEntityId = 0;
                    continue;
                }

                // S12 range gate: skip swing if mob is out of attack range. Aggro keeps the
                // target set even when mob is far away (no out-of-range clear). Without this
                // check, the simulator hallucinates swings for mobs that never engaged the
                // player on the client. Cooldown stays at 0 so the first in-range tick fires
                // immediately (matches typical MMO "queued swing on approach" behavior).
                if (s.AttackRangeSquared > 0f &&
                    targets.TryGetEngagementDistanceSquared(s.MobEntityId, s.TargetPlayerEntityId, out float distSq) &&
                    distSq > s.AttackRangeSquared)
                {
                    s.OutOfRangeSkipCount++;
                    // Throttled log — only on first skip and every 30th after to avoid spam
                    if (s.OutOfRangeSkipCount == 1 || s.OutOfRangeSkipCount % 30 == 0)
                    {
                        Debug.LogError(
                            $"[MOB-SWING-SKIP] mob={s.MobEntityId} -> player={s.TargetPlayerEntityId} " +
                            $"distSq={distSq:F1} rangeSq={s.AttackRangeSquared:F1} skipCount={s.OutOfRangeSkipCount}");
                    }
                    continue;
                }
                s.OutOfRangeSkipCount = 0;
                // 2026-05-29 deterministic-mirror via client RNG-state sharing.
                // Before each swing, fast-forward _roomRng to match client's reported
                // call counter (via opcode 0x66 from DungeonRunners_RNG.exe). Server
                // consumes throwaway Generates to align position with client; then
                // ComputeSwing fires 3 generates that byte-match client's r1/r2/r3.
                int ffConsumed = CombatManager.Instance.FastForwardRoomRngToClient();

                // B4.2 diag (2026-05-28): snapshot rng position before and after the swing.
                // Δ between consecutive swings tells us how many _roomRng calls happened in
                // between (the controller does exactly 3 per swing; anything extra = drift source).
                int rngPosBefore = rng.CallsSinceReseed;
                var result = MonsterDamageComputer.ComputeSwing(s.Stats, targetStats, rng);
                int rngPosAfter = rng.CallsSinceReseed;
                int gapSinceLastSwing = s.LastSwingRngPos > 0 ? rngPosBefore - s.LastSwingRngPos : 0;
                s.LastSwingRngPos = rngPosAfter;
                s.SwingCount++;
                s.LastDamageDealt = result.Damage;

                // C6 — apply DamageTakenMod + elemental resistance, mirroring the client's
                // Unit::onQueryApplyDamage which runs on the target side before HP write.
                uint preResistDamage = result.Damage > 0 ? (uint)result.Damage : 0u;
                uint postResistDamage = MonsterDamageComputer.OnQueryApplyDamage(
                    preResistDamage, result.DamageType, targetStats, out bool resisted);

                // C7 — reflect (thorns). Use the player's reflect fields against the mob's
                // attack-style-derived event kind. v1: 0 for vanilla L1 player (no equipment
                // modifiers wired yet); lights up when player gear with MeleeDamageReflectB /
                // RangeDamageReflectB / DamageReflectBonus modifiers is mirrored into stats.
                byte eventKind = MonsterDamageComputer.EventKindFromAttackStyle(result.AttackStyle);
                uint reflectedToMob = MonsterDamageComputer.ComputeReflectedDamage(
                    postResistDamage, eventKind,
                    targetStats.BaseReflectPct, targetStats.MeleeReflectBonusPct, targetStats.RangedReflectBonusPct);

                Debug.LogError(
                    $"[MOB-SWING] mob={s.MobEntityId} -> player={s.TargetPlayerEntityId} " +
                    $"hit={result.Hit} blocked={result.Blocked} crit={result.Crit} dmg={result.Damage} " +
                    $"AR={result.AttackerAR} DR={result.TargetDR} hitChance={result.HitChanceScaled} " +
                    $"r1={result.R1Hit:X8} r2={result.R2Block:X8} r3={result.R3Damage:X8} " +
                    $"hitRoll={result.HitRoll} blockRoll={result.BlockRoll} dmgRange=[{result.DamageMin}..{result.DamageMax}] " +
                    $"rngPosBefore={rngPosBefore} rngPosAfter={rngPosAfter} mobSwingRngGap={gapSinceLastSwing} " +
                    $"dmgType={result.DamageType} postResist={postResistDamage} resisted={resisted} " +
                    $"reflected={reflectedToMob} applyDamage={EnableServerMobDamage}");

                // Section 10 task S10.5: apply damage to the player when feature flag is on
                // and the swing landed unblocked. The wire damage value is in 256-fixed-point
                // (256 = 1 HP); PlayerState.TakeRuntimeDamage expects this format directly.
                if (EnableServerMobDamage && result.Hit && !result.Blocked && postResistDamage > 0)
                {
                    targets.ApplyDamage(s.TargetPlayerEntityId, postResistDamage);

                    // C7: apply reflected damage to the mob via the player→mob path. Only
                    // fires when player has non-zero reflect (gated by reflectedToMob > 0).
                    if (reflectedToMob > 0)
                    {
                        Debug.LogError(
                            $"[MOB-REFLECT] mob={s.MobEntityId} <- player={s.TargetPlayerEntityId} " +
                            $"incomingToPlayer={postResistDamage} reflectPct={(eventKind == 1 ? targetStats.BaseReflectPct + targetStats.MeleeReflectBonusPct : eventKind == 2 ? targetStats.BaseReflectPct + targetStats.RangedReflectBonusPct : targetStats.BaseReflectPct)} " +
                            $"reflected={reflectedToMob} eventKind={eventKind}");
                        targets.ApplyReflectedDamageToMob(s.MobEntityId, s.TargetPlayerEntityId, reflectedToMob);
                    }
                }

                s.CooldownTicks = s.SwingPeriodTicks;
                swings++;
            }
            return swings;
        }

        /// <summary>
        /// Provider for damage targets — abstracts whether the target is a CombatPlayer,
        /// a Player struct, or a test stub.
        /// </summary>
        public interface IDamageTargetProvider
        {
            bool TryGetTarget(uint entityId, out PlayerUnitStats stats);

            /// <summary>
            /// Apply <paramref name="wireDamage"/> (256 = 1 HP) to the player. Called only
            /// when <see cref="MonsterAttackController.EnableServerMobDamage"/> is true and
            /// the swing actually landed. v1 impl should route to
            /// <c>PlayerState.TakeRuntimeDamage(wireDamage)</c>.
            /// </summary>
            void ApplyDamage(uint entityId, uint wireDamage);

            /// <summary>
            /// C7 reflect (thorns): when the mob's attack lands on a player who carries
            /// reflect equipment, send <paramref name="wireDamage"/> back to the mob.
            /// Implementation should route to <see cref="CombatManager.ApplyNativePlayerDamageToMonsterWire"/>
            /// (which applies the mob's DamageTakenMod + death detection). Called only when
            /// <see cref="MonsterAttackController.EnableServerMobDamage"/> is true AND the
            /// player carries non-zero reflect — i.e. once equipment-modifier mirror is wired.
            /// </summary>
            void ApplyReflectedDamageToMob(uint mobEntityId, uint playerEntityId, uint wireDamage);

            /// <summary>
            /// S12 range gate: distance² between mob and player on the XY plane (world units).
            /// Returns false if either entity is unknown (caller will skip range check rather
            /// than treat as out-of-range). Squared to avoid sqrt on the hot path.
            /// </summary>
            bool TryGetEngagementDistanceSquared(uint mobEntityId, uint playerEntityId, out float distSquared);
        }
    }
}
