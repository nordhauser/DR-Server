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
    /// Damage is applied via the runtime path (no client-sync packet) — the server
    /// keeps its own authoritative HP for death detection while the 666 client
    /// continues to own its on-screen HP bar. Matches the "no client patches"
    /// constraint and the HP-sync-popup avoidance goal.
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
            player.PlayerState.TakeRuntimeDamage(wireDamage);
            uint hpAfter = player.PlayerState.CurrentHPWire;
            Debug.LogError(
                $"[MOB-DAMAGE-APPLY] player={player.Name}#{entityId} wireDamage={wireDamage} " +
                $"hp={hpBefore}->{hpAfter} (256=1HP)");

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

            // B4.2 diag (2026-05-28): mob attacks player on client but server thinks out-of-range.
            // Dump both sides' tracked positions so we can compare against x32dbg-read client values.
            // Throttled to ~1 per second to avoid flooding (called every controller tick).
            _posDiagCounter++;
            if (_posDiagCounter % 30 == 0)
            {
                Debug.LogError(
                    $"[POS-DIAG] mob#{mobEntityId} server=({monster.PosX:F1},{monster.PosY:F1}) " +
                    $"player#{playerEntityId} server=({player.PosX:F1},{player.PosY:F1}) " +
                    $"dx={dx:F1} dy={dy:F1} distSq={distSquared:F1}");
            }
            return true;
        }
        private static int _posDiagCounter;
    }
}
