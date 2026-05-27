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
