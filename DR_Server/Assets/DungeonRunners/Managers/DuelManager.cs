using System;
using System.Collections.Generic;
using UnityEngine;

namespace DungeonRunners.Managers
{
    /// <summary>
    /// Manages PVP duel lifecycle: challenge → accept/decline → combat → win/loss → persist.
    /// Binary-verified opcodes from GroupClient channel 0x0B:
    ///   0x2D = requestPVPDuel(CharSQLID)  — challenger sends target's ID
    ///   0x2E = acceptPVPDuel()            — target accepts
    ///   0x2F = declinePVPDuel()           — target declines
    ///
    /// Duel state is tracked per-player (by login name).
    /// Both players must be online, alive, not already in a duel, and not in a dungeon boss fight.
    /// </summary>
    public class DuelManager
    {
        public enum DuelState
        {
            None,
            PendingAccept,    // Waiting for target to accept/decline
            Countdown,        // Accepted, 5-second countdown before combat
            Active,           // Combat in progress
        }

        public class DuelInfo
        {
            public string ChallengerLogin;
            public string TargetLogin;
            public uint ChallengerCharSqlId;
            public uint TargetCharSqlId;
            public DuelState State;
            public DateTime CreatedAt;
            public DateTime? CombatStartAt;

            /// <summary>Timeout for pending accept (seconds).</summary>
            public const int AcceptTimeoutSec = 30;
            /// <summary>Countdown before combat starts (seconds).</summary>
            public const int CountdownSec = 5;

            public bool IsExpired => State == DuelState.PendingAccept &&
                (DateTime.UtcNow - CreatedAt).TotalSeconds > AcceptTimeoutSec;
        }

        // Active duels indexed by BOTH participants' login names for fast lookup.
        private readonly Dictionary<string, DuelInfo> _activeDuels = new Dictionary<string, DuelInfo>(StringComparer.OrdinalIgnoreCase);

        // Cooldown tracking: login → last duel end time
        private readonly Dictionary<string, DateTime> _cooldowns = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Cooldown between duels in seconds.</summary>
        public int CooldownSeconds { get; set; } = 60;

        /// <summary>Minimum level to duel.</summary>
        public int MinLevel { get; set; } = 5;

        /// <summary>
        /// Check if a player is currently in a duel (any state).
        /// </summary>
        public bool IsInDuel(string loginName) => _activeDuels.ContainsKey(loginName);

        /// <summary>
        /// Get the duel info for a player, or null if not in a duel.
        /// </summary>
        public DuelInfo GetDuel(string loginName)
        {
            _activeDuels.TryGetValue(loginName, out var info);
            return info;
        }

        /// <summary>
        /// Attempt to start a duel challenge.
        /// Returns null on success, or an error message string on failure.
        /// </summary>
        public string TryChallenge(string challengerLogin, uint challengerCharSqlId,
            string targetLogin, uint targetCharSqlId, int challengerLevel, int targetLevel)
        {
            // Validation
            if (string.Equals(challengerLogin, targetLogin, StringComparison.OrdinalIgnoreCase))
                return "You cannot duel yourself.";

            if (challengerLevel < MinLevel)
                return $"You must be at least level {MinLevel} to duel.";

            if (targetLevel < MinLevel)
                return $"Your target must be at least level {MinLevel} to duel.";

            if (IsInDuel(challengerLogin))
                return "You are already in a duel.";

            if (IsInDuel(targetLogin))
                return "That player is already in a duel.";

            // Cooldown check
            if (_cooldowns.TryGetValue(challengerLogin, out var lastEnd) &&
                (DateTime.UtcNow - lastEnd).TotalSeconds < CooldownSeconds)
            {
                int remaining = CooldownSeconds - (int)(DateTime.UtcNow - lastEnd).TotalSeconds;
                return $"Duel cooldown: {remaining}s remaining.";
            }

            // Create the duel
            var duel = new DuelInfo
            {
                ChallengerLogin = challengerLogin,
                TargetLogin = targetLogin,
                ChallengerCharSqlId = challengerCharSqlId,
                TargetCharSqlId = targetCharSqlId,
                State = DuelState.PendingAccept,
                CreatedAt = DateTime.UtcNow,
            };

            _activeDuels[challengerLogin] = duel;
            _activeDuels[targetLogin] = duel;

            Debug.LogError($"[PVP-DUEL] {challengerLogin} challenged {targetLogin} to a duel");
            return null; // success
        }

