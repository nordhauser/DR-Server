using UnityEngine;
using DungeonRunners.Networking;

namespace DungeonRunners.Combat
{
    /// <summary>
    /// Section 10d wiring — concrete <see cref="MonsterAttackController.IDamageTargetProvider"/>
    /// that resolves <see cref="CombatPlayer"/> via <see cref="CombatManager.TryGetCombatPlayerForController"/>
    /// and routes <c>ApplyDamage</c> to <see cref="PlayerState.TakeRuntimeDamage(uint)"/>.
    ///
    /// <para>
    /// Damage is applied via <see cref="PlayerState.TakeDamage(uint,float,bool)"/> (the
    /// client-sync path) so the replayed hit reaches the outbound HP suffix the client
    /// validates against. The client-event-replay model keeps the server's authoritative
    /// HP byte-matched to the client's own local sim, then pushes it. (The earlier no-sync
    /// <c>TakeRuntimeDamage</c> path left the hit invisible to the wire — regen flushed it
    /// back to full before the suffix was built — so the client was always told full HP and
    /// tripped Validate on the first move.)
    /// </para>
    /// </summary>
    public sealed class CombatPlayerDamageTargetProvider : MonsterAttackController.IDamageTargetProvider
    {
        private readonly CombatManager _combatManager;

        public CombatPlayerDamageTargetProvider(CombatManager combatManager)
        {
            _combatManager = combatManager;
        }

        public bool TryGetTarget(uint entityId, out PlayerUnitStats stats)
        {
            stats = default;
            if (!_combatManager.TryGetCombatPlayerForController(entityId, out var player) || player == null || !player.IsAlive)
                return false;

            // v1: PlayerUnitStatsBuilder uses sensible defaults. Once we have a stat-system
            // mirror (gear + level → cached fields), this will pull real values.
            int level = player.PlayerState != null ? Mathf.Max(1, player.PlayerState.Level) : 1;
            stats = PlayerUnitStatsBuilder.Build(player, level);
            return true;
        }

        public void ApplyReflectedDamageToMob(uint mobEntityId, uint playerEntityId, uint wireDamage)
        {
            if (wireDamage == 0) return;
            var monster = _combatManager.GetMonster(mobEntityId);
            if (monster == null) return;
            string srcName = "reflect:unknown";
            if (_combatManager.TryGetCombatPlayerForController(playerEntityId, out var player) && player != null)
                srcName = $"reflect:{player.Name}#{playerEntityId}";
            _combatManager.ApplyNativePlayerDamageToMonsterWire(
                monster, wireDamage, srcName, out uint oldHP, out uint newHP, out bool died);
            Debug.LogError(
                $"[MOB-DAMAGE-APPLY] reflected mob={monster.Name}#{mobEntityId} wire={wireDamage} hp={oldHP}->{newHP}/{monster.MaxHPWire} died={died}");
        }

        public void ApplyDamage(uint entityId, uint wireDamage)
        {
            if (!_combatManager.TryGetCombatPlayerForController(entityId, out var player) || player?.PlayerState == null)
                return;

            uint hpBefore = player.PlayerState.CurrentHPWire;
            // Stat-parity fix 2026-05-30: route replayed mob->player damage through the
            // SYNC-updating path (was TakeRuntimeDamage = no client sync). The no-sync path
            // left _clientSyncHPWire and the native tick clock stale, so the pre-suffix
            // FlushPlayerHPRuntimeBeforeSync -> AdvanceClientSyncHP regenerated the hit back
            // to full before the outbound suffix read SynchHP -> the client was perpetually
            // told full HP while its own local sim showed the damage -> Validate mismatch on
            // the first move -> desync popup. TakeDamage(nativeTime) syncs the HP baseline,
            // resets the tick clock to the hit moment, and applies the post-damage regen
            // cooldown, so the damaged value survives into the HP the client validates against.
            float nativeTime = _combatManager.GetNativeCombatTime();
            // advanceBeforeDamage:false — mirror the client. Unit::onApplyDamage @0x50be50 just
            // subtracts; it does NOT regen before applying damage (regen is the separate Unit::update
            // @0x5093e0 tick). The earlier `true` bulk-pre-regened to full before each hit (a hit
            // logged hp 93696->97454, +15), corrupting the running HP total. Regen is handled by the
            // ServerTick AdvanceClientSyncHP (the Unit::update mirror).
            player.PlayerState.TakeDamage(wireDamage, nativeTime, advanceBeforeDamage: false);
            uint hpAfter = player.PlayerState.CurrentHPWire;
            Debug.LogError(
                $"[MOB-DAMAGE-APPLY] player={player.Name}#{entityId} wireDamage={wireDamage} " +
                $"hp={hpBefore}->{hpAfter} synchHP={player.PlayerState.SynchHP} (256=1HP)");

            // C9 hook point (plan vivid-marinating-pixel, 2026-05-28). When the
            // 1/15 HP fraction crosses a tick, call
            //   UnityGameServer.Instance.BroadcastPlayerHealthToGroup(conn)
            // so party-frame HP bars on other clients see the drop in real time.
            // Currently CombatPlayer doesn't carry an RRConnection; add one (or a
            // lookup by entityId) when B5 (EnableServerMobDamage=true) flips and
            // this path goes live. Cheap dedup: track previous hp15 = hpBefore*15/max
            // and only broadcast when (hpAfter*15/max) differs.
        }

        // S12 range gate: distance² between mob (CombatManager.GetMonster.PosX/Y) and player
        // (CombatPlayer.PosX/Y). Returns false if either is missing — the controller will skip
        // the range check in that case (safer than treating missing data as out-of-range).
        public bool TryGetEngagementDistanceSquared(uint mobEntityId, uint playerEntityId, out float distSquared)
        {
            distSquared = 0f;
            if (!_combatManager.TryGetCombatPlayerForController(playerEntityId, out var player) || player == null)
                return false;
            var monster = _combatManager.GetMonster(mobEntityId);
            if (monster == null) return false;
            float dx = monster.PosX - player.PosX;
            float dy = monster.PosY - player.PosY;
            distSquared = dx * dx + dy * dy;
            return true;
        }
    }
}