        /// <summary>
        /// Target accepts the duel. Returns the DuelInfo or null if invalid.
        /// </summary>
        public DuelInfo TryAccept(string targetLogin)
        {
            if (!_activeDuels.TryGetValue(targetLogin, out var duel))
                return null;

            if (duel.State != DuelState.PendingAccept)
                return null;

            if (!string.Equals(duel.TargetLogin, targetLogin, StringComparison.OrdinalIgnoreCase))
                return null; // Only the target can accept

            if (duel.IsExpired)
            {
                EndDuel(targetLogin, "Duel request expired.");
                return null;
            }

            duel.State = DuelState.Countdown;
            duel.CombatStartAt = DateTime.UtcNow.AddSeconds(DuelInfo.CountdownSec);
            Debug.LogError($"[PVP-DUEL] {targetLogin} accepted duel from {duel.ChallengerLogin} — countdown started");
            return duel;
        }

        /// <summary>
        /// Target declines the duel. Returns the DuelInfo (for notification) or null.
        /// </summary>
        public DuelInfo TryDecline(string targetLogin)
        {
            if (!_activeDuels.TryGetValue(targetLogin, out var duel))
                return null;

            if (duel.State != DuelState.PendingAccept)
                return null;

            Debug.LogError($"[PVP-DUEL] {targetLogin} declined duel from {duel.ChallengerLogin}");
            var info = duel; // capture before removal
            EndDuel(targetLogin, null);
            return info;
        }

        /// <summary>
        /// Transition from Countdown to Active combat. Call this after countdown timer expires.
        /// </summary>
        public bool ActivateCombat(string loginName)
        {
            if (!_activeDuels.TryGetValue(loginName, out var duel))
                return false;

            if (duel.State != DuelState.Countdown)
                return false;

            duel.State = DuelState.Active;
            Debug.LogError($"[PVP-DUEL] Combat active: {duel.ChallengerLogin} vs {duel.TargetLogin}");
            return true;
        }

        /// <summary>
        /// Check if two players are in an active duel with each other.
        /// Used by the damage pipeline to allow PvP damage.
        /// </summary>
        public bool AreInActiveDuel(string loginA, string loginB)
        {
            if (!_activeDuels.TryGetValue(loginA, out var duel))
                return false;

            if (duel.State != DuelState.Active)
                return false;

            return (string.Equals(duel.ChallengerLogin, loginB, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(duel.TargetLogin, loginB, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Report a kill during an active duel. Returns (winnerLogin, loserLogin) or (null,null) if not in a duel.
        /// </summary>
        public (string winner, string loser, DuelInfo duel) ReportKill(string killerLogin, string victimLogin)
        {
            if (!AreInActiveDuel(killerLogin, victimLogin))
                return (null, null, null);

            var duel = _activeDuels[killerLogin];
            Debug.LogError($"[PVP-DUEL] {killerLogin} defeated {victimLogin}!");
            EndDuel(killerLogin, null);
            return (killerLogin, victimLogin, duel);
        }

        /// <summary>
        /// Handle a player disconnecting — cancel any active duel.
        /// </summary>
        public DuelInfo HandleDisconnect(string loginName)
        {
            if (!_activeDuels.TryGetValue(loginName, out var duel))
                return null;

            Debug.LogError($"[PVP-DUEL] {loginName} disconnected during duel — forfeited");
            var info = duel;
            EndDuel(loginName, null);
            return info;
        }

        /// <summary>
        /// Clean up expired pending duels. Call periodically from server tick.
        /// </summary>
        public List<DuelInfo> CleanupExpired()
        {
            var expired = new List<DuelInfo>();
            var toRemove = new List<string>();

            foreach (var kvp in _activeDuels)
            {
                if (kvp.Value.IsExpired && !expired.Contains(kvp.Value))
                {
                    expired.Add(kvp.Value);
                    toRemove.Add(kvp.Value.ChallengerLogin);
                    toRemove.Add(kvp.Value.TargetLogin);
                }
            }

            foreach (var name in toRemove)
                _activeDuels.Remove(name);

            return expired;
        }

        private void EndDuel(string loginName, string reason)
        {
            if (!_activeDuels.TryGetValue(loginName, out var duel))
                return;

            // Set cooldown for both players
            _cooldowns[duel.ChallengerLogin] = DateTime.UtcNow;
            _cooldowns[duel.TargetLogin] = DateTime.UtcNow;

            // Remove both entries
            _activeDuels.Remove(duel.ChallengerLogin);
            _activeDuels.Remove(duel.TargetLogin);

            if (reason != null)
                Debug.LogError($"[PVP-DUEL] Duel ended: {reason}");
        }
    }
}
